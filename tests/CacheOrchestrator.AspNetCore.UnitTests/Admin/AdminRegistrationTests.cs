using CacheOrchestrator.Admin;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CacheOrchestrator.AspNetCore.UnitTests.Admin;

public class AdminRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AddCacheOrchestrator_WhenAdminDisabled_RegistersNoOpCollector()
    {
        ServiceCollection services = new();
        IConfiguration config = BuildConfig(enabled: false);
        services.AddLogging();
        services.AddCacheOrchestratorAspNetCore(config);
        services.AddCacheOrchestratorFusionCache(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        IAdminStatsCollector collector = sp.GetRequiredService<IAdminStatsCollector>();
        collector.IsEnabled.Should().BeFalse();
        sp.GetRequiredService<ICacheOrchestratorManagement>().Should().NotBeNull();
    }

    [Fact]
    public void AddCacheOrchestrator_WhenAdminEnabled_RegistersLiveCollectorAndQuery()
    {
        ServiceCollection services = new();
        IConfiguration config = BuildConfig(enabled: true, apiKey: "secret");
        services.AddLogging();
        services.AddCacheOrchestratorAspNetCore(config);
        services.AddCacheOrchestratorFusionCache(config);
        services.AddRouting();

        using ServiceProvider sp = services.BuildServiceProvider();
        IAdminStatsCollector collector = sp.GetRequiredService<IAdminStatsCollector>();
        collector.IsEnabled.Should().BeTrue();
        sp.GetRequiredService<IDomainRuntimeOverrideStore>().Should().NotBeNull();
        sp.GetRequiredService<ICacheOrchestratorManagement>().Should().NotBeNull();
    }

    [Fact]
    public async Task MapCacheOrchestratorAdmin_WhenDisabled_DoesNotExposeHealth()
    {
        using IHost host = await CreateHostAsync(enabled: false);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/cache-admin/local/health", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MapCacheOrchestratorAdmin_WhenEnabled_HealthAndStatsWork()
    {
        using IHost host = await CreateHostAsync(enabled: true, apiKey: "k", instanceId: "unit-1");
        HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyEndpointFilter.HeaderName, "k");

        HttpResponseMessage health = await client.GetAsync("/cache-admin/local/health", Ct);
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        AdminHealthDto? body = await health.Content.ReadFromJsonAsync<AdminHealthDto>(cancellationToken: Ct);
        body.Should().NotBeNull();
        body.InstanceId.Should().Be("unit-1");
        body.AdminEnabled.Should().BeTrue();
        body.Healthy.Should().BeTrue();

        HttpResponseMessage stats = await client.GetAsync("/cache-admin/local/stats", Ct);
        stats.StatusCode.Should().Be(HttpStatusCode.OK);

        // stats/v2 was removed; analytics prefer OTEL/Prometheus.
        HttpResponseMessage statsV2 = await client.GetAsync("/cache-admin/local/stats/v2", Ct);
        statsV2.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MapCacheOrchestratorAdmin_RejectsMissingApiKey()
    {
        using IHost host = await CreateHostAsync(enabled: true, apiKey: "secret");
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/cache-admin/local/health", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task VersionAndSettingsEndpoints_UpdateEffectiveDomainConfig()
    {
        using IHost host = await CreateHostAsync(enabled: true, apiKey: "k");
        HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyEndpointFilter.HeaderName, "k");

        using StringContent versionBody = new(
            """{"version":"admin-v2"}""",
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage versionResponse =
            await client.PostAsync("/cache-admin/local/domains/catalog/version", versionBody, Ct);
        versionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AdminDomainMutationResultDto? versionResult =
            await versionResponse.Content.ReadFromJsonAsync<AdminDomainMutationResultDto>(cancellationToken: Ct);
        versionResult.Should().NotBeNull();
        versionResult.Effective.Version.Should().Be("admin-v2");
        versionResult.Effective.VersionIsRuntimeOverride.Should().BeTrue();

        using StringContent settingsBody = new(
            """{"settings":{"outputCache.ttlSeconds":42,"clientCache.ttlSeconds":7,"clientCache.ttlMinSeconds":5}}""",
            Encoding.UTF8,
            "application/json");
        HttpRequestMessage settingsRequest = new(HttpMethod.Patch, "/cache-admin/local/domains/catalog/settings")
        {
            Content = settingsBody
        };
        HttpResponseMessage settingsResponse = await client.SendAsync(settingsRequest, Ct);
        settingsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AdminDomainMutationResultDto? settingsResult =
            await settingsResponse.Content.ReadFromJsonAsync<AdminDomainMutationResultDto>(cancellationToken: Ct);
        settingsResult.Should().NotBeNull();
        settingsResult.Effective.OutputCacheTtlSeconds.Should().Be(42);
        settingsResult.Effective.ClientTtlSeconds.Should().Be(7);
        settingsResult.Effective.Version.Should().Be("admin-v2");
    }

    [Fact]
    public async Task InvalidateEndpoint_ReturnsCacheInvalidationResult()
    {
        using IHost host = await CreateHostAsync(enabled: true, apiKey: "k");
        HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyEndpointFilter.HeaderName, "k");

        using StringContent body = new(
            """{"scope":"domain","domain":"catalog"}""",
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await client.PostAsync("/cache-admin/local/invalidate", body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("succeeded", out _).Should().BeTrue();

        // Admin Console AdminApiClient deserializes this type with Web defaults â€” must round-trip.
        CacheInvalidationResult? parsed = System.Text.Json.JsonSerializer.Deserialize<CacheInvalidationResult>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        parsed.Should().NotBeNull();
        parsed.Succeeded.Should().BeTrue();
        parsed.IsSkipped.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidateEndpoint_DefaultDistributeFalse_DoesNotRequireBus()
    {
        // distribute omitted â†’ local-only; must succeed with Null bus.
        using IHost host = await CreateHostAsync(enabled: true, apiKey: "k");
        HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyEndpointFilter.HeaderName, "k");

        using StringContent body = new(
            """{"scope":"domain","domain":"catalog","distribute":false}""",
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await client.PostAsync("/cache-admin/local/invalidate", body, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static IConfiguration BuildConfig(bool enabled, string? apiKey = null, string? instanceId = null)
    {
        Dictionary<string, string?> data = new()
        {
            ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory",
            ["Cache:Domains:catalog:Version"] = "v1",
            ["Cache:Domains:catalog:OutputCache:TtlSeconds"] = "60",
            ["Cache:Admin:Enabled"] = enabled ? "true" : "false"
        };
        if (apiKey is not null)
            data["Cache:Admin:ApiKey"] = apiKey;
        if (instanceId is not null)
            data["Cache:InstanceId"] = instanceId;

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static async Task<IHost> CreateHostAsync(
        bool enabled,
        string? apiKey = null,
        string? instanceId = null)
    {
        IHostBuilder builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    IConfiguration config = BuildConfig(enabled, apiKey, instanceId);
                    services.AddSingleton(config);
                    services.AddLogging();
                    services.AddRouting();
                    services.AddCacheOrchestratorAspNetCore(config, enableMvcConvention: false);
                    services.AddCacheOrchestratorFusionCache(config);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    // Admin API does not require Output Cache middleware; omitting it keeps
                    // TestHost (esp. net10) free of PipeWriter/UnflushedBytes interactions.
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapCacheOrchestratorAdmin();
                        endpoints.MapGet("/api/products/{id}", () => Results.Ok(new { id = 1 }))
                            .CacheOutputWithDomain("catalog");
                    });
                });
            });

        IHost host = await builder.StartAsync(Ct);
        return host;
    }
}
