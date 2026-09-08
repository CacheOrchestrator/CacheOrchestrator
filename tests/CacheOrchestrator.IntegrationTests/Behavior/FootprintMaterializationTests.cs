using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Entity;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using CacheOrchestrator.Orchestration;
using CacheOrchestrator.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;

namespace CacheOrchestrator.IntegrationTests.Behavior;

[Collection("Redis")]
public sealed class FootprintMaterializationTests(RedisFixture redis)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FinalTags_InvalidateMembersDependenciesAndAliases_ThroughL1AndRedisL2(bool hybrid, bool distributed)
    {
        string cacheNamespace = "footprint-" + Guid.NewGuid().ToString("N");
        await using ServiceProvider first = Build(hybrid, cacheNamespace, distributed);
        int calls = 0;
        ValueTask<FootprintCacheBox<string?>> Factory(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Box("value-" + Interlocked.Increment(ref calls)));
        CacheEntryRequest request = new() { Domain = "catalog", Key = "set" };

        (await Read(first, request, Factory)).Value.Should().Be("value-1");
        (await Read(first, request, Factory)).Value.Should().Be("value-1");
        calls.Should().Be(1);

        if (hybrid && distributed)
        {
            DomainCacheOptions options = first.GetRequiredService<IDomainCacheOptionsProvider>().GetOrCreateDomainOptions("catalog");
            string key = Uri.EscapeDataString(options.DataCacheNamespace) + ":co3:catalog:" + options.VersionHex + ":set";
            IDistributedCache backend = first.GetRequiredService<IDistributedCache>();
            byte[]? stored = null;
            for (int attempt = 0; attempt < 100 && stored is null; attempt++)
            {
                stored = await backend.GetAsync(key, TestContext.Current.CancellationToken);
                if (stored is null)
                    await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            stored.Should().NotBeNull("HybridCache releases callers before its L2 write completes");
        }

        // A second service provider has a cold L1, so a hit proves payload + footprint survived L2.
        await using ServiceProvider second = Build(hybrid, cacheNamespace, distributed);
        ServiceProvider reader = distributed ? second : first;
        FootprintCacheBox<string?> restored = await Read(reader, request, Factory);
        restored.Value.Should().Be("value-1");
        restored.Footprint.Members.Should().ContainSingle(r => r.ResourceId == "member");
        restored.Footprint.DependsOn.Should().ContainSingle(r => r.ResourceId == "dependency");
        restored.Footprint.Aliases.Should().ContainSingle(r => r.ResourceId == "alias");
        calls.Should().Be(1);

        foreach (string id in new[] { "member", "dependency", "alias" })
        {
            await reader.GetRequiredService<IDataCacheProvider>().InvalidateAsync(
                new DataCacheInvalidationRequest { Tags = [CacheTags.Entity("catalog", "items", id)] },
                TestContext.Current.CancellationToken);
            int before = calls;
            (await Read(reader, request, Factory)).Value.Should().Be("value-" + (before + 1));
            (await Read(reader, request, Factory)).Value.Should().Be("value-" + (before + 1));
            calls.Should().Be(before + 1);
        }
    }

    [Theory]
    [InlineData("member", false)]
    [InlineData("dependency", false)]
    [InlineData("alias", false)]
    [InlineData("member", true)]
    [InlineData("dependency", true)]
    [InlineData("alias", true)]
    public async Task Fusion_BackgroundRefresh_CommitsFinalTagsAfterRequestEnds(string changedId, bool softTimeout)
    {
        await using ServiceProvider services = Build(
            false, "refresh-" + Guid.NewGuid().ToString("N"), false, background: true, softTimeout);
        IDomainDataCache cache = services.GetRequiredService<IDomainDataCache>();
        int calls = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<EntityCache<string>> Factory(CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return call == 1
                ? EntityCache.Create("value-1")
                : EntityCache.Create("value-" + call)
                    .Members("items", ["member"]).DependsOn("items", "dependency").Alias("items", "alias");
        }

        DefaultHttpContext seed = Http(services);
        (await cache.GetOrSetEntityAsync(seed, Factory, TestContext.Current.CancellationToken)).Should().Be("value-1");
        await Task.Delay(softTimeout ? 1200 : 700, TestContext.Current.CancellationToken);
        DefaultHttpContext refresh = Http(services);
        Task<string?> pending = cache.GetOrSetEntityAsync(refresh, Factory, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Should().Be("value-1");
        refresh.Uninitialize();
        release.TrySetResult();

        // Observe publication, not merely completion of the application factory.
        string? value = null;
        for (int i = 0; i < 100 && value != "value-2"; i++)
        {
            value = await cache.GetOrSetEntityAsync(Http(services), Factory, TestContext.Current.CancellationToken);
            if (value != "value-2")
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        value.Should().Be("value-2");
        calls.Should().Be(2);
        await services.GetRequiredService<IDataCacheProvider>().InvalidateAsync(
            new DataCacheInvalidationRequest { Tags = [CacheTags.Entity("catalog", "items", changedId)] },
            TestContext.Current.CancellationToken);
        (await cache.GetOrSetEntityAsync(Http(services), Factory, TestContext.Current.CancellationToken))
            .Should().Be("value-3");
        calls.Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentWaiters_DoNotOverwriteANewerMaterialization(bool hybrid)
    {
        await using ServiceProvider services = Build(hybrid, "stampede", false);
        CacheEntryRequest request = new() { Domain = "catalog", Key = "concurrent" };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async ValueTask<FootprintCacheBox<string?>> Factory(CancellationToken cancellationToken)
        {
            int generation = Interlocked.Increment(ref calls);
            if (generation == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return Box("value-" + generation);
        }

        Task<FootprintCacheBox<string?>>[] waiters = Enumerable.Range(0, 20)
            .Select(_ => Read(services, request, Factory).AsTask()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.TrySetResult();
        (await Task.WhenAll(waiters)).Should().OnlyContain(box => box.Value == "value-1");
        calls.Should().Be(1);
        await services.GetRequiredService<IDataCacheProvider>().InvalidateAsync(
            new DataCacheInvalidationRequest { Tags = [CacheTags.Entity("catalog", "items", "member")] },
            TestContext.Current.CancellationToken);
        (await Read(services, request, Factory)).Value.Should().Be("value-2");
        (await Read(services, request, Factory)).Value.Should().Be("value-2");
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTagSelection_DoesNotPublishValue_AndExplicitSetReplacesTaggedValue(bool hybrid)
    {
        await using ServiceProvider services = Build(hybrid, "selection", false);
        IDataCacheProvider provider = services.GetRequiredService<IDataCacheProvider>();
        DomainCacheOptions options = services.GetRequiredService<IDomainCacheOptionsProvider>().GetOrCreateDomainOptions("catalog");
        DataCacheProviderRequest request = new()
        {
            Key = "selector", InstanceName = "default", DomainOptions = options, Tags = ["domain:catalog"]
        };
        Func<Task> failed = async () => await provider.GetOrCreateWithTagsAsync(
            request, _ => ValueTask.FromResult("incomplete"),
            _ => throw new InvalidOperationException("tag selection failed"), TestContext.Current.CancellationToken);
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (await provider.GetOrCreateWithTagsAsync(
            request, _ => ValueTask.FromResult("complete"), _ => ["domain:catalog", "final"],
            TestContext.Current.CancellationToken)).Value.Should().Be("complete");
        await provider.InvalidateAsync(new DataCacheInvalidationRequest { Tags = ["final"] }, TestContext.Current.CancellationToken);
        (await provider.GetOrCreateAsync(
            request, _ => ValueTask.FromResult("ordinary"), TestContext.Current.CancellationToken))
            .Value.Should().Be("ordinary", "ordinary lookups must honor a previously stored dynamic footprint");
        await provider.SetAsync(request, "replacement", TestContext.Current.CancellationToken);
        (await provider.GetOrCreateWithTagsAsync(
            request, _ => throw new InvalidOperationException("must hit"), (string _) => ["final"],
            TestContext.Current.CancellationToken)).Value.Should().Be("replacement");
    }

    private ServiceProvider Build(bool hybrid, string cacheNamespace, bool distributed, bool background = false, bool softTimeout = false)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = cacheNamespace,
            ["Cache:DataCacheInstances:default:Provider"] = distributed && !hybrid ? "Redis" : "InMemory",
            ["Cache:Redis:Configuration"] = redis.ConnectionString,
            ["Cache:Domains:catalog:Version"] = "1",
            ["Cache:Domains:catalog:DataCache:TtlSeconds"] = softTimeout ? "1" : "60",
            ["Cache:Domains:catalog:FusionCache:JitterSeconds"] = "0",
            ["Cache:Domains:catalog:FusionCache:EagerRefreshRatio"] = background && !softTimeout ? "0.01" : "0",
            ["Cache:Domains:catalog:FusionCache:FactorySoftTimeoutSeconds"] = "1",
            ["Cache:Domains:catalog:FusionCache:FactoryHardTimeoutSeconds"] = "10",
            ["Cache:Domains:catalog:FusionCache:AllowBackgroundDistributed"] = "false",
            ["Cache:Domains:catalog:FusionCache:AllowBackgroundBackplane"] = "false"
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCacheOrchestratorAspNetCore(configuration);
        if (hybrid)
        {
            if (distributed)
                services.AddStackExchangeRedisCache(options => options.Configuration = redis.ConnectionString);
            services.AddHybridCache();
            services.AddCacheOrchestratorHybridCache();
        }
        else
        {
            if (distributed)
                services.AddRedisFusionCacheBackend(configuration);
            services.AddCacheOrchestratorFusionCache(configuration);
        }
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Http(IServiceProvider services)
    {
        DefaultHttpContext http = new();
        http.Request.Method = "GET";
        http.Request.Path = "/catalog/42";
        services.GetRequiredService<IRequestDomainCacheOptions>().EnsureDomainOptions(http, "catalog");
        services.GetRequiredService<IDomainDataCache>().SetEntityIdentity(http, "catalog", "42");
        return http;
    }

    private static ValueTask<FootprintCacheBox<string?>> Read(
        IServiceProvider services, CacheEntryRequest request,
        Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>> factory) =>
        services.GetRequiredService<ICacheOrchestrator>()
            .GetOrCreateWithFootprintAsync(request, factory, TestContext.Current.CancellationToken);

    private static FootprintCacheBox<string?> Box(string value) => new()
    {
        Value = value,
        Footprint = new EntityFootprint(null)
            .WithMembers([new EntityRef("items", "member")])
            .WithDependsOn([new EntityRef("items", "dependency")])
            .WithAliases([new EntityRef("items", "alias")])
    };
}
