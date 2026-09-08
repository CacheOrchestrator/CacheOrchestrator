using System.Text.Json;

namespace CacheOrchestrator.Admin;

/// <summary>
/// Optional package-owned overlay handler for domain settings not mapped by Core
/// (e.g. <c>fusionCache.*</c> from the FusionCache package).
/// </summary>
public interface IDomainSettingsPatchContributor
{
    /// <summary>Returns <see langword="true"/> when this contributor owns <paramref name="settingId"/>.</summary>
    bool Owns(string settingId);

    /// <summary>
    /// Validates and stages owned overlay settings in the unpublished working copy.
    /// Throw on invalid effective settings. Do not mutate a store or perform external side effects.
    /// Only keys for which <see cref="Owns"/> is true should be present.
    /// </summary>
    void Prepare(DomainSettingsPatchContext context, IReadOnlyDictionary<string, JsonElement> settings);

    /// <summary>Validates the effective state after every contributor has prepared its section.</summary>
    void Validate(DomainSettingsPatchContext context);
}
