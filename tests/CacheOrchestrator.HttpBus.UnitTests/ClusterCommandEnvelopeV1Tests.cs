using CacheOrchestrator.Cluster;
using CacheOrchestrator.Invalidation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheOrchestrator.HttpBus.UnitTests;

public class ClusterCommandEnvelopeV1Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void InvalidateCommand_HasStableV1JsonContract()
    {
        InvalidateCommand command = new()
        {
            CommandId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            OriginInstanceId = "node-a",
            Namespace = "store",
            TimestampUtc = DateTimeOffset.Parse("2026-08-28T10:15:30Z"),
            Kind = CacheInvalidationKind.Entity,
            Scope = "products/product/42",
            Tags = ["entity:products:product:42"],
            Domain = "products",
            EntityKind = "product",
            EntityId = "42"
        };

        string json = JsonSerializer.Serialize(ClusterCommandEnvelopeV1.FromCommand(command), JsonOptions);

        json.Should().Be(
            "{\"protocolVersion\":1,\"commandType\":\"invalidate\",\"commandId\":\"11111111-2222-3333-4444-555555555555\",\"originInstanceId\":\"node-a\",\"namespace\":\"store\",\"timestampUtc\":\"2026-08-28T10:15:30+00:00\",\"kind\":\"entity\",\"scope\":\"products/product/42\",\"tags\":[\"entity:products:product:42\"],\"domain\":\"products\",\"entityKind\":\"product\",\"entityId\":\"42\"}");
    }

    [Fact]
    public void Envelope_RoundTripsToSemanticCommand()
    {
        const string json =
            "{\"protocolVersion\":1,\"commandType\":\"versionBump\",\"commandId\":\"11111111-2222-3333-4444-555555555555\",\"originInstanceId\":\"node-a\",\"namespace\":\"store\",\"timestampUtc\":\"2026-08-28T10:15:30Z\",\"domain\":\"products\",\"version\":\"v2\"}";

        ClusterCommandEnvelopeV1 envelope = JsonSerializer.Deserialize<ClusterCommandEnvelopeV1>(json, JsonOptions)!;
        VersionBumpCommand command = envelope.ToCommand().Should().BeOfType<VersionBumpCommand>().Subject;

        command.Domain.Should().Be("products");
        command.Version.Should().Be("v2");
    }

    [Fact]
    public void SettingsPatch_RoundTripsApplyImmediately()
    {
        SettingsPatchCommand command = new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "node-a",
            Namespace = "store",
            TimestampUtc = DateTimeOffset.UtcNow,
            Domain = "products",
            Settings = new Dictionary<string, JsonElement>
            {
                ["outputCache.ttlSeconds"] = JsonSerializer.SerializeToElement(30)
            },
            ApplyImmediately = true
        };

        ClusterCommandEnvelopeV1 envelope = ClusterCommandEnvelopeV1.FromCommand(command);
        SettingsPatchCommand roundTrip = envelope.ToCommand()
            .Should().BeOfType<SettingsPatchCommand>().Subject;

        envelope.ApplyImmediately.Should().BeTrue();
        roundTrip.ApplyImmediately.Should().BeTrue();
    }

    [Fact]
    public void UnsupportedProtocolVersion_IsRejected()
    {
        ClusterCommandEnvelopeV1 envelope = new()
        {
            ProtocolVersion = 2,
            CommandType = "invalidate",
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "node-a",
            Namespace = "store",
            TimestampUtc = DateTimeOffset.UtcNow
        };

        Action act = () => envelope.ToCommand();

        act.Should().Throw<JsonException>()
            .WithMessage("*Unsupported cluster protocol version '2'*");
    }
}
