using CacheOrchestrator.Admin;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.HttpBus;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheOrchestrator.IntegrationTests.Cluster;

/// <summary>
/// Multi-host HTTP bus scenarios using real Kestrel loopback ports (not TestServer),
/// so peers can reach each other's <c>/cluster/apply</c> endpoints.
/// </summary>
public class ClusterBusMultiHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class HitCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
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

    private static async Task<(ClusterHost A, ClusterHost B)> StartPairAsync(
        string ns,
        string domain,
        string path,
        string? apiKey = "bus-key",
        bool adminEnabled = true)
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        string urlA = $"http://127.0.0.1:{portA}";
        string urlB = $"http://127.0.0.1:{portB}";

        ClusterHost a = await StartHostOnPortAsync(
            "node-a", ns, domain, path, portA,
            peers: [("node-a", urlA), ("node-b", urlB)],
            apiKey, adminEnabled).ConfigureAwait(false);

        ClusterHost b = await StartHostOnPortAsync(
            "node-b", ns, domain, path, portB,
            peers: [("node-a", urlA), ("node-b", urlB)],
            apiKey, adminEnabled).ConfigureAwait(false);

        return (a, b);
    }

    private static async Task<ClusterHost> StartHostOnPortAsync(
        string instanceId,
        string ns,
        string domain,
        string path,
        int port,
        IReadOnlyList<(string Id, string Url)> peers,
        string? apiKey,
        bool adminEnabled,
        string membership = "Static",
        Dictionary<string, string?>? extraConfig = null,
        Action<IServiceCollection, IConfiguration>? configureServices = null)
    {
        Dictionary<string, string?> configValues = new()
        {
            ["Cache:Namespace"] = ns,
            ["Cache:InstanceId"] = instanceId,
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EmitDiagnosticsHeaders"] = "true",
            ["Cache:Cluster:Bus:Enabled"] = "true",
            ["Cache:Cluster:Bus:Membership"] = membership,
            ["Cache:Cluster:Bus:PeerTimeoutMs"] = "5000",
            ["Cache:Cluster:Bus:MaxParallelism"] = "8",
            ["Cache:Cluster:Bus:DedupeWindowSeconds"] = "330",
            ["Cache:Admin:Enabled"] = adminEnabled ? "true" : "false",
            ["Cache:Admin:RoutePrefix"] = "/cache-admin/local",
            [$"Cache:Domains:{domain}:Version"] = "v1",
            [$"Cache:Domains:{domain}:OutputCache:TtlSeconds"] = "120",
            [$"Cache:Domains:{domain}:DataCache:TtlSeconds"] = "300",
            [$"Cache:Domains:{domain}:FusionCache:JitterSeconds"] = "0",
            [$"Cache:Domains:{domain}:ClientCache:Cacheability"] = "Public",
            [$"Cache:Domains:{domain}:ClientCache:TtlSeconds"] = "60",
            [$"Cache:Domains:{domain}:ClientCache:TtlMinSeconds"] = "60",
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            configValues["Cache:Cluster:Bus:ApiKey"] = apiKey;
            configValues["Cache:Admin:ApiKey"] = apiKey;
        }
        else
        {
            configValues["Cache:Cluster:Bus:AllowUnauthenticated"] = "true";
        }

        for (int i = 0; i < peers.Count; i++)
        {
            configValues[$"Cache:Cluster:Bus:Static:Instances:{i}:Id"] = peers[i].Id;
            configValues[$"Cache:Cluster:Bus:Static:Instances:{i}:Url"] = peers[i].Url;
        }

        if (extraConfig is not null)
        {
            foreach ((string k, string? v) in extraConfig)
                configValues[k] = v;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        // Host configuration must include Services:* for ServiceDiscovery (same IConfiguration DI).
        builder.Configuration.AddInMemoryCollection(configValues);
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration, o => o.AddHttpClusterBus(), enableMvcConvention: false);
        builder.Services.AddCacheOrchestratorFusionCache(builder.Configuration);
        builder.Services.AddSingleton<HitCounter>();
        configureServices?.Invoke(builder.Services, builder.Configuration);

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
            string value = await cache
                .GetOrSetAsync(http, domain, _ => Task.FromResult("payload-" + domain), http.RequestAborted)
                .ConfigureAwait(false);
            return Results.Text(value);
        }).CacheOutputWithDomain(domain);

        await app.StartAsync(Ct).ConfigureAwait(false);

        string baseUrl = $"http://127.0.0.1:{port}";
        HttpClient client = new() { BaseAddress = new Uri(baseUrl) };
        if (!string.IsNullOrEmpty(apiKey))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-CacheOrchestrator-Admin-Key", apiKey);

        return new ClusterHost
        {
            InstanceId = instanceId,
            BaseUrl = baseUrl,
            App = app,
            Client = client,
            Hits = hits
        };
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static StringContent CreateCommandContent(ClusterCommand command)
    {
        string json = JsonSerializer.Serialize(ClusterCommandEnvelopeV1.FromCommand(command), CommandJson);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StaticBus_InvalidateDomain_ForcesPeerMiss()
    {
        string ns = "it-bus-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "catalog";
        string path = "/api/item";

        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, domain, path);
        await using (a)
        await using (b)
        {
            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            a.Hits.Count.Should().Be(1);
            b.Hits.Count.Should().Be(1);

            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            a.Hits.Count.Should().Be(1);
            b.Hits.Count.Should().Be(1);

            ICacheOrchestratorInvalidator invA =
                a.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>();
            CacheInvalidationResult result =
                await invA.InvalidateDomainAsync(domain, Ct);
            result.Succeeded.Should().BeTrue();

            await Task.Delay(400, Ct);

            (await a.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync(path, Ct)).EnsureSuccessStatusCode();
            a.Hits.Count.Should().Be(2, "A local invalidate should force miss");
            b.Hits.Count.Should().Be(2, "B should receive InvalidateCommand and miss");
        }
    }

    [Fact]
    public async Task StaticBus_EntityInvalidate_PurgesPeerEntityTag()
    {
        string ns = "it-ent-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "product-detail";
        int portA = GetFreePort();
        int portB = GetFreePort();
        string urlA = $"http://127.0.0.1:{portA}";
        string urlB = $"http://127.0.0.1:{portB}";

        Dictionary<string, string?> BaseCfg(string instanceId) => new()
        {
            ["Cache:Namespace"] = ns,
            ["Cache:InstanceId"] = instanceId,
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:Cluster:Bus:Enabled"] = "true",
            ["Cache:Cluster:Bus:Membership"] = "Static",
            ["Cache:Cluster:Bus:ApiKey"] = "bus-key",
            ["Cache:Cluster:Bus:Static:Instances:0:Id"] = "node-a",
            ["Cache:Cluster:Bus:Static:Instances:0:Url"] = urlA,
            ["Cache:Cluster:Bus:Static:Instances:1:Id"] = "node-b",
            ["Cache:Cluster:Bus:Static:Instances:1:Url"] = urlB,
            [$"Cache:Domains:{domain}:Version"] = "v1",
            [$"Cache:Domains:{domain}:OutputCache:TtlSeconds"] = "60",
            [$"Cache:Domains:{domain}:DataCache:TtlSeconds"] = "120",
            [$"Cache:Domains:{domain}:FusionCache:JitterSeconds"] = "0",
        };

        async Task<ClusterHost> Build(string instanceId, int port)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Configuration.AddInMemoryCollection(BaseCfg(instanceId));
            builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
            builder.Logging.ClearProviders();
            builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration, o => o.AddHttpClusterBus(), enableMvcConvention: false);
            builder.Services.AddCacheOrchestratorFusionCache(builder.Configuration);
            builder.Services.AddSingleton<HitCounter>();
            WebApplication app = builder.Build();
            app.UseRouting();
            app.UseCacheOrchestrator();
            app.MapCacheOrchestratorHttpBus();
            HitCounter hits = app.Services.GetRequiredService<HitCounter>();
            app.MapGet("/api/products/{id}", async (HttpContext http, string id, IDomainDataCache cache, HitCounter h) =>
            {
                h.Increment();
                string? v = await cache.GetOrSetEntityAsync(
                    http, _ => Task.FromResult<string?>("p-" + id), http.RequestAborted);
                return Results.Text(v ?? string.Empty);
            }).CacheOutputWithDomain(domain, resourceRouteKey: "id", entityKind: "products");
            await app.StartAsync(Ct);
            HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-CacheOrchestrator-Admin-Key", "bus-key");
            return new ClusterHost
            {
                InstanceId = instanceId,
                BaseUrl = $"http://127.0.0.1:{port}",
                App = app,
                Client = client,
                Hits = hits
            };
        }

        await using ClusterHost a = await Build("node-a", portA);
        await using ClusterHost b = await Build("node-b", portB);

        (await a.Client.GetAsync("/api/products/42", Ct)).EnsureSuccessStatusCode();
        (await b.Client.GetAsync("/api/products/42", Ct)).EnsureSuccessStatusCode();
        a.Hits.Count.Should().Be(1);
        b.Hits.Count.Should().Be(1);

        await a.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>()
            .InvalidateEntityAsync(domain, "products", "42", Ct);
        await Task.Delay(400, Ct);

        (await a.Client.GetAsync("/api/products/42", Ct)).EnsureSuccessStatusCode();
        (await b.Client.GetAsync("/api/products/42", Ct)).EnsureSuccessStatusCode();
        a.Hits.Count.Should().Be(2);
        b.Hits.Count.Should().Be(2);
    }

    [Fact]
    public async Task NamespaceMismatch_IsRejectedOnReceive()
    {
        string nsB = "app-b-" + Guid.NewGuid().ToString("N")[..6];
        int portB = GetFreePort();
        string urlB = $"http://127.0.0.1:{portB}";

        await using ClusterHost b = await StartHostOnPortAsync(
            "node-b", nsB, "d", "/x", portB,
            peers: [("node-b", urlB)],
            apiKey: "k",
            adminEnabled: false);

        InvalidateCommand foreign = new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "other",
            Namespace = "other-ns",
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = CacheInvalidationKind.Domain,
            Scope = "d",
            Tags = ["domain:d"],
            Domain = "d"
        };

        using HttpRequestMessage req = new(HttpMethod.Post, urlB + "/cache-admin/local/cluster/apply");
        req.Headers.TryAddWithoutValidation("X-CacheOrchestrator-Admin-Key", "k");
        req.Content = CreateCommandContent(foreign);

        using HttpResponseMessage response = await new HttpClient().SendAsync(req, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SelfOrigin_IsIgnoredOnReceive()
    {
        int port = GetFreePort();
        string url = $"http://127.0.0.1:{port}";
        await using ClusterHost host = await StartHostOnPortAsync(
            "solo", "ns-solo", "d", "/x", port,
            peers: [("solo", url)],
            apiKey: "k",
            adminEnabled: false);

        InvalidateCommand selfCmd = new()
        {
            CommandId = Guid.NewGuid(),
            OriginInstanceId = "solo",
            Namespace = "ns-solo",
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = CacheInvalidationKind.Domain,
            Scope = "d",
            Tags = ["domain:d"],
            Domain = "d"
        };

        using HttpRequestMessage req = new(HttpMethod.Post, url + "/cache-admin/local/cluster/apply");
        req.Headers.TryAddWithoutValidation("X-CacheOrchestrator-Admin-Key", "k");
        req.Content = CreateCommandContent(selfCmd);

        using HttpResponseMessage response = await new HttpClient().SendAsync(req, Ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(Ct);
        json.Should().Contain("origin-is-self");
    }

    [Fact]
    public async Task DuplicateCommandId_SecondApplyIsNoOp()
    {
        int port = GetFreePort();
        string url = $"http://127.0.0.1:{port}";
        string domain = "dup-dom";
        await using ClusterHost host = await StartHostOnPortAsync(
            "node", "ns-dup", domain, "/api/x", port,
            peers: [("node", url)],
            apiKey: "k",
            adminEnabled: false);

        (await host.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        host.Hits.Count.Should().Be(1);

        var commandId = Guid.NewGuid();
        InvalidateCommand cmd = new()
        {
            CommandId = commandId,
            OriginInstanceId = "remote",
            Namespace = "ns-dup",
            TimestampUtc = DateTimeOffset.UtcNow,
            Kind = CacheInvalidationKind.Domain,
            Scope = domain,
            Tags = [$"domain:{domain}"],
            Domain = domain
        };

        async Task PostOnce()
        {
            using HttpRequestMessage req = new(HttpMethod.Post, url + "/cache-admin/local/cluster/apply");
            req.Headers.TryAddWithoutValidation("X-CacheOrchestrator-Admin-Key", "k");
            req.Content = CreateCommandContent(cmd);
            using HttpResponseMessage response = await new HttpClient().SendAsync(req, Ct);
            response.EnsureSuccessStatusCode();
        }

        await PostOnce();
        await PostOnce();

        (await host.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        host.Hits.Count.Should().Be(2);
    }

    [Fact]
    public async Task AdminDistributeTrue_VersionBump_AppliesOnPeer()
    {
        string ns = "it-ver-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "tiles";
        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, domain, "/api/t");
        await using (a)
        await using (b)
        {
            using StringContent body = new(
                """{"version":"cluster-v9","distribute":true}""",
                Encoding.UTF8,
                "application/json");
            HttpResponseMessage response =
                await a.Client.PostAsync($"/cache-admin/local/domains/{domain}/version", body, Ct);
            response.EnsureSuccessStatusCode();

            await Task.Delay(400, Ct);

            AdminDomainConfigDto? bDomain = await b.Client
                .GetFromJsonAsync<AdminDomainConfigDto>($"/cache-admin/local/domains/{domain}", Ct);
            bDomain.Should().NotBeNull();
            bDomain.Version.Should().Be("cluster-v9");
            bDomain.VersionIsRuntimeOverride.Should().BeTrue();
        }
    }

    [Fact]
    public async Task AdminDistributeTrue_VersionBump_QueuesEdgePurgeOnlyOnOrigin()
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        string urlA = $"http://127.0.0.1:{portA}";
        string urlB = $"http://127.0.0.1:{portB}";
        string ns = "it-edge-ver-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "tiles";
        (string Id, string Url)[] peers = [("node-a", urlA), ("node-b", urlB)];
        Dictionary<string, string?> edgeConfig = new()
        {
            ["Cache:EdgeInstances:edge:Provider"] = "Test",
            [$"Cache:Domains:{domain}:Edge:Enabled"] = "true",
            [$"Cache:Domains:{domain}:Edge:Instance"] = "edge"
        };
        var queueA = new RecordingEdgeQueue();
        var queueB = new RecordingEdgeQueue();

        await using ClusterHost a = await StartHostOnPortAsync(
            "node-a", ns, domain, "/api/t", portA, peers, "k", true,
            extraConfig: edgeConfig,
            configureServices: (services, configuration) => AddTestEdge(services, configuration, queueA));
        await using ClusterHost b = await StartHostOnPortAsync(
            "node-b", ns, domain, "/api/t", portB, peers, "k", true,
            extraConfig: edgeConfig,
            configureServices: (services, configuration) => AddTestEdge(services, configuration, queueB));

        using StringContent body = new(
            """{"version":"cluster-v9","distribute":true}""",
            Encoding.UTF8,
            "application/json");
        (await a.Client.PostAsync($"/cache-admin/local/domains/{domain}/version", body, Ct))
            .EnsureSuccessStatusCode();

        DateTimeOffset timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        AdminDomainConfigDto? peerDomain = null;
        while (DateTimeOffset.UtcNow < timeout)
        {
            peerDomain = await b.Client.GetFromJsonAsync<AdminDomainConfigDto>(
                $"/cache-admin/local/domains/{domain}",
                Ct);
            if (peerDomain?.Version == "cluster-v9")
                break;
            await Task.Delay(25, Ct);
        }

        peerDomain!.Version.Should().Be("cluster-v9");
        queueA.Jobs.Should().ContainSingle();
        queueB.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task AdminDistributeFalse_DoesNotPublishVersionToPeer()
    {
        string ns = "it-loc-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "tiles";
        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, domain, "/api/t");
        await using (a)
        await using (b)
        {
            using StringContent body = new(
                """{"version":"only-a","distribute":false}""",
                Encoding.UTF8,
                "application/json");
            (await a.Client.PostAsync($"/cache-admin/local/domains/{domain}/version", body, Ct))
                .EnsureSuccessStatusCode();

            await Task.Delay(300, Ct);

            AdminDomainConfigDto? aDomain = await a.Client
                .GetFromJsonAsync<AdminDomainConfigDto>($"/cache-admin/local/domains/{domain}", Ct);
            AdminDomainConfigDto? bDomain = await b.Client
                .GetFromJsonAsync<AdminDomainConfigDto>($"/cache-admin/local/domains/{domain}", Ct);

            aDomain!.Version.Should().Be("only-a");
            bDomain!.Version.Should().Be("v1");
            bDomain.VersionIsRuntimeOverride.Should().BeFalse();
        }
    }

    [Fact]
    public async Task AdminDistributeTrue_SettingsPatch_AppliesOnPeer()
    {
        string ns = "it-settings-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "reports";
        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, domain, "/api/r");
        await using (a)
        await using (b)
        {
            using StringContent body = new(
                """{"settings":{"outputCache.ttlSeconds":77},"distribute":true}""",
                Encoding.UTF8,
                "application/json");
            using HttpRequestMessage req = new(HttpMethod.Patch, $"/cache-admin/local/domains/{domain}/settings")
            {
                Content = body
            };
            (await a.Client.SendAsync(req, Ct)).EnsureSuccessStatusCode();
            await Task.Delay(400, Ct);

            AdminDomainConfigDto? bDomain = await b.Client
                .GetFromJsonAsync<AdminDomainConfigDto>($"/cache-admin/local/domains/{domain}", Ct);
            bDomain!.OutputCacheTtlSeconds.Should().Be(77);
        }
    }

    [Fact]
    public async Task AdminDistributeTrue_ScheduledUpdatePatch_DoesNotQueueEdgePurge()
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        string urlA = $"http://127.0.0.1:{portA}";
        string urlB = $"http://127.0.0.1:{portB}";
        string ns = "it-edge-schedule-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "reports";
        (string Id, string Url)[] peers = [("node-a", urlA), ("node-b", urlB)];
        Dictionary<string, string?> edgeConfig = new()
        {
            ["Cache:EdgeInstances:edge:Provider"] = "Test",
            [$"Cache:Domains:{domain}:Edge:Enabled"] = "true",
            [$"Cache:Domains:{domain}:Edge:Instance"] = "edge"
        };
        var queueA = new RecordingEdgeQueue();
        var queueB = new RecordingEdgeQueue();

        await using ClusterHost a = await StartHostOnPortAsync(
            "node-a", ns, domain, "/api/r", portA, peers, "k", true,
            extraConfig: edgeConfig,
            configureServices: (services, configuration) => AddTestEdge(services, configuration, queueA));
        await using ClusterHost b = await StartHostOnPortAsync(
            "node-b", ns, domain, "/api/r", portB, peers, "k", true,
            extraConfig: edgeConfig,
            configureServices: (services, configuration) => AddTestEdge(services, configuration, queueB));

        const string scheduledUpdateUtc = "2030-12-01T00:00:00Z";
        using StringContent body = new(
            $$"""{"settings":{"clientCache.scheduledUpdateUtc":"{{scheduledUpdateUtc}}"},"distribute":true}""",
            Encoding.UTF8,
            "application/json");
        using HttpRequestMessage request = new(HttpMethod.Patch, $"/cache-admin/local/domains/{domain}/settings")
        {
            Content = body
        };
        (await a.Client.SendAsync(request, Ct)).EnsureSuccessStatusCode();

        DateTimeOffset timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        AdminDomainConfigDto? peerDomain = null;
        while (DateTimeOffset.UtcNow < timeout)
        {
            peerDomain = await b.Client.GetFromJsonAsync<AdminDomainConfigDto>(
                $"/cache-admin/local/domains/{domain}",
                Ct);
            if (peerDomain?.ScheduledUpdateUtc == DateTimeOffset.Parse(scheduledUpdateUtc))
                break;
            await Task.Delay(25, Ct);
        }

        peerDomain!.ScheduledUpdateUtc.Should().Be(DateTimeOffset.Parse(scheduledUpdateUtc));
        queueA.Jobs.Should().BeEmpty();
        queueB.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task ClusterInfo_ReportsMembershipAndPeers()
    {
        string ns = "it-info-" + Guid.NewGuid().ToString("N")[..8];
        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, "d", "/x");
        await using (a)
        await using (b)
        {
            using HttpResponseMessage response =
                await a.Client.GetAsync("/cache-admin/local/cluster/info", Ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("busEnabled").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("membership").GetString().Should().Be("Static");
            doc.RootElement.GetProperty("peerCount").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("instanceId").GetString().Should().Be("node-a");
        }
    }

    [Fact]
    public async Task BusReceiveEndpoints_WorkWhenAdminDisabled()
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        string urlA = $"http://127.0.0.1:{portA}";
        string urlB = $"http://127.0.0.1:{portB}";
        string ns = "it-noadmin-" + Guid.NewGuid().ToString("N")[..6];
        string domain = "d";

        await using ClusterHost a = await StartHostOnPortAsync(
            "node-a", ns, domain, "/api/x", portA,
            peers: [("node-a", urlA), ("node-b", urlB)],
            apiKey: "k",
            adminEnabled: false);
        await using ClusterHost b = await StartHostOnPortAsync(
            "node-b", ns, domain, "/api/x", portB,
            peers: [("node-a", urlA), ("node-b", urlB)],
            apiKey: "k",
            adminEnabled: false);

        (await a.Client.GetAsync("/cache-admin/local/health", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await a.Client.GetAsync("/cache-admin/local/cluster/info", Ct)).EnsureSuccessStatusCode();

        (await a.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        (await b.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        a.Hits.Count.Should().Be(1);
        b.Hits.Count.Should().Be(1);

        await a.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>()
            .InvalidateDomainAsync(domain, Ct);
        await Task.Delay(400, Ct);

        (await b.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        b.Hits.Count.Should().Be(2);
    }

    [Fact]
    public async Task MissingApiKey_IsUnauthorizedWhenConfigured()
    {
        int port = GetFreePort();
        await using ClusterHost host = await StartHostOnPortAsync(
            "sec", "ns", "d", "/x", port,
            peers: [("sec", $"http://127.0.0.1:{port}")],
            apiKey: "secret",
            adminEnabled: false);

        using HttpClient naked = new() { BaseAddress = new Uri(host.BaseUrl) };
        HttpResponseMessage response = await naked.GetAsync("/cache-admin/local/cluster/info", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ThreeNodes_InvalidatePropagatesToAllPeers()
    {
        string ns = "it-3n-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "shared";
        int p1 = GetFreePort();
        int p2 = GetFreePort();
        int p3 = GetFreePort();
        string u1 = $"http://127.0.0.1:{p1}";
        string u2 = $"http://127.0.0.1:{p2}";
        string u3 = $"http://127.0.0.1:{p3}";
        (string Id, string Url)[] peers = [("n1", u1), ("n2", u2), ("n3", u3)];

        await using ClusterHost h1 = await StartHostOnPortAsync("n1", ns, domain, "/api/s", p1, peers, "k", false);
        await using ClusterHost h2 = await StartHostOnPortAsync("n2", ns, domain, "/api/s", p2, peers, "k", false);
        await using ClusterHost h3 = await StartHostOnPortAsync("n3", ns, domain, "/api/s", p3, peers, "k", false);

        foreach (ClusterHost h in new[] { h1, h2, h3 })
            (await h.Client.GetAsync("/api/s", Ct)).EnsureSuccessStatusCode();

        h1.Hits.Count.Should().Be(1);
        h2.Hits.Count.Should().Be(1);
        h3.Hits.Count.Should().Be(1);

        await h2.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>()
            .InvalidateDomainAsync(domain, Ct);
        await Task.Delay(500, Ct);

        foreach (ClusterHost h in new[] { h1, h2, h3 })
            (await h.Client.GetAsync("/api/s", Ct)).EnsureSuccessStatusCode();

        h1.Hits.Count.Should().Be(2);
        h2.Hits.Count.Should().Be(2);
        h3.Hits.Count.Should().Be(2);
    }

    [Fact]
    public async Task ServiceDiscoveryMembership_ResolvesPeersFromConfiguration()
    {
        string serviceName = "svc-" + Guid.NewGuid().ToString("N")[..6];
        int portA = GetFreePort();
        int portB = GetFreePort();
        string ns = "it-sd-" + Guid.NewGuid().ToString("N")[..6];
        string domain = "sd-dom";

        Dictionary<string, string?> sdExtra = new()
        {
            [$"Services:{serviceName}:http:0"] = $"127.0.0.1:{portA}",
            [$"Services:{serviceName}:http:1"] = $"127.0.0.1:{portB}",
            ["Cache:Cluster:Bus:Membership"] = "ServiceDiscovery",
            ["Cache:Cluster:Bus:ServiceDiscovery:ServiceName"] = serviceName,
            ["Cache:Cluster:Bus:ServiceDiscovery:DefaultScheme"] = "http",
            ["Cache:Cluster:Bus:ServiceDiscovery:CacheSeconds"] = "1",
        };

        await using ClusterHost a = await StartHostOnPortAsync(
            "node-a", ns, domain, "/api/sd", portA,
            peers: [],
            apiKey: "k",
            adminEnabled: false,
            membership: "ServiceDiscovery",
            extraConfig: sdExtra);

        await using ClusterHost b = await StartHostOnPortAsync(
            "node-b", ns, domain, "/api/sd", portB,
            peers: [],
            apiKey: "k",
            adminEnabled: false,
            membership: "ServiceDiscovery",
            extraConfig: sdExtra);

        IClusterMembership membership = a.App.Services.GetRequiredService<IClusterMembership>();
        membership.Kind.Should().Be("ServiceDiscovery");
        IReadOnlyList<ClusterPeer> peers = await membership.GetPeersAsync(Ct);
        peers.Should().HaveCount(2);

        (await a.Client.GetAsync("/api/sd", Ct)).EnsureSuccessStatusCode();
        (await b.Client.GetAsync("/api/sd", Ct)).EnsureSuccessStatusCode();
        a.Hits.Count.Should().Be(1);
        b.Hits.Count.Should().Be(1);

        await a.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>()
            .InvalidateDomainAsync(domain, Ct);
        await Task.Delay(500, Ct);

        (await a.Client.GetAsync("/api/sd", Ct)).EnsureSuccessStatusCode();
        (await b.Client.GetAsync("/api/sd", Ct)).EnsureSuccessStatusCode();
        a.Hits.Count.Should().Be(2);
        b.Hits.Count.Should().Be(2);
    }

    [Fact]
    public async Task ProgrammaticInvalidate_PublishesEvenWhenAdminDisabled()
    {
        string ns = "it-prog-" + Guid.NewGuid().ToString("N")[..6];
        string domain = "p";
        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, domain, "/api/p", adminEnabled: false);
        await using (a)
        await using (b)
        {
            (await a.Client.GetAsync("/api/p", Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync("/api/p", Ct)).EnsureSuccessStatusCode();

            await a.App.Services.GetRequiredService<ICacheOrchestratorInvalidator>()
                .InvalidateDomainAsync(domain, Ct);
            await Task.Delay(400, Ct);

            (await b.Client.GetAsync("/api/p", Ct)).EnsureSuccessStatusCode();
            b.Hits.Count.Should().Be(2);
        }
    }

    [Fact]
    public async Task AdminInvalidate_WithDistributeTrue_PurgesPeer()
    {
        string ns = "it-adm-" + Guid.NewGuid().ToString("N")[..6];
        string domain = "adm";
        (ClusterHost a, ClusterHost b) = await StartPairAsync(ns, domain, "/api/a");
        await using (a)
        await using (b)
        {
            (await a.Client.GetAsync("/api/a", Ct)).EnsureSuccessStatusCode();
            (await b.Client.GetAsync("/api/a", Ct)).EnsureSuccessStatusCode();

            using StringContent body = new(
                """{"scope":"domain","domain":"adm","distribute":true}""",
                Encoding.UTF8,
                "application/json");
            (await a.Client.PostAsync("/cache-admin/local/invalidate", body, Ct)).EnsureSuccessStatusCode();
            await Task.Delay(400, Ct);

            (await b.Client.GetAsync("/api/a", Ct)).EnsureSuccessStatusCode();
            b.Hits.Count.Should().Be(2);
        }
    }

    [Fact]
    public async Task UnreachablePeer_DoesNotFlipLocalInvalidationSucceeded()
    {
        string ns = "it-dead-" + Guid.NewGuid().ToString("N")[..8];
        string domain = "dead-peer";
        int port = GetFreePort();
        int deadPort = GetFreePort();
        string url = $"http://127.0.0.1:{port}";
        string dead = $"http://127.0.0.1:{deadPort}";

        await using ClusterHost host = await StartHostOnPortAsync(
            "node-a",
            ns,
            domain,
            "/api/x",
            port,
            peers: [("node-a", url), ("node-dead", dead)],
            apiKey: "k",
            adminEnabled: false,
            extraConfig: new Dictionary<string, string?>
            {
                ["Cache:Cluster:Bus:PeerTimeoutMs"] = "400",
            });

        (await host.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        host.Hits.Count.Should().Be(1);

        CacheInvalidationResult result = await host.App.Services
            .GetRequiredService<ICacheOrchestratorInvalidator>()
            .InvalidateDomainAsync(domain, Ct);

        result.Succeeded.Should().BeTrue(
            "cluster publish failure must not flip local Fusion/Output Succeeded");
        result.DataCacheSucceeded.Should().BeTrue();
        result.OutputSucceeded.Should().BeTrue();
        result.ClusterPublish.Should().NotBeNull();
        result.ClusterPublish.AllSucceeded.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();

        (await host.Client.GetAsync("/api/x", Ct)).EnsureSuccessStatusCode();
        host.Hits.Count.Should().Be(2, "local Output Cache must still be evicted");
    }

    private static void AddTestEdge(
        IServiceCollection services,
        IConfiguration configuration,
        RecordingEdgeQueue queue)
    {
        var provider = new TestEdgeProvider();
        services.AddSingleton<IEdgeResponseProvider>(provider);
        services.AddSingleton<IEdgeInvalidationProvider>(provider);
        services.AddSingleton<IEdgeInvalidationQueue>(queue);
        services.AddCacheOrchestratorEdge(configuration);
    }

    private sealed class RecordingEdgeQueue : IEdgeInvalidationQueue
    {
        public ConcurrentQueue<EdgeInvalidationJob> Jobs { get; } = new();

        public ValueTask EnqueueAsync(EdgeInvalidationJob job, CancellationToken cancellationToken)
        {
            Jobs.Enqueue(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestEdgeProvider : IEdgeResponseProvider, IEdgeInvalidationProvider
    {
        public string Name => "Test";

        public EdgeProviderCapabilities Capabilities { get; } = new()
        {
            SupportsTagInvalidation = true,
            MaxResponseTagBytes = 16 * 1024,
            MaxInvalidationBatchSize = 100,
            SupportsStaleWhileRevalidate = true,
            SupportsStaleIfError = true
        };

        public void ApplyResponseMetadata(HttpResponse response, EdgeResponseMetadata metadata)
        {
        }

        public EdgeInvalidationTarget CaptureTarget(string instanceName, IConfigurationSection instanceConfiguration) =>
            new(instanceName, Name, instanceName, new Dictionary<string, string>());

        public ValueTask<EdgeInvalidationResult> InvalidateAsync(
            EdgeInvalidationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(EdgeInvalidationResult.Success);
    }
}
