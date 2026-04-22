using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Utility.BlockCompression;

/// <summary>
/// Validates the exact on-disk frame layout ClickHouse expects.
/// Checksum scope and the "CompressedSize includes the 9-byte header" invariant
/// are both easy to get wrong; both are pinned here.
/// </summary>
public class BlockFramingTests
{
    [Test]
    public async Task WriteFrameAsync_ProducesParseableFrame_WithCorrectChecksum()
    {
        var payload = Lz4BlockCodecTests.Lcg(256, 1);
        using var ms = new MemoryStream();
        using var codec = new Lz4BlockCodec();

        int total = await BlockFraming.WriteFrameAsync(payload, codec, ms, CancellationToken.None);
        var frame = ms.ToArray();

        Assert.That(frame.Length, Is.EqualTo(total), "Returned byte count must match stream contents");

        // Layout: [checksum 16][method 1][csize 4][usize 4][payload]
        var checksum = frame.AsSpan(0, BlockFraming.ChecksumSize);
        var method = frame[BlockFraming.ChecksumSize];
        uint csize = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(BlockFraming.ChecksumSize + 1, 4));
        uint usize = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(BlockFraming.ChecksumSize + 5, 4));

        Assert.Multiple(() =>
        {
            Assert.That(method, Is.EqualTo(BlockFraming.MethodLz4));
            Assert.That(usize, Is.EqualTo((uint)payload.Length));
            // csize MUST include the 9-byte header — this is the off-by-nine the server errors on.
            Assert.That(csize, Is.EqualTo((uint)(frame.Length - BlockFraming.ChecksumSize)));
        });

        // Checksum is over bytes [16..end]: header(9) + compressed payload.
        Span<byte> expected = stackalloc byte[BlockFraming.ChecksumSize];
        CityHash128.HashBytes(frame.AsSpan(BlockFraming.ChecksumSize), expected);
        Assert.That(checksum.SequenceEqual(expected), Is.True,
            "Checksum must be CityHash128(header + compressed_payload).");
    }

    [Test]
    public async Task WriteFrameAsync_RoundTripsThroughBlockDecompressionStream()
    {
        using var buf = new MemoryStream();
        using var codec = new Lz4BlockCodec();
        var payload = Lz4BlockCodecTests.Lcg(4096, 77);

        await BlockFraming.WriteFrameAsync(payload, codec, buf, CancellationToken.None);

        buf.Position = 0;
        using var decompress = new BlockDecompressionStream(buf, leaveOpen: true);
        using var outMs = new MemoryStream();
        await decompress.CopyToAsync(outMs);

        Assert.That(outMs.ToArray(), Is.EqualTo(payload));
    }
}
