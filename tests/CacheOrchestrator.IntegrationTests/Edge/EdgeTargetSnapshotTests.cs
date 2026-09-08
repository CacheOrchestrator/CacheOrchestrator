using CacheOrchestrator.Edge.Cloudflare;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.Varnish;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Net;
using System.Text;

namespace CacheOrchestrator.IntegrationTests.Edge;

public class EdgeTargetSnapshotTests
{
    [Theory]
    [InlineData("Cloudflare", "Cloudflare")]
    [InlineData("Varnish", "Varnish")]
    [InlineData("Cloudflare", "Varnish")]
    [InlineData("Varnish", "Cloudflare")]
    [InlineData("Cloudflare", null)]
    [InlineData("Varnish", null)]
    public async Task PlacementChangeOrRemoval_PurgesOriginalTargetWithOriginalCredentials(string originalProvider, string? nextProvider)
    {
        IConfigurationRoot configuration = Configuration(originalProvider);
        var queue = new RecordingQueue();
        var handler = new RecordingHandler();
        using ServiceProvider services = Services(configuration, queue, handler);
        EdgeDomainChangeMonitor monitor = services.GetRequiredService<EdgeDomainChangeMonitor>();
        IReadOnlyDictionary<string, EdgeDomainSnapshot> before = monitor.CaptureSnapshot();
        configuration["Cache:EdgeInstances:edge:Provider"] = nextProvider;
        configuration["Cache:Domains:catalog:Edge:Enabled"] = nextProvider is null ? "false" : "true";
        configuration["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = nextProvider is null ? null : "new-zone";
        configuration["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = nextProvider is null ? null : "new-token";
        configuration["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] = nextProvider is null ? null : "http://new-edge/purge";
        configuration["Cache:EdgeInstances:edge:Varnish:ApiKey"] = nextProvider is null ? null : "new-secret";
        if (nextProvider is null)
            configuration.Providers.OfType<RemovableProvider>().Single().RemovePrefix("Cache:EdgeInstances:edge:");
        IReadOnlyDictionary<string, EdgeDomainSnapshot> after = monitor.CaptureSnapshot();
        await monitor.ApplyChangesAsync(before, after, TestContext.Current.CancellationToken);
        queue.Jobs.Should().HaveCount(nextProvider is null ? 1 : 2);
        foreach (EdgeInvalidationJob job in queue.Jobs)
        {
            IEdgeInvalidationProvider provider = services.GetServices<IEdgeInvalidationProvider>().Single(item => item.Name == job.ProviderName);
            EdgeInvalidationResult result = await provider.InvalidateAsync(new EdgeInvalidationRequest
            {
                Target = job.Target, Tags = job.Tags
            }, TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeTrue();
        }
        handler.Requests[0].Url.Should().Be(Url(originalProvider, "old"));
        handler.Requests[0].Credential.Should().Be(originalProvider == "Cloudflare" ? "Bearer old-token" : "old-secret");
        if (nextProvider is not null)
        {
            handler.Requests[1].Url.Should().Be(Url(nextProvider, "new"));
            handler.Requests[1].Credential.Should().Be(nextProvider == "Cloudflare" ? "Bearer new-token" : "new-secret");
        }
    }

    [Theory]
    [InlineData("Cloudflare")]
    [InlineData("Varnish")]
    public async Task CredentialRotation_UpdatesSnapshotWithoutUnnecessaryPlacementPurge(string provider)
    {
        IConfigurationRoot configuration = Configuration(provider);
        var queue = new RecordingQueue();
        using ServiceProvider services = Services(configuration, queue, new RecordingHandler());
        EdgeDomainChangeMonitor monitor = services.GetRequiredService<EdgeDomainChangeMonitor>();
        IReadOnlyDictionary<string, EdgeDomainSnapshot> before = monitor.CaptureSnapshot();
        configuration["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "new-token";
        configuration["Cache:EdgeInstances:edge:Varnish:ApiKey"] = "new-secret";
        IReadOnlyDictionary<string, EdgeDomainSnapshot> after = monitor.CaptureSnapshot();
        before["catalog"].Target.Should().NotBe(after["catalog"].Target);
        before["catalog"].HasSamePlacement(after["catalog"]).Should().BeTrue();
        await monitor.ApplyChangesAsync(before, after, TestContext.Current.CancellationToken);
        queue.Jobs.Should().BeEmpty();
    }

    private static string Url(string provider, string prefix) => provider == "Cloudflare"
        ? $"https://api.cloudflare.com/client/v4/zones/{prefix}-zone/purge_cache" : $"http://{prefix}-edge/purge";

    private static IConfigurationRoot Configuration(string provider) => new ConfigurationBuilder().Add(new RemovableSource(new Dictionary<string, string?>
    {
        ["Cache:Domains:catalog:Version"] = "1", ["Cache:Domains:catalog:Edge:Enabled"] = "true",
        ["Cache:Domains:catalog:Edge:Instance"] = "edge", ["Cache:Domains:catalog:Edge:TtlSeconds"] = "600",
        ["Cache:EdgeInstances:edge:Provider"] = provider, ["Cache:EdgeInstances:edge:Namespace"] = "tags",
        ["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = "old-zone", ["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "old-token",
        ["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] = "http://old-edge/purge", ["Cache:EdgeInstances:edge:Varnish:ApiKey"] = "old-secret",
        ["Cache:EdgeInstances:edge:Varnish:ApiKeyHeaderName"] = "X-Edge-Secret"
    })).Build();

    private static ServiceProvider Services(IConfiguration config, RecordingQueue queue, RecordingHandler handler)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IEdgeInvalidationQueue>(queue);
        services.AddCacheOrchestratorEdge(config, builder => builder.AddCloudflare().AddVarnish());
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/")
        });
        services.AddSingleton(factory);
        return services.BuildServiceProvider();
    }

    private sealed class RemovableSource(Dictionary<string, string?> values) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new RemovableProvider(values);
    }

    private sealed class RemovableProvider : ConfigurationProvider
    {
        public RemovableProvider(Dictionary<string, string?> values) => Data = values;
        public void RemovePrefix(string prefix)
        {
            foreach (string key in Data.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                Data.Remove(key);
        }
    }

    private sealed class RecordingQueue : IEdgeInvalidationQueue
    {
        public List<EdgeInvalidationJob> Jobs { get; } = [];
        public ValueTask EnqueueAsync(EdgeInvalidationJob job, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Credential)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string credential = request.Headers.Authorization?.ToString()
                ?? request.Headers.GetValues("X-Edge-Secret").Single();
            Requests.Add((request.RequestUri!.AbsoluteUri, credential));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
        }
    }
}
