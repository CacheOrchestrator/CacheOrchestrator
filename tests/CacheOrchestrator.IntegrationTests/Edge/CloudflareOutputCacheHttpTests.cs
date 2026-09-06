using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Edge.Cloudflare;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Tags;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Entity;
using CacheOrchestrator.IntegrationTests.Infrastructure;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CacheOrchestrator.IntegrationTests.Edge;

public class CloudflareOutputCacheHttpTests
{
    [Fact]
    public async Task RuntimeVersionChange_QueuesPurgeOnlyForEdgeEnabledDomain()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "edge-test",
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EdgeInstances:edge:Provider"] = "Cloudflare",
            ["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = "zone-1",
            ["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "token-1",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:internal:Edge:Enabled"] = "false"
        });
        var queue = new RecordingEdgeInvalidationQueue();
        builder.Services.AddSingleton<IEdgeInvalidationQueue>(queue);
        builder.Services.AddCacheOrchestrator(builder.Configuration);
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddCloudflare());

        await using WebApplication app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        ICacheOrchestratorManagement management = app.Services.GetRequiredService<ICacheOrchestratorManagement>();

        await management.SetVersionAsync(
            "catalog",
            new AdminVersionRequest { Version = "v2" },
            TestContext.Current.CancellationToken);
        await management.SetVersionAsync(
            "internal",
            new AdminVersionRequest { Version = "v2" },
            TestContext.Current.CancellationToken);

        queue.Jobs.Should().ContainSingle();
        EdgeInvalidationJob job = queue.Jobs.Single();
        job.InstanceName.Should().Be("edge");
        job.Tags.Should().Equal(
            new EdgeTagProjector().Project("edge-test-edge-edge", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task ConfigurationReload_VersionAndDisable_QueueDomainPurges()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var reload = new ReloadableMemoryConfigurationSource(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "edge-reload",
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EdgeInstances:edge:Provider"] = "Cloudflare",
            ["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = "zone-1",
            ["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "token-1",
            ["Cache:Domains:catalog:Version"] = "v1",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge"
        });
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .Add(reload)
            .Build();
        var queue = new RecordingEdgeInvalidationQueue();
        builder.Services.AddSingleton<IEdgeInvalidationQueue>(queue);
        builder.Services.AddCacheOrchestrator(configuration);
        builder.Services.AddCacheOrchestratorEdge(
            configuration,
            edge => edge.AddCloudflare());

        await using WebApplication app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        queue.Jobs.Should().BeEmpty();

        reload.Provider!.SetAndReload("Cache:Domains:catalog:Version", "v2");
        await WaitForJobsAsync(queue, 1);
        reload.Provider.SetAndReload("Cache:Domains:catalog:Edge:Enabled", "false");
        await WaitForJobsAsync(queue, 2);

        queue.Jobs.Should().HaveCount(2);
        queue.Jobs.Should().OnlyContain(job => job.Tags.Single() ==
            new EdgeTagProjector().Project("edge-reload-edge-edge", CacheTags.Domain("catalog")));
    }

    [Fact]
    public async Task PurgeOnStartup_QueuesOnlyOptedInEdgeDomain()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "edge-startup",
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EdgeInstances:edge:Provider"] = "Cloudflare",
            ["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = "zone-1",
            ["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "token-1",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:PurgeOnStartup"] = "true",
            ["Cache:Domains:internal:Edge:Enabled"] = "false",
            ["Cache:Domains:internal:Edge:PurgeOnStartup"] = "true"
        });
        var queue = new RecordingEdgeInvalidationQueue();
        builder.Services.AddSingleton<IEdgeInvalidationQueue>(queue);
        builder.Services.AddCacheOrchestrator(builder.Configuration);
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddCloudflare());

        await using WebApplication app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        await WaitForJobsAsync(queue, 1);

        queue.Jobs.Should().ContainSingle();
    }

    [Fact]
    public async Task OutputCacheHit_ReplaysOriginalCloudflareTags()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "edge-test",
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:EdgeInstances:edge:Provider"] = "Cloudflare",
            ["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = "zone-1",
            ["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "token-1",
            ["Cache:Domains:catalog:OutputCache:Enabled"] = "true",
            ["Cache:Domains:catalog:OutputCache:TtlSeconds"] = "60",
            ["Cache:Domains:catalog:DataCache:Enabled"] = "true",
            ["Cache:Domains:catalog:DataCache:TtlSeconds"] = "60",
            ["Cache:Domains:catalog:ClientCache:Cacheability"] = "Public",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:TtlSeconds"] = "600"
        });
        builder.Services.AddCacheOrchestrator(builder.Configuration);
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddCloudflare());
        var calls = new RequestCounter();
        builder.Services.AddSingleton(calls);

        await using WebApplication app = builder.Build();
        app.UseCacheOrchestrator();
        app.MapGet("/products/{id:int}", async (
            HttpContext http,
            int id,
            IDomainDataCache cache,
            RequestCounter counter) =>
        {
            var value = await cache.GetOrSetEntityAsync(
                http,
                _ => Task.FromResult(
                    EntityCache.Create(new { id, call = counter.Increment() })
                        .DependsOn("categories", 7)),
                http.RequestAborted);
            return Results.Json(value);
        })
            .CacheOutputWithDomain("catalog", resourceRouteKey: "id", entityKind: "products");
        await app.StartAsync(TestContext.Current.CancellationToken);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage first = await client.GetAsync(
            "/products/42",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage second = await client.GetAsync(
            "/products/42",
            TestContext.Current.CancellationToken);

        string firstTags = string.Join(',', first.Headers.GetValues("Cache-Tag"));
        string secondTags = string.Join(',', second.Headers.GetValues("Cache-Tag"));
        firstTags.Should().NotBeEmpty().And.Contain("coe1-");
        firstTags.Should().Contain(
            new EdgeTagProjector().Project("edge-test-edge-edge", "entity:catalog:categories:7"));
        secondTags.Should().Be(firstTags);
        first.Headers.Contains("X-CacheOrchestrator-Staged-Tags").Should().BeFalse();
        second.Headers.Contains("X-CacheOrchestrator-Staged-Tags").Should().BeFalse();
        second.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("oc=hit");
        calls.Value.Should().Be(1);
    }

    private sealed class RequestCounter
    {
        private int _value;

        public int Value => _value;

        public int Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class RecordingEdgeInvalidationQueue : IEdgeInvalidationQueue
    {
        public ConcurrentQueue<EdgeInvalidationJob> Jobs { get; } = new();

        public ValueTask EnqueueAsync(EdgeInvalidationJob job, CancellationToken cancellationToken)
        {
            Jobs.Enqueue(job);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitForJobsAsync(RecordingEdgeInvalidationQueue queue, int expected)
    {
        DateTimeOffset timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (queue.Jobs.Count < expected && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(25, TestContext.Current.CancellationToken);

        queue.Jobs.Count.Should().BeGreaterThanOrEqualTo(expected);
    }
}
