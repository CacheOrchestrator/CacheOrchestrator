using CacheOrchestrator.Admin;
using CacheOrchestrator.AdminConsole.Models;
using CacheOrchestrator.AdminConsole.Options;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.AdminConsole.Services;

/// <summary>
/// Orchestrates parallel fan-out to Admin APIs and aggregates results.
/// </summary>
public sealed class AdminFanOutService
{
    private readonly IAdminApiClient _client;
    private readonly AdminConsoleOptions _options;
    private readonly TimeProvider _time;
    private readonly InstanceReachabilityCache _reachability;

    public AdminFanOutService(
        IAdminApiClient client,
        IOptions<AdminConsoleOptions> options,
        InstanceReachabilityCache reachability,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reachability);
        _client = client;
        _options = options.Value;
        _reachability = reachability;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Configured instances (copy).</summary>
    public IReadOnlyList<AdminInstanceOptions> GetConfiguredInstances() =>
        _options.Instances
            .Where(i => !string.IsNullOrWhiteSpace(i.Id) && !string.IsNullOrWhiteSpace(i.Url))
            .Select(i => new AdminInstanceOptions { Id = i.Id.Trim(), Url = i.Url.TrimEnd('/') })
            .ToArray();

    public async Task<IReadOnlyList<InstanceStatusDto>> GetInstancesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminInstanceOptions> instances = GetConfiguredInstances();
        List<InstanceCallOutcome<AdminHealthDto>> outcomes =
            await FanOutAsync(
                    instances,
                    _client.GetHealthAsync,
                    cancellationToken,
                    skipKnownDown: true)
                .ConfigureAwait(false);

        return outcomes.Select(o =>
        {
            AdminInstanceOptions cfg = instances.First(i => i.Id == o.InstanceId);
            InstanceHealthStatus status = o.Succeeded
                ? (o.Value?.Healthy == true ? InstanceHealthStatus.Healthy : InstanceHealthStatus.Degraded)
                : InstanceHealthStatus.Down;

            if (o.Succeeded)
            {
                _reachability.RecordHealth(
                    o.InstanceId,
                    status,
                    error: null,
                    o.LatencyMs,
                    o.Value?.InstanceId);
            }
            else if (!IsSkippedDownError(o.Error))
            {
                _reachability.RecordHealth(o.InstanceId, InstanceHealthStatus.Down, o.Error, o.LatencyMs, null);
            }

            return new InstanceStatusDto
            {
                Id = o.InstanceId,
                Url = cfg.Url,
                Status = status,
                ReportedInstanceId = o.Value?.InstanceId,
                Error = o.Error,
                LatencyMs = o.LatencyMs,
                StartedAtUtc = o.Value?.StartedAtUtc,
                UptimeSeconds = o.Succeeded ? o.Value?.UptimeSeconds : null,
                // Process-lifetime health counters are not used for Console traffic (Prometheus window only).
                Requests = null
            };
        }).ToArray();
    }

    /// <summary>
    /// Instance health / connectivity overview only.
    /// Traffic counters and hints come from Prometheus (<c>/api/stats/window</c>) in the SPA.
    /// </summary>
    public async Task<OverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InstanceStatusDto> instances = await GetInstancesAsync(cancellationToken)
            .ConfigureAwait(false);

        int down = instances.Count(i => i.Status == InstanceHealthStatus.Down);
        int degraded = instances.Count(i => i.Status == InstanceHealthStatus.Degraded);
        List<string> alerts = [];
        if (down > 0)
            alerts.Add($"{down} instance(s) down.");
        if (degraded > 0)
            alerts.Add($"{degraded} instance(s) degraded.");

        return new OverviewDto
        {
            CollectedAtUtc = _time.GetUtcNow(),
            Instances = instances,
            HealthyCount = instances.Count(i => i.Status == InstanceHealthStatus.Healthy),
            DegradedCount = degraded,
            DownCount = down,
            TotalRequests = 0,
            TotalInvalidations = 0,
            Pipeline = new AdminPipelineDto(),
            OutputCacheHitShare = null,
            FactoryShare = null,
            Alerts = alerts,
            TopDomains = [],
            TopEndpoints = [],
            DomainCount = 0,
            EndpointCount = 0,
            HintSummary = new AdminHintSummaryDto(),
            TopHints = [],
            Impact = null,
            StatsWindow = "metrics-store",
            ImpactRecent = null,
            RecentWindowLabel = null
        };
    }

    public async Task<FanOutResultDto<IReadOnlyList<AdminDomainConfigDto>>> GetDomainsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminInstanceOptions> instances = GetConfiguredInstances();
        List<InstanceCallOutcome<IReadOnlyList<AdminDomainConfigDto>>> outcomes =
            await FanOutAsync(
                    instances,
                    _client.GetDomainsAsync,
                    cancellationToken,
                    skipKnownDown: true)
                .ConfigureAwait(false);
        RecordDataOutcomes(outcomes);

        // Prefer first successful snapshot set (config is expected to match across instances; overlays may differ).
        IReadOnlyList<AdminDomainConfigDto>? data = outcomes
            .FirstOrDefault(o => o.Succeeded && o.Value is not null)?.Value;

        // When multiple instances return domains, merge by name (last write wins for display; callers can drill per instance).
        if (outcomes.Count(o => o.Succeeded && o.Value is not null) > 1)
        {
            Dictionary<string, AdminDomainConfigDto> map = new(StringComparer.Ordinal);
            foreach (InstanceCallOutcome<IReadOnlyList<AdminDomainConfigDto>> o in outcomes)
            {
                if (o.Value is null)
                    continue;
                foreach (AdminDomainConfigDto d in o.Value)
                    map[d.Name] = d;
            }

            data = map.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
        }

        return new FanOutResultDto<IReadOnlyList<AdminDomainConfigDto>>
        {
            Data = data ?? [],
            Results = outcomes.Select(o => o.ToResultDto()).ToArray()
        };
    }

    /// <summary>
    /// Probes <c>/cluster/info</c> on configured instances and recommends fan-out vs bus-distribute.
    /// </summary>
    public async Task<ClusterDistributionCapabilityDto> GetDistributionCapabilityAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminInstanceOptions> all = GetConfiguredInstances();
        List<InstanceCallOutcome<AdminApiClusterInfoDto>> outcomes =
            await FanOutAsync(
                    all,
                    _client.GetClusterInfoAsync,
                    cancellationToken,
                    skipKnownDown: true)
                .ConfigureAwait(false);
        // Cluster info may be missing when the bus is off — do not mark instances Down.
        // A successful probe still refreshes reachability as Up.
        foreach (InstanceCallOutcome<AdminApiClusterInfoDto> o in outcomes)
        {
            if (o.Succeeded && !IsSkippedDownError(o.Error))
                _reachability.RecordSuccess(o.InstanceId, o.Value?.InstanceId, o.LatencyMs);
        }

        var probes = outcomes.Select(o =>
        {
            bool busOn = o.Succeeded
                && o.Value is { BusEnabled: true }
                && !string.Equals(o.Value.Membership, "Null", StringComparison.OrdinalIgnoreCase);
            return new InstanceClusterProbeDto
            {
                Id = o.InstanceId,
                Succeeded = o.Succeeded,
                BusEnabled = busOn,
                Membership = o.Value?.Membership,
                PeerCount = o.Succeeded ? o.Value?.PeerCount : null,
                Error = o.Error
            };
        }).ToList();

        InstanceClusterProbeDto? preferred = probes.FirstOrDefault(p => p.BusEnabled);
        bool busAvailable = preferred is not null;

        return new ClusterDistributionCapabilityDto
        {
            BusAvailable = busAvailable,
            PreferredBusOriginId = preferred?.Id,
            RecommendedMode = busAvailable ? DistributionModes.BusDistribute : DistributionModes.FanOut,
            Summary = busAvailable
                ? $"Cluster bus available — prefer single origin ({preferred!.Id}) with distribute:true (peers apply via bus)."
                : "No cluster bus detected — Admin Console App will HTTP fan-out to each target with distribute:false.",
            Instances = probes
        };
    }

    public async Task<FanOutResultDto<object?>> InvalidateAsync(
        AdminConsoleInvalidateRequest request,
        CancellationToken cancellationToken)
    {
        AdminConsoleWriteValidators.Validate(request);
        WriteDistributionPlan plan = await PlanWriteDistributionAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminInvalidateRequest body = new()
        {
            Scope = request.Scope,
            Domain = request.Domain,
            EntityKind = request.EntityKind,
            EntityId = request.EntityId,
            Tags = request.Tags,
            Distribute = plan.Distribute
        };

        List<InstanceCallOutcome<CacheInvalidationResult>> outcomes =
            await FanOutAsync(
                    plan.Targets,
                    (inst, ct) => _client.InvalidateAsync(inst, body, ct),
                    cancellationToken,
                    skipKnownDown: true)
                .ConfigureAwait(false);
        RecordDataOutcomes(outcomes);

        return new FanOutResultDto<object?>
        {
            Data = null,
            Results = ExpandWriteResults(outcomes),
            DistributionMode = plan.Mode,
            DistributionSummary = plan.Summary,
            BusOriginInstanceId = plan.BusOriginInstanceId,
            Distribute = plan.Distribute
        }.WithWriteOutcome();
    }

    public async Task<FanOutResultDto<object?>> SetVersionAsync(
        string domain,
        AdminConsoleVersionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        AdminConsoleWriteValidators.Validate(request);
        WriteDistributionPlan plan = await PlanWriteDistributionAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminVersionRequest body = new()
        {
            Version = request.Version,
            Distribute = plan.Distribute
        };

        List<InstanceCallOutcome<AdminDomainMutationResultDto>> outcomes =
            await FanOutAsync(
                    plan.Targets,
                    (inst, ct) => _client.SetVersionAsync(inst, domain, body, ct),
                    cancellationToken,
                    skipKnownDown: true)
                .ConfigureAwait(false);
        RecordDataOutcomes(outcomes);

        return new FanOutResultDto<object?>
        {
            Data = outcomes.FirstOrDefault(o => o.Succeeded)?.Value,
            Results = ExpandWriteResults(outcomes),
            DistributionMode = plan.Mode,
            DistributionSummary = plan.Summary,
            BusOriginInstanceId = plan.BusOriginInstanceId,
            Distribute = plan.Distribute
        }.WithWriteOutcome();
    }

    /// <summary>GET domain-settings catalog from the first healthy instance.</summary>
    public async Task<AdminDomainSettingsCatalogDto> GetDomainSettingsCatalogAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminInstanceOptions> targets = GetConfiguredInstances();
        foreach (AdminInstanceOptions inst in targets)
        {
            InstanceCallOutcome<AdminDomainSettingsCatalogDto> outcome =
                await _client.GetDomainSettingsCatalogAsync(inst, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded && outcome.Value is not null)
                return outcome.Value;
        }

        // Fallback: catalog is assembly-local and identical across instances.
        return new AdminDomainSettingsCatalogDto
        {
            Settings = DomainSettingCatalog.GetEntries(),
        };
    }

    public async Task<FanOutResultDto<object?>> PatchSettingsAsync(
        string domain,
        AdminConsoleSettingsPatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        AdminConsoleWriteValidators.Validate(request);
        WriteDistributionPlan plan = await PlanWriteDistributionAsync(cancellationToken)
            .ConfigureAwait(false);

        AdminSettingsPatchRequest body = new()
        {
            Settings = request.Settings,
            Distribute = plan.Distribute,
            ApplyImmediately = request.ApplyImmediately,
        };

        List<InstanceCallOutcome<AdminDomainMutationResultDto>> outcomes =
            await FanOutAsync(
                    plan.Targets,
                    (inst, ct) => _client.PatchSettingsAsync(inst, domain, body, ct),
                    cancellationToken,
                    skipKnownDown: true)
                .ConfigureAwait(false);
        RecordDataOutcomes(outcomes);

        return new FanOutResultDto<object?>
        {
            Data = outcomes.FirstOrDefault(o => o.Succeeded)?.Value,
            Results = ExpandWriteResults(outcomes),
            DistributionMode = plan.Mode,
            DistributionSummary = plan.Summary,
            BusOriginInstanceId = plan.BusOriginInstanceId,
            Distribute = plan.Distribute
        }.WithWriteOutcome();
    }

    /// <summary>
    /// Maps Admin API outcomes to Console results. A 409 cluster-publish incomplete response
    /// (origin applied, peers failed) expands into origin success + per-peer failure rows.
    /// </summary>
    /// <summary>Expands Admin API outcomes (including bus peer failures) into Console result rows.</summary>
    public static IReadOnlyList<InstanceCallResultDto> ExpandWriteResults<T>(
        IEnumerable<InstanceCallOutcome<T>> outcomes)
    {
        List<InstanceCallResultDto> results = [];
        foreach (InstanceCallOutcome<T> o in outcomes)
        {
            if (o.LocalApplied && o.PeerFailures.Count > 0)
            {
                results.Add(new InstanceCallResultDto
                {
                    InstanceId = o.InstanceId,
                    Succeeded = true,
                    StatusCode = o.StatusCode,
                    Error = "Applied locally; peer publish incomplete.",
                    LatencyMs = o.LatencyMs,
                });

                foreach (AdminApiPeerFailureDto peer in o.PeerFailures)
                {
                    string peerId = peer.PeerId!.Trim();
                    results.Add(new InstanceCallResultDto
                    {
                        InstanceId = peerId,
                        Succeeded = false,
                        StatusCode = null,
                        Error = string.IsNullOrWhiteSpace(peer.Error)
                            ? $"Peer publish failed (via bus from '{o.InstanceId}')."
                            : peer.Error.Trim(),
                        LatencyMs = o.LatencyMs,
                    });
                }

                continue;
            }

            results.Add(o.ToResultDto());
        }

        return results;
    }

    /// <summary>
    /// Cluster-wide write plan: bus-enabled preferred origin + distribute, else fan-out to all with distribute:false.
    /// </summary>
    private async Task<WriteDistributionPlan> PlanWriteDistributionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AdminInstanceOptions> all = GetConfiguredInstances();
        ClusterDistributionCapabilityDto capability =
            await GetDistributionCapabilityAsync(cancellationToken).ConfigureAwait(false);

        if (capability.BusAvailable
            && !string.IsNullOrWhiteSpace(capability.PreferredBusOriginId))
        {
            AdminInstanceOptions? origin = all.FirstOrDefault(t =>
                string.Equals(t.Id, capability.PreferredBusOriginId, StringComparison.OrdinalIgnoreCase));

            // Preferred id can theoretically diverge from configured instances; never throw here (would 500 the Console).
            if (origin is not null)
            {
                return new WriteDistributionPlan(
                    Targets: [origin],
                    Distribute: true,
                    Mode: DistributionModes.BusDistribute,
                    BusOriginInstanceId: origin.Id,
                    Summary:
                        $"bus-distribute via origin '{origin.Id}' (Admin Console App → 1 HTTP call with distribute:true; peers apply via cluster bus).");
            }
        }

        string ids = string.Join(", ", all.Select(t => t.Id));
        return new WriteDistributionPlan(
            Targets: all,
            Distribute: false,
            Mode: DistributionModes.FanOut,
            BusOriginInstanceId: null,
            Summary:
                $"fan-out to {all.Count} instance(s) [{ids}] with distribute:false (each process applies locally).");
    }

    private sealed record WriteDistributionPlan(
        IReadOnlyList<AdminInstanceOptions> Targets,
        bool Distribute,
        string Mode,
        string? BusOriginInstanceId,
        string Summary);

    /// <summary>
    /// Parallel fan-out. When <paramref name="skipKnownDown"/> is true, instances marked Down
    /// within the re-probe window return a failed outcome immediately (no HTTP / no timeout wait).
    /// </summary>
    private async Task<List<InstanceCallOutcome<T>>> FanOutAsync<T>(
        IReadOnlyList<AdminInstanceOptions> instances,
        Func<AdminInstanceOptions, CancellationToken, Task<InstanceCallOutcome<T>>> call,
        CancellationToken cancellationToken,
        bool skipKnownDown = true)
    {
        if (instances.Count == 0)
            return [];

        int parallelism = Math.Clamp(_options.Parallelism, 1, 64);
        using SemaphoreSlim gate = new(parallelism, parallelism);
        List<Task<InstanceCallOutcome<T>>> tasks = new(instances.Count);

        foreach (AdminInstanceOptions instance in instances)
        {
            if (skipKnownDown && _reachability.ShouldSkipUnreachable(instance.Id))
            {
                CachedInstanceHealth? cached = _reachability.TryGetSkippedDown(instance.Id);
                tasks.Add(Task.FromResult(new InstanceCallOutcome<T>
                {
                    InstanceId = instance.Id,
                    Succeeded = false,
                    Error = cached?.Error is { Length: > 0 } err
                        ? $"Skipped (instance down): {err}"
                        : "Skipped (instance down; will re-probe shortly).",
                    LatencyMs = 0
                }));
                continue;
            }

            tasks.Add(RunOneAsync(instance, call, gate, cancellationToken));
        }

        InstanceCallOutcome<T>[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    private static async Task<InstanceCallOutcome<T>> RunOneAsync<T>(
        AdminInstanceOptions instance,
        Func<AdminInstanceOptions, CancellationToken, Task<InstanceCallOutcome<T>>> call,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(instance, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void RecordDataOutcomes<T>(IEnumerable<InstanceCallOutcome<T>> outcomes)
    {
        foreach (InstanceCallOutcome<T> o in outcomes)
        {
            if (IsSkippedDownError(o.Error))
                continue;
            // Origin applied locally but peer bus publish failed — origin is still healthy.
            if (o.LocalApplied)
            {
                _reachability.RecordSuccess(o.InstanceId, latencyMs: o.LatencyMs);
                continue;
            }

            if (o.Succeeded)
                _reachability.RecordSuccess(o.InstanceId, latencyMs: o.LatencyMs);
            else
                _reachability.RecordFailure(o.InstanceId, o.Error, o.LatencyMs);
        }
    }

    private static bool IsSkippedDownError(string? error) =>
        error is not null && error.StartsWith("Skipped (instance down", StringComparison.Ordinal);
}
