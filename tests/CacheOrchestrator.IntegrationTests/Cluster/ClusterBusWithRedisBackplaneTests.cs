using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.HttpBus;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;
using CacheOrchestrator.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace CacheOrchestrator.IntegrationTests.Cluster;

/// <summary>
/// Proves Redis Fusion L2 + backplane and HTTP cluster bus can run together:
/// invalidate on origin clears peers via backplane <em>and</em> bus (double apply is harmless).
/// Requires Docker (shared <see cref="RedisFixture"/>).
/// </summary>
[Collection("Redis")]
public class ClusterBusWithRedisBackplaneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RedisFixture _redis;

    public ClusterBusWithRedisBackplaneTests(RedisFixture redis)
    {
        _redis = redis;
    }

    private sealed class HitCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public string DataKey { get; set; } = "";
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class ClusterHost : IAsyncDisposable
    {
        public required string InstanceId { get; init; }
        public required string BaseUrl { get; init; }
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required HitCounter Hits { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Ct).ConfigureAwait(false);
            await App.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<(ClusterHost A, ClusterHost B)> StartRedisBusPairAsync(
        string ns,
        string domain,
        string path,
        bool adminEnabled = true)
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        string urlA = $"http://127.0.0.1:{portA}";
        string urlB = $"http://127.0.0.1:{portB}";
        (string Id, string Url)[] peers = [("node-a", urlA), ("node-b", urlB)];

        ClusterHost a = await StartHostAsync(
            "node-a", ns, domain, path, portA, peers, adminEnabled).ConfigureAwait(false);
        ClusterHost b = await StartHostAsync(
            "node-b", ns, domain, path, portB, peers, adminEnabled).ConfigureAwait(false);
        return (a, b);
    }

    private async Task<ClusterHost> StartHostAsync(
        string instanceId,
        string ns,
        string domain,
        string path,
        int port,
        IReadOnlyList<(string Id, string Url)> peers,
        bool adminEnabled)
    {
        Dictionary<string, string?> configValues = new()
        {
            ["Cache:Namespace"] = ns,
            ["Cache:InstanceId"] = instanceId,
            // OC stays InMemory so bus still matters for HTTP response layer on peers;
            // FC is Redis L2 + backplane (same topology as production mixed/full Redis FC).
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "Redis",
            ["Cache:Redis:Configuration"] = _redis.ConnectionString,
            ["Cache:EmitDiagnosticsHeaders"] = "true",
            ["Cache:Cluster:Bus:Enabled"] = "true",
            ["Cache:Cluster:Bus:Membership"] = "Static",
            ["Cache:Cluster:Bus:PeerTimeoutMs"] = "5000",
            ["Cache:Cluster:Bus:MaxParallelism"] = "8",
            ["Cache:Cluster:Bus:DedupeWindowSeconds"] = "330",
            ["Cache:Cluster:Bus:ApiKey"] = "bus-redis-key",
            ["Cache:Admin:Enabled"] = adminEnabled ? "true" : "false",
            ["Cache:Admin:ApiKey"] = "bus-redis-key",
            ["Cache:Admin:RoutePrefix"] = "/cache-admin/local",
            [$"Cache:Domains:{domain}:Version"] = "v1",
            [$"Cache:Domains:{domain}:OutputCache:TtlSeconds"] = "120",
            [$"Cache:Domains:{domain}:DataCache:TtlSeconds"] = "300",
            [$"Cache:Domains:{domain}:FusionCache:JitterSeconds"] = "0",
            [$"Cache:Domains:{domain}:ClientCache:Cacheability"] = "Public",
            [$"Cache:Domains:{domain}:ClientCache:TtlSeconds"] = "60",
            [$"Cache:Domains:{domain}:ClientCache:TtlMinSeconds"] = "60",
        };

        for (int i = 0; i < peers.Count; i++)
        {
            configValues[$"Cache:Cluster:Bus:Static:Instances:{i}:Id"] = peers[i].Id;
            configValues[$"Cache:Cluster:Bus:Static:Instances:{i}:Url"] = peers[i].Url;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(configValues);
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddCacheOrchestratorAspNetCore(
            builder.Configuration,
            o =>
            {
                o.AddRedisBackend();
                builder.Services.AddCacheOrchestratorFusionCache(builder.Configuration);
                o.AddHttpClusterBus();
            },
            enableMvcConvention: false);
        builder.Services.AddSingleton<HitCounter>();

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseCacheOrchestrator();
        app.MapCacheOrchestratorHttpBus();
        if (adminEnabled)
            app.MapCacheOrchestratorAdmin();

        HitCounter hits = app.Services.GetRequiredService<HitCounter>();
        app.MapGet(path, async (HttpContext http, IDomainDataCache cache, HitCounter h) =>
        {
            h.Increment();
            h.DataKey = FusionCacheProbe.GetDataKey(http.RequestServices, http, domain);
            string value = await cache
                .GetOrSetAsync(http, domain, _ => Task.FromResult("payload-" + domain), http.RequestAborted)
                .ConfigureAwait(false);
            return Results.Text(value);
        }).CacheOutputWithDomain(domain);

        await app.StartAsync(Ct).ConfigureAwait(false);

        string baseUrl = $"http://127.0.0.1:{port}";
        HttpClient client = new() { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-CacheOrchestrator-Admin-Key", "bus-redis-key");

        return new ClusterHost
        {
            InstanceId = instanceId,
            BaseUrl = baseUrl,
            App = app,
            Client = client,
            Hits = hits
        };
    }

    /// <summary>
    /// Invalidate on A: Redis removes L2 + backplane notifies B, and HTTP bus also delivers
    /// InvalidateCommand to B. Peer B must miss exactly once (not thrash / not stay stale).
    /// </summary>
    [Fact]
    public async Task InvalidateOnA_WithRedisBackplaneAndBus_ForcesSingleMissOnB()
    {
        string ns = "it-rb-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "catalog";
        string path = "/api/item";

        (ClusterHost a, ClusterHost b) = await StartRedisBusPairAsync(ns, domain, path, adminEnabled: false);
        await using (a)
        await using (b)
        {
            // Wait for each warm value to reach L2 before testing invalidation.
            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            await FusionCacheProbe.WaitForEntryAsync(a.App.Services, a.Hits.DataKey, present: true, distributedOnly: true);
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            await FusionCacheProbe.WaitForEntryAsync(b.App.Services, b.Hits.DataKey, present: true, distributedOnly: true);

            int hitsAAfterWarm = a.Hits.Count;
            int hitsBAfterWarm = b.Hits.Count;
            hitsAAfterWarm.Should().BeGreaterThanOrEqualTo(1);
            hitsBAfterWarm.Should().BeGreaterThanOrEqualTo(1);

            // Stable hits (no factory)
            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            a.Hits.Count.Should().Be(hitsAAfterWarm);
            b.Hits.Count.Should().Be(hitsBAfterWarm);

            CacheInvalidationResult result = await a.App.Services
                .GetRequiredService<ICacheOrchestratorInvalidator>()
                .InvalidateDomainAsync(domain, Ct);
            result.Succeeded.Should().BeTrue();

            // HttpBus has acknowledged peer execution; also observe native cache invalidation.
            await FusionCacheProbe.WaitForEntryAsync(a.App.Services, a.Hits.DataKey, present: false);
            await FusionCacheProbe.WaitForEntryAsync(b.App.Services, b.Hits.DataKey, present: false);

            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();

            a.Hits.Count.Should().Be(hitsAAfterWarm + 1, "origin should miss once after local invalidate");
            b.Hits.Count.Should().Be(
                hitsBAfterWarm + 1,
                "peer should miss once after backplane and/or bus (double apply must not double-factory)");

            // Second request still hit — no repeated thrashing from double invalidation
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            b.Hits.Count.Should().Be(hitsBAfterWarm + 1);
        }
    }

    /// <summary>
    /// Admin distribute:true on A with Redis+Bus: peer B applies Version overlay (bus path).
    /// Confirms Admin distribute works when Redis backend is also registered.
    /// </summary>
    [Fact]
    public async Task AdminDistributeVersion_WithRedisAndBus_AppliesOnPeer()
    {
        string ns = "it-rv-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "tiles";

        (ClusterHost a, ClusterHost b) = await StartRedisBusPairAsync(ns, domain, "/api/t", adminEnabled: true);
        await using (a)
        await using (b)
        {
            using StringContent body = new(
                """{"version":"redis-bus-v1","distribute":true}""",
                Encoding.UTF8,
                "application/json");
            HttpResponseMessage response =
                await a.Client.PostAsync($"/cache-admin/local/domains/{domain}/version", body, Ct);
            response.EnsureSuccessStatusCode();

            // Successful distributed settings responses acknowledge the peer mutation.

            Admin.AdminDomainConfigDto? bDomain = await b.Client
                .GetFromJsonAsync<Admin.AdminDomainConfigDto>($"/cache-admin/local/domains/{domain}", Ct);
            bDomain.Should().NotBeNull();
            bDomain.Version.Should().Be("redis-bus-v1");
            bDomain.VersionIsRuntimeOverride.Should().BeTrue();
        }
    }

    /// <summary>
    /// Bus alone would clear peer OC InMemory; with Redis FC, invalidate still works for Fusion.
    /// After invalidate, both hosts rebuild Fusion value successfully (no hang / no exception path).
    /// </summary>
    [Fact]
    public async Task Invalidate_WithRedisAndBus_BothHostsRecoverCleanly()
    {
        string ns = "it-rr-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "reports";
        string path = "/api/r";

        (ClusterHost a, ClusterHost b) = await StartRedisBusPairAsync(ns, domain, path, adminEnabled: false);
        await using (a)
        await using (b)
        {
            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            await FusionCacheProbe.WaitForEntryAsync(a.App.Services, a.Hits.DataKey, present: true, distributedOnly: true);
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            await FusionCacheProbe.WaitForEntryAsync(b.App.Services, b.Hits.DataKey, present: true, distributedOnly: true);

            await a.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>()
                .InvalidateDomainAsync(domain, Ct);
            await FusionCacheProbe.WaitForEntryAsync(a.App.Services, a.Hits.DataKey, present: false);
            await FusionCacheProbe.WaitForEntryAsync(b.App.Services, b.Hits.DataKey, present: false);

            HttpResponseMessage ra = await a.Client.GetAsync(path, Ct);
            HttpResponseMessage rb = await b.Client.GetAsync(path, Ct);
            ra.EnsureSuccessStatusCode();
            rb.EnsureSuccessStatusCode();

            string ta = await ra.Content.ReadAsStringAsync(Ct);
            string tb = await rb.Content.ReadAsStringAsync(Ct);
            ta.Should().Be("payload-" + domain);
            tb.Should().Be("payload-" + domain);
        }
    }
}
