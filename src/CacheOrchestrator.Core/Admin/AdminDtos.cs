namespace CacheOrchestrator.Admin;

/// <summary>Discovered endpoint metadata.</summary>
public sealed class AdminEndpointInfoDto
{
    /// <summary>e.g. <c>GET /api/products/{id}</c>.</summary>
    public required string Route { get; init; }

    /// <summary>HTTP method.</summary>
    public required string Method { get; init; }

    /// <summary>Route pattern raw text.</summary>
    public required string Pattern { get; init; }

    /// <summary>Fixed domain from metadata, if any.</summary>
    public string? ConfiguredDomain { get; init; }

    /// <summary>Optional display name (controller/action).</summary>
    public string? DisplayName { get; init; }
}

/// <summary>Effective domain configuration snapshot for Admin.</summary>
public sealed class AdminDomainConfigDto
{
    /// <summary>Normalized domain name.</summary>
    public required string Name { get; init; }

    /// <summary>Effective Version.</summary>
    public required string Version { get; init; }

    /// <summary>True when Version is a runtime override.</summary>
    public bool VersionIsRuntimeOverride { get; init; }

    /// <summary>Output Cache enabled.</summary>
    public bool OutputCacheEnabled { get; init; }

    /// <summary>Data cache enabled.</summary>
    public bool DataCacheEnabled { get; init; }

    /// <summary>Data-cache instance name.</summary>
    public required string DataCacheInstanceName { get; init; }

    /// <summary>Output Cache TTL seconds.</summary>
    public int OutputCacheTtlSeconds { get; init; }

    /// <summary>Data-cache soft TTL seconds.</summary>
    public int DataCacheTtlSeconds { get; init; }

    /// <summary>Client TTL seconds.</summary>
    public int ClientTtlSeconds { get; init; }

    /// <summary>Client TTL min seconds.</summary>
    public int ClientTtlMinSeconds { get; init; }

    /// <summary>Scheduled update UTC, if any.</summary>
    public DateTimeOffset? ScheduledUpdateUtc { get; init; }

    /// <summary>Current schedule phase wire value.</summary>
    public string? SchedulePhase { get; init; }

    /// <summary>Which fields are runtime overrides.</summary>
    public AdminRuntimeOverrideFlagsDto? RuntimeOverrides { get; init; }
}

/// <summary>Flags indicating which effective values come from runtime overlay.</summary>
public sealed class AdminRuntimeOverrideFlagsDto
{
    /// <summary>Version overridden.</summary>
    public bool Version { get; init; }

    /// <summary>Output TTL overridden.</summary>
    public bool OutputCacheTtl { get; init; }

    /// <summary>Data-cache soft TTL overridden.</summary>
    public bool DataCacheTtl { get; init; }

    /// <summary>Client TTL overridden.</summary>
    public bool ClientTtl { get; init; }

    /// <summary>Client min TTL overridden.</summary>
    public bool ClientTtlMin { get; init; }
}

/// <summary>Admin API health response.</summary>
public sealed class AdminHealthDto
{
    /// <summary>
    /// <see langword="true"/> when live counters can be read and every registered
    /// <c>ICacheOrchestratorHealthProbe</c> succeeds. <see langword="false"/> means the instance
    /// responded but is degraded (Admin Console maps this to Degraded). HTTP 200 is still returned.
    /// </summary>
    public bool Healthy { get; init; } = true;

    /// <summary>Instance id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>UTC now on the instance.</summary>
    public DateTimeOffset UtcNow { get; init; }

    /// <summary>Admin feature is enabled on this process.</summary>
    public bool AdminEnabled { get; init; }

    /// <summary>UTC process start time (host process).</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Elapsed time since <see cref="StartedAtUtc"/> in whole seconds.</summary>
    public long UptimeSeconds { get; init; }

    /// <summary>
    /// Lifetime request count on this process (from Admin live counters), when available.
    /// </summary>
    public long Requests { get; init; }

    /// <summary>Registered Data Cache provider name.</summary>
    public string DataCacheProvider { get; init; } = "Unknown";

    /// <summary>
    /// Capabilities declared by the registered provider. All values are false when a custom
    /// provider does not implement <c>IDataCacheProviderCapabilities</c>.
    /// </summary>
    public Orchestration.DataCacheProviderCapabilities DataCacheCapabilities { get; init; } = new();
}

/// <summary>Cluster identity and membership snapshot for the current instance.</summary>
public sealed class AdminClusterInfoDto
{
    /// <summary>Current instance id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Cache namespace used to isolate cluster commands.</summary>
    public required string Namespace { get; init; }

    /// <summary>Whether a cluster command bus is enabled.</summary>
    public bool BusEnabled { get; init; }

    /// <summary>Membership provider kind.</summary>
    public required string Membership { get; init; }

    /// <summary>Number of peers returned by membership discovery.</summary>
    public int PeerCount => Peers.Count;

    /// <summary>Discovered peers.</summary>
    public required IReadOnlyList<AdminClusterPeerDto> Peers { get; init; }
}

/// <summary>One peer in a management cluster snapshot.</summary>
public sealed class AdminClusterPeerDto
{
    /// <summary>Peer id.</summary>
    public required string Id { get; init; }

    /// <summary>Transport address exposed by membership discovery.</summary>
    public required string Url { get; init; }
}

/// <summary>Invalidate request body.</summary>
public sealed class AdminInvalidateRequest
{
    /// <summary><c>domain</c>, <c>entity</c>, <c>entityKind</c>, or <c>tags</c>.</summary>
    public string Scope { get; set; } = "domain";

    /// <summary>Domain name (required for domain/entity scopes).</summary>
    public string? Domain { get; set; }

    /// <summary>Entity kind (required for entity / entityKind scopes).</summary>
    public string? EntityKind { get; set; }

    /// <summary>Entity id (required for entity scope).</summary>
    public string? EntityId { get; set; }

    /// <summary>Tags (required for tags scope).</summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// When <see langword="true"/> and the cluster bus is enabled, peers receive the same invalidation.
    /// Default <see langword="false"/> = this process only (does not publish).
    /// Programmatic <c>ICacheOrchestratorInvalidator</c> always publishes when the bus is enabled.
    /// </summary>
    public bool Distribute { get; set; }

}

/// <summary>Version set/bump request body.</summary>
public sealed class AdminVersionRequest
{
    /// <summary>
    /// New version token. When null or empty, the server generates a unique stamp.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When <see langword="true"/> and the cluster bus is enabled, peers apply the same Version overlay.
    /// Default <see langword="false"/> = this process only.
    /// </summary>
    public bool Distribute { get; set; }
}

/// <summary>Sparse domain settings patch (camelCase keys from <see cref="Configuration.DomainSettingCatalog"/>).</summary>
public sealed class AdminSettingsPatchRequest
{
    /// <summary>Setting id → value. Only <c>runtimeOverlay</c> catalog entries are accepted.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Settings { get; set; }

    /// <summary>
    /// When <see langword="true"/> and the cluster bus is enabled, peers apply the same overlay.
    /// Default <see langword="false"/> = this process only.
    /// </summary>
    public bool Distribute { get; set; }

    /// <summary>
    /// When <see langword="true"/>, changes that shorten an existing cache policy also evict
    /// affected entries. Default <see langword="false"/> lets entries retain their creation policy.
    /// </summary>
    public bool ApplyImmediately { get; set; }
}

/// <summary>Domain settings catalog response.</summary>
public sealed class AdminDomainSettingsCatalogDto
{
    /// <summary>Catalog entries.</summary>
    public required IReadOnlyList<Configuration.DomainSettingCatalogEntry> Settings { get; init; }
}

/// <summary>Response after version or settings mutation.</summary>
public sealed class AdminDomainMutationResultDto
{
    /// <summary>Domain name.</summary>
    public required string Domain { get; init; }

    /// <summary>Effective configuration after the change.</summary>
    public required AdminDomainConfigDto Effective { get; init; }

    /// <summary>
    /// Cluster delivery result when distribution was requested and the bus was enabled;
    /// null when the mutation remained local.
    /// </summary>
    public Cluster.ClusterPublishResult? ClusterPublish { get; init; }
}
