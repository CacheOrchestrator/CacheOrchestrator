using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace CacheOrchestrator.Utilities;

/// <summary>
/// Utility methods for parsing and manipulating HTTP headers safely.
/// </summary>
internal static class HttpHelper
{
    private static readonly string[] TrackingExact =
    [
        "fbclid", "gclid", "msclkid", "ttclid", "_ga", "_gl"
    ];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="key"/> is a known analytics/tracking
    /// query parameter (case-insensitive).
    /// <c>utm_*</c> is a prefix; <c>_ga</c>/<c>_gl</c> match exactly or as <c>_ga_</c>/<c>_gl_</c>
    /// (GA4 / linker). Click ids are exact so <c>_game</c> is not treated as tracking.
    /// </summary>
    public static bool IsTrackingParameter(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        if (key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.StartsWith("_ga_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("_gl_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] exact = TrackingExact;
        for (int i = 0; i < exact.Length; i++)
        {
            if (key.Equals(exact[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when any Cache-Control <paramref name="header"/> value
    /// has a comma-separated directive whose <em>name</em> equals <paramref name="directive"/>
    /// (case-insensitive). <c>s-maxage</c> does not match <c>max-age</c>; <c>no-storey</c>
    /// does not match <c>no-store</c>.
    /// </summary>
    public static bool ContainsCacheDirective(StringValues header, string directive)
    {
        if (header.Count == 0 || string.IsNullOrEmpty(directive))
            return false;

        for (int i = 0; i < header.Count; i++)
        {
            string? value = header[i];
            if (value is not null && HeaderHasDirective(value, directive))
                return true;
        }

        return false;
    }

    private static bool HeaderHasDirective(string value, string directive)
    {
        int start = 0;
        int length = value.Length;
        while (start < length)
        {
            while (start < length && (value[start] is ' ' or '\t' or ','))
                start++;
            if (start >= length)
                break;

            int comma = value.IndexOf(',', start);
            int partEnd = comma < 0 ? length : comma;

            int tokenEnd = start;
            while (tokenEnd < partEnd && value[tokenEnd] is not ('=' or ' ' or '\t'))
                tokenEnd++;

            int tokenLen = tokenEnd - start;
            if (tokenLen == directive.Length
                && string.Compare(value, start, directive, 0, tokenLen, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }

            start = partEnd + 1;
        }

        return false;
    }

    public static void ApplyNoCache(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
    }

    /// <summary>
    /// Canonicalizes a single parameter-free negotiation token without selecting a representation.
    /// Composite values remain intact: only the endpoint knows its formatter, compression, and
    /// localization rules. In particular, never discard exclusions, wildcards, or regional subtags.
    /// </summary>
    internal static string NormalizeNegotiationHeader(StringValues current, string[] canonicalValues)
    {
        if (current.Count != 1)
            return current.ToString();

        string? value = current[0];
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        ReadOnlySpan<char> token = value.AsSpan().Trim();
        if (token.IndexOfAny(',', ';', '*') >= 0)
            return value;

        for (int i = 0; i < canonicalValues.Length; i++)
        {
            string? candidate = canonicalValues[i];
            if (!string.IsNullOrWhiteSpace(candidate)
                && token.Equals(candidate.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Trim();
            }
        }

        return value;
    }
}
