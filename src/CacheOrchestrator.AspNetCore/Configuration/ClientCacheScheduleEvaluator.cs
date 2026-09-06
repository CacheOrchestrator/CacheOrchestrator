namespace CacheOrchestrator.Configuration;

internal readonly record struct ClientCacheScheduleResult(
    int MaxAgeSeconds,
    ClientCacheSchedulePhase Phase);

internal static class ClientCacheScheduleEvaluator
{
    public static ClientCacheScheduleResult Evaluate(
        int maxAgeSeconds,
        int minAgeSeconds,
        DateTimeOffset? scheduledUpdateUtc,
        DateTimeOffset now)
    {
        int max = Math.Max(0, maxAgeSeconds);
        if (max == 0)
            return new ClientCacheScheduleResult(0, ClientCacheSchedulePhase.NotApplicable);

        int min = Math.Clamp(minAgeSeconds, 0, max);
        if (scheduledUpdateUtc is not { } schedule)
            return new ClientCacheScheduleResult(max, ClientCacheSchedulePhase.NotApplicable);

        if (now >= schedule)
            return new ClientCacheScheduleResult(min, ClientCacheSchedulePhase.Hold);

        double secondsToSchedule = (schedule - now).TotalSeconds;
        if (secondsToSchedule >= max)
            return new ClientCacheScheduleResult(max, ClientCacheSchedulePhase.Calm);

        double time = Math.Clamp(secondsToSchedule, min, max);
        int maxAge = (int)Math.Round(time);
        return new ClientCacheScheduleResult(
            Math.Clamp(maxAge, min, max),
            ClientCacheSchedulePhase.Approaching);
    }
}
