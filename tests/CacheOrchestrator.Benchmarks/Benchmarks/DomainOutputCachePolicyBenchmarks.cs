using BenchmarkDotNet.Attributes;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.OutputCache;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using System.IO.Hashing;
using System.Text;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>
/// OC policy request path + non-tracking query key collection (hot path for every GET/HEAD).
/// </summary>
[MemoryDiagnoser]
[ShortJob]
public class DomainOutputCachePolicyBenchmarks
{
    private DomainOutputCachePolicy _policy = null!;
    private OutputCacheContext _simple = null!;
    private OutputCacheContext _withQuery = null!;
    private IQueryCollection _queryMixed = null!;
    private DomainHttpCacheOptions _queryOpts = null!;

    [GlobalSetup]
    public void Setup()
    {
        _policy = new DomainOutputCachePolicy("catalog");

        _simple = CreateContext("/api/catalog");
        _withQuery = CreateContext(
            "/api/catalog",
            new Dictionary<string, StringValues>
            {
                ["page"] = "1",
                ["sort"] = "name",
                ["utm_source"] = "google",
                ["fbclid"] = "abc",
                ["id"] = "42",
            });

        _queryMixed = _withQuery.HttpContext.Request.Query;
        _queryOpts = _withQuery.HttpContext.Features.Get<ICacheOrchestratorFeature>()!.DomainOptions!;
    }

    [Benchmark(Baseline = true)]
    public async Task CacheRequest_SimpleGet()
        => await _policy.CacheRequestAsync(_simple, CancellationToken.None);

    [Benchmark]
    public async Task CacheRequest_WithQueryAndTracking()
        => await _policy.CacheRequestAsync(_withQuery, CancellationToken.None);

    [Benchmark]
    public StringValues CollectQueryKeysForOutputCache()
        => CacheVaryMaterializer.CollectQueryKeysForOutputCache(_queryMixed, _queryOpts);

    [GlobalCleanup]
    public void Cleanup()
    {
        ((ServiceProvider)_simple.HttpContext.RequestServices).Dispose();
        ((ServiceProvider)_withQuery.HttpContext.RequestServices).Dispose();
    }

    private static OutputCacheContext CreateContext(
        string path,
        Dictionary<string, StringValues>? query = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = path;
        http.Request.Host = new HostString("localhost");
        http.Request.Scheme = "https";

        if (query is not null)
            http.Request.Query = new QueryCollection(query);

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
            EncodingNormalizationList = null,
        };

        var provider = new FixedDomainOptionsProvider(cfg);
        var services = new ServiceCollection();
        services.AddSingleton<IRequestDomainCacheOptions>(provider);
        services.AddSingleton(typeof(ILogger<DomainOutputCachePolicy>), NullLogger<DomainOutputCachePolicy>.Instance);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CacheVaryMaterializer>();
        http.RequestServices = services.BuildServiceProvider();
        provider.EnsureDomainOptions(http, cfg.Domain);

        return new OutputCacheContext { HttpContext = http };
    }

    private sealed class FixedDomainOptionsProvider : IRequestDomainCacheOptions
    {
        private readonly DomainHttpCacheOptions _opts;

        public FixedDomainOptionsProvider(DomainHttpCacheOptions opts)
        {
            _opts = opts;
        }

        public DomainHttpCacheOptions EnsureDomainOptions(HttpContext http, string domain)
        {
            http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { DomainOptions = _opts });
            return _opts;
        }

        public DomainHttpCacheOptions? GetDomainOptions(HttpContext http)
            => http.Features.Get<ICacheOrchestratorFeature>()?.DomainOptions;

        public DomainHttpCacheOptions GetOrCreateDomainOptions(string domain) => _opts;
    }
}
