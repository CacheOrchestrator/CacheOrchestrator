namespace CacheOrchestrator.FusionCache;

/// <summary>
/// Process-local Fusion overlay for one domain. Null property = inherit from configuration.
/// </summary>
public sealed class FusionDomainRuntimeOverride
{
    /// <summary>Monotonic stamp; increments on every mutation of this domain's Fusion overlay.</summary>
    public int Stamp { get; init; }

    /// <summary>Override hard TTL.</summary>
    public TimeSpan? HardTtl { get; init; }

    /// <summary>Override fail-safe.</summary>
    public TimeSpan? FailSafe { get; init; }

    /// <summary>Override eager refresh ratio.</summary>
    public double? EagerRefreshRatio { get; init; }

    /// <summary>Override jitter.</summary>
    public TimeSpan? Jitter { get; init; }

    /// <summary>Override factory soft timeout.</summary>
    public TimeSpan? FactorySoftTimeout { get; init; }

    /// <summary>Override factory hard timeout.</summary>
    public TimeSpan? FactoryHardTimeout { get; init; }

    /// <summary>Override background distributed ops.</summary>
    public bool? AllowBackgroundDistributed { get; init; }

    /// <summary>Override background backplane ops.</summary>
    public bool? AllowBackgroundBackplane { get; init; }

    /// <summary>True when any Fusion overlay field is set.</summary>
    public bool HasAny =>
        HardTtl is not null
        || FailSafe is not null
        || EagerRefreshRatio is not null
        || Jitter is not null
        || FactorySoftTimeout is not null
        || FactoryHardTimeout is not null
        || AllowBackgroundDistributed is not null
        || AllowBackgroundBackplane is not null;
}

/// <summary>Partial Fusion settings update. Null properties mean "leave unchanged".</summary>
public sealed class FusionDomainSettingsPatch
{
    /// <summary>Hard TTL.</summary>
    public TimeSpan? HardTtl { get; init; }

    /// <summary>Fail-safe.</summary>
    public TimeSpan? FailSafe { get; init; }

    /// <summary>Eager refresh ratio.</summary>
    public double? EagerRefreshRatio { get; init; }

    /// <summary>Jitter.</summary>
    public TimeSpan? Jitter { get; init; }

    /// <summary>Factory soft timeout.</summary>
    public TimeSpan? FactorySoftTimeout { get; init; }

    /// <summary>Factory hard timeout.</summary>
    public TimeSpan? FactoryHardTimeout { get; init; }

    /// <summary>Background distributed ops.</summary>
    public bool? AllowBackgroundDistributed { get; init; }

    /// <summary>Background backplane ops.</summary>
    public bool? AllowBackgroundBackplane { get; init; }

    /// <summary>True when at least one field is provided.</summary>
    public bool HasAny =>
        HardTtl is not null
        || FailSafe is not null
        || EagerRefreshRatio is not null
        || Jitter is not null
        || FactorySoftTimeout is not null
        || FactoryHardTimeout is not null
        || AllowBackgroundDistributed is not null
        || AllowBackgroundBackplane is not null;
}
