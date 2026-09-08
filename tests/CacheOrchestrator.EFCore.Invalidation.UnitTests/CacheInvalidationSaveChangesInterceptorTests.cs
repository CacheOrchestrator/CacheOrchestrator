using CacheOrchestrator.EFCore;
using CacheOrchestrator.Invalidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.EFCore.Invalidation.UnitTests;

public class CacheInvalidationSaveChangesInterceptorTests
{
    [Fact]
    public async Task SaveChanges_Product_InvalidatesThatEntity()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        db.Products.Add(new Product { Name = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Is<IEnumerable<string>>(ids => ids.Single() == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChanges_AddedIdentity_UsesPostSavePrimaryKey()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        Product product = new() { Name = "A" };
        db.Products.Add(product);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        product.Id.Should().BeGreaterThan(0);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Is<IEnumerable<string>>(ids => ids.Single() == product.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChanges_TwoProducts_BatchesOneCall()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        db.Products.Add(new Product { Name = "A" });
        db.Products.Add(new Product { Name = "B" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Is<IEnumerable<string>>(ids => ids.OrderBy(x => x).SequenceEqual(new[] { "1", "2" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChanges_ProductAndAsset_TwoGroups()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        db.Products.Add(new Product { Name = "A" });
        db.Assets.Add(new Asset { Title = "T" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "products", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "assets", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChanges_UnmappedEntity_DoesNotInvalidate()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        db.Notes.Add(new Note { Text = "x" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntityKindAsync(default!, default!, TestContext.Current.CancellationToken);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateDomainAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SaveChanges_Delete_UsesPrimaryKeyFromSavingChanges()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        Product product = new() { Name = "A" };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        inv.ClearReceivedCalls();

        db.Products.Remove(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Is<IEnumerable<string>>(ids => ids.Single() == product.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveChanges_Failed_DoesNotInvalidate()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) =
            CreateHarness(extraInterceptor: new ThrowingSaveChangesInterceptor());

        db.Products.Add(new Product { Name = "A" });
        Func<Task> act = () => db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnBulkKind_OverThreshold_InvalidatesKind()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness(onBulk: EfCoreOnBulk.Kind, threshold: 2);

        db.Products.Add(new Product { Name = "A" });
        db.Products.Add(new Product { Name = "B" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntityKindAsync("store", "products", Arg.Any<CancellationToken>());
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateDomainAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentContexts_DoNotCrossSnapshots()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv);

        await using TestDbContext db1 = CreateContext(interceptor, "c1");
        await using TestDbContext db2 = CreateContext(interceptor, "c2");

        db1.Products.Add(new Product { Name = "A" });
        db2.Assets.Add(new Asset { Title = "T" });

        await Task.WhenAll(
            db1.SaveChangesAsync(TestContext.Current.CancellationToken),
            db2.SaveChangesAsync(TestContext.Current.CancellationToken));

        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "products", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "assets", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidatorThrows_SaveStillSucceeds()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        inv.InvalidateEntitiesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromException<CacheInvalidationResult>(
                new InvalidOperationException("inv boom")));

        await using TestDbContext db = CreateContext(CreateInterceptor(inv), "throw");
        db.Products.Add(new Product { Name = "A" });

        int written = await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        written.Should().Be(1);
        db.Products.Should().ContainSingle();
    }

    [Fact]
    public async Task InvalidatorCancelsAfterCommit_SaveStillSucceeds()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        inv.InvalidateEntitiesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<CacheInvalidationResult>(new OperationCanceledException("post-commit")));

        await using TestDbContext db = CreateContext(CreateInterceptor(inv), "cancel-after-commit");
        db.Products.Add(new Product { Name = "A" });

        int written = await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        written.Should().Be(1);
        db.Products.Should().ContainSingle();
        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Any<IEnumerable<string>>(),
            Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
    }

    [Fact]
    public async Task CompositeKey_FormatsJoinedResourceId()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        db.Lines.Add(new OrderLine { OrderId = 10, LineNo = 2, Sku = "x" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "lines",
            Arg.Is<IEnumerable<string>>(ids => ids.Single() == "10:2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_DoesNotInvalidate()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness(enabled: false);

        db.Products.Add(new Product { Name = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnBulkKind_UnderThreshold_InvalidatesEntities()
    {
        (TestDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness(onBulk: EfCoreOnBulk.Kind, threshold: 5);

        db.Products.Add(new Product { Name = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "products", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntityKindAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OwnedEntity_IsNotInvalidated()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv);
        await using OwnedDbContext db = new(
            new DbContextOptionsBuilder<OwnedDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .AddInterceptors(interceptor)
                .Options);

        db.Orders.Add(new Order { Address = new Address { City = "Ljubljana" } });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "orders", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.DidNotReceive().InvalidateEntitiesAsync(
            "store", "addresses", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    private static (TestDbContext Db, ICacheOrchestratorInvalidator Inv) CreateHarness(
        EfCoreOnBulk onBulk = EfCoreOnBulk.Entities,
        int threshold = 20,
        bool enabled = true,
        IInterceptor? extraInterceptor = null)
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv, onBulk, threshold, enabled);
        TestDbContext db = CreateContext(interceptor, Guid.NewGuid().ToString("N"), extraInterceptor);
        return (db, inv);
    }

    private static ICacheOrchestratorInvalidator CreateInvalidator()
    {
        ICacheOrchestratorInvalidator inv = Substitute.For<ICacheOrchestratorInvalidator>();
        CacheInvalidationResult ok = new("ok", [], true, true, []);
        inv.InvalidateEntitiesAsync(default!, default!, default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ValueTask.FromResult(ok));
        inv.InvalidateEntityKindAsync(default!, default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ValueTask.FromResult(ok));
        inv.InvalidateDomainAsync(default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ValueTask.FromResult(ok));
        return inv;
    }

    private static CacheInvalidationSaveChangesInterceptor CreateInterceptor(
        ICacheOrchestratorInvalidator inv,
        EfCoreOnBulk onBulk = EfCoreOnBulk.Entities,
        int threshold = 20,
        bool enabled = true)
    {
        IOptionsMonitor<EfCoreInvalidationOptions> monitor = Substitute.For<IOptionsMonitor<EfCoreInvalidationOptions>>();
        monitor.CurrentValue.Returns(new EfCoreInvalidationOptions
        {
            Enabled = enabled,
            BulkThreshold = threshold,
            OnBulk = onBulk
        });

        EntityCacheMappingResolver resolver = new(monitor);
        return new CacheInvalidationSaveChangesInterceptor(
            new CacheInvalidationTransactionInterceptor(inv, NullLogger<CacheInvalidationTransactionInterceptor>.Instance),
            resolver,
            monitor,
            NullLogger<CacheInvalidationSaveChangesInterceptor>.Instance);
    }

    private static TestDbContext CreateContext(
        CacheInvalidationSaveChangesInterceptor interceptor,
        string dbName,
        IInterceptor? extra = null)
    {
        DbContextOptionsBuilder<TestDbContext> builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptor);

        if (extra is not null)
            builder.AddInterceptors(extra);

        return new TestDbContext(builder.Options);
    }

    [CacheEntity("store", "products")]
    public sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [CacheEntity("store", "assets")]
    public sealed class Asset
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
    }

    [CacheEntity("store", "lines")]
    public sealed class OrderLine
    {
        public int OrderId { get; set; }
        public int LineNo { get; set; }
        public string Sku { get; set; } = "";
    }

    public sealed class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    [CacheEntity("store", "orders")]
    public sealed class Order
    {
        public int Id { get; set; }
        public Address Address { get; set; } = new();
    }

    [CacheEntity("store", "addresses")]
    public sealed class Address
    {
        public string City { get; set; } = "";
    }

    public sealed class OwnedDbContext : DbContext
    {
        public OwnedDbContext(DbContextOptions<OwnedDbContext> options)
            : base(options)
        {
        }

        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().OwnsOne(o => o.Address);
        }
    }

    public sealed class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options)
        {
        }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<Asset> Assets => Set<Asset>();
        public DbSet<OrderLine> Lines => Set<OrderLine>();
        public DbSet<Note> Notes => Set<Note>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OrderLine>().HasKey(l => new { l.OrderId, l.LineNo });
        }
    }

    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result) =>
            throw new InvalidOperationException("save failed");

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("save failed");
    }
}
