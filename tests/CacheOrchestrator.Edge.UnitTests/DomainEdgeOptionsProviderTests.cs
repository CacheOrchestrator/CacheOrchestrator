using CacheOrchestrator.Edge.Configuration;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Edge.UnitTests;

public class DomainEdgeOptionsProviderTests
{
    [Fact]
    public void GetDomainOptions_MergesDefaultsAndDomainOverrides()
    {
        IOptionsMonitor<CacheOrchestratorEdgeOptions> monitor = Substitute.For<IOptionsMonitor<CacheOrchestratorEdgeOptions>>();
        monitor.CurrentValue.Returns(new CacheOrchestratorEdgeOptions
        {
            DomainDefaults = new EdgeDomainContainer
            {
                Edge = new DomainEdgeSettings
                {
                    Enabled = true,
                    PurgeOnStartup = true,
                    Instance = "edge",
                    TtlSeconds = 300,
                    StaleIfErrorSeconds = 60
                }
            },
            Domains = new Dictionary<string, EdgeDomainContainer>(StringComparer.OrdinalIgnoreCase)
            {
                ["catalog"] = new() { Edge = new DomainEdgeSettings { TtlSeconds = 900 } }
            }
        });
        var sut = new DomainEdgeOptionsProvider(monitor);

        DomainEdgeOptions result = sut.GetDomainOptions("Catalog");

        result.Domain.Should().Be("catalog");
        result.Enabled.Should().BeTrue();
        result.PurgeOnStartup.Should().BeTrue();
        result.InstanceName.Should().Be("edge");
        result.Ttl.Should().Be(TimeSpan.FromSeconds(900));
        result.StaleIfError.Should().Be(TimeSpan.FromSeconds(60));
    }
}
