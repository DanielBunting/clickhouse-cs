using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClickHouse.Driver.Copy;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests;

[TestFixture]
public class StreamingInsertTests : AbstractConnectionTestFixture
{
    private string CreateTestTableName([CallerMemberName] string testName = null)
        => SanitizeTableName($"test_streaming_{testName}_{Guid.NewGuid():N}");

    private async Task<string> CreateSimpleTestTableAsync([CallerMemberName] string testName = null)
    {
        var tableName = CreateTestTableName(testName);
        await client.ExecuteNonQueryAsync($@"
            CREATE TABLE IF NOT EXISTS test.{tableName}
            (id Int64, name Nullable(String), value Float64)
            ENGINE = MergeTree() ORDER BY id");
        return tableName;
    }

    private static IEnumerable<object[]> GenerateRows(int count)
    {
        for (long i = 0; i < count; i++)
            yield return new object[] { i, $"BulkItem_{i}", i * 1.5 };
    }

    private async Task<ulong> CountInsertQueriesAsync(string queryIdPrefix)
    {
        await client.ExecuteNonQueryAsync("SYSTEM FLUSH LOGS");
        var count = await client.ExecuteScalarAsync(
            $"SELECT count() FROM system.query_log " +
            $"WHERE query_id LIKE '{queryIdPrefix}%' " +
            $"AND query_kind = 'Insert' " +
            $"AND type = 'QueryFinish'");
        return (ulong)count;
    }

    [Test]
    public async Task InsertBinaryAsync_StreamSingleInsert_RoundTripsData()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            var rows = GenerateRows(1000).ToList();
            var inserted = await client.InsertBinaryAsync(
                tableName,
                new[] { "id", "name", "value" },
                rows,
                new InsertOptions { Database = "test", StreamSingleInsert = true });

            Assert.That(inserted, Is.EqualTo(1000));

            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(1000UL));

            var sum = await client.ExecuteScalarAsync($"SELECT sum(id) FROM test.{tableName}");
            Assert.That(Convert.ToInt64(sum), Is.EqualTo(Enumerable.Range(0, 1000).Sum(i => (long)i)));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task InsertBinaryAsync_StreamSingleInsert_AcrossManyBatches_SendsSingleInsert()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            var queryId = $"test_stream_single_{Guid.NewGuid():N}";

            // 50 rows with BatchSize=10 would be 5 separate INSERTs on the default path;
            // streaming must collapse them into one.
            await client.InsertBinaryAsync(
                tableName,
                new[] { "id", "name", "value" },
                GenerateRows(50).ToList(),
                new InsertOptions
                {
                    Database = "test",
                    QueryId = queryId,
                    BatchSize = 10,
                    StreamSingleInsert = true,
                });

            var insertCount = await CountInsertQueriesAsync(queryId);
            Assert.That(insertCount, Is.EqualTo(1UL),
                "StreamSingleInsert should send exactly one INSERT regardless of BatchSize");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task InsertBinaryAsync_DefaultPath_AcrossManyBatches_SendsMultipleInserts()
    {
        // Counterpart to the streaming test above: confirms the default path really does fan out
        // into multiple INSERTs, so the single-INSERT assertion is meaningful.
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            var queryId = $"test_stream_multi_{Guid.NewGuid():N}";

            await client.InsertBinaryAsync(
                tableName,
                new[] { "id", "name", "value" },
                GenerateRows(50).ToList(),
                new InsertOptions
                {
                    Database = "test",
                    QueryId = queryId,
                    BatchSize = 10,
                });

            var insertCount = await CountInsertQueriesAsync(queryId);
            Assert.That(insertCount, Is.EqualTo(5UL),
                "Default path should send one INSERT per batch");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task InsertBinaryAsync_StreamSingleInsert_MatchesDefaultPathContents()
    {
        var streamingTable = await CreateSimpleTestTableAsync();
        var bufferedTable = await CreateSimpleTestTableAsync();
        try
        {
            var rows = GenerateRows(250).ToList();

            await client.InsertBinaryAsync(
                streamingTable,
                new[] { "id", "name", "value" },
                rows,
                new InsertOptions { Database = "test", BatchSize = 30, StreamSingleInsert = true });

            await client.InsertBinaryAsync(
                bufferedTable,
                new[] { "id", "name", "value" },
                rows,
                new InsertOptions { Database = "test", BatchSize = 30 });

            // cityHash64 over the ordered contents must be identical
            var streamingHash = await client.ExecuteScalarAsync(
                $"SELECT sum(cityHash64(id, name, value)) FROM test.{streamingTable}");
            var bufferedHash = await client.ExecuteScalarAsync(
                $"SELECT sum(cityHash64(id, name, value)) FROM test.{bufferedTable}");

            Assert.That(streamingHash, Is.EqualTo(bufferedHash),
                "Streaming and default paths must produce identical table contents");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{streamingTable}");
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{bufferedTable}");
        }
    }

    [Test]
    public async Task InsertBinaryAsync_StreamSingleInsert_WithNulls_RoundTripsData()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            await client.InsertBinaryAsync(
                tableName,
                new[] { "id", "name", "value" },
                new List<object[]>
                {
                    new object[] { 1L, "present", 1.5 },
                    new object[] { 2L, null, 3.0 },
                },
                new InsertOptions { Database = "test", StreamSingleInsert = true });

            var nulls = await client.ExecuteScalarAsync(
                $"SELECT count() FROM test.{tableName} WHERE name IS NULL");
            Assert.That((ulong)nulls, Is.EqualTo(1UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public void InsertBinaryAsync_StreamSingleInsert_WithParallelism_Throws()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.InsertBinaryAsync(
                "nonexistent",
                new[] { "id" },
                new List<object[]> { new object[] { 1L } },
                new InsertOptions
                {
                    ColumnTypes = new Dictionary<string, string> { ["id"] = "Int64" },
                    StreamSingleInsert = true,
                    MaxDegreeOfParallelism = 2,
                }));

        Assert.That(ex.Message, Does.Contain("StreamSingleInsert"));
        Assert.That(ex.Message, Does.Contain("MaxDegreeOfParallelism"));
    }

    [Test]
    public async Task InsertBinaryAsync_StreamSingleInsert_SerializationError_ThrowsWithFailingRow()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            var rows = new List<object[]>
            {
                new object[] { 1L, "ok", 1.5 },
                new object[] { 2L, "bad", "not-a-double" }, // value column is Float64
            };

            Assert.ThrowsAsync<ClickHouseBulkCopySerializationException>(async () =>
                await client.InsertBinaryAsync(
                    tableName,
                    new[] { "id", "name", "value" },
                    rows,
                    new InsertOptions { Database = "test", StreamSingleInsert = true }));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    private class StreamingPoco
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
    }

    [Test]
    public async Task InsertBinaryAsync_Poco_StreamSingleInsert_RoundTripsData()
    {
        var tableName = CreateTestTableName();
        await client.ExecuteNonQueryAsync($@"
            CREATE TABLE IF NOT EXISTS test.{tableName}
            (Id Int64, Name String, Value Float64)
            ENGINE = MergeTree() ORDER BY Id");
        try
        {
            client.RegisterBinaryInsertType<StreamingPoco>();

            var rows = Enumerable.Range(0, 100)
                .Select(i => new StreamingPoco { Id = i, Name = $"Item_{i}", Value = i * 2.0 })
                .ToList();

            var inserted = await client.InsertBinaryAsync(
                tableName,
                rows,
                new InsertOptions { Database = "test", BatchSize = 25, StreamSingleInsert = true });

            Assert.That(inserted, Is.EqualTo(100));

            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(100UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }
}
