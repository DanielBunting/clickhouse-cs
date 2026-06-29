using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests;

[TestFixture]
public class BinaryInserterTests : AbstractConnectionTestFixture
{
    private string CreateTestTableName([CallerMemberName] string testName = null)
        => SanitizeTableName($"test_inserter_{testName}_{Guid.NewGuid():N}");

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
            yield return new object[] { i, $"Item_{i}", i * 1.5 };
    }

    private async Task<ulong> CountInsertQueriesAsync(string queryId)
    {
        await client.ExecuteNonQueryAsync("SYSTEM FLUSH LOGS");
        var count = await client.ExecuteScalarAsync(
            $"SELECT count() FROM system.query_log " +
            $"WHERE query_id LIKE '{queryId}%' AND query_kind = 'Insert' AND type = 'QueryFinish'");
        return (ulong)count;
    }

    [Test]
    public async Task BinaryInserter_WriteThenComplete_RoundTripsData()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            await using var inserter = client.CreateBinaryInserter(
                tableName, new[] { "id", "name", "value" }, new InsertOptions { Database = "test" });
            await inserter.InitAsync();
            await inserter.WriteAsync(GenerateRows(1000).ToList());
            var written = await inserter.CompleteAsync();

            Assert.That(written, Is.EqualTo(1000));
            Assert.That(inserter.RowsWritten, Is.EqualTo(1000));

            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(1000UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task BinaryInserter_MultipleWritesAcrossBatches_SendSingleInsert()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            var queryId = $"test_inserter_one_{Guid.NewGuid():N}";
            await using (var inserter = client.CreateBinaryInserter(
                tableName, new[] { "id", "name", "value" },
                new InsertOptions { Database = "test", QueryId = queryId, BatchSize = 10 }))
            {
                await inserter.InitAsync();
                // 5 separate writes, each smaller than/around the batch size — still one INSERT.
                for (int i = 0; i < 5; i++)
                    await inserter.WriteAsync(GenerateRows(10).ToList());
                await inserter.CompleteAsync();
            }

            Assert.That(await CountInsertQueriesAsync(queryId), Is.EqualTo(1UL),
                "The whole inserter session must be a single INSERT regardless of write/batch count");
            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(50UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task BinaryInserter_WriteRowAsync_RoundTripsData()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            await using var inserter = client.CreateBinaryInserter(
                tableName, new[] { "id", "name", "value" }, new InsertOptions { Database = "test", BatchSize = 7 });
            await inserter.InitAsync();
            foreach (var row in GenerateRows(20))
                await inserter.WriteRowAsync(row);
            var written = await inserter.CompleteAsync();

            Assert.That(written, Is.EqualTo(20));
            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(20UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task BinaryInserter_MatchesInsertBinaryContents()
    {
        var inserterTable = await CreateSimpleTestTableAsync();
        var bufferedTable = await CreateSimpleTestTableAsync();
        try
        {
            var rows = GenerateRows(250).ToList();

            await using (var inserter = client.CreateBinaryInserter(
                inserterTable, new[] { "id", "name", "value" }, new InsertOptions { Database = "test", BatchSize = 40 }))
            {
                await inserter.InitAsync();
                await inserter.WriteAsync(rows);
                await inserter.CompleteAsync();
            }

            await client.InsertBinaryAsync(
                bufferedTable, new[] { "id", "name", "value" }, rows, new InsertOptions { Database = "test" });

            var a = await client.ExecuteScalarAsync($"SELECT sum(cityHash64(id, name, value)) FROM test.{inserterTable}");
            var b = await client.ExecuteScalarAsync($"SELECT sum(cityHash64(id, name, value)) FROM test.{bufferedTable}");
            Assert.That(a, Is.EqualTo(b), "Inserter and InsertBinaryAsync must produce identical contents");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{inserterTable}");
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{bufferedTable}");
        }
    }

    [Test]
    public async Task BinaryInserter_DisposeWithoutComplete_CommitsNothing()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            await using (var inserter = client.CreateBinaryInserter(
                tableName, new[] { "id", "name", "value" }, new InsertOptions { Database = "test", BatchSize = 10 }))
            {
                await inserter.InitAsync();
                // 50 rows is far below max_insert_block_size, so the server holds them in a single
                // uncommitted block. Client-side batch flushes only push compressed bytes over the wire;
                // they do not commit server-side parts. Truncating the request before CompleteAsync
                // therefore commits nothing here. (For an insert large enough to span multiple blocks the
                // server may already have committed earlier parts — abort is not a general rollback.)
                await inserter.WriteAsync(GenerateRows(50).ToList());
            } // DisposeAsync aborts the in-flight INSERT

            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(0UL),
                "Aborting a single-block insert before CompleteAsync must not commit any rows");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task BinaryInserter_WriteBeforeInit_Throws()
    {
        await using var inserter = client.CreateBinaryInserter(
            "nonexistent", new[] { "id" }, new InsertOptions { Database = "test" });
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await inserter.WriteAsync(new List<object[]> { new object[] { 1L } }));
    }

    [Test]
    public async Task BinaryInserter_WriteAfterComplete_Throws()
    {
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            await using var inserter = client.CreateBinaryInserter(
                tableName, new[] { "id", "name", "value" }, new InsertOptions { Database = "test" });
            await inserter.InitAsync();
            await inserter.WriteAsync(GenerateRows(5).ToList());
            await inserter.CompleteAsync();

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await inserter.WriteAsync(GenerateRows(1).ToList()));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    [Test]
    public async Task BinaryInserter_AppliesPerInsertMaxExecutionTime()
    {
        // The streaming session is one request; its server max_execution_time must come from the
        // per-insert InsertOptions (not any client-wide setting). Assert it lands on the INSERT query.
        var tableName = await CreateSimpleTestTableAsync();
        try
        {
            var queryId = $"test_inserter_met_{Guid.NewGuid():N}";
            await using (var inserter = client.CreateBinaryInserter(
                tableName, new[] { "id", "name", "value" },
                new InsertOptions { Database = "test", QueryId = queryId, MaxExecutionTime = TimeSpan.FromSeconds(42) }))
            {
                await inserter.InitAsync();
                await inserter.WriteAsync(GenerateRows(5).ToList());
                await inserter.CompleteAsync();
            }

            await client.ExecuteNonQueryAsync("SYSTEM FLUSH LOGS");
            var applied = await client.ExecuteScalarAsync(
                $"SELECT Settings['max_execution_time'] FROM system.query_log " +
                $"WHERE query_id = '{queryId}' AND query_kind = 'Insert' AND type = 'QueryFinish' LIMIT 1");
            Assert.That(applied?.ToString(), Is.EqualTo("42"),
                "Per-insert MaxExecutionTime must be applied to the streaming INSERT request");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }

    private class InserterPoco
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
    }

    [Test]
    public async Task BinaryInserter_Typed_RoundTripsData()
    {
        var tableName = CreateTestTableName();
        await client.ExecuteNonQueryAsync($@"
            CREATE TABLE IF NOT EXISTS test.{tableName}
            (Id Int64, Name String, Value Float64)
            ENGINE = MergeTree() ORDER BY Id");
        try
        {
            client.RegisterBinaryInsertType<InserterPoco>();

            await using var inserter = client.CreateBinaryInserter<InserterPoco>(
                tableName, new InsertOptions { Database = "test", BatchSize = 25 });
            await inserter.InitAsync();
            await inserter.WriteAsync(Enumerable.Range(0, 100)
                .Select(i => new InserterPoco { Id = i, Name = $"P_{i}", Value = i * 2.0 }));
            var written = await inserter.CompleteAsync();

            Assert.That(written, Is.EqualTo(100));
            var count = await client.ExecuteScalarAsync($"SELECT count() FROM test.{tableName}");
            Assert.That((ulong)count, Is.EqualTo(100UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS test.{tableName}");
        }
    }
}
