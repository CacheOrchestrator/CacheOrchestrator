using System.Collections.ObjectModel;

namespace CacheOrchestrator.Edge.Providers;

/// <summary>
/// Immutable provider routing and credential snapshot for queued invalidation. Parameters may contain
/// secrets: durable queue implementations must protect them and must not log or expose them.
/// </summary>
public sealed class EdgeInvalidationTarget : IEquatable<EdgeInvalidationTarget>
{
    private readonly int _hashCode;

    /// <summary>Captures a provider target and copies its parameters. RoutingKey identifies the cache location.</summary>
    public EdgeInvalidationTarget(
        string instanceName, string providerName, string routingKey,
        IReadOnlyDictionary<string, string> parameters, int formatVersion = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfLessThan(formatVersion, 1);
        InstanceName = instanceName;
        ProviderName = providerName;
        RoutingKey = routingKey;
        FormatVersion = formatVersion;
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        var hash = new HashCode();
        hash.Add(instanceName, StringComparer.OrdinalIgnoreCase);
        hash.Add(providerName, StringComparer.OrdinalIgnoreCase);
        hash.Add(routingKey, StringComparer.Ordinal);
        hash.Add(formatVersion);
        foreach ((string key, string value) in parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            copy.Add(key, value);
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }
        Parameters = new ReadOnlyDictionary<string, string>(copy);
        _hashCode = hash.ToHashCode();
    }

    /// <summary>The logical instance name used for metrics and logs.</summary>
    public string InstanceName { get; }
    /// <summary>The provider that owns this target.</summary>
    public string ProviderName { get; }
    /// <summary>The provider's cache location identity, independent of credential rotation.</summary>
    public string RoutingKey { get; }
    /// <summary>The provider-owned parameter schema version.</summary>
    public int FormatVersion { get; }
    /// <summary>Copied provider parameters, potentially including credentials.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <inheritdoc />
    public bool Equals(EdgeInvalidationTarget? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || _hashCode != other._hashCode
            || !StringComparer.OrdinalIgnoreCase.Equals(InstanceName, other.InstanceName)
            || !StringComparer.OrdinalIgnoreCase.Equals(ProviderName, other.ProviderName)
            || !StringComparer.Ordinal.Equals(RoutingKey, other.RoutingKey)
            || FormatVersion != other.FormatVersion || Parameters.Count != other.Parameters.Count)
        {
            return false;
        }
        foreach ((string key, string value) in Parameters)
        {
            if (!other.Parameters.TryGetValue(key, out string? otherValue) || !StringComparer.Ordinal.Equals(value, otherValue))
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EdgeInvalidationTarget other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => _hashCode;
    /// <summary>Returns only the provider and logical instance, never routing parameters or credentials.</summary>
    public override string ToString() => $"{ProviderName}/{InstanceName}";
}
