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
    public async Task<ClusterCommandResult> ApplyLocalAsync(ClusterCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        CacheOrchestratorMetrics.RecordClusterReceived(command.GetType().Name);
        if (!string.Equals(command.Namespace, _options.CurrentValue.Namespace ?? string.Empty, StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring cluster command {CommandId}: namespace mismatch.", command.CommandId);
            return new(ClusterCommandStatus.Ignored, "namespace-mismatch");
        }
        if (string.Equals(command.OriginInstanceId, _instanceId.InstanceId, StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring cluster command {CommandId}: origin is self.", command.CommandId);
            return new(ClusterCommandStatus.Ignored, "origin-is-self");
        }
        if (command.CommandId == Guid.Empty)
            return new(ClusterCommandStatus.Rejected, "command-id-required");

        using (ClusterCommandScope.EnterRemote())
        {
            ClusterCommandResult result = await _dedupe.ExecuteAsync(command.CommandId,
                (execution, token) => ApplyCommandAsync(command, execution, token), cancellationToken).ConfigureAwait(false);
            if (result.Status == ClusterCommandStatus.Applied)
                CacheOrchestratorMetrics.RecordClusterApplied(command.GetType().Name);
            else if (result.Status == ClusterCommandStatus.AlreadyApplied)
                _logger.LogDebug("Cluster command {CommandId} was already applied.", command.CommandId);
            return result;
        }
    }

    private async Task<ClusterCommandResult> ApplyCommandAsync(
        ClusterCommand command, ClusterCommandExecution execution, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (command)
            {
                case InvalidateCommand invalidate:
                    CacheInvalidationResult invalidation = await ApplyInvalidateAsync(invalidate, cancellationToken).ConfigureAwait(false);
                    return new(invalidation.Succeeded ? ClusterCommandStatus.Applied : ClusterCommandStatus.Failed,
                        invalidation.Succeeded ? null : "invalidation-incomplete")
                    { Invalidation = invalidation };
                case VersionBumpCommand version:
                    ArgumentException.ThrowIfNullOrWhiteSpace(version.Domain);
                    ArgumentException.ThrowIfNullOrWhiteSpace(version.Version);
                    _overrides.SetVersion(version.Domain, version.Version);
                    return new(ClusterCommandStatus.Applied, localMutationApplied: true);
                case SettingsPatchCommand settings:
                    ArgumentException.ThrowIfNullOrWhiteSpace(settings.Domain);
                    if (_settingsInvalidation is null)
                    {
                        DomainSettingsPatchApplicator.Apply(settings.Domain, settings.Settings, _overrides, _settingsContributors);
                        return new(ClusterCommandStatus.Applied, localMutationApplied: true);
                    }
                    DomainSettingsInvalidationPlan plan = _settingsInvalidation.ApplyPatch(
                        settings.Domain, settings.Settings, _overrides, _settingsContributors, settings.ApplyImmediately);
                    // Retry only unfinished purges. Never reapply an older patch over a newer mutation.
                    execution.Resume = token => FinishSettingsAsync(plan, token);
                    return await execution.Resume(cancellationToken).ConfigureAwait(false);
                default:
                    return new(ClusterCommandStatus.Rejected, "unsupported-command");
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Cluster command {CommandId} rejected.", command.CommandId);
            return new(ClusterCommandStatus.Rejected, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cluster command {CommandId} failed.", command.CommandId);
            return new(ClusterCommandStatus.Failed, "execution-failed") { Errors = [ex.Message] };
        }
    }

    private async Task<ClusterCommandResult> FinishSettingsAsync(DomainSettingsInvalidationPlan plan, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> errors = await _settingsInvalidation!.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
        return new(errors.Count == 0 ? ClusterCommandStatus.Applied : ClusterCommandStatus.Failed,
            errors.Count == 0 ? null : "settings-invalidation-incomplete", localMutationApplied: true)
        { Errors = errors };
    }

    private async Task<CacheInvalidationResult> ApplyInvalidateAsync(InvalidateCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(command.Kind))
            throw new ArgumentException("Invalidation kind is not defined.", nameof(command));
        if (command.Kind == CacheInvalidationKind.Domain && !string.IsNullOrWhiteSpace(command.Domain))
            return await _invalidator.InvalidateDomainAsync(command.Domain, cancellationToken).ConfigureAwait(false);
        if (command.Kind == CacheInvalidationKind.EntityKind
            && !string.IsNullOrWhiteSpace(command.Domain) && !string.IsNullOrWhiteSpace(command.EntityKind))
        {
            return await _invalidator.InvalidateEntityKindAsync(command.Domain, command.EntityKind, cancellationToken).ConfigureAwait(false);
        }
        if (command.Kind == CacheInvalidationKind.Entity
            && !string.IsNullOrWhiteSpace(command.Domain) && !string.IsNullOrWhiteSpace(command.EntityKind))
        {
            if (command.ResourceIds is { Count: > 1 })
                return await _invalidator.InvalidateEntitiesAsync(command.Domain, command.EntityKind, command.ResourceIds, cancellationToken).ConfigureAwait(false);
            string? id = command.EntityId;
            if (string.IsNullOrWhiteSpace(id) && command.ResourceIds is { Count: 1 })
                id = command.ResourceIds[0];
            if (!string.IsNullOrWhiteSpace(id))
                return await _invalidator.InvalidateEntityAsync(command.Domain, command.EntityKind, id, cancellationToken).ConfigureAwait(false);
        }
        if (command.Tags is { Length: > 0 })
            return await _invalidator.InvalidateTagsAsync(command.Tags, cancellationToken).ConfigureAwait(false);
        throw new ArgumentException("Invalidation command has no valid domain, entity, or tags.", nameof(command));
    }
}
