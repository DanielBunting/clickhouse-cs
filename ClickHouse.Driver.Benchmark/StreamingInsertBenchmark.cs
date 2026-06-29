using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace ClickHouse.Driver.Benchmark;

/// <summary>
/// Compares the three binary-insert paths for a large insert (1,000,000 rows at a 100,000-row batch size):
/// <list type="bullet">
/// <item><description><b>DefaultBatched</b> — <c>InsertBinaryAsync</c>: one HTTP request per batch (10 requests).</description></item>
/// <item><description><b>StreamSingleInsert</b> — <c>InsertBinaryAsync</c> with <c>StreamSingleInsert = true</c>: one streaming request, flushed at each 100k boundary.</description></item>
/// <item><description><b>StreamingInserter</b> — <c>CreateBinaryInserter</c>: one streaming request driven by explicit <c>WriteAsync</c>/<c>CompleteAsync</c>.</description></item>
/// </list>
/// All three use the <c>object[]</c> path against an <c>ENGINE Null</c> table, so the measurement isolates
/// per-request overhead + client serialization overlap (the property the streaming paths target) without
/// disk or background-merge noise. Note this does <b>not</b> measure server-side part creation: on a real
/// MergeTree table the batched path would additionally create one part per request, whereas a streaming
/// insert is re-blocked by the server at <c>max_insert_block_size</c> — a separate effect not captured here.
/// <c>MemoryDiagnoser</c> shows that the streaming paths keep client allocations bounded to ~one batch
/// rather than scaling with the full dataset.
/// </summary>
[Config(typeof(ComparisonConfig))]
[MemoryDiagnoser(true)]
public class StreamingInsertBenchmark
{
    private ClickHouseClient client;
    private const string TableName = "test.benchmark_streaming_insert";
    private static readonly string[] Columns = ["Id", "Name", "Value"];

    [Params(1_000_000)]
    public int Count { get; set; }

    /// <summary>
    /// 100k splits 1M rows into 10 batches (10 requests on the default path); 1,000,000 makes a single
    /// batch, so the default path also sends one request and the three paths converge.
    /// </summary>
    [Params(100_000, 1_000_000)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("CLICKHOUSE_CONNECTION")
            ?? "Host=localhost";
        client = new ClickHouseClient(connectionString);

        await client.ExecuteNonQueryAsync("CREATE DATABASE IF NOT EXISTS test");
        await client.ExecuteNonQueryAsync(
            $"CREATE TABLE IF NOT EXISTS {TableName} (Id Int64, Name String, Value Float64) ENGINE Null");
    }

    [GlobalCleanup]
    public void Cleanup() => client?.Dispose();

    /// <summary>One HTTP request per 100k batch (10 requests for 1M rows), sent serially (MaxDOP=1).</summary>
    [Benchmark(Baseline = true)]
    public async Task<long> DefaultBatched()
    {
        var options = new InsertOptions { Database = "test", BatchSize = BatchSize };
        return await client.InsertBinaryAsync(TableName, Columns, GenerateRows(Count), options);
    }

    /// <summary>
    /// Default path with parallelism — the throughput lever <c>StreamSingleInsert</c> gives up (it forces
    /// MaxDOP=1). Batches are serialized and sent concurrently across 4 connections, at the cost of 4
    /// connections and one server-side part per batch. This is the fair competitor to the streaming paths.
    /// </summary>
    [Benchmark]
    public async Task<long> DefaultBatchedParallel()
    {
        var options = new InsertOptions { Database = "test", BatchSize = BatchSize, MaxDegreeOfParallelism = 4 };
        return await client.InsertBinaryAsync(TableName, Columns, GenerateRows(Count), options);
    }

    /// <summary>The whole dataset as one streaming request, flushed at each 100k boundary.</summary>
    [Benchmark]
    public async Task<long> StreamSingleInsert()
    {
        var options = new InsertOptions { Database = "test", BatchSize = BatchSize, StreamSingleInsert = true };
        return await client.InsertBinaryAsync(TableName, Columns, GenerateRows(Count), options);
    }

    /// <summary>One streaming request driven explicitly through the stateful inserter.</summary>
    [Benchmark]
    public async Task<long> StreamingInserter()
    {
        var options = new InsertOptions { Database = "test", BatchSize = BatchSize };
        await using var inserter = client.CreateBinaryInserter(TableName, Columns, options);
        await inserter.InitAsync();
        await inserter.WriteAsync(GenerateRows(Count));
        return await inserter.CompleteAsync();
    }

    private static IEnumerable<object[]> GenerateRows(int count)
    {
        for (int i = 0; i < count; i++)
            yield return new object[] { (long)i, $"sensor_{i % 10}", i * 0.1 };
    }
}
