using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsHybrid = Microsoft.Extensions.Caching.Hybrid;

namespace CacheOrchestrator.HybridCache.UnitTests.Orchestration;

public class HybridDataCacheProviderTests
{
    private readonly MsHybrid.HybridCache _cache = Substitute.For<MsHybrid.HybridCache>();
    private readonly CacheOrchestrator.HybridCache.HybridDataCacheProvider _sut;

    public HybridDataCacheProviderTests()
    {
        _sut = new CacheOrchestrator.HybridCache.HybridDataCacheProvider(
            _cache,
            CreateOptions(),
            NullLogger<CacheOrchestrator.HybridCache.HybridDataCacheProvider>.Instance);
    }

    [Fact]
    public void Name_IsHybridCache() => _sut.Name.Should().Be("HybridCache");

    [Fact]
    public void Capabilities_DescribeHybridProviderSurface()
    {
        DataCacheProviderCapabilities capabilities = _sut.Capabilities;

        capabilities.SupportsNamedInstances.Should().BeFalse();
        capabilities.SupportsFailSafe.Should().BeFalse();
        capabilities.SupportsEagerRefresh.Should().BeFalse();
        capabilities.SupportsBackplane.Should().BeFalse();
        capabilities.SupportsBatchInvalidation.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateAsync_CallsHybridWithKeyTagsAndDataCacheTtl()
    {
        string? passedKey = null;
        MsHybrid.HybridCacheEntryOptions? passedOptions = null;
        IEnumerable<string>? passedTags = null;

        _cache
            .GetOrCreateAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, ValueTask<string>>>(),
                Arg.Any<Func<Func<CancellationToken, ValueTask<string>>, CancellationToken, ValueTask<HybridProviderCacheEntry<string>>>>(),
                Arg.Any<MsHybrid.HybridCacheEntryOptions?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                passedKey = callInfo.ArgAt<string>(0);
                passedOptions = callInfo.ArgAt<MsHybrid.HybridCacheEntryOptions?>(3);
                passedTags = callInfo.ArgAt<IEnumerable<string>?>(4);
                return ValueTask.FromResult(new HybridProviderCacheEntry<string>
                {
                    Value = "hit",
                    MaterializationId = Guid.NewGuid()
                });
            });

        DomainCacheOptions domain = new()
        {
            Domain = "products",
            VersionHex = "v",
            DataCacheTtl = TimeSpan.FromMinutes(5),
            DataCacheInstanceName = "default",
            DataCacheNamespace = "shop-dc",
        };

        DataCacheProviderRequest request = new()
        {
            Key = "products:v:product:1",
            InstanceName = "default",
            Tags = ["domain:products", "entity:products:product:1"],
            DomainOptions = domain,
        };

        DataCacheProviderResult<string> result = await _sut.GetOrCreateAsync(
            request,
            _ => ValueTask.FromResult("fresh"),
            TestContext.Current.CancellationToken);

        result.Value.Should().Be("hit");
        result.Outcome.Should().Be(DataCacheProviderOutcome.Cached);
        passedKey.Should().Be("shop-dc:products:v:product:1");
        passedOptions!.Expiration.Should().Be(TimeSpan.FromMinutes(5));
        passedOptions.LocalCacheExpiration.Should().Be(TimeSpan.FromMinutes(5));
        passedTags.Should().Equal("shop-dc:domain:products", "shop-dc:entity:products:product:1");
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusesPreparedOptionsForSnapshot_AndRebuildsForNewSnapshot()
    {
        List<string> keys = [];
        List<MsHybrid.HybridCacheEntryOptions?> entryOptions = [];
        _cache
            .GetOrCreateAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, ValueTask<string>>>(),
                Arg.Any<Func<Func<CancellationToken, ValueTask<string>>, CancellationToken, ValueTask<HybridProviderCacheEntry<string>>>>(),
                Arg.Any<MsHybrid.HybridCacheEntryOptions?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                keys.Add(callInfo.ArgAt<string>(0));
                entryOptions.Add(callInfo.ArgAt<MsHybrid.HybridCacheEntryOptions?>(3));
                return ValueTask.FromResult(new HybridProviderCacheEntry<string>
                {
                    Value = "hit",
                    MaterializationId = Guid.NewGuid()
                });
            });

        DomainCacheOptions firstSnapshot = new()
        {
            Domain = "products",
            DataCacheNamespace = "shop one",
            DataCacheTtl = TimeSpan.FromMinutes(5)
        };
        DataCacheProviderRequest firstRequest = new()
        {
            Key = "products:v1:1",
            InstanceName = "default",
            Tags = ["domain:products"],
            DomainOptions = firstSnapshot
        };
        DomainCacheOptions reloadedSnapshot = new()
        {
            Domain = "products",
            DataCacheNamespace = "shop two",
            DataCacheTtl = TimeSpan.FromMinutes(10)
        };
        DataCacheProviderRequest reloadedRequest = new()
        {
            Key = firstRequest.Key,
            InstanceName = firstRequest.InstanceName,
            Tags = firstRequest.Tags,
            DomainOptions = reloadedSnapshot
        };

        await _sut.GetOrCreateAsync(firstRequest, Factory, TestContext.Current.CancellationToken);
        await _sut.GetOrCreateAsync(firstRequest, Factory, TestContext.Current.CancellationToken);
        await _sut.GetOrCreateAsync(reloadedRequest, Factory, TestContext.Current.CancellationToken);

        entryOptions[0].Should().BeSameAs(entryOptions[1]);
        entryOptions[2].Should().NotBeSameAs(entryOptions[0]);
        entryOptions[2]!.Expiration.Should().Be(TimeSpan.FromMinutes(10));
        keys.Should().Equal(
            "shop%20one:products:v1:1",
            "shop%20one:products:v1:1",
            "shop%20two:products:v1:1");
    }

    [Fact]
    public async Task InvalidateAsync_DelegatesTagsToHybrid()
    {
        await _sut.InvalidateAsync(
            new DataCacheInvalidationRequest { Tags = ["domain:products"] },
            TestContext.Current.CancellationToken);

        await _cache.Received(1).RemoveByTagAsync("shop-dc:domain:products", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_WithInstanceName_StillUsesSingleHybridCache()
    {
        await _sut.InvalidateAsync(
            new DataCacheInvalidationRequest
            {
                InstanceName = "secondary",
                Tags = ["domain:products"]
            },
            TestContext.Current.CancellationToken);

        await _cache.Received(1).RemoveByTagAsync("shop-dc-secondary:domain:products", Arg.Any<CancellationToken>());
    }

    private static IOptionsMonitor<CacheOrchestratorOptions> CreateOptions()
    {
        IOptionsMonitor<CacheOrchestratorOptions> monitor = Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
        monitor.CurrentValue.Returns(new CacheOrchestratorOptions
        {
            Namespace = "shop",
            DataCacheInstances =
            {
                ["default"] = new(),
                ["secondary"] = new()
            }
        });
        return monitor;
    }

    private static ValueTask<string> Factory(CancellationToken cancellationToken) =>
        ValueTask.FromResult("fresh");
}
