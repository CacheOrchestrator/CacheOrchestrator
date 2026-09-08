using CacheOrchestrator.Configuration;
using Microsoft.Extensions.Primitives;

namespace CacheOrchestrator.AspNetCore.UnitTests.Configuration;

public class DomainHttpCacheOptionsImmutabilityTests
{
    [Fact]
    public void Collections_DoNotRetainMutableInputsOrExposeMutableStorage()
    {
        string[] names = ["original"];
        int[] statusCodes = [200];
        var options = new DomainHttpCacheOptions
        {
            VaryByAuthClaims = names, AcceptNormalizationList = names,
            AcceptLanguageNormalizationList = names, VaryByHeaders = names,
            VaryByQueryKeys = names, IgnoreQueryKeys = names, VaryByCookies = names,
            EncodingNormalizationList = names, CacheableStatusCodes = statusCodes
        };
        names[0] = "mutated";
        statusCodes[0] = 500;

        IReadOnlyList<string>?[] collections =
        [
            options.VaryByAuthClaims, options.AcceptNormalizationList,
            options.AcceptLanguageNormalizationList, options.VaryByHeaders,
            options.VaryByQueryKeys, options.IgnoreQueryKeys, options.VaryByCookies,
            options.EncodingNormalizationList
        ];
        foreach (IReadOnlyList<string>? collection in collections)
        {
            collection.Should().Equal("original");
            collection.Should().NotBeAssignableTo<IList<string>>();
            collection.Should().NotBeAssignableTo<string[]>();
        }
        options.CacheableStatusCodes.Should().Equal(200);
        options.CacheableStatusCodes.Should().NotBeAssignableTo<IList<int>>();
        options.VaryByHeaders.Should().BeSameAs(options.VaryByHeaders);
    }

    [Fact]
    public void ETag_DoesNotRetainInputArrayOrExposeMutableBackingArray()
    {
        string[] values = ["\"original\""];
        var options = new DomainHttpCacheOptions { ETag = new StringValues(values) };
        values[0] = "\"changed-input\"";
        string?[] returned = options.ETag.ToArray();
        returned[0] = "\"changed-output\"";
        options.ETag.ToString().Should().Be("\"original\"");
    }

    [Fact]
    public void ETag_RejectsMultipleValidatorsInOneSnapshot()
    {
        Action create = () => _ = new DomainHttpCacheOptions { ETag = new StringValues(["\"a\"", "\"b\""]) };
        create.Should().Throw<ArgumentException>();
    }
}
