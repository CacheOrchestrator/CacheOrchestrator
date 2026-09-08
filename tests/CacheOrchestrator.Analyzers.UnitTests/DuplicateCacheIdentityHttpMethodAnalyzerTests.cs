using CacheOrchestrator.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace CacheOrchestrator.Analyzers.UnitTests;

public sealed class DuplicateCacheIdentityHttpMethodAnalyzerTests
{
    private const string AttributeStubs = """
        namespace CacheOrchestrator.Identity
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
            public sealed class CacheIdentityAttribute : System.Attribute
            {
                public CacheIdentityAttribute(string[] methods, string contractName)
                {
                }
            }

            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
            public sealed class ContentHashCacheIdentityAttribute : System.Attribute
            {
                public ContentHashCacheIdentityAttribute(string[] methods)
                {
                }
            }
        }
        """;

    [Fact]
    public async Task NoDuplicate_Ok()
    {
        await RunAsync("""
            using CacheOrchestrator.Identity;

            class C
            {
                [CacheIdentity(new[] { "GET", "HEAD" }, "a")]
                [ContentHashCacheIdentity(new[] { "POST" })]
                void M() { }
            }
            """);
    }

    [Fact]
    public async Task DuplicateOnSameAttributeList_Fails()
    {
        await RunAsync(
            """
            using CacheOrchestrator.Identity;

            class C
            {
                [CacheIdentity(new[] { "GET" }, "a")]
                [{|#0:CacheIdentity(new[] { "GET" }, "b")|}]
                void M() { }
            }
            """,
            DiagnosticResult
                .CompilerError(DuplicateCacheIdentityHttpMethodAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments("GET", "C.M"));
    }

    [Fact]
    public async Task GetOnClassAndGetOnMethod_Fails()
    {
        await RunAsync(
            """
            using CacheOrchestrator.Identity;

            [CacheIdentity(new[] { "GET" }, "a")]
            class C
            {
                [{|#0:CacheIdentity(new[] { "GET" }, "b")|}]
                void M() { }
            }
            """,
            DiagnosticResult
                .CompilerError(DuplicateCacheIdentityHttpMethodAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments("GET", "C.M"));
    }

    [Fact]
    public async Task GetOnClassAndPostOnMethod_Ok()
    {
        await RunAsync("""
            using CacheOrchestrator.Identity;

            [CacheIdentity(new[] { "GET" }, "a")]
            class C
            {
                [ContentHashCacheIdentity(new[] { "POST" })]
                void M() { }
            }
            """);
    }

    [Fact]
    public async Task CaseInsensitivePostDuplicate_Fails()
    {
        await RunAsync(
            """
            using CacheOrchestrator.Identity;

            class C
            {
                [CacheIdentity(new[] { "POST" }, "a")]
                [{|#0:ContentHashCacheIdentity(new[] { "post" })|}]
                void M() { }
            }
            """,
            DiagnosticResult
                .CompilerError(DuplicateCacheIdentityHttpMethodAnalyzer.DiagnosticId)
                .WithLocation(0)
                .WithArguments("POST", "C.M"));
    }

    [Fact]
    public async Task UnrelatedShortNames_AreIgnoredEvenAlongsideActualIdentity()
    {
        await RunAsync("""
            using CacheOrchestrator.Identity;
            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                public sealed class CacheIdentityAttribute : System.Attribute
                {
                    public CacheIdentityAttribute(string[] methods) { }
                }
                [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                public sealed class ContentHashCacheIdentityAttribute : System.Attribute
                {
                    public ContentHashCacheIdentityAttribute(string[] methods) { }
                }
            }
            class C
            {
                [CacheIdentity(new[] { "GET" }, "url")]
                [Other.CacheIdentity(new[] { "GET", "GET" })]
                [Other.ContentHashCacheIdentity(new[] { "GET", "GET" })]
                void M() { }
            }
            """);
    }

    [Fact]
    public async Task AliasedActualAttribute_StillReportsDuplicates()
    {
        await RunAsync("""
            using Identity = CacheOrchestrator.Identity.CacheIdentityAttribute;
            class C
            {
                [Identity(new[] { "GET" }, "a")]
                [{|#0:Identity(new[] { "get" }, "b")|}]
                void M() { }
            }
            """, DiagnosticResult.CompilerError(DuplicateCacheIdentityHttpMethodAnalyzer.DiagnosticId)
                .WithLocation(0).WithArguments("GET", "C.M"));
    }

    private static async Task RunAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<DuplicateCacheIdentityHttpMethodAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            TestState =
            {
                Sources = { AttributeStubs, source },
            },
        };

        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
