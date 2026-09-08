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

        await sut.ApplyAsync(new DomainSettingsInvalidationPlan("products", "default",
            DomainSettingsInvalidationPlanner.Plan(settings, before, provider.Values, applyImmediately: true), 0), CancellationToken.None);

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

        await sut.ApplyAsync(new DomainSettingsInvalidationPlan("products", "default",
            DomainSettingsInvalidationPlanner.Plan(settings, before, provider.Values, applyImmediately: false), 1), CancellationToken.None);

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
            await sut.ApplyAsync(new DomainSettingsInvalidationPlan("products", "default",
            DomainSettingsInvalidationPlanner.Plan(settings, before, provider.Values, applyImmediately: false), 1), CancellationToken.None);
        }

        observer.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentPatches_CaptureEachTransitionBeforeTheNextMutation()
    {
        var state = new DomainRuntimeOverrideStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var values = new PausingStateValues(state, entered, release);
        IDomainCacheOptionsProvider options = Substitute.For<IDomainCacheOptionsProvider>();
        options.GetOrCreateDomainOptions("products").Returns(new DomainCacheOptions { Domain = "products", DataCacheInstanceName = "default" });
        var coordinator = new DomainSettingsInvalidationCoordinator(
            [values], Substitute.For<IDataCacheProvider>(), options, Substitute.For<IHttpCacheInvalidationSink>(), [],
            NullLogger<DomainSettingsInvalidationCoordinator>.Instance);
        Task<DomainSettingsInvalidationPlan> first = Task.Run(() => coordinator.ApplyPatch(
            "products", Values(("dataCache.ttlSeconds", 300)), state, [], true), TestContext.Current.CancellationToken);
        Task<DomainSettingsInvalidationPlan>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            second = Task.Run(() => coordinator.ApplyPatch(
                "products", Values(("dataCache.ttlSeconds", 600)), state, [], true), TestContext.Current.CancellationToken);
            state.Get("products")!.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(300));
            release.Set();
            (await first).RemainingTargets.Should().Be(DomainSettingsInvalidationTargets.DataCache);
            (await second).RemainingTargets.Should().Be(DomainSettingsInvalidationTargets.None);
        }
        finally
        {
            release.Set();
            await first;
            if (second is not null)
                await second;
        }
    }

    private sealed class PausingStateValues(
        IDomainRuntimeOverrideStore state,
        TaskCompletionSource entered,
        ManualResetEventSlim release) : IDomainSettingValueProvider
    {
        private int _paused;
        public bool TryGetValue(string domain, string settingId, out JsonElement value)
        {
            int seconds = (int)(state.Get(domain)?.DataCacheTtl?.TotalSeconds ?? 600);
            if (seconds == 300 && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                entered.SetResult();
                release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();
            }
            value = JsonSerializer.SerializeToElement(seconds);
            return settingId == "dataCache.ttlSeconds";
        }
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
