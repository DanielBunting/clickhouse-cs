using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Utility.BlockCompression;

/// <summary>
/// Round-trip and error-handling tests for <see cref="BlockDecompressionStream"/>.
/// Hand-rolls frames (via <see cref="BlockFraming.WriteFrameAsync"/> or raw bytes)
/// into a <see cref="MemoryStream"/> and asserts the stream reproduces the original
/// payload or throws for each failure mode.
/// </summary>
public class BlockDecompressionStreamTests
{
    [Test]
    public async Task Read_EndOfStream_BeforeAnyFrame_ReturnsZero()
    {
        // Empty body is legal (server sent no rows); stream must terminate, not throw.
        using var empty = new MemoryStream(Array.Empty<byte>());
        using var stream = new BlockDecompressionStream(empty);

        var buf = new byte[16];
        int read = await stream.ReadAsync(buf, 0, buf.Length);
        Assert.That(read, Is.Zero);
    }

    [Test]
    public async Task Read_MultipleFrames_ConcatenatesPayloads()
    {
        var part1 = Lz4BlockCodecTests.Lcg(1000, 1);
        var part2 = Lz4BlockCodecTests.Lcg(2000, 2);
        var part3 = Lz4BlockCodecTests.Lcg(500, 3);

        var input = await BuildFramedStreamAsync(part1, part2, part3);
        using var decompress = new BlockDecompressionStream(input);

        using var outMs = new MemoryStream();
        await decompress.CopyToAsync(outMs);

        var expected = new byte[part1.Length + part2.Length + part3.Length];
        Buffer.BlockCopy(part1, 0, expected, 0, part1.Length);
        Buffer.BlockCopy(part2, 0, expected, part1.Length, part2.Length);
        Buffer.BlockCopy(part3, 0, expected, part1.Length + part2.Length, part3.Length);

        Assert.That(outMs.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task Read_ReadsAcrossFrameBoundary()
    {
        // Request reads smaller than a frame, and reads spanning two frames.
        var part1 = Lz4BlockCodecTests.Lcg(500, 4);
        var part2 = Lz4BlockCodecTests.Lcg(500, 5);
        var input = await BuildFramedStreamAsync(part1, part2);
        using var decompress = new BlockDecompressionStream(input);

        var buf = new byte[64];
        using var outMs = new MemoryStream();
        int n;
        while ((n = await decompress.ReadAsync(buf, 0, buf.Length)) > 0)
            outMs.Write(buf, 0, n);

        var expected = new byte[1000];
        Buffer.BlockCopy(part1, 0, expected, 0, 500);
        Buffer.BlockCopy(part2, 0, expected, 500, 500);
        Assert.That(outMs.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task Read_NoneMethodBlock_PassesPayloadThroughUnchanged()
    {
        // 0x02 = method "None" — frame is uncompressed but still checksummed and framed.
        var payload = Lz4BlockCodecTests.Lcg(123, 9);
        var frame = BuildRawFrame(BlockFraming.MethodNone, payload, payload);
        using var input = new MemoryStream(frame);
        using var decompress = new BlockDecompressionStream(input);

        using var outMs = new MemoryStream();
        await decompress.CopyToAsync(outMs);
        Assert.That(outMs.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public async Task Read_TruncatedChecksum_Throws()
    {
        // Only 8 of the 16 checksum bytes; must throw (not silently truncate the output).
        using var input = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using var decompress = new BlockDecompressionStream(input);

        var buf = new byte[16];
        var ex = Assert.ThrowsAsync<ClickHouseCompressionException>(async () =>
            await decompress.ReadAsync(buf, 0, buf.Length));
        Assert.That(ex!.Message, Does.Contain("checksum"));
    }

    [Test]
    public async Task Read_TruncatedHeader_Throws()
    {
        // Full checksum but only 4 of 9 header bytes.
        var partial = new byte[BlockFraming.ChecksumSize + 4];
        using var input = new MemoryStream(partial);
        using var decompress = new BlockDecompressionStream(input);

        var buf = new byte[16];
        var ex = Assert.ThrowsAsync<ClickHouseCompressionException>(async () =>
            await decompress.ReadAsync(buf, 0, buf.Length));
        Assert.That(ex!.Message, Does.Contain("header"));
    }

    [Test]
    public async Task Read_TruncatedPayload_Throws()
    {
        // Well-formed header claiming a 1 KiB payload but only half is present.
        var claimedPayloadSize = 1024;
        int csize = BlockFraming.HeaderSize + claimedPayloadSize;

        var buf = new byte[BlockFraming.ChecksumSize + BlockFraming.HeaderSize + claimedPayloadSize / 2];
        buf[BlockFraming.ChecksumSize] = BlockFraming.MethodNone;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(BlockFraming.ChecksumSize + 1, 4), (uint)csize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(BlockFraming.ChecksumSize + 5, 4), (uint)claimedPayloadSize);

        using var input = new MemoryStream(buf);
        using var decompress = new BlockDecompressionStream(input);

        var dst = new byte[16];
        var ex = Assert.ThrowsAsync<ClickHouseCompressionException>(async () =>
            await decompress.ReadAsync(dst, 0, dst.Length));
        Assert.That(ex!.Message, Does.Contain("payload"));
    }

    [Test]
    public async Task Read_ChecksumMismatch_Throws()
    {
        // Build a real frame, then flip a checksum bit so the stream rejects it.
        var payload = Lz4BlockCodecTests.Lcg(256, 6);
        var bad = BuildRawFrame(BlockFraming.MethodNone, payload, payload);
        bad[0] ^= 0xFF;

        using var input = new MemoryStream(bad);
        using var decompress = new BlockDecompressionStream(input);

        var dst = new byte[16];
        var ex = Assert.ThrowsAsync<ClickHouseCompressionException>(async () =>
            await decompress.ReadAsync(dst, 0, dst.Length));
        Assert.That(ex!.Message, Does.Contain("checksum").IgnoreCase);
    }

    [Test]
    public async Task Read_UnknownMethodByte_Throws()
    {
        // 0x99 is a valid codec byte in ClickHouse's universe (DeflateQpl), but we don't support it.
        // A real-world regression here would be e.g. accidentally changing our LZ4 constant.
        var payload = Lz4BlockCodecTests.Lcg(64, 8);
        var frame = BuildRawFrame(method: 0x99, rawPayload: payload, hashablePayload: payload);

        using var input = new MemoryStream(frame);
        using var decompress = new BlockDecompressionStream(input);

        var dst = new byte[16];
        var ex = Assert.ThrowsAsync<ClickHouseCompressionException>(async () =>
            await decompress.ReadAsync(dst, 0, dst.Length));
        Assert.That(ex!.Message, Does.Contain("0x99"));
    }

    [Test]
    public async Task Read_InvalidCompressedSize_BelowHeader_Throws()
    {
        // csize = 5 is nonsense: can't be smaller than the 9-byte header it's supposed to include.
        var bad = new byte[BlockFraming.ChecksumSize + BlockFraming.HeaderSize];
        bad[BlockFraming.ChecksumSize] = BlockFraming.MethodNone;
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(BlockFraming.ChecksumSize + 1, 4), 5u);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(BlockFraming.ChecksumSize + 5, 4), 0u);

        using var input = new MemoryStream(bad);
        using var decompress = new BlockDecompressionStream(input);

        var dst = new byte[16];
        Assert.ThrowsAsync<ClickHouseCompressionException>(async () =>
            await decompress.ReadAsync(dst, 0, dst.Length));
    }

    // --- Helpers ---------------------------------------------------------------

    private static async Task<MemoryStream> BuildFramedStreamAsync(params byte[][] payloads)
    {
        var ms = new MemoryStream();
        using var codec = new Lz4BlockCodec();
        foreach (var payload in payloads)
            await BlockFraming.WriteFrameAsync(payload, codec, ms, CancellationToken.None);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Assembles a raw frame without any codec compression. Accepts a <paramref name="rawPayload"/>
    /// (the bytes that end up on the wire) and a <paramref name="hashablePayload"/> (the bytes the
    /// checksum is computed over — same as rawPayload for a correct frame). This asymmetry lets
    /// tests plant a bad checksum cheaply by passing mismatched payloads.
    /// </summary>
    private static byte[] BuildRawFrame(byte method, byte[] rawPayload, byte[] hashablePayload)
    {
        int csize = BlockFraming.HeaderSize + rawPayload.Length;
        var frame = new byte[BlockFraming.ChecksumSize + BlockFraming.HeaderSize + rawPayload.Length];

        frame[BlockFraming.ChecksumSize] = method;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(BlockFraming.ChecksumSize + 1, 4), (uint)csize);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(BlockFraming.ChecksumSize + 5, 4), (uint)rawPayload.Length);
        Buffer.BlockCopy(rawPayload, 0, frame, BlockFraming.ChecksumSize + BlockFraming.HeaderSize, rawPayload.Length);

        // For the checksum we splice in the "hashable" payload (matching rawPayload in the correct case).
        var hashSource = new byte[BlockFraming.HeaderSize + hashablePayload.Length];
        frame.AsSpan(BlockFraming.ChecksumSize, BlockFraming.HeaderSize).CopyTo(hashSource);
        Buffer.BlockCopy(hashablePayload, 0, hashSource, BlockFraming.HeaderSize, hashablePayload.Length);
        CityHash128.HashBytes(hashSource, frame.AsSpan(0, BlockFraming.ChecksumSize));

        return frame;
    }
}
