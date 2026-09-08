using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.IntegrationTests.Infrastructure;

internal static class FusionCacheProbe
{
    public static string GetDataKey(IServiceProvider services, HttpContext http, string domain)
    {
        DomainHttpCacheOptions options = services.GetRequiredService<IRequestDomainCacheOptions>()
            .EnsureDomainOptions(http, domain);
        return services.GetRequiredService<IDomainKeyGenerator>().Generate(new(options, http));
    }

    public static async Task WaitForEntryAsync(
        IServiceProvider services, string key, bool present, bool distributedOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        IFusionCache cache = services.GetRequiredService<IFusionCacheProvider>().GetCache("default");
        FusionCacheEntryOptions options = new()
        {
            SkipMemoryCacheRead = distributedOnly,
            SkipMemoryCacheWrite = true,
            AllowStaleOnReadOnly = false,
            SkipDistributedCacheReadWhenStale = false,
            IsFailSafeEnabled = false
        };
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        bool? lastObserved = null;
        try
        {
            while (true)
            {
                // A read-only object probe accepts the provider envelope without running a
                // factory or populating L1 with a differently typed deserialized envelope.
                var value = await cache.TryGetAsync<object>(key, options, deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                lastObserved = value.HasValue;
                if (lastObserved == present)
                    return;
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            string layer = distributedOnly ? "Redis L2" : "the peer cache";
            throw new TimeoutException(
                $"Expected key '{key}' present={present} in {layer} within 10 seconds; last observed present={lastObserved}.", error);
        }
    }
}
