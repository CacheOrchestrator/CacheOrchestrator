using BenchmarkDotNet.Attributes;
using CacheOrchestrator.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace CacheOrchestrator.Benchmarks.Benchmarks;

/// <summary>
/// Hot-path HTTP helpers used on nearly every cacheable request (query strip, no-store, encoding).
/// </summary>
[MemoryDiagnoser]
[ShortJob]
public class HttpHelperBenchmarks
{
    private StringValues _cacheControlWithNoStore = default;
    private StringValues _cacheControlWithout = default;
    private StringValues _cacheControlMulti = default;
    private DefaultHttpContext _http = null!;
    private string[] _allowedEncodings = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cacheControlWithNoStore = new StringValues("private, no-store, max-age=0");
        _cacheControlWithout = new StringValues("public, max-age=60");
        _cacheControlMulti = new StringValues(["private", "no-store"]);
        _allowedEncodings = ["br", "gzip"];
        _http = new DefaultHttpContext();
        _http.Request.Headers.AcceptEncoding = "gzip, deflate, br";
    }

    [Benchmark(Baseline = true)]
    public bool IsTracking_BusinessKey()
        => HttpHelper.IsTrackingParameter("page");

    [Benchmark]
    public bool IsTracking_UtmKey()
        => HttpHelper.IsTrackingParameter("utm_source");

    [Benchmark]
    public bool IsTracking_Fbclid()
        => HttpHelper.IsTrackingParameter("fbclid");

    [Benchmark]
    public bool ContainsNoStore_Hit()
        => HttpHelper.ContainsCacheDirective(_cacheControlWithNoStore, "no-store");

    [Benchmark]
    public bool ContainsNoStore_Miss()
        => HttpHelper.ContainsCacheDirective(_cacheControlWithout, "no-store");

    [Benchmark]
    public bool ContainsNoStore_MultiValue()
        => HttpHelper.ContainsCacheDirective(_cacheControlMulti, "no-store");

    [Benchmark]
    public string NormalizeAcceptEncoding_Match()
    {
        _http.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        return HttpHelper.NormalizeNegotiationHeader(_http.Request.Headers.AcceptEncoding, _allowedEncodings);
    }

    [Benchmark]
    public string NormalizeAcceptEncoding_NoMatch()
    {
        _http.Request.Headers.AcceptEncoding = "identity";
        return HttpHelper.NormalizeNegotiationHeader(_http.Request.Headers.AcceptEncoding, _allowedEncodings);
    }
}
