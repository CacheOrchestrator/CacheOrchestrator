using System.Text.Json;

namespace CacheOrchestrator.Cluster;

/// <summary>
/// Cluster command that merges runtime domain settings overlays on each peer.
/// </summary>
public sealed record SettingsPatchCommand : ClusterCommand
{
    /// <summary>Domain name (normalized on apply).</summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Sparse camelCase setting map (same shape as Admin <c>PATCH …/settings</c>).
    /// </summary>
    public required Dictionary<string, JsonElement> Settings { get; init; }

    /// <summary>Whether directional policy changes should evict affected local entries.</summary>
    public bool ApplyImmediately { get; init; }
}
