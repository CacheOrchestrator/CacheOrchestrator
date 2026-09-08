using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CacheOrchestrator.IntegrationTests.Behavior;

public class HostScopedCatalogTests
{
    [Fact]
    public void Core_HybridWeb_AndFusionWeb_ExposeOnlyTheirOwnPackages()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        var coreServices = new ServiceCollection();
        coreServices.AddLogging();
        coreServices.AddCacheOrchestratorCore(config);
        using ServiceProvider core = coreServices.BuildServiceProvider();
        var hybridServices = new ServiceCollection();
        hybridServices.AddLogging();
        hybridServices.AddCacheOrchestratorAspNetCore(config);
        hybridServices.AddHybridCache();
        hybridServices.AddCacheOrchestratorHybridCache();
        using ServiceProvider hybrid = hybridServices.BuildServiceProvider();
        var fusionServices = new ServiceCollection();
        fusionServices.AddLogging();
        fusionServices.AddCacheOrchestrator(config);
        using ServiceProvider fusion = fusionServices.BuildServiceProvider();

        DomainSettingCatalog fusionCatalog = fusion.GetRequiredService<DomainSettingCatalog>();
        DomainSettingCatalog hybridCatalog = hybrid.GetRequiredService<DomainSettingCatalog>();
        DomainSettingCatalog coreCatalog = core.GetRequiredService<DomainSettingCatalog>();
        fusionCatalog.Find("fusionCache.hardTtlSeconds").Should().NotBeNull();
        hybridCatalog.Find("fusionCache.hardTtlSeconds").Should().BeNull();
        coreCatalog.Find("fusionCache.hardTtlSeconds").Should().BeNull();
        fusionCatalog.Find("authBypassMode").Should().NotBeNull();
        hybridCatalog.Find("authBypassMode").Should().NotBeNull();
        coreCatalog.Find("authBypassMode").Should().BeNull();
        foreach (DomainSettingCatalog catalog in new[] { coreCatalog, hybridCatalog, fusionCatalog })
        {
            catalog.Find("dataCache.ttlSeconds").Should().NotBeNull();
            catalog.Find("fusionCache.maxItemBytes").Should().BeNull();
            catalog.GetEntries().Select(entry => entry.Id).Should().OnlyHaveUniqueItems();
        }
    }
}
