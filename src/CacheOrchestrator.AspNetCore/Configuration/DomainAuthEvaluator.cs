using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace CacheOrchestrator.Configuration;

/// <summary>
/// Shared auth-signal and bypass evaluation for Output Cache and FusionCache.
/// </summary>
public static class DomainAuthEvaluator
{
    /// <summary>
    /// Returns whether the request carries an auth signal under the domain's policy
    /// (authenticated identity and/or <c>Authorization</c> header).
    /// </summary>
    public static bool HasAuthSignal(HttpContext http, DomainHttpCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        if (http.User?.Identity?.IsAuthenticated == true)
            return true;

        return options.TreatAuthorizationAsAuthSignal
            && http.Request.Headers.ContainsKey(HeaderNames.Authorization);
    }

    /// <summary>
    /// Effective bypass mode — returns <see cref="DomainHttpCacheOptions.AuthBypassMode"/>.
    /// </summary>
    public static AuthBypassMode GetEffectiveAuthBypassMode(DomainHttpCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AuthBypassMode;
    }

    /// <summary>
    /// Returns whether Output Cache (and optionally FusionCache) should bypass caching
    /// for this request under <see cref="DomainHttpCacheOptions.AuthBypassMode"/>.
    /// </summary>
    public static bool ShouldBypassForAuth(HttpContext http, DomainHttpCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        return GetEffectiveAuthBypassMode(options) switch
        {
            AuthBypassMode.Never => false,
            AuthBypassMode.AuthenticatedIdentityOnly =>
                http.User?.Identity?.IsAuthenticated == true,
            AuthBypassMode.AuthorizationHeaderOnly =>
                http.Request.Headers.ContainsKey(HeaderNames.Authorization),
            AuthBypassMode.AuthenticatedOrAuthorization => HasAuthSignal(http, options),
            _ => HasAuthSignal(http, options),
        };
    }

    /// <summary>
    /// Builds the stable auth-user vary key (never includes raw Authorization / cookies).
    /// </summary>
    public static string ResolveAuthenticatedVaryKey(HttpContext http, DomainHttpCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        System.Security.Claims.ClaimsPrincipal? user = http.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            string[]? claimTypes = options.VaryByAuthClaimsArray;
            if (claimTypes is { Length: > 0 })
            {
                List<string> parts = new(claimTypes.Length);
                for (int i = 0; i < claimTypes.Length; i++)
                {
                    string type = claimTypes[i];
                    if (string.IsNullOrWhiteSpace(type))
                        continue;
                    string? value = user.FindFirst(type.Trim())?.Value;
                    if (!string.IsNullOrWhiteSpace(value))
                        parts.Add(type.Trim() + "=" + value);
                }

                if (parts.Count > 0)
                {
                    parts.Sort(StringComparer.Ordinal);
                    return "claims:" + string.Join(';', parts);
                }
            }

            string? name = user.Identity.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return "u:" + name;

            string? sub = user.FindFirst("sub")?.Value
                ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(sub))
                return "id:" + sub;
        }

        if (options.AuthVaryIncludeAuthorizationHash)
        {
            string? auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
            {
                ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(
                    System.Text.Encoding.UTF8.GetBytes(auth));
                return "ah:" + hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return "auth";
    }
}
