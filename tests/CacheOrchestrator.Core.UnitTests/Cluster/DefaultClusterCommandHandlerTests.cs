using CacheOrchestrator.Admin;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Core.UnitTests.Cluster;

public class DefaultClusterCommandHandlerTests
{
    private readonly ICacheOrchestratorInvalidator _invalidator = Substitute.For<ICacheOrchestratorInvalidator>();
    private readonly IDomainRuntimeOverrideStore _overrides = Substitute.For<IDomainRuntimeOverrideStore>();
    private readonly IInstanceIdProvider _instanceId = Substitute.For<IInstanceIdProvider>();
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options = Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
    private readonly IOptionsMonitor<ClusterCommandHandlingOptions> _clusterOptions =
        new FixedOptionsMonitor<ClusterCommandHandlingOptions>(
            new ClusterCommandHandlingOptions { DedupeWindowSeconds = 60 });
    private readonly ClusterCommandDedupeStore _dedupe;
    private readonly DefaultClusterCommandHandler _sut;

    public DefaultClusterCommandHandlerTests()
    {
        _instanceId.InstanceId.Returns("local-1");
        _options.CurrentValue.Returns(new CacheOrchestratorOptions
        {
            Namespace = "app1"
        });
        _dedupe = new ClusterCommandDedupeStore(_clusterOptions);
        _sut = new DefaultClusterCommandHandler(
            _invalidator,
            _overrides,
            _instanceId,
            _options,
            _dedupe,
            NullLogger<DefaultClusterCommandHandler>.Instance);
    }

    [Fact]
    public async Task ApplyLocalAsync_WhenNamespaceMismatch_DoesNotInvalidate()
    {
        InvalidateCommand cmd = CreateInvalidate(ns: "other", origin: "remote-2");

        await _sut.ApplyLocalAsync(cmd, TestContext.Current.CancellationToken);

        await _invalidator.DidNotReceive()
            .InvalidateDomainAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyLocalAsync_WhenOriginIsSelf_DoesNotInvalidate()
    {
        InvalidateCommand cmd = CreateInvalidate(ns: "app1", origin: "local-1");

        await _sut.ApplyLocalAsync(cmd, TestContext.Current.CancellationToken);

        await _invalidator.DidNotReceive()
            .InvalidateDomainAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyLocalAsync_Domain_CallsInvalidatorUnderRemoteScope()
    {
        InvalidateCommand cmd = CreateInvalidate(ns: "app1", origin: "remote-2") with
        {
            Kind = CacheInvalidationKind.Domain,
            Domain = "products",
            Scope = "products",
            Tags = ["domain:products"]
        };

        bool sawRemote = false;
        _invalidator
            .InvalidateDomainAsync("products", Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                sawRemote = ClusterCommandScope.IsRemote;
                return ValueTask.FromResult(
                    new CacheInvalidationResult("products", ["domain:products"], true, true, []));
            });

        await _sut.ApplyLocalAsync(cmd, TestContext.Current.CancellationToken);

        sawRemote.Should().BeTrue();
        await _invalidator.Received(1).InvalidateDomainAsync("products", Arg.Any<CancellationToken>());
        ClusterCommandScope.IsRemote.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyLocalAsync_VersionBump_SetsRuntimeOverride()
    {
        VersionBumpCommand cmd = new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "remote-2",
            Namespace = "app1",
            TimestampUtc = DateTimeOffset.UtcNow,
            Domain = "catalog",
            Version = "v-remote"
        };

        await _sut.ApplyLocalAsync(cmd, TestContext.Current.CancellationToken);

        _overrides.Received(1).SetVersion("catalog", "v-remote");
    }

    [Fact]
    public async Task ApplyLocalAsync_DuplicateCommandId_IsIgnored()
    {
        var id = Guid.NewGuid();
        InvalidateCommand cmd = CreateInvalidate(ns: "app1", origin: "remote-2") with { CommandId = id };

        _invalidator
            .InvalidateDomainAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(
                new CacheInvalidationResult("products", ["domain:products"], true, true, [])));

        await _sut.ApplyLocalAsync(cmd, TestContext.Current.CancellationToken);
        await _sut.ApplyLocalAsync(cmd, TestContext.Current.CancellationToken);

        await _invalidator.Received(1).InvalidateDomainAsync("products", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyLocalAsync_UndefinedInvalidationKind_IsRejected()
    {
        InvalidateCommand command = CreateInvalidate("app1", "remote") with { Kind = (CacheInvalidationKind)999 };
        ClusterCommandResult result = await _sut.ApplyLocalAsync(command, TestContext.Current.CancellationToken);
        result.Status.Should().Be(ClusterCommandStatus.Rejected);
        await _invalidator.DidNotReceive().InvalidateTagsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyLocalAsync_ThrownFailure_DoesNotSuppressRetry()
    {
        int attempts = 0;
        _invalidator.InvalidateDomainAsync("products", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++attempts == 1)
                throw new IOException("Unavailable");
            return ValueTask.FromResult(new CacheInvalidationResult("products", ["domain:products"], true, true));
        });
        InvalidateCommand command = CreateInvalidate("app1", "remote");
        (await _sut.ApplyLocalAsync(command, TestContext.Current.CancellationToken)).Status.Should().Be(ClusterCommandStatus.Failed);
        (await _sut.ApplyLocalAsync(command, TestContext.Current.CancellationToken)).Status.Should().Be(ClusterCommandStatus.Applied);
        attempts.Should().Be(2);
    }

    private static InvalidateCommand CreateInvalidate(string ns, string origin) => new()
    {
        CommandId = Guid.NewGuid(),
        OriginInstanceId = origin,
        Namespace = ns,
        TimestampUtc = DateTimeOffset.UtcNow,
        Kind = CacheInvalidationKind.Domain,
        Scope = "products",
        Tags = ["domain:products"],
        Domain = "products"
    };
}
