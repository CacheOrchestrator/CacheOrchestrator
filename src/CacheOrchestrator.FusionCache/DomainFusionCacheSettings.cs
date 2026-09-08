using CacheOrchestrator.Configuration;

namespace CacheOrchestrator.FusionCache;

/// <summary>
/// FusionCache-specific knobs. Bound from <c>Cache:DomainDefaults:FusionCache</c> /
/// <c>Cache:Domains:{name}:FusionCache</c> (JSON section name stays <c>FusionCache</c>).
/// Duration fields are int seconds for config DX; runtime uses <see cref="TimeSpan"/>.
/// </summary>
public sealed class DomainFusionCacheSettings
{
    /// <summary>Cap on base Data Cache freshness, in seconds; 0 disables the cap. Jitter can extend freshness, and fail-safe has a separate retention horizon.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Int, RuntimeOverlay = true, Group = "TTL", DisplayName = "Fusion hard TTL (seconds)")]
    public int? HardTtlSeconds { get; init; }

    /// <summary>Maximum fail-safe retention horizon from materialization, in seconds; 0 disables fail-safe. This is not added to the base duration.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Int, RuntimeOverlay = true, Group = "TTL", DisplayName = "Fusion fail-safe (seconds)")]
    public int? FailSafeSeconds { get; init; }

    /// <summary>Eager refresh threshold ratio (0–1 exclusive). 0 = disabled.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Double, RuntimeOverlay = true, Group = "Fusion", DisplayName = "Eager refresh ratio")]
    public double? EagerRefreshRatio { get; init; }

    /// <summary>Max jitter added to Fusion duration, in seconds.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Int, RuntimeOverlay = true, Group = "Fusion", DisplayName = "Fusion jitter (seconds)")]
    public int? JitterSeconds { get; init; }

    /// <summary>Factory soft timeout, in seconds.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Int, RuntimeOverlay = true, Group = "Fusion", DisplayName = "Factory soft timeout (seconds)")]
    public int? FactorySoftTimeoutSeconds { get; init; }

    /// <summary>Factory hard timeout, in seconds.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Int, RuntimeOverlay = true, Group = "Fusion", DisplayName = "Factory hard timeout (seconds)")]
    public int? FactoryHardTimeoutSeconds { get; init; }

    /// <summary>Allow background distributed cache operations.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Bool, RuntimeOverlay = true, Group = "Fusion", DisplayName = "Background distributed ops")]
    public bool? AllowBackgroundDistributed { get; init; }

    /// <summary>Allow background backplane operations.</summary>
    [DomainSetting(Kind = DomainSettingValueKind.Bool, RuntimeOverlay = true, Group = "Fusion", DisplayName = "Background backplane ops")]
    public bool? AllowBackgroundBackplane { get; init; }
}
