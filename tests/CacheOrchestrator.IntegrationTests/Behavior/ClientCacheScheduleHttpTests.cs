using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.IntegrationTests.Behavior;

/// <summary>
/// D — Client Cache Schedule over HTTP (TestServer + fake <see cref="TimeProvider"/>).
/// </summary>
public class ClientCacheScheduleHttpTests
{
    private static async Task<(
        HttpClient Client,
        WebApplication App,
        MutableTimeProvider Clock,
        ReloadableMemoryConfigurationSource Reload)> StartAsync(
        string domain,
        DateTimeOffset scheduleUtc,
        DateTimeOffset initialNow,
        int clientTtl = 3600,
        int clientTtlMin = 60,
        bool mustRevalidateNear = false)
    {
        Dictionary<string, string?> configValues = new()
        {
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EmitDiagnosticsHeaders"] = "true",
            [$"Cache:Domains:{domain}:Version"] = "v1",
            [$"Cache:Domains:{domain}:ClientCache:Cacheability"] = "Public",
            [$"Cache:Domains:{domain}:ClientCache:TtlSeconds"] = clientTtl.ToString(),
            [$"Cache:Domains:{domain}:ClientCache:TtlMinSeconds"] = clientTtlMin.ToString(),
            [$"Cache:Domains:{domain}:ClientCache:MustRevalidateNearUpdate"] = mustRevalidateNear ? "true" : "false",
            [$"Cache:Domains:{domain}:ClientCache:ScheduledUpdateUtc"] = scheduleUtc.ToString("O"),
            [$"Cache:Domains:{domain}:OutputCache:TtlSeconds"] = "1", // avoid OC hiding header changes across phase advances
            [$"Cache:Domains:{domain}:DataCache:TtlSeconds"] = "300",
        };

        var reloadSource = new ReloadableMemoryConfigurationSource(configValues);
        IConfigurationRoot config = new ConfigurationBuilder()
            .Add(reloadSource)
            .Build();

        var clock = new MutableTimeProvider(initialNow);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // Replace system clock before AddCacheOrchestrator (TryAddSingleton).
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddCacheOrchestratorAspNetCore(config);
        builder.Services.AddCacheOrchestratorFusionCache(config);

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseCacheOrchestrator();
        app.MapGet("/x", () => Results.Text("ok")).CacheOutputWithDomain(domain);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app.GetTestClient(), app, clock, reloadSource);
    }

    private static async Task<(string CacheOrchestratorHeader, string CacheControl)> GetHeadersAsync(HttpClient client)
    {
        HttpResponseMessage res = await client.GetAsync("/x", TestContext.Current.CancellationToken);
        res.IsSuccessStatusCode.Should().BeTrue();
        string xCache = res.Headers.TryGetValues("X-CacheOrchestrator", out IEnumerable<string>? xv)
            ? string.Join(",", xv)
            : string.Empty;
        string cc = res.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? cv)
            ? string.Join(",", cv)
            : string.Empty;
        return (xCache, cc);
    }

    [Fact]
    public async Task Schedule_Calm_UsesMaxAgeAndPhaseCalm()
    {
        string domain = "sched-calm-" + Guid.NewGuid().ToString("N");
        DateTimeOffset schedule = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddSeconds(-10_000);

        (HttpClient? client, WebApplication? app, MutableTimeProvider _, ReloadableMemoryConfigurationSource _) =
            await StartAsync(domain, schedule, now, clientTtl: 3600, clientTtlMin: 60);

        try
        {
            (string xCache, string cc) = await GetHeadersAsync(client);
            xCache.Should().Contain("phase=calm");
            cc.Should().Contain("max-age=3600");
            cc.Should().Contain("public");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Schedule_Approaching_RampsMaxAge_AndPhaseApproaching()
    {
        string domain = "sched-app-" + Guid.NewGuid().ToString("N");
        DateTimeOffset schedule = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        // Mid-ramp: secondsToSchedule = 1830 → max-age ~1830
        DateTimeOffset now = schedule.AddSeconds(-1830);

        (HttpClient? client, WebApplication? app, MutableTimeProvider _, ReloadableMemoryConfigurationSource _) =
            await StartAsync(domain, schedule, now, clientTtl: 3600, clientTtlMin: 60);

        try
        {
            (string xCache, string cc) = await GetHeadersAsync(client);
            xCache.Should().Contain("phase=approaching");
            cc.Should().MatchRegex("max-age=1[0-9]{3}"); // roughly mid-range
            // linear mid ≈ 1830
            int maxAge = ParseMaxAge(cc);
            maxAge.Should().BeCloseTo(1830, 10);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Schedule_Hold_UsesMinTtl_AndPhaseHold()
    {
        string domain = "sched-hold-" + Guid.NewGuid().ToString("N");
        DateTimeOffset schedule = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddMinutes(10);

        (HttpClient? client, WebApplication? app, MutableTimeProvider _, ReloadableMemoryConfigurationSource _) =
            await StartAsync(domain, schedule, now, clientTtl: 3600, clientTtlMin: 90);

        try
        {
            (string xCache, string cc) = await GetHeadersAsync(client);
            xCache.Should().Contain("phase=hold");
            cc.Should().Contain("max-age=90");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Schedule_AdvanceClock_TransitionsCalmToHold()
    {
        string domain = "sched-adv-" + Guid.NewGuid().ToString("N");
        DateTimeOffset schedule = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddSeconds(-10_000);

        (HttpClient? client, WebApplication? app, MutableTimeProvider clock, ReloadableMemoryConfigurationSource _) =
            await StartAsync(domain, schedule, now, clientTtl: 3600, clientTtlMin: 60);

        try
        {
            (string x1, string cc1) = await GetHeadersAsync(client);
            x1.Should().Contain("phase=calm");
            cc1.Should().Contain("max-age=3600");

            // Past schedule → Hold (OC TTL is 1s; advance wall clock used for headers, and wait OC expiry)
            clock.SetUtcNow(schedule.AddMinutes(1));
            await Task.Delay(1100, TestContext.Current.CancellationToken);

            (string x2, string cc2) = await GetHeadersAsync(client);
            x2.Should().Contain("phase=hold");
            cc2.Should().Contain("max-age=60");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Schedule_NearFloor_MustRevalidate_Appended()
    {
        string domain = "sched-mr-" + Guid.NewGuid().ToString("N");
        DateTimeOffset schedule = new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddSeconds(-60);

        (HttpClient? client, WebApplication? app, MutableTimeProvider _, ReloadableMemoryConfigurationSource _) =
            await StartAsync(domain, schedule, now, clientTtl: 3600, clientTtlMin: 60, mustRevalidateNear: true);

        try
        {
            (string xCache, string cc) = await GetHeadersAsync(client);
            xCache.Should().Contain("phase=approaching");
            cc.Should().Contain("max-age=60");
            cc.Should().Contain("must-revalidate");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Acceptance: Client Cache Schedule through one cutover and into the next calm window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typical snapshot domain (e.g. map tiles): client <c>max-age</c> is long in Calm, ramps down
    /// toward <c>ScheduledUpdateUtc</c> (Approaching), then sits at the min TTL in Hold.
    /// After the cutover, config sets <c>ScheduledUpdateUtc</c> to the <strong>next</strong> planned
    /// update (far in the future) so the domain returns to <strong>Calm</strong> with full client TTL.
    /// Schedule affects client <c>Cache-Control</c> and an enabled Edge fresh TTL — not server OC/FC TTLs.
    /// </para>
    /// <para>
    /// Script (fake clock + config reload; no real multi-hour wait):
    /// <code>
    /// GIVEN domain with schedule T0, client TTL 3600→60, must-revalidate near
    /// AND TestServer GET /tiles + MutableTimeProvider
    /// AND short OutputCache TTL so each phase regenerates response headers
    ///
    /// WHEN clock = T0 − 10000s  → GET  → phase=calm,        max-age=3600
    /// WHEN clock = T0 − 1830s   → GET  → phase=approaching, max-age≈1830
    /// WHEN clock = T0 − 60s     → GET  → phase=approaching, max-age=60, must-revalidate
    /// WHEN clock = T0 + 5min    → GET  → phase=hold,        max-age=60
    /// WHEN ScheduledUpdateUtc is set to the next update T1 (far ahead of current clock)
    ///                           → GET  → phase=calm,        max-age=3600  (next generation window)
    ///
    /// AND those max-age values come from Client Cache Schedule, not from server OC/FC TTL config
    /// </code>
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ClientCacheSchedule_FullCutoverLifecycle_EmitsPhasesAndMaxAgeOnHttpResponse()
    {
        string domain = "sched-life-" + Guid.NewGuid().ToString("N");
        // Cutover instant T0 (ScheduledUpdateUtc).
        DateTimeOffset t0 = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
        const int clientTtlMax = 3600;
        const int clientTtlMin = 60;

        // Start deep in Calm so the first response is unambiguous.
        (HttpClient? client, WebApplication? app, MutableTimeProvider clock, ReloadableMemoryConfigurationSource reload) =
            await StartAsync(
                domain,
                scheduleUtc: t0,
                initialNow: t0.AddSeconds(-10_000),
                clientTtl: clientTtlMax,
                clientTtlMin: clientTtlMin,
                mustRevalidateNear: true);

        try
        {
            // --- WHEN clock = T0 − 10000s → Calm: full client TTL ---
            (string xCalm, string ccCalm) = await GetHeadersAsync(client);
            xCalm.Should().Contain("phase=calm");
            ccCalm.Should().Contain($"max-age={clientTtlMax}");
            ccCalm.Should().Contain("public");
            ccCalm.Should().NotContain("must-revalidate");
            ParseMaxAge(ccCalm).Should().Be(clientTtlMax);

            // --- WHEN clock = T0 − 1830s → Approaching: mid-ramp max-age ---
            // Linear window is [min, max] seconds-to-schedule; t=1830 → max-age ≈ 1830.
            await AdvancePastOutputCacheAsync(clock, t0.AddSeconds(-1830));
            (string xApproach, string ccApproach) = await GetHeadersAsync(client);
            xApproach.Should().Contain("phase=approaching");
            ParseMaxAge(ccApproach).Should().BeCloseTo(1830, 10);
            ccApproach.Should().NotContain("must-revalidate",
                "must-revalidate only near the floor when ClientMustRevalidateNearUpdate is set");

            // --- WHEN clock = T0 − 60s → Approaching at floor + must-revalidate ---
            await AdvancePastOutputCacheAsync(clock, t0.AddSeconds(-clientTtlMin));
            (string xFloor, string ccFloor) = await GetHeadersAsync(client);
            xFloor.Should().Contain("phase=approaching");
            ParseMaxAge(ccFloor).Should().Be(clientTtlMin);
            ccFloor.Should().Contain("must-revalidate");

            // --- WHEN clock = T0 + 5min → Hold: stay at min client TTL ---
            await AdvancePastOutputCacheAsync(clock, t0.AddMinutes(5));
            (string xHold, string ccHold) = await GetHeadersAsync(client);
            xHold.Should().Contain("phase=hold");
            ParseMaxAge(ccHold).Should().Be(clientTtlMin);
            // Hold near-floor with must-revalidate still requests revalidation.
            ccHold.Should().Contain("must-revalidate");

            // --- Next update: set ScheduledUpdateUtc to T1 far ahead → back to Calm ---
            // Clock stays after T0; only config moves the schedule to the next planned cutover.
            DateTimeOffset t1 = clock.GetUtcNow().AddSeconds(10_000);
            reload.Provider.Should().NotBeNull();
            reload.Provider.SetAndReload(
                $"Cache:Domains:{domain}:ClientCache:ScheduledUpdateUtc",
                t1.ToString("O"));
            await WaitForScheduledUpdateAsync(app.Services, domain, t1);
            await AdvancePastOutputCacheAsync(clock, clock.GetUtcNow()); // expire OC only; clock unchanged

            (string xCalmAgain, string ccCalmAgain) = await GetHeadersAsync(client);
            xCalmAgain.Should().Contain("phase=calm",
                "next ScheduledUpdateUtc far ahead must return to Calm with full client TTL");
            ParseMaxAge(ccCalmAgain).Should().Be(clientTtlMax);
            ccCalmAgain.Should().NotContain("must-revalidate");

            // Sanity: server Fusion soft TTL in config is 300s — schedule must not have rewritten
            // client max-age to that value in any phase above (client max was 3600 / ramp / 60).
            ParseMaxAge(ccCalm).Should().NotBe(300);
            ParseMaxAge(ccHold).Should().NotBe(300);
            ParseMaxAge(ccCalmAgain).Should().NotBe(300);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Moves the fake clock and waits past the short Output Cache TTL so the next GET
    /// rebuilds <c>Cache-Control</c> / <c>X-CacheOrchestrator</c> from current schedule math.
    /// </summary>
    private static async Task AdvancePastOutputCacheAsync(MutableTimeProvider clock, DateTimeOffset newUtcNow)
    {
        clock.SetUtcNow(newUtcNow);
        // Domain is configured with OutputCacheTtlSeconds = 1 in StartAsync.
        await Task.Delay(1100, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Waits until domain options pick up the reloaded <see cref="DomainHttpCacheOptions.ScheduledUpdateUtc"/>.
    /// </summary>
    private static async Task WaitForScheduledUpdateAsync(
        IServiceProvider services,
        string domain,
        DateTimeOffset expected)
    {
        IRequestDomainCacheOptions domains = services.GetRequiredService<IRequestDomainCacheOptions>();
        IOptionsMonitor<CacheOrchestratorOptions> monitor =
            services.GetRequiredService<IOptionsMonitor<CacheOrchestratorOptions>>();

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            _ = monitor.CurrentValue;
            DomainHttpCacheOptions snap = domains.GetOrCreateDomainOptions(domain);
            if (snap.ScheduledUpdateUtc is DateTimeOffset actual
                && Math.Abs((actual - expected).TotalSeconds) < 1)
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        domains.GetOrCreateDomainOptions(domain).ScheduledUpdateUtc.Should().BeCloseTo(expected, TimeSpan.FromSeconds(1));
    }

    private static int ParseMaxAge(string cacheControl)
    {
        const string prefix = "max-age=";
        int i = cacheControl.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        i.Should().BeGreaterThanOrEqualTo(0);
        string rest = cacheControl[(i + prefix.Length)..];
        int end = rest.IndexOfAny([',', ' ']);
        string num = end < 0 ? rest : rest[..end];
        return int.Parse(num);
    }
}
