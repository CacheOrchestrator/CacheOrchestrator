using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Edge.Configuration;
using CacheOrchestrator.Edge.Diagnostics;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Tags;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Threading.Channels;

namespace CacheOrchestrator.Edge.Invalidation;

internal sealed record EdgeConfigurationRegistration(IConfiguration Configuration, string ConfigSection);

internal sealed class EdgeDomainChangeMonitor : BackgroundService, IDomainVersionChangeObserver
{
    private readonly EdgeConfigurationRegistration _registration;
    private readonly IDomainRuntimeOverrideStore? _runtimeOverrides;
    private readonly IDomainEdgeOptionsProvider _domainOptions;
    private readonly EdgeInstanceResolver _instances;
    private readonly EdgeTagProjector _projector;
    private readonly IEdgeInvalidationQueue _queue;
    private readonly ILogger<EdgeDomainChangeMonitor> _logger;
    private readonly Channel<bool> _reloads = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public EdgeDomainChangeMonitor(
        EdgeConfigurationRegistration registration,
        IServiceProvider services,
        IDomainEdgeOptionsProvider domainOptions,
        EdgeInstanceResolver instances,
        EdgeTagProjector projector,
        IEdgeInvalidationQueue queue,
        ILogger<EdgeDomainChangeMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(domainOptions);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(logger);
        _registration = registration;
        _runtimeOverrides = services.GetService<IDomainRuntimeOverrideStore>();
        _domainOptions = domainOptions;
        _instances = instances;
        _projector = projector;
        _queue = queue;
        _logger = logger;
    }

    public async ValueTask OnDomainVersionChangedAsync(
        string domain,
        string version,
        CancellationToken cancellationToken = default)
    {
        DomainEdgeOptions options = _domainOptions.GetDomainOptions(domain);
        if (!options.Enabled)
            return;

        ResolvedEdgeInstance instance = _instances.Resolve(options.InstanceName);
        await EnqueueAsync(
            new EdgeDomainSnapshot(
                options.Domain,
                version,
                VersionIsRuntimeOverride: true,
                Enabled: true,
                options.PurgeOnStartup,
                instance.Name,
                instance.InvalidationProvider.Name,
                instance.TagNamespace),
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using IDisposable registration = ChangeToken.OnChange(
            _registration.Configuration.GetReloadToken,
            () => _reloads.Writer.TryWrite(true));

        IReadOnlyDictionary<string, EdgeDomainSnapshot> previous = CaptureSnapshot();
        try
        {
            await PurgeOnStartupAsync(previous, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to queue one or more Edge startup purges.");
        }

        ChannelReader<bool> reader = _reloads.Reader;
        while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out _))
            {
            }

            IReadOnlyDictionary<string, EdgeDomainSnapshot> current;
            try
            {
                current = CaptureSnapshot();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect Edge domains after configuration reload.");
                continue;
            }

            try
            {
                await ApplyChangesAsync(previous, current, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to queue one or more Edge configuration-change purges.");
            }
            previous = current;
        }
    }

    internal async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, EdgeDomainSnapshot> previous,
        IReadOnlyDictionary<string, EdgeDomainSnapshot> current,
        CancellationToken cancellationToken)
    {
        HashSet<string> domains = new(previous.Keys, StringComparer.Ordinal);
        domains.UnionWith(current.Keys);

        foreach (string domain in domains.OrderBy(static value => value, StringComparer.Ordinal))
        {
            previous.TryGetValue(domain, out EdgeDomainSnapshot? oldState);
            current.TryGetValue(domain, out EdgeDomainSnapshot? newState);
            if (oldState is not { Enabled: true } && newState is not { Enabled: true })
                continue;

            var queued = new HashSet<EdgePurgeIdentity>();

            bool placementChanged = oldState is { Enabled: true }
                && (newState is not { Enabled: true } || !oldState.HasSamePlacement(newState));
            if (placementChanged)
                await EnqueueOnceAsync(oldState!, queued, cancellationToken).ConfigureAwait(false);

            bool configuredVersionChanged = oldState is not null
                && newState is { Enabled: true, VersionIsRuntimeOverride: false }
                && !oldState.VersionIsRuntimeOverride
                && !string.Equals(oldState.Version, newState.Version, StringComparison.Ordinal);
            if (configuredVersionChanged)
                await EnqueueOnceAsync(newState!, queued, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task PurgeOnStartupAsync(
        IReadOnlyDictionary<string, EdgeDomainSnapshot> snapshot,
        CancellationToken cancellationToken)
    {
        foreach (EdgeDomainSnapshot state in snapshot.Values
                     .Where(static value => value.Enabled && value.PurgeOnStartup)
                     .OrderBy(static value => value.Domain, StringComparer.Ordinal))
        {
            await EnqueueAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    internal IReadOnlyDictionary<string, EdgeDomainSnapshot> CaptureSnapshot()
    {
        IConfigurationSection section = _registration.Configuration.GetSection(_registration.ConfigSection);
        var core = new CacheOrchestratorOptions();
        var edge = new CacheOrchestratorEdgeOptions();
        section.Bind(core);
        section.Bind(edge);

        HashSet<string> domains = new(StringComparer.Ordinal);
        foreach (string domain in core.Domains.Keys)
            domains.Add(DomainName.Normalize(domain));
        foreach (string domain in edge.Domains.Keys)
            domains.Add(DomainName.Normalize(domain));
        if (_runtimeOverrides is not null)
        {
            foreach (string domain in _runtimeOverrides.GetOverriddenDomains())
                domains.Add(DomainName.Normalize(domain));
        }

        var snapshot = new Dictionary<string, EdgeDomainSnapshot>(StringComparer.Ordinal);
        foreach (string domain in domains)
        {
            DomainEdgeSettings defaults = edge.DomainDefaults.Edge ?? new DomainEdgeSettings();
            edge.Domains.TryGetValue(domain, out EdgeDomainContainer? container);
            DomainEdgeSettings specific = container?.Edge ?? new DomainEdgeSettings();
            bool enabled = specific.Enabled ?? defaults.Enabled ?? false;
            bool purgeOnStartup = specific.PurgeOnStartup ?? defaults.PurgeOnStartup ?? false;

            string instanceName = specific.Instance ?? defaults.Instance ?? string.Empty;
            string providerName = string.Empty;
            string tagNamespace = string.Empty;
            if (enabled && edge.EdgeInstances.TryGetValue(instanceName, out EdgeInstanceOptions? instance))
            {
                providerName = instance.Provider;
                tagNamespace = !string.IsNullOrWhiteSpace(instance.Namespace)
                    ? instance.Namespace
                    : $"{core.Namespace ?? "app-cache"}-edge-{instanceName}";
            }

            DomainRuntimeOverride? runtimeOverride = _runtimeOverrides?.Get(domain);
            core.Domains.TryGetValue(domain, out CacheOrchestratorOptions.DomainCacheSettings? domainSettings);
            string version = runtimeOverride?.Version
                ?? domainSettings?.Version
                ?? core.DomainDefaults.Version
                ?? "1";

            snapshot.Add(domain, new EdgeDomainSnapshot(
                domain,
                version,
                runtimeOverride?.Version is { Length: > 0 },
                enabled,
                purgeOnStartup,
                instanceName,
                providerName,
                tagNamespace));
        }

        return snapshot;
    }

    private async ValueTask EnqueueOnceAsync(
        EdgeDomainSnapshot state,
        HashSet<EdgePurgeIdentity> queued,
        CancellationToken cancellationToken)
    {
        var identity = new EdgePurgeIdentity(state.InstanceName, state.ProviderName, state.TagNamespace);
        if (queued.Add(identity))
            await EnqueueAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnqueueAsync(EdgeDomainSnapshot state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.InstanceName)
            || string.IsNullOrWhiteSpace(state.ProviderName)
            || string.IsNullOrWhiteSpace(state.TagNamespace))
        {
            _logger.LogWarning(
                "Cannot queue Edge domain purge for '{Domain}' because its effective Edge instance is incomplete.",
                state.Domain);
            return;
        }

        string tag = _projector.Project(state.TagNamespace, CacheTags.Domain(state.Domain));
        await _queue.EnqueueAsync(
            new EdgeInvalidationJob(state.InstanceName, state.ProviderName, [tag]),
            cancellationToken).ConfigureAwait(false);
        EdgeMetrics.RecordQueued(state.InstanceName, state.ProviderName, 1);
    }

    private readonly record struct EdgePurgeIdentity(string InstanceName, string ProviderName, string TagNamespace);
}

internal sealed record EdgeDomainSnapshot(
    string Domain,
    string Version,
    bool VersionIsRuntimeOverride,
    bool Enabled,
    bool PurgeOnStartup,
    string InstanceName,
    string ProviderName,
    string TagNamespace)
{
    public bool HasSamePlacement(EdgeDomainSnapshot other) =>
        string.Equals(InstanceName, other.InstanceName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProviderName, other.ProviderName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(TagNamespace, other.TagNamespace, StringComparison.Ordinal);
}
