using CacheOrchestrator.Admin;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.DependencyInjection;

/// <summary>
/// DI registration entry point for HTTP-free CacheOrchestrator host services.
/// </summary>
public static class CacheOrchestratorCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers domain options, <see cref="ICacheOrchestrator"/>, invalidation, and cluster contracts.
    /// A host must also register exactly one <see cref="IDataCacheProvider"/>, such as FusionCache or HybridCache.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration containing the cache section.</param>
    /// <param name="configSection">Configuration section name. Default: <c>Cache</c>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCacheOrchestratorCore(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSection = "Cache")
    {
        AddCoreServices(services, configuration, configSection, registerCoreValidator: true);
        return services;
    }

    internal static void AddCoreServices(
        IServiceCollection services,
        IConfiguration configuration,
        string configSection,
        bool registerCoreValidator)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSection);

        services.TryAddSingleton(configuration);
        CacheOrchestratorOptionsBinding.EnsureBound(services, configuration, configSection)
            .ValidateOnStart();
        services.AddOptions<ClusterCommandHandlingOptions>()
            .Bind(configuration.GetSection($"{configSection}:Cluster:Bus"));

        if (registerCoreValidator)
        {
            bool validatorRegistered = services.Any(
                descriptor => descriptor.ServiceType == typeof(CacheOrchestratorCoreValidatorMarker));
            if (!validatorRegistered)
            {
                services.AddSingleton<CacheOrchestratorCoreValidatorMarker>();
                services.AddSingleton<IValidateOptions<CacheOrchestratorOptions>>(sp =>
                    new CacheOrchestratorOptionsValidator(
                        logger: sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                            ?.CreateLogger(typeof(CacheOrchestratorOptionsValidator).FullName
                                ?? nameof(CacheOrchestratorOptionsValidator))));
            }
        }

        DomainSettingCatalog.Register(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDomainRuntimeOverrideStore, DomainRuntimeOverrideStore>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDomainSettingValueProvider, CoreDomainSettingValueProvider>());
        services.TryAddSingleton<IAdminStatsCollector>(_ => NoOpAdminStatsCollector.Instance);
        services.TryAddSingleton<IInstanceIdProvider, DefaultInstanceIdProvider>();
        services.TryAddSingleton<ClusterCommandFactory>();
        services.TryAddSingleton<ClusterCommandDedupeStore>();
        services.TryAddSingleton<IClusterCommandBus>(_ => NullClusterCommandBus.Instance);
        services.TryAddSingleton<IClusterMembership>(_ => NullClusterMembership.Instance);
        services.TryAddSingleton<IClusterCommandHandler, DefaultClusterCommandHandler>();
        services.TryAddSingleton<IDataCacheProvider>(_ => NullDataCacheProvider.Instance);
        services.TryAddSingleton<IDomainCacheOptionsProvider, DomainCacheOptionsProvider>();
        services.TryAddSingleton<IAdminEndpointCatalog>(_ => NullAdminEndpointCatalog.Instance);
        services.TryAddSingleton<IAdminDomainConfigProvider, CoreAdminDomainConfigProvider>();
        services.TryAddSingleton<ICacheOrchestrator, CacheOrchestratorService>();
        services.TryAddSingleton<IHttpCacheInvalidationSink>(_ => NullHttpCacheInvalidationSink.Instance);
        services.TryAddSingleton<ICacheOrchestratorInvalidator, CacheOrchestratorInvalidator>();
        services.TryAddSingleton<DomainSettingsInvalidationCoordinator>();
        services.TryAddSingleton<ICacheOrchestratorManagement, CacheOrchestratorManagement>();
    }
}

internal sealed class CacheOrchestratorCoreValidatorMarker;
