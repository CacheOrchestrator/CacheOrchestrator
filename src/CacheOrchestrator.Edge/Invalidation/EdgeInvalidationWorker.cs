using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Diagnostics;
using CacheOrchestrator.Edge.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace CacheOrchestrator.Edge.Invalidation;

internal sealed class EdgeInvalidationWorker : BackgroundService
{
    private readonly EdgeInvalidationChannel _channel;
    private readonly EdgeProviderCatalog _providers;
    private readonly IOptionsMonitor<CacheOrchestratorEdgeOptions> _options;
    private readonly ILogger<EdgeInvalidationWorker> _logger;
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationToken _abortToken;
    private int _disposed;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EdgeInvalidationWorker(
        EdgeInvalidationChannel channel,
        EdgeProviderCatalog providers,
        IOptionsMonitor<CacheOrchestratorEdgeOptions> options,
        ILogger<EdgeInvalidationWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _channel = channel;
        _providers = providers;
        _options = options;
        _logger = logger;
        _abortToken = _abort.Token;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        // .NET 10 schedules ExecuteAsync on the pool. Ensure it has started before StopAsync
        // can cancel that scheduled delegate and strand jobs admitted during startup.
        Task completed = await Task.WhenAny(_started.Task, ExecuteTask!).WaitAsync(cancellationToken).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Channel.Writer.TryComplete();
        using CancellationTokenRegistration deadline = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _abort);
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The host's wait callback can finish StopAsync before our cancellation callback
            // runs. Propagate the deadline before disposing that registration in either order.
            if (cancellationToken.IsCancellationRequested)
                _abort.Cancel();
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _abort.Cancel();
        base.Dispose();
        _abort.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _started.TrySetResult();
        ChannelReader<EdgeInvalidationJob> reader = _channel.Channel.Reader;
        EdgeInvalidationJob? pending = null;
        var groups = new Dictionary<EdgeInvalidationTarget, HashSet<string>>();
        try
        {
            try
            {
                while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    if (!reader.TryRead(out pending))
                        continue;
                    int flushSeconds = _options.CurrentValue.EdgeQueue.FlushIntervalSeconds;
                    if (flushSeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(flushSeconds), stoppingToken).ConfigureAwait(false);
                    Add(pending, groups);
                    pending = null;
                    DrainAvailable(reader, groups);
                    // Normal shutdown interrupts coalescing, not provider I/O already in progress.
                    // Only the host's shutdown deadline aborts the purge.
                    await InvalidateGroupsAsync(groups, _abortToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (pending is not null)
                    Add(pending, groups);
                pending = null;
                DrainAvailable(reader, groups);
                await InvalidateGroupsAsync(groups, _abortToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_abortToken.IsCancellationRequested)
        {
            if (pending is not null)
                Add(pending, groups);
            DrainAvailable(reader, groups);
            foreach ((EdgeInvalidationTarget target, HashSet<string> tags) in groups)
            {
                EdgeMetrics.RecordFailure(target.InstanceName, target.ProviderName, "shutdown-deadline");
                _logger.LogWarning(
                    "Edge shutdown deadline left {TagCount} undelivered tags for instance '{Instance}' provider={Provider}.",
                    tags.Count, target.InstanceName, target.ProviderName);
            }
        }
    }

    private static void DrainAvailable(ChannelReader<EdgeInvalidationJob> reader, Dictionary<EdgeInvalidationTarget, HashSet<string>> groups)
    {
        // Bound each normal coalescing pass so a continuous producer cannot starve provider I/O.
        int count = reader.Count;
        for (int i = 0; i < count && reader.TryRead(out EdgeInvalidationJob? job); i++)
            Add(job, groups);
    }

    private async Task InvalidateGroupsAsync(
        Dictionary<EdgeInvalidationTarget, HashSet<string>> groups, CancellationToken cancellationToken)
    {
        foreach ((EdgeInvalidationTarget target, HashSet<string> tags) in groups.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEdgeInvalidationProvider provider = _providers.ResolveInvalidation(target.ProviderName);
            int batchSize = provider.Capabilities.MaxInvalidationBatchSize;
            string[] allTags = [.. tags];
            for (int offset = 0; offset < allTags.Length; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(batchSize, allTags.Length - offset);
                string[] batch = new string[count];
                Array.Copy(allTags, offset, batch, 0, count);
                await InvalidateBatchAsync(provider, target, batch, cancellationToken).ConfigureAwait(false);
                foreach (string tag in batch)
                    tags.Remove(tag);
            }
            groups.Remove(target);
        }
    }

    private static void Add(EdgeInvalidationJob job, Dictionary<EdgeInvalidationTarget, HashSet<string>> groups)
    {
        if (!groups.TryGetValue(job.Target, out HashSet<string>? tags))
        {
            tags = new(StringComparer.Ordinal);
            groups.Add(job.Target, tags);
        }
        foreach (string tag in job.Tags)
            tags.Add(tag);
    }

    private async Task InvalidateBatchAsync(
        IEdgeInvalidationProvider provider,
        EdgeInvalidationTarget target,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        string instanceName = target.InstanceName;
        EdgeQueueOptions queueOptions = _options.CurrentValue.EdgeQueue;
        for (int attempt = 1; attempt <= queueOptions.MaxAttempts; attempt++)
        {
            EdgeInvalidationResult result;
            try
            {
                result = await provider.InvalidateAsync(
                    new EdgeInvalidationRequest { Target = target, Tags = tags },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = new EdgeInvalidationResult
                {
                    IsTransient = true,
                    Error = ex.GetType().Name
                };
            }

            if (result.Succeeded)
            {
                EdgeMetrics.RecordPurged(instanceName, provider.Name, tags.Count);
                _logger.LogDebug(
                    "Edge invalidation succeeded for instance '{Instance}' provider={Provider} tags={TagCount}",
                    instanceName,
                    provider.Name,
                    tags.Count);
                return;
            }

            if (!result.IsTransient || attempt == queueOptions.MaxAttempts)
            {
                EdgeMetrics.RecordFailure(instanceName, provider.Name, result.IsTransient ? "exhausted" : "permanent");
                _logger.LogWarning(
                    "Edge invalidation failed for instance '{Instance}' provider={Provider} tags={TagCount} attempt={Attempt}: {Error}",
                    instanceName,
                    provider.Name,
                    tags.Count,
                    attempt,
                    result.Error ?? "provider rejected request");
                return;
            }

            TimeSpan delay = result.RetryAfter ?? TimeSpan.FromSeconds(
                queueOptions.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
            delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
