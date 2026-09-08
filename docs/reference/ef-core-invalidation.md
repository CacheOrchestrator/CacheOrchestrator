# EF Core SaveChanges invalidation

> **Reference** — purge mapped entities after the owning transaction commits.

Package **`CacheOrchestrator.EFCore.Invalidation`**. After `SaveChanges` / `SaveChangesAsync` succeeds and its owning transaction commits, the cache for the rows that changed is purged through `ICacheOrchestratorInvalidator`.

Package README: [src/CacheOrchestrator.EFCore.Invalidation/README.md](../../src/CacheOrchestrator.EFCore.Invalidation/README.md). See also [invalidation.md](invalidation.md), [domain-profiles.md](../guide/domain-profiles.md), [Data Cache](data-cache.md), [configuration.md](configuration.md).

## Table of Contents

- [How it works](#how-it-works)
- [Related cache entries (footprint tags)](#related-cache-entries-footprint-tags)
- [When to use it](#when-to-use-it)
- [Install and composition](#install-and-composition)
- [Mapping (code only)](#mapping-code-only)
- [What runs on SaveChanges](#what-runs-on-savechanges)
- [Transaction boundaries](#transaction-boundaries)
- [SaveChanges vs `Execute*`](#savechanges-vs-execute)
- [Multi-instance](#multi-instance)
- [Configuration (`Cache:EFCore:Invalidation`)](#configuration-cacheefcoreinvalidation)
- [Limits](#limits)

## How it works

A **domain** is a cache policy group (TTL, Version, Data Cache instance). Ids are unique together with `entityKind`, not inside the domain alone.

Row identity is always `(domain, entityKind, resourceId)`:

```text
entity:store:products:42
entitykind:store:products
```

The EF package only maps a CLR type → `(domain, entityKind)` and the primary key → `resourceId`. It then calls `ICacheOrchestratorInvalidator`. Multi-instance behaviour is whatever that invalidator already does (local only, Redis Fusion backplane, and/or `CacheOrchestrator.HttpBus`).

```text
SavingChanges          snapshot mapped Added | Modified | Deleted
        │
SaveChanges (DB)
        │
   success                          failure
        │                              │
SavedChanges                    SaveChangesFailed
        │                         discard snapshot
        ▼
Owning transaction commits (or save already committed)
        │
        ▼
InvalidateEntitiesAsync  or  InvalidateEntityKindAsync (OnBulk)
        │
        ├─ local Output Cache + Data Cache tag purge
        ├─ Fusion Redis backplane  (if Redis Fusion L2 is configured — drops peer L1)
        └─ HttpBus                 (if CacheOrchestrator.HttpBus is configured — peer ApplyLocal)
```

### Related cache entries (footprint tags)

EF does not walk FK graphs or `DependsOn` / `Members`. It only purges tags for the mapped `(domain, entityKind, id)`. Related entries disappear when they were **tagged at cache-write time** — for example a product detail that depended on a brand:

```csharp
return EntityCache.Create(productDetailDto)
    .DependsOn("brands", dto.BrandId);
```

Saving `Brand` `7` then invalidates `entity:…:brands:7` and drops that product detail entry too. Wire those links with [Entity footprint](entity-footprint.md); the EF package only supplies the purge call.

---

## When to use it

| Situation | Approach |
|-----------|----------|
| CRUD through tracked entities + `SaveChanges` | This package (automatic) |
| `ExecuteUpdate` / `ExecuteDelete` / `ExecuteInsert` | Manual `InvalidateEntitiesAsync` / `InvalidateEntityKindAsync` |
| List/index pages tagged only `domain:{name}` | TTL, Version bump, or `InvalidateDomainAsync` — **not** this interceptor |
| Snapshot / catalog cutover | Domain `Version`, not per-row EF hooks |

---

## Install and composition

Full **packages + registration + config + endpoint** samples (in-app EF, and EF inside a class library): [composition.md — 7](../how-to/composition.md#scenario-7) and [8](../how-to/composition.md#scenario-8).

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.EFCore.Invalidation --prerelease
```

| API | Notes |
|-----|--------|
| `AddCacheOrchestratorEfCoreInvalidation` | Binds `Cache:EFCore:Invalidation`, registers SaveChanges and transaction interceptors as **singletons** |
| `AddEfCoreInvalidation` | Same registration on `ICacheOrchestratorBuilder` |
| `AddCacheOrchestratorInvalidation` | Attaches both interceptors to **this** `DbContext` only |

`AddCacheOrchestratorInvalidation` attaches both interceptors to **this** `DbContext` only. Call it on each options builder that should invalidate.

---

## Mapping (code only)

No type list in appsettings. First match wins:

1. Fluent `CacheInvalidate` on the EF model  
2. `[CacheEntity]` on the CLR type (`entry.Metadata.ClrType`)  
3. `Map<T>` at DI registration  

Unmapped types, owned types, and keyless types are skipped (no throw).

### 1. Fluent (`OnModelCreating` / `IEntityTypeConfiguration<T>`)

Preferred when the domain model must stay persistence-ignorant.

```csharp
modelBuilder.Entity<Product>().CacheInvalidate("store", "products");
```

### 2. Attribute

```csharp
[CacheEntity("store", "products")]
public class Product { public int Id { get; set; } }
```

### 3. `Map<T>` at composition root

```csharp
builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration, o =>
{
    o.Map<Product>("store", "products");
    o.Map<Asset>("store", "assets");
});
```

The HTTP / library cache path must use the **same** domain and `entityKind` as the mapping — see [composition.md — 7–8](../how-to/composition.md#scenario-7).

Primary keys: stringify each PK part with invariant culture (`byte[]` uses lowercase hexadecimal), percent-encode each composite part independently, then join parts with `:`. The resulting resource id stays opaque; GUIDs use canonical lowercase `D` format. A route `resourceRouteKey` must produce the same string. Entity kinds use `DomainName.NormalizeEntityKind` and remain restricted normalized schema names.

TPH: Fluent `CacheInvalidate` and `Map<T>` match the **exact** `ClrType` — map each concrete type (a base-type Fluent/`Map` entry does not cover derived types). **`[CacheEntity]` is inherited** (`Inherited = true`); an attribute on the base type **does** apply to derived CLR types.

---

## What runs on SaveChanges

| Event | Behaviour |
|-------|-----------|
| `SavingChanges` | Snapshot mapped `Added` / `Modified` / `Deleted`. Per-`DbContext` bag (interceptor is a singleton — no instance fields). Deleted PKs are captured here. |
| Successful `SavedChanges` | Re-read PK (identity is assigned). Capture ids and policy without retaining tracked entries. Invalidate if already committed, otherwise queue for transaction completion. |
| `SaveChangesFailed` / canceled save | Discard this save's snapshot. Earlier successful saves in the same transaction remain pending. |
| Invalidator throws or returns partial failure | Logged. A committed database write is not reported as failed because its cache purge failed. |
| Explicit or ambient rollback | Discard the pending transaction batch. No purge. |

`OnBulk` applies per `(domain, entityKind, bulk policy)` group when the distinct id count reaches `BulkThreshold`. Multiple successful saves in one transaction coalesce before the purge:

| Value | Effect |
|-------|--------|
| `Entities` | Always `InvalidateEntitiesAsync` |
| `Kind` (default) | `InvalidateEntityKindAsync` — all rows of that kind, not sibling kinds |
| `Domain` | `InvalidateDomainAsync` — **entire policy group** (products **and** assets in `store`) |

---

## Transaction boundaries

- An ordinary successful save with no enclosing transaction invalidates before returning.
- `Database.BeginTransaction` / `BeginTransactionAsync` defers work until the EF `IDbContextTransaction.Commit` / `CommitAsync` callback. Commit awaits invalidation; request cancellation after the database commit does not skip it.
- Contexts sharing a native transaction through `UseTransaction` and the same application service provider share one pending batch. Commit through the EF wrapper. A raw ADO.NET commit outside EF cannot be observed: its owner must perform explicit invalidation after confirming the outcome.
- Ambient `TransactionScope` and provider-supported explicit `EnlistTransaction` defer to `TransactionCompleted`. The callback runs after participant outcome notifications. Use `TransactionScopeAsyncFlowOption.Enabled` with async work. `RequiresNew` completes independently of an outer transaction.
- A savepoint rollback keeps a conservative superset of ids until final commit. It may cause extra misses for rolled-back changes; it never publishes invalidation while the enclosing transaction remains open.
- Rolled-back/disposed transactions do not leak changes into a reused or pooled context. Only ids and captured bulk policy are retained while the transaction is pending.
- Delivery remains process-local and best effort. A crash after database commit but before purge, an unknown commit outcome, or a provider failure requires reconciliation/retry by the application. Use an application outbox for durable delivery. Ambient database transaction support still depends on the EF provider; the package does not add it to providers such as InMemory or SQLite.

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
await db.SaveChangesAsync(cancellationToken); // ids captured; no purge yet
await transaction.CommitAsync(cancellationToken); // commit, then invalidate
```

## SaveChanges vs `Execute*`

Composition samples (GET + PUT): [composition.md — 7](../how-to/composition.md#scenario-7) and [8](../how-to/composition.md#scenario-8).

| Path | Invalidation |
|------|----------------|
| Tracked entity + `SaveChangesAsync` | Interceptor only — do **not** call `Invalidate*` |
| `ExecuteUpdate` / `ExecuteDelete` / `ExecuteInsert` | Manual `InvalidateEntitiesAsync` / `InvalidateEntityKindAsync` (no ChangeTracker) |

```csharp
await db.Products
    .Where(p => ids.Contains(p.Id))
    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, p => p.Price * 0.8m), cancellationToken);

await invalidator.InvalidateEntitiesAsync("catalog", "products", ids, cancellationToken);
```

Unknown / too many ids: `InvalidateEntityKindAsync("catalog", "products")`.

---

## Multi-instance

The EF package does not talk to Redis or the Bus. It only calls `ICacheOrchestratorInvalidator`.

| Topology | After the owning transaction commits on node A |
|----------|-------------------------------|
| Single process | Local Output Cache + Data Cache only |
| Redis Fusion L2 + backplane | Shared L2 purged; other nodes drop L1 via Fusion backplane |
| `CacheOrchestrator.HttpBus` (InMemory multi-node) | One `InvalidateCommand` per `(domain, entityKind)` group; peers ApplyLocal |
| Neither | Other nodes keep stale L1 until TTL / Version |

Use the same `entityKind` and tag shape on every node. Mismatched tag formats do not match across the cluster.

Roll the same package build to every node before relying on entity Bus commands.

---

## Configuration (`Cache:EFCore:Invalidation`)

Operational flags only. Bound from the same root section as `AddCacheOrchestrator` (default `Cache`).

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `true` | Master switch |
| `BulkThreshold` | `20` | Id count that triggers `OnBulk` |
| `OnBulk` | `Kind` | `Entities` · `Kind` · `Domain` |

---

## Limits

- Reads still go through `GetOrSetEntityAsync` and Output Cache as usual.
- List and index entries tagged only `domain:{name}` stay until TTL, Version, or `InvalidateDomainAsync`.
- `Execute*` and raw SQL need a manual `Invalidate*` call after the owning transaction commits.
- Each composite primary-key part is percent-encoded before joining with `:`. Binary keys use lowercase hexadecimal; the HTTP `resourceId` must use the same convention.
- `BulkThreshold <= 0` disables the bulk path (always `InvalidateEntitiesAsync`).
- There is no ambient `Suppress()` API; turn the feature off with `Enabled: false` or omit the interceptor on that context.

## Related

- [Guide — topologies](../guide/topologies.md) — when Redis / bus matter for purge reach  
- [invalidation.md](invalidation.md) — tags, invalidator, multi-instance strategies  
- [Data Cache](data-cache.md) — `GetOrSetEntityAsync`  
- [Output Cache](output-cache.md) — `resourceRouteKey` + `entityKind`  
- [entity-footprint.md](entity-footprint.md) — members / depends-on / aliases  
- [faq.md](../guide/faq.md) — `ExecuteUpdate` and bulk-update caveats  
- [Package composition](../how-to/composition.md) — copy-paste package wiring  
