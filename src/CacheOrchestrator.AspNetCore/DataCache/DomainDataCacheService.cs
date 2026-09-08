using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.Entity;
using CacheOrchestrator.Identity;
using CacheOrchestrator.Orchestration;
using CacheOrchestrator.OutputCache;
using CacheOrchestrator.Utilities;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CacheOrchestrator.DataCache;

/// <summary>
/// Default <see cref="IDomainDataCache"/>: HTTP projection (domain, auth, keys, disposition)
/// over <see cref="ICacheOrchestrator"/>.
/// </summary>
internal sealed class DomainDataCacheService : IDomainDataCache
{
    private readonly ICacheOrchestrator _orchestrator;
    private readonly IRequestDomainCacheOptions _domainConfig;
    private readonly IDomainKeyGenerator _keyGenerator;
    private readonly ILogger<DomainDataCacheService> _logger;
    private readonly IAdminStatsCollector _adminStats;

    public DomainDataCacheService(
        ICacheOrchestrator orchestrator,
        IRequestDomainCacheOptions domainConfig,
        IDomainKeyGenerator keyGenerator,
        ILogger<DomainDataCacheService> logger,
        IAdminStatsCollector? adminStats = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(domainConfig);
        ArgumentNullException.ThrowIfNull(keyGenerator);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _domainConfig = domainConfig;
        _keyGenerator = keyGenerator;
        _logger = logger;
        _adminStats = adminStats ?? NoOpAdminStatsCollector.Instance;
    }

    /// <inheritdoc />
    public Task<T> GetOrSetAsync<T>(
        HttpContext http,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
        => GetOrSetCoreAsync(http, domain: null, factory, cancellationToken);

    /// <inheritdoc />
    public Task<T> GetOrSetAsync<T>(
        HttpContext http,
        string domain,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return GetOrSetCoreAsync(http, domain, factory, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> GetOrSetEntityAsync<T>(
        HttpContext http,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(factory);
        EnsureRequestHasPrimaryIdentity(http);

        return GetOrSetFootprintAsync(
            http,
            domain: null,
            useEntityKey: true,
            async token =>
            {
                T? value = await factory(token).ConfigureAwait(false);
                return new FootprintCacheBox<T?>
                {
                    Value = value,
                    IsMiss = value is null,
                    Footprint = EntityFootprint.Empty
                };
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> GetOrSetEntityAsync<T>(
        HttpContext http,
        Func<CancellationToken, Task<EntityCache<T>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(factory);
        EnsureRequestHasPrimaryIdentity(http);

        return GetOrSetFootprintAsync(
            http,
            domain: null,
            useEntityKey: true,
            async token =>
            {
                EntityCache<T> produced = await factory(token).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(produced);
                return new FootprintCacheBox<T?>
                {
                    Value = produced.IsMiss ? default : produced.Value,
                    IsMiss = produced.IsMiss,
                    Footprint = produced.Footprint
                };
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> GetOrSetEntitySetAsync<T>(
        HttpContext http,
        Func<CancellationToken, Task<EntitySet<T>>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(factory);

        string? kind = TryGetRequestEntityKind(http);
        if (string.IsNullOrEmpty(kind))
        {
            throw new InvalidOperationException(
                "Entity kind is not on the request. Use [CacheDomain(domain, entityKind)] / " +
                "CacheOutputWithDomain(domain, entityKind), or SetEntityIdentity before GetOrSetEntitySetAsync.");
        }

        IReadOnlyList<T>? list = await GetOrSetFootprintAsync(
                http,
                domain: null,
                useEntityKey: false,
                async token =>
                {
                    EntitySet<T> produced = await factory(token).ConfigureAwait(false);
                    ArgumentNullException.ThrowIfNull(produced);
                    EntityFootprint footprint = produced.BuildFootprint(kind);
                    return new FootprintCacheBox<IReadOnlyList<T>?>
                    {
                        Value = produced.Value,
                        IsMiss = false,
                        Footprint = footprint
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);

        return list ?? [];
    }

    /// <inheritdoc />
    public void SetEntityIdentity(HttpContext http, string entityKind, string resourceId)
    {
        ArgumentNullException.ThrowIfNull(http);
        EnsureUsableEntityIdentity(entityKind, resourceId);

        ICacheOrchestratorFeature feature = CacheOrchestratorFeatureAccessor.GetOrCreate(http);
        feature.EntityKind = DomainName.NormalizeEntityKind(entityKind);
        feature.ResourceId = DomainName.NormalizeResourceId(resourceId);
    }

    private async Task<T?> GetOrSetFootprintAsync<T>(
        HttpContext http,
        string? domain,
        bool useEntityKey,
        Func<CancellationToken, Task<FootprintCacheBox<T?>>> factory,
        CancellationToken cancellationToken)
    {
        CacheFactoryExecutionFeatureAccessor.GetOrCreate(http).DataCacheObserved = true;
        DomainHttpCacheOptions? opts = ResolveDomainOptions(http, domain);
        if (opts is null)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "Data cache skipped: no domain resolved for {Method} {Path}. " +
                    "Factory runs uncached. Use .CacheOutputWithDomain / [CacheDomain], " +
                    "GetOrSetAsync(http, domain, factory), or EnsureDomainOptions.",
                    http.Request.Method,
                    http.Request.Path.Value);
            }

            return await InvokeFootprintFactoryUncachedAsync(
                    http,
                    DataCacheResult.Unresolved,
                    "unresolved",
                    metricsDomain: "_",
                    adminDomain: null,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!opts.DataCacheEnabled)
        {
            return await InvokeFootprintFactoryUncachedAsync(
                    http,
                    DataCacheResult.Off,
                    "off",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (opts.DataCacheRespectAuthBypass && DomainAuthEvaluator.ShouldBypassForAuth(http, opts))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache skipped due to auth bypass (DataCacheRespectAuthBypass)");

            return await InvokeFootprintFactoryUncachedAsync(
                    http,
                    DataCacheResult.Bypass,
                    "bypass",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (opts.DataCacheRespectNoStore
            && HttpHelper.ContainsCacheDirective(http.Request.Headers.CacheControl, "no-store"))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache skipped due to Cache-Control: no-store");

            return await InvokeFootprintFactoryUncachedAsync(
                    http,
                    DataCacheResult.Bypass,
                    "bypass",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (await TryBypassForIdentityAsync(http, opts, cancellationToken).ConfigureAwait(false))
        {
            return await InvokeFootprintFactoryUncachedAsync(
                    http,
                    DataCacheResult.Bypass,
                    "bypass",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string key = _keyGenerator.Generate(new DomainCacheKeyContext(
            opts,
            http,
            useEntityKey ? DomainCacheKeyShape.Entity : DomainCacheKeyShape.Url));

        EntityFootprint early = BuildEarlyFootprint(http, useEntityKey);
        DataCacheProviderOutcome providerOutcome = DataCacheProviderOutcome.Unknown;
        bool factoryFailed = false;
        long started = Stopwatch.GetTimestamp();

        using Activity? activity = CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
        activity?.SetTag("domain", opts.Domain);
        EntityRef? primary = TryGetRequestPrimary(http);
        if (primary is { } prim)
        {
            activity?.SetTag("entity_kind", prim.EntityKind);
            activity?.SetTag("resource_id", prim.ResourceId);
        }

        FootprintCacheBox<T?> box;
        try
        {
            box = await _orchestrator.GetOrCreateWithFootprintAsync<T>(
                    new CacheEntryRequest
                    {
                        Domain = opts.Domain,
                        Key = key,
                        KeyIsPhysical = true,
                        OutcomeObserver = outcome => providerOutcome = outcome,
                        Footprint = early,
                        AdditionalTags = useEntityKey
                            ? null
                            : TryGetRequestEntityKind(http) is { Length: > 0 } kind
                                ? [CacheTags.EntityKind(opts.Domain, kind)]
                                : null
                    },
                    async token =>
                    {
                        try
                        {
                            FootprintCacheBox<T?> produced = await factory(token).ConfigureAwait(false);
                            // Background refresh may finish after the request has been disposed.
                            EntityFootprint full = primary is { } capturedPrimary
                                ? produced.Footprint.WithPrimary(capturedPrimary)
                                : produced.Footprint;
                            return new FootprintCacheBox<T?>
                            {
                                Value = produced.Value,
                                IsMiss = produced.IsMiss,
                                Footprint = full
                            };
                        }
                        catch (OperationCanceledException)
                        {
                            factoryFailed = true;
                            throw;
                        }
                        catch
                        {
                            factoryFailed = true;
                            throw;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "canceled");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (factoryFailed)
            {
                GetElapsed(started, out long failureTicks, out long failureMs);
                RecordDataCacheAndAdmin(
                    http,
                    opts.Domain,
                    opts.Domain,
                    "fail",
                    failureMs,
                    failureTicks);
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "Data cache ERROR Key={Key}, Error={Error}", key, ex.Message);
            }

            throw;
        }

        EntityFootprintStaging.Stage(http, box.Footprint);

        GetElapsed(started, out long elapsedTicks, out long elapsedMs);
        DataCacheResult dataResult = ToDataCacheResult(providerOutcome);

        SetData(http, dataResult, elapsedMs);
        string resultCode = DataToMetric(dataResult);
        activity?.SetTag("cache.result", resultCode);

        long? resultSizeBytes = providerOutcome == DataCacheProviderOutcome.Materialized
            ? FactoryResultSize.TryEstimateBytes(box.Value)
            : null;

        RecordDataCacheAndAdmin(
            http,
            opts.Domain,
            opts.Domain,
            resultCode,
            elapsedMs,
            elapsedTicks,
            resultSizeBytes);

        if (dataResult == DataCacheResult.Stale)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Data cache STALE Key={Key}, Elapsed={ElapsedMs} ms", key, elapsedMs);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            if (dataResult == DataCacheResult.Miss)
                _logger.LogDebug("Data cache MISS Key={Key}, Elapsed={ElapsedMs} ms", key, elapsedMs);
            else
                _logger.LogDebug("Data cache HIT Key={Key}, Elapsed={ElapsedMs} ms", key, elapsedMs);
        }

        return box.IsMiss ? default : box.Value;
    }

    private async Task<T> GetOrSetCoreAsync<T>(
        HttpContext http,
        string? domain,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(factory);

        CacheFactoryExecutionFeatureAccessor.GetOrCreate(http).DataCacheObserved = true;

        DomainHttpCacheOptions? opts = ResolveDomainOptions(http, domain);

        if (opts is null)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "Data cache skipped: no domain resolved for {Method} {Path}. " +
                    "Factory runs uncached. Use .CacheOutputWithDomain / [CacheDomain], " +
                    "GetOrSetAsync(http, domain, factory), or EnsureDomainOptions.",
                    http.Request.Method,
                    http.Request.Path.Value);
            }

            using Activity? unresolvedActivity =
                CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
            unresolvedActivity?.SetTag("domain", "_");
            unresolvedActivity?.SetTag("cache.result", "unresolved");

            return await InvokeFactoryUncachedAsync(
                    http,
                    DataCacheResult.Unresolved,
                    "unresolved",
                    metricsDomain: "_",
                    adminDomain: null,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await GetOrSetWithOptionsAsync(http, opts, factory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetOrSetWithOptionsAsync<T>(
        HttpContext http,
        DomainHttpCacheOptions opts,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        if (!opts.DataCacheEnabled)
        {
            using Activity? offActivity = CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
            offActivity?.SetTag("domain", opts.Domain);
            offActivity?.SetTag("cache.result", "off");
            return await InvokeFactoryUncachedAsync(
                    http,
                    DataCacheResult.Off,
                    "off",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (opts.DataCacheRespectAuthBypass && DomainAuthEvaluator.ShouldBypassForAuth(http, opts))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache skipped due to auth bypass (DataCacheRespectAuthBypass)");

            using Activity? authBypassActivity = CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
            authBypassActivity?.SetTag("domain", opts.Domain);
            authBypassActivity?.SetTag("cache.result", "bypass");
            return await InvokeFactoryUncachedAsync(
                    http,
                    DataCacheResult.Bypass,
                    "bypass",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (opts.DataCacheRespectNoStore
            && HttpHelper.ContainsCacheDirective(http.Request.Headers.CacheControl, "no-store"))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache skipped due to Cache-Control: no-store");

            using Activity? bypassActivity = CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
            bypassActivity?.SetTag("domain", opts.Domain);
            bypassActivity?.SetTag("cache.result", "bypass");
            return await InvokeFactoryUncachedAsync(
                    http,
                    DataCacheResult.Bypass,
                    "bypass",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (await TryBypassForIdentityAsync(http, opts, cancellationToken).ConfigureAwait(false))
        {
            using Activity? bypassActivity = CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
            bypassActivity?.SetTag("domain", opts.Domain);
            bypassActivity?.SetTag("cache.result", "bypass");
            return await InvokeFactoryUncachedAsync(
                    http,
                    DataCacheResult.Bypass,
                    "bypass",
                    opts.Domain,
                    opts.Domain,
                    factory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string key = _keyGenerator.Generate(
            new DomainCacheKeyContext(opts, http, DomainCacheKeyShape.Url));

        DataCacheProviderOutcome providerOutcome = DataCacheProviderOutcome.Unknown;
        bool factoryFailed = false;
        long started = Stopwatch.GetTimestamp();

        using Activity? activity = CacheOrchestratorActivitySource.Source.StartActivity("cache.dc.get_or_set");
        activity?.SetTag("domain", opts.Domain);

        T? result;
        try
        {
            result = await _orchestrator.GetOrCreateAsync(
                    new CacheEntryRequest
                    {
                        Domain = opts.Domain,
                        Key = key,
                        KeyIsPhysical = true,
                        OutcomeObserver = outcome => providerOutcome = outcome
                    },
                    async token =>
                    {
                        try
                        {
                            return await factory(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            factoryFailed = true;
                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug("Factory canceled for Key={Key}", key);
                            throw;
                        }
                        catch
                        {
                            factoryFailed = true;
                            throw;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "canceled");
            if (factoryFailed && _logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Data cache CANCEL Key={Key}", key);
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (factoryFailed)
            {
                GetElapsed(started, out long failureTicks, out long failureMs);
                RecordDataCacheAndAdmin(
                    http,
                    opts.Domain,
                    opts.Domain,
                    "fail",
                    durationMs: failureMs,
                    elapsedTicks: failureTicks,
                    resultSizeBytes: null);
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "Data cache ERROR Key={Key}, Error={Error}", key, ex.Message);
            }

            throw;
        }

        GetElapsed(started, out long elapsedTicks, out long elapsed);

        DataCacheResult dataResult = ToDataCacheResult(providerOutcome);

        SetData(http, dataResult, elapsed);

        string resultCode = DataToMetric(dataResult);
        activity?.SetTag("cache.result", resultCode);

        long? resultSizeBytes = providerOutcome == DataCacheProviderOutcome.Materialized
            ? FactoryResultSize.TryEstimateBytes(result)
            : null;

        RecordDataCacheAndAdmin(
            http,
            opts.Domain,
            opts.Domain,
            resultCode,
            durationMs: elapsed,
            elapsedTicks: elapsedTicks,
            resultSizeBytes: resultSizeBytes);

        if (dataResult == DataCacheResult.Stale)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Data cache STALE Key={Key}, Elapsed={ElapsedMs} ms", key, elapsed);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            if (dataResult == DataCacheResult.Miss)
                _logger.LogDebug("Data cache MISS Key={Key}, Elapsed={ElapsedMs} ms", key, elapsed);
            else
                _logger.LogDebug("Data cache HIT Key={Key}, Elapsed={ElapsedMs} ms", key, elapsed);
        }

        return result!;
    }

    private static EntityFootprint BuildEarlyFootprint(HttpContext http, bool useEntityKey)
    {
        if (!useEntityKey)
            return EntityFootprint.Empty;

        EntityRef? primary = TryGetRequestPrimary(http);
        return primary is { } p ? new EntityFootprint(p) : EntityFootprint.Empty;
    }

    private static EntityFootprint WithRequestPrimary(HttpContext http, EntityFootprint extra)
    {
        EntityRef? primary = TryGetRequestPrimary(http);
        if (primary is null)
            return extra ?? EntityFootprint.Empty;

        return (extra ?? EntityFootprint.Empty).WithPrimary(primary.Value);
    }

    private static EntityRef? TryGetRequestPrimary(HttpContext http)
    {
        string? kind = TryGetRequestEntityKind(http);
        string? id = TryGetRequestResourceId(http);
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(id))
            return null;
        return new EntityRef(kind, id);
    }

    private static string? TryGetRequestEntityKind(HttpContext http) =>
        http.Features.Get<ICacheOrchestratorFeature>()?.EntityKind is { Length: > 0 } kind ? kind : null;

    private static string? TryGetRequestResourceId(HttpContext http) =>
        http.Features.Get<ICacheOrchestratorFeature>()?.ResourceId is { Length: > 0 } id ? id : null;

    private static void EnsureRequestHasPrimaryIdentity(HttpContext http)
    {
        if (TryGetRequestPrimary(http) is not null)
            return;

        throw new InvalidOperationException(
            "Entity identity is not on the request. Use [CacheDomain(domain, resourceRouteKey, entityKind)] / " +
            "CacheOutputWithDomain(domain, resourceRouteKey, entityKind), or SetEntityIdentity.");
    }

    /// <summary>
    /// When endpoint identity metadata is present, builds material (unless Output Cache already did)
    /// and returns <see langword="true"/> when caching must be skipped (null material).
    /// Absent identity metadata skips this path entirely.
    /// </summary>
    private async ValueTask<bool> TryBypassForIdentityAsync(
        HttpContext http,
        DomainHttpCacheOptions opts,
        CancellationToken cancellationToken)
    {
        if (http.Features.Get<CacheIdentityFeature>() is CacheIdentityFeature existing
            && existing.Resolved)
        {
            return existing.Bypass;
        }

        Endpoint? endpoint = http.GetEndpoint();
        CacheIdentityEndpointMetadata? identityMeta =
            endpoint?.Metadata.GetMetadata<CacheIdentityEndpointMetadata>();
        if (identityMeta is null)
            return false;

        if (!identityMeta.IsResolved)
            CacheIdentityEndpointResolver.EnsureResolved(endpoint!, http.RequestServices);

        if (!identityMeta.TryGetBinding(http.Request.Method, out CacheIdentityBinding? binding)
            || binding.Kind == CacheIdentityKind.Url)
        {
            // Method not bound, or Url-only: no extra identity material for data-cache keys.
            CacheIdentityApplicator.StoreOnFeature(http, CacheIdentityMaterial.Empty, bypass: false, _logger);
            return false;
        }

        CacheIdentityMaterial? material = await CacheIdentityApplicator
            .BuildAsync(binding, http, opts, CacheVarySurface.Fusion, _logger, cancellationToken)
            .ConfigureAwait(false);

        if (material is null)
        {
            CacheIdentityApplicator.StoreOnFeature(http, material: null, bypass: true, _logger);
            return true;
        }

        CacheIdentityApplicator.StoreOnFeature(http, material, bypass: false, _logger);
        return false;
    }

    /// <summary>
    /// 1) Explicit domain argument → EnsureDomainOptions (replaces a different snapshot on the request).
    /// 2) Options already on the request (Output Cache policy usually set them).
    /// 3) Endpoint metadata (DomainOutputCachePolicy / CacheDomainAttribute) → EnsureDomainOptions.
    /// </summary>
    private DomainHttpCacheOptions? ResolveDomainOptions(HttpContext http, string? domain)
    {
        DomainHttpCacheOptions? opts = _domainConfig.GetDomainOptions(http);

        if (!string.IsNullOrWhiteSpace(domain))
        {
            if (opts is not null
                && string.Equals(opts.Domain, DomainName.Normalize(domain), StringComparison.Ordinal))
            {
                return opts;
            }

            return _domainConfig.EnsureDomainOptions(http, domain);
        }

        if (opts is not null)
            return opts;

        string? fromEndpoint = TryResolveDomainFromEndpoint(http);
        if (!string.IsNullOrWhiteSpace(fromEndpoint))
            return _domainConfig.EnsureDomainOptions(http, fromEndpoint);

        return null;
    }

    private static string? TryResolveDomainFromEndpoint(HttpContext http)
    {
        Endpoint? endpoint = http.GetEndpoint();
        if (endpoint is null)
            return null;

        DomainOutputCachePolicy? policy = endpoint.Metadata.OfType<DomainOutputCachePolicy>().LastOrDefault();
        if (policy is not null)
        {
            string resolved = policy.ResolveDomain(http);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        CacheDomainAttribute? attr = endpoint.Metadata.OfType<CacheDomainAttribute>().LastOrDefault();
        return attr?.Domain;
    }

    private static void EnsureUsableEntityIdentity(string entityKind, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (string.IsNullOrEmpty(DomainName.NormalizeEntityKind(entityKind)))
            throw new ArgumentException("Entity kind must contain usable characters after normalization.", nameof(entityKind));
        if (string.IsNullOrEmpty(DomainName.NormalizeResourceId(resourceId)))
            throw new ArgumentException("Resource id must contain usable characters after normalization.", nameof(resourceId));
    }

    private async Task<T> InvokeFactoryUncachedAsync<T>(
        HttpContext http,
        DataCacheResult dataResult,
        string metricResult,
        string metricsDomain,
        string? adminDomain,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        T result;
        try
        {
            result = await factory(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            GetElapsed(started, out long failureTicks, out long failureMs);
            RecordDataCacheAndAdmin(
                http,
                metricsDomain,
                adminDomain,
                "fail",
                failureMs,
                failureTicks);
            throw;
        }

        GetElapsed(started, out long elapsedTicks, out long elapsedMs);
        SetData(http, dataResult, elapsedMs);
        RecordDataCacheAndAdmin(
            http,
            metricsDomain,
            adminDomain,
            metricResult,
            elapsedMs,
            elapsedTicks,
            FactoryResultSize.TryEstimateBytes(result));
        return result;
    }

    private async Task<T?> InvokeFootprintFactoryUncachedAsync<T>(
        HttpContext http,
        DataCacheResult dataResult,
        string metricResult,
        string metricsDomain,
        string? adminDomain,
        Func<CancellationToken, Task<FootprintCacheBox<T?>>> factory,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        FootprintCacheBox<T?> result;
        try
        {
            result = await factory(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            GetElapsed(started, out long failureTicks, out long failureMs);
            RecordDataCacheAndAdmin(
                http,
                metricsDomain,
                adminDomain,
                "fail",
                failureMs,
                failureTicks);
            throw;
        }

        EntityFootprint staged = WithRequestPrimary(http, result.Footprint);
        EntityFootprintStaging.Stage(http, staged);

        GetElapsed(started, out long elapsedTicks, out long elapsedMs);
        SetData(http, dataResult, elapsedMs);
        RecordDataCacheAndAdmin(
            http,
            metricsDomain,
            adminDomain,
            metricResult,
            elapsedMs,
            elapsedTicks,
            FactoryResultSize.TryEstimateBytes(result.Value));
        return result.IsMiss ? default : result.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetElapsed(long started, out long elapsedTicks, out long elapsedMilliseconds)
    {
        elapsedTicks = Stopwatch.GetTimestamp() - started;
        elapsedMilliseconds = (long)(elapsedTicks * 1000d / Stopwatch.Frequency);
    }

    private static void SetData(HttpContext http, DataCacheResult data, long? ms = null)
    {
        ICacheOrchestratorFeature feature = CacheOrchestratorFeatureAccessor.GetOrCreate(http);

        if (feature.Disposition is { } existing)
        {
            existing.Data = data;
            existing.ElapsedMs = ms;
        }
        else
        {
            feature.Disposition = new CacheDisposition { Data = data, ElapsedMs = ms };
        }
    }

    private static string DataToMetric(DataCacheResult d) => d switch
    {
        DataCacheResult.Hit => "hit",
        DataCacheResult.Stale => "stale",
        DataCacheResult.Bypass => "bypass",
        DataCacheResult.Off => "off",
        DataCacheResult.Unresolved => "unresolved",
        DataCacheResult.Miss => "miss",
        _ => "miss"
    };

    private static DataCacheResult ToDataCacheResult(DataCacheProviderOutcome outcome) => outcome switch
    {
        DataCacheProviderOutcome.Cached => DataCacheResult.Hit,
        DataCacheProviderOutcome.Materialized => DataCacheResult.Miss,
        DataCacheProviderOutcome.Stale => DataCacheResult.Stale,
        _ => throw new InvalidOperationException($"Unsupported Data Cache provider outcome '{outcome}'.")
    };

    private void RecordDataCacheAndAdmin(
        HttpContext http,
        string metricsDomain,
        string? adminDomain,
        string result,
        double? durationMs,
        long? elapsedTicks,
        long? resultSizeBytes = null)
    {
        CacheOrchestratorMetricsHttpExtensions.ResolveEndpointKeys(
            http,
            forAdminStats: _adminStats.IsEnabled,
            forMetrics: CacheOrchestratorMetrics.IsDataCacheEnabled,
            out string? endpointKey,
            out string? metricsRoute);
        CacheOrchestratorMetrics.RecordDataCache(
            metricsDomain, result, durationMs, metricsRoute, resultSizeBytes);

        if (!_adminStats.IsEnabled)
            return;

        long? ticks = _adminStats.TrackLatency ? elapsedTicks : null;
        long? size = _adminStats.TrackResultSize ? resultSizeBytes : null;
        _adminStats.RecordDataCache(endpointKey, adminDomain, result, ticks, size);
    }
}
