using System.Text.Json;

namespace CacheOrchestrator.Admin;

/// <summary>Supplies effective values used to plan domain-setting invalidation.</summary>
internal interface IDomainSettingValueProvider
{
    /// <summary>Attempts to read one effective setting value for a normalized domain.</summary>
    bool TryGetValue(string domain, string settingId, out JsonElement value);
}
