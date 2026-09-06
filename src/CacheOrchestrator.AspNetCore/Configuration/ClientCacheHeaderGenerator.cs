using System.Globalization;

namespace CacheOrchestrator.Configuration;

/// <summary>Builds Client Cache-Control headers from ASP.NET Core domain policy.</summary>
public static class ClientCacheHeaderGenerator
{
    /// <summary>Generated Client Cache header and schedule state.</summary>
    public readonly record struct Result(
        string Header,
        int MaxAgeSeconds,
        ClientCacheSchedulePhase Phase);

    /// <summary>Builds Cache-Control for the supplied policy and UTC time.</summary>
    public static Result Build(
        DomainHttpCacheOptions config,
        DateTimeOffset now,
        ClientCacheability? cacheabilityOverride = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        ClientCacheability cacheability = cacheabilityOverride ?? config.ClientCacheability;
        if (cacheability == ClientCacheability.NoStore)
            return new Result("no-store", 0, ClientCacheSchedulePhase.NotApplicable);

        ClientCacheScheduleResult schedule = ClientCacheScheduleEvaluator.Evaluate(
            config.ClientTtlSeconds,
            config.ClientTtlMinSeconds,
            config.ScheduledUpdateUtc,
            now);
        bool mustRevalidate = config.ClientMustRevalidateNearUpdate
            && schedule.Phase is ClientCacheSchedulePhase.Hold
                or ClientCacheSchedulePhase.Approaching
            && schedule.MaxAgeSeconds <= Math.Clamp(
                config.ClientTtlMinSeconds,
                0,
                Math.Max(0, config.ClientTtlSeconds));

        return Finish(
            cacheability,
            schedule.MaxAgeSeconds,
            schedule.Phase,
            mustRevalidate);
    }

    private static Result Finish(
        ClientCacheability cacheability,
        int maxAge,
        ClientCacheSchedulePhase phase,
        bool mustRevalidate)
    {
        string directive = cacheability == ClientCacheability.Private ? "private" : "public";
        string maxAgeText = maxAge.ToString(CultureInfo.InvariantCulture);
        string header = mustRevalidate
            ? $"{directive}, max-age={maxAgeText}, must-revalidate"
            : $"{directive}, max-age={maxAgeText}";
        return new Result(header, maxAge, phase);
    }
}
