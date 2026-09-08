# CacheOrchestrator.EFCore.Invalidation

[**CacheOrchestrator**](https://github.com/CacheOrchestrator/CacheOrchestrator) is a multi-tier cache coordination and synchronized invalidation library for .NET.

This package hooks EF Core **`SaveChanges`**: map a CLR type to `(domain, entityKind)`; after the owning transaction commits, matching entity tags are purged through `ICacheOrchestratorInvalidator`.

## Install

```bash
dotnet add package CacheOrchestrator.EFCore.Invalidation --prerelease
```

## Configuration

Domain policy (same as the rest of CacheOrchestrator). Optional interceptor options:

```json
{
  "Cache": {
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "TtlSeconds": 300 },
        "OutputCache": { "TtlSeconds": 60 }
      }
    },
    "EFCore": {
      "Invalidation": {
        "Enabled": true
      }
    }
  }
}
```

## Usage

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.EFCore.Invalidation --prerelease
```

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
{
    opt.UseSqlServer(connectionString);
    opt.AddCacheOrchestratorInvalidation(sp);
});

// In OnModelCreating (or Map<T> / [CacheEntity]):
modelBuilder.Entity<Product>().CacheInvalidate("catalog", "products");
```

Tracked `SaveChanges` captures generated ids after a successful save. A save without an enclosing transaction invalidates immediately; explicit EF transactions defer and coalesce invalidation until `Commit` / `CommitAsync`, and ambient transactions defer until their completion notification. Rollbacks discard pending invalidation. `ExecuteUpdate` and `ExecuteDelete` bypass the change tracker and require explicit invalidation.

## Documentation

- [README](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/README.md)
- [Documentation index](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/docs/README.md)
- [Repository](https://github.com/CacheOrchestrator/CacheOrchestrator)

## License

MIT — [LICENSE.md](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/LICENSE.md)

Commit externally supplied relational transactions through EF's `IDbContextTransaction` wrapper so the transaction interceptor observes their outcome. A raw ADO.NET commit outside EF requires explicit invalidation after the owner confirms the commit. Delivery is best effort and process local; use an application outbox when invalidation must survive a process failure. See the [transaction contract](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/docs/reference/ef-core-invalidation.md#transaction-boundaries).
