using CacheOrchestrator.Configuration;
using CacheOrchestrator.FusionCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.FusionCache.UnitTests.Configuration;

public sealed class FusionDomainSettingsProviderTests
{
    [Fact]
    public void Get_ReusesEffectiveSettingsUntilConfigurationChanges()
    {
        IConfigurationRoot configuration = BuildConfiguration();
        using FusionDomainSettingsProvider provider = CreateProvider(configuration);

        DomainFusionCacheSettings first = provider.Get("catalog");
        DomainFusionCacheSettings second = provider.Get("catalog");

        second.Should().BeSameAs(first);
        configuration["Cache:Domains:catalog:FusionCache:JitterSeconds"] = "12";
        configuration.Reload();

        DomainFusionCacheSettings reloaded = provider.Get("catalog");
        reloaded.Should().NotBeSameAs(first);
        reloaded.JitterSeconds.Should().Be(12);
    }

    [Fact]
    public void Get_RebuildsEffectiveSettingsWhenRuntimeOverrideStampChanges()
    {
        IConfigurationRoot configuration = BuildConfiguration();
        var overrides = new FusionDomainRuntimeOverrideStore();
        using FusionDomainSettingsProvider provider = CreateProvider(configuration, overrides);
        DomainFusionCacheSettings first = provider.Get("catalog");

        overrides.PatchSettings(
            "catalog",
            new FusionDomainSettingsPatch { Jitter = TimeSpan.FromSeconds(17) });

        DomainFusionCacheSettings updated = provider.Get("catalog");
        updated.Should().NotBeSameAs(first);
        updated.JitterSeconds.Should().Be(17);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_DoesNotPublishAnOldInFlightBinding(bool publishNewFirst)
    {
        IConfigurationRoot configuration = BuildConfiguration();
        IFusionDomainRuntimeOverrideStore overrides = Substitute.For<IFusionDomainRuntimeOverrideStore>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        int reads = 0;
        overrides.Get("catalog").Returns(_ =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();
            }
            return null;
        });
        using FusionDomainSettingsProvider provider = CreateProvider(configuration, overrides);
        Task<DomainFusionCacheSettings> slow = Task.Run(() => provider.Get("catalog"), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            configuration["Cache:Domains:catalog:FusionCache:JitterSeconds"] = "17";
            configuration.Reload();
            DomainFusionCacheSettings? before = publishNewFirst ? provider.Get("catalog") : null;
            release.Set();
            (await slow).JitterSeconds.Should().Be(3);
            DomainFusionCacheSettings fresh = provider.Get("catalog");
            fresh.JitterSeconds.Should().Be(17);
            if (before is not null)
                fresh.Should().BeSameAs(before);
            provider.Get("catalog").Should().BeSameAs(fresh);
        }
        finally
        {
            release.Set();
            await slow;
        }
    }

    private static IConfigurationRoot BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:DomainDefaults:FusionCache:JitterSeconds"] = "3",
                ["Cache:Domains:catalog:FusionCache:EagerRefreshRatio"] = "0.8"
            })
            .Build();

    private static FusionDomainSettingsProvider CreateProvider(
        IConfiguration configuration,
        IFusionDomainRuntimeOverrideStore? overrides = null) =>
        new(
            configuration,
            Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>(),
            overrides,
            "Cache");
}
