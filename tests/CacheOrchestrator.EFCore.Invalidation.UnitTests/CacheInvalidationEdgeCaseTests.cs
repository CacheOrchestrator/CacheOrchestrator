using CacheOrchestrator.EFCore;
using CacheOrchestrator.Invalidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.EFCore.Invalidation.UnitTests;

public class CacheInvalidationEdgeCaseTests
{
    [Fact]
    public void SaveChanges_Sync_Invalidates()
    {
        (HarnessDbContext db, ICacheOrchestratorInvalidator inv) = CreateHarness();

        db.Products.Add(new Product { Name = "A" });
        db.SaveChanges();

        inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Is<IEnumerable<string>>(ids => ids.Single() == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnBulkDomain_OverThreshold_InvalidatesDomain()
    {
        (HarnessDbContext db, ICacheOrchestratorInvalidator inv) =
            CreateHarness(onBulk: EfCoreOnBulk.Domain, threshold: 2);

        db.Products.Add(new Product { Name = "A" });
        db.Products.Add(new Product { Name = "B" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateDomainAsync("store", Arg.Any<CancellationToken>());
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntityKindAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteUpdate_DoesNotInvalidate()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        await using SqliteHarness sqlite = await SqliteHarness.CreateAsync(CreateInterceptor(inv));
        HarnessDbContext db = sqlite.Db;

        db.Products.Add(new Product { Name = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        inv.ClearReceivedCalls();

        int updated = await db.Products
            .Where(p => p.Name == "A")
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Name, "B"),
                TestContext.Current.CancellationToken);

        updated.Should().Be(1);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntityKindAsync(default!, default!, TestContext.Current.CancellationToken);
        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateDomainAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FluentMapping_InvalidatesWithoutAttribute()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv);
        await using FluentDbContext db = new(
            new DbContextOptionsBuilder<FluentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .AddInterceptors(interceptor)
                .Options);

        db.Widgets.Add(new Widget { Label = "w" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store",
            "widgets",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tph_ConcreteAttribute_InvalidatesDerivedKindOnly()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv);
        await using TphDbContext db = new(
            new DbContextOptionsBuilder<TphDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .AddInterceptors(interceptor)
                .Options);

        db.Animals.Add(new Dog { Name = "Rex" });
        db.Animals.Add(new Cat { Lives = 9 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "dogs", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "cats", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.DidNotReceive().InvalidateEntitiesAsync(
            "store", "animals", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoContextTypes_ShareInterceptorWithoutCrossing()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv);

        await using HarnessDbContext products = new(
            new DbContextOptionsBuilder<HarnessDbContext>()
                .UseInMemoryDatabase("two-a-" + Guid.NewGuid().ToString("N"))
                .AddInterceptors(interceptor)
                .Options);
        await using SecondDbContext extras = new(
            new DbContextOptionsBuilder<SecondDbContext>()
                .UseInMemoryDatabase("two-b-" + Guid.NewGuid().ToString("N"))
                .AddInterceptors(interceptor)
                .Options);

        products.Products.Add(new Product { Name = "A" });
        extras.Extras.Add(new Extra { Code = "x" });
        await products.SaveChangesAsync(TestContext.Current.CancellationToken);
        await extras.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "products", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await inv.Received(1).InvalidateEntitiesAsync(
            "store", "extras", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PooledContext_SequentialSaves_InvalidateEachTime()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        string dbName = "pool-" + Guid.NewGuid().ToString("N");

        ServiceCollection services = new();
        services.AddSingleton(inv);
        services.AddSingleton(CreateInterceptor(inv));
        services.AddDbContextPool<HarnessDbContext>((sp, opt) =>
        {
            opt.UseInMemoryDatabase(dbName);
            opt.AddInterceptors(sp.GetRequiredService<CacheInvalidationSaveChangesInterceptor>());
        });

        await using ServiceProvider sp = services.BuildServiceProvider();

        using (IServiceScope scope1 = sp.CreateScope())
        {
            HarnessDbContext db = scope1.ServiceProvider.GetRequiredService<HarnessDbContext>();
            db.Products.Add(new Product { Name = "A" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope2 = sp.CreateScope())
        {
            HarnessDbContext db = scope2.ServiceProvider.GetRequiredService<HarnessDbContext>();
            Product row = await db.Products.SingleAsync(TestContext.Current.CancellationToken);
            row.Name = "B";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await inv.Received(2).InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransactionRollback_AfterSaveChanges_DoesNotInvalidatePrematurely()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        await using SqliteHarness sqlite = await SqliteHarness.CreateAsync(CreateInterceptor(inv));
        HarnessDbContext db = sqlite.Db;

        db.Products.Add(new Product { Name = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        inv.ClearReceivedCalls();

        await using IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Product row = await db.Products.SingleAsync(TestContext.Current.CancellationToken);
        row.Name = "B";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await tx.RollbackAsync(TestContext.Current.CancellationToken);

        await inv.DidNotReceive().InvalidateEntitiesAsync(
            "store",
            "products",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutAttachedInterceptor_DoesNotInvalidate()
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        await using HarnessDbContext db = new(
            new DbContextOptionsBuilder<HarnessDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);

        db.Products.Add(new Product { Name = "A" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await inv.DidNotReceiveWithAnyArgs()
            .InvalidateEntitiesAsync(default!, default!, default!, TestContext.Current.CancellationToken);
    }

    private static (HarnessDbContext Db, ICacheOrchestratorInvalidator Inv) CreateHarness(
        EfCoreOnBulk onBulk = EfCoreOnBulk.Entities,
        int threshold = 20)
    {
        ICacheOrchestratorInvalidator inv = CreateInvalidator();
        CacheInvalidationSaveChangesInterceptor interceptor = CreateInterceptor(inv, onBulk, threshold);
        HarnessDbContext db = new(
            new DbContextOptionsBuilder<HarnessDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .AddInterceptors(interceptor)
                .Options);
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
        int threshold = 20)
    {
        IOptionsMonitor<EfCoreInvalidationOptions> monitor = Substitute.For<IOptionsMonitor<EfCoreInvalidationOptions>>();
        monitor.CurrentValue.Returns(new EfCoreInvalidationOptions
        {
            Enabled = true,
            BulkThreshold = threshold,
            OnBulk = onBulk
        });
        return new CacheInvalidationSaveChangesInterceptor(
            new CacheInvalidationTransactionInterceptor(inv, NullLogger<CacheInvalidationTransactionInterceptor>.Instance),
            new EntityCacheMappingResolver(monitor),
            monitor,
            NullLogger<CacheInvalidationSaveChangesInterceptor>.Instance);
    }

    [CacheEntity("store", "products")]
    public sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class Widget
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    public abstract class Animal
    {
        public int Id { get; set; }
    }

    [CacheEntity("store", "dogs")]
    public sealed class Dog : Animal
    {
        public string Name { get; set; } = "";
    }

    [CacheEntity("store", "cats")]
    public sealed class Cat : Animal
    {
        public int Lives { get; set; }
    }

    [CacheEntity("store", "extras")]
    public sealed class Extra
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
    }

    public sealed class HarnessDbContext : DbContext
    {
        public HarnessDbContext(DbContextOptions<HarnessDbContext> options)
            : base(options)
        {
        }

        public DbSet<Product> Products => Set<Product>();
    }

    public sealed class FluentDbContext : DbContext
    {
        public FluentDbContext(DbContextOptions<FluentDbContext> options)
            : base(options)
        {
        }

        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>().CacheInvalidate("store", "widgets");
        }
    }

    public sealed class TphDbContext : DbContext
    {
        public TphDbContext(DbContextOptions<TphDbContext> options)
            : base(options)
        {
        }

        public DbSet<Animal> Animals => Set<Animal>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>();
            modelBuilder.Entity<Dog>();
            modelBuilder.Entity<Cat>();
        }
    }

    private sealed class SqliteHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteHarness(SqliteConnection connection, HarnessDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public HarnessDbContext Db { get; }

        public static async Task<SqliteHarness> CreateAsync(CacheInvalidationSaveChangesInterceptor interceptor)
        {
            SqliteConnection connection = new("DataSource=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            HarnessDbContext db = new(
                new DbContextOptionsBuilder<HarnessDbContext>()
                    .UseSqlite(connection)
                    .AddInterceptors(interceptor)
                    .Options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new SqliteHarness(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    public sealed class SecondDbContext : DbContext
    {
        public SecondDbContext(DbContextOptions<SecondDbContext> options)
            : base(options)
        {
        }

        public DbSet<Extra> Extras => Set<Extra>();
    }
}
