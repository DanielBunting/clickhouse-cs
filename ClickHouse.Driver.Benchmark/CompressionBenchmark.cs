using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ClickHouse.Driver.ADO;

namespace ClickHouse.Driver.Benchmark;

/// <summary>
/// End-to-end SELECT and INSERT performance across the four supported compression
/// methods. Raw-algorithm benchmarks are useful in isolation, but the driver's
/// wire, framing and buffering costs only show up in the full round-trip — that's
/// what this benchmark measures.
/// </summary>
[Config(typeof(ComparisonConfig))]
[MemoryDiagnoser(true)]
public class CompressionBenchmark
{
    private const string TableName = "test.benchmark_compression_insert";

    private ClickHouseClient client;

    [Params(CompressionMethod.None, CompressionMethod.Gzip, CompressionMethod.Lz4, CompressionMethod.Zstd)]
    public CompressionMethod Compression { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var baseCs = Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTION") ?? "Host=localhost";
        var builder = new ClickHouseConnectionStringBuilder(baseCs)
        {
            CompressionMethod = Compression,
        };
        client = new ClickHouseClient(builder.ToSettings());

        await client.ExecuteNonQueryAsync("CREATE DATABASE IF NOT EXISTS test");
        await client.ExecuteNonQueryAsync(
            $"CREATE TABLE IF NOT EXISTS {TableName} (Id UInt64, Name String, Value Float64) ENGINE Null");
    }

    [GlobalCleanup]
    public void Cleanup() => client?.Dispose();

    /// <summary>
    /// Drains a 1M-row SELECT. Exercises the response-side decompression path
    /// (AutomaticDecompression for Gzip; BlockDecompressionStream for Lz4/Zstd).
    /// </summary>
    [Benchmark]
    public async Task<long> SelectOneMillionRows()
    {
        using var reader = await client.ExecuteReaderAsync(
            "SELECT number, toString(number), toFloat64(number) / 3 FROM system.numbers LIMIT 1000000");
        long rows = 0;
        while (await reader.ReadAsync()) rows++;
        return rows;
    }

    /// <summary>
    /// 100k-row INSERT into ENGINE Null; isolates the client-side serializer and
    /// request-side compression (gzip via GZipStream wrapper; Lz4/Zstd via block framing).
    /// </summary>
    [Benchmark]
    public async Task<long> InsertHundredThousandRows()
    {
        return await client.InsertBinaryAsync(TableName, new[] { "Id", "Name", "Value" }, Rows(100_000));
    }

    private static IEnumerable<object[]> Rows(int count)
    {
        for (int i = 0; i < count; i++)
            yield return new object[] { (ulong)i, $"row_{i % 100}", i * 0.1 };
    }
}
