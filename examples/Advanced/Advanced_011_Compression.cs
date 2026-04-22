using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;

namespace ClickHouse.Driver.Examples;

/// <summary>
/// How to choose a compression method and what each one costs.
///
/// TL;DR: The driver supports four compression methods over HTTP:
///   - <c>None</c>: no compression.
///   - <c>Gzip</c>: standard HTTP gzip (default; back-compatible with every server).
///   - <c>Lz4</c>: ClickHouse native block framing; much lower CPU than gzip.
///   - <c>Zstd</c>: ClickHouse native block framing; best ratio at modest CPU.
///
/// ## Tradeoffs at a glance
///
/// Measured end-to-end on representative RowBinary payloads (see
/// ClickHouse.Driver.Benchmark/CompressionBenchmark.cs):
///
/// | Method | Compression ratio vs gzip | Decompress CPU vs gzip | Notes                    |
/// | ------ | ------------------------- | ---------------------- | ------------------------ |
/// | None   | 1.0× (raw)                | n/a                    | Fastest CPU, widest wire |
/// | Gzip   | baseline                  | baseline               | Ubiquitous, HTTP native  |
/// | Lz4    | slightly worse (≈1.3×)    | ~3–8× faster           | Best for CPU-bound       |
/// | Zstd   | 1.5–2.5× better           | ~1.3–1.7× faster       | Best for bandwidth-bound |
///
/// ## How each method works on the wire
///
/// - <c>Gzip</c>:
///   - Request: <c>Content-Encoding: gzip</c>
///   - Response: <c>Accept-Encoding: gzip</c> + query param <c>enable_http_compression=1</c>
///   - HttpClient decompresses the response automatically (AutomaticDecompression).
///
/// - <c>Lz4</c> / <c>Zstd</c>:
///   - Uses ClickHouse's native block-compression framing, not standard HTTP encodings.
///   - Request: query params <c>compress=1&amp;decompress=1&amp;network_compression_method=lz4</c> (or zstd);
///     body is a sequence of [checksum(16) | method(1) | csize(4) | usize(4) | payload] frames
///     with a CityHash128 per block.
///   - Response: same framing in reverse.
///
/// ## Setting the method
///
/// Via connection string (case-insensitive):
///
///     "Host=localhost;Compression=lz4"
///     "Host=localhost;Compression=zstd"
///     "Host=localhost;Compression=gzip"   // same as Compression=true (default)
///     "Host=localhost;Compression=none"   // same as Compression=false
///
/// Via ClickHouseClientSettings (strongly typed, recommended):
///
///     var settings = new ClickHouseClientSettings
///     {
///         Host = "localhost",
///         CompressionMethod = CompressionMethod.Lz4,
///     };
///
/// ## When to pick which
///
/// - **CPU-bound workloads** (large SELECTs where decompress time dominates): use <c>Lz4</c>.
/// - **Bandwidth-bound workloads** (remote server, limited uplink): use <c>Zstd</c>.
/// - **Localhost, tiny payloads, or already-compressed data** (parquet, encrypted bytes):
///   use <c>None</c> — compression can be net-negative when the payload doesn't compress.
/// - **Default/unsure**: stay on <c>Gzip</c>. It's the old default, works on every server
///   version, and is good enough for most workloads.
///
/// ## Important: Custom HttpClient Configuration
///
/// If you provide your own <see cref="System.Net.Http.HttpClient"/> and use <c>Gzip</c>,
/// you MUST configure <c>AutomaticDecompression</c>:
///
///     var handler = new HttpClientHandler
///     {
///         AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
///     };
///     var httpClient = new HttpClient(handler);
///
/// For <c>Lz4</c>/<c>Zstd</c> no HttpClient config is needed — the driver's own
/// <c>BlockDecompressionStream</c> decodes the response body regardless of handler.
/// </summary>
public static class Compression
{
    public static async Task Run()
    {
        Console.WriteLine("Compression Methods\n");

        // 1. Default — Gzip (back-compatible, ubiquitous).
        Console.WriteLine("1. Default (Gzip):");
        using (var client = new ClickHouseClient("Host=localhost"))
        {
            var result = await client.ExecuteScalarAsync("SELECT 'hello from gzip'");
            Console.WriteLine($"   Result: {result}\n");
        }

        // 2. LZ4 — lowest CPU, slightly worse ratio than gzip.
        Console.WriteLine("2. LZ4 (low CPU):");
        using (var client = new ClickHouseClient("Host=localhost;Compression=lz4"))
        {
            var result = await client.ExecuteScalarAsync("SELECT 'hello from lz4'");
            Console.WriteLine($"   Result: {result}\n");
        }

        // 3. ZSTD — best ratio, moderate CPU.
        Console.WriteLine("3. ZSTD (best ratio):");
        using (var client = new ClickHouseClient("Host=localhost;Compression=zstd"))
        {
            var result = await client.ExecuteScalarAsync("SELECT 'hello from zstd'");
            Console.WriteLine($"   Result: {result}\n");
        }

        // 4. None — no compression. Useful on localhost or for pre-compressed payloads.
        Console.WriteLine("4. None (no compression):");
        using (var client = new ClickHouseClient("Host=localhost;Compression=none"))
        {
            var result = await client.ExecuteScalarAsync("SELECT 'uncompressed'");
            Console.WriteLine($"   Result: {result}\n");
        }

        // 5. Using the strongly-typed enum on ClickHouseClientSettings.
        Console.WriteLine("5. Via ClickHouseClientSettings with the CompressionMethod enum:");
        var settings = new ClickHouseClientSettings
        {
            Host = "localhost",
            CompressionMethod = CompressionMethod.Zstd,
        };
        using (var client = new ClickHouseClient(settings))
        {
            var result = await client.ExecuteScalarAsync("SELECT 1");
            Console.WriteLine($"   CompressionMethod = {settings.CompressionMethod}");
            Console.WriteLine($"   Result: {result}\n");
        }

        Console.WriteLine("Summary:");
        Console.WriteLine("   - Default: Gzip (compatible with every supported server).");
        Console.WriteLine("   - CPU-bound: prefer LZ4.");
        Console.WriteLine("   - Bandwidth-bound: prefer ZSTD.");
        Console.WriteLine("   - Localhost or pre-compressed data: consider None.");
    }
}
