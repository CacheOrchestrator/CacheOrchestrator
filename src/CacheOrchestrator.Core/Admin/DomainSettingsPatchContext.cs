namespace CacheOrchestrator.Admin;

/// <summary>
/// A private working copy of one domain's runtime overlays. Contributors prepare immutable
/// values here; the store publishes every section together only after all preparation succeeds.
/// Do not retain this context or mutate values obtained from it.
/// </summary>
public sealed class DomainSettingsPatchContext
{
    private readonly Dictionary<Type, object> _sections;
    private bool _completed;

    internal DomainSettingsPatchContext(string domain, int stamp, Dictionary<Type, object> sections)
    {
        Domain = domain;
        Stamp = stamp;
        _sections = new(sections);
    }

    /// <summary>The normalized domain being changed.</summary>
    public string Domain { get; }

    /// <summary>The revision assigned if this preparation succeeds.</summary>
    public int Stamp { get; }

    /// <summary>Gets the current or already prepared immutable section.</summary>
    public T? Get<T>() where T : class => _sections.TryGetValue(typeof(T), out object? value) ? (T)value : null;

    /// <summary>Stages an immutable section without making it visible to readers.</summary>
    public void Set<T>(T settings) where T : class
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_completed)
            throw new InvalidOperationException("Settings preparation has already completed.");
        _sections[typeof(T)] = settings;
    }

    /// <summary>Stages removal of one package-owned overlay section.</summary>
    public bool Remove<T>() where T : class
    {
        if (_completed)
            throw new InvalidOperationException("Settings preparation has already completed.");
        return _sections.Remove(typeof(T));
    }

    internal Dictionary<Type, object> Complete()
    {
        _completed = true;
        return _sections;
    }
}
