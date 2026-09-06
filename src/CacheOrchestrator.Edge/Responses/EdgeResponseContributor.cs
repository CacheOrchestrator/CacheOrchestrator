using CacheOrchestrator.Configuration;
using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Diagnostics;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Tags;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CacheOrchestrator.Edge.Responses;

internal sealed class EdgeResponseContributor : ICacheResponseContributor
{
    private readonly IDomainEdgeOptionsProvider _domainOptions;
    private readonly EdgeInstanceResolver _instances;
    private readonly EdgeTagProjector _projector;
    private readonly ILogger<EdgeResponseContributor> _logger;
    private readonly TimeProvider _timeProvider;

    public EdgeResponseContributor(
        IDomainEdgeOptionsProvider domainOptions,
        EdgeInstanceResolver instances,
        EdgeTagProjector projector,
        ILogger<EdgeResponseContributor> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(domainOptions);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(logger);
        _domainOptions = domainOptions;
        _instances = instances;
        _projector = projector;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask ContributeAsync(CacheResponseContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        DomainEdgeOptions options = _domainOptions.GetDomainOptions(context.DomainOptions.Domain);
        if (!options.Enabled)
        {
            return ValueTask.CompletedTask;
        }

        ResolvedEdgeInstance instance = _instances.Resolve(options.InstanceName);
        bool cacheable = context.SharedCacheEligible
            && (HttpMethods.IsGet(context.HttpContext.Request.Method)
                || HttpMethods.IsHead(context.HttpContext.Request.Method));
        IReadOnlyList<string> tags = [];
        if (cacheable && !TryProjectWithinBudget(context, instance, out tags))
        {
            cacheable = false;
        }

        instance.ResponseProvider.ApplyResponseMetadata(context.HttpContext.Response, new EdgeResponseMetadata
        {
            IsCacheable = cacheable,
            Ttl = ResolveTtl(options, context.DomainOptions),
            StaleWhileRevalidate = options.StaleWhileRevalidate,
            StaleIfError = options.StaleIfError,
            Tags = tags
        });
        return ValueTask.CompletedTask;
    }

    private TimeSpan ResolveTtl(DomainEdgeOptions edge, DomainHttpCacheOptions domain)
    {
        int maxSeconds = edge.Ttl.TotalSeconds >= int.MaxValue
            ? int.MaxValue
            : Math.Max(0, (int)edge.Ttl.TotalSeconds);
        DateTimeOffset? scheduledUpdateUtc = domain.ClientTtlSeconds > 0
            ? domain.ScheduledUpdateUtc
            : null;
        ClientCacheScheduleResult schedule = ClientCacheScheduleEvaluator.Evaluate(
            maxSeconds,
            domain.ClientTtlMinSeconds,
            scheduledUpdateUtc,
            _timeProvider.GetUtcNow());
        return TimeSpan.FromSeconds(schedule.MaxAgeSeconds);
    }

    private bool TryProjectWithinBudget(
        CacheResponseContext context,
        ResolvedEdgeInstance instance,
        out IReadOnlyList<string> projectedTags)
    {
        string[] projected = new string[context.Tags.Count];
        int bytes = 0;
        for (int i = 0; i < context.Tags.Count; i++)
        {
            projected[i] = _projector.Project(instance.TagNamespace, context.Tags[i]);
            bytes += projected[i].Length + (i == 0 ? 0 : 1);
        }

        if (bytes <= instance.ResponseProvider.Capabilities.MaxResponseTagBytes)
        {
            projectedTags = projected;
            return true;
        }

        _logger.LogWarning(
            "Edge tag metadata exceeded provider limit for domain '{Domain}'; shared edge caching is disabled for the response",
            context.DomainOptions.Domain);
        EdgeMetrics.RecordFallback(context.DomainOptions.Domain, instance.ResponseProvider.Name);
        projectedTags = [];
        return false;
    }
}
