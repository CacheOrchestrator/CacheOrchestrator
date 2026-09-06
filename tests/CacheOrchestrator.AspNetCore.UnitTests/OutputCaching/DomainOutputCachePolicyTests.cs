using CacheOrchestrator.Configuration;
using CacheOrchestrator.Entity;
using CacheOrchestrator.OutputCache;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;

namespace CacheOrchestrator.AspNetCore.UnitTests.OutputCaching;

public class DomainOutputCachePolicyTests
{
    // =========================
    // Constructor
    // =========================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenDomainIsNullOrWhitespace_Throws(string? domain)
    {
        Func<DomainOutputCachePolicy> act = () => new DomainOutputCachePolicy(domain!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenDomainResolverIsNull_Throws()
    {
        Func<DomainOutputCachePolicy> act = () => new DomainOutputCachePolicy((Func<HttpContext, string>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WhenEntityKindIsGarbage_Throws()
    {
        Func<DomainOutputCachePolicy> act = () => new DomainOutputCachePolicy("store", "id", "!!!");
        act.Should().Throw<ArgumentException>().WithParameterName("entityKind");
    }

    [Fact]
    public void Constructor_WhenEntityKindIsUsable_Normalizes()
    {
        var policy = new DomainOutputCachePolicy("store", "id", "Products");
        policy.EntityKind.Should().Be("products");
    }

    // =========================
    // CacheRequestAsync ï¿½ early exits
    // =========================

    [Fact]
    public async Task CacheRequestAsync_WhenDomainIsEmpty_DoesNotEnableCaching()
    {
        var policy = new DomainOutputCachePolicy(_ => string.Empty);
        (OutputCacheContext? context, DefaultHttpContext http) = CreateContext();

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("domain=_").And.Contain("oc=bypass");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenDynamicDomainIsUnknown_FailsClosedWithoutEchoingDomain()
    {
        var policy = new DomainOutputCachePolicy(_ => "tiles-attacker");
        (OutputCacheContext context, DefaultHttpContext http) = CreateContext();

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("domain=_");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().NotContain("tiles-attacker");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task CacheRequestAsync_WhenMethodIsNotGetOrHead_DoesNotEnableCaching(string method)
    {
        var policy = new DomainOutputCachePolicy("products");

        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext(method: method);

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequestAsync_WhenUserIsAuthenticated_DoesNotEnableCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();

        var identity = new System.Security.Claims.ClaimsIdentity(authenticationType: "test");
        http.User = new System.Security.Claims.ClaimsPrincipal(identity);

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store, no-cache, must-revalidate");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenAuthorizationHeaderPresent_DoesNotEnableCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Request.Headers.Authorization = "Bearer token";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store, no-cache, must-revalidate");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenAuthenticated_AndBypassDisabled_EnablesCachingAndVariesByUser()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(bypassWhenAuthenticated: false, varyOutputCacheByUser: true);

        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alice")],
            authenticationType: "test");
        http.User = new System.Security.Claims.ClaimsPrincipal(identity);

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.CacheVaryByRules.VaryByValues["auth-user"].Should().Be("u:alice");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenAuthorizationOnly_BypassDisabled_VariesByAuthHash()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(bypassWhenAuthenticated: false, varyOutputCacheByUser: true);
        http.Request.Headers.Authorization = "Bearer secret-token";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.CacheVaryByRules.VaryByValues["auth-user"].ToString().Should().StartWith("ah:");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenAuthenticated_BypassDisabled_VaryByUserFalse_DoesNotAddUserVary()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(bypassWhenAuthenticated: false, varyOutputCacheByUser: false);
        http.Request.Headers.Authorization = "Bearer map-api-key";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.CacheVaryByRules.VaryByValues.ContainsKey("auth-user").Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequestAsync_WhenOutputCacheDisabled_DoesNotEnableCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext(outputCacheEnabled: false);

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
    }

    [Fact]
    public async Task OnStarting_WhenOutputCacheDisabled_WritesOutputOff()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(outputCacheEnabled: false);

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("oc=off");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().NotContain("oc=bypass");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenRequestHasNoStore_DoesNotEnableCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Request.Headers.CacheControl = "no-store";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeFalse();
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store, no-cache, must-revalidate");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenCacheControlIsMaxAgeOnly_StillEnablesCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Request.Headers.CacheControl = "private, max-age=60";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
    }

    [Fact]
    public async Task CacheRequestAsync_WhenCacheControlValueLooksLikeNoStore_StillEnablesCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Request.Headers.CacheControl = "max-age=no-store";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
    }

    [Fact]
    public async Task CacheRequestAsync_WhenHead_EnablesCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext(method: "HEAD");

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.AllowCacheLookup.Should().BeTrue();
        context.AllowCacheStorage.Should().BeTrue();
    }

    // =========================
    // CacheRequestAsync ï¿½ happy path
    // =========================

    [Fact]
    public async Task CacheRequestAsync_WhenValidGetRequest_EnablesCaching()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext();

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.AllowCacheLookup.Should().BeTrue();
        context.AllowCacheStorage.Should().BeTrue();
        context.AllowLocking.Should().BeTrue();
    }

    [Fact]
    public async Task CacheRequestAsync_AddsDomainTag()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext();

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.Tags.Should().Contain("domain:products");
    }

    [Fact]
    public async Task CacheRequestAsync_SetsExpirationFromConfig()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext(outputTtlSeconds: 120);

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.ResponseExpirationTimeSpan.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task CacheRequestAsync_SetsETag()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(version: "v1");

        await policy.CacheRequestAsync(context, CancellationToken.None);

        ulong versionHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes("v1"));
        http.Response.Headers.ETag.ToString().Should().Be($"W/\"{versionHash:x16}\"");
    }

    [Fact]
    public async Task CacheRequestAsync_IgnoresTrackingQueryParametersInVary()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Request.QueryString = new QueryString("?id=1&utm_source=google&fbclid=abc");

        // Re-create query collection properly
        http.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["id"] = "1",
            ["utm_source"] = "google",
            ["fbclid"] = "abc"
        });

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.CacheVaryByRules.QueryKeys.Should().Contain("id");
        context.CacheVaryByRules.QueryKeys.Should().NotContain("utm_source");
        context.CacheVaryByRules.QueryKeys.Should().NotContain("fbclid");
    }

    [Fact]
    public async Task CacheRequestAsync_SetsDataVersionAndNamespacePrefix()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext _) = CreateContext(version: "v1");

        await policy.CacheRequestAsync(context, CancellationToken.None);

        ulong versionHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes("v1"));
        context.CacheVaryByRules.VaryByValues["cache-domain"].ToString().Should().Be("products");
        context.CacheVaryByRules.VaryByValues["data-version"].ToString().Should().Be($"{versionHash:x16}");
        context.CacheVaryByRules.VaryByValues.Should().ContainKey("cache-policy");
        context.CacheVaryByRules.CacheKeyPrefix.Should().Be("test-oc");
    }

    [Fact]
    public async Task CacheRequestAsync_WhenEntityRoute_AddsEntityTags()
    {
        var policy = new DomainOutputCachePolicy("store", "id", "products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(domain: "store");
        http.Request.RouteValues["id"] = "42";

        await policy.CacheRequestAsync(context, CancellationToken.None);

        context.Tags.Should().Contain("domain:store");
        context.Tags.Should().Contain("entity:store:products:42");
        context.Tags.Should().Contain("entitykind:store:products");
    }

    // =========================
    // ServeResponseAsync
    // =========================

    [Fact]
    public async Task ServeResponseAsync_WhenStatusNotCacheable_DisablesStorage()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Response.StatusCode = 500;

        // Simulate that EnsureConfig already ran
        DomainHttpCacheOptions cfg = CreateEffectiveConfig();
        http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { DomainOptions = cfg });
        context.AllowCacheStorage = true;

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task ServeResponseAsync_WhenSetCookiePresent_DisablesStorage()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Response.StatusCode = 200;
        http.Response.Headers.SetCookie = "session=abc";

        DomainHttpCacheOptions cfg = CreateEffectiveConfig();
        http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { DomainOptions = cfg });
        context.AllowCacheStorage = true;

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task ServeResponseAsync_WhenStatus200AndNoSensitiveHeaders_KeepsStorageEnabled()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();
        http.Response.StatusCode = 200;

        DomainHttpCacheOptions cfg = CreateEffectiveConfig();
        http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { DomainOptions = cfg });
        context.AllowCacheStorage = true;

        await policy.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }

    // =========================
    // OnStarting headers (X-CacheOrchestrator, schedule, client)
    // =========================

    [Fact]
    public async Task ServeFromCacheAsync_ThenOnStarting_WritesHitWithoutData()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext();

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await policy.ServeFromCacheAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        string xcache = http.Response.Headers["X-CacheOrchestrator"].ToString();
        xcache.Should().Contain("oc=hit");
        xcache.Should().NotContain("dc=");
        xcache.Should().NotContain("fa=");
        xcache.Should().NotContain("ms=");
        xcache.Should().Contain("phase=");
        xcache.Should().Contain("domain=products");
    }

    [Fact]
    public async Task OnStarting_ContributesFinalizedDynamicEntityTags()
    {
        ICacheResponseContributor contributor = Substitute.For<ICacheResponseContributor>();
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(contributor: contributor);

        await policy.CacheRequestAsync(context, TestContext.Current.CancellationToken);
        ICacheOrchestratorFeature feature = http.Features.Get<ICacheOrchestratorFeature>()!;
        feature.PendingEntityFootprint = new EntityFootprint(
            new EntityRef("products", "42"),
            dependsOn: [new EntityRef("categories", "7")]);
        CacheResponseTagStaging.Update(http);
        await FlushHeadersAsync(http);

        await contributor.Received(1).ContributeAsync(
            Arg.Is<CacheResponseContext>(response =>
                response.OutputCacheResult == OutputCacheResult.Miss
                && response.SharedCacheEligible
                && response.Tags.Contains("domain:products")
                && response.Tags.Contains("entity:products:products:42")
                && response.Tags.Contains("entity:products:categories:7")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnStarting_ThenServeResponse_ContributesOnlyOnce()
    {
        ICacheResponseContributor contributor = Substitute.For<ICacheResponseContributor>();
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(contributor: contributor);

        await policy.CacheRequestAsync(context, TestContext.Current.CancellationToken);
        await FlushHeadersAsync(http);
        await policy.ServeResponseAsync(context, TestContext.Current.CancellationToken);

        await contributor.Received(1).ContributeAsync(
            Arg.Any<CacheResponseContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnStarting_WhenSetCookiePresent_ContributesAsNotSharedCacheEligible()
    {
        ICacheResponseContributor contributor = Substitute.For<ICacheResponseContributor>();
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(contributor: contributor);

        await policy.CacheRequestAsync(context, TestContext.Current.CancellationToken);
        http.Response.Headers.SetCookie = "session=abc";
        await FlushHeadersAsync(http);

        await contributor.Received(1).ContributeAsync(
            Arg.Is<CacheResponseContext>(response => !response.SharedCacheEligible),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ServeResponse_ThenOnStarting_ContributesOnlyOnce()
    {
        ICacheResponseContributor contributor = Substitute.For<ICacheResponseContributor>();
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(contributor: contributor);

        await policy.CacheRequestAsync(context, TestContext.Current.CancellationToken);
        await policy.ServeResponseAsync(context, TestContext.Current.CancellationToken);
        await FlushHeadersAsync(http);

        await contributor.Received(1).ContributeAsync(
            Arg.Any<CacheResponseContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnStarting_WhenHoldSchedule_WritesFloorMaxAgeAndPhaseHold()
    {
        var schedule = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddMinutes(5);
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(
            scheduledUpdateUtc: schedule,
            clientTtlSeconds: 3600,
            clientTtlMinSeconds: 90,
            timeProvider: new FixedTimeProvider(now));

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        http.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=90");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("phase=hold");
    }

    [Fact]
    public async Task OnStarting_WhenFarFromSchedule_WritesMaxTtlAndPhaseCalm()
    {
        var schedule = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddHours(-2);
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(
            scheduledUpdateUtc: schedule,
            clientTtlSeconds: 3600,
            clientTtlMinSeconds: 90,
            timeProvider: new FixedTimeProvider(now));

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        http.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=3600");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("phase=calm");
    }

    [Fact]
    public async Task OnStarting_WhenMidwayToSchedule_WritesLinearMaxAgeAndPhaseApproaching()
    {
        var schedule = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = schedule.AddSeconds(-1800);
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(
            scheduledUpdateUtc: schedule,
            clientTtlSeconds: 3600,
            clientTtlMinSeconds: 90,
            timeProvider: new FixedTimeProvider(now));

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        http.Response.Headers.CacheControl.ToString().Should().Be("public, max-age=1800");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("phase=approaching");
    }

    [Fact]
    public async Task OnStarting_WhenAuthenticatedAndPublic_ForcesPrivateClientHeader()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(bypassWhenAuthenticated: false);
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")],
            authenticationType: "test"));

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        http.Response.Headers.CacheControl.ToString().Should().StartWith("private, max-age=");
        http.Response.Headers["X-CacheOrchestrator"].ToString().Should().Contain("client=private");
    }

    [Fact]
    public async Task OnStarting_WhenEmitDiagnosticsHeadersFalse_OmitsCacheOrchestratorHeader()
    {
        var policy = new DomainOutputCachePolicy("products");
        (OutputCacheContext? context, DefaultHttpContext? http) = CreateContext(
            httpOptions: new CacheOrchestratorHttpOptions { EmitDiagnosticsHeaders = false });

        await policy.CacheRequestAsync(context, CancellationToken.None);
        await FlushHeadersAsync(http);

        http.Response.Headers.ContainsKey("X-CacheOrchestrator").Should().BeFalse();
        http.Response.Headers.CacheControl.ToString().Should().Contain("max-age=");
    }

    // =========================
    // Helpers
    // =========================

    private static Task FlushHeadersAsync(DefaultHttpContext http)
    {
        http.Response.StatusCode = 200;
        return http.Response.StartAsync();
    }

    private static (OutputCacheContext context, DefaultHttpContext http) CreateContext(
        string method = "GET",
        bool outputCacheEnabled = true,
        int outputTtlSeconds = 60,
        string? version = null,
        bool bypassWhenAuthenticated = true,
        bool varyOutputCacheByUser = true,
        string domain = "products",
        DateTimeOffset? scheduledUpdateUtc = null,
        int clientTtlSeconds = 60,
        int clientTtlMinSeconds = 60,
        TimeProvider? timeProvider = null,
        CacheOrchestratorHttpOptions? httpOptions = null,
        ICacheResponseContributor? contributor = null)
    {
        var http = new DefaultHttpContext();
        var responseFeature = new OnStartingResponseFeature();
        http.Features.Set<IHttpResponseFeature>(responseFeature);
        http.Features.Set<IHttpResponseBodyFeature>(responseFeature);
        http.Request.Method = method;
        http.Request.Path = "/api/products";

        DomainHttpCacheOptions cfg = CreateEffectiveConfig(
            outputCacheEnabled,
            outputTtlSeconds,
            version,
            bypassWhenAuthenticated,
            varyOutputCacheByUser,
            domain,
            scheduledUpdateUtc,
            clientTtlSeconds,
            clientTtlMinSeconds);

        IRequestDomainCacheOptions domainConfig = Substitute.For<IRequestDomainCacheOptions>();
        domainConfig.EnsureDomainOptions(http, Arg.Any<string>()).Returns(call =>
        {
            http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { DomainOptions = cfg });
            return cfg;
        });

        var services = new ServiceCollection();
        services.AddSingleton(domainConfig);
        services.AddSingleton<CacheVaryMaterializer>();
        services.AddSingleton(typeof(ILogger<DomainOutputCachePolicy>), NullLogger<DomainOutputCachePolicy>.Instance);
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        if (contributor is not null)
        {
            services.AddSingleton(contributor);
        }
        IOptionsMonitor<CacheOrchestratorHttpOptions> monitor =
            new FixedOptionsMonitor<CacheOrchestratorHttpOptions>(
                httpOptions ?? new CacheOrchestratorHttpOptions());
        services.AddSingleton(monitor);

        http.RequestServices = services.BuildServiceProvider();

        var context = new OutputCacheContext
        {
            HttpContext = http,
            // Simulate the ASP.NET base policy already enabling GET/HEAD caching.
            EnableOutputCaching = true
        };

        return (context, http);
    }

    private static DomainHttpCacheOptions CreateEffectiveConfig(
        bool outputCacheEnabled = true,
        int outputTtlSeconds = 60,
        string? version = null,
        bool bypassWhenAuthenticated = true,
        bool varyOutputCacheByUser = true,
        string domain = "products",
        DateTimeOffset? scheduledUpdateUtc = null,
        int clientTtlSeconds = 60,
        int clientTtlMinSeconds = 60) => new()
        {
            CoreOptions = new DomainCacheOptions
            {
                Domain = domain,
                Version = version ?? "1",
                VersionHex = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(version ?? "1")).ToString("x16"),
            },
            OutputCacheEnabled = outputCacheEnabled,
            AuthBypassMode = bypassWhenAuthenticated
                ? AuthBypassMode.AuthenticatedOrAuthorization
                : AuthBypassMode.Never,
            VaryOutputCacheByUser = varyOutputCacheByUser,
            OutputTtl = TimeSpan.FromSeconds(outputTtlSeconds),
            ETag = new StringValues($"W/\"{XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(version ?? "1")):x16}\""),
            CacheableStatusCodes = [200],
            ClientCacheability = ClientCacheability.Public,
            ClientTtlSeconds = clientTtlSeconds,
            ClientTtlMinSeconds = clientTtlMinSeconds,
            ScheduledUpdateUtc = scheduledUpdateUtc,
            ClientMustRevalidateNearUpdate = false,
            OutputCacheNamespace = "test-oc",
            EncodingNormalizationList = null,
            ClientForcePrivateWhenAuthenticated = true,
        };

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    /// <summary>
    /// DefaultHttpContext's stock <see cref="IHttpResponseFeature.OnStarting"/> is a no-op.
    /// This feature stores callbacks and fires them from <see cref="StartAsync"/>, matching Kestrel
    /// (reverse order, <see cref="HasStarted"/> becomes true after callbacks so headers can still be set).
    /// </summary>
    private sealed class OnStartingResponseFeature : IHttpResponseFeature, IHttpResponseBodyFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted { get; private set; }
        public Stream Stream => Body;
        public PipeWriter Writer => field ??= PipeWriter.Create(Body);

        public void OnStarting(Func<object, Task> callback, object state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (HasStarted)
                throw new InvalidOperationException("Headers already sent.");
            _onStarting.Add((callback, state));
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (HasStarted)
                return;

            for (int i = _onStarting.Count - 1; i >= 0; i--)
            {
                (Func<object, Task> callback, object state) = _onStarting[i];
                await callback(state);
            }

            HasStarted = true;
        }

        public Task CompleteAsync() => Task.CompletedTask;

        public void DisableBuffering()
        {
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
