using CacheOrchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Edge.Configuration;

internal sealed class DomainEdgeOptionsProvider : IDomainEdgeOptionsProvider
{
    private readonly IOptionsMonitor<CacheOrchestratorEdgeOptions> _options;

    public DomainEdgeOptionsProvider(IOptionsMonitor<CacheOrchestratorEdgeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public DomainEdgeOptions GetDomainOptions(string domain)
    {
        string normalized = DomainName.Normalize(domain);
        CacheOrchestratorEdgeOptions root = _options.CurrentValue;
        DomainEdgeSettings defaults = root.DomainDefaults.Edge ?? new DomainEdgeSettings();
        root.Domains.TryGetValue(normalized, out EdgeDomainContainer? container);
        DomainEdgeSettings domainSettings = container?.Edge ?? new DomainEdgeSettings();

        return new DomainEdgeOptions
        {
            Domain = normalized,
            Enabled = domainSettings.Enabled ?? defaults.Enabled ?? false,
            PurgeOnStartup = domainSettings.PurgeOnStartup ?? defaults.PurgeOnStartup ?? false,
            InstanceName = domainSettings.Instance ?? defaults.Instance ?? string.Empty,
            Ttl = TimeSpan.FromSeconds(domainSettings.TtlSeconds ?? defaults.TtlSeconds ?? 300),
            StaleWhileRevalidate = SecondsOrNull(
                domainSettings.StaleWhileRevalidateSeconds ?? defaults.StaleWhileRevalidateSeconds),
            StaleIfError = SecondsOrNull(
                domainSettings.StaleIfErrorSeconds ?? defaults.StaleIfErrorSeconds)
        };
    }

    private static TimeSpan? SecondsOrNull(int? seconds) => seconds is null
        ? null
        : TimeSpan.FromSeconds(seconds.Value);
}
