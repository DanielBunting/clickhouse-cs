using System;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Driver.Formats;

namespace ClickHouse.Driver.Copy;

/// <summary>
/// Bridges synchronous, incremental row serialization (push) to a single long-lived streaming INSERT
/// request body (pull). Rows are written through one persistent GZip/writer pipeline into a
/// <see cref="Pipe"/>; the HTTP body callback drains the pipe to the wire. Flushing at batch boundaries
/// applies backpressure so a slow server throttles the producer instead of buffering unboundedly.
/// </summary>
internal sealed class StreamingInsertSession : IDisposable
{
    private readonly Pipe pipe = new();
    private Stream pipeStream;
    private GZipStream gzipStream;
    private BufferedStream bufferedStream;
    private ExtendedBinaryWriter writer;
    private Task<HttpResponseMessage> requestTask;
    private bool finalized;

    /// <summary>The writer rows are serialized into. Valid after <see cref="Open"/>.</summary>
    public ExtendedBinaryWriter Writer => writer;

    /// <summary>
    /// Opens the streaming INSERT: starts the HTTP request (its body draining the pipe) and writes the
    /// INSERT query as the first line of the compressed body.
    /// </summary>
    /// <param name="query">The full INSERT statement, written verbatim as the first body line.</param>
    /// <param name="startRequest">
    /// Starts the HTTP request given a body producer. The producer copies the pipe to the wire and is
    /// invoked by the HTTP stack when it is ready to read the request body.
    /// </param>
    public void Open(string query, Func<Func<Stream, CancellationToken, Task>, Task<HttpResponseMessage>> startRequest)
    {
        requestTask = startRequest(async (httpStream, token) =>
        {
            await pipe.Reader.CopyToAsync(httpStream, token).ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
        });

        // leaveOpen: true so disposing the GZip stream does not complete the PipeWriter — completion is
        // signalled explicitly in CompleteAsync/AbortAsync.
        pipeStream = pipe.Writer.AsStream(leaveOpen: true);
        gzipStream = new GZipStream(pipeStream, CompressionLevel.Fastest, leaveOpen: false);
        bufferedStream = new BufferedStream(gzipStream, 256 * 1024);
        using (var textWriter = new StreamWriter(bufferedStream, Encoding.UTF8, 4 * 1024, leaveOpen: true))
        {
            textWriter.WriteLine(query);
        }

        writer = new ExtendedBinaryWriter(bufferedStream);
    }

    /// <summary>
    /// Flushes buffered compressed bytes to the wire at a batch boundary, applying backpressure.
    /// </summary>
    public async Task FlushAsync(CancellationToken token)
    {
        ThrowIfRequestFaulted();
        writer.Flush();
        await bufferedStream.FlushAsync(token).ConfigureAwait(false);
        ThrowIfRequestFaulted();
    }

    /// <summary>
    /// Finalizes the body (emits the GZip trailer), closes the request, and returns the server response.
    /// </summary>
    public async Task<HttpResponseMessage> CompleteAsync(CancellationToken token)
    {
        finalized = true;
        writer.Flush();
        await bufferedStream.FlushAsync(token).ConfigureAwait(false);

        // Disposing the buffered stream disposes the GZip stream, which writes the trailer into the
        // PipeWriter (pipeStream has leaveOpen: true, so the writer itself is not completed here).
        bufferedStream.Dispose();
        await pipe.Writer.CompleteAsync().ConfigureAwait(false);

        return await requestTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts an unfinished session: the PipeWriter is completed with an exception so the request body is
    /// truncated, causing the server to reject the unread remainder of the INSERT. This is not a rollback:
    /// an insert that already streamed past <c>max_insert_block_size</c> is written as multiple independent
    /// parts, and any the server already committed before the truncation remain.
    /// </summary>
    public async Task AbortAsync()
    {
        if (finalized)
            return;

        finalized = true;

        try
        {
            bufferedStream?.Dispose();
        }
        catch
        {
            // Best-effort during abort.
        }

        await pipe.Writer.CompleteAsync(new OperationCanceledException("Streaming insert aborted before completion.")).ConfigureAwait(false);

        if (requestTask != null)
        {
            try
            {
                await requestTask.ConfigureAwait(false);
            }
            catch
            {
                // Observe the (expected) failure of the aborted request.
            }
        }
    }

    private void ThrowIfRequestFaulted()
    {
        // Surfaces a server/transport failure that aborted the request mid-stream so the caller does not
        // keep serializing into a dead pipe. A faulted task is already complete, so this does not block.
        if (requestTask is { IsFaulted: true })
            requestTask.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Disposes the serialization pipeline. Disposal is idempotent — the streams are normally already
    /// disposed by <see cref="CompleteAsync"/> or <see cref="AbortAsync"/>; this is a final safety net.
    /// </summary>
    public void Dispose()
    {
        // Disposing the writer cascades to the buffered, GZip, and pipe streams.
        writer?.Dispose();
    }
}
