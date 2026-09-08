using CacheOrchestrator.Admin;
using System.Text.Json;

namespace CacheOrchestrator.FusionCache;

internal sealed class FusionDomainSettingValueProvider(IFusionDomainSettingsProvider settings)
    : IDomainSettingValueProvider
{
    public bool TryGetValue(string domain, string settingId, out JsonElement value)
    {
        DomainFusionCacheSettings resolved = settings.Get(domain);
        object? current = settingId switch
        {
            "fusionCache.hardTtlSeconds" => resolved.HardTtlSeconds,
            "fusionCache.failSafeSeconds" => resolved.FailSafeSeconds,
            "fusionCache.jitterSeconds" => resolved.JitterSeconds,
            _ => null
        };
        if (current is null)
        {
            value = default;
            return false;
        }

        value = JsonSerializer.SerializeToElement(current);
        return true;
    }
}
