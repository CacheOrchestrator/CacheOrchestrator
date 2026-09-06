using CacheOrchestrator.Admin;
using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Responses;
using CacheOrchestrator.Edge.Tags;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Edge.UnitTests;

public class EdgeIntegrationTests
{
    [Fact]
    public async Task ResponseContributor_ProjectsTagsAndFreshness()
    {
        TestProvider provider = new();
        (EdgeResponseContributor sut, _, _) = Create(provider);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions { CoreOptions = new DomainCacheOptions { Domain = "catalog" } },
            sharedCacheEligible: true,
            ["domain:catalog", "entity:catalog:products:42"]);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata.Should().NotBeNull();
        provider.Metadata!.IsCacheable.Should().BeTrue();
        provider.Metadata.Ttl.Should().Be(TimeSpan.FromMinutes(10));
        provider.Metadata.StaleWhileRevalidate.Should().Be(TimeSpan.FromSeconds(30));
        provider.Metadata.Tags.Should().HaveCount(2).And.OnlyContain(tag => tag.StartsWith("coe1-"));
    }

    [Theory]
    [InlineData(601, 60, 600)]
    [InlineData(300, 60, 300)]
    [InlineData(30, 60, 60)]
    [InlineData(-1, 60, 60)]
    [InlineData(-1, 700, 600)]
    [InlineData(-1, 0, 0)]
    public async Task ResponseContributor_ClientScheduleControlsEdgeTtl(
        int secondsUntilUpdate,
        int minTtlSeconds,
        int expectedEdgeTtlSeconds)
    {
        DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TestProvider provider = new();
        (EdgeResponseContributor sut, _, _) = Create(provider, new FixedTimeProvider(now));
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions
            {
                CoreOptions = new DomainCacheOptions { Domain = "catalog" },
                ClientTtlSeconds = 3600,
                ClientTtlMinSeconds = minTtlSeconds,
                ScheduledUpdateUtc = now.AddSeconds(secondsUntilUpdate)
            },
            sharedCacheEligible: true,
            ["domain:catalog"]);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata!.Ttl.Should().Be(TimeSpan.FromSeconds(expectedEdgeTtlSeconds));
    }

    [Fact]
    public async Task ResponseContributor_ClientScheduleDisabled_RetainsConfiguredEdgeTtl()
    {
        DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TestProvider provider = new();
        (EdgeResponseContributor sut, _, _) = Create(provider, new FixedTimeProvider(now));
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions
            {
                CoreOptions = new DomainCacheOptions { Domain = "catalog" },
                ClientTtlSeconds = 0,
                ClientTtlMinSeconds = 0,
                ScheduledUpdateUtc = now.AddSeconds(30)
            },
            sharedCacheEligible: true,
            ["domain:catalog"]);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata!.Ttl.Should().Be(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public async Task ResponseContributor_WhenProviderBudgetExceeded_DisablesSharedCaching()
    {
        TestProvider provider = new(maxResponseTagBytes: 1);
        (EdgeResponseContributor sut, _, _) = Create(provider);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions { CoreOptions = new DomainCacheOptions { Domain = "catalog" } },
            sharedCacheEligible: true,
            ["domain:catalog", "entity:catalog:products:42"]);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata!.IsCacheable.Should().BeFalse();
        provider.Metadata.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task ResponseContributor_OnOutputCacheHit_RebuildsProviderHeadersFromStoredTags()
    {
        TestProvider provider = new();
        (EdgeResponseContributor sut, _, _) = Create(provider);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions { CoreOptions = new DomainCacheOptions { Domain = "catalog" } },
            sharedCacheEligible: true,
            ["domain:catalog"],
            OutputCacheResult.Hit);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata.Should().NotBeNull();
        provider.Metadata!.Tags.Should().ContainSingle();
    }

    [Fact]
    public async Task ResponseContributor_HeadRequest_RemainsEdgeCacheable()
    {
        TestProvider provider = new();
        (EdgeResponseContributor sut, _, _) = Create(provider);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Head;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions { CoreOptions = new DomainCacheOptions { Domain = "catalog" } },
            sharedCacheEligible: true,
            ["domain:catalog"]);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata.Should().NotBeNull();
        provider.Metadata!.IsCacheable.Should().BeTrue();
        provider.Metadata.Tags.Should().ContainSingle();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task ResponseContributor_NonGetOrHeadRequest_DisablesEdgeStorage(string method)
    {
        TestProvider provider = new();
        (EdgeResponseContributor sut, _, _) = Create(provider);
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        var context = new CacheResponseContext(
            http,
            new DomainHttpCacheOptions { CoreOptions = new DomainCacheOptions { Domain = "catalog" } },
            sharedCacheEligible: true,
            ["domain:catalog"]);

        await sut.ContributeAsync(context, TestContext.Current.CancellationToken);

        provider.Metadata.Should().NotBeNull();
        provider.Metadata!.IsCacheable.Should().BeFalse();
        provider.Metadata.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidationObserver_LocalEntity_EnqueuesProjectedTag()
    {
        TestProvider provider = new();
        (_, EdgeInvalidationObserver sut, RecordingQueue queue) = Create(provider);
        var context = new CacheInvalidationContext(
            CacheInvalidationKind.Entity,
            "catalog/products/42",
            ["entity:catalog:products:42"],
            CacheInvalidationOrigin.Local);
        var result = new CacheInvalidationResult("catalog/products/42", context.Tags, true, true);

        await sut.OnAfterInvalidateAsync(context, result, TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        queue.Jobs[0].Tags.Should().ContainSingle().Which.Should().StartWith("coe1-");
    }

    [Fact]
    public async Task InvalidationObserver_RemoteCluster_DoesNotEnqueue()
    {
        TestProvider provider = new();
        (_, EdgeInvalidationObserver sut, RecordingQueue queue) = Create(provider);
        var context = new CacheInvalidationContext(
            CacheInvalidationKind.Domain,
            "catalog",
            ["domain:catalog"],
            CacheInvalidationOrigin.RemoteCluster);
        var result = new CacheInvalidationResult("catalog", context.Tags, true, true);

        await sut.OnAfterInvalidateAsync(context, result, TestContext.Current.CancellationToken);

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task VersionChangeObserver_EdgeEnabled_EnqueuesProjectedDomainTag()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.OnDomainVersionChangedAsync("catalog", "v2", TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        queue.Jobs[0].Tags.Should().Equal(
            new EdgeTagProjector().Project("test", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task VersionChangeObserver_EdgeDisabled_DoesNotEnqueue()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: false);

        await sut.OnDomainVersionChangedAsync("catalog", "v2", TestContext.Current.CancellationToken);

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task SettingsChangeObserver_EdgeEnabled_EnqueuesProjectedDomainTag()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.InvalidateDomainSettingsAsync("catalog", TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        queue.Jobs[0].Tags.Should().Equal(
            new EdgeTagProjector().Project("test", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task ConfigurationChange_VersionChanged_EnqueuesCurrentPlacement()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.ApplyChangesAsync(
            Snapshot(State(version: "v1")),
            Snapshot(State(version: "v2")),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        queue.Jobs[0].Tags.Should().Equal(
            new EdgeTagProjector().Project("new-ns", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task ConfigurationChange_EdgeDisabled_EnqueuesPreviousPlacement()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.ApplyChangesAsync(
            Snapshot(State(version: "v1", tagNamespace: "old-ns")),
            Snapshot(State(version: "v1", enabled: false)),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        queue.Jobs[0].Tags.Should().Equal(
            new EdgeTagProjector().Project("old-ns", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task ConfigurationChange_EdgeEnabled_EnqueuesNewPlacement()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.ApplyChangesAsync(
            Snapshot(State(version: "v1", enabled: false)),
            Snapshot(State(version: "v1", enabled: true)),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        queue.Jobs[0].Tags.Should().Equal(
            new EdgeTagProjector().Project("new-ns", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task ConfigurationChange_EdgeSafetyPolicyChanged_EnqueuesCurrentPlacement()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);
        EdgeDomainSnapshot previous = State(version: "v1") with
        {
            EdgeSafetyValues = SafetyValues(("varyByHeaders", new[] { "X-Tenant" }))
        };
        EdgeDomainSnapshot current = State(version: "v1") with
        {
            EdgeSafetyValues = SafetyValues(("varyByHeaders", new[] { "X-Tenant", "X-Locale" }))
        };

        await sut.ApplyChangesAsync(
            Snapshot(previous),
            Snapshot(current),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task ConfigurationChange_PlacementAndVersionChanged_QueuesOldAndNewPlacement()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.ApplyChangesAsync(
            Snapshot(State(version: "v1", instanceName: "old", tagNamespace: "old-ns")),
            Snapshot(State(version: "v2", instanceName: "new", tagNamespace: "new-ns")),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().HaveCount(2);
        queue.Jobs.Select(job => job.InstanceName).Should().Equal("old", "new");
    }

    [Fact]
    public async Task ConfigurationChange_VersionChangedWhileEdgeDisabled_DoesNotEnqueue()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);

        await sut.ApplyChangesAsync(
            Snapshot(State(version: "v1", enabled: false)),
            Snapshot(State(version: "v2", enabled: false)),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigurationChange_ConfigVersionChangedUnderRuntimeOverride_DoesNotEnqueue()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);
        EdgeDomainSnapshot previous = State(version: "v1");
        EdgeDomainSnapshot current = State(version: "runtime-v2") with { VersionIsRuntimeOverride = true };

        await sut.ApplyChangesAsync(
            Snapshot(previous),
            Snapshot(current),
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeOnStartup_EnqueuesOnlyEnabledOptedInDomains()
    {
        TestProvider provider = new();
        (EdgeDomainChangeMonitor sut, RecordingQueue queue) = CreateVersionObserver(provider, enabled: true);
        var snapshot = new Dictionary<string, EdgeDomainSnapshot>(StringComparer.Ordinal)
        {
            ["catalog"] = State(version: "v1", purgeOnStartup: true),
            ["disabled"] = State("disabled", "v1", enabled: false, purgeOnStartup: true),
            ["optout"] = State("optout", "v1")
        };

        await sut.PurgeOnStartupAsync(snapshot, TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
    }

    private static (EdgeDomainChangeMonitor Observer, RecordingQueue Queue) CreateVersionObserver(
        TestProvider provider,
        bool enabled)
    {
        CacheOrchestratorEdgeOptions edgeOptions = CreateEdgeOptions(provider, enabled);
        IOptionsMonitor<CacheOrchestratorEdgeOptions> edgeMonitor = Substitute.For<IOptionsMonitor<CacheOrchestratorEdgeOptions>>();
        edgeMonitor.CurrentValue.Returns(edgeOptions);
        IOptionsMonitor<CacheOrchestratorOptions> coreMonitor = Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
        coreMonitor.CurrentValue.Returns(new CacheOrchestratorOptions { Namespace = "app" });
        var instances = new EdgeInstanceResolver(edgeMonitor, coreMonitor, new EdgeProviderCatalog([provider], [provider]));
        var queue = new RecordingQueue();
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        return (
            new EdgeDomainChangeMonitor(
                new EdgeConfigurationRegistration(new ConfigurationBuilder().Build(), "Cache"),
                services,
                new DomainEdgeOptionsProvider(edgeMonitor),
                instances,
                new EdgeTagProjector(),
                queue,
                NullLogger<EdgeDomainChangeMonitor>.Instance),
            queue);
    }

    private static IReadOnlyDictionary<string, EdgeDomainSnapshot> Snapshot(EdgeDomainSnapshot state) =>
        new Dictionary<string, EdgeDomainSnapshot>(StringComparer.Ordinal) { [state.Domain] = state };

    private static EdgeDomainSnapshot State(
        string domain = "catalog",
        string version = "v1",
        bool enabled = true,
        bool purgeOnStartup = false,
        string instanceName = "edge",
        string tagNamespace = "new-ns") =>
        new(
            domain,
            version,
            VersionIsRuntimeOverride: false,
            enabled,
            purgeOnStartup,
            instanceName,
            "Test",
            tagNamespace);

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> SafetyValues(
        params (string Id, object Value)[] entries) =>
        entries.ToDictionary(
            static entry => entry.Id,
            static entry => System.Text.Json.JsonSerializer.SerializeToElement(entry.Value),
            StringComparer.OrdinalIgnoreCase);

    private static (EdgeResponseContributor Response, EdgeInvalidationObserver Observer, RecordingQueue Queue) Create(
        TestProvider provider,
        TimeProvider? timeProvider = null)
    {
        CacheOrchestratorEdgeOptions edgeOptions = CreateEdgeOptions(provider, enabled: true);
        IOptionsMonitor<CacheOrchestratorEdgeOptions> edgeMonitor = Substitute.For<IOptionsMonitor<CacheOrchestratorEdgeOptions>>();
        edgeMonitor.CurrentValue.Returns(edgeOptions);
        IOptionsMonitor<CacheOrchestratorOptions> coreMonitor = Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
        coreMonitor.CurrentValue.Returns(new CacheOrchestratorOptions { Namespace = "app" });
        var catalog = new EdgeProviderCatalog([provider], [provider]);
        var instances = new EdgeInstanceResolver(edgeMonitor, coreMonitor, catalog);
        var domainOptions = new DomainEdgeOptionsProvider(edgeMonitor);
        var projector = new EdgeTagProjector();
        var queue = new RecordingQueue();
        return (
            new EdgeResponseContributor(
                domainOptions,
                instances,
                projector,
                NullLogger<EdgeResponseContributor>.Instance,
                timeProvider),
            new EdgeInvalidationObserver(domainOptions, instances, projector, queue),
            queue);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static CacheOrchestratorEdgeOptions CreateEdgeOptions(TestProvider provider, bool enabled) =>
        new()
        {
            EdgeInstances = new Dictionary<string, EdgeInstanceOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new() { Provider = provider.Name, Namespace = "test" }
            },
            Domains = new Dictionary<string, EdgeDomainContainer>(StringComparer.OrdinalIgnoreCase)
            {
                ["catalog"] = new()
                {
                    Edge = new DomainEdgeSettings
                    {
                        Enabled = enabled,
                        Instance = "edge",
                        TtlSeconds = 600,
                        StaleWhileRevalidateSeconds = 30
                    }
                }
            }
        };

    private sealed class RecordingQueue : IEdgeInvalidationQueue
    {
        public List<EdgeInvalidationJob> Jobs { get; } = [];

        public ValueTask EnqueueAsync(EdgeInvalidationJob job, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestProvider(int maxResponseTagBytes = 16 * 1024)
        : IEdgeResponseProvider, IEdgeInvalidationProvider
    {
        public string Name => "Test";
        public EdgeProviderCapabilities Capabilities { get; } = new()
        {
            SupportsTagInvalidation = true,
            MaxResponseTagBytes = maxResponseTagBytes,
            MaxInvalidationBatchSize = 100,
            SupportsStaleWhileRevalidate = true,
            SupportsStaleIfError = true
        };
        public EdgeResponseMetadata? Metadata { get; private set; }
        public void ApplyResponseMetadata(HttpResponse response, EdgeResponseMetadata metadata) => Metadata = metadata;
        public ValueTask<EdgeInvalidationResult> InvalidateAsync(EdgeInvalidationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(EdgeInvalidationResult.Success);
    }
}
