using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;

namespace CacheOrchestrator.Admin;

internal sealed class DomainSettingsInvalidationCoordinator(
    IEnumerable<IDomainSettingValueProvider> valueProviders,
    IDataCacheProvider dataCache,
    IDomainCacheOptionsProvider domainOptions,
    IHttpCacheInvalidationSink outputCache,
    IEnumerable<IDomainSettingsInvalidationObserver> observers,
    ILogger<DomainSettingsInvalidationCoordinator> logger,
    DomainSettingCatalog? catalog = null)
{
    private readonly DomainSettingCatalog _catalog = catalog ?? DomainSettingCatalog.Core;
    private readonly IDomainSettingValueProvider[] _valueProviders = [.. valueProviders];
    private readonly IDomainSettingsInvalidationObserver[] _observers = [.. observers];

    private readonly ConcurrentDictionary<string, MutationGate> _mutationGates = new(StringComparer.Ordinal);
    private sealed class MutationGate
    {
#if NET9_0_OR_GREATER
        public readonly Lock Sync = new();
#else
        public readonly object Sync = new();
#endif
    }

    public DomainSettingsInvalidationPlan ApplyPatch(
        string domain,
        IReadOnlyDictionary<string, JsonElement> settings,
        IDomainRuntimeOverrideStore store,
        IEnumerable<IDomainSettingsPatchContributor> contributors,
        bool applyImmediately)
    {
        domain = DomainName.Normalize(domain);
        MutationGate gate = _mutationGates.GetOrAdd(domain, static _ => new());
        lock (gate.Sync)
        {
            string[] ids = CanonicalizeSettingIds(settings.Keys);
            IReadOnlyDictionary<string, JsonElement> before = Capture(domain, ids);
            DomainSettingsPatchApplicator.Apply(domain, settings, store, contributors, _catalog);
            IReadOnlyDictionary<string, JsonElement> after = Capture(domain, ids);
            DomainSettingsInvalidationTargets targets = DomainSettingsInvalidationPlanner.Plan(ids, before, after, applyImmediately);
            return new(domain, domainOptions.GetOrCreateDomainOptions(domain).DataCacheInstanceName, targets, _observers.Length);
        }
    }

    public string[] CanonicalizeSettingIds(IEnumerable<string> settingIds) =>
        [.. settingIds.Select(id => _catalog.Find(id)?.Id ?? id)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyDictionary<string, JsonElement> Capture(string domain, IEnumerable<string> settingIds)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in settingIds)
        {
            for (int i = 0; i < _valueProviders.Length; i++)
            {
                if (_valueProviders[i].TryGetValue(domain, id, out JsonElement value))
                {
                    values[id] = value;
                    break;
                }
            }
        }

        return values;
    }

    public async ValueTask<IReadOnlyList<string>> ApplyAsync(
        DomainSettingsInvalidationPlan plan,
        CancellationToken cancellationToken)
    {
        string domain = plan.Domain;
        DomainSettingsInvalidationTargets targets = plan.RemainingTargets;
        if (targets == DomainSettingsInvalidationTargets.None)
            return [];
        List<string> errors = [];

        string tag = CacheTags.Domain(domain);
        if ((targets & DomainSettingsInvalidationTargets.DataCache) != 0)
        {
            try
            {
                await dataCache.InvalidateAsync(
                    new DataCacheInvalidationRequest { InstanceName = plan.DataCacheInstanceName, Tags = [tag] },
                    cancellationToken).ConfigureAwait(false);
                plan.RemainingTargets &= ~DomainSettingsInvalidationTargets.DataCache;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Data Cache: {ex.Message}");
                logger.LogWarning(ex, "Failed to apply immediate Data Cache policy change for domain '{Domain}'.", domain);
            }
        }

        if ((targets & DomainSettingsInvalidationTargets.OutputCache) != 0)
        {
            try
            {
                await outputCache.EvictByTagAsync(tag, cancellationToken).ConfigureAwait(false);
                plan.RemainingTargets &= ~DomainSettingsInvalidationTargets.OutputCache;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Output Cache: {ex.Message}");
                logger.LogWarning(ex, "Failed to apply immediate Output Cache policy change for domain '{Domain}'.", domain);
            }
        }

        if ((targets & DomainSettingsInvalidationTargets.Edge) != 0 && !ClusterCommandScope.IsRemote)
        {
            for (int i = 0; i < _observers.Length; i++)
            {
                if (plan.CompletedObservers[i])
                    continue;
                try
                {
                    await _observers[i].InvalidateDomainSettingsAsync(domain, cancellationToken).ConfigureAwait(false);
                    plan.CompletedObservers[i] = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"Edge: {ex.Message}");
                    logger.LogWarning(ex, "Failed to apply immediate Edge policy change for domain '{Domain}'.", domain);
                }
            }
        }
        if (ClusterCommandScope.IsRemote || plan.CompletedObservers.All(static done => done))
            plan.RemainingTargets &= ~DomainSettingsInvalidationTargets.Edge;
        return errors;
    }
}

internal sealed class DomainSettingsInvalidationPlan(
    string domain,
    string dataCacheInstanceName,
    DomainSettingsInvalidationTargets targets,
    int observerCount)
{
    public string Domain { get; } = domain;
    public string DataCacheInstanceName { get; } = dataCacheInstanceName;
    public DomainSettingsInvalidationTargets RemainingTargets { get; set; } = targets;
    public bool[] CompletedObservers { get; } = new bool[observerCount];
}
