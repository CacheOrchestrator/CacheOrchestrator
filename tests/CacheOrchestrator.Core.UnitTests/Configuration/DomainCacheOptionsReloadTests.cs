using CacheOrchestrator.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Core.UnitTests.Configuration;

public sealed class DomainCacheOptionsReloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_InFlightOldSnapshotCannotRepopulateCurrentGeneration(bool publishNewFirst)
    {
        CacheOrchestratorOptions current = new() { DomainDefaults = new() { Version = "old" } };
        IOptionsMonitor<CacheOrchestratorOptions> monitor = Substitute.For<IOptionsMonitor<CacheOrchestratorOptions>>();
        Action<CacheOrchestratorOptions, string?>? changed = null;
        monitor.OnChange(Arg.Do<Action<CacheOrchestratorOptions, string?>>(callback => changed = callback))
            .Returns(Substitute.For<IDisposable>());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        int reads = 0;
        monitor.CurrentValue.Returns(_ =>
        {
            CacheOrchestratorOptions captured = Volatile.Read(ref current);
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();
            }
            return captured;
        });
        using DomainCacheOptionsProvider provider = new(monitor, NullLogger<DomainCacheOptionsProvider>.Instance);
        Task<DomainCacheOptions> slow = Task.Run(
            () => provider.GetOrCreateDomainOptions("catalog"), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Volatile.Write(ref current, new() { DomainDefaults = new() { Version = "new" } });
            changed!(current, null);
            DomainCacheOptions? before = publishNewFirst ? provider.GetOrCreateDomainOptions("catalog") : null;
            release.Set();
            (await slow).Version.Should().Be("old");
            DomainCacheOptions fresh = provider.GetOrCreateDomainOptions("catalog");
            fresh.Version.Should().Be("new");
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
}
