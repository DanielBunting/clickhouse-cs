using System;
using System.Linq;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Compression;

/// <summary>
/// End-to-end compression sanity tests, one fixture per <see cref="CompressionMethod"/>.
/// Requires a real ClickHouse server (CLICKHOUSE_CONNECTION) and is thus gated under
/// the "Cloud" category alongside the rest of the server-backed suite.
///
/// These round-trips are belt-and-braces over the pure-unit tests under
/// Utility/BlockCompression/: the unit tests lock the algorithm against hard-coded
/// vectors, while these tests bounce real bytes off a real server — which is the
/// only way to catch subtle divergences in the CityHash128 port (for example, the
/// 8–15-byte seed branch that was originally mis-ported from CH.Native would pass
/// any internally-consistent unit test and only fail on server round-trip).
/// </summary>
[Category("Cloud")]
[TestFixtureSource(nameof(Methods))]
public class CompressionRoundTripTests : IDisposable
{
    public static readonly CompressionMethod[] Methods =
    {
        CompressionMethod.None,
        CompressionMethod.Gzip,
        CompressionMethod.Lz4,
        CompressionMethod.Zstd,
    };

    private readonly CompressionMethod method;
    private readonly ClickHouseClient client;

    public CompressionRoundTripTests(CompressionMethod method)
    {
        this.method = method;
        client = TestUtilities.GetTestClickHouseClient(compressionMethod: method);
        client.ExecuteNonQueryAsync("CREATE DATABASE IF NOT EXISTS test;").GetAwaiter().GetResult();
    }

    [OneTimeTearDown]
    public void Dispose() => client?.Dispose();

    [Test]
    public async Task SelectScalar_RoundTrips()
    {
        // Single tiny frame — exercises small-payload CityHash branch for Lz4/Zstd.
        var value = await client.ExecuteScalarAsync("SELECT 42");
        Assert.That(Convert.ToInt32(value), Is.EqualTo(42));
    }

    [Test]
    public async Task SelectMillionRows_RoundTrips()
    {
        // Forces multi-frame responses on the server side (default max_compress_block_size
        // is 1 MiB; 1M UInt64s + ~1M UTF-8 strings easily crosses multiple frames).
        using var reader = await client.ExecuteReaderAsync(
            "SELECT number, toString(number) FROM system.numbers LIMIT 1000000");

        long rows = 0;
        ulong firstNumber = ulong.MaxValue;
        ulong lastNumber = 0;
        string? lastString = null;
        while (await reader.ReadAsync())
        {
            var n = reader.GetFieldValue<ulong>(0);
            if (rows == 0) firstNumber = n;
            lastNumber = n;
            lastString = reader.GetString(1);
            rows++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.EqualTo(1_000_000));
            Assert.That(firstNumber, Is.EqualTo(0UL));
            Assert.That(lastNumber, Is.EqualTo(999_999UL));
            Assert.That(lastString, Is.EqualTo("999999"),
                "Round-tripping the final string across multiple frames must preserve bytes exactly.");
        });
    }

    [Test]
    public async Task InsertBinaryThenSelect_RoundTrips()
    {
        var table = MakeTableName("compression_roundtrip");

        await client.ExecuteNonQueryAsync($"CREATE TABLE IF NOT EXISTS {table} (id UInt64, value String) ENGINE = Memory");
        try
        {
            // 50k rows × ~40 bytes → comfortably exceeds a single block on the request side,
            // so the binary insert path exercises multi-frame writes for Lz4/Zstd.
            var rows = Enumerable.Range(0, 50_000)
                .Select(i => new object[] { (ulong)i, $"row-{i}-{new string('a', 32)}" });
            await client.InsertBinaryAsync(table, new[] { "id", "value" }, rows);

            var count = Convert.ToUInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table}"));
            Assert.That(count, Is.EqualTo(50_000UL));

            var sample = Convert.ToUInt64(await client.ExecuteScalarAsync($"SELECT id FROM {table} WHERE id = 12345"));
            Assert.That(sample, Is.EqualTo(12345UL));
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    /// <summary>
    /// Sizes picked to land in every branch of <see cref="CityHash128.Hash"/>: 0, 1,
    /// the short &lt;8 path, the 8–15 seed-branch (the one that was mis-ported from
    /// CH.Native originally), =16, the 16–127 CityMurmur main path, =128, &gt;128
    /// (unrolled main loop), and finally sizes that straddle the server's 1 MiB
    /// default <c>max_compress_block_size</c> so we also verify multi-frame round-trips.
    ///
    /// Only Lz4/Zstd use native block framing — None/Gzip travel as plain or
    /// standard-Content-Encoding HTTP, so there's no CityHash to agree on; the test
    /// is <see cref="Assert.Ignore(string)"/>d there.
    /// </summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(11)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(129)]
    [TestCase(1024)]
    [TestCase(1024 * 1024 - 1)]
    [TestCase(1024 * 1024)]
    [TestCase(1024 * 1024 + 1)]
    public async Task InsertAndReadBack_PayloadSizesAcrossCityHashBranches(int payloadSize)
    {
        if (method is CompressionMethod.None or CompressionMethod.Gzip)
            Assert.Ignore("CityHash agreement is only meaningful under Lz4/Zstd native block framing.");

        var table = MakeTableName($"cityhash_{payloadSize}");
        await client.ExecuteNonQueryAsync($"CREATE TABLE {table} (payload String) ENGINE = Memory");
        try
        {
            var payload = new string('A', payloadSize);

            try
            {
                await client.InsertBinaryAsync(
                    table,
                    new[] { "payload" },
                    new[] { new object[] { payload } });
            }
            catch (ClickHouseServerException ex) when (ex.Message.Contains("Checksum doesn't match", StringComparison.Ordinal))
            {
                Assert.Fail($"Server rejected our CityHash128 on INSERT (method={method}, size={payloadSize}): {ex.Message}");
            }

            long storedLength;
            try
            {
                storedLength = Convert.ToInt64(
                    await client.ExecuteScalarAsync($"SELECT length(payload) FROM {table}"));
            }
            catch (ClickHouseCompressionException ex)
            {
                Assert.Fail($"Driver rejected server's CityHash128 on SELECT (method={method}, size={payloadSize}): {ex.Message}");
                throw;
            }

            Assert.That(storedLength, Is.EqualTo(payloadSize),
                $"Round-tripped payload length must match (method={method}).");
        }
        finally
        {
            await client.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    private string MakeTableName(string suffix)
    {
        // Keep within ClickHouse's identifier limits and avoid collisions between parameterised fixtures.
        var raw = $"compression_{method}_{suffix}_{Guid.NewGuid():N}";
        if (raw.Length > 60) raw = raw.Substring(0, 60);
        return $"test.{raw}";
    }
}
