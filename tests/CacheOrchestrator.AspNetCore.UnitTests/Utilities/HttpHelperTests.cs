using CacheOrchestrator.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace CacheOrchestrator.AspNetCore.UnitTests.Utilities;

public class HttpHelperTests
{
    // =========================
    // IsTrackingParameter
    // =========================

    [Theory]
    [InlineData("utm_source")]
    [InlineData("utm_medium")]
    [InlineData("utm_campaign")]
    [InlineData("utm_term")]
    [InlineData("utm_content")]
    [InlineData("UTM_SOURCE")]
    [InlineData("fbclid")]
    [InlineData("gclid")]
    [InlineData("msclkid")]
    [InlineData("ttclid")]
    [InlineData("_ga")]
    [InlineData("_gl")]
    [InlineData("_ga_ABC")]
    public void IsTrackingParameter_KnownTrackingKeys_ReturnsTrue(string key) => HttpHelper.IsTrackingParameter(key).Should().BeTrue();

    [Theory]
    [InlineData("id")]
    [InlineData("page")]
    [InlineData("sort")]
    [InlineData("filter")]
    [InlineData("q")]
    [InlineData("search")]
    [InlineData("category")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("_game")]
    [InlineData("_galaxy")]
    [InlineData("_global")]
    [InlineData("ga")]
    public void IsTrackingParameter_NormalKeys_ReturnsFalse(string? key) =>
        HttpHelper.IsTrackingParameter(key!).Should().BeFalse();

    // =========================
    // ContainsCacheDirective
    // =========================

    [Fact]
    public void ContainsCacheDirective_WhenNoStorePresent_ReturnsTrue()
    {
        StringValues header = new("private, no-store, max-age=0");
        HttpHelper.ContainsCacheDirective(header, "no-store").Should().BeTrue();
    }

    [Fact]
    public void ContainsCacheDirective_WhenSplitAcrossValues_ReturnsTrue()
    {
        StringValues header = new(["private", "no-store"]);
        HttpHelper.ContainsCacheDirective(header, "no-store").Should().BeTrue();
    }

    [Fact]
    public void ContainsCacheDirective_WhenMissing_ReturnsFalse()
    {
        StringValues header = new("max-age=60, public");
        HttpHelper.ContainsCacheDirective(header, "no-store").Should().BeFalse();
    }

    [Fact]
    public void ContainsCacheDirective_WhenEmpty_ReturnsFalse()
    {
        HttpHelper.ContainsCacheDirective(StringValues.Empty, "no-store").Should().BeFalse();
    }

    [Fact]
    public void ContainsCacheDirective_DoesNotMatchSubstringTokens()
    {
        HttpHelper.ContainsCacheDirective(new("s-maxage=60"), "max-age").Should().BeFalse();
        HttpHelper.ContainsCacheDirective(new("no-storey"), "no-store").Should().BeFalse();
        HttpHelper.ContainsCacheDirective(new("max-age=no-store"), "no-store").Should().BeFalse();
    }

    // =========================
    // ApplyNoCache
    // =========================

    [Fact]
    public void ApplyNoCache_SetsExpectedHeaders()
    {
        var http = new DefaultHttpContext();

        HttpHelper.ApplyNoCache(http.Response);

        http.Response.Headers.CacheControl.ToString()
            .Should().Be("no-store, no-cache, must-revalidate");
        http.Response.Headers.Pragma.ToString()
            .Should().Be("no-cache");
    }

    [Theory]
    [InlineData("GZIP", "gzip")]
    [InlineData("  gzip  ", "gzip")]
    [InlineData("gzip, deflate, br", "gzip, deflate, br")]
    [InlineData("br;q=0,gzip;q=1,*;q=0.5", "br;q=0,gzip;q=1,*;q=0.5")]
    [InlineData("application/json;q=0,application/*;q=1", "application/json;q=0,application/*;q=1")]
    [InlineData("application/xml, */*", "application/xml, */*")]
    [InlineData("application/json;profile=first", "application/json;profile=first")]
    [InlineData("application/json-seq", "application/json-seq")]
    [InlineData("en-US", "en-US")]
    [InlineData("en;q=0,sl;q=1", "en;q=0,sl;q=1")]
    [InlineData("identity", "identity")]
    [InlineData("", "")]
    public void NormalizeNegotiationHeader_PreservesRepresentationIdentity(string header, string expected)
    {
        HttpHelper.NormalizeNegotiationHeader(header, ["gzip", "br", "application/json", "application/xml", "en", "sl"])
            .Should().Be(expected);
    }

    [Fact]
    public void NormalizeNegotiationHeader_PreservesMultipleHeaderValues()
    {
        StringValues values = new(["application/json;q=0", "*/*;q=1"]);
        HttpHelper.NormalizeNegotiationHeader(values, ["application/json", "application/xml"])
            .Should().Be(values.ToString());
    }

    [Fact]
    public void NormalizeNegotiationHeader_CommonCompositeHeader_ReusesString()
    {
        string header = "br;q=0,gzip;q=1,*;q=0.5";
        HttpHelper.NormalizeNegotiationHeader(header, ["br", "gzip"]).Should().BeSameAs(header);
    }
}
