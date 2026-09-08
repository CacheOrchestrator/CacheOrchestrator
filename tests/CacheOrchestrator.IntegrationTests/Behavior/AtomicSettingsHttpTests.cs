using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.FusionCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CacheOrchestrator.IntegrationTests.Behavior;

public class AtomicSettingsHttpTests
{
    [Theory]
    [InlineData("authBypassMode", "\"999\"")]
    [InlineData("outputCache.eTagMode", "\"-1\"")]
    [InlineData("clientCache.cacheability", "\"999\"")]
    [InlineData("fusionCache.eagerRefreshRatio", "\"NaN\"")]
    [InlineData("fusionCache.eagerRefreshRatio", "\"Infinity\"")]
    [InlineData("fusionCache.jitterSeconds", "-1")]
    [InlineData("fusionCache.factorySoftTimeoutSeconds", "5")]
    [InlineData("fusionCache.factoryHardTimeoutSeconds", "0")]
    [InlineData("clientCache.ttlMinSeconds", "51")]
    public async Task InvalidFinalSection_Returns400_AndLeavesEveryStoreAndSnapshotUnchanged(string key, string json)
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = CreateClient(app);
        var state = app.Services.GetRequiredService<IDomainRuntimeOverrideStore>();
        var http = app.Services.GetRequiredService<IRequestDomainCacheOptions>();
        var fusion = app.Services.GetRequiredService<IFusionDomainSettingsProvider>();
        DomainHttpCacheOptions before = http.GetOrCreateDomainOptions("catalog");
        DomainFusionCacheSettings fusionBefore = fusion.Get("catalog");
        using HttpResponseMessage response = await PatchAsync(client, new()
        {
            ["dataCache.enabled"] = JsonSerializer.SerializeToElement(false),
            ["clientCache.ttlSeconds"] = JsonSerializer.SerializeToElement(50),
            [key] = JsonSerializer.Deserialize<JsonElement>(json)
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        state.GetStamp("catalog").Should().Be(0);
        state.Get("catalog").Should().BeNull();
        http.GetOrCreateDomainOptions("catalog").Should().BeSameAs(before);
        fusion.Get("catalog").Should().BeSameAs(fusionBefore);
    }

    [Fact]
    public async Task SingleFieldPatch_ValidatesAgainstExistingOverlayAndConfiguration()
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = CreateClient(app);
        using HttpResponseMessage first = await PatchAsync(client, Values(("clientCache.ttlMinSeconds", 80)));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        int revision = app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().GetStamp("catalog");
        using HttpResponseMessage invalid = await PatchAsync(client, Values(("clientCache.ttlSeconds", 70)));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var options = app.Services.GetRequiredService<IRequestDomainCacheOptions>().GetOrCreateDomainOptions("catalog");
        options.ClientTtlSeconds.Should().Be(100);
        options.ClientTtlMinSeconds.Should().Be(80);
        app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().GetStamp("catalog").Should().Be(revision);
    }

    [Fact]
    public async Task CoreDurationChange_ValidatesFusionEvenWithoutFusionKeys_AndCombinedPatchIsValid()
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = CreateClient(app);
        using HttpResponseMessage invalid = await PatchAsync(client, Values(("dataCache.ttlSeconds", 250)));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using HttpResponseMessage valid = await PatchAsync(client, Values(("dataCache.ttlSeconds", 250), ("fusionCache.failSafeSeconds", 300)));
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        var core = app.Services.GetRequiredService<IDomainCacheOptionsProvider>().GetOrCreateDomainOptions("catalog");
        core.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(250));
        app.Services.GetRequiredService<IFusionDomainSettingsProvider>().Get("catalog").FailSafeSeconds.Should().Be(300);
    }

    [Fact]
    public async Task Clear_RemovesCoreHttpAndFusionOverlaysTogether()
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = CreateClient(app);
        using HttpResponseMessage valid = await PatchAsync(client, Values(
            ("dataCache.ttlSeconds", 250), ("fusionCache.failSafeSeconds", 300), ("clientCache.ttlSeconds", 200)));
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        var state = app.Services.GetRequiredService<IDomainRuntimeOverrideStore>();
        state.Clear("catalog").Should().BeTrue();
        var options = app.Services.GetRequiredService<IRequestDomainCacheOptions>().GetOrCreateDomainOptions("catalog");
        options.ClientTtlSeconds.Should().Be(100);
        options.CoreOptions.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(100));
        app.Services.GetRequiredService<IFusionDomainSettingsProvider>().Get("catalog").FailSafeSeconds.Should().Be(200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDuplicateOwner_RejectsEntirePatch(bool duplicateOwner)
    {
        await using WebApplication app = await StartAsync(includeFusion: duplicateOwner, duplicateOwner: duplicateOwner);
        using HttpClient client = CreateClient(app);
        using HttpResponseMessage response = await PatchAsync(client, Values(
            ("dataCache.ttlSeconds", 110), (duplicateOwner ? "clientCache.ttlSeconds" : "fusionCache.jitterSeconds", 10)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().GetStamp("catalog").Should().Be(0);
    }

    private sealed class ConflictingContributor : IDomainSettingsPatchContributor
    {
        public bool Owns(string settingId) => settingId == "clientCache.ttlSeconds";
        public void Prepare(DomainSettingsPatchContext context, IReadOnlyDictionary<string, JsonElement> settings) =>
            throw new InvalidOperationException("Ambiguous ownership must be rejected before preparation.");
        public void Validate(DomainSettingsPatchContext context) { }
    }

    private static Dictionary<string, JsonElement> Values(params (string Key, int Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Dictionary<string, JsonElement> values) =>
        client.PatchAsJsonAsync("/cache-admin/local/domains/catalog/settings", new AdminSettingsPatchRequest
        {
            Settings = values,
            Distribute = false
        }, TestContext.Current.CancellationToken);

    private static HttpClient CreateClient(WebApplication app)
    {
        HttpClient client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-CacheOrchestrator-Admin-Key", "k");
        return client;
    }

    private static async Task<WebApplication> StartAsync(bool includeFusion = true, bool duplicateOwner = false)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:Admin:Enabled"] = "true",
            ["Cache:Admin:ApiKey"] = "k",
            ["Cache:Domains:catalog:Version"] = "1",
            ["Cache:Domains:catalog:ClientCache:TtlSeconds"] = "100",
            ["Cache:Domains:catalog:ClientCache:TtlMinSeconds"] = "10",
            ["Cache:Domains:catalog:DataCache:TtlSeconds"] = "100",
            ["Cache:Domains:catalog:FusionCache:HardTtlSeconds"] = "1000",
            ["Cache:Domains:catalog:FusionCache:FailSafeSeconds"] = "200"
        }).Build();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddCacheOrchestratorAspNetCore(config, enableMvcConvention: false);
        if (includeFusion)
            builder.Services.AddCacheOrchestratorFusionCache(config);
        if (duplicateOwner)
            builder.Services.AddSingleton<IDomainSettingsPatchContributor, ConflictingContributor>();
        WebApplication app = builder.Build();
        app.MapCacheOrchestratorAdmin();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
