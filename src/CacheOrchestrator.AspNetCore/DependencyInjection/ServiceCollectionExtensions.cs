using CacheOrchestrator.Admin;
using CacheOrchestrator.Backends;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.Identity;
using CacheOrchestrator.Orchestration;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.DependencyInjection;

/// <summary>
/// DI registration entry points for CacheOrchestrator ASP.NET Core host services.
/// </summary>
/// <remarks>
/// Registers Output Cache, Core orchestration, and <see cref="IDomainDataCache"/>.
/// Does <strong>not</strong> register a data-cache engine — call
/// <c>AddCacheOrchestratorFusionCache</c> or <c>AddCacheOrchestratorHybridCache</c> (or use the meta package).
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers CacheOrchestrator ASP.NET services, options validation, and Output Cache.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (binds the cache section).</param>
    /// <param name="configure">
    /// Optional builder callback (e.g. <c>o =&gt; o.AddRedisBackend()</c>,
    /// <c>o.ConfigureOutputCache(...)</c>, <c>o.AddOutputCacheBackend(custom)</c>).
    /// </param>
    /// <param name="configSection">Configuration section name. Default: <c>Cache</c>.</param>
    /// <param name="enableMvcConvention">
    /// When <see langword="true"/> (default), registers an MVC convention that automatically applies
    /// <c>[CacheDomain]</c> attributes on controllers as Output Cache policies.
    /// Set to <see langword="false"/> for Minimal API-only applications to avoid pulling in the full
    /// MVC infrastructure via <c>AddControllers()</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// Meta package <c>CacheOrchestrator</c> exposes <c>AddCacheOrchestrator</c> as AspNet + Fusion.
    /// Prefer this method for AspNet-only / Hybrid hosts, then call <c>AddCacheOrchestratorHybridCache</c>.
    /// </remarks>
    public static IServiceCollection AddCacheOrchestratorAspNetCore(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ICacheOrchestratorBuilder>? configure = null,
        string configSection = "Cache",
        bool enableMvcConvention = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterHttpDomainSettingCatalog(services);

        // Backend registrars and options monitors resolve IConfiguration from DI.
        // Host builders usually register it; bare ServiceCollection unit tests do not.
        services.TryAddSingleton(configuration);

        DefaultCacheOrchestratorBuilder builder = new(services, configuration);

        // Built-in InMemory only. Redis: install CacheOrchestrator.Redis and call AddRedisBackend().
        builder.AddOutputCacheBackend(new InMemoryCacheBackendRegistrar());

        // Allow consumers to add/override providers (e.g. o => o.AddRedisBackend())
        configure?.Invoke(builder);

        CacheOrchestratorOptions coreOptions = BindAndValidateOptions(services, configuration, configSection);
        CacheOrchestratorHttpOptions httpOptions = new();
        configuration.GetSection(configSection).Bind(httpOptions);

        CacheOrchestratorCoreServiceCollectionExtensions.AddCoreServices(
            services,
            configuration,
            configSection,
            registerCoreValidator: false);
        services.AddOptions<CacheOrchestratorHttpOptions>()
            .Bind(configuration.GetSection(configSection))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CacheOrchestratorHttpOptions>>(
            new CacheOrchestratorHttpOptionsValidator(builder.GetRegisteredProviderNames())));
        RegisterAspNetCoreServices(services);
        RegisterAdminServices(services, coreOptions.Admin, httpOptions.Admin);

        if (enableMvcConvention)
            RegisterControllerConvention(services);

        // Register Output Cache (single provider)
        IOutputCacheBackendRegistrar outputRegistrar = builder.ResolveRegistrar(httpOptions.OutputCache.Provider);
        RegisterOutputCache(services, configuration, httpOptions.OutputNamespace, configSection, outputRegistrar, builder);

        return services;
    }

    private static CacheOrchestratorOptions BindAndValidateOptions(
        IServiceCollection services,
        IConfiguration configuration,
        string configSection)
    {
        // Single Bind across AspNetCore + Fusion/Hybrid — a second Bind appends list properties
        // (e.g. Cluster:Bus:Static:Instances).
        CacheOrchestratorOptionsBinding.EnsureBound(services, configuration, configSection)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<CacheOrchestratorOptions>>(sp =>
            new CacheOrchestratorOptionsValidator(
                logger: sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    ?.CreateLogger(typeof(CacheOrchestratorOptionsValidator).FullName
                        ?? nameof(CacheOrchestratorOptionsValidator))));

        CacheOrchestratorOptions opts = new();
        configuration.GetSection(configSection).Bind(opts);
        return opts;
    }

    private static void RegisterAspNetCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton<IHttpDomainRuntimeOverrideStore, HttpDomainRuntimeOverrideStore>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDomainSettingsPatchContributor, HttpDomainSettingsPatchContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDomainSettingValueProvider, HttpDomainSettingValueProvider>());
        services.TryAddSingleton<IRequestDomainCacheOptions, RequestDomainCacheOptionsProvider>();
        services.RemoveAll<IAdminDomainConfigProvider>();
        services.AddSingleton<IAdminDomainConfigProvider, HttpAdminDomainConfigProvider>();
        services.TryAddSingleton<IDomainDataCache, DomainDataCacheService>();
        services.RemoveAll<IHttpCacheInvalidationSink>();
        services.AddSingleton<IHttpCacheInvalidationSink, OutputCacheInvalidationSink>();
        services.TryAddSingleton<Vary.CacheVaryMaterializer>(sp =>
            new Vary.CacheVaryMaterializer(sp.GetServices<Vary.ICacheVaryContributor>()));
        services.TryAddSingleton<IDomainKeyGenerator>(sp =>
            new DefaultDomainKeyGenerator(sp.GetRequiredService<Vary.CacheVaryMaterializer>()));
        services.TryAddSingleton<CacheIdentityContractCatalog>(sp =>
            new CacheIdentityContractCatalog(sp.GetServices<ICacheIdentityContract>()));
        services.AddHostedService<CacheIdentityResolutionHostedService>();
    }

    private static void RegisterHttpDomainSettingCatalog(IServiceCollection services)
    {
        DomainSettingCatalog.RegisterSection(services, typeof(DomainHttpCacheSettings), "", "");
        DomainSettingCatalog.RegisterSection(services, typeof(DomainHttpDataCacheSettings), "dataCache", "DataCache");
        DomainSettingCatalog.RegisterSection(services, typeof(DomainOutputCacheSettings), "outputCache", "OutputCache");
        DomainSettingCatalog.RegisterSection(services, typeof(DomainClientCacheSettings), "clientCache", "ClientCache");
    }

    private static void RegisterAdminServices(
        IServiceCollection services,
        CacheOrchestratorOptions.AdminOptions admin,
        HttpAdminOptions httpAdmin)
    {
        if (admin.Enabled || httpAdmin.Enabled)
        {
            services.RemoveAll<IDomainRuntimeOverrideStore>();
            services.RemoveAll<IAdminStatsCollector>();
            services.AddSingleton<IDomainRuntimeOverrideStore, DomainRuntimeOverrideStore>();
            services.AddSingleton<IAdminStatsCollector>(sp =>
            {
                CacheOrchestratorOptions opts = sp.GetRequiredService<IOptions<CacheOrchestratorOptions>>().Value;
                string instanceId = sp.GetRequiredService<IInstanceIdProvider>().InstanceId;
                return new InMemoryAdminStatsCollector(
                    opts.Admin,
                    instanceId,
                    sp.GetService<TimeProvider>());
            });
            services.RemoveAll<IAdminEndpointCatalog>();
            services.AddSingleton<IAdminEndpointCatalog, AdminEndpointCatalog>();
            services.AddSingleton<AdminApiKeyEndpointFilter>();
        }
        else
        {
            services.TryAddSingleton<IAdminStatsCollector>(_ => NoOpAdminStatsCollector.Instance);
            services.TryAddSingleton<IAdminEndpointCatalog>(_ => NullAdminEndpointCatalog.Instance);
        }
    }

    private static void RegisterControllerConvention(IServiceCollection services) =>
        services.AddControllers(options => options.Conventions.Add(new CacheDomainConvention()));

    private static void RegisterOutputCache(
        IServiceCollection services,
        IConfiguration configuration,
        string outputCacheNamespace,
        string configSection,
        IOutputCacheBackendRegistrar registrar,
        DefaultCacheOrchestratorBuilder builder)
    {
        List<Action<OutputCacheOptions>> optionConfigurators = [];

        // Base policy: no cache. Output Cache is opt-in via .CacheOutputWithDomain / [CacheDomain].
        optionConfigurators.Add(o => o.AddBasePolicy(b => b.NoCache()));

        OutputCacheRegistrationContext context = new(
            services,
            configuration,
            outputCacheNamespace,
            configSection,
            registrar.Name,
            optionConfigurators);

        // Backend defaults (e.g. InMemory size limits) then user ConfigureOutputCache callbacks.
        registrar.RegisterOutputCache(context);
        foreach (Action<OutputCacheOptions> user in builder.OutputCacheConfigurators)
            optionConfigurators.Add(user);

        services.AddOutputCache(options =>
        {
            foreach (Action<OutputCacheOptions> configure in optionConfigurators)
                configure(options);
        });

        context.RunStoreRegistrations();
    }
}
