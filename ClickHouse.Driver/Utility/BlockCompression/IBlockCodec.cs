using System;

namespace ClickHouse.Driver.Utility.BlockCompression;

internal interface IBlockCodec : IDisposable
{
    byte MethodByte { get; }

    int MaxCompressedLength(int sourceLength);

    int Encode(ReadOnlySpan<byte> source, Span<byte> destination);

    int Decode(ReadOnlySpan<byte> source, Span<byte> destination);
}
