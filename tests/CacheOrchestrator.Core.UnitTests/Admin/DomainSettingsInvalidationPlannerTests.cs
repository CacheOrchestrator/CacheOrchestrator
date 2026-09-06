using CacheOrchestrator.Admin;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CacheOrchestrator.Core.UnitTests.Admin;

public sealed class DomainSettingsInvalidationPlannerTests
{
    [Theory]
    [InlineData("outputCache.ttlSeconds", 600, 300, 2)]
    [InlineData("dataCache.ttlSeconds", 600, 300, 1)]
    [InlineData("fusionCache.hardTtlSeconds", 600, 300, 1)]
    [InlineData("clientCache.ttlMinSeconds", 60, 30, 4)]
    public void Plan_WhenApplyImmediatelyAndValueDecreases_ReturnsExpectedTarget(
        string settingId,
        int beforeValue,
        int afterValue,
        int expected)
    {
        DomainSettingsInvalidationTargets result = DomainSettingsInvalidationPlanner.Plan(
            [settingId],
            Values((settingId, beforeValue)),
            Values((settingId, afterValue)),
            applyImmediately: true);

        result.Should().Be((DomainSettingsInvalidationTargets)expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_WhenValueIncreases_DoesNotInvalidate(bool applyImmediately)
    {
        DomainSettingsInvalidationTargets result = DomainSettingsInvalidationPlanner.Plan(
            ["outputCache.ttlSeconds", "dataCache.ttlSeconds"],
            Values(("outputCache.ttlSeconds", 300), ("dataCache.ttlSeconds", 300)),
            Values(("outputCache.ttlSeconds", 600), ("dataCache.ttlSeconds", 600)),
            applyImmediately);

        result.Should().Be(DomainSettingsInvalidationTargets.None);
    }

    [Fact]
    public void Plan_WhenApplyImmediatelyIsFalse_KeepsCreationTtl()
    {
        DomainSettingsInvalidationTargets result = DomainSettingsInvalidationPlanner.Plan(
            ["outputCache.ttlSeconds", "dataCache.ttlSeconds"],
            Values(("outputCache.ttlSeconds", 600), ("dataCache.ttlSeconds", 600)),
            Values(("outputCache.ttlSeconds", 300), ("dataCache.ttlSeconds", 300)),
            applyImmediately: false);

        result.Should().Be(DomainSettingsInvalidationTargets.None);
    }

    [Fact]
    public void Plan_WhenEdgeSafetyPolicyChanges_AlwaysInvalidatesEdge()
    {
        DomainSettingsInvalidationTargets result = DomainSettingsInvalidationPlanner.Plan(
            ["varyByHeaders"],
            Values(("varyByHeaders", new[] { "X-Tenant" })),
            Values(("varyByHeaders", new[] { "X-Tenant", "X-Locale" })),
            applyImmediately: false);

        result.Should().Be(DomainSettingsInvalidationTargets.Edge);
    }

    [Fact]
    public void Plan_WhenSeveralChangesTargetSameLayer_ReturnsLayerOnce()
    {
        DomainSettingsInvalidationTargets result = DomainSettingsInvalidationPlanner.Plan(
            ["dataCache.ttlSeconds", "fusionCache.hardTtlSeconds", "outputCache.ttlSeconds"],
            Values(
                ("dataCache.ttlSeconds", 600),
                ("fusionCache.hardTtlSeconds", 600),
                ("outputCache.ttlSeconds", 600)),
            Values(
                ("dataCache.ttlSeconds", 300),
                ("fusionCache.hardTtlSeconds", 300),
                ("outputCache.ttlSeconds", 300)),
            applyImmediately: true);

        result.Should().Be(
            DomainSettingsInvalidationTargets.DataCache | DomainSettingsInvalidationTargets.OutputCache);
    }

    [Fact]
    public async Task Coordinator_WhenSeveralChangesTargetSameLayer_InvalidatesEachLayerOnce()
    {
        var provider = new MutableValueProvider(Values(
            ("dataCache.ttlSeconds", 600),
            ("fusionCache.hardTtlSeconds", 600),
            ("outputCache.ttlSeconds", 600)));
        IDataCacheProvider dataCache = Substitute.For<IDataCacheProvider>();
        IHttpCacheInvalidationSink outputCache = Substitute.For<IHttpCacheInvalidationSink>();
        IDomainCacheOptionsProvider domainOptions = Substitute.For<IDomainCacheOptionsProvider>();
        domainOptions.GetOrCreateDomainOptions("products").Returns(new DomainCacheOptions
        {
            Domain = "products",
            DataCacheInstanceName = "default"
        });
        var sut = new DomainSettingsInvalidationCoordinator(
            [provider],
            dataCache,
            domainOptions,
            outputCache,
            [],
            NullLogger<DomainSettingsInvalidationCoordinator>.Instance);
        string[] settings = ["dataCache.ttlSeconds", "fusionCache.hardTtlSeconds", "outputCache.ttlSeconds"];
        IReadOnlyDictionary<string, JsonElement> before = sut.Capture("products", settings);
        provider.Values = Values(
            ("dataCache.ttlSeconds", 300),
            ("fusionCache.hardTtlSeconds", 300),
            ("outputCache.ttlSeconds", 300));

        await sut.ApplyAsync("products", settings, before, applyImmediately: true, CancellationToken.None);

        await dataCache.Received(1).InvalidateAsync(
            Arg.Any<DataCacheInvalidationRequest>(),
            Arg.Any<CancellationToken>());
        await outputCache.Received(1).EvictByTagAsync(
            CacheTags.Domain("products"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Coordinator_WhenSeveralEdgeSafetySettingsChange_QueuesOneEdgeInvalidation()
    {
        var provider = new MutableValueProvider(Values(
            ("varyByAccept", true),
            ("varyByHeaders", new[] { "X-Tenant" })));
        var observer = new RecordingObserver();
        DomainSettingsInvalidationCoordinator sut = CreateCoordinator(provider, observer);
        string[] settings = ["varyByAccept", "varyByHeaders"];
        IReadOnlyDictionary<string, JsonElement> before = sut.Capture("products", settings);
        provider.Values = Values(
            ("varyByAccept", false),
            ("varyByHeaders", new[] { "X-Tenant", "X-Locale" }));

        await sut.ApplyAsync("products", settings, before, applyImmediately: false, CancellationToken.None);

        observer.Domains.Should().Equal("products");
    }

    [Fact]
    public async Task Coordinator_WhenApplyingRemoteCommand_DoesNotRepeatExternalEdgeInvalidation()
    {
        var provider = new MutableValueProvider(Values(("varyByAccept", true)));
        var observer = new RecordingObserver();
        DomainSettingsInvalidationCoordinator sut = CreateCoordinator(provider, observer);
        string[] settings = ["varyByAccept"];
        IReadOnlyDictionary<string, JsonElement> before = sut.Capture("products", settings);
        provider.Values = Values(("varyByAccept", false));

        using (ClusterCommandScope.EnterRemote())
        {
            await sut.ApplyAsync("products", settings, before, applyImmediately: false, CancellationToken.None);
        }

        observer.Domains.Should().BeEmpty();
    }

    private static DomainSettingsInvalidationCoordinator CreateCoordinator(
        IDomainSettingValueProvider provider,
        IDomainSettingsInvalidationObserver observer)
    {
        IDataCacheProvider dataCache = Substitute.For<IDataCacheProvider>();
        IHttpCacheInvalidationSink outputCache = Substitute.For<IHttpCacheInvalidationSink>();
        IDomainCacheOptionsProvider domainOptions = Substitute.For<IDomainCacheOptionsProvider>();
        return new DomainSettingsInvalidationCoordinator(
            [provider],
            dataCache,
            domainOptions,
            outputCache,
            [observer],
            NullLogger<DomainSettingsInvalidationCoordinator>.Instance);
    }

    private static Dictionary<string, JsonElement> Values(params (string Id, object? Value)[] entries) =>
        entries.ToDictionary(
            static entry => entry.Id,
            static entry => JsonSerializer.SerializeToElement(entry.Value),
            StringComparer.OrdinalIgnoreCase);

    private sealed class MutableValueProvider(IReadOnlyDictionary<string, JsonElement> values)
        : IDomainSettingValueProvider
    {
        public IReadOnlyDictionary<string, JsonElement> Values { get; set; } = values;

        public bool TryGetValue(string domain, string settingId, out JsonElement value) =>
            Values.TryGetValue(settingId, out value);
    }

    private sealed class RecordingObserver : IDomainSettingsInvalidationObserver
    {
        public List<string> Domains { get; } = [];

        public ValueTask InvalidateDomainSettingsAsync(
            string domain,
            CancellationToken cancellationToken = default)
        {
            Domains.Add(domain);
            return ValueTask.CompletedTask;
        }
    }
}
