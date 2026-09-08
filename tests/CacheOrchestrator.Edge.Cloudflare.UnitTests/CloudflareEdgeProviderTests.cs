using CacheOrchestrator.Edge.Providers;
using CacheOrchestrator.Edge.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace CacheOrchestrator.Edge.Cloudflare.UnitTests;

public class CloudflareEdgeProviderTests
{
    [Fact]
    public async Task Registration_WithValidConfiguration_StartsHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:EdgeInstances:edge:Provider"] = "Cloudflare",
            ["Cache:EdgeInstances:edge:Cloudflare:ZoneId"] = "zone-1",
            ["Cache:EdgeInstances:edge:Cloudflare:ApiToken"] = "token-1",
            ["Cache:Domains:catalog:Edge:Enabled"] = "true",
            ["Cache:Domains:catalog:Edge:Instance"] = "edge",
            ["Cache:Domains:catalog:Edge:TtlSeconds"] = "600"
        });
        builder.Services.AddCacheOrchestratorEdge(
            builder.Configuration,
            edge => edge.AddCloudflare());
        using IHost host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ApplyResponseMetadata_WhenCacheable_WritesTagsAndEdgePolicy()
    {
        CloudflareEdgeProvider sut = CreateProvider(new RecordingHandler());
        var http = new DefaultHttpContext();

        sut.ApplyResponseMetadata(http.Response, new EdgeResponseMetadata
        {
            IsCacheable = true,
            Ttl = TimeSpan.FromSeconds(600),
            StaleWhileRevalidate = TimeSpan.FromSeconds(30),
            StaleIfError = TimeSpan.FromSeconds(300),
            Tags = ["coe1-a", "coe1-b"]
        });

        http.Response.Headers["Cloudflare-CDN-Cache-Control"].ToString().Should()
            .Be("max-age=600, stale-while-revalidate=30, stale-if-error=300");
        http.Response.Headers["Cache-Tag"].ToString().Should().Be("coe1-a,coe1-b");
    }

    [Fact]
    public void ApplyResponseMetadata_WhenNotCacheable_WritesNoStoreAndRemovesTags()
    {
        CloudflareEdgeProvider sut = CreateProvider(new RecordingHandler());
        var http = new DefaultHttpContext();
        http.Response.Headers["Cache-Tag"] = "old";

        sut.ApplyResponseMetadata(http.Response, new EdgeResponseMetadata { IsCacheable = false });

        http.Response.Headers["Cloudflare-CDN-Cache-Control"].ToString().Should().Be("no-store");
        http.Response.Headers.ContainsKey("Cache-Tag").Should().BeFalse();
    }

    [Fact]
    public async Task InvalidateAsync_SendsAuthenticatedTagRequest()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
        });
        CloudflareEdgeProvider sut = CreateProvider(handler);

        EdgeInvalidationResult result = await sut.InvalidateAsync(new EdgeInvalidationRequest
        {
            Target = sut.CaptureTarget("edge", TargetConfiguration()),
            Tags = ["coe1-a", "coe1-b"]
        }, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        handler.Method.Should().Be(HttpMethod.Post);
        handler.Uri!.AbsoluteUri.Should().Be("https://api.cloudflare.com/client/v4/zones/zone-1/purge_cache");
        handler.Authorization.Should().Be("Bearer token-1");
        handler.Body.Should().Contain("\"tags\"").And.Contain("coe1-a").And.Contain("coe1-b");
    }

    [Fact]
    public async Task InvalidateAsync_WhenRateLimited_ReturnsTransientRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        CloudflareEdgeProvider sut = CreateProvider(new RecordingHandler(response));

        EdgeInvalidationResult result = await sut.InvalidateAsync(new EdgeInvalidationRequest
        {
            Target = sut.CaptureTarget("edge", TargetConfiguration()),
            Tags = ["coe1-a"]
        }, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.IsTransient.Should().BeTrue();
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    private static CloudflareEdgeProvider CreateProvider(RecordingHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.cloudflare.com/client/v4/") };
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(CloudflareEdgeProvider.HttpClientName).Returns(client);
        return new CloudflareEdgeProvider(factory);
    }

    private static IConfigurationSection TargetConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["edge:Cloudflare:ZoneId"] = "zone-1", ["edge:Cloudflare:ApiToken"] = "token-1" }).Build().GetSection("edge");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage? response = null) =>
            _response = response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            };

        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
