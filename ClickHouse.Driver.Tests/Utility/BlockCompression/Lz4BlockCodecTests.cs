using System;
using System.Linq;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Utility.BlockCompression;

public class Lz4BlockCodecTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(1023)]
    [TestCase(1024)]
    [TestCase(1024 * 1024)]          // Matches default block size; frame boundary.
    [TestCase(1024 * 1024 + 1)]      // One byte past the default block size.
    public void Encode_Decode_RoundTrips_OnRandomPayload(int size)
    {
        var payload = Lcg(size, seed: (ulong)(size + 1));
        using var codec = new Lz4BlockCodec();

        var encoded = new byte[codec.MaxCompressedLength(size)];
        int encodedLen = codec.Encode(payload, encoded);
        Assert.That(encodedLen, Is.GreaterThanOrEqualTo(0));

        var decoded = new byte[size];
        int decodedLen = codec.Decode(encoded.AsSpan(0, encodedLen), decoded);

        Assert.That(decodedLen, Is.EqualTo(size));
        Assert.That(decoded, Is.EqualTo(payload));
    }

    [Test]
    public void Decode_TruncatedInput_Throws()
    {
        // LZ4 block format has no embedded integrity check — random bit-flips don't reliably
        // throw. The outer CityHash128 checksum is what catches corruption (covered in
        // BlockDecompressionStreamTests). Here we assert the codec still refuses outright
        // malformed input: a truncated block cannot yield the promised uncompressed size.
        using var codec = new Lz4BlockCodec();
        var payload = Lcg(4096, 5);
        var encoded = new byte[codec.MaxCompressedLength(payload.Length)];
        int encodedLen = codec.Encode(payload, encoded);

        var decoded = new byte[payload.Length];
        Assert.Throws<ClickHouseCompressionException>(() =>
            codec.Decode(encoded.AsSpan(0, encodedLen / 2), decoded));
    }

    [Test]
    public void MethodByte_MatchesClickHouseLz4Constant()
    {
        using var codec = new Lz4BlockCodec();
        // 0x82 is the wire byte ClickHouse uses for LZ4 in compressed-block framing.
        Assert.That(codec.MethodByte, Is.EqualTo(BlockFraming.MethodLz4));
        Assert.That(codec.MethodByte, Is.EqualTo((byte)0x82));
    }

    internal static byte[] Lcg(int n, ulong seed)
    {
        var a = new byte[n];
        ulong s = seed;
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            a[i] = (byte)(s >> 56);
        }
        return a;
    }
}
