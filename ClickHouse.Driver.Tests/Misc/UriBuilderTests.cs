using System;
using System.Collections.Generic;
using System.Web;
using ClickHouse.Driver.ADO;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests.Misc;

public class UriBuilderTests
{
    [Test]
    public void ShouldSetUriParametersCorrectly()
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            Database = "DATABASE",
            ConnectionQueryStringParameters = new Dictionary<string, object> { { "a", 1 }, { "b", "c" } },
            CommandQueryStringParameters = new Dictionary<string, object> { { "c", 1 }, { "d", "c" } },
            UseCompression = false,
            Sql = "SELECT 1",
            SessionId = "SESSION",
            QueryId = "QUERY",
        };

        builder.AddSqlQueryParameter("sqlParameterName", "sqlParameterValue");

        var result = new Uri(builder.ToString());
        var @params = HttpUtility.ParseQueryString(result.Query);

        Assert.Multiple(() =>
        {
            Assert.That(result.Host, Is.EqualTo("some.server"));
            Assert.That(result.Port, Is.EqualTo(123));
            Assert.That(@params.Get("database"), Is.EqualTo("DATABASE"));
            Assert.That(@params.Get("query"), Is.EqualTo("SELECT 1"));
            Assert.That(@params.Get("a"), Is.EqualTo("1"));
            Assert.That(@params.Get("b"), Is.EqualTo("c"));
            Assert.That(@params.Get("c"), Is.EqualTo("1"));
            Assert.That(@params.Get("d"), Is.EqualTo("c"));
            Assert.That(@params.Get("session_id"), Is.EqualTo("SESSION"));
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("false"));
            Assert.That(@params.Get("query_id"), Is.EqualTo("QUERY"));
            Assert.That(@params.Get("param_sqlParameterName"), Is.EqualTo("sqlParameterValue"));
        });
    }

    [Test]
    public void CommandQueryStringParametersShouldOverrideConnectionParameters()
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            ConnectionQueryStringParameters = new Dictionary<string, object> { { "a", 1 } },
            CommandQueryStringParameters = new Dictionary<string, object> { { "a", 2 } },
        };

        var result = new Uri(builder.ToString());
        var @params = HttpUtility.ParseQueryString(result.Query);

        Assert.That(@params.Get("a"), Is.EqualTo("2"));
    }

    [Test]
    public void ConnectionQueryStringParametersShouldOverrideCommonParameters()
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            Database = "DATABASE",
            UseCompression = false,
            Sql = "SELECT 1",
            SessionId = "SESSION",
            QueryId = "QUERY",
            ConnectionQueryStringParameters = new Dictionary<string, object>
            {
                { "database", "overrided" },
                { "enable_http_compression", "overrided" },
                { "query", "overrided" },
                { "session_id", "overrided" },
                { "query_id", "overrided" },
            },
        };

        builder.AddSqlQueryParameter("sqlParameterName", "sqlParameterValue");

        var result = new Uri(builder.ToString());
        var @params = HttpUtility.ParseQueryString(result.Query);

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("database"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("query"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("session_id"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("query_id"), Is.EqualTo("overrided"));
        });
    }

    [Test]
    public void ConnectionQueryStringParametersShouldOverrideSqlQueryParameters()
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            ConnectionQueryStringParameters = new Dictionary<string, object>
            {
                { "param_sqlParameterName", "overrided" },
            },
        };

        builder.AddSqlQueryParameter("sqlParameterName", "sqlParameterValue");

        var result = new Uri(builder.ToString());
        var @params = HttpUtility.ParseQueryString(result.Query);

        Assert.That(@params.Get("param_sqlParameterName"), Is.EqualTo("overrided"));
    }

    [Test]
    public void CommandQueryStringParametersShouldOverrideCommonParameters()
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            Database = "DATABASE",
            UseCompression = false,
            Sql = "SELECT 1",
            SessionId = "SESSION",
            QueryId = "QUERY",
            CommandQueryStringParameters = new Dictionary<string, object>
            {
                { "database", "overrided" },
                { "enable_http_compression", "overrided" },
                { "query", "overrided" },
                { "session_id", "overrided" },
                { "query_id", "overrided" },
            },
        };

        builder.AddSqlQueryParameter("sqlParameterName", "sqlParameterValue");

        var result = new Uri(builder.ToString());
        var @params = HttpUtility.ParseQueryString(result.Query);

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("database"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("query"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("session_id"), Is.EqualTo("overrided"));
            Assert.That(@params.Get("query_id"), Is.EqualTo("overrided"));
        });
    }

    [Test]
    public void CommandQueryStringParametersShouldOverrideSqlQueryParameters()
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            CommandQueryStringParameters = new Dictionary<string, object>
            {
                { "param_sqlParameterName", "overrided" },
            },
        };

        builder.AddSqlQueryParameter("sqlParameterName", "sqlParameterValue");

        var result = new Uri(builder.ToString());
        var @params = HttpUtility.ParseQueryString(result.Query);

        Assert.That(@params.Get("param_sqlParameterName"), Is.EqualTo("overrided"));
    }

    [Test]
    [TestCase("Çay", "%c3%87ay")]
    public void ShouldEncodeUnicodeCharactersCorrectly(string input, string expected)
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://a.b:123"))
        {
            CommandQueryStringParameters = new Dictionary<string, object>
            {
                { "param_input", input },
            },
        };

        Assert.That(builder.ToString(), Contains.Substring(expected));
    }
    
    [Test]
    public void UriBuilder_ShouldIncludeSingleRole()
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"))
        {
            ConnectionRoles = new[] { "admin" }
        };

        var uri = uriBuilder.ToString();

        Assert.That(uri, Does.Contain("role=admin"));
    }

    [Test]
    public void UriBuilder_ShouldIncludeMultipleRoles()
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"))
        {
            ConnectionRoles = new[] { "admin", "reader" }
        };

        var uri = uriBuilder.ToString();

        Assert.That(uri, Does.Contain("role=admin"));
        Assert.That(uri, Does.Contain("role=reader"));
    }

    [Test]
    [TestCase((string)null)]
    [TestCase("")]
    public void UriBuilder_ShouldGenerateQueryIdWhenNullOrEmpty(string queryId)
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"))
        {
            QueryId = queryId,
        };

        var uri = uriBuilder.ToString();
        var @params = HttpUtility.ParseQueryString(new Uri(uri).Query);

        var generatedQueryId = @params.Get("query_id");
        Assert.That(generatedQueryId, Is.Not.Null.And.Not.Empty);
        Assert.That(Guid.TryParse(generatedQueryId, out _), Is.True, "Auto-generated query_id should be a valid GUID");
    }

    [Test]
    public void UriBuilder_ShouldPreserveProvidedQueryId()
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"))
        {
            QueryId = "my-custom-query-id"
        };

        var uri = uriBuilder.ToString();
        var @params = HttpUtility.ParseQueryString(new Uri(uri).Query);

        Assert.That(@params.Get("query_id"), Is.EqualTo("my-custom-query-id"));
    }

    [Test]
    public void UriBuilder_GetEffectiveQueryId_ShouldReturnSameValueOnRepeatedCalls()
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"));

        var queryId1 = uriBuilder.GetEffectiveQueryId();
        var queryId2 = uriBuilder.GetEffectiveQueryId();

        Assert.That(queryId1, Is.EqualTo(queryId2), "GetEffectiveQueryId should return the same cached value");
        Assert.That(Guid.TryParse(queryId1, out _), Is.True, "Auto-generated query_id should be a valid GUID");
    }

    [Test]
    public void UriBuilder_GetEffectiveQueryId_ShouldReturnProvidedQueryId()
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"))
        {
            QueryId = "my-custom-query-id"
        };

        Assert.That(uriBuilder.GetEffectiveQueryId(), Is.EqualTo("my-custom-query-id"));
    }

    [Test]
    public void UriBuilder_DifferentInstances_ShouldGenerateUniqueQueryIds()
    {
        var uriBuilder1 = new ClickHouseUriBuilder(new Uri("http://localhost:8123"));
        var uriBuilder2 = new ClickHouseUriBuilder(new Uri("http://localhost:8123"));

        var queryId1 = uriBuilder1.GetEffectiveQueryId();
        var queryId2 = uriBuilder2.GetEffectiveQueryId();

        Assert.That(queryId1, Is.Not.EqualTo(queryId2), "Different instances should have different auto-generated query_ids");
    }

    [Test]
    public void UriBuilder_SettingQueryId_ShouldClearCachedEffectiveQueryId()
    {
        var uriBuilder = new ClickHouseUriBuilder(new Uri("http://localhost:8123"));

        // First call generates and caches a GUID
        var autoGenerated = uriBuilder.GetEffectiveQueryId();
        Assert.That(Guid.TryParse(autoGenerated, out _), Is.True);

        // Setting QueryId should clear the cache
        uriBuilder.QueryId = "my-custom-id";

        // Now GetEffectiveQueryId should return the custom ID, not the cached GUID
        Assert.That(uriBuilder.GetEffectiveQueryId(), Is.EqualTo("my-custom-id"));
    }

    // --- Compression method query-string contract -----------------------------------
    //
    // These tests pin the exact URL the driver emits for each CompressionMethod.
    // A regression here would silently make the server reply with incompatible framing
    // (Lz4/Zstd needs compress=1/decompress=1; Gzip needs enable_http_compression=true).

    [Test]
    public void ToString_GzipMethod_EmitsEnableHttpCompressionTrue_AndNoBlockFlags()
    {
        var @params = BuildAndParse(builder =>
        {
            builder.UseCompression = true;
            builder.CompressionMethod = CompressionMethod.Gzip;
        });

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("true"));
            Assert.That(@params.Get("compress"), Is.Null);
            Assert.That(@params.Get("decompress"), Is.Null);
            Assert.That(@params.Get("network_compression_method"), Is.Null);
        });
    }

    [Test]
    public void ToString_Lz4Method_EmitsCompressDecompressAndLz4()
    {
        var @params = BuildAndParse(builder =>
        {
            builder.UseCompression = true;
            builder.CompressionMethod = CompressionMethod.Lz4;
        });

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("compress"), Is.EqualTo("1"));
            Assert.That(@params.Get("decompress"), Is.EqualTo("1"));
            Assert.That(@params.Get("network_compression_method"), Is.EqualTo("lz4"));
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("false"));
        });
    }

    [Test]
    public void ToString_ZstdMethod_EmitsCompressDecompressAndZstd()
    {
        var @params = BuildAndParse(builder =>
        {
            builder.UseCompression = true;
            builder.CompressionMethod = CompressionMethod.Zstd;
        });

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("compress"), Is.EqualTo("1"));
            Assert.That(@params.Get("decompress"), Is.EqualTo("1"));
            Assert.That(@params.Get("network_compression_method"), Is.EqualTo("zstd"));
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("false"));
        });
    }

    [Test]
    public void ToString_NoneMethod_EmitsNoCompressionFlags()
    {
        var @params = BuildAndParse(builder =>
        {
            builder.UseCompression = true;
            builder.CompressionMethod = CompressionMethod.None;
        });

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("compress"), Is.Null);
            Assert.That(@params.Get("decompress"), Is.Null);
            Assert.That(@params.Get("network_compression_method"), Is.Null);
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("false"));
        });
    }

    [Test]
    public void ToString_UseCompressionFalse_DisablesAllFlags_RegardlessOfMethod()
    {
        // Reconciliation invariant: UseCompression=false ⇒ behaves exactly like None,
        // mirroring ClickHouseClientSettings.EffectiveCompressionMethod.
        var @params = BuildAndParse(builder =>
        {
            builder.UseCompression = false;
            builder.CompressionMethod = CompressionMethod.Lz4;
        });

        Assert.Multiple(() =>
        {
            Assert.That(@params.Get("compress"), Is.Null);
            Assert.That(@params.Get("decompress"), Is.Null);
            Assert.That(@params.Get("network_compression_method"), Is.Null);
            Assert.That(@params.Get("enable_http_compression"), Is.EqualTo("false"));
        });
    }

    private static System.Collections.Specialized.NameValueCollection BuildAndParse(Action<ClickHouseUriBuilder> configure)
    {
        var builder = new ClickHouseUriBuilder(new Uri("http://some.server:123"))
        {
            Sql = "SELECT 1",
        };
        configure(builder);
        var uri = new Uri(builder.ToString());
        return HttpUtility.ParseQueryString(uri.Query);
    }
}
