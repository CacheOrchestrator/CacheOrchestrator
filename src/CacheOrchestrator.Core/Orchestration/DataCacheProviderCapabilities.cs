namespace CacheOrchestrator.Orchestration;

/// <summary>Immutable feature descriptor for a registered Data Cache provider implementation.</summary>
public sealed class DataCacheProviderCapabilities
{
    /// <summary>Whether domains may select independently configured named cache instances.</summary>
    public bool SupportsNamedInstances { get; init; }

    /// <summary>Whether expired values may be served when refresh fails.</summary>
    public bool SupportsFailSafe { get; init; }

    /// <summary>Whether entries may refresh eagerly before their logical expiration.</summary>
    public bool SupportsEagerRefresh { get; init; }

    /// <summary>Whether the provider can coordinate invalidation through a backplane.</summary>
    public bool SupportsBackplane { get; init; }

    /// <summary>Whether the provider implements <see cref="IDataCacheBatchInvalidator"/>.</summary>
    public bool SupportsBatchInvalidation { get; init; }
}

/// <summary>Optional provider metadata surface; custom providers do not have to implement it.</summary>
public interface IDataCacheProviderCapabilities
{
    /// <summary>Capabilities supported by this provider implementation.</summary>
    DataCacheProviderCapabilities Capabilities { get; }
}
