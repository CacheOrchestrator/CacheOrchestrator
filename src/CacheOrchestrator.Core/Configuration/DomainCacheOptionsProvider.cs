using CacheOrchestrator.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;

namespace CacheOrchestrator.Configuration;

/// <summary>
/// Resolves and caches HTTP-free <see cref="DomainCacheOptions"/> snapshots per domain.
/// </summary>
internal sealed class DomainCacheOptionsProvider : IDomainCacheOptionsProvider, IDisposable
{
    private readonly ILogger<DomainCacheOptionsProvider> _logger;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _optionsMonitor;
    private readonly IDomainRuntimeOverrideStore _runtimeOverrides;
    private ConcurrentDictionary<string, CachedDomainOptions> _globalCache = new(StringComparer.Ordinal);
    private readonly IDisposable? _changeRegistration;

    public DomainCacheOptionsProvider(
        IOptionsMonitor<CacheOrchestratorOptions> optionsMonitor,
        ILogger<DomainCacheOptionsProvider> logger,
        IDomainRuntimeOverrideStore? runtimeOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(optionsMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _runtimeOverrides = runtimeOverrides ?? NullDomainRuntimeOverrideStore.Instance;

        _changeRegistration = _optionsMonitor.OnChange(_ =>
        {
            ConcurrentDictionary<string, CachedDomainOptions> retired = Interlocked.Exchange(
                ref _globalCache, new(StringComparer.Ordinal));
            _logger.LogInformation(
                "Configuration changed: replaced Core domain snapshot generation ({Count} retired items).",
                retired.Count);
        });
    }

    private sealed class CachedDomainOptions
    {
        public required DomainCacheOptions Options { get; init; }
        public int OverrideStamp { get; init; }
    }

    public void Dispose() => _changeRegistration?.Dispose();

    public DomainCacheOptions GetOrCreateDomainOptions(string domain)
    {
        domain = DomainName.Normalize(domain);
        int stamp = _runtimeOverrides.GetStamp(domain);

        // Publish only into the generation captured before resolving configuration.
        ConcurrentDictionary<string, CachedDomainOptions> generation = Volatile.Read(ref _globalCache);
        if (generation.TryGetValue(domain, out CachedDomainOptions? cached)
            && cached.OverrideStamp == stamp)
        {
            return cached.Options;
        }

        DomainCacheOptions options = CreateDomainOptions(domain);
        generation[domain] = new CachedDomainOptions { Options = options, OverrideStamp = stamp };
        return options;
    }

    private DomainCacheOptions CreateDomainOptions(string domain)
    {
        CacheOrchestratorOptions options = _optionsMonitor.CurrentValue;
        CacheOrchestratorOptions.DomainCacheSettings defaults = options.DomainDefaults;
        DomainRuntimeOverride? overlay = _runtimeOverrides.Get(domain);

        if (!options.Domains.TryGetValue(domain, out CacheOrchestratorOptions.DomainCacheSettings? domainSettings))
        {
            _logger.LogWarning(
                "Domain '{Domain}' is not configured. Falling back to DomainDefaults.",
                domain);
            domainSettings = new CacheOrchestratorOptions.DomainCacheSettings();
        }

        static T Pick<T>(T? specific, T? global, T fallback) where T : struct =>
            specific ?? global ?? fallback;

        string version;
        bool usedDefaultVersion = false;
        if (overlay?.Version is { Length: > 0 } overlayVersion)
        {
            version = overlayVersion;
        }
        else if (!string.IsNullOrWhiteSpace(domainSettings.Version))
        {
            version = domainSettings.Version;
        }
        else if (!string.IsNullOrWhiteSpace(defaults.Version))
        {
            version = defaults.Version;
        }
        else
        {
            version = "1";
            usedDefaultVersion = true;
        }

        if (usedDefaultVersion)
        {
            _logger.LogWarning(
                "Domain '{Domain}' has no Version configured. Using stable default ('1'). " +
                "Cache will not auto-invalidate on restart.",
                domain);
        }

        string? configuredInstance = domainSettings.DataCache?.Instance;
        string? defaultInstance = defaults.DataCache?.Instance;
        string instanceName = !string.IsNullOrWhiteSpace(configuredInstance)
            ? configuredInstance
            : !string.IsNullOrWhiteSpace(defaultInstance)
                ? defaultInstance
                : "default";

        ulong versionHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(version));
        TimeSpan dataCacheTtl = overlay?.DataCacheTtl
            ?? Seconds(Pick(domainSettings.DataCache?.TtlSeconds, defaults.DataCache?.TtlSeconds, 3800));

        string dataCacheNamespace = options.DataCacheInstances.TryGetValue(
            instanceName,
            out CacheOrchestratorOptions.DataCacheInstanceOptions? instance)
                ? instance.GetNamespace(instanceName, options)
                : new CacheOrchestratorOptions.DataCacheInstanceOptions().GetNamespace(instanceName, options);

        return new DomainCacheOptions
        {
            Domain = domain,
            DataCacheInstanceName = instanceName,
            DataCacheEnabled = overlay?.DataCacheEnabled
                ?? Pick(domainSettings.DataCache?.Enabled, defaults.DataCache?.Enabled, true),
            Version = version,
            VersionHex = versionHash.ToString("x16"),
            DataCacheTtl = dataCacheTtl,
            DataCacheNamespace = dataCacheNamespace,
        };
    }

    private static TimeSpan Seconds(int value) =>
        TimeSpan.FromSeconds(value < 0 ? 0 : value);
}
