using CacheOrchestrator.Configuration;
using System.Text.Json;

namespace CacheOrchestrator.Admin;

internal sealed class CoreDomainSettingValueProvider(IDomainCacheOptionsProvider options)
    : IDomainSettingValueProvider
{
    public bool TryGetValue(string domain, string settingId, out JsonElement value)
    {
        DomainCacheOptions resolved = options.GetOrCreateDomainOptions(domain);
        object? current = settingId switch
        {
            "dataCache.enabled" => resolved.DataCacheEnabled,
            "dataCache.instance" => resolved.DataCacheInstanceName,
            "dataCache.ttlSeconds" => (int)resolved.DataCacheTtl.TotalSeconds,
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
