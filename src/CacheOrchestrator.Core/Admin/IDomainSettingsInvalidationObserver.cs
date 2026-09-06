namespace CacheOrchestrator.Admin;

/// <summary>Optional external-cache hook for a planned domain-settings invalidation.</summary>
internal interface IDomainSettingsInvalidationObserver
{
    /// <summary>Invalidates external cached responses for the domain.</summary>
    ValueTask InvalidateDomainSettingsAsync(
        string domain,
        CancellationToken cancellationToken = default);
}
