using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CacheOrchestrator.Admin;

internal sealed class DomainSettingsInvalidationCoordinator(
    IEnumerable<IDomainSettingValueProvider> valueProviders,
    IDataCacheProvider dataCache,
    IDomainCacheOptionsProvider domainOptions,
    IHttpCacheInvalidationSink outputCache,
    IEnumerable<IDomainSettingsInvalidationObserver> observers,
    ILogger<DomainSettingsInvalidationCoordinator> logger)
{
    private readonly IDomainSettingValueProvider[] _valueProviders = [.. valueProviders];
    private readonly IDomainSettingsInvalidationObserver[] _observers = [.. observers];

    public static string[] CanonicalizeSettingIds(IEnumerable<string> settingIds) =>
        [.. settingIds.Select(static id => DomainSettingCatalog.Find(id)?.Id ?? id)
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

    public async ValueTask ApplyAsync(
        string domain,
        IReadOnlyCollection<string> settingIds,
        IReadOnlyDictionary<string, JsonElement> before,
        bool applyImmediately,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement> after = Capture(domain, settingIds);
        DomainSettingsInvalidationTargets targets = DomainSettingsInvalidationPlanner.Plan(
            settingIds,
            before,
            after,
            applyImmediately);
        if (targets == DomainSettingsInvalidationTargets.None)
            return;

        string tag = CacheTags.Domain(domain);
        if ((targets & DomainSettingsInvalidationTargets.DataCache) != 0)
        {
            try
            {
                DomainCacheOptions options = domainOptions.GetOrCreateDomainOptions(domain);
                await dataCache.InvalidateAsync(
                    new DataCacheInvalidationRequest { InstanceName = options.DataCacheInstanceName, Tags = [tag] },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to apply immediate Data Cache policy change for domain '{Domain}'.", domain);
            }
        }

        if ((targets & DomainSettingsInvalidationTargets.OutputCache) != 0)
        {
            try
            {
                await outputCache.EvictByTagAsync(tag, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to apply immediate Output Cache policy change for domain '{Domain}'.", domain);
            }
        }

        if ((targets & DomainSettingsInvalidationTargets.Edge) != 0 && !ClusterCommandScope.IsRemote)
        {
            for (int i = 0; i < _observers.Length; i++)
            {
                try
                {
                    await _observers[i].InvalidateDomainSettingsAsync(domain, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Failed to apply immediate Edge policy change for domain '{Domain}'.", domain);
                }
            }
        }
    }
}
