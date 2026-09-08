namespace CacheOrchestrator.Edge.Providers;

internal sealed class EdgeProviderCatalog
{
    private readonly IReadOnlyDictionary<string, IEdgeResponseProvider> _responseProviders;
    private readonly IReadOnlyDictionary<string, IEdgeInvalidationProvider> _invalidationProviders;

    public EdgeProviderCatalog(
        IEnumerable<IEdgeResponseProvider> responseProviders,
        IEnumerable<IEdgeInvalidationProvider> invalidationProviders)
    {
        ArgumentNullException.ThrowIfNull(responseProviders);
        ArgumentNullException.ThrowIfNull(invalidationProviders);
        _responseProviders = BuildMap(responseProviders, static provider => provider.Name, "response");
        _invalidationProviders = BuildMap(invalidationProviders, static provider => provider.Name, "invalidation");
        foreach (IEdgeInvalidationProvider provider in _invalidationProviders.Values)
        {
            if (provider.Capabilities.MaxInvalidationBatchSize <= 0)
                throw new InvalidOperationException($"Edge invalidation provider '{provider.Name}' must declare a positive batch size.");
        }
    }

    public ResolvedEdgeProvider Resolve(string name)
    {
        if (!_responseProviders.TryGetValue(name, out IEdgeResponseProvider? responseProvider))
        {
            throw new InvalidOperationException($"Edge response provider '{name}' is not registered.");
        }
        if (!_invalidationProviders.TryGetValue(name, out IEdgeInvalidationProvider? invalidationProvider))
        {
            throw new InvalidOperationException($"Edge invalidation provider '{name}' is not registered.");
        }
        return new ResolvedEdgeProvider(responseProvider, invalidationProvider);
    }

    public IEdgeInvalidationProvider ResolveInvalidation(string name) =>
        _invalidationProviders.TryGetValue(name, out IEdgeInvalidationProvider? provider)
            ? provider
            : throw new InvalidOperationException($"Edge invalidation provider '{name}' is not registered.");

    public bool Contains(string name) =>
        _responseProviders.ContainsKey(name) && _invalidationProviders.ContainsKey(name);

    private static IReadOnlyDictionary<string, TProvider> BuildMap<TProvider>(
        IEnumerable<TProvider> providers,
        Func<TProvider, string> getName,
        string role)
        where TProvider : class
    {
        var map = new Dictionary<string, TProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (TProvider provider in providers)
        {
            string providerName = getName(provider);
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new InvalidOperationException($"An edge {role} provider has an empty name.");
            }
            if (!map.TryAdd(providerName, provider))
            {
                throw new InvalidOperationException(
                    $"Edge {role} provider '{providerName}' is registered more than once.");
            }
        }
        return map;
    }
}

internal sealed record ResolvedEdgeProvider(
    IEdgeResponseProvider Response,
    IEdgeInvalidationProvider Invalidation);
