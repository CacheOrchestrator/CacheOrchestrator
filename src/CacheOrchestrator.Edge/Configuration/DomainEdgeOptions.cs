namespace CacheOrchestrator.Edge.Configuration;

/// <summary>Resolved edge policy for one normalized CacheOrchestrator domain.</summary>
public sealed class DomainEdgeOptions
{
    /// <summary>Normalized domain name.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Whether Edge cache integration is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether the domain tag is purged when the application starts.</summary>
    public bool PurgeOnStartup { get; init; }

    /// <summary>Name of the configured edge instance.</summary>
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>Edge freshness duration.</summary>
    public TimeSpan Ttl { get; init; }

    /// <summary>Optional stale-while-revalidate window.</summary>
    public TimeSpan? StaleWhileRevalidate { get; init; }

    /// <summary>Optional stale-if-error window.</summary>
    public TimeSpan? StaleIfError { get; init; }
}

/// <summary>Resolves effective edge settings for CacheOrchestrator domains.</summary>
public interface IDomainEdgeOptionsProvider
{
    /// <summary>Returns the effective edge policy for a domain.</summary>
    DomainEdgeOptions GetDomainOptions(string domain);
}
