using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.FusionCache;

/// <summary>
/// <see cref="IDataCacheProvider"/> backed by ZiggyCreatures FusionCache.
/// </summary>
internal sealed class FusionDataCacheProvider :
    IDataCacheProvider,
    IDataCacheBatchInvalidator,
    IDataCacheProviderCapabilities
{
    private const int InvalidationParallelism = 8;
    private static readonly DataCacheProviderCapabilities ProviderCapabilities = new()
    {
        SupportsNamedInstances = true,
        SupportsFailSafe = true,
        SupportsEagerRefresh = true,
        SupportsBackplane = true,
        SupportsBatchInvalidation = true
    };
    private readonly IFusionCacheProvider _fusionProvider;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options;
    private readonly IFusionDomainSettingsProvider _fusionDomainSettings;
    private readonly ILogger<FusionDataCacheProvider> _logger;
    private readonly ConcurrentDictionary<string, CachedEntryOptions> _entryOptions = new(StringComparer.Ordinal);

    public FusionDataCacheProvider(
        IFusionCacheProvider fusionProvider,
        IOptionsMonitor<CacheOrchestratorOptions> options,
        IFusionDomainSettingsProvider fusionDomainSettings,
        ILogger<FusionDataCacheProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(fusionProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fusionDomainSettings);
        ArgumentNullException.ThrowIfNull(logger);

        _fusionProvider = fusionProvider;
        _options = options;
        _fusionDomainSettings = fusionDomainSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "FusionCache";

    /// <inheritdoc />
    public DataCacheProviderCapabilities Capabilities => ProviderCapabilities;

    private sealed class CachedEntryOptions
    {
        public required DomainCacheOptions DomainOptions { get; init; }
        public required DomainFusionCacheSettings FusionSettings { get; init; }
        public required FusionCacheEntryOptions EntryOptions { get; init; }
    }

    /// <inheritdoc />
    public ValueTask<DataCacheProviderResult<T>> GetOrCreateAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken = default) =>
        GetOrCreateCoreAsync(request, factory, null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataCacheProviderResult<T>> GetOrCreateWithTagsAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, IReadOnlyList<string>> tagSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tagSelector);
        return GetOrCreateCoreAsync(request, factory, tagSelector, cancellationToken);
    }

    private async ValueTask<DataCacheProviderResult<T>> GetOrCreateCoreAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, IReadOnlyList<string>>? tagSelector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);

        IFusionCache fusion = _fusionProvider.GetCache(request.InstanceName);
        FusionCacheEntryOptions entryOptions = GetEntryOptions(request.DomainOptions);
        string[] tags = request.Tags as string[] ?? [.. request.Tags];

        var materializationId = Guid.NewGuid();
        int factoryHadStaleValue = 0;
        FusionProviderCacheEntry<T> entry = await fusion.GetOrSetAsync<FusionProviderCacheEntry<T>>(
                request.Key,
                async (context, token) =>
                {
                    if (context.HasStaleValue)
                        Interlocked.Exchange(ref factoryHadStaleValue, 1);

                    T value = await factory(token).ConfigureAwait(false);
                    if (tagSelector is not null)
                    {
                        IReadOnlyList<string> finalTags = tagSelector(value);
                        ArgumentNullException.ThrowIfNull(finalTags);
                        context.Tags = finalTags as string[] ?? [.. finalTags];
                    }

                    return new FusionProviderCacheEntry<T>
                    {
                        Value = value,
                        MaterializationId = materializationId
                    };
                },
                entryOptions,
                tags: tags,
                token: cancellationToken)
            .ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("FusionDataCacheProvider GetOrCreate Key={Key}", request.Key);

        DataCacheProviderOutcome outcome = entry.MaterializationId == materializationId
            ? DataCacheProviderOutcome.Materialized
            : Volatile.Read(ref factoryHadStaleValue) != 0
                ? DataCacheProviderOutcome.Stale
                : DataCacheProviderOutcome.Cached;
        return new DataCacheProviderResult<T>(entry.Value, outcome);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync<T>(
        DataCacheProviderRequest request,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IFusionCache fusion = _fusionProvider.GetCache(request.InstanceName);
        FusionCacheEntryOptions entryOptions = GetEntryOptions(request.DomainOptions);
        string[] tags = request.Tags as string[] ?? [.. request.Tags];

        await fusion.SetAsync(
                request.Key,
                new FusionProviderCacheEntry<T>
                {
                    Value = value,
                    MaterializationId = Guid.NewGuid()
                },
                entryOptions,
                tags: tags,
                token: cancellationToken)
            .ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("FusionDataCacheProvider Set Key={Key}", request.Key);
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

        List<(IFusionCache Cache, string Tag)> operations = [];
        for (int requestIndex = 0; requestIndex < requests.Count; requestIndex++)
        {
            DataCacheInvalidationRequest request = requests[requestIndex];
            ArgumentNullException.ThrowIfNull(request);

            IEnumerable<string> instances = request.InstanceName is null
                ? _options.CurrentValue.DataCacheInstances.Keys
                : [request.InstanceName];

            foreach (string instanceName in instances)
            {
                IFusionCache fusion = _fusionProvider.GetCache(instanceName);
                for (int tagIndex = 0; tagIndex < request.Tags.Count; tagIndex++)
                {
                    string tag = request.Tags[tagIndex];
                    if (!string.IsNullOrWhiteSpace(tag))
                        operations.Add((fusion, tag));
                }
            }
        }

        if (operations.Count == 0)
            return;

        if (operations.Count == 1)
        {
            (IFusionCache cache, string tag) = operations[0];
            await cache.RemoveByTagAsync(tag, token: cancellationToken).ConfigureAwait(false);
            return;
        }

        await Parallel.ForEachAsync(
                operations,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = InvalidationParallelism
                },
                static async (operation, token) =>
                    await operation.Cache.RemoveByTagAsync(operation.Tag, token: token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private FusionCacheEntryOptions GetEntryOptions(DomainCacheOptions domainOptions)
    {
        string domain = domainOptions.Domain;
        DomainFusionCacheSettings fusionSettings = _fusionDomainSettings.Get(domain);
        if (_entryOptions.TryGetValue(domain, out CachedEntryOptions? cached)
            && ReferenceEquals(cached.DomainOptions, domainOptions)
            && ReferenceEquals(cached.FusionSettings, fusionSettings))
        {
            return cached.EntryOptions;
        }

        FusionCacheEntryOptions entryOptions = FusionEntryOptionsFactory.Create(domainOptions, fusionSettings);
        _entryOptions[domain] = new CachedEntryOptions
        {
            DomainOptions = domainOptions,
            FusionSettings = fusionSettings,
            EntryOptions = entryOptions
        };
        return entryOptions;
    }
}
