using CacheOrchestrator.Cluster;
using CacheOrchestrator.HttpBus;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace CacheOrchestrator.HttpBus.UnitTests;

public class HttpClusterCommandBusTests
{
    [Fact]
    public void BuildApplyUri_CombinesBaseAndPrefix()
    {
        Uri uri = HttpClusterCommandBus.BuildApplyUri(
            new Uri("http://10.0.0.1:8080/"),
            "/cache-admin/local");

        uri.ToString().Should().Be("http://10.0.0.1:8080/cache-admin/local/cluster/apply");
    }

    [Fact]
    public async Task PublishAsync_SkipsSelfAndPostsToPeers()
    {
        List<HttpRequestMessage> seen = [];
        RecordingHandler handler = new(seen, HttpStatusCode.OK);

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClusterCommandBus.HttpClientName)
            .Returns(new HttpClient(handler) { BaseAddress = null });

        IClusterMembership membership = Substitute.For<IClusterMembership>();
        membership.Kind.Returns("Static");
        membership.GetPeersAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ClusterPeer("a", new Uri("http://127.0.0.1:5001")),
            new ClusterPeer("b", new Uri("http://127.0.0.1:5002"))
        ]);

        IInstanceIdProvider instanceId = Substitute.For<IInstanceIdProvider>();
        instanceId.InstanceId.Returns("a");

        IOptionsMonitor<HttpBusOptions> options = new FixedOptionsMonitor<HttpBusOptions>(new HttpBusOptions
        {
            Admin = { RoutePrefix = "/cache-admin/local", ApiKey = "k" },
            Cluster =
            {
                Bus =
                {
                    Enabled = true,
                    PeerTimeoutMs = 2000,
                    MaxParallelism = 8,
                    Membership = "Static"
                }
            }
        });

        HttpClusterCommandBus bus = new(
            factory,
            membership,
            instanceId,
            options,
            NullLogger<HttpClusterCommandBus>.Instance);

        InvalidateCommand command = new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "a",
            Namespace = "app1",
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = CacheInvalidationKind.Domain,
            Scope = "products",
            Tags = ["domain:products"],
            Domain = "products"
        };

        ClusterPublishResult published = await bus.PublishAsync(command, TestContext.Current.CancellationToken);

        published.AllSucceeded.Should().BeTrue();
        published.Peers.Should().ContainSingle(p => p.PeerId == "b" && p.Succeeded);
        seen.Should().ContainSingle();
        seen[0].Method.Should().Be(HttpMethod.Post);
        seen[0].RequestUri!.ToString().Should().Be("http://127.0.0.1:5002/cache-admin/local/cluster/apply");
        seen[0].Headers.Contains(ClusterEndpointAuth.HeaderName).Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_PeerHttpFailure_ReportsIncomplete()
    {
        List<HttpRequestMessage> seen = [];
        RecordingHandler handler = new(seen, HttpStatusCode.ServiceUnavailable);

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClusterCommandBus.HttpClientName)
            .Returns(new HttpClient(handler) { BaseAddress = null });

        IClusterMembership membership = Substitute.For<IClusterMembership>();
        membership.Kind.Returns("Static");
        membership.GetPeersAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ClusterPeer("a", new Uri("http://127.0.0.1:5001")),
            new ClusterPeer("b", new Uri("http://127.0.0.1:5002"))
        ]);

        IInstanceIdProvider instanceId = Substitute.For<IInstanceIdProvider>();
        instanceId.InstanceId.Returns("a");

        IOptionsMonitor<HttpBusOptions> options = new FixedOptionsMonitor<HttpBusOptions>(new HttpBusOptions
        {
            Admin = { RoutePrefix = "/cache-admin/local", ApiKey = "k" },
            Cluster =
            {
                Bus =
                {
                    Enabled = true,
                    PeerTimeoutMs = 2000,
                    MaxParallelism = 8,
                    Membership = "Static"
                }
            }
        });

        HttpClusterCommandBus bus = new(
            factory,
            membership,
            instanceId,
            options,
            NullLogger<HttpClusterCommandBus>.Instance);

        InvalidateCommand command = new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "a",
            Namespace = "app1",
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = CacheInvalidationKind.Domain,
            Scope = "products",
            Tags = ["domain:products"],
            Domain = "products"
        };

        ClusterPublishResult published = await bus.PublishAsync(command, TestContext.Current.CancellationToken);

        published.AllSucceeded.Should().BeFalse();
        published.Peers.Should().ContainSingle(p => p.PeerId == "b" && !p.Succeeded && p.Error != null);
    }

    [Fact]
    public async Task PublishAsync_WhenDisabled_ReturnsEmptyWithoutCallingMembership()
    {
        IClusterMembership membership = Substitute.For<IClusterMembership>();
        HttpClusterCommandBus bus = CreateBus(
            membership,
            instanceId: "a",
            enabled: false,
            new RecordingHandler([], HttpStatusCode.OK));

        ClusterPublishResult published = await bus.PublishAsync(CreateCommand("a"), TestContext.Current.CancellationToken);

        published.Peers.Should().BeEmpty();
        await membership.DidNotReceive().GetPeersAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_WhenOnlySelf_ReturnsEmpty()
    {
        IClusterMembership membership = Substitute.For<IClusterMembership>();
        membership.GetPeersAsync(Arg.Any<CancellationToken>()).Returns(
            [new ClusterPeer("a", new Uri("http://127.0.0.1:5001"))]);

        HttpClusterCommandBus bus = CreateBus(
            membership,
            instanceId: "a",
            enabled: true,
            new RecordingHandler([], HttpStatusCode.OK));

        ClusterPublishResult published = await bus.PublishAsync(CreateCommand("a"), TestContext.Current.CancellationToken);
        published.Peers.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenTransportFails_ReportsError()
    {
        IClusterMembership membership = Substitute.For<IClusterMembership>();
        membership.GetPeersAsync(Arg.Any<CancellationToken>()).Returns(
            [new ClusterPeer("b", new Uri("http://127.0.0.1:5002"))]);

        HttpClusterCommandBus bus = CreateBus(
            membership,
            instanceId: "a",
            enabled: true,
            new ThrowingHandler());

        ClusterPublishResult published = await bus.PublishAsync(CreateCommand("a"), TestContext.Current.CancellationToken);

        published.AllSucceeded.Should().BeFalse();
        published.Peers.Should().ContainSingle(p => p.PeerId == "b" && !p.Succeeded && p.Error == "boom");
    }

    [Fact]
    public async Task PublishAsync_WhenCallerCancels_PropagatesCancellation()
    {
        IClusterMembership membership = Substitute.For<IClusterMembership>();
        membership.GetPeersAsync(Arg.Any<CancellationToken>()).Returns(
            [new ClusterPeer("b", new Uri("http://127.0.0.1:5002"))]);
        var handler = new WaitingHandler();
        HttpClusterCommandBus bus = CreateBus(membership, "a", true, handler);
        using var canceled = new CancellationTokenSource();
        Task<ClusterPublishResult> pending = bus.PublishAsync(CreateCommand("a"), canceled.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        canceled.Cancel();
        Func<Task> wait = async () => await pending;
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation should end the request.");
        }
    }

    [Fact]
    public async Task PublishAsync_WhenCommandIsNull_Throws()
    {
        HttpClusterCommandBus bus = CreateBus(
            Substitute.For<IClusterMembership>(),
            "a",
            enabled: true,
            new RecordingHandler([], HttpStatusCode.OK));

        Func<Task<ClusterPublishResult>> act = () => bus.PublishAsync(null!, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void IsEnabled_FollowsOptions()
    {
        HttpClusterCommandBus bus = CreateBus(
            Substitute.For<IClusterMembership>(),
            "a",
            enabled: true,
            new RecordingHandler([], HttpStatusCode.OK));
        bus.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "/cache-admin/local")]
    [InlineData("", "/cache-admin/local")]
    [InlineData("   ", "/cache-admin/local")]
    [InlineData("/custom/", "/custom")]
    [InlineData("no-slash", "no-slash")]
    public void ResolveRoutePrefix_UsesAdminPrefixOrDefault(string? prefix, string expected)
    {
        HttpBusOptions options = new();
        options.Admin.RoutePrefix = prefix!;
        HttpClusterCommandBus.ResolveRoutePrefix(options).Should().Be(expected);
    }

    [Fact]
    public void ResolveApiKey_PrefersBusKeyOverAdmin()
    {
        HttpBusOptions options = new();
        options.Admin.ApiKey = "admin";
        options.Cluster.Bus.ApiKey = "bus";
        HttpClusterCommandBus.ResolveApiKey(options).Should().Be("bus");
    }

    [Fact]
    public void ResolveApiKey_FallsBackToAdminThenNull()
    {
        HttpBusOptions adminOnly = new();
        adminOnly.Admin.ApiKey = "admin";
        HttpClusterCommandBus.ResolveApiKey(adminOnly).Should().Be("admin");

        HttpClusterCommandBus.ResolveApiKey(new HttpBusOptions()).Should().BeNull();
    }

    [Fact]
    public void BuildApplyUri_AddsLeadingSlashWhenMissing()
    {
        Uri uri = HttpClusterCommandBus.BuildApplyUri(new Uri("http://10.0.0.1:8080"), "cache-admin/local");
        uri.ToString().Should().Be("http://10.0.0.1:8080/cache-admin/local/cluster/apply");
    }

    private static HttpClusterCommandBus CreateBus(
        IClusterMembership membership,
        string instanceId,
        bool enabled,
        HttpMessageHandler handler)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClusterCommandBus.HttpClientName)
            .Returns(new HttpClient(handler) { BaseAddress = null });

        IInstanceIdProvider instance = Substitute.For<IInstanceIdProvider>();
        instance.InstanceId.Returns(instanceId);

        IOptionsMonitor<HttpBusOptions> options = new FixedOptionsMonitor<HttpBusOptions>(new HttpBusOptions
        {
            Admin = { RoutePrefix = "/cache-admin/local", ApiKey = "k" },
            Cluster = { Bus = { Enabled = enabled, PeerTimeoutMs = 2000, MaxParallelism = 8 } }
        });

        return new HttpClusterCommandBus(
            factory,
            membership,
            instance,
            options,
            NullLogger<HttpClusterCommandBus>.Instance);
    }

    private static InvalidateCommand CreateCommand(string origin) => new()
    {
        CommandId = Guid.NewGuid(),
        OriginInstanceId = origin,
        Namespace = "app1",
        TimestampUtc = DateTimeOffset.UtcNow,
        Kind = CacheInvalidationKind.Domain,
        Scope = "products",
        Tags = ["domain:products"],
        Domain = "products"
    };

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("boom");
    }

    private sealed class RecordingHandler(List<HttpRequestMessage> seen, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
