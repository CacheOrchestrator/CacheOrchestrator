using CacheOrchestrator.Admin;
using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.HttpBus;
using CacheOrchestrator.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CacheOrchestrator.IntegrationTests.Behavior;

public class ClusterExecutionHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InvalidationFailure_Returns503_RetriesSameId_ThenReportsDuplicate()
    {
        var failure = new FailureSwitch { FailData = true };
        await using WebApplication app = await StartAsync(failure);
        using HttpClient client = Client(app);
        object command = new
        {
            commandType = "invalidate", commandId = Guid.NewGuid(), originInstanceId = "peer", @namespace = "test",
            timestampUtc = DateTimeOffset.UtcNow, kind = 0, scope = "catalog", domain = "catalog", tags = new[] { "domain:catalog" }
        };
        using HttpResponseMessage failed = await SendAsync(client, command);
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using JsonDocument body = JsonDocument.Parse(await failed.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("applied").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("invalidation").GetProperty("dataCacheSucceeded").GetBoolean().Should().BeFalse();
        failure.FailData = false;
        using HttpResponseMessage retried = await SendAsync(client, command);
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        using HttpResponseMessage duplicate = await SendAsync(client, command);
        duplicate.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync(Ct));
        duplicateBody.RootElement.GetProperty("applied").GetBoolean().Should().BeFalse();
        duplicateBody.RootElement.GetProperty("status").GetString().Should().Be("AlreadyApplied");
        failure.DataCalls.Should().Be(2);
    }

    [Fact]
    public async Task SettingsRetry_ResumesFailedLayer_WithoutOverwritingNewerSettings()
    {
        var failure = new FailureSwitch { FailData = true };
        await using WebApplication app = await StartAsync(failure);
        using HttpClient client = Client(app);
        object original = SettingsCommand(Guid.NewGuid(), 60, applyImmediately: true);
        using HttpResponseMessage failed = await SendAsync(client, original);
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using JsonDocument body = JsonDocument.Parse(await failed.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("localMutationApplied").GetBoolean().Should().BeTrue();
        app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().Get("catalog")!.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(60));
        using HttpResponseMessage newer = await SendAsync(client, SettingsCommand(Guid.NewGuid(), 120, applyImmediately: false));
        newer.StatusCode.Should().Be(HttpStatusCode.OK);
        failure.FailData = false;
        using HttpResponseMessage retried = await SendAsync(client, original);
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().Get("catalog")!.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(120));
        app.Services.GetRequiredService<IRequestDomainCacheOptions>().GetOrCreateDomainOptions("catalog").OutputTtl.Should().Be(TimeSpan.FromSeconds(120));
        failure.DataCalls.Should().Be(2);
        failure.OutputCalls.Should().Be(1, "the successful Output Cache purge must not run again");
    }

    [Fact]
    public async Task RejectedSettings_Return400_WithoutMutation()
    {
        await using WebApplication app = await StartAsync(new());
        using HttpClient client = Client(app);
        using HttpResponseMessage response = await SendAsync(client, new
        {
            commandType = "settingsPatch", commandId = Guid.NewGuid(), originInstanceId = "peer", @namespace = "test",
            timestampUtc = DateTimeOffset.UtcNow, domain = "catalog", settings = new Dictionary<string, object> { ["authBypassMode"] = "999" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().GetStamp("catalog").Should().Be(0);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("status").GetString().Should().Be("Rejected");
    }

    [Fact]
    public async Task AdminSettings_Return503WithPublishedState_WhenLocalPurgeFails()
    {
        await using WebApplication app = await StartAsync(new() { FailData = true });
        using HttpClient client = Client(app);
        using HttpResponseMessage response = await client.PatchAsJsonAsync("/cache-admin/local/domains/catalog/settings", new
        {
            settings = new Dictionary<string, int> { ["dataCache.ttlSeconds"] = 60 }, applyImmediately = true
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("localApplied").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("result").GetProperty("localInvalidationErrors").GetArrayLength().Should().Be(1);
        app.Services.GetRequiredService<IDomainRuntimeOverrideStore>().Get("catalog")!.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(60));
    }

    private static object SettingsCommand(Guid id, int ttl, bool applyImmediately) => new
    {
        commandType = "settingsPatch", commandId = id, originInstanceId = "peer", @namespace = "test",
        timestampUtc = DateTimeOffset.UtcNow, domain = "catalog", applyImmediately,
        settings = new Dictionary<string, int> { ["dataCache.ttlSeconds"] = ttl, ["outputCache.ttlSeconds"] = ttl }
    };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, object command) =>
        client.PostAsJsonAsync("/cache-admin/local/cluster/apply", command, Ct);

    private static HttpClient Client(WebApplication app)
    {
        HttpClient client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-CacheOrchestrator-Admin-Key", "k");
        return client;
    }

    private sealed class FailureSwitch
    {
        public bool FailData;
        public int DataCalls;
        public int OutputCalls;
    }

    private static async Task<WebApplication> StartAsync(FailureSwitch failure)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:Namespace"] = "test", ["Cache:InstanceId"] = "local", ["Cache:Domains:catalog:Version"] = "1",
            ["Cache:DataCacheInstances:default:Provider"] = "InMemory", ["Cache:OutputCache:Provider"] = "InMemory",
            ["Cache:Admin:Enabled"] = "true", ["Cache:Admin:ApiKey"] = "k", ["Cache:Cluster:Bus:Enabled"] = "true",
            ["Cache:Cluster:Bus:ApiKey"] = "k", ["Cache:Cluster:Bus:Membership"] = "Static"
        }).Build();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddCacheOrchestratorAspNetCore(config, options => options.AddHttpClusterBus(), enableMvcConvention: false);
        builder.Services.AddCacheOrchestratorFusionCache(config);
        IDataCacheProvider data = Substitute.For<IDataCacheProvider>();
        data.InvalidateAsync(Arg.Any<DataCacheInvalidationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref failure.DataCalls);
            return failure.FailData ? ValueTask.FromException(new IOException("Data backend unavailable")) : ValueTask.CompletedTask;
        });
        IHttpCacheInvalidationSink output = Substitute.For<IHttpCacheInvalidationSink>();
        output.EvictByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref failure.OutputCalls);
            return ValueTask.CompletedTask;
        });
        builder.Services.AddSingleton(data);
        builder.Services.AddSingleton(output);
        WebApplication app = builder.Build();
        app.MapCacheOrchestratorAdmin();
        app.MapCacheOrchestratorHttpBus();
        await app.StartAsync(Ct);
        return app;
    }
}
