using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CacheOrchestrator.HybridCache;

/// <summary>
/// <see cref="IDataCacheProvider"/> backed by Microsoft <see cref="HybridCache"/>.
/// </summary>
/// <remarks>
/// Maps Core <c>DataCache.TtlSeconds</c> (resolved <c>DataCacheTtl</c>) to <see cref="HybridCacheEntryOptions.Expiration"/>.
/// Fusion-only knobs (fail-safe, soft/hard split, eager refresh, factory timeouts, backplane)
/// are not applied. Named data-cache instances are ignored — a single DI <see cref="HybridCache"/>
/// is used for all domains.
/// </remarks>
internal sealed class HybridDataCacheProvider :
    IDataCacheProvider,
    IDataCacheBatchInvalidator,
    IDataCacheProviderCapabilities
{
    private const int InvalidationParallelism = 8;
    private static readonly DataCacheProviderCapabilities ProviderCapabilities = new()
    {
        SupportsBatchInvalidation = true
    };
    private readonly Microsoft.Extensions.Caching.Hybrid.HybridCache _cache;
    private readonly ILogger<HybridDataCacheProvider> _logger;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options;
    private readonly ConcurrentDictionary<string, PreparedDomainOptions> _preparedOptions = new(StringComparer.Ordinal);

    private sealed class PreparedDomainOptions
    {
        public required DomainCacheOptions DomainOptions { get; init; }

        public required HybridCacheEntryOptions EntryOptions { get; init; }

        public required string NamespacePrefix { get; init; }

        public required HybridCacheEntryOptions ValidationReadOptions { get; init; }
    }

    public HybridDataCacheProvider(
        Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        IOptionsMonitor<CacheOrchestratorOptions> options,
        ILogger<HybridDataCacheProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "HybridCache";

    /// <inheritdoc />
    public DataCacheProviderCapabilities Capabilities => ProviderCapabilities;

    /// <inheritdoc />
    public async ValueTask<DataCacheProviderResult<T>> GetOrCreateAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);

        if (!string.Equals(request.InstanceName, "default", StringComparison.OrdinalIgnoreCase)
            && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "HybridCache provider ignores data-cache instance '{Instance}'; using the single DI HybridCache.",
                request.InstanceName);
        }

        PreparedDomainOptions prepared = GetPreparedOptions(request.DomainOptions);
        string physicalKey = Prefix(prepared.NamespacePrefix, request.Key);
        string[] tags = PrefixTags(prepared.NamespacePrefix, request.Tags);

        // Prefer the (key, state, factory) overload — a bare Func<CancellationToken, ValueTask<T>>
        // can bind to the state overload with the factory as TState.
        var materializationId = Guid.NewGuid();
        HybridProviderCacheEntry<T> entry = await _cache.GetOrCreateAsync(
                physicalKey,
                factory,
                async (f, cancel) => new HybridProviderCacheEntry<T>
                {
                    Value = await f(cancel).ConfigureAwait(false),
                    MaterializationId = materializationId
                },
                prepared.EntryOptions,
                tags,
                cancellationToken)
            .ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HybridDataCacheProvider GetOrCreate Key={Key}", physicalKey);

        if (entry.ValidationKey is not null && !await IsValidAsync(entry, prepared, cancellationToken).ConfigureAwait(false))
        {
            await _cache.RemoveAsync(physicalKey, cancellationToken).ConfigureAwait(false);
            return await GetOrCreateWithTagsAsync(request, factory, SelectTags<T>(request.Tags), cancellationToken).ConfigureAwait(false);
        }

        DataCacheProviderOutcome outcome = entry.MaterializationId == materializationId
            ? DataCacheProviderOutcome.Materialized
            : DataCacheProviderOutcome.Cached;
        return new DataCacheProviderResult<T>(entry.Value, outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DataCacheProviderResult<T>> GetOrCreateWithTagsAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, IReadOnlyList<string>> tagSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(tagSelector);

        PreparedDomainOptions prepared = GetPreparedOptions(request.DomainOptions);
        string physicalKey = Prefix(prepared.NamespacePrefix, request.Key);
        string[] tags = PrefixTags(prepared.NamespacePrefix, request.Tags);
        var materializationId = Guid.NewGuid();

        // Only footprint-aware entries need the extra native-cache lookup. Ordinary hits keep
        // the direct provider path. Bound retries when tags are continuously invalidated.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HybridProviderCacheEntry<T> entry = await _cache.GetOrCreateAsync(
                physicalKey,
                factory,
                async (f, token) =>
                {
                    T value = await f(token).ConfigureAwait(false);
                    IReadOnlyList<string> finalTags = tagSelector(value);
                    ArgumentNullException.ThrowIfNull(finalTags);
                    string validationKey = Prefix(prepared.NamespacePrefix, "co3:footprint-tags:" + Guid.NewGuid().ToString("N"));
                    string[] validationTags = PrefixTags(prepared.NamespacePrefix, finalTags);
                    await _cache.SetAsync(
                        validationKey, true, prepared.EntryOptions,
                        validationTags, token).ConfigureAwait(false);
                    return new HybridProviderCacheEntry<T>
                    {
                        Value = value,
                        MaterializationId = materializationId,
                        ValidationKey = validationKey,
                        ValidationTags = validationTags
                    };
                },
                prepared.EntryOptions,
                tags,
                cancellationToken).ConfigureAwait(false);

            if (entry.MaterializationId == materializationId)
                return new DataCacheProviderResult<T>(entry.Value, DataCacheProviderOutcome.Materialized);

            // SetAsync and ordinary factories already attach their complete tags directly.
            if (entry.ValidationKey is null)
                return new DataCacheProviderResult<T>(entry.Value, DataCacheProviderOutcome.Cached);

            // A missing or invalidated marker can never become valid again: every factory
            // execution owns a new key. L2 hits may populate L1; misses only cache false locally.
            bool valid = await IsValidAsync(entry, prepared, cancellationToken).ConfigureAwait(false);
            if (valid)
                return new DataCacheProviderResult<T>(entry.Value, DataCacheProviderOutcome.Cached);

            await _cache.RemoveAsync(physicalKey, cancellationToken).ConfigureAwait(false);
        }

        return new DataCacheProviderResult<T>(
            await factory(cancellationToken).ConfigureAwait(false), DataCacheProviderOutcome.Materialized);
    }

    // Keep this rare fallback closure out of the ordinary hit path.
    private static Func<T, IReadOnlyList<string>> SelectTags<T>(IReadOnlyList<string> tags) => _ => tags;

    private ValueTask<bool> IsValidAsync<T>(
        HybridProviderCacheEntry<T> entry, PreparedDomainOptions prepared, CancellationToken cancellationToken) =>
        _cache.GetOrCreateAsync(
            entry.ValidationKey!,
            static _ => ValueTask.FromResult(false),
            prepared.ValidationReadOptions,
            tags: entry.ValidationTags,
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async ValueTask SetAsync<T>(
        DataCacheProviderRequest request,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.InstanceName, "default", StringComparison.OrdinalIgnoreCase)
            && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "HybridCache provider ignores data-cache instance '{Instance}'; using the single DI HybridCache.",
                request.InstanceName);
        }

        PreparedDomainOptions prepared = GetPreparedOptions(request.DomainOptions);
        string physicalKey = Prefix(prepared.NamespacePrefix, request.Key);
        string[] tags = PrefixTags(prepared.NamespacePrefix, request.Tags);

        await _cache.SetAsync(
                physicalKey,
                new HybridProviderCacheEntry<T>
                {
                    Value = value,
                    MaterializationId = Guid.NewGuid()
                },
                prepared.EntryOptions,
                tags,
                cancellationToken)
            .ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HybridDataCacheProvider Set Key={Key}", physicalKey);
    }

    /// <inheritdoc />
    public ValueTask InvalidateAsync(
        DataCacheInvalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvalidateBatchAsync([request], cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask InvalidateBatchAsync(
        IReadOnlyList<DataCacheInvalidationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        List<string> tags = [];
        for (int requestIndex = 0; requestIndex < requests.Count; requestIndex++)
        {
            DataCacheInvalidationRequest request = requests[requestIndex];
            ArgumentNullException.ThrowIfNull(request);

            string cacheNamespace = ResolveNamespace(request.InstanceName);
            string namespacePrefix = BuildNamespacePrefix(cacheNamespace);
            for (int tagIndex = 0; tagIndex < request.Tags.Count; tagIndex++)
            {
                string tag = request.Tags[tagIndex];
                if (!string.IsNullOrWhiteSpace(tag))
                    tags.Add(Prefix(namespacePrefix, tag));
            }
        }

        if (tags.Count == 0)
            return;

        if (tags.Count == 1)
        {
            await _cache.RemoveByTagAsync(tags[0], cancellationToken).ConfigureAwait(false);
            return;
        }

        await Parallel.ForEachAsync(
                tags,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = InvalidationParallelism
                },
                _cache.RemoveByTagAsync)
            .ConfigureAwait(false);
    }

    private string ResolveNamespace(string? instanceName)
    {
        string name = string.IsNullOrWhiteSpace(instanceName) ? "default" : instanceName;
        CacheOrchestratorOptions current = _options.CurrentValue;
        CacheOrchestratorOptions.DataCacheInstanceOptions instance = current.DataCacheInstances.TryGetValue(
            name,
            out CacheOrchestratorOptions.DataCacheInstanceOptions? configured)
            ? configured
            : new CacheOrchestratorOptions.DataCacheInstanceOptions();
        return instance.GetNamespace(name, current);
    }

    private PreparedDomainOptions GetPreparedOptions(DomainCacheOptions domainOptions)
    {
        string domain = domainOptions.Domain;
        if (_preparedOptions.TryGetValue(domain, out PreparedDomainOptions? cached)
            && ReferenceEquals(cached.DomainOptions, domainOptions))
        {
            return cached;
        }

        var prepared = new PreparedDomainOptions
        {
            DomainOptions = domainOptions,
            // Fusion MaxItemBytes / fail-safe / hard TTL / eager refresh are not mapped.
            EntryOptions = new HybridCacheEntryOptions
            {
                Expiration = domainOptions.DataCacheTtl,
                LocalCacheExpiration = domainOptions.DataCacheTtl,
            },
            ValidationReadOptions = new HybridCacheEntryOptions
            {
                Expiration = domainOptions.DataCacheTtl,
                LocalCacheExpiration = domainOptions.DataCacheTtl,
                Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite
            },
            NamespacePrefix = BuildNamespacePrefix(domainOptions.DataCacheNamespace)
        };
        _preparedOptions[domain] = prepared;
        return prepared;
    }

    private static string[] PrefixTags(string namespacePrefix, IReadOnlyList<string> tags)
    {
        string[] result = new string[tags.Count];
        for (int i = 0; i < tags.Count; i++)
            result[i] = Prefix(namespacePrefix, tags[i]);
        return result;
    }

    private static string BuildNamespacePrefix(string cacheNamespace) =>
        string.IsNullOrWhiteSpace(cacheNamespace)
            ? string.Empty
            : Uri.EscapeDataString(cacheNamespace) + ":";

    private static string Prefix(string namespacePrefix, string value) =>
        namespacePrefix.Length == 0 ? value : string.Concat(namespacePrefix, value);
}
