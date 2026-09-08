using CacheOrchestrator.Configuration;
using System.Text.Json;

namespace CacheOrchestrator.Admin;

/// <summary>
/// Applies a sparse Admin settings dictionary to Core overlays and optional package contributors.
/// </summary>
internal static class DomainSettingsPatchApplicator
{
    /// <summary>
    /// Validates catalog ids, routes Core keys through <see cref="DomainSettingsPatchMapper"/>,
    /// and forwards owned extras to <paramref name="contributors"/>.
    /// </summary>
    public static DomainSettingsPatch Apply(
        string domain,
        IReadOnlyDictionary<string, JsonElement> settings,
        IDomainRuntimeOverrideStore store,
        IEnumerable<IDomainSettingsPatchContributor>? contributors = null,
        DomainSettingCatalog? catalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        if (settings.Count == 0)
            throw new ArgumentException("At least one setting must be set.", nameof(settings));

        IDomainSettingsPatchContributor[] contribs = contributors?.ToArray() ?? [];
        Dictionary<string, JsonElement> core = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<IDomainSettingsPatchContributor, Dictionary<string, JsonElement>> byContributor = new();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawKey, JsonElement el) in settings)
        {
            DomainSettingCatalogEntry entry = (catalog ?? DomainSettingCatalog.Core).Find(rawKey)
                ?? throw new ArgumentException($"Unknown domain setting '{rawKey}'.", nameof(settings));
            if (!seen.Add(entry.Id))
                throw new ArgumentException($"Setting '{entry.Id}' was supplied more than once through different aliases.", nameof(settings));
            if (!entry.RuntimeOverlay)
                throw new ArgumentException($"Setting '{entry.Id}' is not runtime-patchable.", nameof(settings));

            IDomainSettingsPatchContributor[] owners = contribs.Where(c => c.Owns(entry.Id)).ToArray();
            if (owners.Length != 0 && entry.Id is "dataCache.enabled" or "dataCache.ttlSeconds")
                throw new ArgumentException($"Setting '{entry.Id}' is owned by Core.", nameof(settings));
            if (owners.Length > 1)
                throw new ArgumentException($"Multiple contributors own setting '{entry.Id}'.", nameof(settings));
            IDomainSettingsPatchContributor? owner = owners.FirstOrDefault();
            if (owner is not null)
            {
                if (!byContributor.TryGetValue(owner, out Dictionary<string, JsonElement>? bag))
                {
                    bag = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    byContributor[owner] = bag;
                }

                bag[entry.Id] = el;
                continue;
            }

            core[entry.Id] = el;
        }

        DomainSettingsPatch patch = core.Count > 0 ? DomainSettingsPatchMapper.FromDictionary(core) : new();
        store.Update(domain, context =>
        {
            if (patch.HasAny)
                context.Set(DomainRuntimeOverrideStore.Merge(context.Get<DomainRuntimeOverride>() ?? new(), patch, context.Stamp));
            foreach ((IDomainSettingsPatchContributor contributor, Dictionary<string, JsonElement> bag) in byContributor)
                contributor.Prepare(context, bag);
            foreach (IDomainSettingsPatchContributor contributor in contribs)
                contributor.Validate(context);
        });
        return patch;
    }
}
