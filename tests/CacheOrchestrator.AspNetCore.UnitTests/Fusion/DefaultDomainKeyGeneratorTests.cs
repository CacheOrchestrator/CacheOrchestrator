using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using System.IO.Hashing;
using System.Text;

namespace CacheOrchestrator.AspNetCore.UnitTests.Fusion;

public class DefaultDomainKeyGeneratorTests
{
    private readonly DefaultDomainKeyGenerator _sut = new();

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Generate_IdentityValueOrder_DoesNotChangeKey(int count)
    {
        DefaultHttpContext first = CreateHttpContext();
        DefaultHttpContext second = CreateHttpContext();
        KeyValuePair<string, string>[] entries =
        [
            new("d", "4"),
            new("b", "2"),
            new("a", "1"),
            new("c", "3")
        ];

        CacheIdentityApplicator.StoreOnFeature(
            first,
            new CacheIdentityMaterial(entries.Take(count)),
            bypass: false,
            NullLogger.Instance);
        CacheIdentityApplicator.StoreOnFeature(
            second,
            new CacheIdentityMaterial(entries.Take(count).Reverse()),
            bypass: false,
            NullLogger.Instance);

        _sut.Generate(CreateConfig(), first).Should().Be(_sut.Generate(CreateConfig(), second));
    }

    // =========================
    // Determinism & basic behaviour
    // =========================

    [Fact]
    public void Generate_SameRequest_ProducesSameKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        DefaultHttpContext http = CreateHttpContext();

        string key1 = _sut.Generate(cfg, http);
        string key2 = _sut.Generate(cfg, http);

        key1.Should().Be(key2);
    }

    [Fact]
    public void Generate_IncludesDomainAndVersion()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "catalog");
        DefaultHttpContext http = CreateHttpContext();

        string key = _sut.Generate(cfg, http);

        key.Should().MatchRegex($"^co3:catalog:{cfg.VersionHex}:u:[0-9a-f]{{16}}$");
    }

    [Fact]
    public void Generate_WithResourceId_UsesEntityShapeMarkerAndIsStable()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "products");
        DefaultHttpContext http = CreateHttpContext();
        http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "items", ResourceId = "42" });

        string key1 = _sut.Generate(cfg, http);
        string key2 = _sut.Generate(cfg, http);

        key1.Should().Be(key2);
        key1.Should().MatchRegex($"^co3:products:{cfg.VersionHex}:e:[0-9a-f]{{16}}$");
    }

    [Fact]
    public void Generate_DifferentEntityKinds_SameResourceId_ProduceDifferentKeys()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "store");
        DefaultHttpContext product = CreateHttpContext();
        product.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "products", ResourceId = "1" });
        DefaultHttpContext asset = CreateHttpContext();
        asset.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "assets", ResourceId = "1" });

        string productKey = _sut.Generate(cfg, product);
        string assetKey = _sut.Generate(cfg, asset);

        productKey.Should().NotBe(assetKey);
        productKey.Should().Contain(":e:");
        assetKey.Should().Contain(":e:");
        productKey.Should().NotContain(":id:");
        assetKey.Should().NotContain(":id:");
    }

    [Fact]
    public void Generate_ResourceIdWithoutEntityKind_UsesUrlShape()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "products");
        DefaultHttpContext http = CreateHttpContext();
        http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { ResourceId = "42" });

        string key = _sut.Generate(cfg, http);

        key.Should().MatchRegex($"^co3:products:{cfg.VersionHex}:u:[0-9a-f]{{16}}$");
    }

    [Fact]
    public void Generate_DifferentResourceIds_ProduceDifferentKeys()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "products");
        DefaultHttpContext http1 = CreateHttpContext();
        http1.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "items", ResourceId = "1" });
        DefaultHttpContext http2 = CreateHttpContext();
        http2.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "items", ResourceId = "2" });

        _sut.Generate(cfg, http1).Should().NotBe(_sut.Generate(cfg, http2));
    }

    [Fact]
    public void Generate_DifferentDomain_ProducesDifferentKey()
    {
        DefaultHttpContext http = CreateHttpContext();

        string key1 = _sut.Generate(CreateConfig(domain: "products"), http);
        string key2 = _sut.Generate(CreateConfig(domain: "orders"), http);

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_DifferentVersion_ProducesDifferentKey()
    {
        DefaultHttpContext http = CreateHttpContext();
        DomainHttpCacheOptions cfg1 = CreateConfig();
        _ = CreateConfig();
        var cfg2 = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions(cfg1.Domain, "2"),
            DataCacheVaryOnEncoding = false,
            DataCacheVaryOnPublicAddress = false
        };

        string key1 = _sut.Generate(cfg1, http);
        string key2 = _sut.Generate(cfg2, http);

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_WhenDataKeyPolicyChanges_UsesDifferentGeneration()
    {
        DefaultHttpContext http = CreateHttpContext();
        var first = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            DataCacheVaryOnPublicAddress = false
        };
        var second = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            DataCacheVaryOnPublicAddress = true
        };
        DomainCachePolicyGeneration.Apply(first);
        DomainCachePolicyGeneration.Apply(second);

        _sut.Generate(first, http).Should().NotBe(_sut.Generate(second, http));
    }

    [Fact]
    public void PolicyGeneration_WhenOnlyTtlChanges_RemainsStable()
    {
        var first = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            OutputTtl = TimeSpan.FromSeconds(600),
            ClientTtlSeconds = 600
        };
        var second = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            OutputTtl = TimeSpan.FromSeconds(300),
            ClientTtlSeconds = 300
        };
        DomainCachePolicyGeneration.Apply(first);
        DomainCachePolicyGeneration.Apply(second);

        second.OutputCachePolicyGeneration.Should().Be(first.OutputCachePolicyGeneration);
        second.DataCachePolicyGeneration.Should().Be(first.DataCachePolicyGeneration);
    }

    // =========================
    // Path / Route
    // =========================

    [Fact]
    public void Generate_DifferentPath_ProducesDifferentKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        string key1 = _sut.Generate(cfg, CreateHttpContext(path: "/api/products"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(path: "/api/orders"));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_PathCase_IsPreserved()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        string upper = _sut.Generate(cfg, CreateHttpContext(path: "/api/Products"));
        string lower = _sut.Generate(cfg, CreateHttpContext(path: "/api/products"));

        upper.Should().NotBe(lower);
    }

    [Fact]
    public void Generate_EmptyPath_DoesNotThrow()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        DefaultHttpContext http = CreateHttpContext(path: "");

        Func<string> act = () => _sut.Generate(cfg, http);

        act.Should().NotThrow();
    }

    // =========================
    // Query parameters
    // =========================

    [Fact]
    public void Generate_DifferentQuery_ProducesDifferentKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        string key1 = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "1" }));
        string key2 = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "2" }));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_QueryValueCase_IsPreserved()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        string upper = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "ABC" }));
        string lower = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "abc" }));

        upper.Should().NotBe(lower);
    }

    [Fact]
    public void Generate_SingleCommaValue_DiffersFromTwoValues()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        string single = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "a,b" }));
        string multiple = _sut.Generate(
            cfg,
            CreateHttpContext(query: new() { ["id"] = new StringValues(["a", "b"]) }));

        single.Should().NotBe(multiple);
    }

    [Fact]
    public void Generate_OpaqueResourceIds_RemainDistinctInEntityHash()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "products");
        DefaultHttpContext slash = CreateHttpContext();
        slash.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature
        {
            EntityKind = "items",
            ResourceId = "A/B"
        });
        DefaultHttpContext space = CreateHttpContext();
        space.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature
        {
            EntityKind = "items",
            ResourceId = "A B"
        });

        string slashKey = _sut.Generate(cfg, slash);
        string spaceKey = _sut.Generate(cfg, space);

        slashKey.Should().NotBe(spaceKey);
        slashKey.Should().Contain(":e:");
        spaceKey.Should().Contain(":e:");
        slashKey.Should().NotContain("A/B");
        spaceKey.Should().NotContain("A B");
    }

    [Fact]
    public void Generate_QueryParameterOrder_DoesNotMatter()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        DefaultHttpContext http1 = CreateHttpContext(query: new()
        {
            ["b"] = "2",
            ["a"] = "1"
        });

        DefaultHttpContext http2 = CreateHttpContext(query: new()
        {
            ["a"] = "1",
            ["b"] = "2"
        });

        string key1 = _sut.Generate(cfg, http1);
        string key2 = _sut.Generate(cfg, http2);

        key1.Should().Be(key2);
    }

    [Fact]
    public void Generate_EmptyQuery_DoesNotThrow()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        DefaultHttpContext http = CreateHttpContext(query: []);

        Func<string> act = () => _sut.Generate(cfg, http);

        act.Should().NotThrow();
    }

    // =========================
    // Tracking parameters (must be ignored)
    // =========================

    [Theory]
    [InlineData("utm_source")]
    [InlineData("utm_medium")]
    [InlineData("utm_campaign")]
    [InlineData("utm_term")]
    [InlineData("utm_content")]
    [InlineData("fbclid")]
    [InlineData("gclid")]
    [InlineData("msclkid")]
    [InlineData("ttclid")]
    [InlineData("_ga")]
    [InlineData("_gl")]
    public void Generate_IgnoresTrackingParameter(string trackingParam)
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        DefaultHttpContext httpWithTracking = CreateHttpContext(query: new()
        {
            ["id"] = "42",
            [trackingParam] = "something"
        });

        DefaultHttpContext httpWithoutTracking = CreateHttpContext(query: new()
        {
            ["id"] = "42"
        });

        string key1 = _sut.Generate(cfg, httpWithTracking);
        string key2 = _sut.Generate(cfg, httpWithoutTracking);

        key1.Should().Be(key2);
    }

    [Fact]
    public void Generate_IgnoresMultipleTrackingParameters()
    {
        DomainHttpCacheOptions cfg = CreateConfig();

        DefaultHttpContext http1 = CreateHttpContext(query: new() { ["id"] = "42" });
        DefaultHttpContext http2 = CreateHttpContext(query: new()
        {
            ["id"] = "42",
            ["utm_source"] = "google",
            ["fbclid"] = "abc",
            ["gclid"] = "xyz",
            ["_ga"] = "GA1.2.xxx"
        });

        string key1 = _sut.Generate(cfg, http1);
        string key2 = _sut.Generate(cfg, http2);

        key1.Should().Be(key2);
    }

    [Fact]
    public void Generate_DoesNotTreatGameQueryAsTracking()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        string withGame = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "42", ["_game"] = "1" }));
        string without = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "42" }));

        withGame.Should().NotBe(without);
    }

    // =========================
    // Accept-Encoding
    // =========================

    [Fact]
    public void Generate_VaryOnEncoding_DifferentEncoding_ProducesDifferentKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig(varyOnEncoding: true);

        string key1 = _sut.Generate(cfg, CreateHttpContext(acceptEncoding: "gzip"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(acceptEncoding: "br"));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_VaryOnEncodingDisabled_IgnoresEncoding()
    {
        DomainHttpCacheOptions cfg = CreateConfig(varyOnEncoding: false);

        string key1 = _sut.Generate(cfg, CreateHttpContext(acceptEncoding: "gzip"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(acceptEncoding: "br"));

        key1.Should().Be(key2);
    }

    [Fact]
    public void Generate_WithAcceptNormalization_DoesNotLeaveMutatedRequestHeaders()
    {
        var cfg = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            DataCacheVaryOnEncoding = false,
            DataCacheVaryOnPublicAddress = false,
            VaryByAccept = true,
            AcceptNormalizationList = ["application/json", "application/xml"],
        };

        const string original = "text/html, application/json;q=0.9";
        DefaultHttpContext http = CreateHttpContext(accept: original);

        _ = _sut.Generate(cfg, http);

        http.Request.Headers.Accept.ToString().Should().Be(original);
    }

    [Fact]
    public void Generate_AcceptNormalization_DifferentNegotiation_ProducesDifferentKeys()
    {
        var cfg = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            DataCacheVaryOnEncoding = false,
            DataCacheVaryOnPublicAddress = false,
            VaryByAccept = true,
            AcceptNormalizationList = ["application/json", "application/xml"],
        };

        string key1 = _sut.Generate(cfg, CreateHttpContext(accept: "text/html, application/json;q=0.9"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(accept: "application/json"));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_WithExplicitUrlShape_IgnoresEntityWithoutMutatingFeature()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "products");
        DefaultHttpContext http = CreateHttpContext();
        http.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "items", ResourceId = "42" });
        string entityKey = _sut.Generate(cfg, http, DomainCacheKeyShape.Entity);
        string urlKey = _sut.Generate(cfg, http, DomainCacheKeyShape.Url);

        entityKey.Should().Contain(":e:");
        urlKey.Should().Contain(":u:");
        urlKey.Should().NotContain(":e:");
        http.Features.Get<ICacheOrchestratorFeature>()!.EntityKind.Should().Be("items");
        http.Features.Get<ICacheOrchestratorFeature>()!.ResourceId.Should().Be("42");
    }

    [Fact]
    public void Generate_UrlShape_DifferentHttpMethods_ProduceDifferentKeys()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        DefaultHttpContext get = CreateHttpContext(method: "GET");
        DefaultHttpContext post = CreateHttpContext(method: "POST");

        string getKey = _sut.Generate(cfg, get);
        string postKey = _sut.Generate(cfg, post);

        getKey.Should().NotBe(postKey);
        getKey.Should().Contain(":u:");
        postKey.Should().Contain(":u:");
    }

    [Fact]
    public void Generate_UrlShape_MethodCasing_DoesNotChangeKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        DefaultHttpContext upper = CreateHttpContext(method: "GET");
        DefaultHttpContext lower = CreateHttpContext(method: "get");

        _sut.Generate(cfg, upper).Should().Be(_sut.Generate(cfg, lower));
    }

    [Fact]
    public void Generate_EntityShape_IgnoresHttpMethod()
    {
        DomainHttpCacheOptions cfg = CreateConfig(domain: "products");
        DefaultHttpContext get = CreateHttpContext(method: "GET");
        get.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "items", ResourceId = "42" });
        DefaultHttpContext post = CreateHttpContext(method: "POST");
        post.Features.Set<ICacheOrchestratorFeature>(new CacheOrchestratorFeature { EntityKind = "items", ResourceId = "42" });

        _sut.Generate(cfg, get).Should().Be(_sut.Generate(cfg, post));
    }

    [Fact]
    public void Generate_VaryByAccept_DifferentAccept_ProducesDifferentKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig();
        cfg = new DomainHttpCacheOptions
        {
            CoreOptions = cfg.CoreOptions,
            DataCacheVaryOnEncoding = false,
            DataCacheVaryOnPublicAddress = false,
            VaryByAccept = true,
        };

        string key1 = _sut.Generate(cfg, CreateHttpContext(accept: "application/json"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(accept: "application/xml"));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_VaryByQueryKeysAllowlist_IgnoresOtherKeys()
    {
        var cfg = new DomainHttpCacheOptions
        {
            CoreOptions = CreateCoreOptions("products", "1"),
            DataCacheVaryOnEncoding = false,
            DataCacheVaryOnPublicAddress = false,
            VaryByQueryKeys = ["id"],
        };

        string key1 = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "1", ["page"] = "1" }));
        string key2 = _sut.Generate(cfg, CreateHttpContext(query: new() { ["id"] = "1", ["page"] = "99" }));

        key1.Should().Be(key2);
    }

    // =========================
    // Public address (scheme + host)
    // =========================

    [Fact]
    public void Generate_VaryOnPublicAddress_DifferentHost_ProducesDifferentKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig(varyOnPublicAddress: true);

        string key1 = _sut.Generate(cfg, CreateHttpContext(host: "shop1.example.com"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(host: "shop2.example.com"));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_VaryOnPublicAddress_DifferentScheme_ProducesDifferentKey()
    {
        DomainHttpCacheOptions cfg = CreateConfig(varyOnPublicAddress: true);

        string key1 = _sut.Generate(cfg, CreateHttpContext(scheme: "http"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(scheme: "https"));

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Generate_VaryOnPublicAddressDisabled_IgnoresHostAndScheme()
    {
        DomainHttpCacheOptions cfg = CreateConfig(varyOnPublicAddress: false);

        string key1 = _sut.Generate(cfg, CreateHttpContext(host: "shop1.example.com", scheme: "http"));
        string key2 = _sut.Generate(cfg, CreateHttpContext(host: "shop2.example.com", scheme: "https"));

        key1.Should().Be(key2);
    }

    // =========================
    // Helpers
    // =========================

    private static DomainHttpCacheOptions CreateConfig(
        string domain = "products",
        bool varyOnEncoding = false,
        bool varyOnPublicAddress = false) => new()
        {
            CoreOptions = CreateCoreOptions(domain, "1"),
            DataCacheVaryOnEncoding = varyOnEncoding,
            DataCacheVaryOnPublicAddress = varyOnPublicAddress
        };

    private static DomainCacheOptions CreateCoreOptions(string domain, string version) => new()
    {
        Domain = domain,
        Version = version,
        VersionHex = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(version)).ToString("x16"),
    };

    private static DefaultHttpContext CreateHttpContext(
        string path = "/api/products",
        Dictionary<string, StringValues>? query = null,
        string? acceptEncoding = null,
        string? accept = null,
        string scheme = "https",
        string host = "localhost",
        string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);

        if (query is not null)
            context.Request.Query = new QueryCollection(query);

        if (acceptEncoding is not null)
            context.Request.Headers.AcceptEncoding = acceptEncoding;

        if (accept is not null)
            context.Request.Headers.Accept = accept;

        return context;
    }
}
