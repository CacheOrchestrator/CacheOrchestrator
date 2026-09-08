using CacheOrchestrator.Admin;

namespace CacheOrchestrator.FusionCache;

/// <summary>Default in-memory <see cref="IFusionDomainRuntimeOverrideStore"/>.</summary>
internal sealed class FusionDomainRuntimeOverrideStore : IFusionDomainRuntimeOverrideStore
{
    private readonly IDomainRuntimeOverrideStore _state;
    public FusionDomainRuntimeOverrideStore() : this(new DomainRuntimeOverrideStore()) { }
    public FusionDomainRuntimeOverrideStore(IDomainRuntimeOverrideStore state)
    {
        _state = state;
    }

    public FusionDomainRuntimeOverride? Get(string domain) => _state.GetSettings<FusionDomainRuntimeOverride>(domain);
    public int GetStamp(string domain) => _state.GetStamp(domain);

    public FusionDomainRuntimeOverride PatchSettings(string domain, FusionDomainSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.HasAny)
            throw new ArgumentException("At least one Fusion setting must be set.", nameof(patch));
        FusionDomainRuntimeOverride result = null!;
        _state.Update(domain, context =>
        {
            result = Merge(context.Get<FusionDomainRuntimeOverride>() ?? new(), patch, context.Stamp);
            context.Set(result);
        });
        return result;
    }

    public bool Clear(string domain)
    {
        bool removed = false;
        _state.Update(domain, context => removed = context.Remove<FusionDomainRuntimeOverride>());
        return removed;
    }

    internal static FusionDomainRuntimeOverride Merge(
        FusionDomainRuntimeOverride existing,
        FusionDomainSettingsPatch patch,
        int stamp) =>
        new()
        {
            Stamp = stamp,
            HardTtl = patch.HardTtl ?? existing.HardTtl,
            FailSafe = patch.FailSafe ?? existing.FailSafe,
            EagerRefreshRatio = patch.EagerRefreshRatio ?? existing.EagerRefreshRatio,
            Jitter = patch.Jitter ?? existing.Jitter,
            FactorySoftTimeout = patch.FactorySoftTimeout ?? existing.FactorySoftTimeout,
            FactoryHardTimeout = patch.FactoryHardTimeout ?? existing.FactoryHardTimeout,
            MaxItemBytes = patch.MaxItemBytes ?? existing.MaxItemBytes,
            AllowBackgroundDistributed = patch.AllowBackgroundDistributed ?? existing.AllowBackgroundDistributed,
            AllowBackgroundBackplane = patch.AllowBackgroundBackplane ?? existing.AllowBackgroundBackplane,
        };

}

/// <summary>No-op Fusion overlay store.</summary>
internal sealed class NullFusionDomainRuntimeOverrideStore : IFusionDomainRuntimeOverrideStore
{
    public static readonly NullFusionDomainRuntimeOverrideStore Instance = new();

    private NullFusionDomainRuntimeOverrideStore()
    {
    }

    public FusionDomainRuntimeOverride? Get(string domain) => null;

    public int GetStamp(string domain) => 0;

    public FusionDomainRuntimeOverride PatchSettings(string domain, FusionDomainSettingsPatch patch) =>
        throw new InvalidOperationException("Fusion runtime overlays are not available.");

    public bool Clear(string domain) => false;
}
