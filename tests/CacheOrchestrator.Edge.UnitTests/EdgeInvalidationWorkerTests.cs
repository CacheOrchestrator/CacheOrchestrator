using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CacheOrchestrator.Edge.UnitTests;

public class EdgeInvalidationWorkerTests
{
    [Fact]
    public async Task Worker_CoalescesDeduplicatesAndSplitsProviderBatches()
    {
        var provider = new RecordingProvider(maxBatchSize: 2);
        (EdgeInvalidationWorker sut, EdgeInvalidationChannel channel) = CreateWorker(provider, flushIntervalSeconds: 1);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await channel.Channel.Writer.WriteAsync(
            new EdgeInvalidationJob(new EdgeInvalidationTarget("edge", provider.Name, "edge", new Dictionary<string, string>()), ["a", "b"]),
            TestContext.Current.CancellationToken);
        await channel.Channel.Writer.WriteAsync(
            new EdgeInvalidationJob(new EdgeInvalidationTarget("edge", provider.Name, "edge", new Dictionary<string, string>()), ["b", "c"]),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => provider.Requests.Count == 2);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        provider.Requests.SelectMany(request => request.Tags).Should()
            .BeEquivalentTo(["a", "b", "c"]);
        provider.Requests.Should().OnlyContain(request => request.Tags.Count <= 2);
    }

    [Fact]
    public async Task Worker_RetriesTransientProviderFailure()
    {
        var provider = new RecordingProvider(
            maxBatchSize: 100,
            results:
            [
                new EdgeInvalidationResult { IsTransient = true, RetryAfter = TimeSpan.Zero },
                EdgeInvalidationResult.Success
            ]);
        (EdgeInvalidationWorker sut, EdgeInvalidationChannel channel) = CreateWorker(provider, flushIntervalSeconds: 0);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await channel.Channel.Writer.WriteAsync(
            new EdgeInvalidationJob(new EdgeInvalidationTarget("edge", provider.Name, "edge", new Dictionary<string, string>()), ["a"]),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => provider.Requests.Count == 2);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        provider.Requests.Should().HaveCount(2);
    }

    private static (EdgeInvalidationWorker Worker, EdgeInvalidationChannel Channel) CreateWorker(
        RecordingProvider provider,
        int flushIntervalSeconds)
    {
        var channel = new EdgeInvalidationChannel(16);
        IOptionsMonitor<CacheOrchestratorEdgeOptions> options =
            Substitute.For<IOptionsMonitor<CacheOrchestratorEdgeOptions>>();
        options.CurrentValue.Returns(new CacheOrchestratorEdgeOptions
        {
            EdgeQueue = new EdgeQueueOptions
            {
                FlushIntervalSeconds = flushIntervalSeconds,
                MaxAttempts = 3,
                RetryBaseDelaySeconds = 0
            }
        });
        var worker = new EdgeInvalidationWorker(
            channel,
            new EdgeProviderCatalog([], [provider]),
            options,
            NullLogger<EdgeInvalidationWorker>.Instance);
        return (worker, channel);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        condition().Should().BeTrue("the background purge worker should complete within the test timeout");
    }

    private sealed class RecordingProvider(
        int maxBatchSize,
        IReadOnlyList<EdgeInvalidationResult>? results = null) : IEdgeInvalidationProvider
    {
        private int _resultIndex;

        public string Name => "Test";

        public EdgeProviderCapabilities Capabilities { get; } = new()
        {
            SupportsTagInvalidation = true,
            MaxResponseTagBytes = 16 * 1024,
            MaxInvalidationBatchSize = maxBatchSize
        };

        public ConcurrentQueue<EdgeInvalidationRequest> Requests { get; } = new();

        public EdgeInvalidationTarget CaptureTarget(string instanceName, IConfigurationSection instanceConfiguration) =>
            new(instanceName, Name, instanceName, new Dictionary<string, string>());

        public ValueTask<EdgeInvalidationResult> InvalidateAsync(
            EdgeInvalidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);

            int index = Interlocked.Increment(ref _resultIndex) - 1;
            EdgeInvalidationResult result = results is not null && index < results.Count
                ? results[index]
                : EdgeInvalidationResult.Success;
            return ValueTask.FromResult(result);
        }
    }
}
