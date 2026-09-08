using System.Text.Json.Serialization;

namespace CacheOrchestrator.HybridCache;

internal sealed class HybridProviderCacheEntry<T>
{
    public required T Value { get; init; }

    public required Guid MaterializationId { get; init; }

    // HybridCache snapshots tags before invoking a factory. Dynamic footprints publish a unique,
    // tagged validity marker before the payload. Static-tag entries need no marker or extra read.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValidationKey { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ValidationTags { get; init; }
}
