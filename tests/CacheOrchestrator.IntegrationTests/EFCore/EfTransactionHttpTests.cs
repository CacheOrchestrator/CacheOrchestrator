using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.EFCore;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CacheOrchestrator.IntegrationTests.EFCore;

public sealed class EfTransactionHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readers_KeepCommittedOutputAndDataUntilTransactionOutcome(bool commit)
    {
        string database = Path.Combine(Path.GetTempPath(), "cache-orchestrator-tx-" + Guid.NewGuid().ToString("N") + ".db");
        WebApplication? app = null;
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:Domains:tx:Version"] = "1",
                ["Cache:Domains:tx:FusionCache:EagerRefreshRatio"] = "0",
                ["Cache:Domains:tx:FusionCache:JitterSeconds"] = "0"
            });
            builder.Services.AddCacheOrchestrator(builder.Configuration);
            builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration);
            builder.Services.AddDbContextPool<TransactionDb>((services, options) =>
            {
                options.UseSqlite("Data Source=" + database + ";Pooling=False");
                options.AddCacheOrchestratorInvalidation(services);
            });
            app = builder.Build();
            app.UseCacheOrchestrator();
            int factories = 0;
            async Task<string?> Read(HttpContext http, int id, IDomainDataCache cache, TransactionDb db)
            {
                return await cache.GetOrSetEntityAsync(http, async cancellationToken =>
                {
                    Interlocked.Increment(ref factories);
                    return await db.Rows.AsNoTracking().Where(row => row.Id == id)
                        .Select(row => row.Name).SingleOrDefaultAsync(cancellationToken);
                }, http.RequestAborted);
            }
            app.MapGet("/items/{id:int}", Read)
                .CacheOutputWithDomain("tx", resourceRouteKey: "id", entityKind: "items");
            app.MapGet("/items-data/{id:int}", (
                HttpContext http, int id, IDomainDataCache cache, IRequestDomainCacheOptions domains, TransactionDb db) =>
            {
                domains.EnsureDomainOptions(http, "tx");
                cache.SetEntityIdentity(http, "items", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Read(http, id, cache, db);
            });
            await using (AsyncServiceScope seed = app.Services.CreateAsyncScope())
            {
                TransactionDb db = seed.ServiceProvider.GetRequiredService<TransactionDb>();
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", TestContext.Current.CancellationToken);
                db.Rows.Add(new() { Id = 1, Name = "committed-old" });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            await app.StartAsync(TestContext.Current.CancellationToken);
            using HttpClient client = app.GetTestClient();
            (await Get(client, "/items/1")).Should().Be("committed-old");
            (await Get(client, "/items/1")).Should().Be("committed-old");
            (await Get(client, "/items-data/1")).Should().Be("committed-old");
            factories.Should().Be(1);

            await using (AsyncServiceScope writer = app.Services.CreateAsyncScope())
            {
                TransactionDb db = writer.ServiceProvider.GetRequiredService<TransactionDb>();
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
                Row row = await db.Rows.SingleAsync(TestContext.Current.CancellationToken);
                row.Name = "intermediate";
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                row.Name = "committed-new";
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                // Independent pooled contexts use separate WAL connections. Premature invalidation
                // would refill with the old committed database value while the writer is still open.
                (await Get(client, "/items/1")).Should().Be("committed-old");
                (await Get(client, "/items-data/1")).Should().Be("committed-old");
                factories.Should().Be(1, "no invalidation is eligible before transaction completion");
                if (commit)
                    await transaction.CommitAsync(TestContext.Current.CancellationToken);
                else
                    await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }

            string expected = commit ? "committed-new" : "committed-old";
            (await Get(client, "/items-data/1")).Should().Be(expected);
            (await Get(client, "/items/1")).Should().Be(expected);
            (await Get(client, "/items/1")).Should().Be(expected);
            factories.Should().Be(commit ? 2 : 1);
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                File.Delete(database + suffix);
        }
    }

    private static async Task<string> Get(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    public sealed class TransactionDb(DbContextOptions<TransactionDb> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();
    }

    [CacheEntity("tx", "items")]
    public sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
