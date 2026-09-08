using BenchmarkDotNet.Attributes;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.FusionCache;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>End-to-end provider L1 hits, including settings and entry-options resolution.</summary>
[MemoryDiagnoser]
[ShortJob]
public class DataCacheProviderHitBenchmarks
{
    private ServiceProvider _fusionServices = null!;
    private ServiceProvider _hybridServices = null!;
    private IDataCacheProvider _fusion = null!;
    private IDataCacheProvider _fusionUncachedSettings = null!;
    private IDataCacheProvider _hybrid = null!;
    private DataCacheProviderRequest _request = null!;
    private DataCacheProviderRequest _uncachedSettingsRequest = null!;
    private DataCacheProviderRequest _footprintRequest = null!;

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
                ["Cache:Domains:catalog:FusionCache:EagerRefreshRatio"] = "0"
            })
            .Build();

        _fusionServices = BuildFusion(configuration);
        _hybridServices = BuildHybrid(configuration);
        _fusion = _fusionServices.GetRequiredService<IDataCacheProvider>();
        _fusionUncachedSettings = new FusionDataCacheProvider(
            _fusionServices.GetRequiredService<IFusionCacheProvider>(),
            _fusionServices.GetRequiredService<IOptionsMonitor<CacheOrchestratorOptions>>(),
            new BindingFusionSettingsProvider(configuration),
            NullLogger<FusionDataCacheProvider>.Instance);
        _hybrid = _hybridServices.GetRequiredService<IDataCacheProvider>();
        _request = new DataCacheProviderRequest
        {
            Key = "catalog:01:bench-hit",
            InstanceName = "default",
            Tags = ["domain:catalog"],
            DomainOptions = new DomainCacheOptions
            {
                Domain = "catalog",
                Version = "1",
                VersionHex = "01",
                DataCacheEnabled = true,
                DataCacheInstanceName = "default",
                DataCacheNamespace = "bench-dc",
                DataCacheTtl = TimeSpan.FromMinutes(5)
            }
        };
        _uncachedSettingsRequest = new DataCacheProviderRequest
        {
            Key = "catalog:01:bench-hit-uncached-settings",
            InstanceName = _request.InstanceName,
            Tags = _request.Tags,
            DomainOptions = _request.DomainOptions
        };

        _footprintRequest = new DataCacheProviderRequest
        {
            Key = "catalog:01:bench-footprint", InstanceName = _request.InstanceName,
            Tags = _request.Tags, DomainOptions = _request.DomainOptions
        };
        await _fusion.GetOrCreateWithTagsAsync(_footprintRequest, Factory, Tags);
        await _hybrid.GetOrCreateWithTagsAsync(_footprintRequest, Factory, Tags);
        await _fusion.GetOrCreateAsync(_request, Factory);
        await _fusionUncachedSettings.GetOrCreateAsync(_uncachedSettingsRequest, Factory);
        await _hybrid.GetOrCreateAsync(_request, Factory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fusionServices.Dispose();
        _hybridServices.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<DataCacheProviderResult<string>> Fusion_L1Hit() =>
        _fusion.GetOrCreateAsync(_request, Factory);

    [Benchmark]
    public ValueTask<DataCacheProviderResult<string>> Fusion_UncachedSettings_L1Hit() =>
        _fusionUncachedSettings.GetOrCreateAsync(_uncachedSettingsRequest, Factory);

    [Benchmark]
    public ValueTask<DataCacheProviderResult<string>> Hybrid_L1Hit() =>
        _hybrid.GetOrCreateAsync(_request, Factory);

    [Benchmark]
    public ValueTask<DataCacheProviderResult<string>> Fusion_DynamicTags_L1Hit() =>
        _fusion.GetOrCreateWithTagsAsync(_footprintRequest, Factory, Tags);

    [Benchmark]
    public ValueTask<DataCacheProviderResult<string>> Hybrid_DynamicTags_L1Hit() =>
        _hybrid.GetOrCreateWithTagsAsync(_footprintRequest, Factory, Tags);

    private static IReadOnlyList<string> Tags(string value) => ["domain:catalog", "entity:catalog:items:42"];

    private static ValueTask<string> Factory(CancellationToken cancellationToken) =>
        ValueTask.FromResult("value");

    private static ServiceProvider BuildFusion(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCacheOrchestratorCore(configuration);
        services.AddCacheOrchestratorFusionCache(configuration);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildHybrid(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCacheOrchestratorCore(configuration);
        services.AddHybridCache();
        services.AddCacheOrchestratorHybridCache();
        return services.BuildServiceProvider();
    }

    private sealed class BindingFusionSettingsProvider : IFusionDomainSettingsProvider
    {
        private readonly IConfiguration _configuration;

        public BindingFusionSettingsProvider(IConfiguration configuration) =>
            _configuration = configuration;

        public DomainFusionCacheSettings Get(string domain)
        {
            DomainFusionCacheSettings defaults = new();
            DomainFusionCacheSettings specific = new();
            _configuration.GetSection("Cache:DomainDefaults:FusionCache").Bind(defaults);
            _configuration.GetSection($"Cache:Domains:{domain}:FusionCache").Bind(specific);
            return new DomainFusionCacheSettings
            {
                HardTtlSeconds = specific.HardTtlSeconds ?? defaults.HardTtlSeconds ?? 43200,
                FailSafeSeconds = specific.FailSafeSeconds ?? defaults.FailSafeSeconds ?? 86400,
                EagerRefreshRatio = specific.EagerRefreshRatio ?? defaults.EagerRefreshRatio ?? 0.9,
                JitterSeconds = specific.JitterSeconds ?? defaults.JitterSeconds ?? 60,
                FactorySoftTimeoutSeconds = specific.FactorySoftTimeoutSeconds
                    ?? defaults.FactorySoftTimeoutSeconds
                    ?? 1,
                FactoryHardTimeoutSeconds = specific.FactoryHardTimeoutSeconds
                    ?? defaults.FactoryHardTimeoutSeconds
                    ?? 5,
                AllowBackgroundDistributed = specific.AllowBackgroundDistributed
                    ?? defaults.AllowBackgroundDistributed
                    ?? true,
                AllowBackgroundBackplane = specific.AllowBackgroundBackplane
                    ?? defaults.AllowBackgroundBackplane
                    ?? true
            };
        }
    }
}
