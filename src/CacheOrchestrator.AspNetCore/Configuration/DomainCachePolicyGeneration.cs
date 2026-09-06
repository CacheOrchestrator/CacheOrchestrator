using System.Security.Cryptography;
using System.Text;

namespace CacheOrchestrator.Configuration;

internal static class DomainCachePolicyGeneration
{
    public static void Apply(DomainHttpCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.OutputCachePolicyGeneration = Compute(options, outputCache: true);
        options.DataCachePolicyGeneration = Compute(options, outputCache: false);
    }

    private static string Compute(DomainHttpCacheOptions options, bool outputCache)
    {
        var value = new StringBuilder(384);
        Add(value, "authBypassMode", options.AuthBypassMode);
        Add(value, "varyOutputCacheByUser", options.VaryOutputCacheByUser);
        Add(value, "treatAuthorizationAsAuthSignal", options.TreatAuthorizationAsAuthSignal);
        Add(value, "authVaryIncludeAuthorizationHash", options.AuthVaryIncludeAuthorizationHash);
        Add(value, "varyByAuthClaims", options.VaryByAuthClaims);
        Add(value, "varyByAccept", options.VaryByAccept);
        Add(value, "acceptNormalizationList", options.AcceptNormalizationList);
        Add(value, "varyByAcceptLanguage", options.VaryByAcceptLanguage);
        Add(value, "acceptLanguageNormalizationList", options.AcceptLanguageNormalizationList);
        Add(value, "varyByHeaders", options.VaryByHeaders);
        Add(value, "varyByQueryKeys", options.VaryByQueryKeys);
        Add(value, "ignoreQueryKeys", options.IgnoreQueryKeys);
        Add(value, "varyByCookies", options.VaryByCookies);
        Add(value, "encodingNormalizationList", options.EncodingNormalizationList);

        if (outputCache)
        {
            Add(value, "varyByHost", options.OutputCacheVaryByHost);
        }
        else
        {
            Add(value, "dataCacheRespectAuthBypass", options.DataCacheRespectAuthBypass);
            Add(value, "varyOnPublicAddress", options.DataCacheVaryOnPublicAddress);
            Add(value, "varyOnEncoding", options.DataCacheVaryOnEncoding);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static void Add<T>(StringBuilder builder, string name, T value) where T : struct
    {
        builder.Append(name).Append('=').Append(value).Append(';');
    }

    private static void Add(StringBuilder builder, string name, string[]? values)
    {
        builder.Append(name).Append('=');
        if (values is null)
        {
            builder.Append("null;");
            return;
        }

        builder.Append(values.Length).Append(':');
        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i] ?? string.Empty;
            builder.Append(value.Length).Append(':').Append(value).Append(';');
        }
    }
}
