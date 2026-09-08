using CacheOrchestrator.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.FusionCache;

/// <summary>Validates provider-owned Fusion settings after domain inheritance is applied.</summary>
internal sealed class FusionCacheConfigurationValidator : IValidateOptions<CacheOrchestratorOptions>
{
    private readonly IConfiguration? _configuration;
    private readonly string _configSection;

    public FusionCacheConfigurationValidator(IConfiguration? configuration, string configSection)
    {
        _configuration = configuration;
        _configSection = string.IsNullOrWhiteSpace(configSection) ? "Cache" : configSection;
    }

    public ValidateOptionsResult Validate(string? name, CacheOrchestratorOptions options)
    {
        // AddCacheOrchestratorFusionCache intentionally supports provider-only registration
        // without IConfiguration. There are no bound Fusion settings to validate in that mode.
        if (_configuration is null)
            return ValidateOptionsResult.Success;

        List<string> failures = [];
        DomainFusionCacheSettings defaults = Bind("DomainDefaults", failures);
        ValidateRaw("DomainDefaults", defaults, failures);
        ValidateEffective(
            "DomainDefaults",
            defaults,
            options.DomainDefaults.DataCache?.TtlSeconds ?? 3800,
            failures);

        foreach ((string domain, CacheOrchestratorOptions.DomainCacheSettings coreSettings) in options.Domains)
        {
            DomainFusionCacheSettings specific = Bind($"Domains:{domain}", failures);
            ValidateRaw($"Domain '{domain}'", specific, failures);
            ValidateEffective(
                $"Domain '{domain}'",
                Merge(defaults, specific),
                coreSettings.DataCache?.TtlSeconds
                    ?? options.DomainDefaults.DataCache?.TtlSeconds
                    ?? 3800,
                failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private DomainFusionCacheSettings Bind(string path, List<string> failures)
    {
        DomainFusionCacheSettings settings = new();
        IConfigurationSection section = _configuration!.GetSection($"{_configSection}:{path}:FusionCache");
        if (section.GetSection("MaxItemBytes").Exists())
            failures.Add($"{section.Path}:MaxItemBytes was removed in 3.0. Remove the key; it never enforced a byte limit. Configure memory budgets in the underlying cache engine.");
        section.Bind(settings);
        return settings;
    }

    private static DomainFusionCacheSettings Merge(
        DomainFusionCacheSettings defaults,
        DomainFusionCacheSettings specific) =>
        new()
        {
            HardTtlSeconds = specific.HardTtlSeconds ?? defaults.HardTtlSeconds,
            FailSafeSeconds = specific.FailSafeSeconds ?? defaults.FailSafeSeconds,
            EagerRefreshRatio = specific.EagerRefreshRatio ?? defaults.EagerRefreshRatio,
            JitterSeconds = specific.JitterSeconds ?? defaults.JitterSeconds,
            FactorySoftTimeoutSeconds = specific.FactorySoftTimeoutSeconds ?? defaults.FactorySoftTimeoutSeconds,
            FactoryHardTimeoutSeconds = specific.FactoryHardTimeoutSeconds ?? defaults.FactoryHardTimeoutSeconds,
            AllowBackgroundDistributed = specific.AllowBackgroundDistributed ?? defaults.AllowBackgroundDistributed,
            AllowBackgroundBackplane = specific.AllowBackgroundBackplane ?? defaults.AllowBackgroundBackplane,
        };

    private static void ValidateRaw(
        string label,
        DomainFusionCacheSettings settings,
        List<string> failures)
    {
        NonNegative(label, nameof(settings.HardTtlSeconds), settings.HardTtlSeconds, failures);
        NonNegative(label, nameof(settings.FailSafeSeconds), settings.FailSafeSeconds, failures);
        NonNegative(label, nameof(settings.JitterSeconds), settings.JitterSeconds, failures);
        NonNegative(label, nameof(settings.FactorySoftTimeoutSeconds), settings.FactorySoftTimeoutSeconds, failures);
        NonNegative(label, nameof(settings.FactoryHardTimeoutSeconds), settings.FactoryHardTimeoutSeconds, failures);

        if (settings.EagerRefreshRatio is double eager
            && (!double.IsFinite(eager) || eager < 0 || eager >= 1))
        {
            failures.Add($"{label}: FusionCache.EagerRefreshRatio must be in [0, 1).");
        }
    }

    internal static void ValidateEffective(
        string label,
        DomainFusionCacheSettings settings,
        int dataCacheTtlSeconds,
        List<string> failures)
    {
        int hardTtl = settings.HardTtlSeconds ?? 43200;
        int failSafe = settings.FailSafeSeconds ?? 86400;
        int duration = Math.Max(0, dataCacheTtlSeconds);
        if (hardTtl > 0)
            duration = Math.Min(duration, hardTtl);

        if (failSafe > 0 && failSafe < duration)
        {
            failures.Add(
                $"{label}: FusionCache.FailSafeSeconds must be 0 (disabled) or >= the effective Data Cache duration ({duration} seconds).");
        }

        int softTimeout = settings.FactorySoftTimeoutSeconds ?? 1;
        int hardTimeout = settings.FactoryHardTimeoutSeconds ?? 5;
        if (softTimeout <= 0)
            failures.Add($"{label}: FusionCache.FactorySoftTimeoutSeconds must be > 0.");
        if (hardTimeout <= 0)
            failures.Add($"{label}: FusionCache.FactoryHardTimeoutSeconds must be > 0.");
        if (softTimeout > 0 && hardTimeout > 0 && softTimeout >= hardTimeout)
        {
            failures.Add(
                $"{label}: FusionCache.FactorySoftTimeoutSeconds must be < FactoryHardTimeoutSeconds.");
        }
    }

    private static void NonNegative(string label, string property, int? value, List<string> failures)
    {
        if (value < 0)
            failures.Add($"{label}: FusionCache.{property} cannot be negative.");
    }
}

internal sealed class FusionCacheValidatorMarker;
