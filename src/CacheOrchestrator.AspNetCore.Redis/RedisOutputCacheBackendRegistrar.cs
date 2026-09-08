using CacheOrchestrator.Backends;
using CacheOrchestrator.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace CacheOrchestrator.Redis;

/// <summary>
/// Registers a Redis-backed ASP.NET Core Output Cache store and health probe.
/// </summary>
internal sealed class RedisOutputCacheBackendRegistrar : IOutputCacheBackendRegistrar
{
    private static readonly object ConnectionKey = new();

    /// <inheritdoc />
    public string Name => RedisConfiguration.ProviderName;

    /// <inheritdoc />
    public void RegisterOutputCache(OutputCacheRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RedisConnectionOptions redis = RedisConfiguration.ResolveForOutputCache(
            context.Configuration, context.ConfigSection);

        if (string.IsNullOrWhiteSpace(redis.Configuration))
        {
            throw new InvalidOperationException(
                "Redis configuration is required when OutputCache.Provider is 'Redis'. " +
                $"Set '{context.ConfigSection}:Redis:Configuration' or " +
                $"'{context.ConfigSection}:OutputCache:Redis:Configuration'.");
        }

        ConfigurationOptions configOptions = RedisConfigurationOptionsFactory.Create(redis);

        context.Services.AddSingleton<ICacheOrchestratorHealthProbe>(sp =>
        {
            IConnectionMultiplexer mux = sp.GetRequiredKeyedService<IConnectionMultiplexer>(ConnectionKey);
            return new RedisCacheHealthProbe("redis:output-cache", mux);
        });

        context.RegisterStore(() =>
        {
            context.Services.TryAddKeyedSingleton<IConnectionMultiplexer>(
                ConnectionKey,
                (_, _) => ConnectionMultiplexer.Connect(configOptions));

            context.Services.AddStackExchangeRedisOutputCache(o =>
            {
                o.ConfigurationOptions = configOptions;
                o.InstanceName = context.OutputCacheNamespace;
            });
        });
    }
}
