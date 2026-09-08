using CacheOrchestrator.Diagnostics;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CacheOrchestrator.Cluster;

/// <summary>Serializes duplicate delivery and retains successful outcomes and retry continuations.</summary>
internal sealed class ClusterCommandDedupeStore(
    IOptionsMonitor<ClusterCommandHandlingOptions> options,
    TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastCleanupTicks;

    private sealed class Entry(Func<ClusterCommandExecution, CancellationToken, Task<ClusterCommandResult>> execute)
    {
#if NET9_0_OR_GREATER
        public readonly Lock Gate = new();
#else
        public readonly object Gate = new();
#endif
        public readonly ClusterCommandExecution Execution = new(execute);
        public TaskCompletionSource<ClusterCommandResult> Completion = NewCompletion();
        public ClusterCommandResult? Result;
        public long CompletedAt;
        public bool Running = true;
        public bool Retired;
    }

    public async Task<ClusterCommandResult> ExecuteAsync(
        Guid commandId,
        Func<ClusterCommandExecution, CancellationToken, Task<ClusterCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        int windowSeconds = options.CurrentValue.DedupeWindowSeconds;
        if (commandId == Guid.Empty || windowSeconds <= 0)
            return await new ClusterCommandExecution(execute).RunAsync(cancellationToken).ConfigureAwait(false);

        long windowTicks = TimeSpan.FromSeconds(Math.Clamp(windowSeconds, 1, 3600)).Ticks;
        CleanupIfNeeded(_time.GetUtcNow().UtcTicks, windowTicks);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(commandId, out Entry? entry))
            {
                entry = new(execute);
                if (_entries.TryAdd(commandId, entry))
                    return await RunOwnedAsync(entry, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Task<ClusterCommandResult> pending;
            bool owner = false;
            lock (entry.Gate)
            {
                if (entry.Retired)
                    continue;
                if (!entry.Running && _time.GetUtcNow().UtcTicks - entry.CompletedAt > windowTicks)
                {
                    Retire(commandId, entry);
                    continue;
                }
                if (!entry.Running && entry.Result is { Succeeded: true } completed)
                {
                    CacheOrchestratorMetrics.RecordClusterDedupeHit();
                    return completed.AsDuplicate();
                }
                if (!entry.Running)
                {
                    entry.Running = true;
                    entry.Completion = NewCompletion();
                    owner = true;
                }
                pending = entry.Completion.Task;
            }
            if (owner)
                return await RunOwnedAsync(entry, cancellationToken).ConfigureAwait(false);
            CacheOrchestratorMetrics.RecordClusterDedupeHit();
            ClusterCommandResult result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.AsDuplicate() : result;
        }
    }

    private async Task<ClusterCommandResult> RunOwnedAsync(Entry entry, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ClusterCommandResult> completion = entry.Completion;
        ClusterCommandResult result;
        Exception? failure = null;
        try
        {
            result = await entry.Execution.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            result = new(ClusterCommandStatus.Failed, "execution-interrupted");
        }
        lock (entry.Gate)
        {
            entry.Result = result;
            entry.CompletedAt = _time.GetUtcNow().UtcTicks;
            entry.Running = false;
        }
        if (failure is OperationCanceledException canceled)
            completion.TrySetCanceled(canceled.CancellationToken);
        else if (failure is not null)
            completion.TrySetException(failure);
        else
            completion.TrySetResult(result);
        return await completion.Task.ConfigureAwait(false);
    }

    private static TaskCompletionSource<ClusterCommandResult> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Retire(Guid id, Entry entry)
    {
        entry.Retired = true;
        ((ICollection<KeyValuePair<Guid, Entry>>)_entries).Remove(new(id, entry));
    }

    private void CleanupIfNeeded(long now, long windowTicks)
    {
        long last = Volatile.Read(ref _lastCleanupTicks);
        if (now - last < TimeSpan.TicksPerSecond || Interlocked.CompareExchange(ref _lastCleanupTicks, now, last) != last)
            return;
        foreach ((Guid id, Entry entry) in _entries)
        {
            lock (entry.Gate)
            {
                if (!entry.Running && now - entry.CompletedAt > windowTicks)
                    Retire(id, entry);
            }
        }
    }
}

/// <summary>Retains only the unfinished work after a mutation has already been published.</summary>
internal sealed class ClusterCommandExecution(Func<ClusterCommandExecution, CancellationToken, Task<ClusterCommandResult>> execute)
{
    public Func<CancellationToken, Task<ClusterCommandResult>>? Resume { get; set; }
    public Task<ClusterCommandResult> RunAsync(CancellationToken cancellationToken) =>
        Resume is { } resume ? resume(cancellationToken) : execute(this, cancellationToken);
}
