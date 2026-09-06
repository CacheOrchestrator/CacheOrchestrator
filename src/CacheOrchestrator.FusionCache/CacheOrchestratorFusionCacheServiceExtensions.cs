using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.FusionCache;
using CacheOrchestrator.FusionCache.Backends;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.DependencyInjection;

/// <summary>
/// DI registration for the FusionCache <see cref="IDataCacheProvider"/> and named Fusion instances.
/// </summary>
public static class CacheOrchestratorFusionCacheServiceExtensions
{
    /// <summary>
    /// Registers Fusion domain settings, <see cref="FusionDataCacheProvider"/> as <see cref="IDataCacheProvider"/>,
    /// and every named FusionCache instance under <c>DataCacheInstances</c> (L2 via registered backends).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Application configuration. When null, named instances are not registered (provider + settings only);
    /// prefer passing configuration in hosts.
    /// </param>
    /// <param name="configSection">Root configuration section (default <c>Cache</c>).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCacheOrchestratorFusionCache(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        string configSection = "Cache")
    {
        ArgumentNullException.ThrowIfNull(services);

        DomainSettingCatalog.RegisterSection(
            typeof(DomainFusionCacheSettings),
            idPrefix: "fusionCache",
            propertyPrefix: "FusionCache");

        string section = string.IsNullOrWhiteSpace(configSection) ? "Cache" : configSection;

        services.TryAddSingleton<IFusionDomainRuntimeOverrideStore, FusionDomainRuntimeOverrideStore>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDomainSettingsPatchContributor, FusionDomainSettingsPatchContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDomainSettingValueProvider, FusionDomainSettingValueProvider>());

        if (configuration is not null)
            services.TryAddSingleton(configuration);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(FusionCacheValidatorMarker)))
        {
            services.AddSingleton<FusionCacheValidatorMarker>();
            services.AddSingleton<IValidateOptions<CacheOrchestratorOptions>>(sp =>
                new FusionCacheConfigurationValidator(
                    configuration ?? sp.GetService<IConfiguration>(),
                    section));
        }

        // Prefer the IConfiguration instance passed to this method. WebApplication.CreateBuilder
        // already registers host IConfiguration; TryAddSingleton above would not replace it, and
        // reading FusionCache:* from the host config would miss test/lab overlays.
        IConfiguration? fusionConfiguration = configuration;
        services.TryAddSingleton<IFusionDomainSettingsProvider>(sp =>
            new FusionDomainSettingsProvider(
                fusionConfiguration ?? sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IOptionsMonitor<CacheOrchestratorOptions>>(),
                sp.GetService<IFusionDomainRuntimeOverrideStore>(),
                section));
        // Replace AspNetCore's NullDataCacheProvider (TryAdd would leave Null in place).
        services.RemoveAll<IDataCacheProvider>();
        services.AddSingleton<IDataCacheProvider, FusionDataCacheProvider>();

        var registry = FusionCacheBackendRegistrarRegistry.GetOrCreate(services);

        if (configuration is not null)
            RegisterNamedFusionInstances(services, configuration, section, registry);

        return services;
    }

    /// <summary>
    /// Registers a custom Fusion L2 / backplane backend registrar.
    /// </summary>
    public static IServiceCollection AddFusionCacheBackend(
        this IServiceCollection services,
        IFusionCacheBackendRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registrar);
        FusionCacheBackendRegistrarRegistry.GetOrCreate(services).Add(registrar);
        return services;
    }

    private static void RegisterNamedFusionInstances(
        IServiceCollection services,
        IConfiguration configuration,
        string configSection,
        FusionCacheBackendRegistrarRegistry registry)
    {
        CacheOrchestratorOptions rootOpts = new();
        configuration.GetSection(configSection).Bind(rootOpts);

        // Bind once when AspNet host was not used (worker / library). AspNetCore already binds;
        // a second Bind would append Cluster:Bus:Static:Instances.
        CacheOrchestratorOptionsBinding.EnsureBound(services, configuration, configSection);

        services.AddMemoryCache();

        foreach ((string? instanceName, CacheOrchestratorOptions.DataCacheInstanceOptions? instanceOptions) in rootOpts.DataCacheInstances)
        {
            if (string.IsNullOrWhiteSpace(instanceName) || instanceOptions is null)
                continue;

            IFusionCacheBackendRegistrar fusionRegistrar = registry.Resolve(instanceOptions.Provider);
            RegisterFusionCacheInstance(
                services,
                configuration,
                rootOpts,
                configSection,
                instanceName,
                instanceOptions,
                fusionRegistrar);

        }
    }

    private static void RegisterFusionCacheInstance(
        IServiceCollection services,
        IConfiguration configuration,
        CacheOrchestratorOptions rootOpts,
        string configSection,
        string instanceName,
        CacheOrchestratorOptions.DataCacheInstanceOptions instanceOpts,
        IFusionCacheBackendRegistrar registrar)
    {
        DistributedResilienceOptions resilience = rootOpts.GetEffectiveDistributedResilience();
        bool isDistributed = !string.Equals(instanceOpts.Provider, "InMemory", StringComparison.OrdinalIgnoreCase);
        // Effective Data Cache namespace (not the Fusion cache name). Trailing ':' matches FusionCache docs;
        // WithCacheKeyPrefix(string) does not append the separator itself.
        string dcNamespace = instanceOpts.GetNamespace(instanceName, rootOpts);

        IFusionCacheBuilder fusionBuilder = services
            .AddFusionCache(instanceName)
            .WithCacheKeyPrefix(dcNamespace + ":")
            .WithOptions(o =>
            {
                if (isDistributed)
                {
                    o.DistributedCacheCircuitBreakerDuration =
                        TimeSpan.FromSeconds(Math.Max(0, resilience.CircuitBreakerSeconds));
                    o.DefaultEntryOptions.DistributedCacheSoftTimeout =
                        TimeSpan.FromSeconds(Math.Max(0, resilience.SoftTimeoutSeconds));
                    o.DefaultEntryOptions.DistributedCacheHardTimeout =
                        TimeSpan.FromSeconds(Math.Max(0, resilience.HardTimeoutSeconds));
                    o.DefaultEntryOptions.AllowBackgroundDistributedCacheOperations = true;
                }
            })
            .WithSystemTextJsonSerializer()
            .TryWithRegisteredMemoryCache();

        FusionCacheRegistrationContext context = new(
            services,
            configuration,
            rootOpts,
            configSection,
            instanceName,
            instanceOpts,
            registrar.Name,
            fusionBuilder,
            resilience);

        registrar.RegisterFusionCache(context);
    }
}
