using System;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;

namespace ClickHouse.Driver.Tests.ADO;

// Issue #486: ReadStringsAsByteArrays must not break the driver's own metadata queries,
// and GetString on a column deliberately read as byte[] must fail loudly instead of
// returning the literal "System.Byte[]".
public class ReadStringsAsByteArraysReaderTests : AbstractConnectionTestFixture
{
    private ClickHouseConnection byteArrayConnection;

    [OneTimeSetUp]
    public void OpenByteArrayConnection()
    {
        var builder = TestUtilities.GetConnectionStringBuilder();
        builder.ReadStringsAsByteArrays = true;
        byteArrayConnection = new ClickHouseConnection(builder.ToString());
        byteArrayConnection.Open();
    }

    [OneTimeTearDown]
    public void CloseByteArrayConnection() => byteArrayConnection?.Dispose();

    [Test]
    public async Task GetSchema_ColumnsWithReadStringsAsByteArrays_ReturnsStringMetadata()
    {
        var table = CreateTableName();
        await connection.ExecuteStatementAsync($"CREATE TABLE {table} (id UInt64, name String) ENGINE Memory");
        var bare = table[(table.IndexOf('.') + 1)..];

        var schema = byteArrayConnection.GetSchema("Columns", new[] { TestUtilities.TestDatabase, bare, null });

        Assert.That(schema.Rows.Count, Is.EqualTo(2));
        foreach (var column in new[] { "Database", "Table", "Name", "ProviderType", "DataType" })
            Assert.That(schema.Columns[column].DataType, Is.EqualTo(typeof(string)), $"column {column}");

        var rows = schema.Rows.Cast<DataRow>().ToDictionary(r => (string)r["Name"]);
        Assert.That(rows.Keys, Is.EquivalentTo(new[] { "id", "name" }));
        Assert.That(rows["name"]["ProviderType"], Is.EqualTo("String"));
        // DataType reports the CLR type a reader would return, so it keeps honoring the flag.
        Assert.That(rows["name"]["DataType"], Is.EqualTo("Byte[]"));
        Assert.That(rows["id"]["DataType"], Is.EqualTo("UInt64"));
    }

    [Test]
    [TestCase("'hello'::String")]
    [TestCase("toFixedString('hello', 5)")]
    [TestCase("'hello'::Nullable(String)")]
    [TestCase("'hello'::LowCardinality(String)")]
    [TestCase("CAST('hello', 'SimpleAggregateFunction(any, String)')")]
    public async Task GetString_ReadStringsAsByteArrays_ThrowsInvalidCastException(string expression)
    {
        using var reader = await byteArrayConnection.ExecuteReaderAsync($"SELECT {expression}");
        Assert.That(reader.Read(), Is.True);

        var exception = Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.That(exception.Message, Does.Contain("ReadStringsAsByteArrays"));

        // The accessors the exception message points to keep working.
        Assert.That(reader.GetFieldValue<byte[]>(0), Is.EqualTo(Encoding.UTF8.GetBytes("hello")));
        Assert.That(reader.GetValue(0), Is.EqualTo(Encoding.UTF8.GetBytes("hello")));
    }

    [Test]
    public async Task GetString_ArrayOfUInt8_KeepsLenientCoercion()
    {
        // Array(UInt8) materializes as byte[] regardless of the setting, so the throw above is
        // scoped to String/FixedString columns; other byte[]-valued columns keep ToString()'s coercion.
        using var reader = await byteArrayConnection.ExecuteReaderAsync("SELECT [1, 2]::Array(UInt8)");
        Assert.That(reader.Read(), Is.True);
        Assert.DoesNotThrow(() => reader.GetString(0));
    }
}
