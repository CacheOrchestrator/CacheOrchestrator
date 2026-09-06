using CacheOrchestrator.Configuration;
using System.Text.Json;

namespace CacheOrchestrator.Admin;

internal sealed class HttpDomainSettingValueProvider(IRequestDomainCacheOptions options)
    : IDomainSettingValueProvider
{
    public bool TryGetValue(string domain, string settingId, out JsonElement value)
    {
        DomainHttpCacheOptions resolved = options.GetOrCreateDomainOptions(domain);
        object? current = settingId switch
        {
            "outputCache.enabled" => resolved.OutputCacheEnabled,
            "outputCache.ttlSeconds" => (int)resolved.OutputTtl.TotalSeconds,
            "outputCache.eTagMode" => resolved.ETagMode.ToString(),
            "authBypassMode" => resolved.AuthBypassMode.ToString(),
            "treatAuthorizationAsAuthSignal" => resolved.TreatAuthorizationAsAuthSignal,
            "varyByAccept" => resolved.VaryByAccept,
            "varyByAcceptLanguage" => resolved.VaryByAcceptLanguage,
            "varyByHeaders" => resolved.VaryByHeaders,
            "emitResponseVary" => resolved.EmitResponseVary,
            "clientCache.cacheability" => resolved.ClientCacheability.ToString(),
            "clientCache.forcePrivateWhenAuthenticated" => resolved.ClientForcePrivateWhenAuthenticated,
            "clientCache.ttlSeconds" => resolved.ClientTtlSeconds,
            "clientCache.ttlMinSeconds" => resolved.ClientTtlMinSeconds,
            "clientCache.scheduledUpdateUtc" => resolved.ScheduledUpdateUtc?.ToString("O"),
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
