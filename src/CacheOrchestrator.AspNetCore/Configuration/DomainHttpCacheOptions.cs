using Microsoft.Extensions.Primitives;

namespace CacheOrchestrator.Configuration;

/// <summary>
/// Resolved ASP.NET Core cache policy for one domain.
/// </summary>
/// <remarks>
/// HTTP-free domain identity and Data Cache policy are available through <see cref="CoreOptions"/>.
/// This snapshot owns Output Cache, Client Cache, authentication, vary, ETag, and HTTP Data Cache key policy.
/// </remarks>
public sealed class DomainHttpCacheOptions
{
    /// <summary>HTTP-free domain snapshot shared with Core orchestration and Data Cache providers.</summary>
    public DomainCacheOptions CoreOptions { get; init; } = new();

    /// <summary>Normalized domain name.</summary>
    public string Domain => CoreOptions.Domain;

    /// <summary>Name of the Data Cache instance that handles this domain.</summary>
    public string DataCacheInstanceName => CoreOptions.DataCacheInstanceName;

    /// <summary>Whether Data Cache is enabled for this domain.</summary>
    public bool DataCacheEnabled => CoreOptions.DataCacheEnabled;

    /// <summary>Version token used by all cache layers.</summary>
    public string Version => CoreOptions.Version;

    /// <summary>Hashed Version material used in cache keys.</summary>
    public string VersionHex => CoreOptions.VersionHex;

    /// <summary>Logical Data Cache TTL.</summary>
    public TimeSpan DataCacheTtl => CoreOptions.DataCacheTtl;

    /// <summary>Data Cache key namespace.</summary>
    public string DataCacheNamespace => CoreOptions.DataCacheNamespace;

    /// <summary>Whether Output Cache is enabled for this domain.</summary>
    public bool OutputCacheEnabled { get; init; } = true;

    /// <summary>Authentication mode that bypasses HTTP caching.</summary>
    public AuthBypassMode AuthBypassMode { get; init; } = AuthBypassMode.AuthenticatedOrAuthorization;

    /// <summary>Whether authenticated responses vary by user identity.</summary>
    public bool VaryOutputCacheByUser { get; init; } = true;

    /// <summary>Whether an Authorization header counts as an authentication signal.</summary>
    public bool TreatAuthorizationAsAuthSignal { get; init; } = true;

    /// <summary>Whether Authorization is hashed into user vary material when claims are unavailable.</summary>
    public bool AuthVaryIncludeAuthorizationHash { get; init; } = true;

    /// <summary>Claim types included in authenticated vary material.</summary>
    public string[]? VaryByAuthClaims { get; init; }

    /// <summary>Whether HTTP Data Cache calls respect the same authentication bypass as Output Cache.</summary>
    public bool DataCacheRespectAuthBypass { get; init; } = true;

    /// <summary>Whether public Client Cache policy is forced private for authenticated users.</summary>
    public bool ClientForcePrivateWhenAuthenticated { get; init; } = true;

    /// <summary>Whether HTTP cache identity varies by Accept.</summary>
    public bool VaryByAccept { get; init; }

    /// <summary>
    /// Canonical spellings for single, parameter-free Accept tokens. Composite headers retain
    /// their complete negotiation material. <see langword="null"/> keeps the raw header.
    /// </summary>
    public string[]? AcceptNormalizationList { get; init; }

    /// <summary>Whether HTTP cache identity varies by Accept-Language.</summary>
    public bool VaryByAcceptLanguage { get; init; }

    /// <summary>Canonical spellings for single, parameter-free Accept-Language tokens; composite headers are retained.</summary>
    public string[]? AcceptLanguageNormalizationList { get; init; }

    /// <summary>Additional request headers included in HTTP cache identity.</summary>
    public string[]? VaryByHeaders { get; init; }

    /// <summary>Query key allowlist included in HTTP cache identity.</summary>
    public string[]? VaryByQueryKeys { get; init; }

    /// <summary>Additional query keys ignored by HTTP cache identity.</summary>
    public string[]? IgnoreQueryKeys { get; init; }

    /// <summary>Cookie names included in HTTP cache identity.</summary>
    public string[]? VaryByCookies { get; init; }

    /// <summary>Whether response Vary headers are emitted for non-secret varied headers.</summary>
    public bool EmitResponseVary { get; init; } = true;

    /// <summary>ETag mode for Output Cache responses.</summary>
    public ETagMode ETagMode { get; init; } = ETagMode.Version;

    /// <summary>Precomputed Version ETag.</summary>
    public StringValues ETag { get; init; }

    /// <summary>HTTP status codes that may be stored in Output Cache.</summary>
    public int[] CacheableStatusCodes { get; init; } = [200];

    /// <summary>Canonical spellings for single, parameter-free Accept-Encoding tokens; composite headers are retained.</summary>
    public string[]? EncodingNormalizationList { get; init; } = ["br", "gzip"];

    /// <summary>Client Cache response cacheability.</summary>
    public ClientCacheability ClientCacheability { get; init; }

    /// <summary>Client Cache max-age away from a scheduled update.</summary>
    public int ClientTtlSeconds { get; init; }

    /// <summary>Client Cache max-age floor near a scheduled update.</summary>
    public int ClientTtlMinSeconds { get; init; }

    /// <summary>Next planned client-visible content update.</summary>
    public DateTimeOffset? ScheduledUpdateUtc { get; init; }

    /// <summary>Whether must-revalidate is emitted near a scheduled update.</summary>
    public bool ClientMustRevalidateNearUpdate { get; init; }

    /// <summary>Output Cache entry duration.</summary>
    public TimeSpan OutputTtl { get; init; }

    /// <summary>Output Cache key namespace.</summary>
    public string OutputCacheNamespace { get; init; } = string.Empty;

    /// <summary>Whether HTTP Data Cache calls bypass on Cache-Control: no-store.</summary>
    public bool DataCacheRespectNoStore { get; init; } = true;

    /// <summary>Whether HTTP Data Cache keys include Accept-Encoding.</summary>
    public bool DataCacheVaryOnEncoding { get; init; } = true;

    /// <summary>Whether HTTP Data Cache keys include scheme and host.</summary>
    public bool DataCacheVaryOnPublicAddress { get; init; } = true;

    /// <summary>Whether Output Cache keys vary by request host.</summary>
    public bool OutputCacheVaryByHost { get; init; } = true;

    /// <summary>Stable generation for Output Cache key-shaping settings.</summary>
    internal string OutputCachePolicyGeneration { get; set; } = string.Empty;

    /// <summary>Stable generation for HTTP-derived Data Cache key-shaping settings.</summary>
    internal string DataCachePolicyGeneration { get; set; } = string.Empty;
}
