namespace CacheOrchestrator.Cluster;

/// <summary>The local outcome of a cluster command delivery.</summary>
public enum ClusterCommandStatus
{
    /// <summary>All requested local work completed.</summary>
    Applied,
    /// <summary>A previous delivery already completed the same command.</summary>
    AlreadyApplied,
    /// <summary>The command was ignored because it targets another namespace or originated here.</summary>
    Ignored,
    /// <summary>The command is invalid or unsupported.</summary>
    Rejected,
    /// <summary>Some requested local work failed and may be retried.</summary>
    Failed
}
