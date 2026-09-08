using CacheOrchestrator.EFCore;
using CacheOrchestrator.Invalidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Transactions;

namespace CacheOrchestrator.EFCore.Invalidation.UnitTests;

public sealed class TransactionInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitTransaction_DefersAndCoalescesSavedChangesUntilCommit(bool asynchronous)
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using TestDb db = CreateSqlite(services, connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Row first = new() { Name = "first" };
        db.Rows.Add(first);
        if (asynchronous)
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        else
            db.SaveChanges();
        first.Id.Should().BeGreaterThan(0);
        first.Name = "updated";
        Row second = new() { Name = "second" };
        db.Rows.Add(second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        calls.Should().BeEmpty();
        if (asynchronous)
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        else
            transaction.Commit();
        calls.Should().ContainSingle();
        calls.Single().Should().BeEquivalentTo(first.Id.ToString(), second.Id.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackOrDisposal_DiscardsWork_AndContextCanBeReused(bool explicitRollback)
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using TestDb db = CreateSqlite(services, connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            db.Rows.Add(new() { Name = "rolled back" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            calls.Should().BeEmpty();
            if (explicitRollback)
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        calls.Should().BeEmpty();
        db.ChangeTracker.Clear();
        (await db.Rows.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        db.Rows.Add(new() { Name = "committed" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        calls.Should().ContainSingle();
    }

    [Fact]
    public async Task FailedSave_DiscardsOnlyFailedCapture_AndPreservesEarlierTransactionWork()
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using TestDb db = CreateSqlite(services, connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Row committed = new() { Name = "kept" };
        db.Rows.Add(committed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Row invalid = new() { Name = null! };
        db.Rows.Add(invalid);
        Func<Task> save = () => db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await save.Should().ThrowAsync<DbUpdateException>();
        calls.Should().BeEmpty();
        db.Entry(invalid).State = EntityState.Detached;
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        calls.Should().ContainSingle();
        calls.Single().Should().Equal(committed.Id.ToString());
    }

    [Fact]
    public async Task SharedTransaction_CollectsChangesFromBothContexts()
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using TestDb owner = CreateSqlite(services, connection);
        await owner.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using IDbContextTransaction transaction = await owner.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using TestDb participant = CreateSqlite(services, connection);
        await participant.Database.UseTransactionAsync(transaction.GetDbTransaction(), TestContext.Current.CancellationToken);
        Row first = new() { Name = "owner" };
        Row second = new() { Name = "participant" };
        owner.Rows.Add(first);
        participant.Rows.Add(second);
        await owner.SaveChangesAsync(TestContext.Current.CancellationToken);
        await participant.SaveChangesAsync(TestContext.Current.CancellationToken);
        calls.Should().BeEmpty();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        calls.Should().ContainSingle();
        calls.Single().Should().BeEquivalentTo(first.Id.ToString(), second.Id.ToString());
    }

    [Fact]
    public async Task SavepointRollback_DoesNotPublishUntilFinalCommit()
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using TestDb db = CreateSqlite(services, connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Row row = new() { Name = "kept" };
        db.Rows.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CreateSavepointAsync("kept", TestContext.Current.CancellationToken);
        row.Name = "discarded";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.RollbackToSavepointAsync("kept", TestContext.Current.CancellationToken);
        calls.Should().BeEmpty();
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        calls.Should().ContainSingle();
        db.ChangeTracker.Clear();
        (await db.Rows.SingleAsync(TestContext.Current.CancellationToken)).Name.Should().Be("kept");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbientNotifications_PublishOnCommit_AndDiscardOnRollback(bool commit)
    {
        // InMemory models SavedChanges only; System.Transactions supplies the real notification protocol.
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        await using TestDb db = CreateMemory(services);
        using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
        {
            db.Rows.Add(new() { Name = "ambient" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            calls.Should().BeEmpty();
            if (commit)
                scope.Complete();
        }
        calls.Count.Should().Be(commit ? 1 : 0);
    }

    [Fact]
    public async Task AmbientRequiresNew_CommitsIndependentlyOfOuterRollback()
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        using (TransactionScope outer = new(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using TestDb first = CreateMemory(services);
            first.Rows.Add(new() { Id = 41, Name = "outer" });
            await first.SaveChangesAsync(TestContext.Current.CancellationToken);
            using (TransactionScope inner = new(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled))
            {
                await using TestDb second = CreateMemory(services);
                second.Rows.Add(new() { Id = 42, Name = "inner" });
                await second.SaveChangesAsync(TestContext.Current.CancellationToken);
                calls.Should().BeEmpty();
                inner.Complete();
            }
            calls.Should().ContainSingle();
            calls.Single().Should().Equal("42");
        }
        calls.Should().ContainSingle();
    }

    [Fact]
    public async Task AmbientCompletion_RunsAfterLaterParticipantOutcomeNotification()
    {
        ConcurrentQueue<string[]> calls = new();
        await using ServiceProvider services = Build(calls);
        using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using TestDb db = CreateMemory(services);
            db.Rows.Add(new() { Name = "ambient" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Transaction.Current!.EnlistVolatile(
                new LaterParticipant(() => calls.Should().BeEmpty("other participants receive the commit before cache invalidation")),
                EnlistmentOptions.None);
            scope.Complete();
        }
        calls.Should().ContainSingle();
    }

    private sealed class LaterParticipant(Action onCommit) : IEnlistmentNotification
    {
        public void Prepare(PreparingEnlistment preparingEnlistment) => preparingEnlistment.Prepared();
        public void Commit(Enlistment enlistment) { onCommit(); enlistment.Done(); }
        public void Rollback(Enlistment enlistment) => enlistment.Done();
        public void InDoubt(Enlistment enlistment) => enlistment.Done();
    }

    private static ServiceProvider Build(ConcurrentQueue<string[]> calls)
    {
        ICacheOrchestratorInvalidator invalidator = Substitute.For<ICacheOrchestratorInvalidator>();
        invalidator.InvalidateEntitiesAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            calls.Enqueue(call.ArgAt<IEnumerable<string>>(2).ToArray());
            call.ArgAt<CancellationToken>(3).CanBeCanceled.Should().BeFalse();
            return ValueTask.FromResult(new CacheInvalidationResult("store", [], true, true));
        });
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(invalidator);
        services.AddCacheOrchestratorEfCoreInvalidation(
            new ConfigurationBuilder().Build(), options => options.OnBulk = EfCoreOnBulk.Entities);
        return services.BuildServiceProvider();
    }

    private static TestDb CreateSqlite(IServiceProvider services, SqliteConnection connection)
    {
        DbContextOptionsBuilder<TestDb> options = new();
        options.UseSqlite(connection);
        options.AddCacheOrchestratorInvalidation(services);
        return new TestDb(options.Options);
    }

    private static TestDb CreateMemory(IServiceProvider services)
    {
        DbContextOptionsBuilder<TestDb> options = new();
        options.UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        options.AddCacheOrchestratorInvalidation(services);
        return new TestDb(options.Options);
    }

    public sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();
    }

    [CacheEntity("store", "items")]
    public sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
