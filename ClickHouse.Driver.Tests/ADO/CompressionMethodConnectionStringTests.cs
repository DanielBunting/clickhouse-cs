using System;
using ClickHouse.Driver.ADO;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.ADO;

/// <summary>
/// Tests the Compression key's parsing semantics and the new CompressionMethod enum property.
/// Legacy "Compression=true|false" must keep working; new aliases "None|Gzip|Lz4|Zstd" are added.
/// </summary>
public class CompressionMethodConnectionStringTests
{
    [Test]
    public void Compression_DefaultsToGzip_WhenKeyAbsent()
    {
        var builder = new ClickHouseConnectionStringBuilder("Host=localhost");
        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.Gzip));
            Assert.That(builder.Compression, Is.True);
        });
    }

    [Test]
    public void Compression_AcceptsBoolean_True_MapsToGzip()
    {
        var builder = new ClickHouseConnectionStringBuilder("Host=localhost;Compression=true");
        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.Gzip));
            Assert.That(builder.Compression, Is.True);
        });
    }

    [Test]
    public void Compression_AcceptsBoolean_False_MapsToNone()
    {
        var builder = new ClickHouseConnectionStringBuilder("Host=localhost;Compression=false");
        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.None));
            Assert.That(builder.Compression, Is.False);
        });
    }

    [TestCase("none", CompressionMethod.None, false)]
    [TestCase("NONE", CompressionMethod.None, false)]
    [TestCase("None", CompressionMethod.None, false)]
    [TestCase("gzip", CompressionMethod.Gzip, true)]
    [TestCase("Gzip", CompressionMethod.Gzip, true)]
    [TestCase("GZIP", CompressionMethod.Gzip, true)]
    [TestCase("lz4", CompressionMethod.Lz4, true)]
    [TestCase("LZ4", CompressionMethod.Lz4, true)]
    [TestCase("Lz4", CompressionMethod.Lz4, true)]
    [TestCase("zstd", CompressionMethod.Zstd, true)]
    [TestCase("ZSTD", CompressionMethod.Zstd, true)]
    [TestCase("ZsTd", CompressionMethod.Zstd, true)]
    public void Compression_AcceptsEnumNames_CaseInsensitive(string value, CompressionMethod expectedMethod, bool expectedBool)
    {
        var builder = new ClickHouseConnectionStringBuilder($"Host=localhost;Compression={value}");
        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(expectedMethod));
            Assert.That(builder.Compression, Is.EqualTo(expectedBool));
        });
    }

    [Test]
    public void Compression_UnknownValue_Throws()
    {
        var builder = new ClickHouseConnectionStringBuilder("Host=localhost;Compression=brotli");
        var ex = Assert.Throws<ArgumentException>(() => _ = builder.CompressionMethod);
        Assert.That(ex.Message, Does.Contain("brotli"));
        // Message should list the valid values so the user knows what to do.
        Assert.That(ex.Message, Does.Contain("None").And.Contain("Gzip").And.Contain("Lz4").And.Contain("Zstd"));
    }

    [Test]
    public void CompressionMethod_SetLz4_WritesCanonicalName()
    {
        var builder = new ClickHouseConnectionStringBuilder();
        builder.CompressionMethod = CompressionMethod.Lz4;

        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.Lz4));
            Assert.That(builder.Compression, Is.True);
            Assert.That(builder.ConnectionString, Does.Contain("Compression=Lz4"));
        });
    }

    [Test]
    public void CompressionMethod_SetZstd_WritesCanonicalName()
    {
        var builder = new ClickHouseConnectionStringBuilder();
        builder.CompressionMethod = CompressionMethod.Zstd;

        Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.Zstd));
        Assert.That(builder.ConnectionString, Does.Contain("Compression=Zstd"));
    }

    [Test]
    public void CompressionMethod_SetNone_RoundTripsAsLegacyFalse()
    {
        // Preserve legacy on-the-wire shape: "Compression=False" (not "None") so that
        // existing connection strings and dictionaries continue to round-trip byte-identically.
        var builder = new ClickHouseConnectionStringBuilder();
        builder.CompressionMethod = CompressionMethod.None;

        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.None));
            Assert.That(builder.Compression, Is.False);
            Assert.That(builder.ConnectionString, Does.Contain("Compression=False"));
        });
    }

    [Test]
    public void CompressionMethod_SetGzip_RoundTripsAsLegacyTrue()
    {
        var builder = new ClickHouseConnectionStringBuilder();
        builder.CompressionMethod = CompressionMethod.Gzip;

        Assert.Multiple(() =>
        {
            Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.Gzip));
            Assert.That(builder.Compression, Is.True);
            Assert.That(builder.ConnectionString, Does.Contain("Compression=True"));
        });
    }

    [Test]
    public void CompressionBoolSetter_True_StillYieldsGzip()
    {
        // Legacy callers using the bool setter must keep working.
        var builder = new ClickHouseConnectionStringBuilder { Compression = true };
        Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.Gzip));
    }

    [Test]
    public void CompressionBoolSetter_False_StillYieldsNone()
    {
        var builder = new ClickHouseConnectionStringBuilder { Compression = false };
        Assert.That(builder.CompressionMethod, Is.EqualTo(CompressionMethod.None));
    }
}
