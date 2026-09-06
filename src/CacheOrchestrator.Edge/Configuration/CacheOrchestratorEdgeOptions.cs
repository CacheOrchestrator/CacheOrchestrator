namespace CacheOrchestrator.Edge.Configuration;

/// <summary>Edge-cache configuration bound from the Cache section.</summary>
public sealed class CacheOrchestratorEdgeOptions
{
    /// <summary>Named edge instances.</summary>
    public Dictionary<string, EdgeInstanceOptions> EdgeInstances { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Background purge queue settings.</summary>
    public EdgeQueueOptions EdgeQueue { get; set; } = new();

    /// <summary>Default edge policy inherited by domains.</summary>
    public EdgeDomainContainer DomainDefaults { get; set; } = new();

    /// <summary>Per-domain edge policy overrides.</summary>
    public Dictionary<string, EdgeDomainContainer> Domains { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One named edge instance.</summary>
public sealed class EdgeInstanceOptions
{
    /// <summary>Registered provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Optional namespace used to isolate opaque edge tags.</summary>
    public string? Namespace { get; set; }
}

/// <summary>Container used to bind edge policy beside other domain sections.</summary>
public sealed class EdgeDomainContainer
{
    /// <summary>Edge policy for the domain.</summary>
    public DomainEdgeSettings? Edge { get; set; }
}

/// <summary>Per-domain edge settings; null values inherit from domain defaults.</summary>
public sealed class DomainEdgeSettings
{
    /// <summary>Whether edge caching and coordinated invalidation are enabled.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Whether the domain tag is purged when the application starts.</summary>
    public bool? PurgeOnStartup { get; set; }

    /// <summary>Name of the edge instance used by this domain.</summary>
    public string? Instance { get; set; }

    /// <summary>Edge freshness duration in seconds.</summary>
    public int? TtlSeconds { get; set; }

    /// <summary>Optional stale-while-revalidate window in seconds.</summary>
    public int? StaleWhileRevalidateSeconds { get; set; }

    /// <summary>Optional stale-if-error window in seconds.</summary>
    public int? StaleIfErrorSeconds { get; set; }
}

/// <summary>Background edge purge queue settings.</summary>
public sealed class EdgeQueueOptions
{
    /// <summary>Maximum number of pending purge jobs before producers apply backpressure.</summary>
    public int Capacity { get; set; } = 1024;

    /// <summary>Maximum provider calls attempted for a transient failure.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Window used to coalesce nearby invalidations.</summary>
    public int FlushIntervalSeconds { get; set; } = 1;

    /// <summary>Base delay used for exponential retries when the provider supplies no Retry-After value.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 1;
}
