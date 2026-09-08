using CacheOrchestrator.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CacheOrchestrator.Configuration;

/// <summary>
/// Resolves ASP.NET Core domain policy and pins a snapshot on each request.
/// </summary>
internal sealed class RequestDomainCacheOptionsProvider : IRequestDomainCacheOptions, IDisposable
{
    private readonly IDomainCacheOptionsProvider _coreOptions;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _coreOptionsMonitor;
    private readonly IOptionsMonitor<CacheOrchestratorHttpOptions> _httpOptionsMonitor;
    private readonly IDomainRuntimeOverrideStore _coreRuntimeOverrides;
    private readonly IHttpDomainRuntimeOverrideStore _httpRuntimeOverrides;
    private readonly ILogger<RequestDomainCacheOptionsProvider> _logger;
    private ConcurrentDictionary<string, CachedHttpOptions> _globalCache = new(StringComparer.Ordinal);
    private readonly IDisposable? _coreChangeRegistration;
    private readonly IDisposable? _httpChangeRegistration;

    public RequestDomainCacheOptionsProvider(
        IDomainCacheOptionsProvider coreOptions,
        IOptionsMonitor<CacheOrchestratorOptions> coreOptionsMonitor,
        IOptionsMonitor<CacheOrchestratorHttpOptions> httpOptionsMonitor,
        ILogger<RequestDomainCacheOptionsProvider> logger,
        IDomainRuntimeOverrideStore coreRuntimeOverrides,
        IHttpDomainRuntimeOverrideStore httpRuntimeOverrides)
    {
        ArgumentNullException.ThrowIfNull(coreOptions);
        ArgumentNullException.ThrowIfNull(coreOptionsMonitor);
        ArgumentNullException.ThrowIfNull(httpOptionsMonitor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(coreRuntimeOverrides);
        ArgumentNullException.ThrowIfNull(httpRuntimeOverrides);

        _coreOptions = coreOptions;
        _coreOptionsMonitor = coreOptionsMonitor;
        _httpOptionsMonitor = httpOptionsMonitor;
        _logger = logger;
        _coreRuntimeOverrides = coreRuntimeOverrides;
        _httpRuntimeOverrides = httpRuntimeOverrides;
        _coreChangeRegistration = _coreOptionsMonitor.OnChange(_ => Interlocked.Exchange(ref _globalCache, new(StringComparer.Ordinal)));
        _httpChangeRegistration = _httpOptionsMonitor.OnChange(_ => Interlocked.Exchange(ref _globalCache, new(StringComparer.Ordinal)));
    }

    private sealed class CachedHttpOptions
    {
        public required DomainHttpCacheOptions Options { get; init; }
        public int CoreOverrideStamp { get; init; }
        public int HttpOverrideStamp { get; init; }
    }

    public void Dispose()
    {
        _coreChangeRegistration?.Dispose();
        _httpChangeRegistration?.Dispose();
    }

    public DomainHttpCacheOptions GetOrCreateDomainOptions(string domain)
    {
        string normalized = DomainName.Normalize(domain);
        int coreStamp = _coreRuntimeOverrides.GetStamp(normalized);
        int httpStamp = _httpRuntimeOverrides.GetStamp(normalized);

        ConcurrentDictionary<string, CachedHttpOptions> generation = Volatile.Read(ref _globalCache);
        if (generation.TryGetValue(normalized, out CachedHttpOptions? cached)
            && cached.CoreOverrideStamp == coreStamp
            && cached.HttpOverrideStamp == httpStamp)
        {
            return cached.Options;
        }

        DomainHttpCacheOptions options = CreateDomainOptions(normalized);
        generation[normalized] = new CachedHttpOptions
        {
            Options = options,
            CoreOverrideStamp = coreStamp,
            HttpOverrideStamp = httpStamp
        };
        return options;
    }

    public DomainHttpCacheOptions EnsureDomainOptions(HttpContext http, string domain)
    {
        ArgumentNullException.ThrowIfNull(http);

        string normalized = DomainName.Normalize(domain);
        ICacheOrchestratorFeature feature = CacheOrchestratorFeatureAccessor.GetOrCreate(http);

        if (feature.DomainOptions is { } cached)
        {
            if (string.Equals(cached.Domain, normalized, StringComparison.Ordinal))
                return cached;

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "Replacing request domain snapshot '{PreviousDomain}' with '{Domain}'.",
                    cached.Domain,
                    normalized);
            }
        }

        DomainHttpCacheOptions resolved = GetOrCreateDomainOptions(normalized);
        feature.DomainOptions = resolved;
        return resolved;
    }

    public DomainHttpCacheOptions? GetDomainOptions(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Features.Get<ICacheOrchestratorFeature>()?.DomainOptions;
    }

    private DomainHttpCacheOptions CreateDomainOptions(string domain)
    {
        DomainCacheOptions core = _coreOptions.GetOrCreateDomainOptions(domain);
        CacheOrchestratorHttpOptions options = _httpOptionsMonitor.CurrentValue;
        DomainHttpCacheSettings defaults = options.DomainDefaults;
        HttpDomainRuntimeOverride? overlay = _httpRuntimeOverrides.Get(domain);

        if (!options.Domains.TryGetValue(domain, out DomainHttpCacheSettings? domainSettings))
            domainSettings = new DomainHttpCacheSettings();

        static T Pick<T>(T? specific, T? global, T fallback) where T : struct =>
            specific ?? global ?? fallback;

        TimeSpan outputTtl = overlay?.OutputCacheTtl
            ?? Seconds(Pick(domainSettings.OutputCache?.TtlSeconds, defaults.OutputCache?.TtlSeconds, 3700));
        TimeSpan clientTtl = overlay?.ClientTtl
            ?? Seconds(Pick(domainSettings.ClientCache?.TtlSeconds, defaults.ClientCache?.TtlSeconds, 3600));
        TimeSpan clientTtlMin = overlay?.ClientTtlMin
            ?? Seconds(Pick(domainSettings.ClientCache?.TtlMinSeconds, defaults.ClientCache?.TtlMinSeconds, 60));

        var resolved = new DomainHttpCacheOptions
        {
            CoreOptions = core,
            OutputCacheEnabled = overlay?.OutputCacheEnabled
                ?? Pick(domainSettings.OutputCache?.Enabled, defaults.OutputCache?.Enabled, true),
            AuthBypassMode = ResolveAuthBypassMode(overlay, domainSettings, defaults),
            VaryOutputCacheByUser = overlay?.VaryOutputCacheByUser
                ?? Pick(domainSettings.VaryOutputCacheByUser, defaults.VaryOutputCacheByUser, true),
            TreatAuthorizationAsAuthSignal = overlay?.TreatAuthorizationAsAuthSignal
                ?? Pick(domainSettings.TreatAuthorizationAsAuthSignal, defaults.TreatAuthorizationAsAuthSignal, true),
            AuthVaryIncludeAuthorizationHash = overlay?.AuthVaryIncludeAuthorizationHash
                ?? Pick(domainSettings.AuthVaryIncludeAuthorizationHash, defaults.AuthVaryIncludeAuthorizationHash, true),
            VaryByAuthClaims = Copy(overlay?.VaryByAuthClaims
                ?? domainSettings.VaryByAuthClaims
                ?? defaults.VaryByAuthClaims),
            DataCacheRespectAuthBypass = overlay?.DataCacheRespectAuthBypass
                ?? Pick(domainSettings.DataCacheRespectAuthBypass, defaults.DataCacheRespectAuthBypass, true),
            ClientForcePrivateWhenAuthenticated = overlay?.ClientForcePrivateWhenAuthenticated
                ?? Pick(
                    domainSettings.ClientCache?.ForcePrivateWhenAuthenticated,
                    defaults.ClientCache?.ForcePrivateWhenAuthenticated,
                    true),
            VaryByAccept = overlay?.VaryByAccept
                ?? Pick(domainSettings.VaryByAccept, defaults.VaryByAccept, true),
            // Raw Accept is the default. Configured tokens may canonicalize spelling, never
            // select a formatter or discard other negotiation information.
            AcceptNormalizationList = Copy(overlay?.AcceptNormalizationList
                ?? domainSettings.AcceptNormalizationList
                ?? defaults.AcceptNormalizationList),
            VaryByAcceptLanguage = overlay?.VaryByAcceptLanguage
                ?? Pick(domainSettings.VaryByAcceptLanguage, defaults.VaryByAcceptLanguage, false),
            AcceptLanguageNormalizationList = Copy(overlay?.AcceptLanguageNormalizationList
                ?? domainSettings.AcceptLanguageNormalizationList
                ?? defaults.AcceptLanguageNormalizationList),
            VaryByHeaders = Copy(overlay?.VaryByHeaders
                ?? domainSettings.VaryByHeaders
                ?? defaults.VaryByHeaders),
            VaryByQueryKeys = Copy(overlay?.VaryByQueryKeys
                ?? domainSettings.VaryByQueryKeys
                ?? defaults.VaryByQueryKeys),
            IgnoreQueryKeys = Copy(overlay?.IgnoreQueryKeys
                ?? domainSettings.IgnoreQueryKeys
                ?? defaults.IgnoreQueryKeys),
            VaryByCookies = Copy(overlay?.VaryByCookies
                ?? domainSettings.VaryByCookies
                ?? defaults.VaryByCookies),
            EmitResponseVary = overlay?.EmitResponseVary
                ?? Pick(domainSettings.EmitResponseVary, defaults.EmitResponseVary, true),
            ETagMode = overlay?.ETagMode
                ?? domainSettings.OutputCache?.ETagMode
                ?? defaults.OutputCache?.ETagMode
                ?? ETagMode.Version,
            ETag = CacheETagFactory.FromVersion(core.Version),
            CacheableStatusCodes = Copy(domainSettings.OutputCache?.CacheableStatusCodes
                ?? defaults.OutputCache?.CacheableStatusCodes
                ?? [200])!,
            EncodingNormalizationList = Copy(domainSettings.OutputCache?.EncodingNormalizationList
                ?? defaults.OutputCache?.EncodingNormalizationList
                ?? ["br", "gzip"]),
            ClientCacheability = overlay?.ClientCacheability
                ?? domainSettings.ClientCache?.Cacheability
                ?? defaults.ClientCache?.Cacheability
                ?? ClientCacheability.Public,
            ClientTtlSeconds = ToNonNegativeSeconds(clientTtl),
            ClientTtlMinSeconds = ToNonNegativeSeconds(clientTtlMin),
            ScheduledUpdateUtc = overlay?.ScheduledUpdateUtc
                ?? domainSettings.ClientCache?.ScheduledUpdateUtc
                ?? defaults.ClientCache?.ScheduledUpdateUtc,
            ClientMustRevalidateNearUpdate = overlay?.ClientMustRevalidateNearUpdate
                ?? Pick(
                    domainSettings.ClientCache?.MustRevalidateNearUpdate,
                    defaults.ClientCache?.MustRevalidateNearUpdate,
                    false),
            OutputTtl = outputTtl < TimeSpan.Zero ? TimeSpan.Zero : outputTtl,
            OutputCacheNamespace = options.OutputNamespace,
            DataCacheRespectNoStore = overlay?.DataCacheRespectNoStore
                ?? Pick(domainSettings.DataCache?.RespectNoStore, defaults.DataCache?.RespectNoStore, true),
            DataCacheVaryOnPublicAddress = overlay?.DataCacheVaryOnPublicAddress
                ?? Pick(domainSettings.DataCache?.VaryOnPublicAddress, defaults.DataCache?.VaryOnPublicAddress, true),
            DataCacheVaryOnEncoding = overlay?.DataCacheVaryOnEncoding
                ?? Pick(domainSettings.DataCache?.VaryOnEncoding, defaults.DataCache?.VaryOnEncoding, true),
            OutputCacheVaryByHost = overlay?.OutputCacheVaryByHost
                ?? Pick(domainSettings.OutputCache?.VaryByHost, defaults.OutputCache?.VaryByHost, true),
        };
        DomainCachePolicyGeneration.Apply(resolved);
        return resolved;
    }

    private static TimeSpan Seconds(int value) =>
        TimeSpan.FromSeconds(value < 0 ? 0 : value);

    private static T[]? Copy<T>(T[]? values) => values is null ? null : [.. values];

    private static int ToNonNegativeSeconds(TimeSpan value)
    {
        double seconds = value.TotalSeconds;
        if (seconds <= 0)
            return 0;
        if (seconds >= int.MaxValue)
            return int.MaxValue;
        return (int)Math.Round(seconds);
    }

    private static AuthBypassMode ResolveAuthBypassMode(
        HttpDomainRuntimeOverride? overlay,
        DomainHttpCacheSettings domain,
        DomainHttpCacheSettings defaults) =>
        overlay?.AuthBypassMode
        ?? domain.AuthBypassMode
        ?? defaults.AuthBypassMode
        ?? AuthBypassMode.AuthenticatedOrAuthorization;
}
