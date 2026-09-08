using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace CacheOrchestrator.FusionCache.UnitTests.Configuration;

public class FusionDomainSettingCatalogTests
{
    [Fact]
    public void AddCacheOrchestratorFusionCache_registers_fusionCache_catalog_section()
    {
        ServiceCollection services = new();
        services.AddCacheOrchestratorFusionCache();

        using ServiceProvider provider = services.BuildServiceProvider();
        IReadOnlyList<DomainSettingCatalogEntry> all = provider.GetRequiredService<DomainSettingCatalog>().GetEntries();
        Assert.Contains(all, e => e.Id == "fusionCache.hardTtlSeconds" && e.RuntimeOverlay);
        Assert.Contains(all, e => e.Id == "fusionCache.failSafeSeconds" && e.RuntimeOverlay);
        Assert.Contains(all, e => e.Id == "fusionCache.eagerRefreshRatio" && e.RuntimeOverlay);
    }
}
