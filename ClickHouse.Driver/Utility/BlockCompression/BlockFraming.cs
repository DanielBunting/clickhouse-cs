using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClickHouse.Driver.Utility.BlockCompression;

/// <summary>
/// Low-level helpers for ClickHouse's native block-compression framing
/// used on the HTTP endpoint when compress/decompress query-string flags
/// are set.
///
/// Frame layout:
///   Checksum[16]         CityHash128(Method || CompressedSize || UncompressedSize || Payload)
///   Method[1]            0x02 NONE · 0x82 LZ4 · 0x90 ZSTD
///   CompressedSize[4]    LE u32, INCLUDES the 9-byte header but NOT the checksum
///   UncompressedSize[4]  LE u32
///   Payload[CompressedSize - 9]
/// </summary>
internal static class BlockFraming
{
    public const byte MethodNone = 0x02;
    public const byte MethodLz4 = 0x82;
    public const byte MethodZstd = 0x90;

    public const int ChecksumSize = 16;
    public const int HeaderSize = 9; // method(1) + csize(4) + usize(4)
    public const int FrameOverhead = ChecksumSize + HeaderSize;

    /// <summary>Target uncompressed block size when writing (matches server default).</summary>
    public const int DefaultBlockSize = 1024 * 1024;

    /// <summary>
    /// Compresses <paramref name="source"/> with <paramref name="codec"/> and writes
    /// a single framed block to <paramref name="output"/>. Returns bytes written.
    /// </summary>
    public static async Task<int> WriteFrameAsync(
        ReadOnlyMemory<byte> source,
        IBlockCodec codec,
        Stream output,
        CancellationToken cancellationToken)
    {
        // Layout: [checksum 16][method 1][csize 4][usize 4][payload]
        int maxCompressed = codec.MaxCompressedLength(source.Length);
        int frameMax = ChecksumSize + HeaderSize + maxCompressed;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameMax);
        try
        {
            Span<byte> frame = buffer.AsSpan(0, frameMax);

            int compressed = codec.Encode(source.Span, frame.Slice(ChecksumSize + HeaderSize));
            if (compressed <= 0)
                throw new ClickHouseCompressionException($"Codec {codec.GetType().Name} failed to encode block of {source.Length} bytes.");

            int compressedSizeField = HeaderSize + compressed;
            frame[ChecksumSize] = codec.MethodByte;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(ChecksumSize + 1, 4), (uint)compressedSizeField);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(ChecksumSize + 5, 4), (uint)source.Length);

            // Checksum covers header + payload.
            Span<byte> hashed = frame.Slice(ChecksumSize, HeaderSize + compressed);
            CityHash128.HashBytes(hashed, frame.Slice(0, ChecksumSize));

            int total = ChecksumSize + HeaderSize + compressed;
            await output.WriteAsync(buffer.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a frame header (checksum + method/sizes) from the stream.
    /// Returns false at EOF (no bytes available). Throws on truncation mid-header or invalid sizes.
    /// </summary>
    public static async Task<bool> TryReadFrameHeaderAsync(
        Stream input,
        Memory<byte> checksumOut,
        FrameHeader[] headerOut,
        CancellationToken cancellationToken)
    {
        if (checksumOut.Length < ChecksumSize) throw new ArgumentException("checksumOut too small", nameof(checksumOut));

        int read = await ReadAtLeastAsync(input, checksumOut.Slice(0, ChecksumSize), required: ChecksumSize, allowEndOfStreamBefore: true, cancellationToken).ConfigureAwait(false);
        if (read == 0)
            return false;
        if (read < ChecksumSize)
            throw new ClickHouseCompressionException($"Truncated frame checksum: read {read} of {ChecksumSize} bytes.");

        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            int headerRead = await ReadAtLeastAsync(input, headerBuf.AsMemory(0, HeaderSize), required: HeaderSize, allowEndOfStreamBefore: false, cancellationToken).ConfigureAwait(false);
            if (headerRead < HeaderSize)
                throw new ClickHouseCompressionException($"Truncated frame header: read {headerRead} of {HeaderSize} bytes.");

            byte method = headerBuf[0];
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(1, 4));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(5, 4));

            if (compressedSize < HeaderSize)
                throw new ClickHouseCompressionException($"Invalid frame: CompressedSize {compressedSize} < header size {HeaderSize}.");

            headerOut[0] = new FrameHeader(method, compressedSize, uncompressedSize);
            // Copy raw header bytes into the first 9 bytes of the caller's hash-input buffer.
            headerBuf.AsSpan(0, HeaderSize).CopyTo(headerOut[0].RawHeader.Span);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    internal readonly struct FrameHeader
    {
        public readonly byte Method;
        public readonly uint CompressedSize; // includes the 9-byte header
        public readonly uint UncompressedSize;
        public readonly Memory<byte> RawHeader;

        public FrameHeader(byte method, uint compressedSize, uint uncompressedSize)
        {
            Method = method;
            CompressedSize = compressedSize;
            UncompressedSize = uncompressedSize;
            RawHeader = new byte[HeaderSize];
        }

        /// <summary>Payload byte count (CompressedSize - HeaderSize).</summary>
        public int PayloadSize => (int)(CompressedSize - HeaderSize);
    }

    internal static async Task<int> ReadAtLeastAsync(
        Stream stream,
        Memory<byte> buffer,
        int required,
        bool allowEndOfStreamBefore,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < required)
        {
            int n = await stream.ReadAsync(buffer.Slice(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0 && allowEndOfStreamBefore) return 0;
                return total;
            }
            total += n;
        }
        return total;
    }
}
