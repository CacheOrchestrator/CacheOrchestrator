using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.AspNetCore.UnitTests.Configuration;

public class DomainCacheConfigProviderTests
{
    // =========================
    // NormalizeDomain
    // =========================

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("   ", "default")]
    [InlineData("Product-Catalog", "product-catalog")]
    [InlineData("PRODUCT_CATALOG", "product_catalog")]
    [InlineData("product@catalog", "product@catalog")]
    [InlineData("product:catalog", "product:catalog")]
    [InlineData("product--catalog", "product-catalog")]
    [InlineData("product---catalog", "product-catalog")]
    [InlineData("---product---", "product")]
    [InlineData("product!!!catalog", "product-catalog")]
    [InlineData("product   catalog", "product-catalog")]
    [InlineData("a", "a")]
    [InlineData("---", "default")]
    [InlineData("!!!", "default")]
    [InlineData("-a-", "a")]
    [InlineData("a-b-c", "a-b-c")]
    public void NormalizeDomain_ReturnsExpectedResult(string? input, string expected)
    {
        string result = DomainName.Normalize(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizeDomain_IsCaseInsensitiveAndStable()
    {
        string a = DomainName.Normalize("My-Domain");
        string b = DomainName.Normalize("my-domain");
        string c = DomainName.Normalize("MY-DOMAIN");

        a.Should().Be(b).And.Be(c).And.Be("my-domain");
    }

    // =========================
    // EnsureConfig / GetConfig
    // =========================

    [Fact]
    public void EnsureConfig_ReturnsSameInstance_WithinSameRequest()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions(), new CacheOrchestratorHttpOptions
        {
            Domains = { ["products"] = new() { OutputCache = new() { TtlSeconds = 100 } } }
        });
        var http = new DefaultHttpContext();

        DomainHttpCacheOptions cfg1 = provider.EnsureDomainOptions(http, "products");
        DomainHttpCacheOptions cfg2 = provider.EnsureDomainOptions(http, "products");

        cfg1.Should().BeSameAs(cfg2);
    }

    [Fact]
    public void EnsureConfig_DifferentDomainOnSameRequest_ReplacesSnapshot()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions
        {
            Domains =
            {
                ["products"] = new() { Version = "p1" },
                ["catalog"] = new() { Version = "c1" }
            }
        }, new CacheOrchestratorHttpOptions
        {
            Domains =
            {
                ["products"] = new() { OutputCache = new() { TtlSeconds = 100 } },
                ["catalog"] = new() { OutputCache = new() { TtlSeconds = 200 } }
            }
        });
        var http = new DefaultHttpContext();

        DomainHttpCacheOptions products = provider.EnsureDomainOptions(http, "products");
        DomainHttpCacheOptions catalog = provider.EnsureDomainOptions(http, "catalog");

        products.Domain.Should().Be("products");
        catalog.Should().NotBeSameAs(products);
        catalog.Domain.Should().Be("catalog");
        catalog.OutputTtl.Should().Be(TimeSpan.FromSeconds(200));
        catalog.Version.Should().Be("c1");
        provider.GetDomainOptions(http).Should().BeSameAs(catalog);
    }

    [Fact]
    public void EnsureConfig_UsesDomainSpecificSettings()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions(), new CacheOrchestratorHttpOptions
        {
            DomainDefaults = new() { OutputCache = new() { TtlSeconds = 60 } },
            Domains = { ["products"] = new() { OutputCache = new() { TtlSeconds = 120 } } }
        });

        DomainHttpCacheOptions cfg = provider.EnsureDomainOptions(new DefaultHttpContext(), "products");

        cfg.Domain.Should().Be("products");
        cfg.OutputTtl.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void EnsureConfig_FallsBackToDomainDefaults_WhenDomainMissing()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions
        {
            DomainDefaults = new()
            {
                DataCache = new() { TtlSeconds = 300 }
            }
        }, new CacheOrchestratorHttpOptions
        {
            DomainDefaults = new() { OutputCache = new() { TtlSeconds = 90 } }
        });

        DomainHttpCacheOptions cfg = provider.EnsureDomainOptions(new DefaultHttpContext(), "unknown-domain");

        cfg.Domain.Should().Be("unknown-domain");
        cfg.OutputTtl.Should().Be(TimeSpan.FromSeconds(90));
        cfg.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void EnsureConfig_NormalizesDomainName()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions());
        DomainHttpCacheOptions cfg = provider.EnsureDomainOptions(new DefaultHttpContext(), "  Product-Catalog  ");

        cfg.Domain.Should().Be("product-catalog");
    }

    [Fact]
    public void EnsureConfig_UsesStableDefaultVersion_WhenNotConfigured()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions());
        DomainHttpCacheOptions cfg = provider.EnsureDomainOptions(new DefaultHttpContext(), "products");

        cfg.Version.Should().Be("1");
    }

    [Fact]
    public void EnsureConfig_UsesConfiguredVersion()
    {
        const string version = "v2";

        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions
        {
            Domains = { ["products"] = new() { Version = version } }
        });

        DomainHttpCacheOptions cfg = provider.EnsureDomainOptions(new DefaultHttpContext(), "products");

        cfg.Version.Should().Be(version);
    }

    [Fact]
    public void EnsureConfig_CachesPerDomain_AcrossDifferentHttpContexts()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions(), new CacheOrchestratorHttpOptions
        {
            Domains = { ["products"] = new() { OutputCache = new() { TtlSeconds = 150 } } }
        });

        DomainHttpCacheOptions cfg1 = provider.EnsureDomainOptions(new DefaultHttpContext(), "products");
        DomainHttpCacheOptions cfg2 = provider.EnsureDomainOptions(new DefaultHttpContext(), "products");

        cfg1.Should().BeSameAs(cfg2);
    }

    [Fact]
    public void EnsureConfig_CopiesMutableConfigurationArraysIntoSnapshot()
    {
        string[] headers = ["X-Tenant"];
        IRequestDomainCacheOptions provider = CreateProvider(
            new CacheOrchestratorOptions(),
            new CacheOrchestratorHttpOptions
            {
                DomainDefaults = new() { VaryByHeaders = headers }
            });

        DomainHttpCacheOptions snapshot = provider.EnsureDomainOptions(
            new DefaultHttpContext(),
            "products");
        headers[0] = "X-Changed";

        snapshot.VaryByHeaders.Should().Equal("X-Tenant");
    }

    [Fact]
    public void GetConfig_ReturnsNull_WhenNotYetEnsured()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions());
        DomainHttpCacheOptions? cfg = provider.GetDomainOptions(new DefaultHttpContext());

        cfg.Should().BeNull();
    }

    [Fact]
    public void GetConfig_ReturnsConfig_AfterEnsureConfig()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions());
        var http = new DefaultHttpContext();

        provider.EnsureDomainOptions(http, "products");
        DomainHttpCacheOptions? cfg = provider.GetDomainOptions(http);

        cfg.Should().NotBeNull();
        cfg.Domain.Should().Be("products");
    }

    [Fact]
    public void EnsureConfig_AppliesAllImportantDefaults()
    {
        IRequestDomainCacheOptions provider = CreateProvider(new CacheOrchestratorOptions());
        DomainHttpCacheOptions cfg = provider.EnsureDomainOptions(new DefaultHttpContext(), "products");

        cfg.OutputCacheEnabled.Should().BeTrue();
        cfg.DataCacheEnabled.Should().BeTrue();
        cfg.CacheableStatusCodes.Should().Contain(200);
        cfg.OutputTtl.Should().Be(TimeSpan.FromSeconds(3700));
        cfg.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(3800));
        cfg.EncodingNormalizationList.Should().Equal("br", "gzip");
        cfg.VaryByAccept.Should().BeTrue();
        cfg.AcceptNormalizationList.Should().BeNull();
        cfg.DataCacheRespectAuthBypass.Should().BeTrue();
        cfg.DataCacheRespectNoStore.Should().BeTrue();
        cfg.DataCacheVaryOnEncoding.Should().BeTrue();
        cfg.OutputCacheVaryByHost.Should().BeTrue();
    }

    [Fact]
    public void HandBuiltDomainCacheOptions_ShareProviderBoolDefaults()
    {
        DomainHttpCacheOptions opts = new();

        opts.OutputCacheEnabled.Should().BeTrue();
        opts.DataCacheEnabled.Should().BeTrue();
        opts.DataCacheRespectNoStore.Should().BeTrue();
        opts.DataCacheVaryOnEncoding.Should().BeTrue();
        opts.DataCacheVaryOnPublicAddress.Should().BeTrue();
        opts.OutputCacheVaryByHost.Should().BeTrue();
        opts.EncodingNormalizationList.Should().Equal("br", "gzip");
        opts.VaryByAccept.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("42", "42")]
    [InlineData("ABC-1", "ABC-1")]
    [InlineData("!!!", "!!!")]
    [InlineData("---", "---")]
    [InlineData("default", "default")]
    [InlineData("DEFAULT", "DEFAULT")]
    [InlineData("  opaque/id  ", "opaque/id")]
    public void NormalizeResourceId_DoesNotCollapseGarbageToDefault(string? input, string expected)
    {
        DomainName.NormalizeResourceId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("products", "products")]
    [InlineData("Items", "items")]
    [InlineData("!!!", "")]
    [InlineData("---", "")]
    [InlineData("default", "default")]
    public void NormalizeEntityKind_DoesNotCollapseGarbageToDefault(string? input, string expected)
    {
        DomainName.NormalizeEntityKind(input).Should().Be(expected);
    }

    // =========================
    // Config reload â†’ snapshot invalidation (L2 global cache)
    // =========================

    [Fact]
    public void OptionsMonitorChange_ClearsGlobalSnapshotCache_AndRebuildsWithNewVersion()
    {
        var initial = new CacheOrchestratorOptions
        {
            Domains =
            {
                ["catalog"] = new()
                {
                    Version = "v1",
                    DataCache = new() { TtlSeconds = 120 }
                }
            }
        };

        IRequestDomainCacheOptions provider = CreateProvider(initial, out TestOptionsMonitor monitor);

        DomainHttpCacheOptions before = provider.GetOrCreateDomainOptions("catalog");
        before.Version.Should().Be("v1");
        string versionHexBefore = before.VersionHex;

        // Same process cache hit (L2)
        provider.GetOrCreateDomainOptions("catalog").Should().BeSameAs(before);

        var reloaded = new CacheOrchestratorOptions
        {
            Domains =
            {
                ["catalog"] = new()
                {
                    Version = "v2",
                    DataCache = new() { TtlSeconds = 300 }
                }
            }
        };

        // Simulates IOptionsMonitor reload (appsettings change / sample playground save).
        monitor.TriggerChange(reloaded);

        DomainHttpCacheOptions after = provider.GetOrCreateDomainOptions("catalog");

        after.Should().NotBeSameAs(before, "global snapshot cache must be cleared on options change");
        after.Version.Should().Be("v2");
        after.VersionHex.Should().NotBe(versionHexBefore);
        after.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(300));

        // Subsequent calls share the new snapshot
        provider.GetOrCreateDomainOptions("catalog").Should().BeSameAs(after);
    }

    [Fact]
    public void OptionsMonitorChange_EnsureDomainOptions_OnNewRequest_UsesReloadedSnapshot()
    {
        var initial = new CacheOrchestratorOptions
        {
            Domains = { ["orders"] = new() { Version = "gen-a" } }
        };

        IRequestDomainCacheOptions provider = CreateProvider(initial, out TestOptionsMonitor monitor);

        DomainHttpCacheOptions firstRequest = provider.EnsureDomainOptions(new DefaultHttpContext(), "orders");
        firstRequest.Version.Should().Be("gen-a");

        monitor.TriggerChange(new CacheOrchestratorOptions
        {
            Domains = { ["orders"] = new() { Version = "gen-b" } }
        });

        DomainHttpCacheOptions secondRequest = provider.EnsureDomainOptions(new DefaultHttpContext(), "orders");

        secondRequest.Should().NotBeSameAs(firstRequest);
        secondRequest.Version.Should().Be("gen-b");
    }

    [Fact]
    public void OptionsMonitorChange_DoesNotReplaceSnapshotAlreadyPinnedOnHttpContext()
    {
        // L1 (ICacheOrchestratorFeature) is per-request: reload mid-request must not mutate the
        // options already attached to that context (stable for the remainder of the request).
        var initial = new CacheOrchestratorOptions
        {
            Domains = { ["live"] = new() { Version = "1" } }
        };

        IRequestDomainCacheOptions provider = CreateProvider(initial, out TestOptionsMonitor monitor);

        DefaultHttpContext http = new();
        DomainHttpCacheOptions pinned = provider.EnsureDomainOptions(http, "live");
        pinned.Version.Should().Be("1");

        monitor.TriggerChange(new CacheOrchestratorOptions
        {
            Domains = { ["live"] = new() { Version = "2" } }
        });

        // Same request still sees L1
        DomainHttpCacheOptions stillPinned = provider.EnsureDomainOptions(http, "live");
        stillPinned.Should().BeSameAs(pinned);
        stillPinned.Version.Should().Be("1");

        // Fresh request sees reloaded config
        DomainHttpCacheOptions fresh = provider.EnsureDomainOptions(new DefaultHttpContext(), "live");
        fresh.Version.Should().Be("2");
        fresh.Should().NotBeSameAs(pinned);
    }

    [Fact]
    public void OptionsMonitorChange_ClearsAllDomains_NotOnlyChangedOne()
    {
        var initial = new CacheOrchestratorOptions
        {
            Domains =
            {
                ["a"] = new() { Version = "a1" },
                ["b"] = new() { Version = "b1" }
            }
        };

        IRequestDomainCacheOptions provider = CreateProvider(initial, out TestOptionsMonitor monitor);

        DomainHttpCacheOptions a1 = provider.GetOrCreateDomainOptions("a");
        DomainHttpCacheOptions b1 = provider.GetOrCreateDomainOptions("b");

        monitor.TriggerChange(new CacheOrchestratorOptions
        {
            Domains =
            {
                ["a"] = new() { Version = "a2" },
                ["b"] = new() { Version = "b2" }
            }
        });

        DomainHttpCacheOptions a2 = provider.GetOrCreateDomainOptions("a");
        DomainHttpCacheOptions b2 = provider.GetOrCreateDomainOptions("b");

        a2.Should().NotBeSameAs(a1);
        b2.Should().NotBeSameAs(b1);
        a2.Version.Should().Be("a2");
        b2.Version.Should().Be("b2");
    }

    [Fact]
    public void CoreRuntimeOverride_RebuildsComposedHttpSnapshot()
    {
        var coreMonitor = new TestOptionsMonitor(new CacheOrchestratorOptions());
        var httpMonitor = new HttpTestOptionsMonitor(new CacheOrchestratorHttpOptions());
        var coreOverrides = new DomainRuntimeOverrideStore();
        DomainCacheOptionsProvider inner = new(
            coreMonitor,
            NullLogger<DomainCacheOptionsProvider>.Instance,
            coreOverrides);
        using var provider = new RequestDomainCacheOptionsProvider(
            inner,
            coreMonitor,
            httpMonitor,
            NullLogger<RequestDomainCacheOptionsProvider>.Instance,
            coreOverrides,
            new HttpDomainRuntimeOverrideStore());

        DomainHttpCacheOptions before = provider.GetOrCreateDomainOptions("products");
        coreOverrides.PatchSettings("products", new DomainSettingsPatch
        {
            DataCacheTtl = TimeSpan.FromSeconds(17)
        });

        DomainHttpCacheOptions after = provider.GetOrCreateDomainOptions("products");

        after.Should().NotBeSameAs(before);
        after.DataCacheTtl.Should().Be(TimeSpan.FromSeconds(17));
    }

    // =========================
    // Helpers
    // =========================

    private static IRequestDomainCacheOptions CreateProvider(CacheOrchestratorOptions options)
        => CreateProvider(options, new CacheOrchestratorHttpOptions());

    private static IRequestDomainCacheOptions CreateProvider(
        CacheOrchestratorOptions options,
        CacheOrchestratorHttpOptions httpOptions)
    {
        var monitor = new TestOptionsMonitor(options);
        var httpMonitor = new HttpTestOptionsMonitor(httpOptions);
        DomainCacheOptionsProvider inner = new(monitor, NullLogger<DomainCacheOptionsProvider>.Instance);
        return new RequestDomainCacheOptionsProvider(
            inner,
            monitor,
            httpMonitor,
            NullLogger<RequestDomainCacheOptionsProvider>.Instance,
            new DomainRuntimeOverrideStore(),
            new HttpDomainRuntimeOverrideStore());
    }

    private static IRequestDomainCacheOptions CreateProvider(CacheOrchestratorOptions options, out TestOptionsMonitor monitor)
    {
        monitor = new TestOptionsMonitor(options);
        var httpMonitor = new HttpTestOptionsMonitor(new CacheOrchestratorHttpOptions());
        DomainCacheOptionsProvider inner = new(monitor, NullLogger<DomainCacheOptionsProvider>.Instance);
        return new RequestDomainCacheOptionsProvider(
            inner,
            monitor,
            httpMonitor,
            NullLogger<RequestDomainCacheOptionsProvider>.Instance,
            new DomainRuntimeOverrideStore(),
            new HttpDomainRuntimeOverrideStore());
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<CacheOrchestratorOptions>
    {
        private event Action<CacheOrchestratorOptions, string?>? _onChange;

        public TestOptionsMonitor(CacheOrchestratorOptions current)
        {
            CurrentValue = current;
        }

        public CacheOrchestratorOptions CurrentValue { get; private set; }
        public CacheOrchestratorOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<CacheOrchestratorOptions, string?> listener)
        {
            _onChange += listener;
            return new Subscription(() => _onChange -= listener);
        }

        public void TriggerChange(CacheOrchestratorOptions newOptions)
        {
            CurrentValue = newOptions;
            _onChange?.Invoke(newOptions, null);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class HttpTestOptionsMonitor : IOptionsMonitor<CacheOrchestratorHttpOptions>
    {
        private event Action<CacheOrchestratorHttpOptions, string?>? _onChange;

        public HttpTestOptionsMonitor(CacheOrchestratorHttpOptions current) => CurrentValue = current;

        public CacheOrchestratorHttpOptions CurrentValue { get; private set; }
        public CacheOrchestratorHttpOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<CacheOrchestratorHttpOptions, string?> listener)
        {
            _onChange += listener;
            return new HttpSubscription(() => _onChange -= listener);
        }

        public void TriggerChange(CacheOrchestratorHttpOptions newOptions)
        {
            CurrentValue = newOptions;
            _onChange?.Invoke(newOptions, null);
        }

        private sealed class HttpSubscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
