using CacheOrchestrator.Configuration;
using CacheOrchestrator.FusionCache;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.FusionCache.UnitTests.Orchestration;

public class FusionDataCacheProviderTests
{
    private readonly IFusionCacheProvider _fusionProvider = Substitute.For<IFusionCacheProvider>();
    private readonly IFusionCache _fusionCache = Substitute.For<IFusionCache>();
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options =
        Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
    private readonly IFusionDomainSettingsProvider _fusionSettings = Substitute.For<IFusionDomainSettingsProvider>();
    private readonly FusionDataCacheProvider _sut;

    public FusionDataCacheProviderTests()
    {
        _fusionProvider.GetCache("default").Returns(_fusionCache);
        _options.CurrentValue.Returns(new CacheOrchestratorOptions
        {
            DataCacheInstances =
            {
                ["default"] = new CacheOrchestratorOptions.DataCacheInstanceOptions()
            }
        });
        _fusionSettings.Get(Arg.Any<string>()).Returns(new DomainFusionCacheSettings());
        _sut = new FusionDataCacheProvider(
            _fusionProvider,
            _options,
            _fusionSettings,
            NullLogger<FusionDataCacheProvider>.Instance);
    }

    [Fact]
    public void Name_IsFusionCache() => _sut.Name.Should().Be("FusionCache");

    [Fact]
    public void Capabilities_DescribeFusionProviderSurface()
    {
        DataCacheProviderCapabilities capabilities = _sut.Capabilities;

        capabilities.SupportsNamedInstances.Should().BeTrue();
        capabilities.SupportsFailSafe.Should().BeTrue();
        capabilities.SupportsEagerRefresh.Should().BeTrue();
        capabilities.SupportsBackplane.Should().BeTrue();
        capabilities.SupportsBatchInvalidation.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateAsync_CallsFusionGetOrSetWithKeyTagsAndFactory()
    {
        DomainCacheOptions domain = new()
        {
            Domain = "products",
            VersionHex = "v",
            DataCacheInstanceName = "default",
            DataCacheTtl = TimeSpan.FromMinutes(1)
        };

        string? passedKey = null;
        IEnumerable<string>? passedTags = null;
        _fusionCache
            .GetOrSetAsync(
                Arg.Any<string>(),
                Arg.Any<Func<FusionCacheFactoryExecutionContext<FusionProviderCacheEntry<string>>, CancellationToken, Task<FusionProviderCacheEntry<string>>>>(),
                Arg.Any<MaybeValue<FusionProviderCacheEntry<string>>>(),
                Arg.Any<FusionCacheEntryOptions>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                passedKey = callInfo.ArgAt<string>(0);
                passedTags = callInfo.ArgAt<IEnumerable<string>?>(4);
                return ValueTask.FromResult(new FusionProviderCacheEntry<string>
                {
                    Value = "hit",
                    MaterializationId = Guid.NewGuid()
                });
            });

        DataCacheProviderRequest request = new()
        {
            Key = "products:v:product:1",
            InstanceName = "default",
            Tags = ["domain:products", "entity:products:product:1"],
            DomainOptions = domain
        };

        DataCacheProviderResult<string> result = await _sut.GetOrCreateAsync(
            request,
            _ => ValueTask.FromResult("fresh"),
            TestContext.Current.CancellationToken);

        result.Value.Should().Be("hit");
        result.Outcome.Should().Be(DataCacheProviderOutcome.Cached);
        passedKey.Should().Be("products:v:product:1");
        passedTags.Should().Equal("domain:products", "entity:products:product:1");
    }

    [Fact]
    public async Task InvalidateAsync_WithoutInstance_RemovesTagsOnAllConfiguredInstances()
    {
        IFusionCache second = Substitute.For<IFusionCache>();
        _fusionProvider.GetCache("secondary").Returns(second);
        _options.CurrentValue.Returns(new CacheOrchestratorOptions
        {
            DataCacheInstances =
            {
                ["default"] = new CacheOrchestratorOptions.DataCacheInstanceOptions(),
                ["secondary"] = new CacheOrchestratorOptions.DataCacheInstanceOptions()
            }
        });

        await _sut.InvalidateAsync(
            new DataCacheInvalidationRequest { Tags = ["domain:products"] },
            TestContext.Current.CancellationToken);

        await _fusionCache.Received(1).RemoveByTagAsync("domain:products", token: Arg.Any<CancellationToken>());
        await second.Received(1).RemoveByTagAsync("domain:products", token: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusesPreparedEntryOptionsForSameSnapshots()
    {
        DomainCacheOptions domain = new()
        {
            Domain = "products",
            VersionHex = "v",
            DataCacheInstanceName = "default",
            DataCacheTtl = TimeSpan.FromMinutes(1)
        };
        List<FusionCacheEntryOptions> observed = [];
        _fusionCache
            .GetOrSetAsync(
                Arg.Any<string>(),
                Arg.Any<Func<FusionCacheFactoryExecutionContext<FusionProviderCacheEntry<string>>, CancellationToken, Task<FusionProviderCacheEntry<string>>>>(),
                Arg.Any<MaybeValue<FusionProviderCacheEntry<string>>>(),
                Arg.Do<FusionCacheEntryOptions>(observed.Add),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new FusionProviderCacheEntry<string>
            {
                Value = "hit",
                MaterializationId = Guid.NewGuid()
            });
        DataCacheProviderRequest request = new()
        {
            Key = "products:v:product:1",
            InstanceName = "default",
            Tags = ["domain:products"],
            DomainOptions = domain
        };

        await _sut.GetOrCreateAsync(
            request,
            _ => ValueTask.FromResult("fresh"),
            TestContext.Current.CancellationToken);
        await _sut.GetOrCreateAsync(
            request,
            _ => ValueTask.FromResult("fresh"),
            TestContext.Current.CancellationToken);

        observed.Should().HaveCount(2);
        observed[1].Should().BeSameAs(observed[0]);
    }
}
