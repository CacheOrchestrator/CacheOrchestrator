using CacheOrchestrator.Configuration;
using CacheOrchestrator.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace CacheOrchestrator.Vary;

/// <summary>
/// Builds shared <see cref="CacheVaryMaterial"/> from domain options and optional
/// <see cref="ICacheVaryContributor"/> registrations.
/// </summary>
public sealed class CacheVaryMaterializer
{
    /// <summary>Maximum entries allowed in <c>VaryByHeaders</c>.</summary>
    public const int MaxVaryByHeaders = 8;

    /// <summary>Maximum entries allowed in <c>VaryByCookies</c>.</summary>
    public const int MaxVaryByCookies = 8;

    private static readonly string[] SensitiveHeaderNames =
    [
        HeaderNames.Authorization,
        HeaderNames.Cookie,
        "X-Api-Key",
        "X-Auth-Token",
        "Proxy-Authorization",
    ];
    private static readonly CacheVaryMaterial EmptyMaterial = new()
    {
        HeaderNames = Array.Empty<string>(),
        Values = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)),
        QueryKeys = Array.Empty<string>(),
        ResponseVaryHeaderNames = Array.Empty<string>(),
    };

    private readonly ICacheVaryContributor[] _contributors;

    /// <summary>Creates a materializer with no contributors (tests / fallback).</summary>
    public CacheVaryMaterializer()
        : this(Array.Empty<ICacheVaryContributor>())
    {
    }

    /// <summary>Creates a materializer that runs <paramref name="contributors"/> after built-in rules.</summary>
    public CacheVaryMaterializer(IEnumerable<ICacheVaryContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors = contributors.OrderBy(c => c.Order).ToArray();
    }

    /// <summary>
    /// Builds vary material for the given surface. Built-in domain settings run first;
    /// registered contributors run afterward in <see cref="ICacheVaryContributor.Order"/>.
    /// </summary>
    public CacheVaryMaterial Build(HttpContext http, DomainHttpCacheOptions options, CacheVarySurface surface)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        // Allocation optimization: the anonymous no-vary path can reuse immutable material and skip Builder.
        if (_contributors.Length == 0 && HasNoVaryMaterial(http, options, surface))
            return EmptyMaterial;

        Builder builder = new(http);

        ApplyBuiltIn(http, options, surface, builder);

        if (_contributors.Length > 0)
        {
            CacheVaryContext context = new()
            {
                HttpContext = http,
                Options = options,
                Surface = surface,
            };
            for (int i = 0; i < _contributors.Length; i++)
                _contributors[i].Contribute(context, builder);
        }

        return builder.ToMaterial();
    }

    private static bool HasNoVaryMaterial(
        HttpContext http,
        DomainHttpCacheOptions options,
        CacheVarySurface surface)
    {
        // Configured header-vary dimensions always produce at least Response Vary names,
        // even when the request omits the header (intermediate caches need the advertisement).
        if (surface == CacheVarySurface.OutputCache || options.DataCacheVaryOnEncoding)
            return false;

        if (options.VaryByAccept)
            return false;
        if (options.VaryByAcceptLanguage)
            return false;

        if (options.VaryByHeaders is { Length: > 0 } varyHeaders)
        {
            for (int i = 0; i < varyHeaders.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(varyHeaders[i]))
                    return false;
            }
        }

        if (options.VaryByCookies is { Length: > 0 } varyCookies)
        {
            IRequestCookieCollection cookies = http.Request.Cookies;
            for (int i = 0; i < varyCookies.Length; i++)
            {
                string? name = varyCookies[i];
                if (!string.IsNullOrWhiteSpace(name) && cookies.ContainsKey(name.Trim()))
                    return false;
            }
        }

        if (DomainAuthEvaluator.HasAuthSignal(http, options)
            && options.VaryOutputCacheByUser
            && ShouldIncludeAuthUserVary(options, surface))
        {
            return false;
        }

        return http.Request.Query.Count == 0;
    }

    private static void ApplyBuiltIn(
        HttpContext http,
        DomainHttpCacheOptions options,
        CacheVarySurface surface,
        Builder builder)
    {
        // Accept-Encoding: OC always varies (historical). Fusion when DataCacheVaryOnEncoding.
        // Advertise Vary even when the request omits the header so intermediates do not pin a default.
        bool considerEncoding = surface == CacheVarySurface.OutputCache || options.DataCacheVaryOnEncoding;
        StringValues ae = http.Request.Headers.AcceptEncoding;
        if (considerEncoding)
        {
            if (ae.Count > 0)
            {
                if (options.EncodingNormalizationList is { Length: > 0 } encodingNormalization)
                {
                    builder.AddNormalizedHeader(
                        HeaderNames.AcceptEncoding,
                        "normalized:accept-encoding",
                        HttpHelper.NormalizeNegotiationHeader(ae, encodingNormalization));
                }
                else
                {
                    builder.AddHeader(HeaderNames.AcceptEncoding);
                }
            }
            else
            {
                builder.AdvertiseResponseHeader(HeaderNames.AcceptEncoding);
            }
        }

        if (options.VaryByAccept)
        {
            StringValues accept = http.Request.Headers.Accept;
            if (accept.Count > 0)
            {
                if (options.AcceptNormalizationList is { Length: > 0 } acceptNormalization)
                {
                    builder.AddNormalizedHeader(
                        HeaderNames.Accept,
                        "normalized:accept",
                        HttpHelper.NormalizeNegotiationHeader(accept, acceptNormalization));
                }
                else
                {
                    builder.AddHeader(HeaderNames.Accept);
                }
            }
            else
            {
                builder.AdvertiseResponseHeader(HeaderNames.Accept);
            }
        }

        if (options.VaryByAcceptLanguage)
        {
            StringValues al = http.Request.Headers.AcceptLanguage;
            if (al.Count > 0)
            {
                if (options.AcceptLanguageNormalizationList is { Length: > 0 } languageNormalization)
                {
                    builder.AddNormalizedHeader(
                        HeaderNames.AcceptLanguage,
                        "normalized:accept-language",
                        HttpHelper.NormalizeNegotiationHeader(al, languageNormalization));
                }
                else
                {
                    builder.AddHeader(HeaderNames.AcceptLanguage);
                }
            }
            else
            {
                builder.AdvertiseResponseHeader(HeaderNames.AcceptLanguage);
            }
        }

        string[]? extraHeaders = options.VaryByHeaders;
        if (extraHeaders is { Length: > 0 })
        {
            for (int i = 0; i < extraHeaders.Length; i++)
            {
                string name = extraHeaders[i];
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                name = name.Trim();
                if (!http.Request.Headers.ContainsKey(name))
                {
                    // Non-sensitive configured headers must still appear in response Vary.
                    if (!IsSensitiveHeader(name))
                        builder.AdvertiseResponseHeader(name);
                    continue;
                }

                builder.AddHeader(name);
            }
        }

        string[]? cookies = options.VaryByCookies;
        if (cookies is { Length: > 0 })
        {
            IRequestCookieCollection requestCookies = http.Request.Cookies;
            for (int i = 0; i < cookies.Length; i++)
            {
                string cookieName = cookies[i];
                if (string.IsNullOrWhiteSpace(cookieName))
                    continue;
                cookieName = cookieName.Trim();
                if (!requestCookies.TryGetValue(cookieName, out string? value) || value is null)
                    continue;
                builder.AddHashedValue("cookie:" + cookieName, value);
            }
        }

        bool hasAuthSignal = DomainAuthEvaluator.HasAuthSignal(http, options);
        if (hasAuthSignal && options.VaryOutputCacheByUser && ShouldIncludeAuthUserVary(options, surface))
        {
            string userVary = DomainAuthEvaluator.ResolveAuthenticatedVaryKey(http, options);
            builder.AddValue("auth-user", userVary);
        }

        builder.SetQueryKeys(ResolveQueryKeys(http.Request.Query, options));
    }

    /// <summary>
    /// Output Cache always applies auth-user when varying by user.
    /// Fusion only does so when auth caching is intentional (<see cref="AuthBypassMode.Never"/>)
    /// or when <see cref="DomainHttpCacheOptions.VaryByAuthClaims"/> is configured — preserving
    /// historical Fusion keys under the default auth-bypass modes.
    /// </summary>
    private static bool ShouldIncludeAuthUserVary(DomainHttpCacheOptions options, CacheVarySurface surface)
    {
        if (surface == CacheVarySurface.OutputCache)
            return true;

        if (options.VaryByAuthClaims is { Length: > 0 })
            return true;

        return DomainAuthEvaluator.GetEffectiveAuthBypassMode(options) == AuthBypassMode.Never;
    }

    /// <summary>
    /// Resolves query keys for cache identity.
    /// <see langword="null"/> when no keys matched (treat as empty for consumers);
    /// empty array when allowlist is explicitly empty (no query vary);
    /// otherwise the selected key list.
    /// </summary>
    /// <remarks>
    /// When <see cref="DomainHttpCacheOptions.VaryByQueryKeys"/> is <see langword="null"/>, all non-tracking
    /// keys (minus <see cref="DomainHttpCacheOptions.IgnoreQueryKeys"/>) are returned — historical behaviour.
    /// </remarks>
    public static IReadOnlyList<string> ResolveQueryKeys(IQueryCollection query, DomainHttpCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        string[]? allow = options.VaryByQueryKeys;
        string[]? ignore = options.IgnoreQueryKeys;

        if (allow is { Length: 0 })
            return Array.Empty<string>();

        if (allow is null)
        {
            if (query.Count == 0)
                return Array.Empty<string>();

            List<string> list = new(query.Count);
            foreach (string key in query.Keys)
            {
                if (HttpHelper.IsTrackingParameter(key) || IsIgnoredQueryKey(key, ignore))
                    continue;
                list.Add(key);
            }

            return list;
        }

        List<string> selected = new(allow.Length);
        for (int i = 0; i < allow.Length; i++)
        {
            string key = allow[i];
            if (string.IsNullOrWhiteSpace(key))
                continue;
            key = key.Trim();
            if (!query.ContainsKey(key))
                continue;
            if (HttpHelper.IsTrackingParameter(key) || IsIgnoredQueryKey(key, ignore))
                continue;
            selected.Add(key);
        }

        return selected;
    }

    /// <summary>
    /// Collects query keys as <see cref="StringValues"/> for Output Cache.
    /// </summary>
    public static StringValues CollectQueryKeysForOutputCache(IQueryCollection query, DomainHttpCacheOptions options)
    {
        IReadOnlyList<string> keys = ResolveQueryKeys(query, options);
        if (keys.Count == 0)
            return StringValues.Empty;
        if (keys.Count == 1)
            return new StringValues(keys[0]);
        return new StringValues(keys is string[] arr ? arr : keys.ToArray());
    }

    private static bool IsIgnoredQueryKey(string key, string[]? ignore)
    {
        if (ignore is null || ignore.Length == 0)
            return false;
        for (int i = 0; i < ignore.Length; i++)
        {
            string? item = ignore[i];
            if (string.IsNullOrWhiteSpace(item))
                continue;
            if (string.Equals(key, item.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Returns true when the header value must never appear in plaintext vary material.</summary>
    public static bool IsSensitiveHeader(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
            return false;
        for (int i = 0; i < SensitiveHeaderNames.Length; i++)
        {
            if (string.Equals(headerName, SensitiveHeaderNames[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Hashes raw material to a stable hex segment.</summary>
    public static string HashSegment(string raw)
    {
        string value = raw ?? string.Empty;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        // Allocation optimization: small secrets hash from the stack; larger headers use a pooled buffer.
        Span<byte> bytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            int written = Encoding.UTF8.GetBytes(value, bytes);
            ulong hash = XxHash3.HashToUInt64(bytes[..written]);
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private sealed class Builder : ICacheVaryBuilder
    {
        private const int HeaderSetThreshold = 4;
        private static readonly IReadOnlyDictionary<string, string> EmptyValues =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

        private readonly HttpContext _http;
        private List<string>? _headers;
        private HashSet<string>? _headerSet;
        private Dictionary<string, string>? _values;
        private List<string>? _responseVary;
        private IReadOnlyList<string> _queryKeys = Array.Empty<string>();

        public Builder(HttpContext http)
        {
            _http = http;
        }

        public void AddHeader(string headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName))
                return;
            headerName = headerName.Trim();

            // Sensitive headers must never enter OC HeaderNames (framework would store raw values).
            if (IsSensitiveHeader(headerName))
            {
                StringValues raw = _http.Request.Headers[headerName];
                if (raw.Count == 0)
                    return;
                AddHashedValue("hdr:" + headerName, raw.ToString());
                return;
            }

            if (_headerSet is not null)
            {
                if (!_headerSet.Add(headerName))
                    return;
            }
            else if (_headers is not null)
            {
                for (int i = 0; i < _headers.Count; i++)
                {
                    if (string.Equals(_headers[i], headerName, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                if (_headers.Count == HeaderSetThreshold)
                {
                    _headerSet = new HashSet<string>(_headers, StringComparer.OrdinalIgnoreCase)
                    {
                        headerName
                    };
                }
            }

            (_headers ??= []).Add(headerName);
            (_responseVary ??= []).Add(headerName);
        }

        public void AddValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            (_values ??= new Dictionary<string, string>(StringComparer.Ordinal))[key.Trim()] = value ?? string.Empty;
        }

        public void AddHashedValue(string key, string raw) =>
            AddValue(key, "h:" + HashSegment(raw));

        public void AddNormalizedHeader(string headerName, string key, string value)
        {
            AddValue(key, value);
            AddResponseVaryHeader(headerName);
        }

        /// <summary>
        /// Advertises a response <c>Vary</c> token without adding key material
        /// (used when the request omits a configured vary header).
        /// </summary>
        public void AdvertiseResponseHeader(string headerName) =>
            AddResponseVaryHeader(headerName);

        private void AddResponseVaryHeader(string headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName))
                return;
            headerName = headerName.Trim();

            if (_responseVary is not null)
            {
                for (int i = 0; i < _responseVary.Count; i++)
                {
                    if (string.Equals(_responseVary[i], headerName, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            (_responseVary ??= []).Add(headerName);
        }

        public void SetQueryKeys(IReadOnlyList<string> keys) => _queryKeys = keys;

        public CacheVaryMaterial ToMaterial() => new()
        {
            HeaderNames = (IReadOnlyList<string>?)_headers ?? Array.Empty<string>(),
            Values = _values ?? EmptyValues,
            QueryKeys = _queryKeys,
            ResponseVaryHeaderNames = (IReadOnlyList<string>?)_responseVary ?? Array.Empty<string>(),
        };
    }
}
