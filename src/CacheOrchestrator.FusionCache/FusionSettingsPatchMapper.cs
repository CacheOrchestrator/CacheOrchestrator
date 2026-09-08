using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using System.Globalization;
using System.Text.Json;

namespace CacheOrchestrator.FusionCache;

/// <summary>Maps <c>fusionCache.*</c> Admin overlay keys onto <see cref="IFusionDomainRuntimeOverrideStore"/>.</summary>
internal static class FusionSettingsPatchMapper
{
    /// <summary>Parses and validates owned Fusion overlay keys.</summary>
    public static FusionDomainSettingsPatch Parse(IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count == 0)
            throw new ArgumentException("At least one Fusion setting must be set.", nameof(settings));

        TimeSpan? hardTtl = null;
        TimeSpan? failSafe = null;
        double? eagerRefreshRatio = null;
        TimeSpan? jitter = null;
        TimeSpan? factorySoftTimeout = null;
        TimeSpan? factoryHardTimeout = null;
        bool? allowBackgroundDistributed = null;
        bool? allowBackgroundBackplane = null;

        foreach ((string rawKey, JsonElement el) in settings)
        {
            string id = rawKey;
            if (!id.StartsWith("fusionCache.", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Setting '{rawKey}' is not a Fusion overlay key.", nameof(settings));

            string suffix = id["fusionCache.".Length..];
            switch (suffix)
            {
                case "hardTtlSeconds":
                    hardTtl = ReadNonNegSecondsAsTimeSpan(el, id);
                    break;
                case "failSafeSeconds":
                    failSafe = ReadNonNegSecondsAsTimeSpan(el, id);
                    break;
                case "eagerRefreshRatio":
                    eagerRefreshRatio = ReadDouble(el, id);
                    if (!double.IsFinite(eagerRefreshRatio.Value) || eagerRefreshRatio is < 0 or >= 1)
                        throw new ArgumentException($"Setting '{id}' must be in [0, 1).", id);
                    break;
                case "jitterSeconds":
                    jitter = ReadNonNegSecondsAsTimeSpan(el, id);
                    break;
                case "factorySoftTimeoutSeconds":
                    factorySoftTimeout = ReadNonNegSecondsAsTimeSpan(el, id);
                    break;
                case "factoryHardTimeoutSeconds":
                    factoryHardTimeout = ReadNonNegSecondsAsTimeSpan(el, id);
                    break;
                case "allowBackgroundDistributed":
                    allowBackgroundDistributed = ReadBool(el, id);
                    break;
                case "allowBackgroundBackplane":
                    allowBackgroundBackplane = ReadBool(el, id);
                    break;
                default:
                    throw new ArgumentException($"Setting '{id}' is not mapped for Fusion overlay.", nameof(settings));
            }
        }

        FusionDomainSettingsPatch patch = new()
        {
            HardTtl = hardTtl,
            FailSafe = failSafe,
            EagerRefreshRatio = eagerRefreshRatio,
            Jitter = jitter,
            FactorySoftTimeout = factorySoftTimeout,
            FactoryHardTimeout = factoryHardTimeout,
            AllowBackgroundDistributed = allowBackgroundDistributed,
            AllowBackgroundBackplane = allowBackgroundBackplane,
        };

        if (!patch.HasAny)
            throw new ArgumentException("At least one Fusion setting must be set.", nameof(settings));

        return patch;
    }

    private static bool ReadBool(JsonElement el, string id) =>
        el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out bool b) => b,
            _ => throw new ArgumentException($"Setting '{id}' must be a boolean.", id),
        };

    private static int ReadNonNegInt(JsonElement el, string id)
    {
        int v = el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
            _ => throw new ArgumentException($"Setting '{id}' must be an integer.", id),
        };
        if (v < 0)
            throw new ArgumentException($"Setting '{id}' must be >= 0.", id);
        return v;
    }

    private static TimeSpan ReadNonNegSecondsAsTimeSpan(JsonElement el, string id)
    {
        int seconds = ReadNonNegInt(el, id);
        return TimeSpan.FromSeconds(seconds);
    }

    private static double ReadDouble(JsonElement el, string id) =>
        el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            _ => throw new ArgumentException($"Setting '{id}' must be a number.", id),
        };
}

/// <summary>Routes <c>fusionCache.*</c> Admin/cluster settings patches to the Fusion overlay store.</summary>
internal sealed class FusionDomainSettingsPatchContributor : IDomainSettingsPatchContributor
{
    private readonly IFusionDomainSettingsProvider _settings;

    private readonly IDomainCacheOptionsProvider _core;
    public FusionDomainSettingsPatchContributor(IFusionDomainSettingsProvider settings, IDomainCacheOptionsProvider core)
    {
        _settings = settings;
        _core = core;
    }

    public bool Owns(string settingId) => settingId.StartsWith("fusionCache.", StringComparison.OrdinalIgnoreCase);

    public void Prepare(DomainSettingsPatchContext context, IReadOnlyDictionary<string, JsonElement> settings)
    {
        FusionDomainSettingsPatch patch = FusionSettingsPatchMapper.Parse(settings);
        FusionDomainRuntimeOverride merged = FusionDomainRuntimeOverrideStore.Merge(
            context.Get<FusionDomainRuntimeOverride>() ?? new(), patch, context.Stamp);
        context.Set(merged);
    }

    public void Validate(DomainSettingsPatchContext context)
    {
        FusionDomainRuntimeOverride merged = context.Get<FusionDomainRuntimeOverride>() ?? new();
        DomainFusionCacheSettings effective = _settings.Get(context.Domain);
        var candidate = new DomainFusionCacheSettings
        {
            HardTtlSeconds = (int?)merged.HardTtl?.TotalSeconds ?? effective.HardTtlSeconds,
            FailSafeSeconds = (int?)merged.FailSafe?.TotalSeconds ?? effective.FailSafeSeconds,
            FactorySoftTimeoutSeconds = (int?)merged.FactorySoftTimeout?.TotalSeconds ?? effective.FactorySoftTimeoutSeconds,
            FactoryHardTimeoutSeconds = (int?)merged.FactoryHardTimeout?.TotalSeconds ?? effective.FactoryHardTimeoutSeconds
        };
        TimeSpan dataTtl = context.Get<DomainRuntimeOverride>()?.DataCacheTtl
            ?? _core.GetOrCreateDomainOptions(context.Domain).DataCacheTtl;
        List<string> failures = [];
        FusionCacheConfigurationValidator.ValidateEffective(context.Domain, candidate, (int)dataTtl.TotalSeconds, failures);
        if (failures.Count != 0)
            throw new ArgumentException(string.Join(" ", failures));
    }
}
