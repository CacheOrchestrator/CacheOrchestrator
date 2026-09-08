using CacheOrchestrator.Edge.Invalidation;
using CacheOrchestrator.Edge.Providers;
using System.Text.Json;

namespace CacheOrchestrator.Edge.UnitTests;

public class EdgeInvalidationTargetTests
{
    [Fact]
    public void JobOwnsItsParametersAndTags_AndRoundTripsForDurableQueues()
    {
        var parameters = new Dictionary<string, string> { ["token"] = "original-secret", ["zone"] = "old" };
        var target = new EdgeInvalidationTarget("edge", "Test", "old", parameters);
        string[] tags = ["tag-a"];
        var job = new EdgeInvalidationJob(target, tags);
        parameters["token"] = "changed";
        tags[0] = "changed";
        target.Parameters["token"].Should().Be("original-secret");
        job.Tags.Should().Equal("tag-a");
        target.ToString().Should().Be("Test/edge").And.NotContain("secret");
        string serialized = JsonSerializer.Serialize(job);
        EdgeInvalidationJob restored = JsonSerializer.Deserialize<EdgeInvalidationJob>(serialized)!;
        restored.Target.Should().Be(target);
        restored.Tags.Should().Equal("tag-a");
    }

    [Fact]
    public void EqualityIncludesProviderRouteAndCredentials_WithoutDependingOnParameterOrder()
    {
        var target = new EdgeInvalidationTarget("edge", "Test", "location", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var reordered = new EdgeInvalidationTarget("EDGE", "TEST", "location", new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        reordered.Should().Be(target);
        reordered.GetHashCode().Should().Be(target.GetHashCode());
        new EdgeInvalidationTarget("edge", "Other", "location", target.Parameters).Should().NotBe(target);
        new EdgeInvalidationTarget("edge", "Test", "other-location", target.Parameters).Should().NotBe(target);
        new EdgeInvalidationTarget("edge", "Test", "location", new Dictionary<string, string> { ["a"] = "changed", ["b"] = "2" }).Should().NotBe(target);
    }
}
