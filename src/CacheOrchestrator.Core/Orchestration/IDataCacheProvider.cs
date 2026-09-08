namespace CacheOrchestrator.Orchestration;

/// <summary>
/// Data-cache engine behind <see cref="ICacheOrchestrator"/> (e.g. FusionCache, HybridCache).
/// </summary>
/// <remarks>
/// Not intended as the primary app/library dependency — use <see cref="ICacheOrchestrator"/>.
/// A host should register exactly one implementation.
/// </remarks>
public interface IDataCacheProvider
{
    /// <summary>Stable provider name (e.g. <c>FusionCache</c>, <c>HybridCache</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Gets or creates a value at <paramref name="request"/>.Key with the given tags and domain policy.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="T"/> is the stored type (may itself be nullable, e.g. <c>string?</c>).
    /// Prefer this over a <c>T?</c> return so value-type entries stay <c>int</c> rather than <c>int?</c>.
    /// </remarks>
    ValueTask<DataCacheProviderResult<T>> GetOrCreateAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or creates a value whose complete tags are selected after each successful factory execution.
    /// </summary>
    /// <remarks>
    /// Providers must associate the selected tags with the same materialization before publishing it,
    /// including eager or background refresh. Do not implement this as a post-return overwrite.
    /// The selector runs only for newly produced values and must include any required request tags.
    /// </remarks>
    ValueTask<DataCacheProviderResult<T>> GetOrCreateWithTagsAsync<T>(
        DataCacheProviderRequest request,
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, IReadOnlyList<string>> tagSelector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites the value at <paramref name="request"/>.Key with <paramref name="value"/>
    /// and the request's tags / domain policy.
    /// </summary>
    ValueTask SetAsync<T>(
        DataCacheProviderRequest request,
        T value,
        CancellationToken cancellationToken = default);

    /// <summary>Logically invalidates tagged entries on one or every configured instance.</summary>
    ValueTask InvalidateAsync(
        DataCacheInvalidationRequest request,
        CancellationToken cancellationToken = default);
}
