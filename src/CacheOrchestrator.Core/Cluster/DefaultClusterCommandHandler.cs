using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Cluster;

/// <summary>
/// Default local applicator: namespace/self checks, then command under <see cref="ClusterCommandScope"/>.
/// </summary>
internal sealed class DefaultClusterCommandHandler : IClusterCommandHandler
{
    private readonly ICacheOrchestratorInvalidator _invalidator;
    private readonly IDomainRuntimeOverrideStore _overrides;
    private readonly IInstanceIdProvider _instanceId;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options;
    private readonly ClusterCommandDedupeStore _dedupe;
    private readonly IEnumerable<IDomainSettingsPatchContributor> _settingsContributors;
    private readonly ILogger<DefaultClusterCommandHandler> _logger;
    private readonly DomainSettingsInvalidationCoordinator? _settingsInvalidation;

    public DefaultClusterCommandHandler(
        ICacheOrchestratorInvalidator invalidator,
        IDomainRuntimeOverrideStore overrides,
        IInstanceIdProvider instanceId,
        IOptionsMonitor<CacheOrchestratorOptions> options,
        ClusterCommandDedupeStore dedupe,
        ILogger<DefaultClusterCommandHandler> logger,
        IEnumerable<IDomainSettingsPatchContributor>? settingsContributors = null,
        DomainSettingsInvalidationCoordinator? settingsInvalidation = null)
    {
        ArgumentNullException.ThrowIfNull(invalidator);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dedupe);
        ArgumentNullException.ThrowIfNull(logger);

        _invalidator = invalidator;
        _overrides = overrides;
        _instanceId = instanceId;
        _options = options;
        _dedupe = dedupe;
        _settingsContributors = settingsContributors ?? [];
        _logger = logger;
        _settingsInvalidation = settingsInvalidation;
    }

    /// <inheritdoc />
    public async Task ApplyLocalAsync(ClusterCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CacheOrchestratorMetrics.RecordClusterReceived(command.GetType().Name);

        string localNs = _options.CurrentValue.Namespace ?? string.Empty;
        if (!string.Equals(command.Namespace, localNs, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Ignoring cluster command {CommandId}: namespace mismatch (command={CommandNs}, local={LocalNs})",
                command.CommandId,
                command.Namespace,
                localNs);
            return;
        }

        if (string.Equals(command.OriginInstanceId, _instanceId.InstanceId, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Ignoring cluster command {CommandId}: origin is self ({InstanceId})",
                command.CommandId,
                _instanceId.InstanceId);
            return;
        }

        if (!_dedupe.TryMarkAsNew(command.CommandId))
        {
            _logger.LogDebug(
                "Ignoring cluster command {CommandId}: duplicate within dedupe window",
                command.CommandId);
            return;
        }

        using (ClusterCommandScope.EnterRemote())
        {
            switch (command)
            {
                case InvalidateCommand inv:
                    await ApplyInvalidateAsync(inv, cancellationToken).ConfigureAwait(false);
                    break;

                case VersionBumpCommand bump:
                    ApplyVersionBump(bump);
                    break;

                case SettingsPatchCommand settings:
                    await ApplySettingsPatchAsync(settings, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    _logger.LogWarning(
                        "Unsupported cluster command type {Type} ({CommandId})",
                        command.GetType().Name,
                        command.CommandId);
                    return;
            }
        }

        CacheOrchestratorMetrics.RecordClusterApplied(command.GetType().Name);
    }

    private async Task ApplyInvalidateAsync(InvalidateCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind == CacheInvalidationKind.Domain && !string.IsNullOrWhiteSpace(command.Domain))
        {
            await _invalidator.InvalidateDomainAsync(command.Domain, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Kind == CacheInvalidationKind.EntityKind
            && !string.IsNullOrWhiteSpace(command.Domain)
            && !string.IsNullOrWhiteSpace(command.EntityKind))
        {
            await _invalidator.InvalidateEntityKindAsync(command.Domain, command.EntityKind, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (command.Kind == CacheInvalidationKind.Entity
            && !string.IsNullOrWhiteSpace(command.Domain)
            && !string.IsNullOrWhiteSpace(command.EntityKind))
        {
            if (command.ResourceIds is { Count: > 1 })
            {
                await _invalidator.InvalidateEntitiesAsync(
                        command.Domain,
                        command.EntityKind,
                        command.ResourceIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            string? id = command.EntityId;
            if (string.IsNullOrWhiteSpace(id) && command.ResourceIds is { Count: 1 })
                id = command.ResourceIds[0];

            if (!string.IsNullOrWhiteSpace(id))
            {
                await _invalidator.InvalidateEntityAsync(
                        command.Domain,
                        command.EntityKind,
                        id,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (command.Tags is { Length: > 0 })
        {
            await _invalidator.InvalidateTagsAsync(command.Tags, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "InvalidateCommand {CommandId} had no domain/entity/tags to apply (scope={Scope})",
            command.CommandId,
            command.Scope);
    }

    private void ApplyVersionBump(VersionBumpCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Domain) || string.IsNullOrWhiteSpace(command.Version))
        {
            _logger.LogWarning(
                "VersionBumpCommand {CommandId} missing domain or version",
                command.CommandId);
            return;
        }

        _overrides.SetVersion(command.Domain, command.Version);
    }

    private async Task ApplySettingsPatchAsync(SettingsPatchCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Domain))
        {
            _logger.LogWarning("SettingsPatchCommand {CommandId} missing domain", command.CommandId);
            return;
        }

        try
        {
            string[] settingIds = DomainSettingsInvalidationCoordinator.CanonicalizeSettingIds(command.Settings.Keys);
            IReadOnlyDictionary<string, System.Text.Json.JsonElement>? before =
                _settingsInvalidation?.Capture(command.Domain, settingIds);
            DomainSettingsPatchApplicator.Apply(
                command.Domain,
                command.Settings,
                _overrides,
                _settingsContributors);

            if (_settingsInvalidation is not null && before is not null)
            {
                await _settingsInvalidation.ApplyAsync(
                        command.Domain,
                        settingIds,
                        before,
                        command.ApplyImmediately,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                ex,
                "SettingsPatchCommand {CommandId} rejected: {Message}",
                command.CommandId,
                ex.Message);
        }
    }
}
