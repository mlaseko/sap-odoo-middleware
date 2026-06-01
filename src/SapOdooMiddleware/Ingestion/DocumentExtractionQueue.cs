using System.Threading.Channels;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// In-process queue of document ids awaiting extraction. The document row's Status is the
/// durable source of truth; this channel just triggers the worker promptly after upload.
/// (Lost queue entries on restart are recovered by the worker's startup sweep.)
/// </summary>
public interface IDocumentExtractionQueue
{
    void Enqueue(Guid documentId);
    ChannelReader<Guid> Reader { get; }
}

public sealed class DocumentExtractionQueue : IDocumentExtractionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid documentId) => _channel.Writer.TryWrite(documentId);
}
