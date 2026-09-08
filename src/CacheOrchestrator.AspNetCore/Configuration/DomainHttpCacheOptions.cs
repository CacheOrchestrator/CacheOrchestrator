using Microsoft.Extensions.Primitives;

namespace CacheOrchestrator.Configuration;

/// <summary>
/// Resolved ASP.NET Core cache policy for one domain.
/// </summary>
/// <remarks>
/// HTTP-free domain identity and Data Cache policy are available through <see cref="CoreOptions"/>.
/// Collection setters copy their input into read-only storage once; callers cannot mutate shared policy.
/// This snapshot owns Output Cache, Client Cache, authentication, vary, ETag, and HTTP Data Cache key policy.
/// </remarks>
public sealed class DomainHttpCacheOptions
{
    private static readonly SnapshotList<int> DefaultStatusCodes = new([200]);
    private static readonly SnapshotList<string> DefaultEncodings = new(["br", "gzip"]);
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
    public IReadOnlyList<string>? VaryByAuthClaims
    {
        get => _varyByAuthClaims;
        init => _varyByAuthClaims = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _varyByAuthClaims;
    internal string[]? VaryByAuthClaimsArray => _varyByAuthClaims?.Values;

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
    public IReadOnlyList<string>? AcceptNormalizationList
    {
        get => _acceptNormalizationList;
        init => _acceptNormalizationList = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _acceptNormalizationList;
    internal string[]? AcceptNormalizationListArray => _acceptNormalizationList?.Values;

    /// <summary>Whether HTTP cache identity varies by Accept-Language.</summary>
    public bool VaryByAcceptLanguage { get; init; }

    /// <summary>Canonical spellings for single, parameter-free Accept-Language tokens; composite headers are retained.</summary>
    public IReadOnlyList<string>? AcceptLanguageNormalizationList
    {
        get => _acceptLanguageNormalizationList;
        init => _acceptLanguageNormalizationList = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _acceptLanguageNormalizationList;
    internal string[]? AcceptLanguageNormalizationListArray => _acceptLanguageNormalizationList?.Values;

    /// <summary>Additional request headers included in HTTP cache identity.</summary>
    public IReadOnlyList<string>? VaryByHeaders
    {
        get => _varyByHeaders;
        init => _varyByHeaders = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _varyByHeaders;
    internal string[]? VaryByHeadersArray => _varyByHeaders?.Values;

    /// <summary>Query key allowlist included in HTTP cache identity.</summary>
    public IReadOnlyList<string>? VaryByQueryKeys
    {
        get => _varyByQueryKeys;
        init => _varyByQueryKeys = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _varyByQueryKeys;
    internal string[]? VaryByQueryKeysArray => _varyByQueryKeys?.Values;

    /// <summary>Additional query keys ignored by HTTP cache identity.</summary>
    public IReadOnlyList<string>? IgnoreQueryKeys
    {
        get => _ignoreQueryKeys;
        init => _ignoreQueryKeys = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _ignoreQueryKeys;
    internal string[]? IgnoreQueryKeysArray => _ignoreQueryKeys?.Values;

    /// <summary>Cookie names included in HTTP cache identity.</summary>
    public IReadOnlyList<string>? VaryByCookies
    {
        get => _varyByCookies;
        init => _varyByCookies = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _varyByCookies;
    internal string[]? VaryByCookiesArray => _varyByCookies?.Values;

    /// <summary>Whether response Vary headers are emitted for non-secret varied headers.</summary>
    public bool EmitResponseVary { get; init; } = true;

    /// <summary>ETag mode for Output Cache responses.</summary>
    public ETagMode ETagMode { get; init; } = ETagMode.Version;

    /// <summary>Precomputed Version ETag.</summary>
    public StringValues ETag
    {
        get => _eTag;
        init => _eTag = value.Count switch
        {
            0 => null,
            1 => value[0],
            _ => throw new ArgumentException("A domain ETag must contain a single value.", nameof(value))
        };
    }
    private readonly string? _eTag;

    /// <summary>HTTP status codes that may be stored in Output Cache.</summary>
    public IReadOnlyList<int> CacheableStatusCodes
    {
        get => _cacheableStatusCodes;
        init => _cacheableStatusCodes = new(value ?? throw new ArgumentNullException(nameof(value)));
    }
    private readonly SnapshotList<int> _cacheableStatusCodes = DefaultStatusCodes;
    internal int[] CacheableStatusCodesArray => _cacheableStatusCodes.Values;

    /// <summary>Canonical spellings for single, parameter-free Accept-Encoding tokens; composite headers are retained.</summary>
    public IReadOnlyList<string>? EncodingNormalizationList
    {
        get => _encodingNormalizationList;
        init => _encodingNormalizationList = value is null ? null : new(value);
    }
    private readonly SnapshotList<string>? _encodingNormalizationList = DefaultEncodings;
    internal string[]? EncodingNormalizationListArray => _encodingNormalizationList?.Values;

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
