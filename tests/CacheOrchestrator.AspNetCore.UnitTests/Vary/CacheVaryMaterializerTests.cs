using CacheOrchestrator.Configuration;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Claims;
using System.Text;

namespace CacheOrchestrator.AspNetCore.UnitTests.Vary;

public class CacheVaryMaterializerTests
{
    [Fact]
    public void Build_NoVaryMaterial_ReusesImmutableEmptyResult()
    {
        var materializer = new CacheVaryMaterializer();
        DefaultHttpContext first = CreateHttp();
        DefaultHttpContext second = CreateHttp();
        DomainHttpCacheOptions options = CreateOptions();

        CacheVaryMaterial firstResult = materializer.Build(first, options, CacheVarySurface.DataCache);
        CacheVaryMaterial secondResult = materializer.Build(second, options, CacheVarySurface.DataCache);

        firstResult.Should().BeSameAs(secondResult);
        firstResult.HeaderNames.Should().BeEmpty();
        firstResult.Values.Should().BeEmpty();
        firstResult.QueryKeys.Should().BeEmpty();
    }

    [Theory]
    [InlineData("kratko-ž")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void HashSegment_MatchesUtf8XxHash3(string value)
    {
        string expected = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(value))
            .ToString("x16", CultureInfo.InvariantCulture);

        CacheVaryMaterializer.HashSegment(value).Should().Be(expected);
    }

    [Fact]
    public void Build_DefaultOptions_OutputCache_UsesNormalizedEncodingValue()
    {
        DefaultHttpContext http = CreateHttp(acceptEncoding: "gzip");
        DomainHttpCacheOptions opts = CreateOptions();

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.HeaderNames.Should().NotContain(HeaderNames.AcceptEncoding);
        material.Values["normalized:accept-encoding"].Should().Be("gzip");
        material.ResponseVaryHeaderNames.Should().Contain(HeaderNames.AcceptEncoding);
        material.Values.Should().NotContainKey("auth-user");
    }

    [Fact]
    public void Build_DefaultOptions_Fusion_UsesNormalizedEncodingValueWhenFlagOn()
    {
        DefaultHttpContext http = CreateHttp(acceptEncoding: "br");
        DomainHttpCacheOptions opts = CreateOptions(varyEncoding: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        material.HeaderNames.Should().NotContain(HeaderNames.AcceptEncoding);
        material.Values["normalized:accept-encoding"].Should().Be("br");
    }

    [Fact]
    public void Build_VaryByAccept_IncludesAcceptHeader()
    {
        DefaultHttpContext http = CreateHttp(accept: "application/json");
        DomainHttpCacheOptions opts = CreateOptions(varyByAccept: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.HeaderNames.Should().Contain(HeaderNames.Accept);
        material.ResponseVaryHeaderNames.Should().Contain(HeaderNames.Accept);
    }

    [Fact]
    public void Build_VaryByQueryKeys_Empty_MeansNoQueryVary()
    {
        DefaultHttpContext http = CreateHttp(query: new() { ["id"] = "1", ["utm_source"] = "x" });
        DomainHttpCacheOptions opts = CreateOptions(varyByQueryKeys: []);

        IReadOnlyList<string> keys = CacheVaryMaterializer.ResolveQueryKeys(http.Request.Query, opts);

        keys.Should().BeEmpty();
    }

    [Fact]
    public void Build_VaryByQueryKeys_Allowlist_OnlyListedKeys()
    {
        DefaultHttpContext http = CreateHttp(query: new() { ["id"] = "1", ["page"] = "2", ["utm_source"] = "x" });
        DomainHttpCacheOptions opts = CreateOptions(varyByQueryKeys: ["id"]);

        IReadOnlyList<string> keys = CacheVaryMaterializer.ResolveQueryKeys(http.Request.Query, opts);

        keys.Should().Equal("id");
    }

    [Fact]
    public void Build_IgnoreQueryKeys_RemovesExtraKeys()
    {
        DefaultHttpContext http = CreateHttp(query: new() { ["id"] = "1", ["debug"] = "1" });
        DomainHttpCacheOptions opts = CreateOptions(ignoreQueryKeys: ["debug"]);

        IReadOnlyList<string> keys = CacheVaryMaterializer.ResolveQueryKeys(http.Request.Query, opts);

        keys.Should().Equal("id");
    }

    [Fact]
    public void Build_SensitiveHeader_IsHashedNotListedInHeaderNames()
    {
        DefaultHttpContext http = CreateHttp();
        http.Request.Headers.Authorization = "Bearer secret-token";
        DomainHttpCacheOptions opts = CreateOptions(
            authBypassMode: AuthBypassMode.Never,
            varyByHeaders: ["Authorization"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.HeaderNames.Should().NotContain(HeaderNames.Authorization);
        material.Values.Keys.Should().Contain(k => k.StartsWith("hdr:", StringComparison.OrdinalIgnoreCase));
        material.Values["hdr:Authorization"].Should().StartWith("h:");
        string joined = string.Join('|', material.Values.Values);
        joined.Should().NotContain("secret-token");
        material.ResponseVaryHeaderNames.Should().NotContain(HeaderNames.Authorization);
    }

    [Fact]
    public void Build_VaryByCookies_HashesCookieValue()
    {
        DefaultHttpContext http = CreateHttp();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IRequestCookiesFeature>(
            new Microsoft.AspNetCore.Http.Features.RequestCookiesFeature(
                new TestCookies(new Dictionary<string, string>
                {
                    ["ab_bucket"] = "variant-a",
                    ["session"] = "raw-secret",
                })));
        DomainHttpCacheOptions opts = CreateOptions(varyByCookies: ["ab_bucket"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        material.Values.Should().ContainKey("cookie:ab_bucket");
        material.Values["cookie:ab_bucket"].Should().StartWith("h:");
        material.Values["cookie:ab_bucket"].Should().NotContain("variant-a");
        material.Values.Should().NotContainKey("cookie:session");
    }

    [Fact]
    public void Build_AuthUser_OnOutputCache_WhenVaryByUser()
    {
        DefaultHttpContext http = CreateHttp();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "alice")],
            authenticationType: "test"));
        DomainHttpCacheOptions opts = CreateOptions(authBypassMode: AuthBypassMode.Never, varyByUser: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.Values.Should().ContainKey("auth-user");
        material.Values["auth-user"].Should().Be("u:alice");
    }

    [Fact]
    public void Build_AuthUser_NotOnFusion_UnderDefaultBypassMode()
    {
        DefaultHttpContext http = CreateHttp();
        http.Request.Headers.Authorization = "Bearer x";
        DomainHttpCacheOptions opts = CreateOptions(authBypassMode: AuthBypassMode.AuthenticatedOrAuthorization, varyByUser: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        material.Values.Should().NotContainKey("auth-user");
    }

    [Fact]
    public void Build_AuthUser_OnFusion_WhenAuthBypassNever()
    {
        DefaultHttpContext http = CreateHttp();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "bob")],
            authenticationType: "test"));
        DomainHttpCacheOptions opts = CreateOptions(authBypassMode: AuthBypassMode.Never, varyByUser: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        material.Values.Should().ContainKey("auth-user");
        material.Values["auth-user"].Should().Be("u:bob");
    }

    [Fact]
    public void Build_VaryByAuthClaims_UsesClaimMaterial()
    {
        DefaultHttpContext http = CreateHttp();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "acme"), new Claim(ClaimTypes.Name, "carol")],
            authenticationType: "test"));
        DomainHttpCacheOptions opts = CreateOptions(
            authBypassMode: AuthBypassMode.Never,
            varyByUser: true,
            varyByAuthClaims: ["tenant_id"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.Values["auth-user"].Should().Be("claims:tenant_id=acme");
    }

    [Fact]
    public void Contributor_Order_IsDeterministic()
    {
        var early = new TestContributor(order: 10, key: "a", value: "1");
        var late = new TestContributor(order: 200, key: "b", value: "2");
        var materializer = new CacheVaryMaterializer([late, early]);
        DefaultHttpContext http = CreateHttp();
        DomainHttpCacheOptions opts = CreateOptions();

        CacheVaryMaterial material = materializer.Build(http, opts, CacheVarySurface.DataCache);

        material.Values.Should().ContainKey("a").And.ContainKey("b");
        material.Values["a"].Should().Be("1");
        material.Values["b"].Should().Be("2");
    }

    [Fact]
    public void AcceptNormalization_AddsNormalizedValueWithoutMutatingRequest()
    {
        DefaultHttpContext http = CreateHttp(accept: "text/html, application/json;q=0.9");
        DomainHttpCacheOptions opts = CreateOptions(
            varyByAccept: true,
            acceptNormalization: ["application/json", "application/xml"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        http.Request.Headers.Accept.ToString().Should().Be("text/html, application/json;q=0.9");
        material.Values["normalized:accept"].Should().Be("text/html, application/json;q=0.9");
        material.HeaderNames.Should().NotContain(HeaderNames.Accept);
        material.ResponseVaryHeaderNames.Should().Contain(HeaderNames.Accept);
    }

    [Fact]
    public void AcceptNormalization_DoesNotMatchJsonSeqAsJson()
    {
        DefaultHttpContext http = CreateHttp(accept: "application/json-seq");
        DomainHttpCacheOptions opts = CreateOptions(
            varyByAccept: true,
            acceptNormalization: ["application/json", "application/xml"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        http.Request.Headers.Accept.ToString().Should().Be("application/json-seq");
        material.Values["normalized:accept"].Should().Be("application/json-seq");
    }

    [Fact]
    public void AcceptNormalization_UsesFirstPreferThatIsPresent()
    {
        DefaultHttpContext http = CreateHttp(accept: "application/json");
        DomainHttpCacheOptions opts = CreateOptions(
            varyByAccept: true,
            acceptNormalization: ["application/xml", "application/json"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        http.Request.Headers.Accept.ToString().Should().Be("application/json");
        material.Values["normalized:accept"].Should().Be("application/json");
    }

    [Fact]
    public void AcceptNormalization_PreservesClientQualityInformation()
    {
        DefaultHttpContext http = CreateHttp(accept: "application/json;q=0, application/xml;q=1");
        DomainHttpCacheOptions opts = CreateOptions(
            varyByAccept: true,
            acceptNormalization: ["application/json", "application/xml"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.DataCache);

        material.Values["normalized:accept"].Should().Be("application/json;q=0, application/xml;q=1");
        material.ResponseVaryHeaderNames.Should().Contain(HeaderNames.Accept);
    }

    [Fact]
    public void Build_VaryByAcceptLanguage_AdvertisesVaryWhenHeaderAbsent()
    {
        DefaultHttpContext http = CreateHttp();
        DomainHttpCacheOptions opts = CreateOptions(varyByAcceptLanguage: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.HeaderNames.Should().NotContain(HeaderNames.AcceptLanguage);
        material.Values.Should().NotContainKey("normalized:accept-language");
        material.ResponseVaryHeaderNames.Should().Contain(HeaderNames.AcceptLanguage);
    }

    [Fact]
    public void Build_VaryByAccept_AdvertisesVaryWhenHeaderAbsent()
    {
        DefaultHttpContext http = CreateHttp();
        DomainHttpCacheOptions opts = CreateOptions(varyByAccept: true);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.HeaderNames.Should().NotContain(HeaderNames.Accept);
        material.ResponseVaryHeaderNames.Should().Contain(HeaderNames.Accept);
    }

    [Fact]
    public void Build_ConfiguredExtraHeader_AdvertisesVaryWhenAbsent()
    {
        DefaultHttpContext http = CreateHttp();
        DomainHttpCacheOptions opts = CreateOptions(varyByHeaders: ["X-Api-Version"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.HeaderNames.Should().NotContain("X-Api-Version");
        material.ResponseVaryHeaderNames.Should().Contain("X-Api-Version");
    }

    [Fact]
    public void Build_SensitiveConfiguredHeader_DoesNotAdvertiseVaryWhenAbsent()
    {
        DefaultHttpContext http = CreateHttp();
        DomainHttpCacheOptions opts = CreateOptions(
            authBypassMode: AuthBypassMode.Never,
            varyByHeaders: ["Authorization"]);

        CacheVaryMaterial material = new CacheVaryMaterializer().Build(http, opts, CacheVarySurface.OutputCache);

        material.ResponseVaryHeaderNames.Should().NotContain(HeaderNames.Authorization);
    }

    private static DomainHttpCacheOptions CreateOptions(
        bool varyEncoding = false,
        bool varyByAccept = false,
        bool varyByAcceptLanguage = false,
        bool varyByUser = true,
        AuthBypassMode authBypassMode = AuthBypassMode.AuthenticatedOrAuthorization,
        string[]? varyByQueryKeys = null,
        string[]? ignoreQueryKeys = null,
        string[]? varyByHeaders = null,
        string[]? varyByCookies = null,
        string[]? varyByAuthClaims = null,
        string[]? acceptNormalization = null) => new()
        {
            CoreOptions = new DomainCacheOptions { Domain = "products" },
            AuthBypassMode = authBypassMode,
            VaryOutputCacheByUser = varyByUser,
            DataCacheVaryOnEncoding = varyEncoding,
            VaryByAccept = varyByAccept,
            VaryByAcceptLanguage = varyByAcceptLanguage,
            VaryByQueryKeys = varyByQueryKeys,
            IgnoreQueryKeys = ignoreQueryKeys,
            VaryByHeaders = varyByHeaders,
            VaryByCookies = varyByCookies,
            VaryByAuthClaims = varyByAuthClaims,
            AcceptNormalizationList = acceptNormalization,
        };

    private static DefaultHttpContext CreateHttp(
        string path = "/api/products",
        Dictionary<string, StringValues>? query = null,
        string? acceptEncoding = null,
        string? accept = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        if (query is not null)
            context.Request.Query = new QueryCollection(query);
        if (acceptEncoding is not null)
            context.Request.Headers.AcceptEncoding = acceptEncoding;
        if (accept is not null)
            context.Request.Headers.Accept = accept;
        return context;
    }

    private sealed class TestContributor : ICacheVaryContributor
    {
        private readonly string _key;
        private readonly string _value;

        public TestContributor(int order, string key, string value)
        {
            Order = order;
            _key = key;
            _value = value;
        }

        public int Order { get; }

        public void Contribute(CacheVaryContext context, ICacheVaryBuilder builder) =>
            builder.AddValue(_key, _value);
    }

    private sealed class TestCookies : IRequestCookieCollection
    {
        private readonly Dictionary<string, string> _map;

        public TestCookies(Dictionary<string, string> map)
        {
            _map = map;
        }

        public string? this[string key] => _map.TryGetValue(key, out string? value) ? value : null;

        public int Count => _map.Count;

        public ICollection<string> Keys => _map.Keys;

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
            _map.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _map.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
