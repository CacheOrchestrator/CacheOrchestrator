using CacheOrchestrator.Admin;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace CacheOrchestrator.Configuration;

internal sealed record HttpDomainRuntimeOverride
{
    public int Stamp { get; init; }
    public bool? OutputCacheEnabled { get; init; }
    public AuthBypassMode? AuthBypassMode { get; init; }
    public bool? VaryOutputCacheByUser { get; init; }
    public bool? TreatAuthorizationAsAuthSignal { get; init; }
    public bool? AuthVaryIncludeAuthorizationHash { get; init; }
    public bool? DataCacheRespectAuthBypass { get; init; }
    public bool? ClientForcePrivateWhenAuthenticated { get; init; }
    public bool? VaryByAccept { get; init; }
    public bool? VaryByAcceptLanguage { get; init; }
    public bool? EmitResponseVary { get; init; }
    public string[]? AcceptNormalizationList { get; init; }
    public string[]? AcceptLanguageNormalizationList { get; init; }
    public string[]? VaryByHeaders { get; init; }
    public string[]? VaryByQueryKeys { get; init; }
    public string[]? IgnoreQueryKeys { get; init; }
    public string[]? VaryByCookies { get; init; }
    public string[]? VaryByAuthClaims { get; init; }
    public ETagMode? ETagMode { get; init; }
    public ClientCacheability? ClientCacheability { get; init; }
    public TimeSpan? ClientTtl { get; init; }
    public TimeSpan? ClientTtlMin { get; init; }
    public DateTimeOffset? ScheduledUpdateUtc { get; init; }
    public bool? ClientMustRevalidateNearUpdate { get; init; }
    public TimeSpan? OutputCacheTtl { get; init; }
    public bool? DataCacheRespectNoStore { get; init; }
    public bool? DataCacheVaryOnPublicAddress { get; init; }
    public bool? DataCacheVaryOnEncoding { get; init; }
    public bool? OutputCacheVaryByHost { get; init; }
}

internal interface IHttpDomainRuntimeOverrideStore
{
    HttpDomainRuntimeOverride? Get(string domain);
    int GetStamp(string domain);
    void Patch(string domain, HttpDomainRuntimeOverride patch);
}

internal sealed class HttpDomainRuntimeOverrideStore : IHttpDomainRuntimeOverrideStore
{
    private readonly IDomainRuntimeOverrideStore _state;
    public HttpDomainRuntimeOverrideStore() : this(new DomainRuntimeOverrideStore()) { }
    public HttpDomainRuntimeOverrideStore(IDomainRuntimeOverrideStore state)
    {
        _state = state;
    }

    public HttpDomainRuntimeOverride? Get(string domain) => _state.GetSettings<HttpDomainRuntimeOverride>(domain);
    public int GetStamp(string domain) => _state.GetStamp(domain);

    public void Patch(string domain, HttpDomainRuntimeOverride patch) =>
        _state.Update(domain, context => context.Set(Merge(context.Get<HttpDomainRuntimeOverride>() ?? new(), patch, context.Stamp)));

    internal static HttpDomainRuntimeOverride Merge(HttpDomainRuntimeOverride current, HttpDomainRuntimeOverride patch, int stamp) =>
        patch with
        {
            Stamp = stamp,
            OutputCacheEnabled = patch.OutputCacheEnabled ?? current.OutputCacheEnabled,
            AuthBypassMode = patch.AuthBypassMode ?? current.AuthBypassMode,
            VaryOutputCacheByUser = patch.VaryOutputCacheByUser ?? current.VaryOutputCacheByUser,
            TreatAuthorizationAsAuthSignal = patch.TreatAuthorizationAsAuthSignal ?? current.TreatAuthorizationAsAuthSignal,
            AuthVaryIncludeAuthorizationHash = patch.AuthVaryIncludeAuthorizationHash ?? current.AuthVaryIncludeAuthorizationHash,
            DataCacheRespectAuthBypass = patch.DataCacheRespectAuthBypass ?? current.DataCacheRespectAuthBypass,
            ClientForcePrivateWhenAuthenticated = patch.ClientForcePrivateWhenAuthenticated ?? current.ClientForcePrivateWhenAuthenticated,
            VaryByAccept = patch.VaryByAccept ?? current.VaryByAccept,
            VaryByAcceptLanguage = patch.VaryByAcceptLanguage ?? current.VaryByAcceptLanguage,
            EmitResponseVary = patch.EmitResponseVary ?? current.EmitResponseVary,
            AcceptNormalizationList = patch.AcceptNormalizationList ?? current.AcceptNormalizationList,
            AcceptLanguageNormalizationList = patch.AcceptLanguageNormalizationList ?? current.AcceptLanguageNormalizationList,
            VaryByHeaders = patch.VaryByHeaders ?? current.VaryByHeaders,
            VaryByQueryKeys = patch.VaryByQueryKeys ?? current.VaryByQueryKeys,
            IgnoreQueryKeys = patch.IgnoreQueryKeys ?? current.IgnoreQueryKeys,
            VaryByCookies = patch.VaryByCookies ?? current.VaryByCookies,
            VaryByAuthClaims = patch.VaryByAuthClaims ?? current.VaryByAuthClaims,
            ETagMode = patch.ETagMode ?? current.ETagMode,
            ClientCacheability = patch.ClientCacheability ?? current.ClientCacheability,
            ClientTtl = patch.ClientTtl ?? current.ClientTtl,
            ClientTtlMin = patch.ClientTtlMin ?? current.ClientTtlMin,
            ScheduledUpdateUtc = patch.ScheduledUpdateUtc ?? current.ScheduledUpdateUtc,
            ClientMustRevalidateNearUpdate = patch.ClientMustRevalidateNearUpdate ?? current.ClientMustRevalidateNearUpdate,
            OutputCacheTtl = patch.OutputCacheTtl ?? current.OutputCacheTtl,
            DataCacheRespectNoStore = patch.DataCacheRespectNoStore ?? current.DataCacheRespectNoStore,
            DataCacheVaryOnPublicAddress = patch.DataCacheVaryOnPublicAddress ?? current.DataCacheVaryOnPublicAddress,
            DataCacheVaryOnEncoding = patch.DataCacheVaryOnEncoding ?? current.DataCacheVaryOnEncoding,
            OutputCacheVaryByHost = patch.OutputCacheVaryByHost ?? current.OutputCacheVaryByHost
        };

}

internal sealed class HttpDomainSettingsPatchContributor : IDomainSettingsPatchContributor
{
    private readonly IOptionsMonitor<CacheOrchestratorHttpOptions> _options;

    public HttpDomainSettingsPatchContributor(IOptionsMonitor<CacheOrchestratorHttpOptions> options)
    {
        _options = options;
    }

    public bool Owns(string settingId) => settingId is
        "outputCache.enabled" or "authBypassMode" or "varyOutputCacheByUser" or
        "treatAuthorizationAsAuthSignal" or "authVaryIncludeAuthorizationHash" or "dataCacheRespectAuthBypass" or
        "clientCache.forcePrivateWhenAuthenticated" or "varyByAccept" or "varyByAcceptLanguage" or "emitResponseVary" or
        "acceptNormalizationList" or "acceptLanguageNormalizationList" or "varyByHeaders" or "varyByQueryKeys" or
        "ignoreQueryKeys" or "varyByCookies" or "varyByAuthClaims" or "outputCache.eTagMode" or
        "clientCache.cacheability" or "clientCache.ttlSeconds" or "clientCache.ttlMinSeconds" or
        "clientCache.scheduledUpdateUtc" or "clientCache.mustRevalidateNearUpdate" or "outputCache.ttlSeconds" or
        "dataCache.respectNoStore" or "dataCache.varyOnPublicAddress" or "dataCache.varyOnEncoding" or "outputCache.varyByHost";

    public void Prepare(DomainSettingsPatchContext context, IReadOnlyDictionary<string, JsonElement> settings)
    {
        HttpDomainRuntimeOverride patch = new();
        foreach ((string id, JsonElement value) in settings)
        {
            patch = id switch
            {
                "outputCache.enabled" => patch with { OutputCacheEnabled = ReadBool(value, id) },
                "authBypassMode" => patch with { AuthBypassMode = ReadEnum<AuthBypassMode>(value, id) },
                "varyOutputCacheByUser" => patch with { VaryOutputCacheByUser = ReadBool(value, id) },
                "treatAuthorizationAsAuthSignal" => patch with { TreatAuthorizationAsAuthSignal = ReadBool(value, id) },
                "authVaryIncludeAuthorizationHash" => patch with { AuthVaryIncludeAuthorizationHash = ReadBool(value, id) },
                "dataCacheRespectAuthBypass" => patch with { DataCacheRespectAuthBypass = ReadBool(value, id) },
                "clientCache.forcePrivateWhenAuthenticated" => patch with { ClientForcePrivateWhenAuthenticated = ReadBool(value, id) },
                "varyByAccept" => patch with { VaryByAccept = ReadBool(value, id) },
                "varyByAcceptLanguage" => patch with { VaryByAcceptLanguage = ReadBool(value, id) },
                "emitResponseVary" => patch with { EmitResponseVary = ReadBool(value, id) },
                "acceptNormalizationList" => patch with { AcceptNormalizationList = ReadArray(value, id, 16) },
                "acceptLanguageNormalizationList" => patch with { AcceptLanguageNormalizationList = ReadArray(value, id, 16) },
                "varyByHeaders" => patch with { VaryByHeaders = ReadArray(value, id, 8) },
                "varyByQueryKeys" => patch with { VaryByQueryKeys = ReadArray(value, id, 32) },
                "ignoreQueryKeys" => patch with { IgnoreQueryKeys = ReadArray(value, id, 32) },
                "varyByCookies" => patch with { VaryByCookies = ReadArray(value, id, 8) },
                "varyByAuthClaims" => patch with { VaryByAuthClaims = ReadArray(value, id, 16) },
                "outputCache.eTagMode" => patch with { ETagMode = ReadEnum<ETagMode>(value, id) },
                "clientCache.cacheability" => patch with { ClientCacheability = ReadEnum<ClientCacheability>(value, id) },
                "clientCache.ttlSeconds" => patch with { ClientTtl = ReadSeconds(value, id) },
                "clientCache.ttlMinSeconds" => patch with { ClientTtlMin = ReadSeconds(value, id) },
                "clientCache.scheduledUpdateUtc" => patch with { ScheduledUpdateUtc = ReadDate(value, id) },
                "clientCache.mustRevalidateNearUpdate" => patch with { ClientMustRevalidateNearUpdate = ReadBool(value, id) },
                "outputCache.ttlSeconds" => patch with { OutputCacheTtl = ReadSeconds(value, id) },
                "dataCache.respectNoStore" => patch with { DataCacheRespectNoStore = ReadBool(value, id) },
                "dataCache.varyOnPublicAddress" => patch with { DataCacheVaryOnPublicAddress = ReadBool(value, id) },
                "dataCache.varyOnEncoding" => patch with { DataCacheVaryOnEncoding = ReadBool(value, id) },
                "outputCache.varyByHost" => patch with { OutputCacheVaryByHost = ReadBool(value, id) },
                _ => throw new ArgumentException($"Setting '{id}' is not mapped by ASP.NET Core.", nameof(settings))
            };
        }

        HttpDomainRuntimeOverride merged = HttpDomainRuntimeOverrideStore.Merge(context.Get<HttpDomainRuntimeOverride>() ?? new(), patch, context.Stamp);
        context.Set(merged);
    }

    public void Validate(DomainSettingsPatchContext context)
    {
        HttpDomainRuntimeOverride merged = context.Get<HttpDomainRuntimeOverride>() ?? new();
        CacheOrchestratorHttpOptions options = _options.CurrentValue;
        options.Domains.TryGetValue(context.Domain, out DomainHttpCacheSettings? specific);
        double max = merged.ClientTtl?.TotalSeconds ?? specific?.ClientCache?.TtlSeconds ?? options.DomainDefaults.ClientCache?.TtlSeconds ?? 3600;
        double min = merged.ClientTtlMin?.TotalSeconds ?? specific?.ClientCache?.TtlMinSeconds ?? options.DomainDefaults.ClientCache?.TtlMinSeconds ?? 60;
        if (max > 0 && min > max)
            throw new ArgumentException("clientCache.ttlMinSeconds must be <= effective clientCache.ttlSeconds.");
    }

    private static bool ReadBool(JsonElement value, string id) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out bool result) => result,
        _ => throw new ArgumentException($"Setting '{id}' must be a boolean.", id)
    };

    private static TimeSpan ReadSeconds(JsonElement value, string id)
    {
        int seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) => number,
            _ => throw new ArgumentException($"Setting '{id}' must be an integer number of seconds.", id)
        };
        if (seconds < 0)
            throw new ArgumentException($"Setting '{id}' must be >= 0.", id);
        return TimeSpan.FromSeconds(seconds);
    }

    private static T ReadEnum<T>(JsonElement value, string id) where T : struct, Enum =>
        value.ValueKind == JsonValueKind.String && Enum.TryParse(value.GetString(), true, out T result) && Enum.IsDefined(result)
            ? result
            : throw new ArgumentException($"Setting '{id}' must be one of: {string.Join(", ", Enum.GetNames<T>())}.", id);

    private static DateTimeOffset ReadDate(JsonElement value, string id) =>
        value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset result)
            ? result.ToUniversalTime()
            : throw new ArgumentException($"Setting '{id}' must be an ISO-8601 date-time.", id);

    private static string[] ReadArray(JsonElement value, string id, int max)
    {
        string[] values = value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.String => value.GetString()?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [],
            JsonValueKind.Array => [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim() ?? string.Empty
                : throw new ArgumentException($"Setting '{id}' array entries must be strings.", id))],
            _ => throw new ArgumentException($"Setting '{id}' must be an array of strings.", id)
        };
        if (values.Length > max || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Setting '{id}' must contain at most {max} non-empty entries.", id);
        return values;
    }
}
