using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace CacheOrchestrator.Edge.Varnish.UnitTests;

public class VarnishEdgeProviderTests
{
    [Fact]
    public async Task Registration_WithValidConfiguration_StartsHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:EdgeInstances:edge:Provider"] = "Varnish",
            ["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] = "http://varnish/cache-orchestrator/purge",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:TtlSeconds"] = "600",
            ["Cache:Domains:catalog:Edge:StaleWhileRevalidateSeconds"] = "30"
        });
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddVarnish());
        using IHost host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Registration_WithStaleIfError_FailsStartupValidation()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:EdgeInstances:edge:Provider"] = "Varnish",
            ["Cache:EdgeInstances:edge:Varnish:PurgeUrl"] = "http://varnish/cache-orchestrator/purge",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:StaleIfErrorSeconds"] = "30"
        });
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddVarnish());
        using IHost host = builder.Build();

        Func<Task> start = () => host.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*StaleIfErrorSeconds*not supported*");
    }

    [Fact]
    public void ApplyResponseMetadata_WhenCacheable_WritesVclContractHeaders()
    {
        VarnishEdgeProvider sut = CreateProvider(new RecordingHandler());
        var http = new DefaultHttpContext();

        sut.ApplyResponseMetadata(http.Response, new EdgeResponseMetadata
        {
            IsCacheable = true,
            Ttl = TimeSpan.FromSeconds(600),
            StaleWhileRevalidate = TimeSpan.FromSeconds(30),
            Tags = ["coe1-a", "coe1-b"]
        });

        http.Response.Headers[VarnishEdgeProvider.CacheableHeader].ToString().Should().Be("1");
        http.Response.Headers[VarnishEdgeProvider.TtlHeader].ToString().Should().Be("600");
        http.Response.Headers[VarnishEdgeProvider.GraceHeader].ToString().Should().Be("30");
        http.Response.Headers[VarnishEdgeProvider.TagHeader].ToString().Should().Be("coe1-a coe1-b");
    }

    [Fact]
    public async Task InvalidateAsync_SendsProtectedXkeyRequest()
    {
        var handler = new RecordingHandler();
        VarnishEdgeProvider sut = CreateProvider(handler);

        EdgeInvalidationResult result = await sut.InvalidateAsync(new EdgeInvalidationRequest
        {
            Target = sut.CaptureTarget("edge", TargetConfiguration()),
            Tags = ["coe1-a", "coe1-b"]
        }, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        handler.Method!.Method.Should().Be("PURGE");
        handler.Uri!.AbsoluteUri.Should().Be("http://varnish/cache-orchestrator/purge");
        handler.Headers[VarnishEdgeProvider.PurgeHeader].Should().Be("coe1-a coe1-b");
        handler.Headers["X-Edge-Key"].Should().Be("secret");
    }

    private static VarnishEdgeProvider CreateProvider(RecordingHandler handler)
    {
        var client = new HttpClient(handler);
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(VarnishEdgeProvider.HttpClientName).Returns(client);
        return new VarnishEdgeProvider(factory);
    }

    private static IConfigurationSection TargetConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["edge:Varnish:PurgeUrl"] = "http://varnish/cache-orchestrator/purge", ["edge:Varnish:ApiKey"] = "secret", ["edge:Varnish:ApiKeyHeaderName"] = "X-Edge-Key" }).Build().GetSection("edge");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            foreach ((string name, IEnumerable<string> values) in request.Headers)
                Headers[name] = string.Join(",", values);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
