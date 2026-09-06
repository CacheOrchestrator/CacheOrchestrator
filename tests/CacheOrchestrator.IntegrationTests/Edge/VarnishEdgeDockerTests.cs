using CacheOrchestrator.Admin;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Varnish;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using CacheOrchestrator.OutputCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CacheOrchestrator.IntegrationTests.Edge;

[Collection("Varnish")]
public sealed class VarnishEdgeDockerTests(VarnishFixture varnish)
{
    [Fact]
    public async Task ClientSchedule_TransitionsCalmApproachingHold_VersionPurgeHold_ThenRescheduleCalm()
    {
        int originPort = GetFreePort();
        await using KestrelVarnishProxy proxy = await KestrelVarnishProxy.StartAsync(
            originPort,
            TestContext.Current.CancellationToken);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{originPort}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "varnish-ccs",
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EdgeQueue:FlushIntervalSeconds"] = "0",
            ["Cache:EdgeInstances:edge:Provider"] = "Varnish",
            ["Cache:EdgeInstances:edge:Namespace"] = "varnish-ccs",
            ["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] =
                new Uri(proxy.Address, "/cache-orchestrator/purge").AbsoluteUri,
            ["Cache:EdgeInstances:edge:Varnish:ApiKey"] = "integration-secret",
            ["Cache:Domains:catalog:OutputCache:Enabled"] = "false",
            ["Cache:Domains:catalog:ClientCache:Cacheability"] = "Public",
            ["Cache:Domains:catalog:ClientCache:TtlSeconds"] = "10",
            ["Cache:Domains:catalog:ClientCache:TtlMinSeconds"] = "2",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:TtlSeconds"] = "4",
            ["Cache:Domains:catalog:Edge:StaleWhileRevalidateSeconds"] = "0"
        });
        builder.Services.AddCacheOrchestrator(builder.Configuration);
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddVarnish());
        var calls = new OriginCounter();
        builder.Services.AddSingleton(calls);

        await using WebApplication app = builder.Build();
        app.UseCacheOrchestrator();
        app.MapGet("/ccs-flow", (OriginCounter counter) => Results.Text(counter.Next().ToString()))
            .CacheOutputWithDomain("catalog");
        await app.StartAsync(TestContext.Current.CancellationToken);
        ICacheOrchestratorManagement management = app.Services.GetRequiredService<ICacheOrchestratorManagement>();
        DateTimeOffset firstUpdate = DateTimeOffset.UtcNow.AddSeconds(7);
        await SetScheduledUpdateAsync(management, firstUpdate);

        using var client = new HttpClient { BaseAddress = proxy.Address };
        using HttpResponseMessage calm = await client.GetAsync("/ccs-flow", TestContext.Current.CancellationToken);
        calm.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; fwd=uri-miss");
        GetOriginEdgeTtl(calm).Should().Be(4);
        calm.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("phase=approaching");
        (await calm.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("1");

        using HttpResponseMessage calmHit = await client.GetAsync("/ccs-flow", TestContext.Current.CancellationToken);
        calmHit.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; hit");
        (await calmHit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("1");

        using HttpResponseMessage approaching = await WaitForMissAsync(client, "/ccs-flow", TimeSpan.FromSeconds(6));
        int approachingTtl = GetOriginEdgeTtl(approaching);
        approachingTtl.Should().BeInRange(2, 3).And.BeLessThan(4);
        (await approaching.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("2");

        using HttpResponseMessage hold = await WaitForMissAsync(client, "/ccs-flow", TimeSpan.FromSeconds(5));
        GetOriginEdgeTtl(hold).Should().Be(2);
        hold.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("phase=hold");
        (await hold.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("3");

        await management.SetVersionAsync(
            "catalog",
            new AdminVersionRequest { Version = "v2" },
            TestContext.Current.CancellationToken);
        using HttpResponseMessage versionPurged = await WaitForMissAsync(
            client,
            "/ccs-flow",
            TimeSpan.FromMilliseconds(1800));
        GetOriginEdgeTtl(versionPurged).Should().Be(2);
        versionPurged.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("phase=hold");
        (await versionPurged.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("4");

        await SetScheduledUpdateAsync(management, DateTimeOffset.UtcNow.AddMinutes(1));
        using HttpResponseMessage existingEntry = await client.GetAsync(
            "/ccs-flow",
            TestContext.Current.CancellationToken);
        existingEntry.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; hit");
        (await existingEntry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("4");

        using HttpResponseMessage rescheduled = await WaitForMissAsync(
            client,
            "/ccs-flow",
            TimeSpan.FromSeconds(4));
        GetOriginEdgeTtl(rescheduled).Should().Be(4);
        rescheduled.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("phase=calm");
        (await rescheduled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("5");
    }

    [Fact]
    public async Task RuntimeVersionChange_AutomaticallyPurgesCachedDomain()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "varnish-test",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EdgeQueue:FlushIntervalSeconds"] = "0",
            ["Cache:EdgeInstances:edge:Provider"] = "Varnish",
            ["Cache:EdgeInstances:edge:Namespace"] = "varnish-integration",
            ["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] =
                new Uri(varnish.Address, "/cache-orchestrator/purge").AbsoluteUri,
            ["Cache:EdgeInstances:edge:Varnish:ApiKey"] = "integration-secret",
            ["Cache:Domains:catalog:Version"] = "v1",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:TtlSeconds"] = "300"
        });
        builder.Services.AddCacheOrchestratorCore(builder.Configuration);
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddVarnish());
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient { BaseAddress = varnish.Address };
        using HttpResponseMessage first = await client.GetAsync(
            "/domain-item",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage second = await client.GetAsync(
            "/domain-item",
            TestContext.Current.CancellationToken);
        first.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; fwd=uri-miss");
        second.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; hit");

        await host.Services.GetRequiredService<ICacheOrchestratorManagement>().SetVersionAsync(
            "catalog",
            new AdminVersionRequest { Version = "v2" },
            TestContext.Current.CancellationToken);

        using HttpResponseMessage afterVersionChange = await WaitForMissAsync(client, "/domain-item");
        afterVersionChange.Headers.GetValues("Cache-Status").Should()
            .ContainSingle("Varnish; fwd=uri-miss");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task XkeyInvalidation_ChangesHitToMiss()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:EdgeInstances:edge:Provider"] = "Varnish",
            ["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] =
                new Uri(varnish.Address, "/cache-orchestrator/purge").AbsoluteUri,
            ["Cache:EdgeInstances:edge:Varnish:ApiKey"] = "integration-secret",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:TtlSeconds"] = "300",
            ["Cache:Domains:catalog:Edge:StaleWhileRevalidateSeconds"] = "30"
        });
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddVarnish());
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient { BaseAddress = varnish.Address };
        using HttpResponseMessage first = await client.GetAsync("/item", TestContext.Current.CancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/item", TestContext.Current.CancellationToken);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        first.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; fwd=uri-miss");
        second.Headers.GetValues("Cache-Status").Should().ContainSingle("Varnish; hit");
        first.Headers.Contains("xkey").Should().BeFalse();
        first.Headers.Contains("X-CacheOrchestrator-Edge-Ttl").Should().BeFalse();

        IEdgeInvalidationProvider provider = host.Services
            .GetServices<IEdgeInvalidationProvider>()
            .Single(candidate => candidate.Name == "Varnish");
        EdgeInvalidationResult invalidation = await provider.InvalidateAsync(
            new EdgeInvalidationRequest
            {
                InstanceName = "edge",
                Tags = ["coe1-integration-item"]
            },
            TestContext.Current.CancellationToken);

        invalidation.Succeeded.Should().BeTrue();
        using HttpResponseMessage afterInvalidation = await client.GetAsync(
            "/item",
            TestContext.Current.CancellationToken);
        afterInvalidation.EnsureSuccessStatusCode();
        afterInvalidation.Headers.GetValues("Cache-Status").Should()
            .ContainSingle("Varnish; fwd=uri-miss");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> WaitForMissAsync(
        HttpClient client,
        string path,
        TimeSpan? timeoutAfter = null)
    {
        DateTimeOffset timeout = DateTimeOffset.UtcNow.Add(timeoutAfter ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            if (response.Headers.GetValues("Cache-Status").Single() == "Varnish; fwd=uri-miss")
                return response;

            response.Dispose();
            if (DateTimeOffset.UtcNow >= timeout)
                throw new TimeoutException("Varnish did not return an origin miss within the expected interval.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task SetScheduledUpdateAsync(
        ICacheOrchestratorManagement management,
        DateTimeOffset scheduledUpdateUtc)
    {
        await management.PatchSettingsAsync(
            "catalog",
            new AdminSettingsPatchRequest
            {
                Settings = new Dictionary<string, JsonElement>
                {
                    ["clientCache.scheduledUpdateUtc"] = JsonSerializer.SerializeToElement(
                        scheduledUpdateUtc.ToString("O"))
                }
            },
            TestContext.Current.CancellationToken);
    }

    private static int GetOriginEdgeTtl(HttpResponseMessage response) =>
        int.Parse(
            response.Headers.GetValues("X-Test-Origin-Edge-Ttl").Single(),
            System.Globalization.CultureInfo.InvariantCulture);

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class OriginCounter
    {
        private int _value;

        public int Next() => Interlocked.Increment(ref _value);
    }
}
