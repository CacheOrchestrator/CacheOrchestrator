using CacheOrchestrator.Configuration;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.Entity;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CacheOrchestrator.Orchestration;

/// <summary>
/// Default <see cref="ICacheOrchestrator"/>: domain policy + Version keying + tags, then
/// <see cref="IDataCacheProvider"/>.
/// </summary>
internal sealed class CacheOrchestratorService : ICacheOrchestrator
{
    private readonly IDomainCacheOptionsProvider _domainOptions;
    private readonly IDataCacheProvider _dataCache;
    private readonly ILogger<CacheOrchestratorService> _logger;
    private int _nullProviderWarningLogged;

    public CacheOrchestratorService(
        IDomainCacheOptionsProvider domainOptions,
        IDataCacheProvider dataCache,
        ILogger<CacheOrchestratorService> logger)
    {
        ArgumentNullException.ThrowIfNull(domainOptions);
        ArgumentNullException.ThrowIfNull(dataCache);
        ArgumentNullException.ThrowIfNull(logger);

        _domainOptions = domainOptions;
        _dataCache = dataCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<T?> GetOrCreateAsync<T>(
        CacheEntryRequest request,
        Func<CancellationToken, ValueTask<T?>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);

        DomainCacheOptions opts = _domainOptions.GetOrCreateDomainOptions(request.Domain);

        if (!opts.DataCacheEnabled)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache off for domain {Domain}; factory runs uncached.", opts.Domain);

            return await factory(cancellationToken).ConfigureAwait(false);
        }

        DataCacheProviderRequest providerRequest = CreateProviderRequest(opts, request);
        WarnIfNullProviderIsUsed(opts.Domain);

        using Activity? activity = CacheOrchestratorActivitySource.Source.StartActivity("cache.orchestrator.get_or_create");
        activity?.SetTag("domain", opts.Domain);
        activity?.SetTag("provider", _dataCache.Name);

        try
        {
            // Store type is T? so null values can be cached when the caller uses a nullable T.
            DataCacheProviderResult<T?> result = await _dataCache.GetOrCreateAsync(
                    providerRequest,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureKnownOutcome(result.Outcome);
            request.OutcomeObserver?.Invoke(result.Outcome);
            activity?.SetTag("cache.result", OutcomeTag(result.Outcome));
            return result.Value;
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "canceled");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<FootprintCacheBox<T?>> GetOrCreateWithFootprintAsync<T>(
        CacheEntryRequest request,
        Func<CancellationToken, ValueTask<FootprintCacheBox<T?>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);

        DomainCacheOptions opts = _domainOptions.GetOrCreateDomainOptions(request.Domain);

        if (!opts.DataCacheEnabled)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache off for domain {Domain}; factory runs uncached.", opts.Domain);

            FootprintCacheBox<T?> uncached = await factory(cancellationToken).ConfigureAwait(false);
            return NormalizeBox(uncached);
        }

        DataCacheProviderRequest earlyRequest = CreateProviderRequest(opts, request);
        WarnIfNullProviderIsUsed(opts.Domain);
        using Activity? activity = CacheOrchestratorActivitySource.Source.StartActivity("cache.orchestrator.get_or_create_footprint");
        activity?.SetTag("domain", opts.Domain);
        activity?.SetTag("provider", _dataCache.Name);

        try
        {
            DataCacheProviderResult<FootprintCacheBox<T?>> result = await _dataCache.GetOrCreateWithTagsAsync(
                    earlyRequest,
                    async token =>
                    {
                        FootprintCacheBox<T?> produced = await factory(token).ConfigureAwait(false);
                        return NormalizeBox(produced);
                    },
                    box => BuildTags(opts, box.Footprint, request.AdditionalTags),
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureKnownOutcome(result.Outcome);
            request.OutcomeObserver?.Invoke(result.Outcome);

            FootprintCacheBox<T?> box = NormalizeBox(result.Value);

            activity?.SetTag("cache.result", OutcomeTag(result.Outcome));
            return box;
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "canceled");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Physical key: <c>co3:{escapedDomain}:{versionHex}:{logicalKey}</c>.
    /// </summary>
    internal static string BuildPhysicalKey(DomainCacheOptions opts, string logicalKey)
        => string.Concat(opts.PhysicalKeyPrefix, logicalKey);

    internal static IReadOnlyList<string> BuildTags(
        DomainCacheOptions options,
        EntityFootprint? footprint,
        IReadOnlyList<string>? additionalTags)
    {
        string domainTag = options.DomainTag;
        bool hasFootprint = footprint is not null && !ReferenceEquals(footprint, EntityFootprint.Empty);
        if (!hasFootprint && additionalTags is not { Count: > 0 })
            return options.DomainTags;

        if (additionalTags is not { Count: > 0 }
            && footprint!.Primary is { } primary
            && footprint.Members.Count == 0
            && footprint.DependsOn.Count == 0
            && footprint.Aliases.Count == 0)
        {
            return
            [
                domainTag,
                CacheTags.Entity(options.Domain, primary.EntityKind, primary.ResourceId),
                CacheTags.EntityKind(options.Domain, primary.EntityKind)
            ];
        }

        if (!hasFootprint && additionalTags is { Count: 1 })
        {
            string? additionalTag = additionalTags[0];
            if (string.IsNullOrWhiteSpace(additionalTag)
                || string.Equals(additionalTag, domainTag, StringComparison.Ordinal))
            {
                return options.DomainTags;
            }

            return [domainTag, additionalTag];
        }

        List<string> tags = hasFootprint
            ? [.. footprint!.ToTags(options.Domain)]
            : [domainTag];

        if (additionalTags is { Count: > 0 })
        {
            HashSet<string> seen = new(tags, StringComparer.Ordinal);
            for (int i = 0; i < additionalTags.Count; i++)
            {
                string? tag = additionalTags[i];
                if (string.IsNullOrWhiteSpace(tag))
                    continue;
                if (seen.Add(tag))
                    tags.Add(tag);
            }
        }

        return [.. tags];
    }

    private static DataCacheProviderRequest CreateProviderRequest(DomainCacheOptions opts, CacheEntryRequest request)
    {
        string physicalKey = request.KeyIsPhysical
            ? request.Key
            : BuildPhysicalKey(opts, request.Key);

        return new DataCacheProviderRequest
        {
            Key = physicalKey,
            InstanceName = opts.DataCacheInstanceName,
            Tags = BuildTags(opts, request.Footprint, request.AdditionalTags),
            DomainOptions = opts
        };
    }

    private static FootprintCacheBox<T?> NormalizeBox<T>(FootprintCacheBox<T?>? box)
    {
        if (box is null)
        {
            return new FootprintCacheBox<T?>
            {
                Value = default,
                IsMiss = true,
                Footprint = EntityFootprint.Empty
            };
        }

        if (box.Footprint is not null)
            return box;

        return new FootprintCacheBox<T?>
        {
            Value = box.Value,
            IsMiss = box.IsMiss,
            Footprint = box.Footprint ?? EntityFootprint.Empty
        };
    }

    private static void EnsureKnownOutcome(DataCacheProviderOutcome outcome)
    {
        if (outcome == DataCacheProviderOutcome.Unknown)
        {
            throw new InvalidOperationException(
                "The data-cache provider returned an unknown outcome. Providers must explicitly return Cached or Materialized.");
        }

        if (outcome is not DataCacheProviderOutcome.Cached
            and not DataCacheProviderOutcome.Materialized
            and not DataCacheProviderOutcome.Stale)
        {
            throw new InvalidOperationException($"The data-cache provider returned unsupported outcome '{outcome}'.");
        }
    }

    private void WarnIfNullProviderIsUsed(string domain)
    {
        if (_dataCache is not NullDataCacheProvider
            || Interlocked.Exchange(ref _nullProviderWarningLogged, 1) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "Data Cache was requested for domain {Domain}, but no Data Cache provider is registered. " +
            "The factory will run uncached. Register FusionCache or HybridCache to cache data.",
            domain);
    }

    private static string OutcomeTag(DataCacheProviderOutcome outcome) => outcome switch
    {
        DataCacheProviderOutcome.Cached => "hit",
        DataCacheProviderOutcome.Materialized => "miss",
        DataCacheProviderOutcome.Stale => "stale",
        _ => "unknown"
    };

}
