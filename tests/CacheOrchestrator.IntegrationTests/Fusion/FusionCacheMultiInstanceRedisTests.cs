using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.IntegrationTests.Fusion;

/// <summary>
/// Verifies that two Redis-backed FusionCache instances can target different Redis endpoints
/// (or databases) without sharing a single global <see cref="IDistributedCache"/>.
/// </summary>
public sealed class FusionCacheMultiInstanceRedisTests : IAsyncLifetime
{
    // Two independent containers (not the shared Redis collection) so each Fusion instance
    // talks to a different Redis endpoint — isolation under test.
    private readonly RedisContainer _redisA = RedisFixture.CreateContainer();
    private readonly RedisContainer _redisB = RedisFixture.CreateContainer();

    public async ValueTask InitializeAsync()
    {
        try
        {
            await Task.WhenAll(_redisA.StartAsync(), _redisB.StartAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start Redis Testcontainers for multi-instance tests. Docker must be running.",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _redisA.DisposeAsync().ConfigureAwait(false);
        await _redisB.DisposeAsync().ConfigureAwait(false);
    }

    private ServiceProvider BuildProvider()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:Namespace"] = "multi",
                ["Cache:OutputCache:Provider"] = "InMemory",
                ["Cache:DataCacheInstances:default:Provider"] = "Redis",
                ["Cache:DataCacheInstances:default:Redis:Configuration"] = _redisA.GetConnectionString(),
                ["Cache:DataCacheInstances:pii:Provider"] = "Redis",
                ["Cache:DataCacheInstances:pii:Redis:Configuration"] = _redisB.GetConnectionString(),
                ["Cache:Domains:products:DataCache:Instance"] = "default",
                ["Cache:Domains:products:Version"] = "v1",
                ["Cache:Domains:products:DataCache:TtlSeconds"] = "120",
                ["Cache:Domains:users:DataCache:Instance"] = "pii",
                ["Cache:Domains:users:Version"] = "v1",
                ["Cache:Domains:users:DataCache:TtlSeconds"] = "120",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddCacheOrchestratorAspNetCore(config, o => o.AddRedisBackend());
        services.AddCacheOrchestratorFusionCache(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registration_CreatesDistinctKeyedDistributedCachesAndMultiplexers()
    {
        using ServiceProvider sp = BuildProvider();

        IDistributedCache defaultCache = sp.GetRequiredKeyedService<IDistributedCache>("default");
        IDistributedCache piiCache = sp.GetRequiredKeyedService<IDistributedCache>("pii");
        defaultCache.Should().NotBeSameAs(piiCache);

        IConnectionMultiplexer muxDefault = sp.GetRequiredKeyedService<IConnectionMultiplexer>("default");
        IConnectionMultiplexer muxPii = sp.GetRequiredKeyedService<IConnectionMultiplexer>("pii");
        muxDefault.Should().NotBeSameAs(muxPii);

        IFusionCacheProvider fusionProvider = sp.GetRequiredService<IFusionCacheProvider>();
        fusionProvider.GetCache("default").Should().NotBeSameAs(fusionProvider.GetCache("pii"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataInstanceNamedOc_IsIsolatedFromOutputCache_InEitherRegistrationOrder(bool fusionFirst)
    {
        string suffix = Guid.NewGuid().ToString("N");
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "collision-" + suffix,
            ["Cache:OutputCache:Provider"] = "Redis",
            ["Cache:OutputCache:Redis:Configuration"] = _redisA.GetConnectionString(),
            ["Cache:DataCacheInstances:oc:Provider"] = "Redis",
            ["Cache:DataCacheInstances:oc:Redis:Configuration"] = _redisB.GetConnectionString(),
            ["Cache:DomainDefaults:DataCache:Instance"] = "oc"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedisFusionCacheBackend(config);
        if (fusionFirst)
            services.AddCacheOrchestratorFusionCache(config);
        services.AddCacheOrchestratorAspNetCore(config, builder => builder.AddRedisOutputCacheBackend());
        if (!fusionFirst)
            services.AddCacheOrchestratorFusionCache(config);
        await using ServiceProvider provider = services.BuildServiceProvider();
        ICacheOrchestratorHealthProbe[] probes = provider.GetServices<ICacheOrchestratorHealthProbe>().ToArray();
        probes.Select(probe => probe.Name).Should().BeEquivalentTo(new[] { "redis:output-cache", "redis:data-cache:oc" });
        foreach (ICacheOrchestratorHealthProbe probe in probes)
            await probe.ProbeAsync(TestContext.Current.CancellationToken);

        string key = "data-collision-" + suffix;
        await provider.GetRequiredKeyedService<IDistributedCache>("oc").SetAsync(key, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        await using IConnectionMultiplexer outputRedis = await ConnectionMultiplexer.ConnectAsync(_redisA.GetConnectionString());
        await using IConnectionMultiplexer dataRedis = await ConnectionMultiplexer.ConnectAsync(_redisB.GetConnectionString());
        (await dataRedis.GetDatabase().KeyExistsAsync(key)).Should().BeTrue();
        (await outputRedis.GetDatabase().KeyExistsAsync(key)).Should().BeFalse();

        IOutputCacheStore output = provider.GetRequiredService<IOutputCacheStore>();
        await output.SetAsync("output-" + suffix, new byte[] { 4, 5 }, [], TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        (await output.GetAsync("output-" + suffix, TestContext.Current.CancellationToken)).Should().Equal(4, 5);
        outputRedis.GetServer(outputRedis.GetEndPoints()[0]).Keys(pattern: "*output-" + suffix + "*").Should().NotBeEmpty();
        dataRedis.GetServer(dataRedis.GetEndPoints()[0]).Keys(pattern: "*output-" + suffix + "*").Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrSetAsync_WritesL2OnlyToInstanceRedis_AndInvalidationIsIsolated()
    {
        await using ServiceProvider sp = BuildProvider();
        IDomainDataCache cache = sp.GetRequiredService<IDomainDataCache>();
        ICacheOrchestratorInvalidator invalidator = sp.GetRequiredService<ICacheOrchestratorInvalidator>();
        IRequestDomainCacheOptions domains = sp.GetRequiredService<IRequestDomainCacheOptions>();

        DefaultHttpContext productsHttp = new();
        productsHttp.Request.Method = "GET";
        productsHttp.Request.Path = "/api/products/1";
        domains.EnsureDomainOptions(productsHttp, "products");

        DefaultHttpContext usersHttp = new();
        usersHttp.Request.Method = "GET";
        usersHttp.Request.Path = "/api/users/1";
        domains.EnsureDomainOptions(usersHttp, "users");

        int productCalls = 0;
        int userCalls = 0;

        await cache.GetOrSetAsync(productsHttp, "products", _ =>
        {
            productCalls++;
            return Task.FromResult("product-v1");
        }, TestContext.Current.CancellationToken);

        await cache.GetOrSetAsync(usersHttp, "users", _ =>
        {
            userCalls++;
            return Task.FromResult("user-v1");
        }, TestContext.Current.CancellationToken);

        productCalls.Should().Be(1);
        userCalls.Should().Be(1);

        // L2 must land on the correct Redis: products → Redis A, users → Redis B.
        await using IConnectionMultiplexer muxA = await ConnectionMultiplexer.ConnectAsync(_redisA.GetConnectionString());
        await using IConnectionMultiplexer muxB = await ConnectionMultiplexer.ConnectAsync(_redisB.GetConnectionString());

        // Allow async L2 write (background distributed ops may apply).
        await WaitUntilAsync(
            async () => await CountKeysAsync(muxA) > 0 && await CountKeysAsync(muxB) > 0,
            timeout: TimeSpan.FromSeconds(10));

        long keysA = await CountKeysAsync(muxA);
        long keysB = await CountKeysAsync(muxB);
        keysA.Should().BeGreaterThan(0, "products L2 should write to Redis A");
        keysB.Should().BeGreaterThan(0, "users L2 should write to Redis B");

        // Hits from L1 (and L2) without factory.
        await cache.GetOrSetAsync(productsHttp, "products", _ =>
        {
            productCalls++;
            return Task.FromResult("x");
        }, TestContext.Current.CancellationToken);
        await cache.GetOrSetAsync(usersHttp, "users", _ =>
        {
            userCalls++;
            return Task.FromResult("y");
        }, TestContext.Current.CancellationToken);
        productCalls.Should().Be(1);
        userCalls.Should().Be(1);

        // Invalidate only products → users must remain cached.
        await invalidator.InvalidateDomainAsync("products", TestContext.Current.CancellationToken);

        await cache.GetOrSetAsync(productsHttp, "products", _ =>
        {
            productCalls++;
            return Task.FromResult("product-v2");
        }, TestContext.Current.CancellationToken);
        await cache.GetOrSetAsync(usersHttp, "users", _ =>
        {
            userCalls++;
            return Task.FromResult("user-v2");
        }, TestContext.Current.CancellationToken);

        productCalls.Should().Be(2);
        userCalls.Should().Be(1);
    }

    private static async Task<long> CountKeysAsync(IConnectionMultiplexer mux)
    {
        IServer server = mux.GetServers().First(s => s.IsConnected);
        long count = 0;
        await foreach (RedisKey _ in server.KeysAsync(pattern: "*"))
            count++;
        return count;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition not met within {timeout}.");
    }
}
