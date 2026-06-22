using System.Threading.Channels;

namespace SapOdooMiddleware.Ingestion;

/// <summary>
/// In-process queue of Autohub document ids awaiting extraction. Kept separate from the Lubes
/// extraction queue so the two tenants' pipelines are fully isolated. The parts_catalog
/// staging_document row is the durable source of truth; lost entries on restart are recovered by
/// the Autohub worker's startup sweep.
/// </summary>
public interface IPartsExtractionQueue
{
    void Enqueue(Guid documentId);
    ChannelReader<Guid> Reader { get; }
}

public sealed class PartsExtractionQueue : IPartsExtractionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid documentId) => _channel.Writer.TryWrite(documentId);
}
