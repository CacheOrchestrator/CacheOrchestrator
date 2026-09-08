using BenchmarkDotNet.Attributes;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>Complete HTTP Output Cache pipeline, including the in-process test transport.</summary>
[MemoryDiagnoser]
[ShortJob]
public class HttpOutputCacheBenchmarks
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _factories;

    [Params(true, false)]
    public bool Hit { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Domains:catalog:OutputCache:TtlSeconds"] = Hit ? "3600" : "0",
            ["Cache:Domains:catalog:ClientCache:Cacheability"] = "Public"
        });
        builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration, enableMvcConvention: false);
        _app = builder.Build();
        _app.UseCacheOrchestrator();
        _app.MapGet("/catalog", () => Results.Text(Interlocked.Increment(ref _factories).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .CacheOutputWithDomain("catalog");
        await _app.StartAsync();
        _client = _app.GetTestClient();
        await Get();
        await Get();
        if (_factories != (Hit ? 1 : 2))
            throw new InvalidOperationException("HTTP benchmark must exercise the selected cache path.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Benchmark]
    public async Task Get()
    {
        using HttpResponseMessage response = await _client.GetAsync("/catalog");
        response.EnsureSuccessStatusCode();
    }
}
