using CacheOrchestrator.Configuration;
using CacheOrchestrator.Identity;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;

namespace CacheOrchestrator.DataCache;

/// <summary>
/// Default high-performance key generator using XxHash3.
/// </summary>
public sealed class DefaultDomainKeyGenerator : IDomainKeyGenerator
{
    private static readonly byte[] PrefixRoute = "r:"u8.ToArray();
    private static readonly byte[] PrefixParam = "|p:"u8.ToArray();
    private static readonly byte[] PrefixPath = "path:"u8.ToArray();
    private static readonly byte[] PrefixMethod = "|m:"u8.ToArray();
    private static readonly byte[] PrefixQuery = "|q:"u8.ToArray();
    private static readonly byte[] PrefixEnc = "|e:"u8.ToArray();
    private static readonly byte[] PrefixHdr = "|hdr:"u8.ToArray();
    private static readonly byte[] PrefixVal = "|v:"u8.ToArray();
    private static readonly byte[] PrefixScheme = "|s:"u8.ToArray();
    private static readonly byte[] PrefixHost = "|h:"u8.ToArray();
    private static readonly byte[] EqualsSign = "="u8.ToArray();
    private static readonly byte[] Colon = ":"u8.ToArray();

    private readonly CacheVaryMaterializer _materializer;

    /// <summary>Creates a generator with no <see cref="ICacheVaryContributor"/> registrations.</summary>
    public DefaultDomainKeyGenerator()
        : this(new CacheVaryMaterializer())
    {
    }

    /// <summary>Creates a generator that uses the shared <paramref name="materializer"/>.</summary>
    public DefaultDomainKeyGenerator(CacheVaryMaterializer materializer)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        _materializer = materializer;
    }

    /// <summary>Creates a generator that runs the given contributors after built-in vary rules.</summary>
    public DefaultDomainKeyGenerator(IEnumerable<ICacheVaryContributor> contributors)
        : this(new CacheVaryMaterializer(contributors))
    {
    }

    /// <inheritdoc />
    public string Generate(DomainCacheKeyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Generate(context.Options, context.HttpContext, context.Shape);
    }

    /// <summary>Creates a key from explicit inputs.</summary>
    public string Generate(
        DomainHttpCacheOptions opts,
        HttpContext http,
        DomainCacheKeyShape shape = DomainCacheKeyShape.Automatic)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(http);

        XxHash3 hasher = new();

        Span<byte> byteBuffer = stackalloc byte[512];
        Span<char> charBuffer = stackalloc char[256];

        byte[]? rentedBytes = null;
        char[]? rentedChars = null;

        try
        {
            CacheVaryMaterial vary = _materializer.Build(http, opts, CacheVarySurface.DataCache);
            AppendString(
                hasher,
                opts.DataCachePolicyGeneration,
                ref byteBuffer,
                ref rentedBytes,
                ref charBuffer,
                ref rentedChars,
                lowercase: false);

            // 0. Entity identity (CRUD) — both kind and id are required; no id-only key shape.
            // Lookup string is co3:…:e:{hash}; kind/id are hash material only (tags carry them for purge).
            ICacheOrchestratorFeature? feature = http.Features.Get<ICacheOrchestratorFeature>();
            if (shape != DomainCacheKeyShape.Url
                && feature?.EntityKind is { Length: > 0 } entityKind
                && feature.ResourceId is { Length: > 0 } resourceId)
            {
                AppendRaw(hasher, "id:"u8);
                AppendString(hasher, entityKind, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);
                AppendRaw(hasher, ":"u8);
                AppendString(hasher, resourceId, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);

                AppendVaryMaterial(hasher, http, opts, vary, includeQuery: false, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
                AppendIdentityMaterial(hasher, http, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);

                if (opts.DataCacheVaryOnPublicAddress)
                    AppendPublicAddress(hasher, http, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);

                ulong resourceHash = hasher.GetCurrentHashAsUInt64();
                return string.Create(
                    null,
                    stackalloc char[160],
                    $"{opts.CoreOptions.PhysicalKeyPrefix}e:{resourceHash:x16}");
            }

            // 1. Route / path
            if (http.GetEndpoint() is RouteEndpoint endpoint)
            {
                AppendRaw(hasher, PrefixRoute);
                AppendString(hasher, endpoint.RoutePattern.RawText, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);

                foreach (RoutePatternParameterPart p in endpoint.RoutePattern.Parameters)
                {
                    AppendRaw(hasher, PrefixParam);

                    if (http.Request.RouteValues.TryGetValue(p.Name, out object? value) && value is not null)
                    {
                        if (value is string s)
                            AppendString(hasher, s, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);
                        else
                            AppendString(hasher, value.ToString(), ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);
                    }
                }
            }
            else
            {
                AppendRaw(hasher, PrefixPath);
                AppendString(hasher, http.Request.Path.Value, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);
            }

            // 1b. HTTP method — URL-shaped keys must not share GET/POST (or other) payloads.
            // Entity-shaped keys stay method-agnostic (resource identity). Method is upper-invariant.
            AppendRaw(hasher, PrefixMethod);
            AppendString(
                hasher,
                http.Request.Method.ToUpperInvariant(),
                ref byteBuffer,
                ref rentedBytes,
                ref charBuffer,
                ref rentedChars,
                lowercase: false);

            // 2. Query + header/auth/custom vary (+ encoding via materializer)
            AppendVaryMaterial(hasher, http, opts, vary, includeQuery: true, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);

            // 2b. Endpoint cache identity (only when already resolved onto the request feature)
            AppendIdentityMaterial(hasher, http, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);

            // 3. Public address
            if (opts.DataCacheVaryOnPublicAddress)
                AppendPublicAddress(hasher, http, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);

            ulong hash = hasher.GetCurrentHashAsUInt64();
            return string.Create(null, stackalloc char[160], $"{opts.CoreOptions.PhysicalKeyPrefix}u:{hash:x16}");
        }
        finally
        {
            if (rentedBytes != null)
                ArrayPool<byte>.Shared.Return(rentedBytes);
            if (rentedChars != null)
                ArrayPool<char>.Shared.Return(rentedChars);
        }
    }

    private static void AppendPublicAddress(
        XxHash3 hasher,
        HttpContext http,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars)
    {
        AppendRaw(hasher, PrefixScheme);
        AppendString(hasher, http.Request.Scheme, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: true);

        AppendRaw(hasher, PrefixHost);
        AppendString(hasher, http.Request.Host.Value, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: true);
    }

    private static void AppendIdentityMaterial(
        XxHash3 hasher,
        HttpContext http,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars)
    {
        CacheIdentityFeature? feature = http.Features.Get<CacheIdentityFeature>();
        if (feature is null
            || !feature.Resolved
            || feature.Material is null
            || feature.Material.Values.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> values = feature.Material.Values;
        AppendSortedValues(
            hasher,
            values,
            CacheIdentityApplicator.VaryValuePrefix,
            ref byteBuffer,
            ref rentedBytes,
            ref charBuffer,
            ref rentedChars);
    }

    private static void AppendVaryMaterial(
        XxHash3 hasher,
        HttpContext http,
        DomainHttpCacheOptions opts,
        CacheVaryMaterial vary,
        bool includeQuery,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars)
    {
        if (includeQuery)
        {
            IReadOnlyList<string> queryKeys = vary.QueryKeys;
            if (queryKeys.Count > 0)
            {
                string[] keys = ArrayPool<string>.Shared.Rent(queryKeys.Count);
                try
                {
                    for (int i = 0; i < queryKeys.Count; i++)
                        keys[i] = queryKeys[i];

                    int keyCount = queryKeys.Count;
                    if (keyCount > 1)
                        Array.Sort(keys, 0, keyCount, StringComparer.OrdinalIgnoreCase);

                    IQueryCollection query = http.Request.Query;
                    for (int i = 0; i < keyCount; i++)
                    {
                        string key = keys[i];
                        AppendRaw(hasher, PrefixQuery);
                        AppendString(hasher, key, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);
                        AppendRaw(hasher, EqualsSign);
                        AppendStringValues(hasher, query[key], ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(keys);
                }
            }
        }

        // Header names (non-sensitive): hash current request values.
        // Accept-Encoding uses the historical "|e:" prefix so existing keys stay stable when only encoding varies.
        for (int i = 0; i < vary.HeaderNames.Count; i++)
        {
            string headerName = vary.HeaderNames[i];
            StringValues values = http.Request.Headers[headerName];
            if (values.Count == 0)
                continue;

            if (string.Equals(headerName, HeaderNames.AcceptEncoding, StringComparison.OrdinalIgnoreCase))
            {
                AppendRaw(hasher, PrefixEnc);
                AppendStringValues(hasher, values, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
            }
            else
            {
                AppendRaw(hasher, PrefixHdr);
                AppendString(hasher, headerName, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: true);
                AppendRaw(hasher, EqualsSign);
                AppendStringValues(hasher, values, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
            }
        }

        // Named values (auth-user, hashed cookies/headers, contributor values) — sorted for stability.
        if (vary.Values.Count > 0)
        {
            AppendSortedValues(
                hasher,
                vary.Values,
                keyPrefix: null,
                ref byteBuffer,
                ref rentedBytes,
                ref charBuffer,
                ref rentedChars);
        }
    }

    private static void AppendSortedValues(
        XxHash3 hasher,
        IReadOnlyDictionary<string, string> values,
        string? keyPrefix,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars)
    {
        if (values.Count <= 3)
        {
            string? first = null;
            string? second = null;
            string? third = null;
            int index = 0;
            foreach (string key in values.Keys)
            {
                if (index == 0)
                    first = key;
                else if (index == 1)
                    second = key;
                else
                    third = key;
                index++;
            }

            // Allocation optimization: sort the common one-to-three-key case without creating an array.
            CompareExchange(ref first, ref second);
            CompareExchange(ref second, ref third);
            CompareExchange(ref first, ref second);

            if (first is not null)
                AppendNamedValue(hasher, first, values[first], keyPrefix, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
            if (second is not null)
                AppendNamedValue(hasher, second, values[second], keyPrefix, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
            if (third is not null)
                AppendNamedValue(hasher, third, values[third], keyPrefix, ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars);
            return;
        }

        // Allocation optimization: managed string references cannot be stackallocated, so sorting uses a pooled array.
        string[] keys = ArrayPool<string>.Shared.Rent(values.Count);
        try
        {
            int count = 0;
            foreach (string key in values.Keys)
                keys[count++] = key;

            Array.Sort(keys, 0, count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = keys[i];
                AppendNamedValue(
                    hasher,
                    key,
                    values[key],
                    keyPrefix,
                    ref byteBuffer,
                    ref rentedBytes,
                    ref charBuffer,
                    ref rentedChars);
            }
        }
        finally
        {
            ArrayPool<string>.Shared.Return(keys, clearArray: true);
        }
    }

    private static void CompareExchange(ref string? left, ref string? right)
    {
        if (right is null || (left is not null && string.CompareOrdinal(left, right) <= 0))
            return;

        (left, right) = (right, left);
    }

    private static void AppendNamedValue(
        XxHash3 hasher,
        string key,
        string value,
        string? keyPrefix,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars)
    {
        AppendRaw(hasher, PrefixVal);
        AppendString(
            hasher,
            keyPrefix is null ? key : keyPrefix + key,
            ref byteBuffer,
            ref rentedBytes,
            ref charBuffer,
            ref rentedChars,
            lowercase: false);
        AppendRaw(hasher, Colon);
        AppendString(
            hasher,
            value,
            ref byteBuffer,
            ref rentedBytes,
            ref charBuffer,
            ref rentedChars,
            lowercase: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendRaw(XxHash3 hasher, ReadOnlySpan<byte> data) => hasher.Append(data);

    private static void AppendString(
        XxHash3 hasher,
        string? value,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars,
        bool lowercase)
    {
        if (value is null)
        {
            Span<byte> nullLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(nullLength, -1);
            hasher.Append(nullLength);
            return;
        }

        ReadOnlySpan<char> source = value.AsSpan();

        if (lowercase)
        {
            if (source.Length > charBuffer.Length)
            {
                if (rentedChars != null)
                    ArrayPool<char>.Shared.Return(rentedChars);
                rentedChars = ArrayPool<char>.Shared.Rent(source.Length);
                charBuffer = rentedChars;
            }

            int written = source.ToLowerInvariant(charBuffer);
            source = charBuffer[..written];
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(source.Length);
        if (maxBytes > byteBuffer.Length)
        {
            if (rentedBytes != null)
                ArrayPool<byte>.Shared.Return(rentedBytes);
            rentedBytes = ArrayPool<byte>.Shared.Rent(maxBytes);
            byteBuffer = rentedBytes;
        }

        int bytesWritten = Encoding.UTF8.GetBytes(source, byteBuffer);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytesWritten);
        hasher.Append(length);
        hasher.Append(byteBuffer[..bytesWritten]);
    }

    private static void AppendStringValues(
        XxHash3 hasher,
        StringValues values,
        ref Span<byte> byteBuffer,
        ref byte[]? rentedBytes,
        ref Span<char> charBuffer,
        ref char[]? rentedChars)
    {
        Span<byte> count = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(count, values.Count);
        hasher.Append(count);

        for (int i = 0; i < values.Count; i++)
        {
            AppendString(hasher, values[i], ref byteBuffer, ref rentedBytes, ref charBuffer, ref rentedChars, lowercase: false);
        }
    }
}
