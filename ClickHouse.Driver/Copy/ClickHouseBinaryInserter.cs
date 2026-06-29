using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.Copy.Serializer;

namespace ClickHouse.Driver.Copy;

/// <summary>
/// A stateful, schema-fixed bulk inserter that streams rows into a single ClickHouse INSERT request.
/// <para>
/// Created via <see cref="ClickHouseClient.CreateBinaryInserter(string, IEnumerable{string}, InsertOptions)"/>.
/// <see cref="InitAsync"/> resolves the table schema once (fixing the column shape and serializer) and
/// opens one streaming HTTP INSERT; rows pushed via <see cref="WriteAsync"/>/<see cref="WriteRowAsync"/>
/// stream into that open request; <see cref="CompleteAsync"/> finalizes it and returns the row count.
/// Because rows flow into one open request, the fixed per-request cost (connection acquisition and query
/// setup) is paid once for the whole insert instead of once per batch, and client serialization overlaps
/// server ingestion. Memory stays bounded to roughly one batch: rows are flushed to the server at each
/// <c>BatchSize</c> boundary (with backpressure) rather than buffered in full.
/// </para>
/// <para>
/// Not thread-safe: write from a single logical flow. This is one HTTP request but not necessarily one
/// part, and not atomic: ClickHouse re-blocks the incoming stream at <c>max_insert_block_size</c> (~1M
/// rows), so an insert large enough to exceed it, or one spanning multiple partitions, is written as
/// multiple parts that commit independently. Disposing without calling <see cref="CompleteAsync"/>
/// truncates the request so the server rejects the unsent remainder, but this is not a rollback — any
/// parts the server already committed from earlier blocks remain.
/// The request is subject to the client request timeout (<c>ClickHouseClientSettings.Timeout</c>) for the
/// whole session and the server's <c>max_execution_time</c>; set the latter per-insert via
/// <see cref="QueryOptions.MaxExecutionTime"/> on the <see cref="InsertOptions"/> passed at creation.
/// </para>
/// </summary>
public sealed class ClickHouseBinaryInserter : IAsyncDisposable
{
    private readonly ClickHouseClient client;
    private readonly string table;
    private readonly IEnumerable<string> columns;
    private readonly InsertOptions options;

    private ClickHouseClient.InsertPlan plan;
    private BatchSerializer serializer;
    private StreamingInsertSession session;
    private CancellationTokenSource requestCts;
    private object[][] pending;
    private int batchSize;
    private int pendingCount;
    private long rowsWritten;
    private bool initialized;
    private bool completed;

    internal ClickHouseBinaryInserter(ClickHouseClient client, string table, IEnumerable<string> columns, InsertOptions options)
    {
        this.client = client;
        this.table = table;
        this.columns = columns;
        this.options = options;
    }

    /// <summary>Gets the number of rows serialized into the stream so far.</summary>
    public long RowsWritten => Interlocked.Read(ref rowsWritten);

    /// <summary>
    /// Resolves the table schema once (fixing the column shape and serializer) and opens the streaming INSERT.
    /// </summary>
    /// <param name="cancellationToken">Token bounding the lifetime of the underlying streaming request.</param>
    /// <returns>A task that completes once the request is open and ready to receive rows.</returns>
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
            throw new InvalidOperationException("Inserter is already initialized.");

        plan = await client.PrepareInsertAsync(table, columns, options).ConfigureAwait(false);
        serializer = BatchSerializer.GetByRowBinaryFormat(plan.Options.Format);
        batchSize = plan.Options.BatchSize;
        pending = ArrayPool<object[]>.Shared.Rent(batchSize);

        requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestOptions = plan.Options.WithQueryId(plan.BaseQueryId);

        session = new StreamingInsertSession();
        session.Open(plan.Query, body => client.PostStreamAsync(null, body, isCompressed: true, requestCts.Token, requestOptions));

        initialized = true;
    }

    /// <summary>Appends a single row, flushing to the server when a batch fills.</summary>
    /// <param name="row">The row values, ordered to match the inserter's columns.</param>
    /// <param name="cancellationToken">Token observed while flushing a completed batch.</param>
    /// <returns>A task that completes when the row has been buffered (and flushed, if the batch filled).</returns>
    public Task WriteRowAsync(object[] row, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (row is null)
            throw new ArgumentNullException(nameof(row));

        pending[pendingCount++] = row;
        return pendingCount >= batchSize ? FlushPendingAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>Appends a sequence of rows, flushing to the server at batch boundaries.</summary>
    /// <param name="rows">The rows to append; each is ordered to match the inserter's columns.</param>
    /// <param name="cancellationToken">Token observed while flushing completed batches.</param>
    /// <returns>A task that completes when all rows have been buffered/flushed.</returns>
    public async Task WriteAsync(IEnumerable<object[]> rows, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (rows is null)
            throw new ArgumentNullException(nameof(rows));

        foreach (var row in rows)
        {
            pending[pendingCount++] = row;
            if (pendingCount >= batchSize)
                await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes any remaining rows, closes the INSERT, and returns the total rows written.</summary>
    /// <param name="cancellationToken">Token observed while flushing and finalizing the request.</param>
    /// <returns>The total number of rows written by this inserter.</returns>
    public async Task<long> CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();

        await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        await session.CompleteAsync(cancellationToken).ConfigureAwait(false);

        completed = true;
        ReturnPending();
        return rowsWritten;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!completed && session != null)
            await session.AbortAsync().ConfigureAwait(false);

        session?.Dispose();
        ReturnPending();
        requestCts?.Dispose();
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        if (pendingCount == 0)
            return;

        serializer.SerializeRows(pending, pendingCount, plan.ColumnTypes, session.Writer);
        Interlocked.Add(ref rowsWritten, pendingCount);
        pendingCount = 0;

        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureReady()
    {
        if (!initialized)
            throw new InvalidOperationException($"Call {nameof(InitAsync)} before writing.");
        if (completed)
            throw new InvalidOperationException("Inserter has already been completed.");
    }

    private void ReturnPending()
    {
        if (pending != null)
        {
            ArrayPool<object[]>.Shared.Return(pending, clearArray: true);
            pending = null;
        }
    }
}
