using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CacheOrchestrator.Configuration;

/// <summary>
/// Builds the domain-settings catalog from <see cref="DomainSettingAttribute"/> on
/// <see cref="CacheOrchestratorOptions.DomainCacheSettings"/> (including nested sections).
/// </summary>
public sealed class DomainSettingCatalog
{
    private readonly IReadOnlyList<DomainSettingCatalogEntry> _entries;
    private readonly IReadOnlyList<DomainSettingCatalogEntry> _overlays;
    private readonly Dictionary<string, DomainSettingCatalogEntry> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    internal static DomainSettingCatalog Core { get; } = new();

    internal DomainSettingCatalog() : this([]) { }

    private DomainSettingCatalog(IEnumerable<Section> sections)
    {
        _entries = Build(sections);
        _overlays = Array.AsReadOnly(_entries.Where(static entry => entry.RuntimeOverlay).ToArray());
        foreach (DomainSettingCatalogEntry entry in _entries)
        {
            AddAlias(entry.Id, entry);
            AddAlias(entry.PropertyName, entry);
        }
    }

    private void AddAlias(string alias, DomainSettingCatalogEntry entry)
    {
        if (_byAlias.TryGetValue(alias, out DomainSettingCatalogEntry? existing))
        {
            if (!ReferenceEquals(existing, entry))
                throw new InvalidOperationException($"Domain setting alias '{alias}' is registered more than once.");
            return;
        }
        _byAlias.Add(alias, entry);
    }

    internal static void Register(IServiceCollection services) =>
        services.TryAddSingleton(sp => new DomainSettingCatalog(sp.GetServices<Section>()));

    private sealed record Section(Type Type, string IdPrefix, string PropertyPrefix);

    /// <summary>
    /// Registers an additional attributed settings type under a fixed id prefix.
    /// Registration is scoped to the service collection. Each built provider owns an immutable catalog.
    /// </summary>
    public static void RegisterSection(IServiceCollection services, Type settingsType, string idPrefix, string propertyPrefix)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settingsType);
        ArgumentNullException.ThrowIfNull(idPrefix);
        ArgumentNullException.ThrowIfNull(propertyPrefix);

        Register(services);
        var section = new Section(settingsType, idPrefix, propertyPrefix);
        if (!services.Any(descriptor => descriptor.ImplementationInstance is Section registered && registered == section))
            services.AddSingleton(section);
    }

    /// <summary>All attributed domain settings (config shape).</summary>
    public IReadOnlyList<DomainSettingCatalogEntry> GetEntries() => _entries;

    /// <summary>Only settings with <see cref="DomainSettingAttribute.RuntimeOverlay"/>.</summary>
    public IReadOnlyList<DomainSettingCatalogEntry> GetOverlayEntries() => _overlays;

    /// <summary>Looks up an entry by camelCase <paramref name="id"/> (case-insensitive).</summary>
    public DomainSettingCatalogEntry? Find(string? id) =>
        id is not null && _byAlias.TryGetValue(id, out DomainSettingCatalogEntry? entry) ? entry : null;

    private static IReadOnlyList<DomainSettingCatalogEntry> Build(IEnumerable<Section> sections)
    {
        List<DomainSettingCatalogEntry> list = [];
        Walk(typeof(CacheOrchestratorOptions.DomainCacheSettings), prefixId: null, prefixProperty: null, overlayOnly: false, list);

        foreach ((Type type, string idPrefix, string propertyPrefix) in sections)
        {
            Walk(
                type,
                idPrefix.Length == 0 ? null : idPrefix,
                propertyPrefix.Length == 0 ? null : propertyPrefix,
                overlayOnly: false,
                list);
        }

        list.Sort((a, b) =>
        {
            int g = string.Compare(a.Group ?? "", b.Group ?? "", StringComparison.OrdinalIgnoreCase);
            return g != 0 ? g : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        return list.AsReadOnly();
    }

    private static void Walk(
        Type type,
        string? prefixId,
        string? prefixProperty,
        bool overlayOnly,
        List<DomainSettingCatalogEntry> list)
    {
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            string camel = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            string id = prefixId is null ? camel : prefixId + "." + camel;
            string propertyName = prefixProperty is null ? prop.Name : prefixProperty + "." + prop.Name;

            if (propType == typeof(DomainDataCacheSettings))
            {
                Walk(propType, id, propertyName, overlayOnly: false, list);
                continue;
            }

            DomainSettingAttribute? attr = prop.GetCustomAttribute<DomainSettingAttribute>();
            if (attr is null)
                continue;
            if (overlayOnly && !attr.RuntimeOverlay)
                continue;

            Type t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            IReadOnlyList<string>? enumValues = null;
            if (attr.Kind == DomainSettingValueKind.Enum && t.IsEnum)
                enumValues = Array.AsReadOnly(Enum.GetNames(t));

            list.Add(new DomainSettingCatalogEntry
            {
                Id = id,
                PropertyName = propertyName,
                DisplayName = attr.DisplayName ?? SplitDisplayName(prop.Name),
                Group = attr.Group,
                Kind = attr.Kind,
                RuntimeOverlay = attr.RuntimeOverlay,
                EnumValues = enumValues,
            });
        }
    }

    private static string SplitDisplayName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return propertyName;
        Span<char> buf = stackalloc char[propertyName.Length * 2];
        int n = 0;
        for (int i = 0; i < propertyName.Length; i++)
        {
            char c = propertyName[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(propertyName[i - 1]) || (i + 1 < propertyName.Length && char.IsLower(propertyName[i + 1]))))
                buf[n++] = ' ';
            buf[n++] = c;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(new string(buf[..n]).ToLowerInvariant());
    }
}
