using CacheOrchestrator.Configuration;
using System.Collections.Concurrent;

namespace CacheOrchestrator.Admin;

/// <summary>
/// Default in-memory <see cref="IDomainRuntimeOverrideStore"/>.
/// </summary>
internal sealed class DomainRuntimeOverrideStore : IDomainRuntimeOverrideStore
{
    private readonly ConcurrentDictionary<string, DomainEntry> _map = new(StringComparer.Ordinal);
    private int _stamp;

    private sealed class DomainEntry
    {
#if NET9_0_OR_GREATER
        public readonly Lock Gate = new();
#else
        public readonly object Gate = new();
#endif
        public Snapshot State = new(0, []);
        public bool Preparing;
    }

    private sealed record Snapshot(int Stamp, Dictionary<Type, object> Sections);

    public T? GetSettings<T>(string domain) where T : class
    {
        if (!_map.TryGetValue(DomainName.Normalize(domain), out DomainEntry? entry))
            return null;
        Snapshot state = Volatile.Read(ref entry.State);
        return state.Sections.TryGetValue(typeof(T), out object? value) ? (T)value : null;
    }

    public DomainRuntimeOverride? Get(string domain) => GetSettings<DomainRuntimeOverride>(domain);

    public int GetStamp(string domain) =>
        _map.TryGetValue(DomainName.Normalize(domain), out DomainEntry? entry)
            ? Volatile.Read(ref entry.State).Stamp : 0;

    public void Update(string domain, Action<DomainSettingsPatchContext> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        string key = DomainName.Normalize(domain);
        DomainEntry entry = _map.GetOrAdd(key, static _ => new());
        lock (entry.Gate)
        {
            if (entry.Preparing)
                throw new InvalidOperationException("Nested settings mutations are not supported.");
            entry.Preparing = true;
            var context = new DomainSettingsPatchContext(key, NextStamp(), entry.State.Sections);
            try
            {
                prepare(context);
                Volatile.Write(ref entry.State, new(context.Stamp, context.Complete()));
            }
            finally
            {
                context.Complete();
                entry.Preparing = false;
            }
        }
    }

    public DomainRuntimeOverride SetVersion(string domain, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        DomainRuntimeOverride result = null!;
        Update(domain, context =>
        {
            result = WithVersion(context.Get<DomainRuntimeOverride>() ?? new(), version.Trim(), context.Stamp);
            context.Set(result);
        });
        return result;
    }

    public DomainRuntimeOverride PatchSettings(string domain, DomainSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.HasAny)
            throw new ArgumentException("At least one setting must be set.", nameof(patch));
        DomainRuntimeOverride result = null!;
        Update(domain, context =>
        {
            result = Merge(context.Get<DomainRuntimeOverride>() ?? new(), patch, context.Stamp);
            context.Set(result);
        });
        return result;
    }

    public bool Clear(string domain)
    {
        if (!_map.TryGetValue(DomainName.Normalize(domain), out DomainEntry? entry))
            return false;
        lock (entry.Gate)
        {
            if (entry.Preparing)
                throw new InvalidOperationException("Nested settings mutations are not supported.");
            bool hadValues = entry.State.Sections.Count != 0;
            Volatile.Write(ref entry.State, new(NextStamp(), []));
            return hadValues;
        }
    }

    public IReadOnlyCollection<string> GetOverriddenDomains() =>
        [.. _map.Where(static pair => Volatile.Read(ref pair.Value.State).Sections.Count != 0).Select(static pair => pair.Key)];

    internal static DomainRuntimeOverride FromPatch(DomainSettingsPatch patch, string? version, int stamp) =>
        Merge(new DomainRuntimeOverride { Stamp = stamp, Version = version }, patch, stamp);

    internal static DomainRuntimeOverride Merge(DomainRuntimeOverride existing, DomainSettingsPatch patch, int stamp) =>
        new()
        {
            Stamp = stamp,
            Version = existing.Version,
            DataCacheEnabled = patch.DataCacheEnabled ?? existing.DataCacheEnabled,
            DataCacheTtl = patch.DataCacheTtl ?? existing.DataCacheTtl,
        };

    private static DomainRuntimeOverride WithVersion(DomainRuntimeOverride existing, string version, int stamp) =>
        new()
        {
            Stamp = stamp,
            Version = version,
            DataCacheEnabled = existing.DataCacheEnabled,
            DataCacheTtl = existing.DataCacheTtl,
        };

    private int NextStamp() => Interlocked.Increment(ref _stamp);
}

/// <summary>No-op store used when Admin is disabled.</summary>
internal sealed class NullDomainRuntimeOverrideStore : IDomainRuntimeOverrideStore
{
    public static readonly NullDomainRuntimeOverrideStore Instance = new();

    private NullDomainRuntimeOverrideStore()
    {
    }

    public T? GetSettings<T>(string domain) where T : class => null;

    public void Update(string domain, Action<DomainSettingsPatchContext> prepare) =>
        throw new InvalidOperationException("Admin runtime overrides are disabled.");

    public DomainRuntimeOverride? Get(string domain) => null;

    public int GetStamp(string domain) => 0;

    public DomainRuntimeOverride SetVersion(string domain, string version) =>
        throw new InvalidOperationException("Admin runtime overrides are disabled.");

    public DomainRuntimeOverride PatchSettings(string domain, DomainSettingsPatch patch) =>
        throw new InvalidOperationException("Admin runtime overrides are disabled.");

    public bool Clear(string domain) => false;

    public IReadOnlyCollection<string> GetOverriddenDomains() => [];
}
