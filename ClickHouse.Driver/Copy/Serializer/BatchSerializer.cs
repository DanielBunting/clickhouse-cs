using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.Formats;
using ClickHouse.Driver.Types;

namespace ClickHouse.Driver.Copy.Serializer;

internal class BatchSerializer : IBatchSerializer
{
    public static BatchSerializer GetByRowBinaryFormat(RowBinaryFormat format)
    {
        return format switch
        {
            RowBinaryFormat.RowBinary => new BatchSerializer(new RowBinarySerializer()),
            RowBinaryFormat.RowBinaryWithDefaults => new BatchSerializer(new RowBinaryWithDefaultsSerializer()),
            _ => throw new NotSupportedException(format.ToString())
        };
    }

    private readonly IRowSerializer rowSerializer;

    public BatchSerializer(IRowSerializer rowSerializer)
    {
        this.rowSerializer = rowSerializer;
    }

    public void Serialize(Batch batch, Stream stream)
    {
        using var gzipStream = new BufferedStream(new GZipStream(stream, CompressionLevel.Fastest, true), 256 * 1024);
        using (var textWriter = new StreamWriter(gzipStream, Encoding.UTF8, 4 * 1024, true))
        {
            textWriter.WriteLine(batch.Query);
        }

        using var writer = new ExtendedBinaryWriter(gzipStream);
        SerializeRows(batch.Rows, batch.Size, batch.Types, writer);
    }

    /// <summary>
    /// Streams every batch into a single GZip-compressed request body. The compression and writer
    /// pipeline is built once; compressed bytes are flushed to <paramref name="stream"/> at each batch
    /// boundary so client serialization overlaps server ingestion within one INSERT.
    /// </summary>
    public async Task SerializeStreamingAsync(string query, IEnumerable<Batch> batches, Stream stream, Action<long> onBatchSerialized, CancellationToken token)
    {
        using var gzipStream = new BufferedStream(new GZipStream(stream, CompressionLevel.Fastest, true), 256 * 1024);
        using (var textWriter = new StreamWriter(gzipStream, Encoding.UTF8, 4 * 1024, true))
        {
            textWriter.WriteLine(query);
        }

        using var writer = new ExtendedBinaryWriter(gzipStream);

        foreach (var batch in batches)
        {
            using (batch) // Return the rented array regardless of whether serialization succeeds
            {
                token.ThrowIfCancellationRequested();
                SerializeRows(batch.Rows, batch.Size, batch.Types, writer);
                writer.Flush();
                await gzipStream.FlushAsync(token).ConfigureAwait(false);
                onBatchSerialized?.Invoke(batch.Size);
            }
        }
    }

    internal void SerializeRows(object[][] rows, int size, ClickHouseType[] types, ExtendedBinaryWriter writer)
    {
        object[] row = null;
        try
        {
            var span = rows.AsSpan(0, size);
            for (int i = 0; i < span.Length; i++)
            {
                row = span[i];
                rowSerializer.Serialize(row, types, writer);
            }
        }
        catch (Exception e)
        {
            throw new ClickHouseBulkCopySerializationException(row, e);
        }
    }
}
