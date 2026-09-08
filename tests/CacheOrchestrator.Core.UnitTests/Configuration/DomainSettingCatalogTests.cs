using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CacheOrchestrator.Core.UnitTests.Configuration;

public class DomainSettingCatalogTests
{
    [Fact]
    public void Registrations_AreHostScopedAndFrozenAtProviderCreation()
    {
        var services = new ServiceCollection();
        services.AddCacheOrchestratorCore(new ConfigurationBuilder().Build());
        using ServiceProvider before = services.BuildServiceProvider();
        DomainSettingCatalog.RegisterSection(services, typeof(ExtraSettings), "extra", "Extra");
        DomainSettingCatalog.RegisterSection(services, typeof(ExtraSettings), "extra", "Extra");
        using ServiceProvider after = services.BuildServiceProvider();
        DomainSettingCatalog oldCatalog = before.GetRequiredService<DomainSettingCatalog>();
        DomainSettingCatalog newCatalog = after.GetRequiredService<DomainSettingCatalog>();
        oldCatalog.Find("extra.enabled").Should().BeNull();
        newCatalog.Find("Extra.Enabled").Should().NotBeNull();
        newCatalog.GetEntries().Count(entry => entry.Id == "extra.enabled").Should().Be(1);
        DomainSettingCatalog.Core.Find("extra.enabled").Should().BeNull();
        Action mutate = () => ((IList<DomainSettingCatalogEntry>)newCatalog.GetEntries()).Clear();
        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ConflictingAliases_AreRejectedWhenCatalogIsResolved()
    {
        var services = new ServiceCollection();
        DomainSettingCatalog.RegisterSection(services, typeof(ExtraSettings), "dataCache", "DataCache");
        using ServiceProvider provider = services.BuildServiceProvider();
        Action resolve = () => provider.GetRequiredService<DomainSettingCatalog>();
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*registered more than once*");
    }

    private sealed class ExtraSettings
    {
        [DomainSetting(Kind = DomainSettingValueKind.Bool, RuntimeOverlay = true)]
        public bool Enabled { get; set; }
    }

    [Fact]
    public void GetEntries_includes_attributed_domain_settings()
    {
        IReadOnlyList<DomainSettingCatalogEntry> all = DomainSettingCatalog.Core.GetEntries();
        Assert.NotEmpty(all);
        Assert.Contains(all, e => e.Id == "dataCache.enabled" && e.RuntimeOverlay);
        Assert.Contains(all, e => e.Id == "dataCache.ttlSeconds" && e.RuntimeOverlay);
        Assert.DoesNotContain(all, e => e.Id == "dataCache.hardTtl");
        Assert.DoesNotContain(all, e => e.Id == "dataCache.failSafe");
        Assert.Contains(all, e => e.Id == "dataCache.instance" && !e.RuntimeOverlay);
        Assert.DoesNotContain(all, e => e.Id.StartsWith("fusionCache.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(all, e => e.Id == "version" && !e.RuntimeOverlay);
        Assert.DoesNotContain(all, e => e.Id.StartsWith("outputCache.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, e => e.Id.StartsWith("clientCache.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetOverlayEntries_only_runtime_overlay()
    {
        IReadOnlyList<DomainSettingCatalogEntry> overlay = DomainSettingCatalog.Core.GetOverlayEntries();
        Assert.NotEmpty(overlay);
        Assert.All(overlay, e => Assert.True(e.RuntimeOverlay));
        Assert.DoesNotContain(overlay, e => e.Id == "dataCache.instance");
        Assert.DoesNotContain(overlay, e => e.Id == "version");
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        DomainSettingCatalogEntry? a = DomainSettingCatalog.Core.Find("dataCache.ttlSeconds");
        DomainSettingCatalogEntry? b = DomainSettingCatalog.Core.Find("DATACACHE.TTLSECONDS");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.Id, b.Id);
    }
}
