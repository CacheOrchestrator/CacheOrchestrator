namespace CacheOrchestrator.Admin;

/// <summary>
/// Optional hook invoked after a runtime domain Version change is applied locally.
/// </summary>
/// <remarks>
/// Observers must not throw for normal control flow. Exceptions are logged and do not
/// fail the Version change.
/// </remarks>
public interface IDomainVersionChangeObserver
{
    /// <summary>Called after the effective runtime Version has changed.</summary>
    /// <param name="domain">Normalized domain name.</param>
    /// <param name="version">New effective Version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask OnDomainVersionChangedAsync(
        string domain,
        string version,
        CancellationToken cancellationToken = default);
}
