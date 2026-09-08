namespace CacheOrchestrator.Admin;

/// <summary>
/// Process-local runtime overlays for domain Version / settings (Admin).
/// </summary>
public interface IDomainRuntimeOverrideStore
{
    /// <summary>Gets an immutable package-owned overlay section, or null.</summary>
    T? GetSettings<T>(string domain) where T : class;

    /// <summary>
    /// Serializes preparation with other changes to this domain and atomically publishes all sections.
    /// If preparation throws, no changes become visible. Preparation must only stage immutable values
    /// in the supplied context and must not perform external side effects or nested store mutations.
    /// </summary>
    void Update(string domain, Action<DomainSettingsPatchContext> prepare);

    /// <summary>Gets the overlay for a domain, or null.</summary>
    DomainRuntimeOverride? Get(string domain);

    /// <summary>Current domain revision (0 before its first mutation). Clearing also advances the revision.</summary>
    int GetStamp(string domain);

    /// <summary>Sets or replaces the Version overlay.</summary>
    DomainRuntimeOverride SetVersion(string domain, string version);

    /// <summary>Merges a partial settings patch into the domain overlay.</summary>
    DomainRuntimeOverride PatchSettings(string domain, DomainSettingsPatch patch);

    /// <summary>Clears all overlays for a domain.</summary>
    bool Clear(string domain);

    /// <summary>Domains that currently have any overlay.</summary>
    IReadOnlyCollection<string> GetOverriddenDomains();
}
