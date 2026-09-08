using BenchmarkDotNet.Attributes;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Identity;
using CacheOrchestrator.OutputCache;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using System.IO.Hashing;
using System.Text;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>
/// POST content-hash identity path (separate from GET baseline in <see cref="DomainOutputCachePolicyBenchmarks"/>).
/// </summary>
[MemoryDiagnoser]
[ShortJob]
public class CacheIdentityPolicyBenchmarks
{
    private DomainOutputCachePolicy _policy = null!;
    private OutputCacheContext _postHash = null!;
    private OutputCacheContext _getNoIdentity = null!;

    [GlobalSetup]
    public void Setup()
    {
        _policy = new DomainOutputCachePolicy("catalog");

        CacheIdentityEndpointMetadata identity = new();
        identity.AddBinding("POST", CacheIdentityBinding.CreateContentHash(65_536), "bench");

        _postHash = CreateContext(
            HttpMethods.Post,
            "/graphql",
            identity,
            body: "{\"query\":\"{ products { id name } }\"}");

        _getNoIdentity = CreateContext(HttpMethods.Get, "/api/catalog", identity: null, body: null);
    }

    [Benchmark(Baseline = true)]
    public async Task CacheRequest_Get_NoIdentity()
        => await _policy.CacheRequestAsync(_getNoIdentity, CancellationToken.None);

    [Benchmark]
    public async Task CacheRequest_Post_ContentHash()
        => await _policy.CacheRequestAsync(_postHash, CancellationToken.None);

    [GlobalCleanup]
    public void Cleanup()
    {
        ((ServiceProvider)_postHash.HttpContext.RequestServices).Dispose();
        ((ServiceProvider)_getNoIdentity.HttpContext.RequestServices).Dispose();
        _postHash.HttpContext.Request.Body.Dispose();
    }

    private static OutputCacheContext CreateContext(
        string method,
        string path,
        CacheIdentityEndpointMetadata? identity,
        string? body)
    {
        DefaultHttpContext http = new();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.Host = new HostString("localhost");
        http.Request.Scheme = "https";

        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
        }

        if (identity is not null)
        {
            Endpoint endpoint = new(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(identity),
                "bench");
            http.SetEndpoint(endpoint);
        }

        DomainHttpCacheOptions cfg = new()
        {
            CoreOptions = new DomainCacheOptions
            {
                Domain = "catalog",
                Version = "1",
                VersionHex = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes("1")).ToString("x16"),
            },
            OutputCacheEnabled = true,
            AuthBypassMode = AuthBypassMode.AuthenticatedOrAuthorization,
            VaryOutputCacheByUser = true,
            OutputTtl = TimeSpan.FromSeconds(60),
            ETag = new StringValues($"W/\"{XxHash3.HashToUInt64(Encoding.UTF8.GetBytes("1")):x16}\""),
            CacheableStatusCodes = [200],
            ClientCacheability = ClientCacheability.Public,
            ClientTtlSeconds = 60,
            ClientTtlMinSeconds = 60,
            OutputCacheNamespace = "bench-oc",
        };

        FixedDomainOptionsProvider provider = new(cfg);
        ServiceCollection services = new();
        services.AddSingleton<IRequestDomainCacheOptions>(provider);
        services.AddSingleton(typeof(ILogger<DomainOutputCachePolicy>), NullLogger<DomainOutputCachePolicy>.Instance);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CacheVaryMaterializer>();
        http.RequestServices = services.BuildServiceProvider();
        provider.EnsureDomainOptions(http, cfg.Domain);

        return new OutputCacheContext { HttpContext = http };
    }

    private sealed class FixedDomainOptionsProvider(DomainHttpCacheOptions opts) : IRequestDomainCacheOptions
    {
        public DomainHttpCacheOptions EnsureDomainOptions(HttpContext http, string domain)
        {
            http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { DomainOptions = opts });
            return opts;
        }

        public DomainHttpCacheOptions? GetDomainOptions(HttpContext http)
            => http.Features.Get<ICacheOrchestratorFeature>()?.DomainOptions;

        public DomainHttpCacheOptions GetOrCreateDomainOptions(string domain) => opts;
    }
}
