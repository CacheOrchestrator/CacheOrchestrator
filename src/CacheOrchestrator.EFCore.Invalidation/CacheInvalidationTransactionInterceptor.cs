using CacheOrchestrator.Invalidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Transactions;

namespace CacheOrchestrator.EFCore;

// Captured ids and policy contain no DbContext, EntityEntry, or request-scoped references.
internal sealed record EfInvalidationWork(
    string Domain, string EntityKind, IReadOnlyList<string> Ids, EfCoreOnBulk OnBulk, int BulkThreshold);

/// <summary>Defers saved changes until the owning transaction commits.</summary>
internal sealed class CacheInvalidationTransactionInterceptor(
    ICacheOrchestratorInvalidator invalidator,
    ILogger<CacheInvalidationTransactionInterceptor> logger) : DbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbTransaction, PendingBatch> _relational = new();
    private readonly ConditionalWeakTable<Transaction, AmbientBatch> _ambient = new();

    public ValueTask PublishAsync(DbContext context, IReadOnlyList<EfInvalidationWork> work)
    {
        Transaction? ambient = Transaction.Current;
        if (ambient is null && context.Database.IsRelational())
            ambient = context.Database.GetEnlistedTransaction();
        if (ambient is not null)
        {
            AmbientBatch batch;
            // Subscribe once per ambient transaction, after its other outcome notifications.
            lock (_ambient)
            {
                if (!_ambient.TryGetValue(ambient, out batch!))
                {
                    batch = new AmbientBatch(this);
                    _ambient.Add(ambient, batch);
                    ambient.TransactionCompleted += batch.OnCompleted;
                }
            }
            return EnqueueOrCompleteAsync(batch, work);
        }

        if (context.Database.IsRelational() && context.Database.CurrentTransaction is { } transaction)
        {
            PendingBatch batch = _relational.GetValue(transaction.GetDbTransaction(), static _ => new());
            return EnqueueOrCompleteAsync(batch, work);
        }

        return ExecuteAsync(work);
    }

    private ValueTask EnqueueOrCompleteAsync(PendingBatch batch, IReadOnlyList<EfInvalidationWork> work)
    {
        // A commit callback can race the final SavedChanges callback when the owner is external.
        bool? completed = batch.Add(work);
        return completed == true ? ExecuteAsync(work) : ValueTask.CompletedTask;
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        CommitAsync(transaction).AsTask().GetAwaiter().GetResult();

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        CommitAsync(transaction).AsTask();

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        Discard(transaction);

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(transaction);
        return Task.CompletedTask;
    }

    private ValueTask CommitAsync(DbTransaction transaction) =>
        _relational.TryGetValue(transaction, out PendingBatch? batch)
            ? ExecuteAsync(batch.Complete(committed: true))
            : ValueTask.CompletedTask;

    private void Discard(DbTransaction transaction)
    {
        if (_relational.TryGetValue(transaction, out PendingBatch? batch))
            batch.Complete(committed: false);
    }

    private async ValueTask ExecuteAsync(IReadOnlyList<EfInvalidationWork> work)
    {
        foreach (EfInvalidationWork item in work)
        {
            try
            {
                bool bulk = item.OnBulk != EfCoreOnBulk.Entities
                    && item.BulkThreshold > 0 && item.Ids.Count >= item.BulkThreshold;
                ValueTask<CacheInvalidationResult> operation = (bulk, item.OnBulk) switch
                {
                    (true, EfCoreOnBulk.Domain) => invalidator.InvalidateDomainAsync(item.Domain, CancellationToken.None),
                    (true, EfCoreOnBulk.Kind) => invalidator.InvalidateEntityKindAsync(item.Domain, item.EntityKind, CancellationToken.None),
                    _ => invalidator.InvalidateEntitiesAsync(item.Domain, item.EntityKind, item.Ids, CancellationToken.None)
                };
                CacheInvalidationResult result = await operation.ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    logger.LogWarning("Cache invalidation partially failed after commit for domain '{Domain}' entityKind '{EntityKind}': {Errors}",
                        item.Domain, item.EntityKind, string.Join("; ", result.Errors));
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Cache invalidation failed after commit for domain '{Domain}' entityKind '{EntityKind}'.",
                    item.Domain, item.EntityKind);
            }
        }
    }

    private class PendingBatch
    {
#if NET9_0_OR_GREATER
        private readonly Lock _gate = new();
#else
        private readonly object _gate = new();
#endif
        private readonly Dictionary<(string Domain, string Kind, EfCoreOnBulk Bulk, int Threshold), HashSet<string>> _groups = [];
        private bool? _completed;

        // null = queued, true = already committed, false = already rolled back.
        public bool? Add(IReadOnlyList<EfInvalidationWork> work)
        {
            lock (_gate)
            {
                if (_completed.HasValue)
                    return _completed;
                foreach (EfInvalidationWork item in work)
                {
                    (string, string, EfCoreOnBulk, int) key = (item.Domain, item.EntityKind, item.OnBulk, item.BulkThreshold);
                    if (!_groups.TryGetValue(key, out HashSet<string>? ids))
                        _groups[key] = ids = new(StringComparer.Ordinal);
                    foreach (string id in item.Ids)
                    {
                        if (item.OnBulk != EfCoreOnBulk.Entities && item.BulkThreshold > 0 && ids.Count >= item.BulkThreshold)
                            break;
                        ids.Add(id);
                    }
                }
                return null;
            }
        }

        public IReadOnlyList<EfInvalidationWork> Complete(bool committed)
        {
            lock (_gate)
            {
                if (_completed.HasValue)
                    return [];
                _completed = committed;
                IReadOnlyList<EfInvalidationWork> result = committed
                    ? _groups.Select(pair => new EfInvalidationWork(
                        pair.Key.Domain, pair.Key.Kind, [.. pair.Value], pair.Key.Bulk, pair.Key.Threshold)).ToArray()
                    : [];
                _groups.Clear();
                return result;
            }
        }
    }

    private sealed class AmbientBatch(CacheInvalidationTransactionInterceptor owner) : PendingBatch
    {
        public void OnCompleted(object? sender, TransactionEventArgs args)
        {
            TransactionStatus? status = args.Transaction?.TransactionInformation.Status;
            bool committed = status is TransactionStatus.Committed or TransactionStatus.InDoubt;
            // InDoubt may represent a committed database; conservatively purge.
            owner.ExecuteAsync(Complete(committed)).AsTask().GetAwaiter().GetResult();
            if (args.Transaction is { } transaction)
                transaction.TransactionCompleted -= OnCompleted;
        }
    }
}
