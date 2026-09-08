using CacheOrchestrator.Cluster;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Core.UnitTests.Cluster;

public class ClusterCommandDedupeStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ClusterCommandResult Applied = new(ClusterCommandStatus.Applied);

    [Theory]
    [InlineData(120, 1, ClusterCommandStatus.AlreadyApplied)]
    [InlineData(0, 2, ClusterCommandStatus.Applied)]
    public async Task CompletedDelivery_IsDeduplicatedOnlyWhenEnabled(int window, int expectedCalls, ClusterCommandStatus secondStatus)
    {
        var store = Create(window);
        Guid id = Guid.NewGuid();
        int calls = 0;
        Task<ClusterCommandResult> Execute(ClusterCommandExecution _, CancellationToken cancellationToken)
        {
            calls++;
            return Task.FromResult(Applied);
        }
        (await store.ExecuteAsync(id, Execute, Ct)).Status.Should().Be(ClusterCommandStatus.Applied);
        (await store.ExecuteAsync(id, Execute, Ct)).Status.Should().Be(secondStatus);
        calls.Should().Be(expectedCalls);
    }

    [Fact]
    public async Task ConcurrentDeliveries_AwaitOneExecution_EvenAfterWindowExpires()
    {
        var time = new ManualTime();
        var store = Create(30, time);
        Guid id = Guid.NewGuid();
        var release = new TaskCompletionSource<ClusterCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<ClusterCommandResult> Execute(ClusterCommandExecution _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        }
        Task<ClusterCommandResult> owner = store.ExecuteAsync(id, Execute, Ct);
        time.Now = time.Now.AddMinutes(2);
        await store.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.FromResult(Applied), Ct);
        Task<ClusterCommandResult>[] duplicates = Enumerable.Range(0, 32).Select(_ => store.ExecuteAsync(id, Execute, Ct)).ToArray();
        calls.Should().Be(1);
        duplicates.Should().OnlyContain(task => !task.IsCompleted);
        release.SetResult(Applied);
        (await owner).Status.Should().Be(ClusterCommandStatus.Applied);
        (await Task.WhenAll(duplicates)).Should().OnlyContain(result => result.Status == ClusterCommandStatus.AlreadyApplied);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task FailedDelivery_CanResumeAndThenDeduplicatesSuccess()
    {
        var store = Create(120);
        Guid id = Guid.NewGuid();
        int preparations = 0;
        int resumptions = 0;
        Task<ClusterCommandResult> Execute(ClusterCommandExecution execution, CancellationToken cancellationToken)
        {
            preparations++;
            execution.Resume = _ =>
            {
                resumptions++;
                return Task.FromResult(Applied);
            };
            return Task.FromResult(new ClusterCommandResult(ClusterCommandStatus.Failed));
        }
        (await store.ExecuteAsync(id, Execute, Ct)).Succeeded.Should().BeFalse();
        (await store.ExecuteAsync(id, Execute, Ct)).Status.Should().Be(ClusterCommandStatus.Applied);
        (await store.ExecuteAsync(id, Execute, Ct)).Status.Should().Be(ClusterCommandStatus.AlreadyApplied);
        preparations.Should().Be(1);
        resumptions.Should().Be(1);
    }

    [Fact]
    public async Task CanceledWaiter_DoesNotCancelOwner()
    {
        var store = Create(120);
        Guid id = Guid.NewGuid();
        var release = new TaskCompletionSource<ClusterCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ClusterCommandResult> owner = store.ExecuteAsync(id, (_, _) => release.Task, Ct);
        using var canceled = new CancellationTokenSource();
        Task<ClusterCommandResult> waiter = store.ExecuteAsync(id, (_, _) => throw new InvalidOperationException(), canceled.Token);
        canceled.Cancel();
        Func<Task> wait = async () => await waiter;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        owner.IsCompleted.Should().BeFalse();
        release.SetResult(Applied);
        (await owner).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task CanceledOwner_AllowsAnotherDeliveryToRetry()
    {
        var store = Create(120);
        Guid id = Guid.NewGuid();
        int attempts = 0;
        using var canceled = new CancellationTokenSource();
        async Task<ClusterCommandResult> Execute(ClusterCommandExecution _, CancellationToken cancellationToken)
        {
            if (++attempts == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Applied;
        }
        Task<ClusterCommandResult> owner = store.ExecuteAsync(id, Execute, canceled.Token);
        canceled.Cancel();
        Func<Task> wait = async () => await owner;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        (await store.ExecuteAsync(id, Execute, Ct)).Succeeded.Should().BeTrue();
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task CompletedOutcome_ExpiresAndPermitsNewExecution()
    {
        var time = new ManualTime();
        var store = Create(30, time);
        Guid id = Guid.NewGuid();
        int calls = 0;
        Task<ClusterCommandResult> Execute(ClusterCommandExecution _, CancellationToken cancellationToken)
        {
            calls++;
            return Task.FromResult(Applied);
        }
        await store.ExecuteAsync(id, Execute, Ct);
        time.Now = time.Now.AddSeconds(31);
        (await store.ExecuteAsync(id, Execute, Ct)).Status.Should().Be(ClusterCommandStatus.Applied);
        calls.Should().Be(2);
    }

    private static ClusterCommandDedupeStore Create(int window, TimeProvider? time = null) =>
        new(new FixedOptionsMonitor<ClusterCommandHandlingOptions>(new() { DedupeWindowSeconds = window }), time);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
