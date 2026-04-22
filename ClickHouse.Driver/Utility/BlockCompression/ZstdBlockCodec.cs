using System;
using ZstdSharp;

namespace ClickHouse.Driver.Utility.BlockCompression;

/// <summary>
/// ZSTD codec for a single ClickHouse block. ZstdSharp's Compressor and
/// Decompressor hold internal contexts and are NOT thread-safe — one codec
/// instance per logical caller (per request/response).
/// </summary>
internal sealed class ZstdBlockCodec : IBlockCodec
{
    private readonly Compressor compressor;
    private readonly Decompressor decompressor;

    public ZstdBlockCodec(int compressionLevel = 3)
    {
        compressor = new Compressor(compressionLevel);
        decompressor = new Decompressor();
    }

    public byte MethodByte => BlockFraming.MethodZstd;

    public int MaxCompressedLength(int sourceLength) => Compressor.GetCompressBound(sourceLength);

    public int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        try
        {
            if (!compressor.TryWrap(source, destination, out int written))
                throw new ClickHouseCompressionException($"ZSTD encode failed (source={source.Length}, dest={destination.Length}).");
            return written;
        }
        catch (Exception ex) when (ex is not ClickHouseCompressionException)
        {
            throw new ClickHouseCompressionException($"ZSTD encode failed (source={source.Length}, dest={destination.Length}): {ex.Message}", ex);
        }
    }

    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        try
        {
            if (!decompressor.TryUnwrap(source, destination, out int written))
                throw new ClickHouseCompressionException($"ZSTD decode failed (source={source.Length}, dest={destination.Length}).");
            return written;
        }
        catch (Exception ex) when (ex is not ClickHouseCompressionException)
        {
            throw new ClickHouseCompressionException($"ZSTD decode failed (source={source.Length}, dest={destination.Length}): {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        compressor.Dispose();
        decompressor.Dispose();
    }
}
