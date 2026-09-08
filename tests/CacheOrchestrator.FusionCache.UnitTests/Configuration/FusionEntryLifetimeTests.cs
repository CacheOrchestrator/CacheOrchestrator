using CacheOrchestrator.Configuration;
using CacheOrchestrator.FusionCache;
using ZiggyCreatures.Caching.Fusion;

namespace CacheOrchestrator.FusionCache.UnitTests.Configuration;

public class FusionEntryLifetimeTests
{
    [Fact]
    public void LongDataTtl_DefaultsCapFreshnessWhileKeepingSeparateJitterAndFailSafeHorizon()
    {
        FusionCacheEntryOptions options = FusionEntryOptionsFactory.Create(new DomainCacheOptions
        {
            DataCacheTtl = TimeSpan.FromDays(30)
        });
        options.Duration.Should().Be(TimeSpan.FromHours(12));
        options.JitterMaxDuration.Should().Be(TimeSpan.FromMinutes(1));
        options.IsFailSafeEnabled.Should().BeTrue();
        options.FailSafeMaxDuration.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void ExplicitSnapshotPolicy_AllowsThirtyDaysWithoutJitterStaleRetentionOrEarlyRefresh()
    {
        FusionCacheEntryOptions options = FusionEntryOptionsFactory.Create(
            new DomainCacheOptions { DataCacheTtl = TimeSpan.FromDays(30) },
            new DomainFusionCacheSettings
            {
                HardTtlSeconds = 2592000, FailSafeSeconds = 0, JitterSeconds = 0, EagerRefreshRatio = 0
            });
        options.Duration.Should().Be(TimeSpan.FromDays(30));
        options.JitterMaxDuration.Should().Be(TimeSpan.Zero);
        options.IsFailSafeEnabled.Should().BeFalse();
        options.EagerRefreshThreshold.Should().BeNull();
    }
}
