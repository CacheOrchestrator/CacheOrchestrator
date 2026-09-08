using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Diagnostics;

/// <summary>
/// Aggregates registered <see cref="ICacheOrchestratorHealthProbe"/> instances into one health check.
/// </summary>
internal sealed class CacheOrchestratorHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options;
    private readonly IEnumerable<ICacheOrchestratorHealthProbe> _probes;
    private readonly IDataCacheProvider _dataCacheProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheOrchestratorHealthCheck"/> class.
    /// </summary>
    public CacheOrchestratorHealthCheck(
        IOptionsMonitor<CacheOrchestratorOptions> options,
        IEnumerable<ICacheOrchestratorHealthProbe> probes,
        IDataCacheProvider dataCacheProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(dataCacheProvider);

        _options = options;
        _probes = probes;
        _dataCacheProvider = dataCacheProvider;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        CacheOrchestratorOptions opts = _options.CurrentValue;
        Dictionary<string, object> data =
            new() { ["data_cache_provider"] = _dataCacheProvider.Name };
        DataCacheProviderCapabilities capabilities = GetCapabilities(_dataCacheProvider);
        data["data_cache_capability:named_instances"] = capabilities.SupportsNamedInstances;
        data["data_cache_capability:fail_safe"] = capabilities.SupportsFailSafe;
        data["data_cache_capability:eager_refresh"] = capabilities.SupportsEagerRefresh;
        data["data_cache_capability:backplane"] = capabilities.SupportsBackplane;
        data["data_cache_capability:batch_invalidation"] = capabilities.SupportsBatchInvalidation;

        foreach ((string? instanceName, CacheOrchestratorOptions.DataCacheInstanceOptions? instanceOpts) in opts.DataCacheInstances)
            data[$"data_cache_instance:{instanceName}"] = instanceOpts.Provider ?? "InMemory";

        List<ICacheOrchestratorHealthProbe> probes = [.. _probes];
        List<string> failures = [];
        if (probes.Count == 0)
        {
            if (failures.Count == 0)
                return HealthCheckResult.Healthy("No cache health probes registered.", data);

            return new HealthCheckResult(
                context.Registration.FailureStatus,
                failures[0],
                data: data);
        }

        foreach (ICacheOrchestratorHealthProbe probe in probes)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(context.Registration.Timeout);
                await probe.ProbeAsync(cts.Token).ConfigureAwait(false);
                data[$"probe:{probe.Name}"] = "ok";
            }
            catch (Exception ex)
            {
                data[$"probe:{probe.Name}"] = "fail";
                data[$"probe:{probe.Name}:error"] = ex.Message;
                failures.Add($"{probe.Name}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
            return HealthCheckResult.Healthy("All cache probes succeeded.", data);

        string description = "One or more cache probes failed: " + string.Join("; ", failures);
        return new HealthCheckResult(
            context.Registration.FailureStatus,
            description,
            data: data);
    }

    private static DataCacheProviderCapabilities GetCapabilities(IDataCacheProvider provider) =>
        provider is IDataCacheProviderCapabilities source
            ? source.Capabilities
            : new DataCacheProviderCapabilities();
}
