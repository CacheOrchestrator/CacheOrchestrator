using CacheOrchestrator.Configuration;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Admin;

/// <summary>
/// Implements transport-independent management operations from Core services and host adapters.
/// Statistics are exposed as canonical raw counters; presentation metrics belong to consumers.
/// </summary>
internal sealed class CacheOrchestratorManagement : ICacheOrchestratorManagement
{
    private readonly IAdminStatsCollector _stats;
    private readonly IAdminEndpointCatalog _endpoints;
    private readonly IAdminDomainConfigProvider _domainConfig;
    private readonly IDomainRuntimeOverrideStore _overrides;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options;
    private readonly TimeProvider _time;
    private readonly ICacheOrchestratorHealthProbe[] _probes;
    private readonly ICacheOrchestratorInvalidator _invalidator;
    private readonly IClusterCommandBus _bus;
    private readonly IClusterMembership _membership;
    private readonly IInstanceIdProvider _instanceId;
    private readonly ClusterCommandFactory _commands;
    private readonly IDomainSettingsPatchContributor[] _settingsContributors;
    private readonly ILogger<CacheOrchestratorManagement> _logger;
    private readonly IDataCacheProvider _dataCacheProvider;
    private readonly IDomainVersionChangeObserver[] _versionChangeObservers;

    public CacheOrchestratorManagement(
        IAdminStatsCollector stats,
        IAdminEndpointCatalog endpoints,
        IAdminDomainConfigProvider domainConfig,
        IDomainRuntimeOverrideStore overrides,
        IOptionsMonitor<CacheOrchestratorOptions> options,
        ICacheOrchestratorInvalidator invalidator,
        IClusterCommandBus bus,
        IClusterMembership membership,
        IInstanceIdProvider instanceId,
        ClusterCommandFactory commands,
        ILogger<CacheOrchestratorManagement>? logger = null,
        TimeProvider? timeProvider = null,
        IEnumerable<ICacheOrchestratorHealthProbe>? probes = null,
        IEnumerable<IDomainSettingsPatchContributor>? settingsContributors = null,
        IDataCacheProvider? dataCacheProvider = null,
        IEnumerable<IDomainVersionChangeObserver>? versionChangeObservers = null)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(domainConfig);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(invalidator);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(commands);

        _stats = stats;
        _endpoints = endpoints;
        _domainConfig = domainConfig;
        _overrides = overrides;
        _options = options;
        _invalidator = invalidator;
        _bus = bus;
        _membership = membership;
        _instanceId = instanceId;
        _commands = commands;
        _logger = logger ?? NullLogger<CacheOrchestratorManagement>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _probes = probes is null ? [] : [.. probes];
        _settingsContributors = settingsContributors is null ? [] : [.. settingsContributors];
        _dataCacheProvider = dataCacheProvider ?? NullDataCacheProvider.Instance;
        _versionChangeObservers = versionChangeObservers is null ? [] : [.. versionChangeObservers];
    }

    public async Task<AdminHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        CacheOrchestratorOptions.AdminOptions admin = _options.CurrentValue.Admin;
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset started = AdminProcessInfo.StartedAtUtc;
        long uptimeSeconds = (long)Math.Max(0, (now - started).TotalSeconds);
        long requests = 0;
        bool statsOk = true;
        try
        {
            AdminLiveStatsRawSnapshot snap = _stats.GetRawSnapshot();
            foreach (AdminDomainCountersDto d in snap.Domains)
                requests += RequestDenominator(d);
            if (requests == 0)
            {
                foreach (AdminEndpointCountersDto e in snap.UnassignedEndpoints)
                    requests += RequestDenominator(e);
            }
        }
        catch
        {
            statsOk = false;
        }

        bool probesOk = true;
        for (int i = 0; i < _probes.Length; i++)
        {
            ICacheOrchestratorHealthProbe probe = _probes[i];
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                await probe.ProbeAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                probesOk = false;
            }
        }

        DataCacheProviderCapabilities capabilities =
            (_dataCacheProvider as IDataCacheProviderCapabilities)?.Capabilities ?? new();

        return new AdminHealthDto
        {
            Healthy = statsOk && probesOk,
            InstanceId = AdminInstanceId.Resolve(_options.CurrentValue),
            UtcNow = now,
            AdminEnabled = admin.Enabled,
            StartedAtUtc = started,
            UptimeSeconds = uptimeSeconds,
            Requests = requests,
            DataCacheProvider = _dataCacheProvider.Name,
            DataCacheCapabilities = capabilities
        };
    }

    public async Task<AdminClusterInfoDto> GetClusterInfoAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ClusterPeer> peers =
            await _membership.GetPeersAsync(cancellationToken).ConfigureAwait(false);

        return new AdminClusterInfoDto
        {
            InstanceId = _instanceId.InstanceId,
            Namespace = _options.CurrentValue.Namespace ?? string.Empty,
            BusEnabled = _bus.IsEnabled,
            Membership = _membership.Kind,
            Peers = [.. peers.Select(peer => new AdminClusterPeerDto
            {
                Id = peer.Id,
                Url = peer.BaseUrl.ToString()
            })]
        };
    }

    /// <summary>
    /// Raw process-lifetime counters (internal / diagnostics).
    /// Prefer OTEL meter <c>CacheOrchestrator</c> + Prometheus for analytics.
    /// </summary>
    public AdminLiveStatsRawSnapshot GetStats()
    {
        string instanceId = AdminInstanceId.Resolve(_options.CurrentValue);
        AdminLiveStatsRawSnapshot raw = _stats.GetRawSnapshot();
        IReadOnlyList<AdminEndpointInfoDto> discovered = _endpoints.GetEndpoints();

        var discoveredByRoute =
            discovered.ToDictionary(e => e.Route, StringComparer.Ordinal);

        Dictionary<string, List<AdminEndpointCountersDto>> byDomain = new(StringComparer.Ordinal);
        List<AdminEndpointCountersDto> unassigned = [];
        List<AdminEndpointCountersDto> allEndpoints = [];

        foreach (AdminEndpointCountersDto ep in raw.UnassignedEndpoints)
        {
            string? domain = null;
            if (discoveredByRoute.TryGetValue(ep.Route, out AdminEndpointInfoDto? info))
                domain = info.ConfiguredDomain;
            domain ??= ep.ConfiguredDomain;

            AdminEndpointCountersDto normalized = CloneEndpoint(ep, instanceId, domain);
            allEndpoints.Add(normalized);

            if (string.IsNullOrEmpty(domain))
            {
                unassigned.Add(normalized);
                continue;
            }

            if (!byDomain.TryGetValue(domain, out List<AdminEndpointCountersDto>? list))
            {
                list = [];
                byDomain[domain] = list;
            }

            list.Add(normalized);
        }

        HashSet<string> domainNames = new(StringComparer.Ordinal);
        foreach (AdminDomainCountersDto d in raw.Domains)
            domainNames.Add(d.Name);
        foreach (string d in _options.CurrentValue.Domains.Keys)
            domainNames.Add(DomainName.Normalize(d));
        foreach (string d in _overrides.GetOverriddenDomains())
            domainNames.Add(d);
        foreach (AdminEndpointInfoDto ep in discovered)
        {
            if (!string.IsNullOrEmpty(ep.ConfiguredDomain))
                domainNames.Add(ep.ConfiguredDomain);
        }

        var rawDomains =
            raw.Domains.ToDictionary(d => d.Name, StringComparer.Ordinal);

        List<AdminDomainCountersDto> domains = [];
        foreach (string name in domainNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            AdminDomainConfigDto domainConfig = _domainConfig.GetDomainConfig(name);
            rawDomains.TryGetValue(name, out AdminDomainCountersDto? counters);

            if (!byDomain.TryGetValue(name, out List<AdminEndpointCountersDto>? epList))
                epList = [];

            HashSet<string> present = new(epList.Select(e => e.Route), StringComparer.Ordinal);
            foreach (AdminEndpointInfoDto info in discovered)
            {
                if (!string.Equals(info.ConfiguredDomain, name, StringComparison.Ordinal))
                    continue;
                if (!present.Add(info.Route))
                    continue;

                AdminEndpointCountersDto empty = EmptyEndpoint(info.Route, name, instanceId);
                epList.Add(empty);
                allEndpoints.Add(empty);
            }

            epList.Sort(static (a, b) => string.CompareOrdinal(a.Route, b.Route));

            AdminDomainCountersDto row = BuildDomainRow(
                name,
                instanceId,
                domainConfig,
                counters,
                epList);

            domains.Add(row);
        }

        allEndpoints.Sort(static (a, b) => string.CompareOrdinal(a.Route, b.Route));

        return new AdminLiveStatsRawSnapshot
        {
            InstanceId = instanceId,
            CollectedAtUtc = _time.GetUtcNow(),
            Domains = domains,
            UnassignedEndpoints = [.. unassigned.OrderBy(e => e.Route, StringComparer.Ordinal)],
            Endpoints = allEndpoints
        };
    }

    public IReadOnlyList<AdminEndpointInfoDto> GetEndpoints() => _endpoints.GetEndpoints();

    public IReadOnlyList<AdminDomainConfigDto> GetDomains()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string d in _options.CurrentValue.Domains.Keys)
            names.Add(DomainName.Normalize(d));
        foreach (string d in _overrides.GetOverriddenDomains())
            names.Add(d);
        foreach (AdminEndpointInfoDto ep in _endpoints.GetEndpoints())
        {
            if (!string.IsNullOrEmpty(ep.ConfiguredDomain))
                names.Add(ep.ConfiguredDomain);
        }

        foreach (AdminDomainCountersDto d in _stats.GetRawSnapshot().Domains)
            names.Add(d.Name);

        return [.. names.OrderBy(n => n, StringComparer.Ordinal).Select(_domainConfig.GetDomainConfig)];
    }

    public AdminDomainConfigDto? GetDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;
        return _domainConfig.GetDomainConfig(DomainName.Normalize(domain));
    }

    public AdminDomainSettingsCatalogDto GetDomainSettingsCatalog() =>
        new()
        {
            Settings = DomainSettingCatalog.GetEntries()
        };

    public async Task<CacheInvalidationResult> InvalidateAsync(
        AdminInvalidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string scope = (request.Scope ?? "domain").Trim().ToLowerInvariant();
        using IDisposable? localOnly = request.Distribute ? null : ClusterCommandScope.EnterLocalOnly();

        return scope switch
        {
            "domain" when string.IsNullOrWhiteSpace(request.Domain) =>
                throw new ArgumentException("domain is required for scope=domain.", nameof(request)),
            "domain" => await _invalidator.InvalidateDomainAsync(request.Domain, cancellationToken)
                .ConfigureAwait(false),
            "entity" when string.IsNullOrWhiteSpace(request.Domain)
                || string.IsNullOrWhiteSpace(request.EntityKind)
                || string.IsNullOrWhiteSpace(request.EntityId) =>
                throw new ArgumentException(
                    "domain, entityKind, and entityId are required for scope=entity.",
                    nameof(request)),
            "entity" => await _invalidator.InvalidateEntityAsync(
                    request.Domain,
                    request.EntityKind,
                    request.EntityId,
                    cancellationToken)
                .ConfigureAwait(false),
            "entitykind" when string.IsNullOrWhiteSpace(request.Domain)
                || string.IsNullOrWhiteSpace(request.EntityKind) =>
                throw new ArgumentException(
                    "domain and entityKind are required for scope=entityKind.",
                    nameof(request)),
            "entitykind" => await _invalidator.InvalidateEntityKindAsync(
                    request.Domain,
                    request.EntityKind,
                    cancellationToken)
                .ConfigureAwait(false),
            "tags" when request.Tags is null || request.Tags.Length == 0 =>
                throw new ArgumentException("tags are required for scope=tags.", nameof(request)),
            "tags" => await _invalidator.InvalidateTagsAsync(request.Tags, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentException(
                "scope must be domain, entity, entityKind, or tags.",
                nameof(request))
        };
    }

    public async Task<AdminDomainMutationResultDto> SetVersionAsync(
        string domain,
        AdminVersionRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        string normalizedDomain = DomainName.Normalize(domain);
        string? requested = request?.Version;
        string version = string.IsNullOrWhiteSpace(requested)
            ? "rt-" + _time.GetUtcNow().UtcTicks.ToString("x")
            : requested.Trim();

        _overrides.SetVersion(normalizedDomain, version);

        for (int i = 0; i < _versionChangeObservers.Length; i++)
        {
            IDomainVersionChangeObserver observer = _versionChangeObservers[i];
            try
            {
                await observer.OnDomainVersionChangedAsync(normalizedDomain, version, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "IDomainVersionChangeObserver.OnDomainVersionChangedAsync failed ({Observer})",
                    observer.GetType().Name);
            }
        }

        ClusterPublishResult? clusterPublish = null;
        if (request?.Distribute == true && _bus.IsEnabled)
        {
            clusterPublish = await PublishMutationAsync(
                    _commands.CreateVersionBump(normalizedDomain, version),
                    nameof(VersionBumpCommand),
                    normalizedDomain,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new AdminDomainMutationResultDto
        {
            Domain = normalizedDomain,
            Effective = _domainConfig.GetDomainConfig(normalizedDomain),
            ClusterPublish = clusterPublish
        };
    }

    public async Task<AdminDomainMutationResultDto> PatchSettingsAsync(
        string domain,
        AdminSettingsPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settings is null || request.Settings.Count == 0)
            throw new ArgumentException("settings must contain at least one entry.", nameof(request));

        string normalizedDomain = DomainName.Normalize(domain);
        DomainSettingsPatchApplicator.Apply(
            normalizedDomain,
            request.Settings,
            _overrides,
            _settingsContributors);

        ClusterPublishResult? clusterPublish = null;
        if (request.Distribute && _bus.IsEnabled)
        {
            clusterPublish = await PublishMutationAsync(
                    _commands.CreateSettingsPatch(normalizedDomain, request.Settings),
                    nameof(SettingsPatchCommand),
                    normalizedDomain,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new AdminDomainMutationResultDto
        {
            Domain = normalizedDomain,
            Effective = _domainConfig.GetDomainConfig(normalizedDomain),
            ClusterPublish = clusterPublish
        };
    }

    private async Task<ClusterPublishResult> PublishMutationAsync(
        ClusterCommand command,
        string metricName,
        string domain,
        CancellationToken cancellationToken)
    {
        try
        {
            ClusterPublishResult published = await _bus.PublishAsync(command, cancellationToken)
                .ConfigureAwait(false);
            CacheOrchestratorMetrics.RecordClusterPublished(metricName);
            return published;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            CacheOrchestratorMetrics.RecordClusterPublishFailure("exception");
            _logger.LogWarning(ex, "Cluster publish failed for {Command} on domain {Domain}", metricName, domain);
            return new ClusterPublishResult(
            [
                new ClusterPeerPublishOutcome
                {
                    PeerId = "(bus)",
                    Succeeded = false,
                    Error = ex.Message
                }
            ]);
        }
    }

    private AdminDomainCountersDto BuildDomainRow(
        string name,
        string instanceId,
        AdminDomainConfigDto domainConfig,
        AdminDomainCountersDto? counters,
        List<AdminEndpointCountersDto> epList)
    {
        long ocH = counters?.OutputCacheHits ?? 0;
        long ocM = counters?.OutputCacheMisses ?? 0;
        long ocB = counters?.OutputCacheBypass ?? 0;
        long outputCacheOff = counters?.OutputCacheOff ?? 0;
        long fcH = counters?.DataCacheHits ?? 0;
        long fcM = counters?.DataCacheMisses ?? 0;
        long fcS = counters?.DataCacheStale ?? 0;
        long fcB = counters?.DataCacheBypass ?? 0;
        long runs = counters?.FactoryRuns ?? 0;
        long fails = counters?.FactoryFailures ?? 0;
        double? durationSum = counters?.FactoryDurationSumMs;
        long durationCount = counters?.FactoryDurationCount ?? 0;
        long? sizeSum = counters?.FactoryResultSizeSumBytes;
        long sizeCount = counters?.FactoryResultSizeCount ?? 0;

        long domainRequests = AdminCounterMath.Requests(ocH, ocM, ocB, fcH, fcM, fcS, fcB, outputCacheOff, runs);

        // If domain counters empty but endpoints have traffic, rebuild from endpoint sums.
        if (domainRequests == 0 && epList.Count > 0)
        {
            ocH = ocM = ocB = outputCacheOff = fcH = fcM = fcS = fcB = runs = fails = 0;
            durationSum = null;
            durationCount = 0;
            sizeSum = null;
            sizeCount = 0;
            double sumMs = 0;
            long sumCount = 0;
            long sumSize = 0;
            long sumSizeCount = 0;
            foreach (AdminEndpointCountersDto e in epList)
            {
                ocH += e.OutputCacheHits;
                ocM += e.OutputCacheMisses;
                ocB += e.OutputCacheBypass;
                outputCacheOff += e.OutputCacheOff;
                fcH += e.DataCacheHits;
                fcM += e.DataCacheMisses;
                fcS += e.DataCacheStale;
                fcB += e.DataCacheBypass;
                runs += e.FactoryRuns;
                fails += e.FactoryFailures;
                if (e.FactoryDurationCount > 0 && e.FactoryDurationSumMs is double ms)
                {
                    sumMs += ms;
                    sumCount += e.FactoryDurationCount;
                }

                if (e.FactoryResultSizeCount > 0 && e.FactoryResultSizeSumBytes is long sz)
                {
                    sumSize += sz;
                    sumSizeCount += e.FactoryResultSizeCount;
                }
            }

            if (sumCount > 0)
            {
                durationSum = sumMs;
                durationCount = sumCount;
            }

            if (sumSizeCount > 0)
            {
                sizeSum = sumSize;
                sizeCount = sumSizeCount;
            }
        }

        return new AdminDomainCountersDto
        {
            Name = name,
            InstanceId = instanceId,
            Version = domainConfig.Version,
            VersionIsRuntimeOverride = domainConfig.VersionIsRuntimeOverride,
            SchedulePhase = domainConfig.SchedulePhase,
            LastInvalidationUtc = counters?.LastInvalidationUtc,
            Invalidations = counters?.Invalidations ?? 0,
            OutputCacheHits = ocH,
            OutputCacheMisses = ocM,
            OutputCacheBypass = ocB,
            OutputCacheOff = outputCacheOff,
            DataCacheHits = fcH,
            DataCacheMisses = fcM,
            DataCacheStale = fcS,
            DataCacheBypass = fcB,
            FactoryRuns = runs,
            FactoryFailures = fails,
            FactoryDurationSumMs = durationSum,
            FactoryDurationCount = durationCount,
            FactoryResultSizeSumBytes = sizeSum,
            FactoryResultSizeCount = sizeCount,
            Endpoints = epList
        };
    }

    private static AdminEndpointCountersDto CloneEndpoint(
        AdminEndpointCountersDto ep,
        string instanceId,
        string? domain) =>
        new()
        {
            Route = ep.Route,
            InstanceId = instanceId,
            ConfiguredDomain = domain,
            OutputCacheHits = ep.OutputCacheHits,
            OutputCacheMisses = ep.OutputCacheMisses,
            OutputCacheBypass = ep.OutputCacheBypass,
            OutputCacheOff = ep.OutputCacheOff,
            DataCacheHits = ep.DataCacheHits,
            DataCacheMisses = ep.DataCacheMisses,
            DataCacheStale = ep.DataCacheStale,
            DataCacheBypass = ep.DataCacheBypass,
            FactoryRuns = ep.FactoryRuns,
            FactoryFailures = ep.FactoryFailures,
            FactoryDurationSumMs = ep.FactoryDurationSumMs,
            FactoryDurationCount = ep.FactoryDurationCount,
            FactoryResultSizeSumBytes = ep.FactoryResultSizeSumBytes,
            FactoryResultSizeCount = ep.FactoryResultSizeCount
        };

    private static AdminEndpointCountersDto EmptyEndpoint(string route, string domain, string instanceId) =>
        new()
        {
            Route = route,
            InstanceId = instanceId,
            ConfiguredDomain = domain
        };

    private static long RequestDenominator(AdminDomainCountersDto d) =>
        AdminCounterMath.Requests(
            d.OutputCacheHits, d.OutputCacheMisses, d.OutputCacheBypass,
            d.DataCacheHits, d.DataCacheMisses, d.DataCacheStale, d.DataCacheBypass,
            d.OutputCacheOff, d.FactoryRuns);

    private static long RequestDenominator(AdminEndpointCountersDto e) =>
        AdminCounterMath.Requests(
            e.OutputCacheHits, e.OutputCacheMisses, e.OutputCacheBypass,
            e.DataCacheHits, e.DataCacheMisses, e.DataCacheStale, e.DataCacheBypass,
            e.OutputCacheOff, e.FactoryRuns);
}
