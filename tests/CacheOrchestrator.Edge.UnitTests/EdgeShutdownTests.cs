using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CacheOrchestrator.Edge.UnitTests;

public class EdgeShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StopDuringActiveBatch_CompletesRemainingBatchesAndOtherProvider()
    {
        var first = new Provider("first", blockFirst: true);
        var second = new Provider("second");
        using EdgeInvalidationWorker worker = Worker([first, second], out EdgeInvalidationChannel channel);
        await channel.Channel.Writer.WriteAsync(Job("first", "old", "a", "b"), Ct);
        await channel.Channel.Writer.WriteAsync(Job("second", "new", "c"), Ct);
        await worker.StartAsync(Ct);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task stop = worker.StopAsync(deadline.Token);
        first.ActiveToken.IsCancellationRequested.Should().BeFalse();
        stop.IsCompleted.Should().BeFalse();
        first.Release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        first.Completed.SelectMany(request => request.Tags).Should().Equal("a", "b");
        second.Completed.SelectMany(request => request.Tags).Should().Equal("c");
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task StopDuringCoalescing_DrainsPendingAndUnreadJobs()
    {
        var provider = new Provider("test");
        using EdgeInvalidationWorker worker = Worker([provider], out EdgeInvalidationChannel channel, flushSeconds: 60);
        await channel.Channel.Writer.WriteAsync(Job("test", "target", "a"), Ct);
        await worker.StartAsync(Ct);
        await WaitAsync(() => channel.Channel.Reader.Count == 0);
        await channel.Channel.Writer.WriteAsync(Job("test", "target", "b"), Ct);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(deadline.Token);
        provider.Completed.SelectMany(request => request.Tags).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task SameInstanceAndProvider_WithDifferentTargets_StaySeparate()
    {
        var provider = new Provider("test");
        using EdgeInvalidationWorker worker = Worker([provider], out EdgeInvalidationChannel channel);
        await channel.Channel.Writer.WriteAsync(Job("test", "old", "a"), Ct);
        await channel.Channel.Writer.WriteAsync(Job("test", "new", "b"), Ct);
        await worker.StartAsync(Ct);
        await WaitAsync(() => provider.Completed.Count == 2);
        await worker.StopAsync(Ct);
        provider.Completed.Should().Contain(request => request.Target.RoutingKey == "old" && request.Tags.Single() == "a");
        provider.Completed.Should().Contain(request => request.Target.RoutingKey == "new" && request.Tags.Single() == "b");
    }

    [Fact]
    public async Task ShutdownDeadline_CancelsProvider_AndReportsUndeliveredWork()
    {
        var provider = new Provider("test", blockFirst: true);
        var logger = new RecordingLogger();
        using EdgeInvalidationWorker worker = Worker([provider], out EdgeInvalidationChannel channel, logger: logger);
        await channel.Channel.Writer.WriteAsync(Job("test", "target", "a", "b"), Ct);
        await worker.StartAsync(Ct);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        using var deadline = new CancellationTokenSource();
        Task stop = worker.StopAsync(deadline.Token);
        deadline.Cancel();
        await stop.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        provider.ActiveToken.IsCancellationRequested.Should().BeTrue();
        provider.Completed.Should().BeEmpty();
        logger.Messages.Should().Contain(message => message.Contains("2 undelivered tags", StringComparison.Ordinal));
    }

    private static EdgeInvalidationJob Job(string provider, string route, params string[] tags) =>
        new(new EdgeInvalidationTarget("same-instance", provider, route, new Dictionary<string, string> { ["route"] = route }), tags);

    private static EdgeInvalidationWorker Worker(
        IEdgeInvalidationProvider[] providers, out EdgeInvalidationChannel channel,
        int flushSeconds = 0, ILogger<EdgeInvalidationWorker>? logger = null)
    {
        channel = new EdgeInvalidationChannel(16);
        IOptionsMonitor<CacheOrchestratorEdgeOptions> options = Substitute.For<IOptionsMonitor<CacheOrchestratorEdgeOptions>>();
        options.CurrentValue.Returns(new CacheOrchestratorEdgeOptions
        {
            EdgeQueue = new() { FlushIntervalSeconds = flushSeconds, MaxAttempts = 2, RetryBaseDelaySeconds = 0 }
        });
        return new(channel, new EdgeProviderCatalog([], providers), options,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EdgeInvalidationWorker>.Instance);
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Provider(string name, bool blockFirst = false) : IEdgeInvalidationProvider
    {
        private int _calls;
        public string Name => name;
        public EdgeProviderCapabilities Capabilities { get; } = new() { SupportsTagInvalidation = true, MaxInvalidationBatchSize = 1 };
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ActiveToken { get; private set; }
        public ConcurrentQueue<EdgeInvalidationRequest> Completed { get; } = new();
        public EdgeInvalidationTarget CaptureTarget(string instanceName, IConfigurationSection instanceConfiguration) =>
            new(instanceName, Name, instanceName, new Dictionary<string, string>());
        public async ValueTask<EdgeInvalidationResult> InvalidateAsync(EdgeInvalidationRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1 && blockFirst)
            {
                ActiveToken = cancellationToken;
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            Completed.Enqueue(request);
            return EdgeInvalidationResult.Success;
        }
    }

    private sealed class RecordingLogger : ILogger<EdgeInvalidationWorker>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
