using CacheOrchestrator.Admin;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.AspNetCore.UnitTests.Admin;

public sealed class CacheOrchestratorManagementTests
{
    [Fact]
    public async Task GetHealthAsync_WhenNoProbes_IsHealthy()
    {
        CacheOrchestratorManagement sut = CreateSut();

        AdminHealthDto health = await sut.GetHealthAsync(TestContext.Current.CancellationToken);

        health.Healthy.Should().BeTrue();
        health.AdminEnabled.Should().BeTrue();
        health.InstanceId.Should().NotBeNullOrWhiteSpace();
        health.DataCacheProvider.Should().Be("Test");
        health.DataCacheCapabilities.SupportsBatchInvalidation.Should().BeFalse();
    }

    [Fact]
    public async Task GetHealthAsync_WhenProbeFails_IsDegraded()
    {
        ICacheOrchestratorHealthProbe probe = Substitute.For<ICacheOrchestratorHealthProbe>();
        probe.Name.Returns("redis");
        probe.ProbeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("redis down")));

        CacheOrchestratorManagement sut = CreateSut(probes: [probe]);

        AdminHealthDto health = await sut.GetHealthAsync(TestContext.Current.CancellationToken);

        health.Healthy.Should().BeFalse();
        health.InstanceId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetHealthAsync_WhenStatsThrow_IsDegraded()
    {
        IAdminStatsCollector stats = Substitute.For<IAdminStatsCollector>();
        stats.GetRawSnapshot().Returns(_ => throw new InvalidOperationException("counters"));

        CacheOrchestratorManagement sut = CreateSut(stats: stats);

        AdminHealthDto health = await sut.GetHealthAsync(TestContext.Current.CancellationToken);

        health.Healthy.Should().BeFalse();
    }

    [Fact]
    public async Task GetHealthAsync_WhenProbeSucceeds_IsHealthy()
    {
        ICacheOrchestratorHealthProbe probe = Substitute.For<ICacheOrchestratorHealthProbe>();
        probe.Name.Returns("redis");
        probe.ProbeAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        CacheOrchestratorManagement sut = CreateSut(probes: [probe]);

        AdminHealthDto health = await sut.GetHealthAsync(TestContext.Current.CancellationToken);

        health.Healthy.Should().BeTrue();
        await probe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetVersionAsync_WhenPeerFails_ReturnsLocalResultAndPublishDetails()
    {
        IClusterCommandBus bus = Substitute.For<IClusterCommandBus>();
        bus.IsEnabled.Returns(true);
        bus.PublishAsync(Arg.Any<ClusterCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ClusterPublishResult(
            [
                new ClusterPeerPublishOutcome
                {
                    PeerId = "peer-2",
                    Succeeded = false,
                    Error = "unreachable"
                }
            ]));

        IDomainRuntimeOverrideStore overrides = new DomainRuntimeOverrideStore();
        IAdminDomainConfigProvider domainConfig = Substitute.For<IAdminDomainConfigProvider>();
        domainConfig.GetDomainConfig("catalog").Returns(call => new AdminDomainConfigDto
        {
            Name = "catalog",
            Version = overrides.Get("catalog")?.Version ?? "1",
            DataCacheInstanceName = "default"
        });
        CacheOrchestratorManagement sut = CreateSut(bus: bus, overrides: overrides, domainConfig: domainConfig);

        AdminDomainMutationResultDto result = await sut.SetVersionAsync(
            "catalog",
            new AdminVersionRequest { Version = "2", Distribute = true },
            TestContext.Current.CancellationToken);

        result.Effective.Version.Should().Be("2");
        result.ClusterPublish.Should().NotBeNull();
        result.ClusterPublish!.AllSucceeded.Should().BeFalse();
        result.ClusterPublish.Failures.Single().PeerId.Should().Be("peer-2");
    }

    [Fact]
    public async Task SetVersionAsync_NotifiesVersionChangeObservers()
    {
        IDomainVersionChangeObserver observer = Substitute.For<IDomainVersionChangeObserver>();
        CacheOrchestratorManagement sut = CreateSut(versionChangeObservers: [observer]);

        await sut.SetVersionAsync(
            " Catalog ",
            new AdminVersionRequest { Version = " v2 " },
            TestContext.Current.CancellationToken);

        await observer.Received(1).OnDomainVersionChangedAsync(
            "catalog",
            "v2",
            Arg.Any<CancellationToken>());
    }

    private static CacheOrchestratorManagement CreateSut(
        IAdminStatsCollector? stats = null,
        IEnumerable<ICacheOrchestratorHealthProbe>? probes = null,
        IClusterCommandBus? bus = null,
        IDomainRuntimeOverrideStore? overrides = null,
        IAdminDomainConfigProvider? domainConfig = null,
        IEnumerable<IDomainVersionChangeObserver>? versionChangeObservers = null)
    {
        if (stats is null)
        {
            stats = Substitute.For<IAdminStatsCollector>();
            stats.GetRawSnapshot().Returns(new AdminLiveStatsRawSnapshot
            {
                InstanceId = "unit",
                CollectedAtUtc = DateTimeOffset.UtcNow,
                Domains = [],
                UnassignedEndpoints = []
            });
        }

        IOptionsMonitor<CacheOrchestratorOptions> options = Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
        options.CurrentValue.Returns(new CacheOrchestratorOptions
        {
            InstanceId = "unit",
            Admin = { Enabled = true }
        });

        IInstanceIdProvider instanceId = Substitute.For<IInstanceIdProvider>();
        instanceId.InstanceId.Returns("unit");

        return new CacheOrchestratorManagement(
            stats,
            Substitute.For<IAdminEndpointCatalog>(),
            domainConfig ?? Substitute.For<IAdminDomainConfigProvider>(),
            overrides ?? Substitute.For<IDomainRuntimeOverrideStore>(),
            options,
            Substitute.For<ICacheOrchestratorInvalidator>(),
            bus ?? Substitute.For<IClusterCommandBus>(),
            Substitute.For<IClusterMembership>(),
            instanceId,
            new ClusterCommandFactory(instanceId, options),
            NullLogger<CacheOrchestratorManagement>.Instance,
            TimeProvider.System,
            probes,
            dataCacheProvider: CreateDataCacheProvider(),
            versionChangeObservers: versionChangeObservers);
    }

    private static IDataCacheProvider CreateDataCacheProvider()
    {
        IDataCacheProvider provider = Substitute.For<IDataCacheProvider>();
        provider.Name.Returns("Test");
        return provider;
    }
}
