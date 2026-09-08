using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Identity;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace CacheOrchestrator.IntegrationTests.Behavior;

/// <summary>
/// Endpoint cache identity: content-hash POST caching, unbound create POST stays uncached,
/// fluent duplicate methods fail before traffic.
/// </summary>
public class CacheIdentityHttpTests
{
    private static Dictionary<string, string?> Base(string domain) => new()
    {
        ["Cache:OutputCache:Provider"] = "InMemory",
        ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
        ["Cache:EmitDiagnosticsHeaders"] = "true",
        [$"Cache:Domains:{domain}:Version"] = "v1",
        [$"Cache:Domains:{domain}:ClientCache:Cacheability"] = "Public",
        [$"Cache:Domains:{domain}:ClientCache:TtlSeconds"] = "60",
        [$"Cache:Domains:{domain}:ClientCache:TtlMinSeconds"] = "60",
        [$"Cache:Domains:{domain}:OutputCache:TtlSeconds"] = "120",
        [$"Cache:Domains:{domain}:DataCache:TtlSeconds"] = "300",
        [$"Cache:Domains:{domain}:FusionCache:JitterSeconds"] = "0",
    };

    [Fact]
    public async Task ContentHashPost_SameBody_SecondRequestIsHit()
    {
        string domain = "id-" + Guid.NewGuid().ToString("N");
        int hits = 0;

        await using WebApplication app = await CreateAppAsync(domain, map =>
        {
            map.MapPost("/graphql", async (HttpContext http) =>
            {
                Interlocked.Increment(ref hits);
                string body = await new StreamReader(http.Request.Body).ReadToEndAsync(http.RequestAborted);
                return Results.Text(body);
            })
            .CacheOutputWithDomain(domain)
            .WithContentHashCacheIdentity(["POST"], maxBodyBytes: 65_536);
        });

        HttpClient client = app.GetTestClient();
        using StringContent content = new("{\"query\":\"{ ping }\"}", Encoding.UTF8, "application/json");

        HttpResponseMessage first = await client.PostAsync("/graphql", content, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.TryGetValues("X-CacheOrchestrator", out IEnumerable<string>? x1).Should().BeTrue();
        string.Join(',', x1!).Should().Contain("oc=miss");

        using StringContent content2 = new("{\"query\":\"{ ping }\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage second = await client.PostAsync("/graphql", content2, TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.TryGetValues("X-CacheOrchestrator", out IEnumerable<string>? x2).Should().BeTrue();
        string.Join(',', x2!).Should().Contain("oc=hit");

        Volatile.Read(ref hits).Should().Be(1);
    }

    [Fact]
    public async Task ContentHashPost_DifferentBodies_AreDistinctEntries()
    {
        string domain = "id-" + Guid.NewGuid().ToString("N");
        int hits = 0;

        await using WebApplication app = await CreateAppAsync(domain, map =>
        {
            map.MapPost("/graphql", async (HttpContext http) =>
            {
                Interlocked.Increment(ref hits);
                string body = await new StreamReader(http.Request.Body).ReadToEndAsync(http.RequestAborted);
                return Results.Text(body);
            })
            .CacheOutputWithDomain(domain)
            .WithContentHashCacheIdentity(["POST"]);
        });

        HttpClient client = app.GetTestClient();

        (await client.PostAsync(
            "/graphql",
            new StringContent("{\"q\":1}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        (await client.PostAsync(
            "/graphql",
            new StringContent("{\"q\":2}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        Volatile.Read(ref hits).Should().Be(2);
    }

    [Fact]
    public async Task CreatePost_SameDomain_WithoutIdentity_IsNotOutputCached()
    {
        string domain = "id-" + Guid.NewGuid().ToString("N");
        int hits = 0;

        await using WebApplication app = await CreateAppAsync(domain, map =>
        {
            map.MapPost("/api/products", () =>
            {
                Interlocked.Increment(ref hits);
                return Results.Ok(new { id = 1 });
            }).CacheOutputWithDomain(domain);
        });

        HttpClient client = app.GetTestClient();
        (await client.PostAsync("/api/products", new StringContent("{}"), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        (await client.PostAsync("/api/products", new StringContent("{}"), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        Volatile.Read(ref hits).Should().Be(2);
    }

    [Fact]
    public async Task FluentDuplicateMethods_ThrowDuringEndpointBuild()
    {
        string domain = "id-" + Guid.NewGuid().ToString("N");

        Func<Task> act = async () =>
        {
            await using WebApplication app = await CreateAppAsync(domain, map =>
            {
                map.MapPost("/x", () => Results.Ok())
                    .CacheOutputWithDomain(domain)
                    .WithCacheIdentity(["POST"], CacheIdentities.Url)
                    .WithContentHashCacheIdentity(["POST"]);
            });
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*POST*");
    }

    [Fact]
    public async Task ContentHashPost_OversizedChunkedBody_BypassesCache_ButEndpointReadsFullBody()
    {
        string domain = "id-" + Guid.NewGuid().ToString("N");
        string payload = new string('x', 20);

        await using WebApplication app = await CreateAppAsync(domain, map =>
        {
            map.MapPost("/graphql", async (HttpContext http) =>
            {
                string body = await new StreamReader(http.Request.Body).ReadToEndAsync(http.RequestAborted);
                return Results.Text(body);
            })
            .CacheOutputWithDomain(domain)
            .WithContentHashCacheIdentity(["POST"], maxBodyBytes: 8);
        });

        HttpClient client = app.GetTestClient();
        using ChunkedStringContent content = new(payload);
        HttpResponseMessage response = await client.PostAsync("/graphql", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Be(payload);
        response.Headers.TryGetValues("X-CacheOrchestrator", out IEnumerable<string>? x).Should().BeTrue();
        string.Join(',', x!).Should().NotContain("oc=hit");
    }

    private sealed class ChunkedStringContent : HttpContent
    {
        private readonly byte[] _payload;

        public ChunkedStringContent(string payload)
        {
            _payload = Encoding.UTF8.GetBytes(payload);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task UnknownContractName_FailsWhenResolved()
    {
        string domain = "id-" + Guid.NewGuid().ToString("N");

        await using WebApplication app = await CreateAppAsync(domain, map =>
        {
            map.MapPost("/search", () => Results.Ok())
                .CacheOutputWithDomain(domain)
                .WithCacheIdentity(["POST"], "missing-contract");
        });

        EndpointDataSource dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        CacheIdentityEndpointMetadata? meta = dataSource.Endpoints
            .Select(e => e.Metadata.GetMetadata<CacheIdentityEndpointMetadata>())
            .FirstOrDefault(m => m is not null);
        meta.Should().NotBeNull("identity metadata should be present after the host starts");

        // Startup may defer resolution until ApplicationStarted; force the same path explicitly.
        meta.IsResolved.Should().BeFalse(
            "unknown contract must not mark identity metadata as resolved");

        Action resolve = () => CacheIdentityEndpointResolver.ResolveAll(app.Services);
        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing-contract*");
    }

    private static async Task<WebApplication> CreateAppAsync(
        string domain,
        Action<WebApplication> map)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(Base(domain))
            .Build();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddCacheOrchestratorAspNetCore(config, enableMvcConvention: false);
        builder.Services.AddCacheOrchestratorFusionCache(config);

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseCacheOrchestrator();
        map(app);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
