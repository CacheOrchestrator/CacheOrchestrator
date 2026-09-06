using CacheOrchestrator.Cluster;
using CacheOrchestrator.Invalidation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheOrchestrator.HttpBus;

/// <summary>
/// Versioned HTTP wire contract. Core cluster commands are semantic operations and are never
/// serialized directly, so another transport can define an independent protocol.
/// </summary>
internal sealed record ClusterCommandEnvelopeV1
{
    public const int CurrentProtocolVersion = 1;

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
    public required string CommandType { get; init; }
    public required Guid CommandId { get; init; }
    public required string OriginInstanceId { get; init; }
    public required string Namespace { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string? CorrelationId { get; init; }
    public CacheInvalidationKind? Kind { get; init; }
    public string? Scope { get; init; }
    public string[]? Tags { get; init; }
    public string? Domain { get; init; }
    public string? EntityKind { get; init; }
    public string? EntityId { get; init; }
    public IReadOnlyList<string>? ResourceIds { get; init; }
    public string? Version { get; init; }
    public Dictionary<string, JsonElement>? Settings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ApplyImmediately { get; init; }

    public static ClusterCommandEnvelopeV1 FromCommand(ClusterCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            InvalidateCommand invalidate => CreateBase(invalidate, "invalidate") with
            {
                Kind = invalidate.Kind,
                Scope = invalidate.Scope,
                Tags = invalidate.Tags,
                Domain = invalidate.Domain,
                EntityKind = invalidate.EntityKind,
                EntityId = invalidate.EntityId,
                ResourceIds = invalidate.ResourceIds
            },
            VersionBumpCommand version => CreateBase(version, "versionBump") with
            {
                Domain = version.Domain,
                Version = version.Version
            },
            SettingsPatchCommand patch => CreateBase(patch, "settingsPatch") with
            {
                Domain = patch.Domain,
                Settings = patch.Settings,
                ApplyImmediately = patch.ApplyImmediately
            },
            _ => throw new NotSupportedException(
                $"HTTP cluster protocol v1 does not support command type '{command.GetType().FullName}'.")
        };
    }

    public ClusterCommand ToCommand()
    {
        if (ProtocolVersion != CurrentProtocolVersion)
            throw new JsonException($"Unsupported cluster protocol version '{ProtocolVersion}'.");

        return CommandType switch
        {
            "invalidate" => new InvalidateCommand
            {
                CommandId = CommandId,
                OriginInstanceId = Require(OriginInstanceId, nameof(OriginInstanceId)),
                Namespace = Require(Namespace, nameof(Namespace)),
                TimestampUtc = TimestampUtc,
                CorrelationId = CorrelationId,
                Kind = Kind ?? throw new JsonException("kind is required for invalidate."),
                Scope = Require(Scope, nameof(Scope)),
                Tags = Tags ?? throw new JsonException("tags is required for invalidate."),
                Domain = Domain,
                EntityKind = EntityKind,
                EntityId = EntityId,
                ResourceIds = ResourceIds
            },
            "versionBump" => new VersionBumpCommand
            {
                CommandId = CommandId,
                OriginInstanceId = Require(OriginInstanceId, nameof(OriginInstanceId)),
                Namespace = Require(Namespace, nameof(Namespace)),
                TimestampUtc = TimestampUtc,
                CorrelationId = CorrelationId,
                Domain = Require(Domain, nameof(Domain)),
                Version = Require(Version, nameof(Version))
            },
            "settingsPatch" => new SettingsPatchCommand
            {
                CommandId = CommandId,
                OriginInstanceId = Require(OriginInstanceId, nameof(OriginInstanceId)),
                Namespace = Require(Namespace, nameof(Namespace)),
                TimestampUtc = TimestampUtc,
                CorrelationId = CorrelationId,
                Domain = Require(Domain, nameof(Domain)),
                Settings = Settings ?? throw new JsonException("settings is required for settingsPatch."),
                ApplyImmediately = ApplyImmediately
            },
            _ => throw new JsonException($"Unsupported cluster command type '{CommandType}'.")
        };
    }

    private static ClusterCommandEnvelopeV1 CreateBase(ClusterCommand command, string commandType) =>
        new()
        {
            CommandType = commandType,
            CommandId = command.CommandId,
            OriginInstanceId = command.OriginInstanceId,
            Namespace = command.Namespace,
            TimestampUtc = command.TimestampUtc,
            CorrelationId = command.CorrelationId
        };

    private static string Require(string? value, string propertyName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new JsonException($"{propertyName} is required.")
            : value;
}
