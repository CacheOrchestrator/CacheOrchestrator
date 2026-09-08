using CacheOrchestrator.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;

namespace CacheOrchestrator.FusionCache;

/// <summary>
/// Reads <c>Cache:DomainDefaults:FusionCache</c> and <c>Cache:Domains:{name}:FusionCache</c>,
/// then merges Admin runtime overlays from <see cref="IFusionDomainRuntimeOverrideStore"/>.
/// </summary>
internal sealed class FusionDomainSettingsProvider : IFusionDomainSettingsProvider, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IFusionDomainRuntimeOverrideStore _runtimeOverrides;
    private readonly string _configSection;
    private ConcurrentDictionary<string, CachedSettings> _cache = new(StringComparer.Ordinal);
    private readonly IDisposable _reloadRegistration;

    public FusionDomainSettingsProvider(
        IConfiguration configuration,
        IOptionsMonitor<CacheOrchestratorOptions> options,
        IFusionDomainRuntimeOverrideStore? runtimeOverrides = null,
        string configSection = "Cache")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        _configuration = configuration;
        _runtimeOverrides = runtimeOverrides ?? NullFusionDomainRuntimeOverrideStore.Instance;
        _configSection = string.IsNullOrWhiteSpace(configSection) ? "Cache" : configSection;
        _reloadRegistration = ChangeToken.OnChange(
            _configuration.GetReloadToken,
            () => Interlocked.Exchange(ref _cache, new(StringComparer.Ordinal)));
    }

    private sealed class CachedSettings
    {
        public required DomainFusionCacheSettings Settings { get; init; }
        public required int OverrideStamp { get; init; }
    }

    public void Dispose() => _reloadRegistration.Dispose();

    /// <inheritdoc />
    public DomainFusionCacheSettings Get(string domain)
    {
        domain = DomainName.Normalize(domain);
        int overrideStamp = _runtimeOverrides.GetStamp(domain);
        ConcurrentDictionary<string, CachedSettings> generation = Volatile.Read(ref _cache);
        if (generation.TryGetValue(domain, out CachedSettings? cached)
            && cached.OverrideStamp == overrideStamp)
        {
            return cached.Settings;
        }

        DomainFusionCacheSettings settings = CreateSettings(domain);
        generation[domain] = new CachedSettings
        {
            Settings = settings,
            OverrideStamp = overrideStamp
        };
        return settings;
    }

    private DomainFusionCacheSettings CreateSettings(string domain)
    {

        DomainFusionCacheSettings defaults = BindSection($"{_configSection}:DomainDefaults:FusionCache");
        DomainFusionCacheSettings specific = BindSection($"{_configSection}:Domains:{domain}:FusionCache");
        FusionDomainRuntimeOverride? overlay = _runtimeOverrides.Get(domain);

        static T Pick<T>(T? specific, T? global, T fallback) where T : struct =>
            specific ?? global ?? fallback;

        static TimeSpan FromSecondsOrOverlay(TimeSpan? overlay, int? specific, int? global, int fallbackSeconds)
        {
            if (overlay is { } o)
                return o < TimeSpan.Zero ? TimeSpan.Zero : o;
            int seconds = Pick(specific, global, fallbackSeconds);
            if (seconds < 0)
                seconds = 0;
            return TimeSpan.FromSeconds(seconds);
        }

        TimeSpan hardTtl = FromSecondsOrOverlay(overlay?.HardTtl, specific.HardTtlSeconds, defaults.HardTtlSeconds, 43200);
        TimeSpan failSafe = FromSecondsOrOverlay(overlay?.FailSafe, specific.FailSafeSeconds, defaults.FailSafeSeconds, 86400);
        TimeSpan jitter = FromSecondsOrOverlay(overlay?.Jitter, specific.JitterSeconds, defaults.JitterSeconds, 60);
        TimeSpan factorySoft = FromSecondsOrOverlay(
            overlay?.FactorySoftTimeout, specific.FactorySoftTimeoutSeconds, defaults.FactorySoftTimeoutSeconds, 1);
        TimeSpan factoryHard = FromSecondsOrOverlay(
            overlay?.FactoryHardTimeout, specific.FactoryHardTimeoutSeconds, defaults.FactoryHardTimeoutSeconds, 5);

        return new DomainFusionCacheSettings
        {
            HardTtlSeconds = ToNonNegSeconds(hardTtl),
            FailSafeSeconds = ToNonNegSeconds(failSafe),
            EagerRefreshRatio = overlay?.EagerRefreshRatio
                ?? Pick(specific.EagerRefreshRatio, defaults.EagerRefreshRatio, 0.9),
            JitterSeconds = ToNonNegSeconds(jitter),
            FactorySoftTimeoutSeconds = ToNonNegSeconds(factorySoft),
            FactoryHardTimeoutSeconds = ToNonNegSeconds(factoryHard),
            MaxItemBytes = overlay?.MaxItemBytes
                ?? Pick(specific.MaxItemBytes, defaults.MaxItemBytes, 0),
            AllowBackgroundDistributed = overlay?.AllowBackgroundDistributed
                ?? Pick(specific.AllowBackgroundDistributed, defaults.AllowBackgroundDistributed, true),
            AllowBackgroundBackplane = overlay?.AllowBackgroundBackplane
                ?? Pick(specific.AllowBackgroundBackplane, defaults.AllowBackgroundBackplane, true),
        };
    }

    private DomainFusionCacheSettings BindSection(string path)
    {
        DomainFusionCacheSettings settings = new();
        _configuration.GetSection(path).Bind(settings);
        return settings;
    }

    private static int ToNonNegSeconds(TimeSpan value)
    {
        double seconds = value.TotalSeconds;
        if (seconds <= 0)
            return 0;
        if (seconds >= int.MaxValue)
            return int.MaxValue;
        return (int)Math.Round(seconds);
    }
}
