using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ClickHouse.Driver.Utility.BlockCompression;

/// <summary>
/// HttpContent wrapper that emits ClickHouse's native block-compression
/// framing. Use for LZ4/ZSTD request bodies — NOT a standard HTTP
/// Content-Encoding, so no Content-Encoding header is added. The server
/// detects the method byte from each frame.
/// </summary>
internal sealed class BlockCompressedContent : HttpContent
{
    private readonly HttpContent originalContent;
    private readonly IBlockCodec codec;
    private readonly int blockSize;
    private bool disposed;

    public BlockCompressedContent(HttpContent content, IBlockCodec codec, int blockSize = BlockFraming.DefaultBlockSize)
    {
        originalContent = content ?? throw new ArgumentNullException(nameof(content));
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));

        foreach (var header in originalContent.Headers)
        {
            // Skip Content-Encoding — block framing is not a standard encoding.
            if (string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
        await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

#if NET5_0_OR_GREATER
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
    {
        await using var source = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await WriteFramedAsync(source, stream, cancellationToken).ConfigureAwait(false);
    }
#endif

    private async Task WriteFramedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            while (true)
            {
                int read = await BlockFraming.ReadAtLeastAsync(source, buf.AsMemory(0, blockSize), required: blockSize, allowEndOfStreamBefore: true, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await BlockFraming.WriteFrameAsync(buf.AsMemory(0, read), codec, destination, cancellationToken).ConfigureAwait(false);

                if (read < blockSize)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            originalContent.Dispose();
            codec.Dispose();
        }
        disposed = true;
        base.Dispose(disposing);
    }
}
