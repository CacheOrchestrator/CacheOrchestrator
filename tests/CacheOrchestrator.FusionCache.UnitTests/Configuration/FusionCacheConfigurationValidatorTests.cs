using CacheOrchestrator.Configuration;
using CacheOrchestrator.FusionCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.FusionCache.UnitTests.Configuration;

public sealed class FusionCacheConfigurationValidatorTests
{
    [Theory]
    [InlineData("HardTtlSeconds", "-1", "cannot be negative")]
    [InlineData("EagerRefreshRatio", "1", "must be in [0, 1)")]
    [InlineData("FactorySoftTimeoutSeconds", "5", "must be < FactoryHardTimeoutSeconds")]
    [InlineData("FactoryHardTimeoutSeconds", "0", "must be > 0")]
    [InlineData("FailSafeSeconds", "10", "effective Data Cache duration")]
    [InlineData("MaxItemBytes", "0", "MaxItemBytes was removed")]
    [InlineData("MaxItemBytes", "1024", "MaxItemBytes was removed")]
    public void Validate_RejectsInvalidEffectiveFusionSettings(
        string property,
        string value,
        string expectedFailure)
    {
        Dictionary<string, string?> values = new()
        {
            ["Cache:Domains:catalog:DataCache:TtlSeconds"] = "300",
            ["Cache:Domains:catalog:FusionCache:FactoryHardTimeoutSeconds"] = "5"
        };
        values[$"Cache:Domains:catalog:FusionCache:{property}"] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        CacheOrchestratorOptions options = new();
        configuration.GetSection("Cache").Bind(options);

        ValidateOptionsResult result =
            new FusionCacheConfigurationValidator(configuration, "Cache").Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AppliesInheritedTimeoutsAndTtls()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:DomainDefaults:DataCache:TtlSeconds"] = "120",
                ["Cache:DomainDefaults:FusionCache:FailSafeSeconds"] = "300",
                ["Cache:DomainDefaults:FusionCache:FactorySoftTimeoutSeconds"] = "2",
                ["Cache:Domains:catalog:FusionCache:FactoryHardTimeoutSeconds"] = "8",
            })
            .Build();
        CacheOrchestratorOptions options = new();
        configuration.GetSection("Cache").Bind(options);

        ValidateOptionsResult result =
            new FusionCacheConfigurationValidator(configuration, "Cache").Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }
}
