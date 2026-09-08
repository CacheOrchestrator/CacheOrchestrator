namespace CacheOrchestrator.Cluster;

/// <summary>
/// Applies a cluster command on the <strong>local</strong> process only (never re-publishes).
/// </summary>
public interface IClusterCommandHandler
{
    /// <summary>
    /// Applies <paramref name="command"/> locally under a remote-origin scope so the invalidator
    /// does not publish again.
    /// </summary>
    /// <param name="command">Command received from the origin or Admin distribute path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The actual local outcome; transports must surface rejected or incomplete work as failure.</returns>
    Task<ClusterCommandResult> ApplyLocalAsync(ClusterCommand command, CancellationToken cancellationToken = default);
}
