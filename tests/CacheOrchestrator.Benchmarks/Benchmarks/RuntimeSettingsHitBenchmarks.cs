using BenchmarkDotNet.Attributes;
using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.FusionCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>Published settings hits with the actual shared runtime store used by DI.</summary>
[MemoryDiagnoser]
[ShortJob]
public class RuntimeSettingsHitBenchmarks
{
    private ServiceProvider _services = null!;
    private IDomainCacheOptionsProvider _core = null!;
    private IRequestDomainCacheOptions _http = null!;
    private IFusionDomainSettingsProvider _fusion = null!;

    [Params(false, true)]
    public bool HasRuntimeOverlay { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Domains:catalog:Version"] = "1",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory"
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCacheOrchestratorAspNetCore(config, enableMvcConvention: false);
        services.AddCacheOrchestratorFusionCache(config);
        _services = services.BuildServiceProvider();
        if (HasRuntimeOverlay)
            _services.GetRequiredService<IDomainRuntimeOverrideStore>().SetVersion("catalog", "2");
        _core = _services.GetRequiredService<IDomainCacheOptionsProvider>();
        _http = _services.GetRequiredService<IRequestDomainCacheOptions>();
        _fusion = _services.GetRequiredService<IFusionDomainSettingsProvider>();
        _ = CoreHit();
        _ = HttpHit();
        _ = FusionHit();
    }

    [Benchmark]
    public DomainCacheOptions CoreHit() => _core.GetOrCreateDomainOptions("catalog");

    [Benchmark]
    public DomainHttpCacheOptions HttpHit() => _http.GetOrCreateDomainOptions("catalog");

    [Benchmark]
    public DomainFusionCacheSettings FusionHit() => _fusion.Get("catalog");

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();
}
