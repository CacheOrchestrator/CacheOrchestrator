using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Diagnostics;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Tags;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;

namespace CacheOrchestrator.Edge.Invalidation;

internal sealed class EdgeInvalidationObserver : ICacheInvalidationObserver
{
    private readonly IDomainEdgeOptionsProvider _domainOptions;
    private readonly EdgeInstanceResolver _instances;
    private readonly EdgeTagProjector _projector;
    private readonly IEdgeInvalidationQueue _queue;

    public EdgeInvalidationObserver(
        IDomainEdgeOptionsProvider domainOptions,
        EdgeInstanceResolver instances,
        EdgeTagProjector projector,
        IEdgeInvalidationQueue queue)
    {
        ArgumentNullException.ThrowIfNull(domainOptions);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(queue);
        _domainOptions = domainOptions;
        _instances = instances;
        _projector = projector;
        _queue = queue;
    }

    public ValueTask OnBeforeInvalidateAsync(
        CacheInvalidationContext context,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public async ValueTask OnAfterInvalidateAsync(
        CacheInvalidationContext context,
        CacheInvalidationResult result,
        CancellationToken cancellationToken = default)
    {
        if (result.IsSkipped || context.Origin == CacheInvalidationOrigin.RemoteCluster)
            return;

        var groups = new Dictionary<EdgeInvalidationTarget, HashSet<string>>();
        var captured = new Dictionary<string, (ResolvedEdgeInstance Instance, EdgeInvalidationTarget Target)>(StringComparer.OrdinalIgnoreCase);
        foreach (string canonicalTag in context.Tags)
        {
            string? domain = TryGetDomain(canonicalTag);
            if (domain is null)
                continue;
            DomainEdgeOptions domainOptions = _domainOptions.GetDomainOptions(domain);
            if (!domainOptions.Enabled)
                continue;

            if (!captured.TryGetValue(domainOptions.InstanceName, out (ResolvedEdgeInstance Instance, EdgeInvalidationTarget Target) snapshot))
            {
                ResolvedEdgeInstance resolved = _instances.Resolve(domainOptions.InstanceName);
                snapshot = (resolved, _instances.CaptureTarget(resolved.Name, resolved.InvalidationProvider.Name));
                captured.Add(domainOptions.InstanceName, snapshot);
            }
            (ResolvedEdgeInstance instance, EdgeInvalidationTarget target) = snapshot;
            if (!groups.TryGetValue(target, out HashSet<string>? tags))
            {
                tags = new(StringComparer.Ordinal);
                groups.Add(target, tags);
            }
            tags.Add(_projector.Project(instance.TagNamespace, canonicalTag));
        }

        foreach ((EdgeInvalidationTarget target, HashSet<string> tags) in groups)
        {
            string[] values = [.. tags];
            await _queue.EnqueueAsync(
                new EdgeInvalidationJob(target, values),
                cancellationToken).ConfigureAwait(false);
            EdgeMetrics.RecordQueued(target.InstanceName, target.ProviderName, values.Length);
        }
    }

    private static string? TryGetDomain(string tag)
    {
        string? encoded = null;
        if (tag.StartsWith(CacheTags.DomainPrefix, StringComparison.Ordinal))
            encoded = tag[CacheTags.DomainPrefix.Length..];
        else if (tag.StartsWith(CacheTags.EntityPrefix, StringComparison.Ordinal))
            encoded = FirstSegment(tag, CacheTags.EntityPrefix.Length);
        else if (tag.StartsWith(CacheTags.EntityKindPrefix, StringComparison.Ordinal))
            encoded = FirstSegment(tag, CacheTags.EntityKindPrefix.Length);

        return string.IsNullOrEmpty(encoded) ? null : Uri.UnescapeDataString(encoded);
    }

    private static string? FirstSegment(string value, int start)
    {
        int end = value.IndexOf(':', start);
        return end <= start ? null : value[start..end];
    }
}
