using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace CacheOrchestrator.Edge.Providers;

/// <summary>Capabilities and limits exposed by a tag-native edge provider.</summary>
public sealed class EdgeProviderCapabilities
{
    /// <summary>Whether the provider supports response tags and invalidation by tag.</summary>
    public bool SupportsTagInvalidation { get; init; }

    /// <summary>Maximum aggregate response tag-header bytes.</summary>
    public int MaxResponseTagBytes { get; init; }

    /// <summary>Maximum tags accepted by one invalidation request.</summary>
    public int MaxInvalidationBatchSize { get; init; }

    /// <summary>Whether the response provider implements stale-while-revalidate semantics.</summary>
    public bool SupportsStaleWhileRevalidate { get; init; }

    /// <summary>Whether the response provider implements stale-if-error semantics.</summary>
    public bool SupportsStaleIfError { get; init; }
}

/// <summary>Final edge-cache metadata written to a response without network I/O.</summary>
public sealed class EdgeResponseMetadata
{
    /// <summary>Whether the response is eligible for shared edge caching.</summary>
    public bool IsCacheable { get; init; }

    /// <summary>Freshness duration.</summary>
    public TimeSpan Ttl { get; init; }

    /// <summary>Optional stale-while-revalidate window.</summary>
    public TimeSpan? StaleWhileRevalidate { get; init; }

    /// <summary>Optional stale-if-error window.</summary>
    public TimeSpan? StaleIfError { get; init; }

    /// <summary>Opaque provider-safe tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>One provider tag-invalidation request.</summary>
public sealed class EdgeInvalidationRequest
{
    /// <summary>The immutable target captured when the job was created.</summary>
    public required EdgeInvalidationTarget Target { get; init; }

    /// <summary>Named edge instance receiving the request.</summary>
    public string InstanceName => Target.InstanceName;

    /// <summary>Opaque edge tags to invalidate.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>Structured provider invalidation outcome.</summary>
public sealed class EdgeInvalidationResult
{
    /// <summary>Whether the provider accepted the invalidation.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Whether retrying may succeed.</summary>
    public bool IsTransient { get; init; }

    /// <summary>Optional provider-directed retry delay.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Sanitized failure description.</summary>
    public string? Error { get; init; }

    /// <summary>Successful result singleton.</summary>
    public static EdgeInvalidationResult Success { get; } = new() { Succeeded = true };
}

/// <summary>Writes provider-specific metadata used to store and tag edge responses.</summary>
public interface IEdgeResponseProvider
{
    /// <summary>Provider name used in configuration.</summary>
    string Name { get; }

    /// <summary>Provider capabilities and limits.</summary>
    EdgeProviderCapabilities Capabilities { get; }

    /// <summary>Writes provider-specific response metadata without network I/O.</summary>
    void ApplyResponseMetadata(HttpResponse response, EdgeResponseMetadata metadata);
}

/// <summary>Invalidates edge objects by opaque CacheOrchestrator tags.</summary>
public interface IEdgeInvalidationProvider
{
    /// <summary>Provider name used in configuration.</summary>
    string Name { get; }

    /// <summary>Provider capabilities and limits.</summary>
    EdgeProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Copies routing and credentials from one complete EdgeInstances:{name} configuration section.
    /// This runs when creating invalidation work or reload snapshots, never on the response hot path.
    /// </summary>
    EdgeInvalidationTarget CaptureTarget(string instanceName, IConfigurationSection instanceConfiguration);

    /// <summary>Invalidates an opaque tag batch using only its captured target, without resolving current configuration.</summary>
    ValueTask<EdgeInvalidationResult> InvalidateAsync(
        EdgeInvalidationRequest request,
        CancellationToken cancellationToken = default);
}
