using CacheOrchestrator.Configuration;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.FusionCache;

/// <summary>
/// Builds <see cref="FusionCacheEntryOptions"/> from domain options + Fusion engine settings.
/// </summary>
internal static class FusionEntryOptionsFactory
{
    /// <summary>
    /// Creates Fusion entry options. Data TTL comes from <paramref name="opts"/>;
    /// hard TTL / fail-safe / jitter / factory timeouts come from <paramref name="fusion"/>
    /// (already merged with optional <paramref name="overlay"/> when provided separately).
    /// </summary>
    public static FusionCacheEntryOptions Create(
        DomainCacheOptions opts,
        DomainFusionCacheSettings? fusion = null,
        FusionDomainRuntimeOverride? overlay = null)
    {
        ArgumentNullException.ThrowIfNull(opts);

        TimeSpan hardTtl = overlay?.HardTtl
            ?? SecondsOrDefault(fusion?.HardTtlSeconds, 43200);
        TimeSpan failSafe = overlay?.FailSafe
            ?? SecondsOrDefault(fusion?.FailSafeSeconds, 86400);
        TimeSpan jitter = overlay?.Jitter
            ?? SecondsOrDefault(fusion?.JitterSeconds, 60);
        TimeSpan factorySoftTimeout = overlay?.FactorySoftTimeout
            ?? SecondsOrDefault(fusion?.FactorySoftTimeoutSeconds, 1);
        TimeSpan factoryHardTimeout = overlay?.FactoryHardTimeout
            ?? SecondsOrDefault(fusion?.FactoryHardTimeoutSeconds, 5);
        double eagerRefresh = overlay?.EagerRefreshRatio
            ?? fusion?.EagerRefreshRatio
            ?? 0.9;
        bool allowBackgroundDistributed = overlay?.AllowBackgroundDistributed
            ?? fusion?.AllowBackgroundDistributed
            ?? true;
        bool allowBackgroundBackplane = overlay?.AllowBackgroundBackplane
            ?? fusion?.AllowBackgroundBackplane
            ?? true;

        // Soft/data duration; cap by hard TTL when hard is shorter (defensive).
        TimeSpan duration = opts.DataCacheTtl;
        if (hardTtl > TimeSpan.Zero && duration > hardTtl)
            duration = hardTtl;

        // Fail-safe must be explicitly enabled; FailSafeMaxDuration alone is ignored by FusionCache.
        bool failSafeEnabled = failSafe > TimeSpan.Zero;

        FusionCacheEntryOptions o = new()
        {
            Duration = duration,
            JitterMaxDuration = jitter < TimeSpan.Zero ? TimeSpan.Zero : jitter,
            IsFailSafeEnabled = failSafeEnabled,
            FailSafeMaxDuration = failSafe,
            AllowBackgroundDistributedCacheOperations = allowBackgroundDistributed,
            AllowBackgroundBackplaneOperations = allowBackgroundBackplane,
        };

        if (eagerRefresh is > 0 and < 1)
            o.EagerRefreshThreshold = (float)eagerRefresh;

        TimeSpan soft = factorySoftTimeout < TimeSpan.Zero ? TimeSpan.Zero : factorySoftTimeout;
        TimeSpan hard = factoryHardTimeout < TimeSpan.Zero ? TimeSpan.Zero : factoryHardTimeout;

        if (hard <= TimeSpan.Zero)
            hard = TimeSpan.FromSeconds(5);

        if (soft <= TimeSpan.Zero || soft >= hard)
            soft = TimeSpan.FromMilliseconds(Math.Max(100, hard.TotalMilliseconds * 0.2));

        o.FactorySoftTimeout = soft;
        o.FactoryHardTimeout = hard;

        return o;
    }

    private static TimeSpan SecondsOrDefault(int? seconds, int fallback)
    {
        int value = seconds ?? fallback;
        if (value < 0)
            value = 0;
        return TimeSpan.FromSeconds(value);
    }
}
