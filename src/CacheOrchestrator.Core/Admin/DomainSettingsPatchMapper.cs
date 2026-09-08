using CacheOrchestrator.Configuration;
using System.Globalization;
using System.Text.Json;

namespace CacheOrchestrator.Admin;

/// <summary>Maps portable Core domain settings from the management wire shape.</summary>
internal static class DomainSettingsPatchMapper
{
    /// <summary>Builds a Core patch from portable settings.</summary>
    public static DomainSettingsPatch FromDictionary(IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count == 0)
            throw new ArgumentException("At least one setting must be set.", nameof(settings));

        bool? enabled = null;
        TimeSpan? ttl = null;
        foreach ((string rawKey, JsonElement value) in settings)
        {
            DomainSettingCatalogEntry entry = DomainSettingCatalog.Core.Find(rawKey)
                ?? throw new ArgumentException($"Unknown domain setting '{rawKey}'.", nameof(settings));
            if (!entry.RuntimeOverlay)
                throw new ArgumentException($"Setting '{entry.Id}' is not runtime-patchable.", nameof(settings));

            switch (entry.Id)
            {
                case "dataCache.enabled":
                    enabled = ReadBool(value, entry.Id);
                    break;
                case "dataCache.ttlSeconds":
                    ttl = TimeSpan.FromSeconds(ReadNonNegativeInt(value, entry.Id));
                    break;
                default:
                    throw new ArgumentException($"Setting '{entry.Id}' is not owned by Core.", nameof(settings));
            }
        }

        return new DomainSettingsPatch { DataCacheEnabled = enabled, DataCacheTtl = ttl };
    }

    private static bool ReadBool(JsonElement value, string id) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
        _ => throw new ArgumentException($"Setting '{id}' must be a boolean.", id)
    };

    private static int ReadNonNegativeInt(JsonElement value, string id)
    {
        int parsed = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) => number,
            _ => throw new ArgumentException($"Setting '{id}' must be an integer number of seconds.", id)
        };
        if (parsed < 0)
            throw new ArgumentException($"Setting '{id}' must be >= 0.", id);
        return parsed;
    }
}
