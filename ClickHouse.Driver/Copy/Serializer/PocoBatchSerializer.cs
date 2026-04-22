using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ClickHouse.Driver.Formats;

namespace ClickHouse.Driver.Copy.Serializer;

/// <summary>
/// Serializes POCO batches directly without intermediate object[] allocation.
/// Mirrors <see cref="BatchSerializer"/> but reads property values from POCOs via compiled getters.
/// </summary>
internal class PocoBatchSerializer
{
    public static PocoBatchSerializer GetByRowBinaryFormat(RowBinaryFormat format)
    {
        return format switch
        {
            RowBinaryFormat.RowBinary => new PocoBatchSerializer(new PocoRowBinarySerializer()),
            RowBinaryFormat.RowBinaryWithDefaults => new PocoBatchSerializer(new PocoRowBinaryWithDefaultsSerializer()),
            _ => throw new NotSupportedException(format.ToString()),
        };
    }

    private readonly IPocoRowSerializer rowSerializer;

    private PocoBatchSerializer(IPocoRowSerializer rowSerializer)
    {
        this.rowSerializer = rowSerializer;
    }

    /// <summary>
    /// Serializes a batch of POCO rows into the target stream using GZip compression.
    /// </summary>
    /// <typeparam name="T">The POCO type.</typeparam>
    /// <param name="batch">The batch of rows, query text, and resolved column types.</param>
    /// <param name="getters">Compiled property accessors, ordered to match the batch's column types.</param>
    /// <param name="stream">The output stream (typically a recyclable memory stream).</param>
    public void Serialize<T>(PocoBatch<T> batch, Func<T, object>[] getters, Stream stream, bool useGzip = true)
    {
        Stream sink = useGzip
            ? new BufferedStream(new GZipStream(stream, CompressionLevel.Fastest, true), 256 * 1024)
            : stream;
        try
        {
            using (var textWriter = new StreamWriter(sink, Encoding.UTF8, 4 * 1024, true))
            {
                textWriter.WriteLine(batch.Query);
            }

            using var writer = new ExtendedBinaryWriter(sink, leaveOpen: true);

            var types = batch.Types;
            T current = default;
            try
            {
                for (int i = 0; i < batch.Size; i++)
                {
                    current = batch.Rows[i];
                    rowSerializer.Serialize(current, getters, types, writer);
                }
            }
            catch (Exception e)
            {
                var failedRow = new object[getters.Length];
                if (current != null)
                {
                    for (int col = 0; col < getters.Length; col++)
                    {
                        try
                        {
                            failedRow[col] = getters[col](current);
                        }
                        catch
                        {
                            // Ignore: don't rethrow inside the catch.
                        }
                    }
                }
                throw new ClickHouseBulkCopySerializationException(failedRow, e);
            }
        }
        finally
        {
            if (useGzip)
                sink.Dispose();
        }
    }
}
