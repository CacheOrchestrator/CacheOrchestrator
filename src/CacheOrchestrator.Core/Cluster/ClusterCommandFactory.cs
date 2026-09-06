using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Cluster;

/// <summary>
/// Builds stamped cluster commands for origin publish (invalidator / Admin distribute).
/// </summary>
internal sealed class ClusterCommandFactory
{
    private readonly IInstanceIdProvider _instanceId;
    private readonly IOptionsMonitor<CacheOrchestratorOptions> _options;

    public ClusterCommandFactory(
        IInstanceIdProvider instanceId,
        IOptionsMonitor<CacheOrchestratorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(options);
        _instanceId = instanceId;
        _options = options;
    }

    public InvalidateCommand CreateInvalidate(
        CacheInvalidationKind kind,
        string scopeLabel,
        IReadOnlyList<string> tags,
        string? domain,
        string? entityKind,
        string? entityId,
        IReadOnlyList<string>? resourceIds) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = _instanceId.InstanceId,
            Namespace = _options.CurrentValue.Namespace ?? string.Empty,
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = kind,
            Scope = scopeLabel,
            Tags = tags is string[] arr ? arr : [.. tags],
            Domain = domain,
            EntityKind = entityKind,
            EntityId = entityId,
            ResourceIds = resourceIds
        };

    public VersionBumpCommand CreateVersionBump(string domain, string version) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = _instanceId.InstanceId,
            Namespace = _options.CurrentValue.Namespace ?? string.Empty,
            TimestampUtc = DateTimeOffset.UtcNow,
            Domain = domain,
            Version = version
        };

    public SettingsPatchCommand CreateSettingsPatch(
        string domain,
        Dictionary<string, System.Text.Json.JsonElement> settings,
        bool applyImmediately) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = _instanceId.InstanceId,
            Namespace = _options.CurrentValue.Namespace ?? string.Empty,
            TimestampUtc = DateTimeOffset.UtcNow,
            Domain = domain,
            Settings = settings,
            ApplyImmediately = applyImmediately
        };
}
