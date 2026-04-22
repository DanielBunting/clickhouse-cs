using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClickHouse.Driver.Utility.BlockCompression;

/// <summary>
/// Read-only Stream that parses ClickHouse native block-compression framing
/// from an inner stream: reads one frame at a time, verifies the 16-byte
/// CityHash128 checksum, decompresses using the method indicated in the
/// header byte, and exposes the concatenated payload bytes.
///
/// Not thread-safe. Not seekable.
/// </summary>
internal sealed class BlockDecompressionStream : Stream
{
    private readonly Stream inner;
    private readonly bool leaveOpen;
    private readonly int compressionLevel;
    private readonly byte[] checksumBuf = new byte[BlockFraming.ChecksumSize];
    private readonly byte[] headerBuf = new byte[BlockFraming.HeaderSize];

    private IBlockCodec lz4Codec;
    private IBlockCodec zstdCodec;

    private byte[] decodedBuffer; // rented
    private int decodedLength;
    private int decodedOffset;
    private bool endReached;
    private bool disposed;

    public BlockDecompressionStream(Stream inner, int zstdLevel = 3, bool leaveOpen = false)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.leaveOpen = leaveOpen;
        compressionLevel = zstdLevel;
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Real synchronous implementation — do NOT wrap ReadAsync in GetResult().
        // That was the prior implementation and is the textbook sync-over-async
        // deadlock pattern. .NET Core's default lack of a SynchronizationContext
        // usually hides it, but ADO.NET callers can end up on arbitrary threads
        // so "usually" isn't a property we want to ship.
        if (count == 0) return 0;
        var destination = new Memory<byte>(buffer, offset, count);

        if (decodedOffset >= decodedLength)
        {
            if (endReached) return 0;
            if (!ReadNextFrame())
            {
                endReached = true;
                return 0;
            }
        }

        return CopyOutDecoded(destination.Span);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsyncCore(new Memory<byte>(buffer, offset, count), cancellationToken).ConfigureAwait(false);
    }

#if NET5_0_OR_GREATER
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return new ValueTask<int>(ReadAsyncCore(buffer, cancellationToken));
    }
#endif

    private async Task<int> ReadAsyncCore(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length == 0) return 0;

        if (decodedOffset >= decodedLength)
        {
            if (endReached) return 0;
            if (!await ReadNextFrameAsync(cancellationToken).ConfigureAwait(false))
            {
                endReached = true;
                return 0;
            }
        }

        return CopyOutDecoded(destination.Span);
    }

    private int CopyOutDecoded(Span<byte> destination)
    {
        int available = decodedLength - decodedOffset;
        int toCopy = Math.Min(available, destination.Length);
        decodedBuffer.AsSpan(decodedOffset, toCopy).CopyTo(destination);
        decodedOffset += toCopy;
        return toCopy;
    }

    // --- Frame parsing. Sync + async twins share the post-read processing via ProcessFrame. --

    private bool ReadNextFrame()
    {
        int checksumRead = ReadAtLeast(inner, checksumBuf, required: BlockFraming.ChecksumSize, allowEndOfStreamBefore: true);
        if (checksumRead == 0) return false;
        if (checksumRead < BlockFraming.ChecksumSize)
            throw new ClickHouseCompressionException($"Truncated frame checksum: got {checksumRead} of {BlockFraming.ChecksumSize} bytes.");

        int headerRead = ReadAtLeast(inner, headerBuf, required: BlockFraming.HeaderSize, allowEndOfStreamBefore: false);
        if (headerRead < BlockFraming.HeaderSize)
            throw new ClickHouseCompressionException($"Truncated frame header: got {headerRead} of {BlockFraming.HeaderSize} bytes.");

        var (payloadSize, uncompressedSize) = ParseAndValidateHeader();

        byte[] hashInput = ArrayPool<byte>.Shared.Rent(BlockFraming.HeaderSize + payloadSize);
        try
        {
            headerBuf.AsSpan(0, BlockFraming.HeaderSize).CopyTo(hashInput);
            int payloadRead = ReadAtLeast(inner, hashInput, bufferOffset: BlockFraming.HeaderSize, required: payloadSize, allowEndOfStreamBefore: false);
            if (payloadRead < payloadSize)
                throw new ClickHouseCompressionException($"Truncated frame payload: got {payloadRead} of {payloadSize} bytes.");

            FinishFrame(hashInput, payloadSize, uncompressedSize);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashInput);
        }
    }

    private async Task<bool> ReadNextFrameAsync(CancellationToken cancellationToken)
    {
        int checksumRead = await BlockFraming.ReadAtLeastAsync(inner, checksumBuf, required: BlockFraming.ChecksumSize, allowEndOfStreamBefore: true, cancellationToken).ConfigureAwait(false);
        if (checksumRead == 0) return false;
        if (checksumRead < BlockFraming.ChecksumSize)
            throw new ClickHouseCompressionException($"Truncated frame checksum: got {checksumRead} of {BlockFraming.ChecksumSize} bytes.");

        int headerRead = await BlockFraming.ReadAtLeastAsync(inner, headerBuf, required: BlockFraming.HeaderSize, allowEndOfStreamBefore: false, cancellationToken).ConfigureAwait(false);
        if (headerRead < BlockFraming.HeaderSize)
            throw new ClickHouseCompressionException($"Truncated frame header: got {headerRead} of {BlockFraming.HeaderSize} bytes.");

        var (payloadSize, uncompressedSize) = ParseAndValidateHeader();

        byte[] hashInput = ArrayPool<byte>.Shared.Rent(BlockFraming.HeaderSize + payloadSize);
        try
        {
            headerBuf.AsSpan(0, BlockFraming.HeaderSize).CopyTo(hashInput);
            int payloadRead = await BlockFraming.ReadAtLeastAsync(inner, hashInput.AsMemory(BlockFraming.HeaderSize, payloadSize), required: payloadSize, allowEndOfStreamBefore: false, cancellationToken).ConfigureAwait(false);
            if (payloadRead < payloadSize)
                throw new ClickHouseCompressionException($"Truncated frame payload: got {payloadRead} of {payloadSize} bytes.");

            FinishFrame(hashInput, payloadSize, uncompressedSize);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashInput);
        }
    }

    /// <summary>
    /// Parses the 9-byte header in <c>headerBuf</c> and validates sizes against
    /// <see cref="BlockFraming.MaxFrameSize"/>. Rejects hostile or corrupt headers
    /// claiming multi-GiB sizes BEFORE any allocation is made — we haven't yet
    /// verified the checksum, so the declared sizes are untrusted input.
    /// </summary>
    private (int payloadSize, int uncompressedSize) ParseAndValidateHeader()
    {
        byte method = headerBuf[0];
        uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(1, 4));
        uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(5, 4));

        if (compressedSize < BlockFraming.HeaderSize)
            throw new ClickHouseCompressionException($"Invalid frame: CompressedSize {compressedSize} < header size {BlockFraming.HeaderSize}.");

        if (compressedSize > BlockFraming.MaxFrameSize)
            throw new ClickHouseCompressionException(
                $"Rejecting frame: CompressedSize {compressedSize} exceeds MaxFrameSize ({BlockFraming.MaxFrameSize}). " +
                "Untrusted header cannot allocate beyond this bound.");

        if (uncompressedSize > BlockFraming.MaxFrameSize)
            throw new ClickHouseCompressionException(
                $"Rejecting frame: UncompressedSize {uncompressedSize} exceeds MaxFrameSize ({BlockFraming.MaxFrameSize}). " +
                "Untrusted header cannot allocate beyond this bound.");

        _ = method; // Method byte is dispatched in FinishFrame.
        return (payloadSize: (int)(compressedSize - BlockFraming.HeaderSize), uncompressedSize: (int)uncompressedSize);
    }

    /// <summary>
    /// Verifies the checksum, decompresses into <c>decodedBuffer</c>, and resets the
    /// read cursor. Runs identically for sync and async paths — all IO has happened
    /// by the time we get here.
    /// </summary>
    private void FinishFrame(byte[] hashInput, int payloadSize, int uncompressedSize)
    {
        Span<byte> actualChecksum = stackalloc byte[BlockFraming.ChecksumSize];
        CityHash128.HashBytes(hashInput.AsSpan(0, BlockFraming.HeaderSize + payloadSize), actualChecksum);
        if (!actualChecksum.SequenceEqual(checksumBuf))
            throw new ClickHouseCompressionException("Block checksum mismatch (received bytes do not match server-computed CityHash128).");

        EnsureDecoded(uncompressedSize);
        ReadOnlySpan<byte> payloadSpan = hashInput.AsSpan(BlockFraming.HeaderSize, payloadSize);
        byte method = hashInput[0];
        int decoded;

        switch (method)
        {
            case BlockFraming.MethodNone:
                payloadSpan.CopyTo(decodedBuffer.AsSpan(0, payloadSize));
                decoded = payloadSize;
                break;
            case BlockFraming.MethodLz4:
                lz4Codec ??= new Lz4BlockCodec();
                decoded = lz4Codec.Decode(payloadSpan, decodedBuffer.AsSpan(0, uncompressedSize));
                break;
            case BlockFraming.MethodZstd:
                zstdCodec ??= new ZstdBlockCodec(compressionLevel);
                decoded = zstdCodec.Decode(payloadSpan, decodedBuffer.AsSpan(0, uncompressedSize));
                break;
            default:
                throw new ClickHouseCompressionException($"Unknown block compression method byte: 0x{method:X2}.");
        }

        decodedLength = decoded;
        decodedOffset = 0;
    }

    private void EnsureDecoded(int required)
    {
        // Caller has already validated `required` is within MaxFrameSize.
        if (decodedBuffer != null && decodedBuffer.Length >= required) return;
        if (decodedBuffer != null)
            ArrayPool<byte>.Shared.Return(decodedBuffer);
        decodedBuffer = ArrayPool<byte>.Shared.Rent(required);
    }

    /// <summary>Synchronous counterpart of <see cref="BlockFraming.ReadAtLeastAsync"/>.</summary>
    private static int ReadAtLeast(Stream stream, byte[] buffer, int required, bool allowEndOfStreamBefore) =>
        ReadAtLeast(stream, buffer, bufferOffset: 0, required, allowEndOfStreamBefore);

    private static int ReadAtLeast(Stream stream, byte[] buffer, int bufferOffset, int required, bool allowEndOfStreamBefore)
    {
        int total = 0;
        while (total < required)
        {
            int n = stream.Read(buffer, bufferOffset + total, required - total);
            if (n == 0)
            {
                if (total == 0 && allowEndOfStreamBefore) return 0;
                return total;
            }
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            if (decodedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(decodedBuffer);
                decodedBuffer = null;
            }
            lz4Codec?.Dispose();
            zstdCodec?.Dispose();
            if (!leaveOpen)
                inner.Dispose();
        }
        disposed = true;
        base.Dispose(disposing);
    }
}
