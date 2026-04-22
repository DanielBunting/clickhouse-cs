using System;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Utility.BlockCompression;

public class ZstdBlockCodecTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(1023)]
    [TestCase(1024)]
    [TestCase(1024 * 1024)]
    [TestCase(1024 * 1024 + 1)]
    public void Encode_Decode_RoundTrips_OnRandomPayload(int size)
    {
        var payload = Lz4BlockCodecTests.Lcg(size, seed: (ulong)(size + 1));
        using var codec = new ZstdBlockCodec();

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
        // Same reasoning as Lz4: ZSTD has internal checks but bit-flip location-dependent.
        // Truncation, however, is always detected.
        using var codec = new ZstdBlockCodec();
        var payload = Lz4BlockCodecTests.Lcg(4096, 5);
        var encoded = new byte[codec.MaxCompressedLength(payload.Length)];
        int encodedLen = codec.Encode(payload, encoded);

        var decoded = new byte[payload.Length];
        Assert.Throws<ClickHouseCompressionException>(() =>
            codec.Decode(encoded.AsSpan(0, encodedLen / 2), decoded));
    }

    [Test]
    public void MethodByte_MatchesClickHouseZstdConstant()
    {
        using var codec = new ZstdBlockCodec();
        Assert.That(codec.MethodByte, Is.EqualTo(BlockFraming.MethodZstd));
        Assert.That(codec.MethodByte, Is.EqualTo((byte)0x90));
    }
}
