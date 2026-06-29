#nullable enable

using System.Collections.Generic;
using ClickHouse.Driver.Copy;

namespace ClickHouse.Driver;

/// <summary>
/// Options for binary insert operations that can override client-level defaults.
/// </summary>
public sealed class InsertOptions : QueryOptions
{
    /// <summary>
    /// Gets or sets the number of rows per batch. Default is 100,000.
    /// </summary>
    public int BatchSize { get; init; } = 100_000;

    /// <summary>
    /// Gets or sets the maximum number of parallel batch insert operations. Default is 1.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// When <c>true</c>, the entire insert is sent as a single streaming HTTP INSERT instead of one
    /// request per batch. The per-request cost (connection acquisition and query setup) is paid once for
    /// the whole insert instead of once per batch, and client serialization overlaps server ingestion.
    /// <see cref="BatchSize"/> still controls the cadence at which compressed bytes are flushed to the
    /// server, keeping client memory bounded to roughly one batch rather than buffering the whole dataset.
    /// <see cref="MaxDegreeOfParallelism"/> must be 1. Default is <c>false</c>.
    /// <para>
    /// This sends one HTTP request but does <b>not</b> make the insert atomic. ClickHouse re-blocks the
    /// incoming stream at <c>max_insert_block_size</c> (~1M rows), so an insert large enough to exceed it,
    /// or one spanning multiple partitions, is written as multiple parts that commit independently — a
    /// mid-stream failure can therefore leave a partial commit. Only an insert that fits in a single block
    /// within a single partition is atomic.
    /// </para>
    /// <para>
    /// Timeout caveat: the whole insert runs as one HTTP request, so the client-side request timeout
    /// (<c>ClickHouseClientSettings.Timeout</c>, default 2 minutes) must cover the entire insert rather
    /// than resetting per batch as it does in the default path. Raise that timeout for large streaming
    /// inserts. Likewise, if the server enforces <c>max_execution_time</c>, set it (via
    /// <see cref="QueryOptions.MaxExecutionTime"/> or a custom setting) to cover the full insert duration.
    /// </para>
    /// </summary>
    public bool StreamSingleInsert { get; init; }

    /// <summary>
    /// Gets or sets the row binary format to use. Default is RowBinary.
    /// </summary>
    public RowBinaryFormat Format { get; init; } = RowBinaryFormat.RowBinary;

    /// <summary>
    /// Gets or sets explicit column type mappings (key: column name; value: ClickHouse type string).
    /// When set, the schema probe query (<c>SELECT ... WHERE 1=0</c>) is skipped entirely.
    /// Takes priority over <see cref="UseSchemaCache"/>.
    /// <br/>
    /// If this is used, a list of columns <b>must</b> be provided to InsertBinaryAsync().
    /// </summary>
    public IReadOnlyDictionary<string, string>? ColumnTypes { get; init; }

    /// <summary>
    /// Gets or sets whether to cache the table schema per (database, table) combination.
    /// When <c>true</c>, the full table schema is fetched once and reused for subsequent
    /// inserts on the same <see cref="ClickHouseClient"/> instance, regardless of which columns are selected.
    /// Schema changes (e.g. <c>ALTER TABLE</c>) are not detected while cached.
    /// </summary>
    public bool UseSchemaCache { get; init; }

    internal new InsertOptions WithQueryId(string queryId)
    {
        return new InsertOptions
        {
            QueryId = queryId,
            Database = Database,
            Roles = Roles,
            CustomSettings = CustomSettings,
            CustomHeaders = CustomHeaders,
            UseSession = UseSession,
            SessionId = SessionId,
            BearerToken = BearerToken,
            ParameterTypeResolver = ParameterTypeResolver,
            ParameterFormatter = ParameterFormatter,
            MaxExecutionTime = MaxExecutionTime,
            BatchSize = BatchSize,
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            StreamSingleInsert = StreamSingleInsert,
            Format = Format,
            ColumnTypes = ColumnTypes,
            UseSchemaCache = UseSchemaCache,
        };
    }

    internal InsertOptions WithColumnTypes(IReadOnlyDictionary<string, string> columnTypes)
    {
        return new InsertOptions
        {
            QueryId = QueryId,
            Database = Database,
            Roles = Roles,
            CustomSettings = CustomSettings,
            CustomHeaders = CustomHeaders,
            UseSession = UseSession,
            SessionId = SessionId,
            BearerToken = BearerToken,
            ParameterFormatter = ParameterFormatter,
            MaxExecutionTime = MaxExecutionTime,
            BatchSize = BatchSize,
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            StreamSingleInsert = StreamSingleInsert,
            Format = Format,
            ColumnTypes = columnTypes,
            UseSchemaCache = UseSchemaCache,
        };
    }
}
