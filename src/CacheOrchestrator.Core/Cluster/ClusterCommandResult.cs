using CacheOrchestrator.Invalidation;

namespace CacheOrchestrator.Cluster;

/// <summary>Truthful local execution outcome, including partial invalidation and published settings.</summary>
public sealed class ClusterCommandResult
{
    /// <summary>Creates an execution outcome.</summary>
    public ClusterCommandResult(ClusterCommandStatus status, string? reason = null, bool localMutationApplied = false)
    {
        Status = status;
        Reason = reason;
        LocalMutationApplied = localMutationApplied;
    }

    /// <summary>The outcome of this delivery.</summary>
    public ClusterCommandStatus Status { get; }
    /// <summary>A stable reason code or validation explanation.</summary>
    public string? Reason { get; }
    /// <summary>Whether every requested operation completed, including an already completed duplicate.</summary>
    public bool Succeeded => Status is ClusterCommandStatus.Applied or ClusterCommandStatus.AlreadyApplied;
    /// <summary>Whether the command's version/settings mutation was published, even if subsequent purging failed.</summary>
    public bool LocalMutationApplied { get; }
    /// <summary>Detailed invalidation outcome, when the command performs tag invalidation.</summary>
    public CacheInvalidationResult? Invalidation { get; init; }
    /// <summary>Errors from an incomplete settings invalidation.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    internal ClusterCommandResult AsDuplicate() => new(ClusterCommandStatus.AlreadyApplied, "already-applied", LocalMutationApplied)
    {
        Invalidation = Invalidation
    };
}
