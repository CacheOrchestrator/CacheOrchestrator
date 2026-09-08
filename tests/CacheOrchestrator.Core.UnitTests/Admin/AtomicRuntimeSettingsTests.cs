using CacheOrchestrator.Admin;

namespace CacheOrchestrator.Core.UnitTests.Admin;

public class AtomicRuntimeSettingsTests
{
    private sealed record First(int Value);
    private sealed record Second(int Value);

    [Fact]
    public void FailedPreparation_PublishesNothing_AndRetainedContextCannotMutate()
    {
        var store = new DomainRuntimeOverrideStore();
        store.Update("catalog", context => context.Set(new First(1)));
        int before = store.GetStamp("catalog");
        DomainSettingsPatchContext captured = null!;
        Action change = () => store.Update("catalog", context =>
        {
            captured = context;
            context.Set(new First(2));
            context.Set(new Second(2));
            throw new ArgumentException("invalid last section");
        });
        change.Should().Throw<ArgumentException>();
        store.GetSettings<First>("catalog")!.Value.Should().Be(1);
        store.GetSettings<Second>("catalog").Should().BeNull();
        store.GetStamp("catalog").Should().Be(before);
        Action late = () => captured.Set(new First(3));
        late.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ConcurrentChanges_SerializePreparation_AndPreserveBothSections()
    {
        var store = new DomainRuntimeOverrideStore();
        store.Update("catalog", context => context.Set(new First(0)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task first = Task.Run(() => store.Update("catalog", context =>
        {
            context.Set(new First(1));
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                throw new TimeoutException();
            context.Set(new Second(1));
        }), cancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            store.GetSettings<First>("catalog")!.Value.Should().Be(0);
            store.GetSettings<Second>("catalog").Should().BeNull();
            Task second = Task.Run(() => store.Update("catalog", context =>
            {
                context.Get<First>()!.Value.Should().Be(1);
                context.Set(new First(2));
            }), cancellationToken);
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            store.GetSettings<First>("catalog")!.Value.Should().Be(2);
            store.GetSettings<Second>("catalog")!.Value.Should().Be(1);
        }
        finally
        {
            release.Set();
            await first;
        }
    }

    [Fact]
    public void Clear_RemovesEverySection_AndSubsequentMutationStartsEmpty()
    {
        var store = new DomainRuntimeOverrideStore();
        store.SetVersion("catalog", "2");
        store.Update("catalog", context => context.Set(new First(1)));
        store.Clear("catalog").Should().BeTrue();
        store.Get("catalog").Should().BeNull();
        store.GetSettings<First>("catalog").Should().BeNull();
        store.GetOverriddenDomains().Should().BeEmpty();
        store.Update("catalog", context => context.Set(new Second(2)));
        store.GetSettings<First>("catalog").Should().BeNull();
        store.GetSettings<Second>("catalog")!.Value.Should().Be(2);
    }

    [Fact]
    public void NestedMutation_IsRejectedWithoutPublishingEitherChange()
    {
        var store = new DomainRuntimeOverrideStore();
        Action change = () => store.Update("catalog", context =>
        {
            context.Set(new First(1));
            store.SetVersion("catalog", "2");
        });
        change.Should().Throw<InvalidOperationException>();
        store.GetSettings<First>("catalog").Should().BeNull();
        store.Get("catalog").Should().BeNull();
    }
}
