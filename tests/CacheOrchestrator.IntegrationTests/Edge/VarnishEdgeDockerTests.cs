using CacheOrchestrator.Admin;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Varnish;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CacheOrchestrator.IntegrationTests.Edge;

[Collection("Varnish")]
public sealed class VarnishEdgeDockerTests(VarnishFixture varnish)
{
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

    private static async Task<HttpResponseMessage> WaitForMissAsync(HttpClient client, string path)
    {
        DateTimeOffset timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            if (response.Headers.GetValues("Cache-Status").Single() == "Varnish; fwd=uri-miss")
                return response;

            response.Dispose();
            if (DateTimeOffset.UtcNow >= timeout)
                throw new TimeoutException("Varnish did not observe the automatic domain purge within five seconds.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
