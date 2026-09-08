namespace CacheOrchestrator.Orchestration;

/// <summary>
/// Pass-through data-cache provider used when no Fusion/Hybrid package is registered
/// (Output Cache–only hosts). Factories always run; tag removes are no-ops.
/// </summary>
internal sealed class NullDataCacheProvider : IDataCacheProvider, IDataCacheProviderCapabilities
{
    private static readonly DataCacheProviderCapabilities ProviderCapabilities = new();
    /// <summary>Shared instance for DI and tests.</summary>
    public static readonly NullDataCacheProvider Instance = new();

    private NullDataCacheProvider()
    {
    }

    /// <inheritdoc />
    public ValueTask<DataCacheProviderResult<T>> GetOrCreateWithTagsAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, IReadOnlyList<string>> tagSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tagSelector);
        return GetOrCreateAsync(request, factory, cancellationToken);
    }

    /// <inheritdoc />
    public string Name => "Null";

    public DataCacheProviderCapabilities Capabilities => ProviderCapabilities;

    /// <inheritdoc />
    public async ValueTask<DataCacheProviderResult<T>> GetOrCreateAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(factory);
        T value = await factory(cancellationToken).ConfigureAwait(false);
        return new DataCacheProviderResult<T>(value, DataCacheProviderOutcome.Materialized);
    }

    /// <inheritdoc />
    public ValueTask SetAsync<T>(
        DataCacheProviderRequest request,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask InvalidateAsync(
        DataCacheInvalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.CompletedTask;
    }
}
