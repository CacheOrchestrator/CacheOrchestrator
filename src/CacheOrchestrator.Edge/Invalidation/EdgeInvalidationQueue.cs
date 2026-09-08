using System.Threading.Channels;
using CacheOrchestrator.Edge.Providers;

namespace CacheOrchestrator.Edge.Invalidation;

/// <summary>One provider-neutral invalidation job containing only opaque projected tags.</summary>
public sealed class EdgeInvalidationJob
{
    /// <summary>Creates a job and copies its tags for safe queue ownership.</summary>
    public EdgeInvalidationJob(EdgeInvalidationTarget target, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tags);
        Target = target;
        Tags = Array.AsReadOnly(tags.ToArray());
    }

    /// <summary>The original routing and credentials snapshot.</summary>
    public EdgeInvalidationTarget Target { get; }
    /// <summary>The target's logical instance.</summary>
    public string InstanceName => Target.InstanceName;
    /// <summary>The target's provider.</summary>
    public string ProviderName => Target.ProviderName;
    /// <summary>Copied opaque projected tags.</summary>
    public IReadOnlyList<string> Tags { get; }
}

/// <summary>Queue boundary for edge invalidation; replace it to use a durable outbox.</summary>
public interface IEdgeInvalidationQueue
{
    /// <summary>Enqueues one invalidation job without performing provider network I/O.</summary>
    ValueTask EnqueueAsync(EdgeInvalidationJob job, CancellationToken cancellationToken);
}

internal sealed class EdgeInvalidationChannel
{
    public EdgeInvalidationChannel(int capacity)
    {
        Channel = System.Threading.Channels.Channel.CreateBounded<EdgeInvalidationJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Channel<EdgeInvalidationJob> Channel { get; }
}

internal sealed class ChannelEdgeInvalidationQueue : IEdgeInvalidationQueue
{
    private readonly EdgeInvalidationChannel _channel;

    public ChannelEdgeInvalidationQueue(EdgeInvalidationChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    public ValueTask EnqueueAsync(EdgeInvalidationJob job, CancellationToken cancellationToken) =>
        _channel.Channel.Writer.WriteAsync(job, cancellationToken);
}
