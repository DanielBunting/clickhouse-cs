using System;
using K4os.Compression.LZ4;

namespace ClickHouse.Driver.Utility.BlockCompression;

/// <summary>
/// LZ4 codec for a single ClickHouse block. Stateless and thread-safe
/// (the underlying K4os static API is).
/// </summary>
internal sealed class Lz4BlockCodec : IBlockCodec
{
    public byte MethodByte => BlockFraming.MethodLz4;

    public int MaxCompressedLength(int sourceLength) => LZ4Codec.MaximumOutputSize(sourceLength);

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int n = LZ4Codec.Encode(source, destination, LZ4Level.L00_FAST);
        if (n < 0)
            throw new ClickHouseCompressionException($"LZ4 encode failed (source={source.Length}, dest={destination.Length}).");
        return n;
    }

    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int n = LZ4Codec.Decode(source, destination);
        if (n < 0)
            throw new ClickHouseCompressionException($"LZ4 decode failed (source={source.Length}, dest={destination.Length}).");
        return n;
    }

    public void Dispose()
    {
    }
}
