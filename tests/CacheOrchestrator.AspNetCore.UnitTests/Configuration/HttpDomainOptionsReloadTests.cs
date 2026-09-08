using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.AspNetCore.UnitTests.Configuration;

public sealed class HttpDomainOptionsReloadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Reload_OldResolutionCannotRepopulateCurrentHttpGeneration(bool reloadCore, bool publishNewFirst)
    {
        TestMonitor<CacheOrchestratorOptions> coreMonitor = new();
        TestMonitor<CacheOrchestratorHttpOptions> httpMonitor = new();
        IDomainCacheOptionsProvider coreProvider = Substitute.For<IDomainCacheOptionsProvider>();

        DomainCacheOptions currentCore = new() { Domain = "catalog", Version = "old" };
        CacheOrchestratorHttpOptions currentHttp = new()
        {
            DomainDefaults = new() { OutputCache = new() { TtlSeconds = 30 } }
        };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        int reads = 0;
        void PauseFirstResolution()
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();
            }
        }
        coreProvider.GetOrCreateDomainOptions("catalog").Returns(_ =>
        {
            DomainCacheOptions captured = Volatile.Read(ref currentCore);
            if (reloadCore)
                PauseFirstResolution();
            return captured;
        });
        httpMonitor.Read = () =>
        {
            CacheOrchestratorHttpOptions captured = Volatile.Read(ref currentHttp);
            if (!reloadCore)
                PauseFirstResolution();
            return captured;
        };
        using RequestDomainCacheOptionsProvider provider = new(
            coreProvider, coreMonitor, httpMonitor, NullLogger<RequestDomainCacheOptionsProvider>.Instance,
            new DomainRuntimeOverrideStore(), new HttpDomainRuntimeOverrideStore());
        Task<DomainHttpCacheOptions> slow = Task.Run(
            () => provider.GetOrCreateDomainOptions("catalog"), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (reloadCore)
            {
                Volatile.Write(ref currentCore, new() { Domain = "catalog", Version = "new" });
                coreMonitor.Notify(new());
            }
            else
            {
                Volatile.Write(ref currentHttp, new()
                {
                    DomainDefaults = new() { OutputCache = new() { TtlSeconds = 120 } }
                });
                httpMonitor.Notify(currentHttp);
            }
            DomainHttpCacheOptions? before = publishNewFirst ? provider.GetOrCreateDomainOptions("catalog") : null;
            release.Set();
            DomainHttpCacheOptions old = await slow;
            old.Version.Should().Be("old");
            old.OutputTtl.Should().Be(TimeSpan.FromSeconds(30));
            DomainHttpCacheOptions fresh = provider.GetOrCreateDomainOptions("catalog");
            fresh.Version.Should().Be(reloadCore ? "new" : "old");
            fresh.OutputTtl.Should().Be(TimeSpan.FromSeconds(reloadCore ? 30 : 120));
            if (before is not null)
                fresh.Should().BeSameAs(before);
            provider.GetOrCreateDomainOptions("catalog").Should().BeSameAs(fresh);
        }
        finally
        {
            release.Set();
            await slow;
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentMutation_RetriesMixedCoreAndHttpSnapshot(bool clearAfterChange)
    {
        var state = new DomainRuntimeOverrideStore();
        TestMonitor<CacheOrchestratorOptions> coreMonitor = new();
        TestMonitor<CacheOrchestratorHttpOptions> httpMonitor = new() { Read = () => new() };
        IDomainCacheOptionsProvider coreProvider = Substitute.For<IDomainCacheOptionsProvider>();
        int reads = 0;
        coreProvider.GetOrCreateDomainOptions("catalog").Returns(_ =>
        {
            DomainCacheOptions captured = new() { Domain = "catalog", Version = state.Get("catalog")?.Version ?? "old" };
            if (Interlocked.Increment(ref reads) == 1)
            {
                state.Update("catalog", context =>
                {
                    context.Set(new DomainRuntimeOverride { Version = "new", Stamp = context.Stamp });
                    context.Set(new HttpDomainRuntimeOverride { OutputCacheTtl = TimeSpan.FromSeconds(120), Stamp = context.Stamp });
                });
                if (clearAfterChange)
                    state.Clear("catalog");
            }
            return captured;
        });
        using var provider = new RequestDomainCacheOptionsProvider(
            coreProvider, coreMonitor, httpMonitor, NullLogger<RequestDomainCacheOptionsProvider>.Instance,
            state, new HttpDomainRuntimeOverrideStore(state));
        DomainHttpCacheOptions resolved = provider.GetOrCreateDomainOptions("catalog");
        reads.Should().Be(2);
        resolved.Version.Should().Be(clearAfterChange ? "old" : "new");
        resolved.OutputTtl.Should().Be(TimeSpan.FromSeconds(clearAfterChange ? 3700 : 120));
        provider.GetOrCreateDomainOptions("catalog").Should().BeSameAs(resolved);
    }

    private sealed class TestMonitor<T> : IOptionsMonitor<T> where T : class
    {
        public Func<T> Read { get; set; } = () => throw new InvalidOperationException("No read configured.");
        private event Action<T, string?>? Changed;
        public T CurrentValue => Read();
        public T Get(string? name) => Read();
        public void Notify(T value) => Changed?.Invoke(value, null);
        public IDisposable OnChange(Action<T, string?> listener)
        {
            Changed += listener;
            return new Subscription(() => Changed -= listener);
        }
        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
