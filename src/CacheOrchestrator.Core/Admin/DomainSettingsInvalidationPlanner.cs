using System.Text.Json;

namespace CacheOrchestrator.Admin;

[Flags]
internal enum DomainSettingsInvalidationTargets
{
    None = 0,
    DataCache = 1,
    OutputCache = 2,
    Edge = 4
}

internal static class DomainSettingsInvalidationPlanner
{
    private static readonly HashSet<string> RequiredEdgeSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "authBypassMode",
        "treatAuthorizationAsAuthSignal",
        "varyByAccept",
        "varyByAcceptLanguage",
        "varyByHeaders"
    };

    private static readonly HashSet<string> ImmediateDataDecreaseSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "dataCache.ttlSeconds",
        "fusionCache.hardTtlSeconds",
        "fusionCache.failSafeSeconds",
        "fusionCache.jitterSeconds"
    };

    public static DomainSettingsInvalidationTargets Plan(
        IReadOnlyCollection<string> settingIds,
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after,
        bool applyImmediately)
    {
        DomainSettingsInvalidationTargets targets = DomainSettingsInvalidationTargets.None;
        foreach (string id in settingIds)
        {
            if (!Changed(id, before, after))
                continue;

            if (RequiredEdgeSettings.Contains(id))
                targets |= DomainSettingsInvalidationTargets.Edge;

            if (id.Equals("clientCache.cacheability", StringComparison.OrdinalIgnoreCase)
                && IsPublic(before, id)
                && !IsPublic(after, id))
            {
                targets |= DomainSettingsInvalidationTargets.Edge;
            }

            if (id.Equals("clientCache.forcePrivateWhenAuthenticated", StringComparison.OrdinalIgnoreCase)
                && IsFalse(before, id)
                && IsTrue(after, id))
            {
                targets |= DomainSettingsInvalidationTargets.Edge;
            }

            if (id.Equals("emitResponseVary", StringComparison.OrdinalIgnoreCase)
                && IsFalse(before, id)
                && IsTrue(after, id))
            {
                targets |= DomainSettingsInvalidationTargets.Edge;
            }

            if (!applyImmediately)
                continue;

            if (id.Equals("outputCache.enabled", StringComparison.OrdinalIgnoreCase) && IsFalse(after, id))
            {
                targets |= DomainSettingsInvalidationTargets.OutputCache;
            }
            else if (id.Equals("dataCache.enabled", StringComparison.OrdinalIgnoreCase) && IsFalse(after, id))
            {
                targets |= DomainSettingsInvalidationTargets.DataCache;
            }
            else if (id.Equals("outputCache.ttlSeconds", StringComparison.OrdinalIgnoreCase)
                     && Decreased(id, before, after))
            {
                targets |= DomainSettingsInvalidationTargets.OutputCache;
            }
            else if (ImmediateDataDecreaseSettings.Contains(id) && Decreased(id, before, after))
            {
                targets |= DomainSettingsInvalidationTargets.DataCache;
            }
            else if (id.Equals("outputCache.eTagMode", StringComparison.OrdinalIgnoreCase))
            {
                targets |= DomainSettingsInvalidationTargets.OutputCache | DomainSettingsInvalidationTargets.Edge;
            }
            else if (id.Equals("clientCache.ttlMinSeconds", StringComparison.OrdinalIgnoreCase)
                     && Decreased(id, before, after))
            {
                targets |= DomainSettingsInvalidationTargets.Edge;
            }
            else if (id.Equals("clientCache.ttlSeconds", StringComparison.OrdinalIgnoreCase)
                     && Int(before, id) == 0
                     && Int(after, id) > 0)
            {
                targets |= DomainSettingsInvalidationTargets.Edge;
            }
            else if (id.Equals("clientCache.scheduledUpdateUtc", StringComparison.OrdinalIgnoreCase)
                     && MovedEarlier(id, before, after))
            {
                targets |= DomainSettingsInvalidationTargets.Edge;
            }
        }

        return targets;
    }

    private static bool Changed(
        string id,
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after) =>
        before.TryGetValue(id, out JsonElement oldValue)
        && after.TryGetValue(id, out JsonElement newValue)
        && !string.Equals(oldValue.GetRawText(), newValue.GetRawText(), StringComparison.Ordinal);

    private static bool Decreased(
        string id,
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after) =>
        Int(after, id) < Int(before, id);

    private static int Int(IReadOnlyDictionary<string, JsonElement> values, string id) =>
        values.TryGetValue(id, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : int.MinValue;

    private static bool IsTrue(IReadOnlyDictionary<string, JsonElement> values, string id) =>
        values.TryGetValue(id, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static bool IsFalse(IReadOnlyDictionary<string, JsonElement> values, string id) =>
        values.TryGetValue(id, out JsonElement value) && value.ValueKind == JsonValueKind.False;

    private static bool IsPublic(IReadOnlyDictionary<string, JsonElement> values, string id) =>
        values.TryGetValue(id, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Equals("Public", StringComparison.OrdinalIgnoreCase) == true;

    private static bool MovedEarlier(
        string id,
        IReadOnlyDictionary<string, JsonElement> before,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        DateTimeOffset? oldValue = Date(before, id);
        DateTimeOffset? newValue = Date(after, id);
        return newValue is not null && (oldValue is null || newValue < oldValue);
    }

    private static DateTimeOffset? Date(IReadOnlyDictionary<string, JsonElement> values, string id) =>
        values.TryGetValue(id, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset result)
            ? result
            : null;
}
