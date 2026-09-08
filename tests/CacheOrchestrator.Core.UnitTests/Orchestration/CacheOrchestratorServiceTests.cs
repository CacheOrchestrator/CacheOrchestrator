using CacheOrchestrator.Configuration;
using CacheOrchestrator.Entity;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CacheOrchestrator.Core.UnitTests.Orchestration;

public class CacheOrchestratorServiceTests
{
    private readonly IDomainCacheOptionsProvider _domainOptions = Substitute.For<IDomainCacheOptionsProvider>();
    private readonly IDataCacheProvider _dataCache = Substitute.For<IDataCacheProvider>();
    private readonly CacheOrchestratorService _sut;

    public CacheOrchestratorServiceTests()
    {
        _dataCache.Name.Returns("FusionCache");
        _sut = new CacheOrchestratorService(
            _domainOptions,
            _dataCache,
            NullLogger<CacheOrchestratorService>.Instance);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenDataCacheDisabled_RunsFactoryUncached()
    {
        DomainCacheOptions opts = CreateOptions(enabled: false);
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        bool factoryCalled = false;
        string? value = await _sut.GetOrCreateAsync(
            new CacheEntryRequest { Domain = "products", Key = "product:1" },
            _ =>
            {
                factoryCalled = true;
                return ValueTask.FromResult<string?>("fresh");
            },
            TestContext.Current.CancellationToken);

        value.Should().Be("fresh");
        factoryCalled.Should().BeTrue();
        await _dataCache.DidNotReceive()
            .GetOrCreateAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<string?>>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenEnabled_DelegatesToProviderWithPhysicalKeyAndDomainTag()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "products", versionHex: "abc123");
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        DataCacheProviderRequest? captured = null;
        _dataCache
            .GetOrCreateAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<string?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<DataCacheProviderRequest>();
                return Cached<string?>("cached");
            });

        string? value = await _sut.GetOrCreateAsync(
            new CacheEntryRequest { Domain = "products", Key = "product:42" },
            _ => ValueTask.FromResult<string?>("fresh"),
            TestContext.Current.CancellationToken);

        value.Should().Be("cached");
        captured.Should().NotBeNull();
        captured.Key.Should().Be("co3:products:abc123:product:42");
        captured.InstanceName.Should().Be("default");
        captured.Tags.Should().Equal("domain:products");
        captured.DomainOptions.Should().BeSameAs(opts);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithFootprint_ExpandsEntityTags()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "store", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("store").Returns(opts);

        DataCacheProviderRequest? captured = null;
        _dataCache
            .GetOrCreateAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<int?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<DataCacheProviderRequest>();
                return Cached<int?>(1);
            });

        EntityFootprint footprint = new(new EntityRef("items", "42"));

        await _sut.GetOrCreateAsync(
            new CacheEntryRequest
            {
                Domain = "store",
                Key = "id:items:42",
                Footprint = footprint
            },
            _ => ValueTask.FromResult<int?>(1),
            TestContext.Current.CancellationToken);

        captured!.Tags.Should().Equal(
            "domain:store",
            "entity:store:items:42",
            "entitykind:store:items");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithAdditionalTags_MergesWithoutDuplicates()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "store", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("store").Returns(opts);

        DataCacheProviderRequest? captured = null;
        _dataCache
            .GetOrCreateAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<int?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<DataCacheProviderRequest>();
                return Cached<int?>(1);
            });

        await _sut.GetOrCreateAsync(
            new CacheEntryRequest
            {
                Domain = "store",
                Key = "list",
                AdditionalTags = ["domain:store", "custom:tag"]
            },
            _ => ValueTask.FromResult<int?>(1),
            TestContext.Current.CancellationToken);

        captured!.Tags.Should().Equal("domain:store", "custom:tag");
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenDomainMissing_Throws()
    {
        Func<Task> act = () => _sut.GetOrCreateAsync(
            new CacheEntryRequest { Domain = "  ", Key = "k" },
            _ => ValueTask.FromResult<string?>("x"),
            TestContext.Current.CancellationToken).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenKeyMissing_Throws()
    {
        Func<Task> act = () => _sut.GetOrCreateAsync(
            new CacheEntryRequest { Domain = "products", Key = "" },
            _ => ValueTask.FromResult<string?>("x"),
            TestContext.Current.CancellationToken).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void BuildPhysicalKey_IncludesDomainVersionAndLogicalKey()
    {
        DomainCacheOptions opts = CreateOptions(domain: "catalog", versionHex: "deadbeef");
        CacheOrchestratorService.BuildPhysicalKey(opts, "page:1")
            .Should().Be("co3:catalog:deadbeef:page:1");
    }

    [Fact]
    public void BuildPhysicalKey_SeparatesDomainVersionAndLogicalKeyUnambiguously()
    {
        DomainCacheOptions first = CreateOptions(domain: "a:b", versionHex: "c");
        DomainCacheOptions second = CreateOptions(domain: "a", versionHex: "b");

        string firstKey = CacheOrchestratorService.BuildPhysicalKey(first, "d");
        string secondKey = CacheOrchestratorService.BuildPhysicalKey(second, "c:d");

        firstKey.Should().Be("co3:a%3Ab:c:d");
        secondKey.Should().Be("co3:a:b:c:d");
        firstKey.Should().NotBe(secondKey);
    }

    [Fact]
    public void BuildTags_DomainOnly_ReusesPreparedSnapshotTags()
    {
        DomainCacheOptions opts = CreateOptions(domain: "catalog");

        IReadOnlyList<string> first = CacheOrchestratorService.BuildTags(opts, null, null);
        IReadOnlyList<string> second = CacheOrchestratorService.BuildTags(opts, EntityFootprint.Empty, []);

        first.Should().BeSameAs(second);
        first.Should().Equal("domain:catalog");
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenKeyIsPhysical_DoesNotReprefix()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "products", versionHex: "abc123");
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        DataCacheProviderRequest? captured = null;
        _dataCache
            .GetOrCreateAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<string?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<DataCacheProviderRequest>();
                return Cached<string?>("cached");
            });

        await _sut.GetOrCreateAsync(
            new CacheEntryRequest
            {
                Domain = "products",
                Key = "co3:products:abc123:already-physical",
                KeyIsPhysical = true
            },
            _ => ValueTask.FromResult<string?>("fresh"),
            TestContext.Current.CancellationToken);

        captured!.Key.Should().Be("co3:products:abc123:already-physical");
    }

    [Fact]
    public async Task GetOrCreateWithFootprintAsync_OnMiss_SelectsFinalTagsBeforePublication()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "store", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("store").Returns(opts);

        EntityRef primary = new("items", "42");
        EntityFootprint early = new(primary);
        EntityFootprint expanded = early.WithDependsOn([new EntityRef("categories", "9")]);
        IReadOnlyList<string>? finalTags = null;

        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>> factory =
                    callInfo.ArgAt<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(1);
                return MaterializeWithTags(factory, callInfo.ArgAt<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(2), tags => finalTags = tags);
            });

        FootprintCacheBox<string?> box = await _sut.GetOrCreateWithFootprintAsync<string>(
            new CacheEntryRequest
            {
                Domain = "store",
                Key = "id:items:42",
                Footprint = early
            },
            _ => ValueTask.FromResult(new FootprintCacheBox<string?>
            {
                Value = "payload",
                IsMiss = false,
                Footprint = expanded
            }),
            TestContext.Current.CancellationToken);

        box.Value.Should().Be("payload");
        box.Footprint.DependsOn.Should().ContainSingle(r => r.EntityKind == "categories" && r.ResourceId == "9");

        finalTags.Should().NotBeNull();
        finalTags!.Should().Contain("entity:store:items:42");
        finalTags!.Should().Contain("entity:store:categories:9");
        finalTags!.Should().Contain("entitykind:store:categories");
        await _dataCache.DidNotReceive().SetAsync(
            Arg.Any<DataCacheProviderRequest>(), Arg.Any<FootprintCacheBox<string?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateWithFootprintAsync_OnHit_DoesNotCallSetAsync()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "store", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("store").Returns(opts);

        EntityFootprint footprint = new(new EntityRef("items", "1"));
        var cached = new FootprintCacheBox<string?>
        {
            Value = "hit",
            IsMiss = false,
            Footprint = footprint
        };

        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Cached(cached));

        FootprintCacheBox<string?> box = await _sut.GetOrCreateWithFootprintAsync<string>(
            new CacheEntryRequest { Domain = "store", Key = "k", Footprint = footprint },
            _ => throw new InvalidOperationException("factory should not run on hit"),
            TestContext.Current.CancellationToken);

        box.Value.Should().Be("hit");
        box.Should().BeSameAs(cached);
        await _dataCache.DidNotReceive()
            .SetAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<FootprintCacheBox<string?>>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateWithFootprintAsync_OnMissWithUnchangedTags_DoesNotCallSetAsync()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "store", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("store").Returns(opts);

        EntityFootprint footprint = new(new EntityRef("items", "1"));
        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Materialize(
                callInfo.ArgAt<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(1),
                CancellationToken.None));

        await _sut.GetOrCreateWithFootprintAsync<string>(
            new CacheEntryRequest { Domain = "store", Key = "k", Footprint = footprint },
            _ => ValueTask.FromResult(new FootprintCacheBox<string?>
            {
                Value = "fresh",
                IsMiss = false,
                Footprint = footprint
            }),
            TestContext.Current.CancellationToken);

        await _dataCache.DidNotReceive().SetAsync(
            Arg.Any<DataCacheProviderRequest>(),
            Arg.Any<FootprintCacheBox<string?>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenProviderOmitsOutcome_ThrowsContractError()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true);
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);
        _dataCache
            .GetOrCreateAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<string?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new DataCacheProviderResult<string?>("value", DataCacheProviderOutcome.Unknown));

        Func<Task> act = async () => await _sut.GetOrCreateAsync(
            new CacheEntryRequest { Domain = "products", Key = "k" },
            _ => ValueTask.FromResult<string?>("fresh"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicitly return Cached or Materialized*");
    }

    [Fact]
    public async Task GetOrCreateWithFootprintAsync_WhenRefreshStartsButCachedValueReturns_DoesNotPromoteCachedValue()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "store", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("store").Returns(opts);

        EntityFootprint footprint = new(new EntityRef("items", "1"));
        var stale = new FootprintCacheBox<string?>
        {
            Value = "stale",
            IsMiss = false,
            Footprint = footprint
        };
        var completion = new TaskCompletionSource<FootprintCacheBox<string?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FootprintCacheBox<string?>>? backgroundFactory = null;

        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>> factory =
                    callInfo.ArgAt<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(1);
                backgroundFactory = factory(CancellationToken.None).AsTask();
                return Cached(stale);
            });

        FootprintCacheBox<string?> box = await _sut.GetOrCreateWithFootprintAsync<string>(
            new CacheEntryRequest { Domain = "store", Key = "k", Footprint = footprint },
            _ => new ValueTask<FootprintCacheBox<string?>>(completion.Task),
            TestContext.Current.CancellationToken);

        box.Value.Should().Be("stale");
        await _dataCache.DidNotReceive()
            .SetAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<FootprintCacheBox<string?>>(),
                Arg.Any<CancellationToken>());

        completion.SetResult(new FootprintCacheBox<string?>
        {
            Value = "fresh",
            IsMiss = false,
            Footprint = footprint
        });
        (await backgroundFactory!).Value.Should().Be("fresh");
    }

    [Fact]
    public async Task GetOrCreateEntityAsync_TagsPrimaryAndReturnsValue()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "products", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        DataCacheProviderRequest? getRequest = null;
        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                getRequest = callInfo.ArgAt<DataCacheProviderRequest>(0);
                Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>> factory =
                    callInfo.ArgAt<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(1);
                return Materialize(factory, CancellationToken.None);
            });

        _dataCache
            .SetAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<FootprintCacheBox<string?>>(),
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        string? value = await _sut.GetOrCreateEntityAsync(
            "products",
            "id:products:7",
            new EntityRef("products", "7"),
            _ => ValueTask.FromResult<string?>("sku-7"),
            TestContext.Current.CancellationToken);

        value.Should().Be("sku-7");
        getRequest!.Key.Should().Be("co3:products:v1:id:products:7");
        getRequest.Tags.Should().Contain("entity:products:products:7");
        getRequest.Tags.Should().Contain("entitykind:products:products");
    }

    [Fact]
    public async Task GetOrCreateEntityAsync_EntityCacheFactory_MergesDependsOnTags()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "products", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        IReadOnlyList<string>? finalTags = null;
        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>> factory =
                    callInfo.ArgAt<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(1);
                return MaterializeWithTags(factory, callInfo.ArgAt<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(2), tags => finalTags = tags);
            });

        string? value = await _sut.GetOrCreateEntityAsync(
            "products",
            "id:products:1",
            new EntityRef("products", "1"),
            _ => ValueTask.FromResult(
                EntityCache.Create("p1").DependsOn("categories", "cat-1")),
            TestContext.Current.CancellationToken);

        value.Should().Be("p1");
        finalTags!.Should().Contain("entity:products:categories:cat-1");
    }

    [Fact]
    public async Task GetOrCreateEntitySetAsync_TagsMembersFromSet()
    {
        DomainCacheOptions opts = CreateOptions(enabled: true, domain: "products", versionHex: "v1");
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        IReadOnlyList<string>? finalTags = null;
        _dataCache
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<IReadOnlyList<string>?>>>>(),
                Arg.Any<Func<FootprintCacheBox<IReadOnlyList<string>?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Func<CancellationToken, ValueTask<FootprintCacheBox<IReadOnlyList<string>?>>> factory =
                    callInfo.ArgAt<Func<CancellationToken, ValueTask<FootprintCacheBox<IReadOnlyList<string>?>>>>(1);
                return MaterializeWithTags(factory, callInfo.ArgAt<Func<FootprintCacheBox<IReadOnlyList<string>?>, IReadOnlyList<string>>>(2), tags => finalTags = tags);
            });

        IReadOnlyList<string> list = await _sut.GetOrCreateEntitySetAsync(
            "products",
            "list:all",
            "products",
            _ => ValueTask.FromResult(EntitySet.Create(["a", "b"], id => id)),
            TestContext.Current.CancellationToken);

        list.Should().Equal("a", "b");
        finalTags!.Should().Contain("entity:products:products:a");
        finalTags!.Should().Contain("entity:products:products:b");
        finalTags!.Should().Contain("entitykind:products:products");
    }

    [Fact]
    public async Task GetOrCreateEntityAsync_WhenDataCacheDisabled_RunsFactoryUncached()
    {
        DomainCacheOptions opts = CreateOptions(enabled: false);
        _domainOptions.GetOrCreateDomainOptions("products").Returns(opts);

        string? value = await _sut.GetOrCreateEntityAsync(
            "products",
            "id:products:1",
            new EntityRef("products", "1"),
            _ => ValueTask.FromResult<string?>("fresh"),
            TestContext.Current.CancellationToken);

        value.Should().Be("fresh");
        await _dataCache.DidNotReceive()
            .GetOrCreateWithTagsAsync(
                Arg.Any<DataCacheProviderRequest>(),
                Arg.Any<Func<CancellationToken, ValueTask<FootprintCacheBox<string?>>>>(),
                Arg.Any<Func<FootprintCacheBox<string?>, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>());
    }

    private static async ValueTask<DataCacheProviderResult<T>> MaterializeWithTags<T>(
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, IReadOnlyList<string>> tagSelector,
        Action<IReadOnlyList<string>> capture)
    {
        T value = await factory(CancellationToken.None);
        capture(tagSelector(value));
        return new(value, DataCacheProviderOutcome.Materialized);
    }

    private static ValueTask<DataCacheProviderResult<T>> Cached<T>(T value) =>
        ValueTask.FromResult(new DataCacheProviderResult<T>(value, DataCacheProviderOutcome.Cached));

    private static async ValueTask<DataCacheProviderResult<T>> Materialize<T>(
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken)
    {
        T value = await factory(cancellationToken);
        return new DataCacheProviderResult<T>(value, DataCacheProviderOutcome.Materialized);
    }

    private static DomainCacheOptions CreateOptions(
        bool enabled = true,
        string domain = "products",
        string versionHex = "1")
        => new()
        {
            Domain = domain,
            Version = "1",
            VersionHex = versionHex,
            DataCacheEnabled = enabled,
            DataCacheInstanceName = "default",
            DataCacheTtl = TimeSpan.FromMinutes(5),
        };
}
