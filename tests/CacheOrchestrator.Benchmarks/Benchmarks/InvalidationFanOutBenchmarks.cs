using BenchmarkDotNet.Attributes;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>Real in-memory Fusion tag invalidation across independently named instances.</summary>
[MemoryDiagnoser]
[ShortJob]
public class InvalidationFanOutBenchmarks
{
    private ServiceProvider _services = null!;
    private ICacheOrchestratorInvalidator _invalidator = null!;
    private string[] _domains = null!;

    [Params(1, 8)]
    public int InstanceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var values = new Dictionary<string, string?>();
        _domains = Enumerable.Range(0, InstanceCount).Select(i => $"domain-{i}").ToArray();
        for (int i = 0; i < InstanceCount; i++)
        {
            values[$"Cache:DataCacheInstances:engine-{i}:Provider"] = "InMemory";
            values[$"Cache:Domains:{_domains[i]}:DataCache:Instance"] = $"engine-{i}";
        }
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCacheOrchestratorCore(configuration);
        services.AddCacheOrchestratorFusionCache(configuration);
        _services = services.BuildServiceProvider();
        _invalidator = _services.GetRequiredService<ICacheOrchestratorInvalidator>();
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark]
    public async ValueTask<CacheInvalidationResult> InvalidateDomains()
    {
        CacheInvalidationResult result = await _invalidator.InvalidateDomainsAsync(_domains);
        if (!result.Succeeded)
            throw new InvalidOperationException("Invalidation benchmark must complete all native operations.");
        return result;
    }
}
