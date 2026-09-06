using CacheOrchestrator.Edge.Providers;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Edge.Configuration;

internal sealed class CacheOrchestratorEdgeOptionsValidator : IValidateOptions<CacheOrchestratorEdgeOptions>
{
    private readonly EdgeProviderCatalog _providers;

    public CacheOrchestratorEdgeOptionsValidator(EdgeProviderCatalog providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    public ValidateOptionsResult Validate(string? name, CacheOrchestratorEdgeOptions options)
    {
        List<string> failures = [];
        if (options.EdgeQueue.Capacity <= 0)
            failures.Add("Cache:EdgeQueue:Capacity must be greater than zero.");
        if (options.EdgeQueue.MaxAttempts <= 0)
            failures.Add("Cache:EdgeQueue:MaxAttempts must be greater than zero.");
        if (options.EdgeQueue.FlushIntervalSeconds < 0)
            failures.Add("Cache:EdgeQueue:FlushIntervalSeconds cannot be negative.");
        if (options.EdgeQueue.RetryBaseDelaySeconds < 0)
            failures.Add("Cache:EdgeQueue:RetryBaseDelaySeconds cannot be negative.");

        foreach ((string instanceName, EdgeInstanceOptions instance) in options.EdgeInstances)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                failures.Add("Cache:EdgeInstances contains an empty instance name.");
            }
            if (string.IsNullOrWhiteSpace(instance.Provider))
            {
                failures.Add($"Cache:EdgeInstances:{instanceName}:Provider is required.");
                continue;
            }
            if (!_providers.Contains(instance.Provider))
            {
                failures.Add($"Cache:EdgeInstances:{instanceName}:Provider '{instance.Provider}' is not registered.");
                continue;
            }

            ResolvedEdgeProvider provider = _providers.Resolve(instance.Provider);
            if (!provider.Invalidation.Capabilities.SupportsTagInvalidation)
            {
                failures.Add($"Cache:EdgeInstances:{instanceName}:Provider '{instance.Provider}' does not support tag purge.");
            }
            if (provider.Response.Capabilities.MaxResponseTagBytes <= 0)
            {
                failures.Add($"Edge provider '{instance.Provider}' has an invalid response tag limit.");
            }
            if (provider.Invalidation.Capabilities.MaxInvalidationBatchSize <= 0)
            {
                failures.Add($"Edge provider '{instance.Provider}' has an invalid invalidation batch limit.");
            }
        }

        ValidateDomain("DomainDefaults", options.DomainDefaults.Edge, options, failures);
        foreach ((string domain, EdgeDomainContainer container) in options.Domains)
        {
            ValidateDomain($"Domains:{domain}", Merge(options.DomainDefaults.Edge, container.Edge), options, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateDomain(
        string path,
        DomainEdgeSettings? settings,
        CacheOrchestratorEdgeOptions root,
        List<string> failures)
    {
        if (settings is null)
        {
            return;
        }
        ValidateSeconds(path, nameof(settings.TtlSeconds), settings.TtlSeconds, failures);
        ValidateSeconds(path, nameof(settings.StaleWhileRevalidateSeconds), settings.StaleWhileRevalidateSeconds, failures);
        ValidateSeconds(path, nameof(settings.StaleIfErrorSeconds), settings.StaleIfErrorSeconds, failures);
        if (settings.Enabled == true)
        {
            if (string.IsNullOrWhiteSpace(settings.Instance))
            {
                failures.Add($"Cache:{path}:Edge:Instance is required when Edge caching is enabled.");
            }
            else if (!root.EdgeInstances.ContainsKey(settings.Instance))
            {
                failures.Add($"Cache:{path}:Edge:Instance '{settings.Instance}' is not configured.");
            }
            else
            {
                EdgeInstanceOptions instance = root.EdgeInstances[settings.Instance];
                if (_providers.Contains(instance.Provider))
                {
                    EdgeProviderCapabilities capabilities = _providers.Resolve(instance.Provider).Response.Capabilities;
                    if (settings.StaleWhileRevalidateSeconds is not null
                        && !capabilities.SupportsStaleWhileRevalidate)
                    {
                        failures.Add(
                            $"Cache:{path}:Edge:StaleWhileRevalidateSeconds is not supported by provider '{instance.Provider}'.");
                    }
                    if (settings.StaleIfErrorSeconds is not null && !capabilities.SupportsStaleIfError)
                    {
                        failures.Add(
                            $"Cache:{path}:Edge:StaleIfErrorSeconds is not supported by provider '{instance.Provider}'.");
                    }
                }
            }
        }
    }

    private static void ValidateSeconds(string path, string property, int? value, List<string> failures)
    {
        if (value < 0)
        {
            failures.Add($"Cache:{path}:Edge:{property} cannot be negative.");
        }
    }

    private static DomainEdgeSettings? Merge(DomainEdgeSettings? defaults, DomainEdgeSettings? specific)
    {
        if (defaults is null && specific is null)
        {
            return null;
        }
        return new DomainEdgeSettings
        {
            Enabled = specific?.Enabled ?? defaults?.Enabled,
            PurgeOnStartup = specific?.PurgeOnStartup ?? defaults?.PurgeOnStartup,
            Instance = specific?.Instance ?? defaults?.Instance,
            TtlSeconds = specific?.TtlSeconds ?? defaults?.TtlSeconds,
            StaleWhileRevalidateSeconds = specific?.StaleWhileRevalidateSeconds ?? defaults?.StaleWhileRevalidateSeconds,
            StaleIfErrorSeconds = specific?.StaleIfErrorSeconds ?? defaults?.StaleIfErrorSeconds
        };
    }
}
