using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Invalidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

IConfiguration configuration = new ConfigurationBuilder().Build();
foreach (string composition in new[] { "core", "hybrid-web", "fusion-web" })
{
    var services = new ServiceCollection();
    services.AddLogging();
    if (composition == "core")
        services.AddCacheOrchestratorCore(configuration);
    else if (composition == "hybrid-web")
    {
        services.AddCacheOrchestratorAspNetCore(configuration);
        services.AddHybridCache();
        services.AddCacheOrchestratorHybridCache();
    }
    else
        services.AddCacheOrchestrator(configuration);

    using ServiceProvider provider = services.BuildServiceProvider();
    DomainSettingCatalog catalog = provider.GetRequiredService<DomainSettingCatalog>();
    if (catalog.Find("dataCache.ttlSeconds") is null ||
        (catalog.Find("fusionCache.hardTtlSeconds") is not null) != (composition == "fusion-web") ||
        (catalog.Find("authBypassMode") is not null) != (composition != "core"))
        throw new InvalidOperationException($"Incorrect catalog: {composition}");
    _ = provider.GetRequiredService<ICacheOrchestratorInvalidator>();
    Console.WriteLine($"Packaged composition OK: {composition}");
}

// Same short name as the product attribute: the packaged analyzer must ignore it.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CacheIdentityAttribute(string[] methods) : Attribute
{
    public string[] Methods { get; } = methods;
}

public sealed class UnrelatedConsumer
{
    [CacheIdentity(["GET"]), CacheIdentity(["GET"])]
    public void Execute() { }
}
