using BenchmarkDotNet.Attributes;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Entity;
using CacheOrchestrator.Invalidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>Full ASP.NET Core Data Cache hit paths over a real FusionCache L1.</summary>
[MemoryDiagnoser]
[ShortJob]
public class DomainDataCacheHitBenchmarks
{
    private ServiceProvider _services = null!;
    private IDomainDataCache _cache = null!;
    private ICacheOrchestratorInvalidator _invalidator = null!;
    private DefaultHttpContext _domainRequest = null!;
    private DefaultHttpContext _footprintRequest = null!;
    private DefaultHttpContext _staleFootprintRequest = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:Namespace"] = "bench",
                ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
                ["Cache:Domains:catalog:Version"] = "1",
                ["Cache:Domains:catalog:DataCache:TtlSeconds"] = "300",
                ["Cache:Domains:catalog:FusionCache:JitterSeconds"] = "0",
                ["Cache:Domains:catalog:FusionCache:EagerRefreshRatio"] = "0",
                ["Cache:Domains:stale:Version"] = "1",
                ["Cache:Domains:stale:DataCache:TtlSeconds"] = "0",
                ["Cache:Domains:stale:FusionCache:HardTtlSeconds"] = "300",
                ["Cache:Domains:stale:FusionCache:FailSafeSeconds"] = "300",
                ["Cache:Domains:stale:FusionCache:JitterSeconds"] = "0",
                ["Cache:Domains:stale:FusionCache:EagerRefreshRatio"] = "0"
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddCacheOrchestratorAspNetCore(configuration, enableMvcConvention: false);
        services.AddCacheOrchestratorFusionCache(configuration);
        _services = services.BuildServiceProvider();
        _cache = _services.GetRequiredService<IDomainDataCache>();
        _invalidator = _services.GetRequiredService<ICacheOrchestratorInvalidator>();

        _domainRequest = CreateRequest("/api/catalog");
        _footprintRequest = CreateRequest("/api/products/42");
        _staleFootprintRequest = CreateRequest("/api/products/43");
        IRequestDomainCacheOptions domains = _services.GetRequiredService<IRequestDomainCacheOptions>();
        domains.EnsureDomainOptions(_footprintRequest, "catalog");
        domains.EnsureDomainOptions(_staleFootprintRequest, "stale");
        _cache.SetEntityIdentity(_footprintRequest, "products", 42);
        _cache.SetEntityIdentity(_staleFootprintRequest, "products", 43);

        await _cache.GetOrSetAsync(_domainRequest, "catalog", DomainFactory);
        await _cache.GetOrSetEntityAsync(_footprintRequest, FootprintFactory);
        await _cache.GetOrSetEntityAsync(_staleFootprintRequest, FootprintFactory);
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true)]
    public Task<string> Domain_L1Hit()
    {
        ResetRequestState(_domainRequest);
        return _cache.GetOrSetAsync(_domainRequest, "catalog", DomainFactory);
    }

    [Benchmark]
    public Task<string?> EntityFootprint_L1Hit()
    {
        ResetRequestState(_footprintRequest);
        return _cache.GetOrSetEntityAsync(_footprintRequest, FootprintFactory);
    }

    [Benchmark]
    public Task<string?> EntityFootprint_StaleFallback()
    {
        ResetRequestState(_staleFootprintRequest);
        return _cache.GetOrSetEntityAsync(_staleFootprintRequest, FailingFootprintFactory);
    }

    private DefaultHttpContext CreateRequest(string path)
    {
        DefaultHttpContext http = new() { RequestServices = _services };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = path;
        return http;
    }

    [Benchmark]
    public async Task<string?> EntityFootprint_InvalidateAndRefresh()
    {
        CacheInvalidationResult result = await _invalidator.InvalidateEntityAsync("catalog", "products", "42");
        if (!result.Succeeded)
            throw new InvalidOperationException("Benchmark invalidation failed.");
        ResetRequestState(_footprintRequest);
        return await _cache.GetOrSetEntityAsync(_footprintRequest, FootprintFactory);
    }

    private static void ResetRequestState(HttpContext http)
    {
        if (http.Features.Get<ICacheOrchestratorFeature>() is { } feature)
        {
            feature.Disposition = null;
            feature.PendingEntityFootprint = null;
        }
    }

    private static Task<string> DomainFactory(CancellationToken cancellationToken) =>
        Task.FromResult("value");

    private static Task<EntityCache<string>> FootprintFactory(CancellationToken cancellationToken) =>
        Task.FromResult(EntityCache.Create("value").DependsOn("categories", 7));

    private static Task<EntityCache<string>> FailingFootprintFactory(CancellationToken cancellationToken) =>
        Task.FromException<EntityCache<string>>(new InvalidOperationException("benchmark refresh failure"));
}
