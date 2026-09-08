using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.FusionCache.Backends;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;

namespace CacheOrchestrator.Redis;

/// <summary>
/// Registers Redis L2 and backplane for a named FusionCache instance.
/// </summary>
internal sealed class RedisFusionCacheBackendRegistrar : IFusionCacheBackendRegistrar
{
    /// <inheritdoc />
    public string Name => RedisConfiguration.ProviderName;

    /// <inheritdoc />
    public void RegisterFusionCache(FusionCacheRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RedisConnectionOptions redis = RedisConfiguration.ResolveForFusionInstance(
            context.Configuration, context.ConfigSection, context.InstanceName);

        if (string.IsNullOrWhiteSpace(redis.Configuration))
        {
            throw new InvalidOperationException(
                $"DataCacheInstances['{context.InstanceName}']: Redis configuration is required when Provider is 'Redis'. " +
                $"Set '{context.ConfigSection}:Redis:Configuration' or " +
                $"'{context.ConfigSection}:DataCacheInstances:{context.InstanceName}:Redis:Configuration'.");
        }

        ConfigurationOptions configOptions = RedisConfigurationOptionsFactory.Create(redis);
        string dcNamespace = context.InstanceOptions.GetNamespace(context.InstanceName, context.RootOptions);
        string instanceName = context.InstanceName;

        context.Services.TryAddKeyedSingleton<IConnectionMultiplexer>(
            instanceName,
            (_, _) => ConnectionMultiplexer.Connect(configOptions));

        context.Services.AddSingleton<ICacheOrchestratorHealthProbe>(sp =>
        {
            IConnectionMultiplexer mux = sp.GetRequiredKeyedService<IConnectionMultiplexer>(instanceName);
            return new RedisCacheHealthProbe($"redis:data-cache:{instanceName}", mux);
        });

        context.Services.TryAddKeyedSingleton<IDistributedCache>(instanceName, (sp, _) =>
        {
            IConnectionMultiplexer mux = sp.GetRequiredKeyedService<IConnectionMultiplexer>(instanceName);
            // InstanceName left empty: Fusion CacheKeyPrefix (effective Data Cache namespace) owns keyspace
            // isolation so Redis keys are not double-prefixed.
            RedisCacheOptions redisCacheOptions = new()
            {
                ConnectionMultiplexerFactory = () => Task.FromResult(mux)
            };

            return new RedisCache(Options.Create(redisCacheOptions));
        });

        context.FusionBuilder.WithRegisteredKeyedDistributedCache(instanceName);

        context.FusionBuilder.WithBackplane(sp =>
        {
            IConnectionMultiplexer mux = sp.GetRequiredKeyedService<IConnectionMultiplexer>(instanceName);
            return new RedisBackplane(new RedisBackplaneOptions
            {
                ConnectionMultiplexerFactory = () => Task.FromResult(mux)
            });
        });

        string backplaneChannel = dcNamespace + ":backplane";
        context.FusionBuilder.WithOptions(o => o.BackplaneChannelPrefix = backplaneChannel);
    }
}
