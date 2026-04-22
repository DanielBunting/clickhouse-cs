namespace ClickHouse.Driver.ADO;

/// <summary>
/// Compression method used for HTTP request/response bodies.
/// Gzip uses standard HTTP Content-Encoding; Lz4/Zstd use ClickHouse's
/// native block framing (compress=1 / decompress=1 query-string flags).
/// </summary>
public enum CompressionMethod
{
    /// <summary>No compression.</summary>
    None = 0,

    /// <summary>Gzip via standard HTTP Content-Encoding.</summary>
    Gzip = 1,

    /// <summary>LZ4 via ClickHouse native block framing.</summary>
    Lz4 = 2,

    /// <summary>ZSTD via ClickHouse native block framing.</summary>
    Zstd = 3,
}
