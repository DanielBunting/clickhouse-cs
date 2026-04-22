using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Tests.Utilities;
using ClickHouse.Driver.Utility;
using ClickHouse.Driver.Utility.BlockCompression;
using NUnit.Framework;

namespace ClickHouse.Driver.Tests;

/// <summary>
/// Verifies the exact on-the-wire shape of requests the driver emits for each
/// <see cref="CompressionMethod"/>. Uses <see cref="TrackingHandler"/> to intercept
/// the outbound HttpRequestMessage without needing a real ClickHouse server.
///
/// A regression in any of these assertions would silently produce requests the
/// server can't interpret (wrong framing) or mixes incompatible schemes
/// (Content-Encoding: gzip AND block framing, for example).
/// </summary>
[TestFixture]
public class ClickHouseClientCompressionWireTests
{
    private static HttpResponseMessage EmptyOk() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    private static (ClickHouseClient client, TrackingHandler handler) NewClient(CompressionMethod method)
    {
        var handler = new TrackingHandler(_ => EmptyOk());
        var settings = new ClickHouseClientSettings
        {
            HttpClient = new HttpClient(handler),
            CompressionMethod = method,
            UseCompression = method != CompressionMethod.None,
        };
        return (new ClickHouseClient(settings), handler);
    }

    [Test]
    public async Task ExecuteNonQueryAsync_Gzip_EmitsEnableHttpCompressionAndGzipContentEncoding()
    {
        var (client, handler) = NewClient(CompressionMethod.Gzip);
        using (client) await client.ExecuteNonQueryAsync("SELECT 1");

        var request = handler.Requests.Single();
        var query = request.RequestUri!.Query;

        Assert.Multiple(() =>
        {
            Assert.That(query, Does.Contain("enable_http_compression=true"));
            Assert.That(query, Does.Not.Contain("compress=1"));
            Assert.That(query, Does.Not.Contain("decompress=1"));
            Assert.That(query, Does.Not.Contain("network_compression_method="));

            Assert.That(request.Content, Is.InstanceOf<CompressedContent>());
            Assert.That(request.Content!.Headers.ContentEncoding, Does.Contain("gzip"));
            Assert.That(request.Headers.AcceptEncoding.Select(v => v.Value),
                Does.Contain("gzip").And.Contain("deflate"));
        });
    }

    [Test]
    public async Task ExecuteNonQueryAsync_Lz4_EmitsBlockFramingFlagsAndNoContentEncoding()
    {
        var (client, handler) = NewClient(CompressionMethod.Lz4);
        using (client) await client.ExecuteNonQueryAsync("SELECT 1");

        var request = handler.Requests.Single();
        var query = request.RequestUri!.Query;

        Assert.Multiple(() =>
        {
            Assert.That(query, Does.Contain("compress=1"));
            Assert.That(query, Does.Contain("decompress=1"));
            Assert.That(query, Does.Contain("network_compression_method=lz4"));
            Assert.That(query, Does.Contain("enable_http_compression=false"));

            Assert.That(request.Content, Is.InstanceOf<BlockCompressedContent>());
            // Block framing is NOT a standard Content-Encoding.
            Assert.That(request.Content!.Headers.ContentEncoding, Is.Empty);
            // Accept-Encoding only matters for gzip/deflate; block-framed responses are octet-stream.
            Assert.That(request.Headers.AcceptEncoding, Is.Empty);
        });
    }

    [Test]
    public async Task ExecuteNonQueryAsync_Zstd_EmitsZstdMethod()
    {
        var (client, handler) = NewClient(CompressionMethod.Zstd);
        using (client) await client.ExecuteNonQueryAsync("SELECT 1");

        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Multiple(() =>
        {
            Assert.That(query, Does.Contain("compress=1"));
            Assert.That(query, Does.Contain("decompress=1"));
            Assert.That(query, Does.Contain("network_compression_method=zstd"));
        });
    }

    [Test]
    public async Task ExecuteNonQueryAsync_None_EmitsNoCompressionFlags_AndNoWrapping()
    {
        var (client, handler) = NewClient(CompressionMethod.None);
        using (client) await client.ExecuteNonQueryAsync("SELECT 1");

        var request = handler.Requests.Single();
        var query = request.RequestUri!.Query;

        Assert.Multiple(() =>
        {
            Assert.That(query, Does.Not.Contain("compress=1"));
            Assert.That(query, Does.Not.Contain("decompress=1"));
            Assert.That(query, Does.Not.Contain("network_compression_method="));
            Assert.That(query, Does.Contain("enable_http_compression=false"));

            // No compression wrapper — plain StringContent.
            Assert.That(request.Content, Is.Not.InstanceOf<CompressedContent>());
            Assert.That(request.Content, Is.Not.InstanceOf<BlockCompressedContent>());
            Assert.That(request.Content!.Headers.ContentEncoding, Is.Empty);
        });
    }

    [Test]
    public async Task InsertBinaryAsync_Lz4_BodyIsFramedNotGzipped()
    {
        // Path under test: the insert-binary serializer must skip its GZipStream when the
        // driver is in Lz4 mode, and the resulting plain RowBinary body must be wrapped in
        // BlockCompressedContent at the HTTP layer. The body is captured inside the handler
        // (via a BodyCapturingHandler) because HttpClient disposes the request content once
        // the request completes.
        var capture = new BodyCapturingHandler();
        var settings = new ClickHouseClientSettings
        {
            HttpClient = new HttpClient(capture),
            CompressionMethod = CompressionMethod.Lz4,
        };
        using var client = new ClickHouseClient(settings);

        // Provide types explicitly so no schema-probe SELECT fires (keeps the test tight).
        var options = new InsertOptions
        {
            ColumnTypes = new Dictionary<string, string>
            {
                ["id"] = "UInt64",
                ["value"] = "String",
            },
        };

        await client.InsertBinaryAsync(
            table: "fake",
            columns: new[] { "id", "value" },
            rows: new[] { new object[] { 42UL, "hello" } },
            options: options);

        Assert.That(capture.ContentTypeName, Is.EqualTo(nameof(BlockCompressedContent)),
            "Lz4 insert bodies must be wrapped in BlockCompressedContent — not gzipped.");
        Assert.That(capture.ContentEncoding, Is.Empty);

        var body = capture.Body!;
        Assert.That(body.Length, Is.GreaterThan(BlockFraming.FrameOverhead),
            "body must contain at least one full framed block");

        byte method = body[BlockFraming.ChecksumSize];
        Assert.That(method, Is.EqualTo(BlockFraming.MethodLz4), "first frame must be an LZ4 block");

        uint csize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(BlockFraming.ChecksumSize + 1, 4));
        Assert.That(csize, Is.GreaterThanOrEqualTo((uint)BlockFraming.HeaderSize),
            "csize must include the 9-byte header");

        // CityHash128(header + payload) must equal the leading 16 bytes.
        Span<byte> expected = stackalloc byte[BlockFraming.ChecksumSize];
        CityHash128.HashBytes(body.AsSpan(BlockFraming.ChecksumSize, (int)csize), expected);
        Assert.That(body.AsSpan(0, BlockFraming.ChecksumSize).SequenceEqual(expected), Is.True,
            "Leading 16 bytes of the request body must be CityHash128(header || compressed_payload).");

        // Round-trip the whole body through BlockDecompressionStream; it should yield plain bytes
        // containing the INSERT statement followed by the RowBinary row (no gzip anywhere).
        using var src = new MemoryStream(body);
        using var decompress = new BlockDecompressionStream(src);
        using var decoded = new MemoryStream();
        await decompress.CopyToAsync(decoded);
        var decodedBytes = decoded.ToArray();
        var leadingText = System.Text.Encoding.UTF8.GetString(decodedBytes,
            0, Math.Min(decodedBytes.Length, 100));
        Assert.That(leadingText, Does.Contain("INSERT INTO").And.Contain("FORMAT"),
            "Decoded body must start with the RowBinary INSERT statement — not gzip bytes.");
    }

    /// <summary>
    /// Captures the request body INSIDE <see cref="SendAsync"/>, before HttpClient disposes
    /// the outgoing content. Needed for flows (e.g. InsertBinaryAsync) where the driver
    /// disposes HttpRequestMessage immediately after the request completes.
    /// </summary>
    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }
        public string? ContentTypeName { get; private set; }
        public System.Collections.Generic.IList<string> ContentEncoding { get; private set; } = Array.Empty<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                ContentTypeName = request.Content.GetType().Name;
                ContentEncoding = request.Content.Headers.ContentEncoding.ToList();
                Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            return EmptyOk();
        }
    }
}
