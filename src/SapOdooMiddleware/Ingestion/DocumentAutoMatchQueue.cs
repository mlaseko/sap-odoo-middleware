using System.Threading.Channels;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// In-process queue of document ids awaiting SAP auto-match. Kept separate from the extraction
/// queue so a slow SAP lookup (or outage) never blocks PDF extraction. The document row is the
/// durable source of truth; lost entries on restart are recovered by the worker's startup sweep.
/// </summary>
public interface IDocumentAutoMatchQueue
{
    void Enqueue(Guid documentId);
    ChannelReader<Guid> Reader { get; }
}

public sealed class DocumentAutoMatchQueue : IDocumentAutoMatchQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid documentId) => _channel.Writer.TryWrite(documentId);
}
