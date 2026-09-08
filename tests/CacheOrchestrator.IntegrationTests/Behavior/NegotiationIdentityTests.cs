using System.Security.Claims;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CacheOrchestrator.IntegrationTests.Behavior;

public class NegotiationIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MvcNegotiation_CachedResponseMatchesFormatter(bool normalize, bool reverse)
    {
        await using WebApplication app = CreateApp(normalize);
        app.UseCacheOrchestrator();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();

        string[][] pairs =
        [
            ["application/json;q=1, application/xml;q=0.1", "application/json;q=0, application/xml;q=1"],
            ["application/xml", "application/xml, */*"],
            ["application/xml", "application/json;q=0, application/*;q=1"]
        ];
        for (int i = 0; i < pairs.Length; i++)
        {
            string[] pair = reverse ? [pairs[i][1], pairs[i][0]] : pairs[i];
            string path = "/negotiation-contract/" + i;
            using HttpResponseMessage seed = await SendAsync(client, path, "Accept", pair[0]);
            using HttpResponseMessage actual = await SendAsync(client, path, "Accept", pair[1]);
            using HttpResponseMessage repeated = await SendAsync(client, path, "Accept", pair[1]);
            using HttpResponseMessage expected = await SendAsync(client, path, "Accept", pair[1], bypass: true);

            actual.EnsureSuccessStatusCode();
            actual.Content.Headers.ContentType.Should().Be(expected.Content.Headers.ContentType);
            (await actual.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(await expected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            repeated.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("oc=hit");
            repeated.Content.Headers.ContentType.Should().Be(expected.Content.Headers.ContentType);
        }
    }

    [Theory]
    [InlineData(false, "br;q=0,gzip;q=1,*;q=0.5")]
    [InlineData(true, "br;q=0,gzip;q=1,*;q=0.5")]
    [InlineData(false, "br;q=0.1,gzip;q=1")]
    [InlineData(true, "br;q=0.1,gzip;q=1")]
    public async Task Compression_UsesCurrentNegotiationOnCacheHit(bool compressionOutside, string acceptEncoding)
    {
        await using WebApplication app = CreateApp(normalize: true);
        if (compressionOutside)
            app.UseResponseCompression();
        app.UseCacheOrchestrator();
        if (!compressionOutside)
            app.UseResponseCompression();
        app.MapGet("/compressed-contract", () => Results.Text(new string('x', 2048)))
            .CacheOutputWithDomain("negotiation");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage seed = await SendAsync(client, "/compressed-contract", "Accept-Encoding", "br");
        using HttpResponseMessage first = await SendAsync(client, "/compressed-contract", "Accept-Encoding", acceptEncoding);
        using HttpResponseMessage hit = await SendAsync(client, "/compressed-contract", "Accept-Encoding", acceptEncoding);
        using HttpResponseMessage expected = await SendAsync(client, "/compressed-contract", "Accept-Encoding", acceptEncoding, bypass: true);

        hit.EnsureSuccessStatusCode();
        hit.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("oc=hit");
        hit.Content.Headers.ContentEncoding.Should().Equal("gzip");
        hit.Content.Headers.ContentEncoding.Should().Equal(expected.Content.Headers.ContentEncoding);
        (await hit.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(await expected.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("alice", "acme", "bob", "acme")]
    [InlineData("user;tenant_id=x", "y", "user", "x;tenant_id=y")]
    [InlineData("user", "x;tenant_id=y", "user;tenant_id=x", "y")]
    public async Task DocumentedPerUserClaims_SeparateUsersAcrossOutputAndDataCache(
        string firstUser, string firstTenant, string secondUser, string secondTenant)
    {
        await using WebApplication app = CreateApp(normalize: false, privateUsers: true);
        app.Use(async (http, next) =>
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", http.Request.Headers["X-Test-User"].ToString()),
                    new Claim("tenant_id", http.Request.Headers["X-Test-Tenant"].ToString())],
                "test"));
            await next(http);
        });
        app.UseCacheOrchestrator();
        int calls = 0;
        app.MapGet("/per-user-contract", async (HttpContext http, IDomainDataCache cache) =>
            await cache.GetOrSetAsync(http, _ => Task.FromResult(
                http.User.FindFirst("sub")!.Value + ":" + Interlocked.Increment(ref calls))))
            .CacheOutputWithDomain("negotiation");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();

        foreach ((string user, string tenant) in new[] { (firstUser, firstTenant), (secondUser, secondTenant) })
        {
            using HttpResponseMessage miss = await SendAsync(client, "/per-user-contract", "X-Test-User", user, tenant: tenant);
            using HttpResponseMessage hit = await SendAsync(client, "/per-user-contract", "X-Test-User", user, tenant: tenant);
            string body = await miss.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.Should().StartWith(user + ":");
            (await hit.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(body);
            miss.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("dc=miss");
            hit.Headers.GetValues("X-CacheOrchestrator").Single().Should().Contain("oc=hit");
            hit.Headers.CacheControl!.Private.Should().BeTrue();
        }
        calls.Should().Be(2);
    }

    private static WebApplication CreateApp(bool normalize, bool privateUsers = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        Dictionary<string, string?> config = new() { ["Cache:Domains:negotiation:Version"] = "1" };
        if (normalize)
        {
            config["Cache:Domains:negotiation:AcceptNormalizationList:0"] = "application/json";
            config["Cache:Domains:negotiation:AcceptNormalizationList:1"] = "application/xml";
        }
        if (privateUsers)
        {
            config["Cache:Domains:negotiation:AuthBypassMode"] = "Never";
            config["Cache:Domains:negotiation:VaryOutputCacheByUser"] = "true";
            config["Cache:Domains:negotiation:VaryByAuthClaims:0"] = "sub";
            config["Cache:Domains:negotiation:VaryByAuthClaims:1"] = "tenant_id";
            config["Cache:Domains:negotiation:ClientCache:Cacheability"] = "Private";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddCacheOrchestrator(builder.Configuration);
        builder.Services.AddControllers().AddXmlSerializerFormatters();
        builder.Services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        return builder.Build();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string header, string value, bool bypass = false, string? tenant = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(header, value);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Test-Tenant", tenant);
        if (bypass)
            request.Headers.CacheControl = new() { NoStore = true };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

[ApiController]
public sealed class NegotiationContractController : ControllerBase
{
    [HttpGet("/negotiation-contract/{id}")]
    [CacheDomain("negotiation")]
    public NegotiationContractPayload Get(string id) => new() { Value = id };
}

public sealed class NegotiationContractPayload
{
    public string Value { get; set; } = "";
}
