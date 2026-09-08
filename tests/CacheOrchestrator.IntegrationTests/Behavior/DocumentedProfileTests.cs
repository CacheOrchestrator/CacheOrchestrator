using CacheOrchestrator.Configuration;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.FusionCache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.RegularExpressions;

namespace CacheOrchestrator.IntegrationTests.Behavior;

public class DocumentedProfileTests
{
    [Fact]
    public void DomainProfileJson_BindsAndValidatesTheDocumentedLifetimes()
    {
        string markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Documentation", "domain-profiles.md"));
        MatchCollection blocks = Regex.Matches(markdown, "```json\\r?\\n([\\s\\S]*?)```", RegexOptions.CultureInvariant);
        blocks.Should().HaveCount(2);
        foreach (Match block in blocks)
        {
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(block.Groups[1].Value));
            IConfiguration configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCacheOrchestrator(configuration);
            using ServiceProvider provider = services.BuildServiceProvider();
            string domain = configuration.GetSection("Cache:Domains").GetChildren().Single().Key;
            DomainHttpCacheOptions http = provider.GetRequiredService<IRequestDomainCacheOptions>().GetOrCreateDomainOptions(domain);
            DomainFusionCacheSettings fusion = provider.GetRequiredService<IFusionDomainSettingsProvider>().Get(domain);
            TimeSpan expected = domain == "osm-tiles" ? TimeSpan.FromDays(30) : TimeSpan.FromMinutes(5);
            http.DataCacheTtl.Should().Be(expected);
            fusion.HardTtlSeconds.Should().Be((int)expected.TotalSeconds);
            fusion.FailSafeSeconds.Should().Be(0);
            fusion.JitterSeconds.Should().Be(0);
            http.ClientTtlMinSeconds.Should().BeLessThanOrEqualTo(http.ClientTtlSeconds);
        }
    }
}
