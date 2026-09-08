using CacheOrchestrator.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.EFCore;

/// <summary>
/// Registers EF Core SaveChanges invalidation with CacheOrchestrator.
/// </summary>
public static class CacheOrchestratorEfCoreServiceExtensions
{
    /// <summary>
    /// Binds <c>{section}:EFCore:Invalidation</c> and registers the SaveChanges and transaction interceptors.
    /// Does not attach the interceptor to any <c>DbContext</c> — call
    /// <see cref="AddCacheOrchestratorInvalidation"/> on the options builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="configure">Optional code maps via <see cref="EfCoreInvalidationOptions.Map{TEntity}"/>.</param>
    /// <param name="configSection">Root section passed to <c>AddCacheOrchestrator</c>. Default: <c>Cache</c>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddCacheOrchestratorEfCoreInvalidation(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<EfCoreInvalidationOptions>? configure = null,
        string configSection = "Cache")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSection);

        OptionsBuilder<EfCoreInvalidationOptions> options = services.AddOptions<EfCoreInvalidationOptions>()
            .Bind(configuration.GetSection(configSection + ":EFCore:Invalidation"));
        if (configure is not null)
            options.PostConfigure(configure);

        services.TryAddSingleton<IEntityCacheMappingResolver, EntityCacheMappingResolver>();
        services.TryAddSingleton<CacheInvalidationTransactionInterceptor>();
        services.TryAddSingleton(sp => new CacheInvalidationSaveChangesInterceptor(
            sp.GetRequiredService<CacheInvalidationTransactionInterceptor>(),
            sp.GetRequiredService<IEntityCacheMappingResolver>(),
            sp.GetRequiredService<IOptionsMonitor<EfCoreInvalidationOptions>>(),
            sp.GetRequiredService<ILogger<CacheInvalidationSaveChangesInterceptor>>()));
        return services;
    }

    /// <summary>
    /// Same as <see cref="AddCacheOrchestratorEfCoreInvalidation(IServiceCollection, IConfiguration, Action{EfCoreInvalidationOptions}?, string)"/>
    /// on the CacheOrchestrator builder (parity with <c>AddRedisBackend</c> / <c>AddHttpClusterBus</c>).
    /// </summary>
    public static TBuilder AddEfCoreInvalidation<TBuilder>(
        this TBuilder builder,
        Action<EfCoreInvalidationOptions>? configure = null,
        string configSection = "Cache")
        where TBuilder : ICacheOrchestratorServiceBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration, configure, configSection);
        return builder;
    }

    /// <summary>
    /// Attaches the CacheOrchestrator SaveChanges and transaction interceptors registered in the service provider.
    /// </summary>
    /// <param name="options">The DbContext options builder.</param>
    /// <param name="services">The application service provider (from <c>AddDbContext</c> factory).</param>
    /// <returns>The same <paramref name="options"/> for chaining.</returns>
    public static DbContextOptionsBuilder AddCacheOrchestratorInvalidation(
        this DbContextOptionsBuilder options,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        options.AddInterceptors(
            services.GetRequiredService<CacheInvalidationSaveChangesInterceptor>(),
            services.GetRequiredService<CacheInvalidationTransactionInterceptor>());
        return options;
    }
}
