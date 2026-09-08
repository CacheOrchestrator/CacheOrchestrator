using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.Admin;
using CacheOrchestrator.Identity;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CacheOrchestrator.IntegrationTests.Behavior;

/// <summary>
/// HTTP pipeline coverage for contracts that unit tests pin on the policy/helper,
/// but that still need TestServer + ASP.NET Output Cache to prove end-to-end.
/// </summary>
public class HttpPipelineContractTests
{
    private sealed class HitCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class RecordingObserver : ICacheInvalidationObserver
    {
        public List<CacheInvalidationKind> Before { get; } = [];
        public List<CacheInvalidationKind> After { get; } = [];

        public ValueTask OnBeforeInvalidateAsync(
            CacheInvalidationContext context,
            CancellationToken cancellationToken = default)
        {
            lock (Before)
                Before.Add(context.Kind);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnAfterInvalidateAsync(
            CacheInvalidationContext context,
            CacheInvalidationResult result,
            CancellationToken cancellationToken = default)
        {
            lock (After)
                After.Add(context.Kind);
            return ValueTask.CompletedTask;
        }
    }

    private static Dictionary<string, string?> BaseConfig(string domain, Action<Dictionary<string, string?>>? extra = null)
    {
        Dictionary<string, string?> d = new()
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
        extra?.Invoke(d);
        return d;
    }

    private static Dictionary<string, string?> DefaultsOnlyConfig() => new()
    {
        ["Cache:OutputCache:Provider"] = "InMemory",
        ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
        ["Cache:EmitDiagnosticsHeaders"] = "true",
        ["Cache:DomainDefaults:Version"] = "v1",
        ["Cache:DomainDefaults:ClientCache:Cacheability"] = "Public",
        ["Cache:DomainDefaults:ClientCache:TtlSeconds"] = "60",
        ["Cache:DomainDefaults:ClientCache:TtlMinSeconds"] = "60",
        ["Cache:DomainDefaults:OutputCache:TtlSeconds"] = "120",
        ["Cache:DomainDefaults:DataCache:TtlSeconds"] = "300",
    };

    private static async Task<(HttpClient Client, WebApplication App)> StartAsync(
        Dictionary<string, string?> configValues,
        Action<WebApplication> map,
        Action<IServiceCollection>? configureServices = null)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddCacheOrchestratorAspNetCore(config);
        builder.Services.AddCacheOrchestratorFusionCache(config);
        builder.Services.AddSingleton<HitCounter>();
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseCacheOrchestrator();
        map(app);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app.GetTestClient(), app);
    }

    private static async Task<(HttpResponseMessage Res, string CacheOrchestratorHeader, string Body)> GetAsync(
        HttpClient client,
        string url,
        Dictionary<string, string>? headers = null)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> h in headers)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        HttpResponseMessage res = await client.SendAsync(req, TestContext.Current.CancellationToken);
        string body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string xCache = res.Headers.TryGetValues("X-CacheOrchestrator", out IEnumerable<string>? values)
            ? string.Join(",", values)
            : string.Empty;
        return (res, xCache, body);
    }

    [Fact]
    public async Task DirectEndpoint_ReportsNotApplicableDataCache_AndOneFactoryRun()
    {
        string domain = "direct-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain, values =>
        {
            values["Cache:Admin:Enabled"] = "true";
            values["Cache:Admin:TrackEndpoints"] = "true";
            values["Cache:Admin:TrackLatency"] = "true";
        });

        (HttpClient client, WebApplication app) = await StartAsync(config, endpoints =>
        {
            endpoints.MapGet("/direct", () => Results.Json(new { Value = 42 }))
                .CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage _, string miss, string _) = await GetAsync(client, "/direct");
            (HttpResponseMessage _, string hit, string _) = await GetAsync(client, "/direct");

            miss.Should().Contain("oc=miss")
                .And.Contain("dc=n/a")
                .And.Contain("fa=run")
                .And.Contain("ms=");
            hit.Should().Contain("oc=hit").And.NotContain("dc=").And.NotContain("fa=");

            AdminDomainCountersDto counters = app.Services
                .GetRequiredService<IAdminStatsCollector>()
                .GetRawSnapshot()
                .Domains
                .Single(d => d.Name == domain);
            counters.FactoryRuns.Should().Be(1);
            counters.FactoryFailures.Should().Be(0);
            counters.FactoryDurationCount.Should().Be(1);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DataCacheFactory_IsNotDoubleCountedByDirectFactoryTelemetry()
    {
        string domain = "data-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain, values =>
        {
            values["Cache:Admin:Enabled"] = "true";
            values["Cache:Admin:TrackEndpoints"] = "true";
        });

        (HttpClient client, WebApplication app) = await StartAsync(config, endpoints =>
        {
            endpoints.MapGet("/data-once", async (
                HttpContext http,
                IDomainDataCache cache,
                CancellationToken cancellationToken) =>
            {
                int value = await cache.GetOrSetAsync(http, _ => Task.FromResult(42), cancellationToken);
                return Results.Json(new { Value = value });
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage _, string miss, string _) = await GetAsync(client, "/data-once");
            miss.Should().Contain("oc=miss").And.Contain("dc=miss").And.Contain("fa=run");

            AdminDomainCountersDto counters = app.Services
                .GetRequiredService<IAdminStatsCollector>()
                .GetRawSnapshot()
                .Domains
                .Single(d => d.Name == domain);
            counters.FactoryRuns.Should().Be(1);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptNormalization_PreservesHandlerHeader_AndSharesOutputCacheEntry()
    {
        string domain = "accept-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain, values =>
        {
            values[$"Cache:Domains:{domain}:VaryByAccept"] = "true";
            values[$"Cache:Domains:{domain}:AcceptNormalizationList:0"] = "application/json";
            values[$"Cache:Domains:{domain}:AcceptNormalizationList:1"] = "application/xml";
        });

        (HttpClient client, WebApplication app) = await StartAsync(config, endpoints =>
        {
            endpoints.MapGet("/accept", (HttpContext http, HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text(http.Request.Headers.Accept.ToString());
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage _, string firstCache, string firstBody) = await GetAsync(
                client,
                "/accept",
                new Dictionary<string, string> { ["Accept"] = "text/html, application/json;q=0.9" });
            (HttpResponseMessage _, string secondCache, string secondBody) = await GetAsync(
                client,
                "/accept",
                new Dictionary<string, string> { ["Accept"] = "application/json" });

            firstCache.Should().Contain("oc=miss");
            secondCache.Should().Contain("oc=miss");
            firstBody.Should().Contain("text/html").And.Contain("application/json").And.Contain("q=0.9");
            firstBody.Should().NotBe("application/json");
            secondBody.Should().Be("application/json");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task EmptyDynamicDomain_DoesNotStoreInOutputCache()
    {
        (HttpClient? client, WebApplication? app) = await StartAsync(DefaultsOnlyConfig(), a =>
        {
            a.MapGet("/data", (HttpContext http, HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("body");
            }).CacheOutputWithDomain(http => http.Request.Query["tenant"].ToString());
        });

        try
        {
            (HttpResponseMessage r1, string x1, string b1) = await GetAsync(client, "/data");
            r1.IsSuccessStatusCode.Should().BeTrue();
            b1.Should().Be("body");
            x1.Should().NotContain("oc=hit");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(1);

            (HttpResponseMessage r2, string x2, string _) = await GetAsync(client, "/data");
            r2.IsSuccessStatusCode.Should().BeTrue();
            x2.Should().NotContain("oc=hit",
                "empty dynamic domain must disable Output Cache (base policy is NoCache; domain policy opts in)");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task EmptyTemplateDomain_DoesNotStore_ResolvedTenantCaches()
    {
        Dictionary<string, string?> config = DefaultsOnlyConfig();
        config["Cache:Domains:acme:Version"] = "v1";
        (HttpClient? client, WebApplication? app) = await StartAsync(config, a =>
        {
            a.MapGet("/t", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("ok");
            }).CacheOutputWithDomainTemplate("{query:tenant}");
        });

        try
        {
            await GetAsync(client, "/t");
            await GetAsync(client, "/t");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2,
                "missing {query:tenant} resolves to empty and must not enable Output Cache");

            (HttpResponseMessage miss, string xMiss, string _) = await GetAsync(client, "/t?tenant=acme");
            xMiss.Should().Contain("oc=miss");
            xMiss.Should().Contain("domain=acme");

            (HttpResponseMessage hit, string xHit, string _) = await GetAsync(client, "/t?tenant=acme");
            xHit.Should().Contain("oc=hit");
            miss.IsSuccessStatusCode.Should().BeTrue();
            hit.IsSuccessStatusCode.Should().BeTrue();
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(3);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnconfiguredDynamicDomain_FailsClosed()
    {
        (HttpClient? client, WebApplication? app) = await StartAsync(DefaultsOnlyConfig(), a =>
        {
            a.MapGet("/unconfigured", async (
                HttpContext http,
                IDomainDataCache cache,
                HitCounter hits,
                CancellationToken cancellationToken) =>
            {
                string body = await cache.GetOrSetAsync(
                    http,
                    _ =>
                    {
                        hits.Increment();
                        return Task.FromResult("body");
                    },
                    cancellationToken);
                return Results.Text(body);
            }).CacheOutputWithDomain(http => http.Request.Query["tenant"].ToString());
        });

        try
        {
            (HttpResponseMessage first, string firstCache, string _) =
                await GetAsync(client, "/unconfigured?tenant=unknown");
            (HttpResponseMessage second, string secondCache, string _) =
                await GetAsync(client, "/unconfigured?tenant=unknown");

            first.Headers.CacheControl?.NoStore.Should().BeTrue();
            second.Headers.CacheControl?.NoStore.Should().BeTrue();
            firstCache.Should().Contain("domain=_").And.Contain("oc=bypass");
            secondCache.Should().Contain("domain=_").And.Contain("oc=bypass");
            firstCache.Should().NotContain("unknown");
            secondCache.Should().NotContain("unknown");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CacheControl_NoStorey_DoesNotBypassOutputCache()
    {
        string domain = "cc-storey-" + Guid.NewGuid().ToString("N");
        (HttpClient? client, WebApplication? app) = await StartAsync(BaseConfig(domain), a =>
        {
            a.MapGet("/x", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("body");
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            Dictionary<string, string> lookalike = new() { ["Cache-Control"] = "no-storey" };

            (HttpResponseMessage r1, string x1, string _) = await GetAsync(client, "/x", lookalike);
            x1.Should().Contain("oc=miss");
            x1.Should().NotContain("oc=bypass");

            (HttpResponseMessage r2, string x2, string _) = await GetAsync(client, "/x", lookalike);
            x2.Should().Contain("oc=hit", "no-storey is not the no-store directive name");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(1);
            r1.IsSuccessStatusCode.Should().BeTrue();
            r2.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CacheControl_MaxAgeEqualsNoStore_DoesNotBypassOutputCache()
    {
        string domain = "cc-maxage-" + Guid.NewGuid().ToString("N");
        (HttpClient? client, WebApplication? app) = await StartAsync(BaseConfig(domain), a =>
        {
            a.MapGet("/x", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("body");
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            Dictionary<string, string> headers = new() { ["Cache-Control"] = "max-age=no-store" };

            (HttpResponseMessage r1, string x1, string _) = await GetAsync(client, "/x", headers);
            x1.Should().Contain("oc=miss");

            (HttpResponseMessage r2, string x2, string _) = await GetAsync(client, "/x", headers);
            x2.Should().Contain("oc=hit", "no-store as a max-age value is not a directive name");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(1);
            r1.IsSuccessStatusCode.Should().BeTrue();
            r2.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Response_DoesNotWriteLastModified_StillWritesETag()
    {
        string domain = "lm-" + Guid.NewGuid().ToString("N");
        (HttpClient? client, WebApplication? app) = await StartAsync(BaseConfig(domain), a =>
        {
            a.MapGet("/x", () => Results.Text("ok")).CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage res, string xCache, string body) = await GetAsync(client, "/x");
            res.IsSuccessStatusCode.Should().BeTrue();
            body.Should().Be("ok");
            xCache.Should().Contain("oc=miss");

            res.Content.Headers.LastModified.Should().BeNull();
            res.Content.Headers.Contains("Last-Modified").Should().BeFalse();
            (res.Headers.ETag?.Tag ?? (res.Headers.TryGetValues("ETag", out IEnumerable<string>? v)
                ? v.FirstOrDefault()
                : null)).Should().NotBeNullOrWhiteSpace("default ETagMode.Version still writes ETag");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task TrackingQuery_GaAndGaPrefix_DoNotFragment_GameDoes()
    {
        string domain = "trk-" + Guid.NewGuid().ToString("N");
        (HttpClient? client, WebApplication? app) = await StartAsync(BaseConfig(domain), a =>
        {
            a.MapGet("/x", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("same");
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage r1, string x1, string _) = await GetAsync(client, "/x");
            x1.Should().Contain("oc=miss");

            (HttpResponseMessage rGa, string xGa, string _) = await GetAsync(client, "/x?_ga=GA1.2.xxx");
            xGa.Should().Contain("oc=hit", "exact _ga is tracking");

            (HttpResponseMessage rGa4, string xGa4, string _) = await GetAsync(client, "/x?_ga_ABC=1");
            xGa4.Should().Contain("oc=hit", "_ga_* is GA4 tracking");

            (HttpResponseMessage rGl, string xGl, string _) = await GetAsync(client, "/x?_gl=1");
            xGl.Should().Contain("oc=hit", "exact _gl is tracking");

            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(1);

            (HttpResponseMessage rGame, string xGame, string bGame) = await GetAsync(client, "/x?_game=1");
            bGame.Should().Be("same");
            xGame.Should().Contain("oc=miss", "_game is not a tracking prefix of _ga");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);

            (HttpResponseMessage rGlobal, string xGlobal, string _) = await GetAsync(client, "/x?_global=1");
            xGlobal.Should().Contain("oc=miss", "_global is not _gl / _gl_*");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(3);

            r1.IsSuccessStatusCode.Should().BeTrue();
            rGa.IsSuccessStatusCode.Should().BeTrue();
            rGa4.IsSuccessStatusCode.Should().BeTrue();
            rGl.IsSuccessStatusCode.Should().BeTrue();
            rGame.IsSuccessStatusCode.Should().BeTrue();
            rGlobal.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DefaultEncodingNormalization_PreservesCompositeNegotiation()
    {
        string domain = "enc-" + Guid.NewGuid().ToString("N");
        (HttpClient? client, WebApplication? app) = await StartAsync(BaseConfig(domain), a =>
        {
            a.MapGet("/x", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("enc-body");
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            Dictionary<string, string> gzip = new() { ["Accept-Encoding"] = "gzip" };
            Dictionary<string, string> gzipDeflate = new() { ["Accept-Encoding"] = "gzip, deflate" };
            Dictionary<string, string> br = new() { ["Accept-Encoding"] = "br" };

            (HttpResponseMessage r1, string x1, string _) = await GetAsync(client, "/x", gzip);
            x1.Should().Contain("oc=miss");

            (HttpResponseMessage r2, string x2, string _) = await GetAsync(client, "/x", gzipDeflate);
            x2.Should().Contain("oc=miss",
                "another content coding may be selected by the endpoint");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);

            (HttpResponseMessage r3, string x3, string _) = await GetAsync(client, "/x", br);
            x3.Should().Contain("oc=miss", "br is a different preferred encoding than gzip");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(3);
            r1.IsSuccessStatusCode.Should().BeTrue();
            r2.IsSuccessStatusCode.Should().BeTrue();
            r3.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptNormalization_ZeroQualityDoesNotCollapseOntoRejectedType()
    {
        string domain = "acc-q-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain, values =>
        {
            values[$"Cache:Domains:{domain}:VaryByAccept"] = "true";
            values[$"Cache:Domains:{domain}:AcceptNormalizationList:0"] = "application/json";
            values[$"Cache:Domains:{domain}:AcceptNormalizationList:1"] = "application/xml";
        });

        (HttpClient client, WebApplication app) = await StartAsync(config, endpoints =>
        {
            endpoints.MapGet("/accept-q", (HttpContext http, HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text(http.Request.Headers.Accept.ToString());
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage _, string jsonPref, string jsonBody) = await GetAsync(
                client,
                "/accept-q",
                new Dictionary<string, string> { ["Accept"] = "application/json;q=1, application/xml;q=0.1" });
            (HttpResponseMessage xmlRes, string xmlPref, string xmlBody) = await GetAsync(
                client,
                "/accept-q",
                new Dictionary<string, string> { ["Accept"] = "application/json;q=0, application/xml;q=1" });

            jsonPref.Should().Contain("oc=miss");
            xmlPref.Should().Contain("oc=miss",
                "q=0 must not let application/json win the prefer-list collapse");
            xmlBody.Should().NotBe(jsonBody);
            xmlRes.Headers.Vary.Should().Contain("Accept");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task VaryByAcceptLanguage_EmitsVaryWhenHeaderAbsent()
    {
        string domain = "al-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain, values =>
        {
            values[$"Cache:Domains:{domain}:VaryByAcceptLanguage"] = "true";
        });

        (HttpClient client, WebApplication app) = await StartAsync(config, endpoints =>
        {
            endpoints.MapGet("/lang", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("body");
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            (HttpResponseMessage res, string cache, string _) = await GetAsync(client, "/lang");
            cache.Should().Contain("oc=miss");
            res.Headers.Vary.Should().Contain("Accept-Language");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetAndPost_SameUrl_DoNotShareDataCacheEntry()
    {
        string domain = "method-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain);

        async Task<IResult> Handler(HttpContext http, IDomainDataCache cache, CancellationToken cancellationToken)
        {
            string value = await cache.GetOrSetAsync(
                http,
                _ => Task.FromResult(http.Request.Method + " payload"),
                cancellationToken);
            return Results.Text(value);
        }

        (HttpClient client, WebApplication app) = await StartAsync(config, endpoints =>
        {
            endpoints.MapGet("/resource", Handler).CacheOutputWithDomain(domain);
            endpoints.MapPost("/resource", Handler)
                .CacheOutputWithDomain(domain)
                .WithCacheIdentity(["POST"], CacheIdentities.Url);
        });

        try
        {
            (HttpResponseMessage getRes, string getCache, string getBody) = await GetAsync(client, "/resource");
            using HttpRequestMessage postReq = new(HttpMethod.Post, "/resource");
            HttpResponseMessage postRes = await client.SendAsync(postReq, TestContext.Current.CancellationToken);
            string postBody = await postRes.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            string postCache = postRes.Headers.TryGetValues("X-CacheOrchestrator", out IEnumerable<string>? values)
                ? string.Join(",", values)
                : string.Empty;

            getRes.IsSuccessStatusCode.Should().BeTrue();
            postRes.IsSuccessStatusCode.Should().BeTrue();
            getCache.Should().Contain("dc=miss");
            getBody.Should().Be("GET payload");
            postBody.Should().Be("POST payload",
                "POST must not reuse the GET Data Cache entry for the same URL");
            postCache.Should().Contain("dc=miss")
                .And.NotContain("dc=hit", "URL-shaped Data Cache keys must include the HTTP method");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcceptPreferList_JsonDoesNotMatchJsonSeq()
    {
        string domain = "acc-" + Guid.NewGuid().ToString("N");
        Dictionary<string, string?> config = BaseConfig(domain, d =>
        {
            d[$"Cache:Domains:{domain}:VaryByAccept"] = "true";
            d[$"Cache:Domains:{domain}:AcceptNormalizationList:0"] = "application/json";
        });

        (HttpClient? client, WebApplication? app) = await StartAsync(config, a =>
        {
            a.MapGet("/x", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("n" + hits.Count);
            }).CacheOutputWithDomain(domain);
        });

        try
        {
            Dictionary<string, string> json = new() { ["Accept"] = "application/json" };
            Dictionary<string, string> jsonSeq = new() { ["Accept"] = "application/json-seq" };

            (HttpResponseMessage r1, string x1, string b1) = await GetAsync(client, "/x", json);
            x1.Should().Contain("oc=miss");
            b1.Should().Be("n1");

            (HttpResponseMessage rHit, string xHit, string bHit) = await GetAsync(client, "/x", json);
            xHit.Should().Contain("oc=hit");
            bHit.Should().Be("n1");

            (HttpResponseMessage rSeq, string xSeq, string bSeq) = await GetAsync(client, "/x", jsonSeq);
            xSeq.Should().Contain("oc=miss", "json-seq is not a substring match of application/json");
            bSeq.Should().Be("n2");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);
            r1.IsSuccessStatusCode.Should().BeTrue();
            rHit.IsSuccessStatusCode.Should().BeTrue();
            rSeq.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidateEntity_OpaqueResourceId_IsAccepted_AndDoesNotAffectOtherEntity()
    {
        string domain = "skip-id-" + Guid.NewGuid().ToString("N");
        (HttpClient? client, WebApplication? app) = await StartAsync(BaseConfig(domain), a =>
        {
            a.MapGet("/p/{id}", (string id, HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("p-" + id);
            }).CacheOutputWithDomain(domain, resourceRouteKey: "id", entityKind: "items");
        });

        try
        {
            await GetAsync(client, "/p/1");
            (HttpResponseMessage hit, string xHit, string _) = await GetAsync(client, "/p/1");
            xHit.Should().Contain("oc=hit");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(1);

            CacheInvalidationResult invalidated = await app.Services
                .GetRequiredService<ICacheOrchestratorInvalidator>()
                .InvalidateEntityAsync(domain, "items", "!!!", TestContext.Current.CancellationToken);

            invalidated.IsSkipped.Should().BeFalse();
            invalidated.Succeeded.Should().BeTrue();

            (HttpResponseMessage still, string xStill, string _) = await GetAsync(client, "/p/1");
            xStill.Should().Contain("oc=hit");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(1);
            hit.IsSuccessStatusCode.Should().BeTrue();
            still.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidateDomainsAsync_PurgesBoth_AndNotifiesBatchOnce()
    {
        string a = "obs-a-" + Guid.NewGuid().ToString("N");
        string b = "obs-b-" + Guid.NewGuid().ToString("N");
        RecordingObserver observer = new();

        Dictionary<string, string?> config = BaseConfig(a);
        foreach (KeyValuePair<string, string?> kv in BaseConfig(b))
            config[kv.Key] = kv.Value;

        (HttpClient? client, WebApplication? app) = await StartAsync(config, app =>
        {
            app.MapGet("/a", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("a");
            }).CacheOutputWithDomain(a);
            app.MapGet("/b", (HitCounter hits) =>
            {
                hits.Increment();
                return Results.Text("b");
            }).CacheOutputWithDomain(b);
        }, services => services.AddSingleton<ICacheInvalidationObserver>(observer));

        try
        {
            await GetAsync(client, "/a");
            await GetAsync(client, "/b");
            (await GetAsync(client, "/a")).CacheOrchestratorHeader.Should().Contain("oc=hit");
            (await GetAsync(client, "/b")).CacheOrchestratorHeader.Should().Contain("oc=hit");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(2);

            CacheInvalidationResult result = await app.Services
                .GetRequiredService<ICacheOrchestratorInvalidator>()
                .InvalidateDomainsAsync([a, b], TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeTrue();
            result.Parts.Should().HaveCount(2);

            lock (observer.Before)
            {
                observer.Before.Should().Equal(CacheInvalidationKind.Domains);
            }

            lock (observer.After)
            {
                observer.After.Should().Equal(CacheInvalidationKind.Domains);
            }

            (await GetAsync(client, "/a")).CacheOrchestratorHeader.Should().Contain("oc=miss");
            (await GetAsync(client, "/b")).CacheOrchestratorHeader.Should().Contain("oc=miss");
            app.Services.GetRequiredService<HitCounter>().Count.Should().Be(4);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}
