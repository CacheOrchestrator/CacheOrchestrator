# Invalidation

> **Reference** — Version cutovers, tags, observers, and multi-instance purge.

When data changes, retire it in **every** layer that still holds it — Data Cache tags, Output Cache tags, and (via Version or Client Cache Schedule) the client generation story.

Prefer **`ICacheOrchestratorInvalidator`** over talking to Data Cache or Output Cache stores directly. Multi-instance behaviour depends on topology ([deployment](deployment.md), [cluster bus](cluster-bus.md)).

Server invalidation cannot recall responses already cached by a browser. For application-owned versioned URLs and client refresh after writes, see [Client cache busting and invalidation](../guide/client-invalidation.md).

## Table of Contents

- [Version (preferred for bulk cutovers)](#version-preferred-for-bulk-cutovers)
- [Programmatic API (`ICacheOrchestratorInvalidator`)](#programmatic-api-icacheorchestratorinvalidator)
- [When to use which](#when-to-use-which)
- [EF Core SaveChanges (`CacheOrchestrator.EFCore.Invalidation`)](#ef-core-savechanges-cacheorchestratorefcoreinvalidation)
- [Multi-instance invalidation](#multi-instance-invalidation)

## Version (preferred for bulk cutovers)

In config:

```json
{
  "Cache": {
    "Domains": {
      "products": {
        "Version": "v1"
      }
    }
  }
}
```

When you deploy a content update, bump `Version` (and reload configuration).  

- Output Cache vary value `data-version` changes  
- Data Cache keys include the version hex
- Old entries expire by TTL (no mass delete)

For an Edge-enabled domain, a Version change through configuration reload or the Management/Admin API automatically enqueues a domain-tag purge. HttpBus peers apply a distributed runtime Version without duplicating the origin's Edge purge; shared configuration reloads are observed independently by every process and may enqueue idempotent duplicates. Browser copies retain the cache policy they received.

If `Version` is omitted, the library uses `"1"` and logs a warning (keys stable across restarts).

## Programmatic API (`ICacheOrchestratorInvalidator`)

All methods return **`CacheInvalidationResult`**. Store invalidation is best-effort: a Data Cache or Output Cache failure is reported in the result instead of being thrown.

```csharp
using CacheOrchestrator.Invalidation;

// Entire domain (Output Cache + Data Cache on the owning instance)
CacheInvalidationResult r1 = await invalidator.InvalidateDomainAsync("products", cancellationToken);
if (!r1.Succeeded)
{
    // r1.DataCacheSucceeded / r1.OutputSucceeded / r1.Errors
}

// Several domains
CacheInvalidationResult r2 = await invalidator.InvalidateDomainsAsync(
    ["products", "catalog"], cancellationToken);

// Single entity — requires entries stored with entityKind + resource id / resourceRouteKey
CacheInvalidationResult r3 = await invalidator.InvalidateEntityAsync(
    "store", "products", 42, cancellationToken);

// Several ids of one kind (one local apply + one Bus publish)
CacheInvalidationResult r4 = await invalidator.InvalidateEntitiesAsync(
    "store", "products", [42, 43], cancellationToken);

// Every entry tagged entitykind:store:products
CacheInvalidationResult r5 = await invalidator.InvalidateEntityKindAsync(
    "store", "products", cancellationToken);

// Custom or multiple tags (all Data Cache instances + Output Cache)
CacheInvalidationResult r6 = await invalidator.InvalidateTagsAsync(
    ["domain:store", "entity:store:products:42", "custom:batch-7"], cancellationToken);
```

Use the generic entity overloads with the ID's natural type, as above. `int`, `long`, `Guid`, and other `IFormattable` values are converted with invariant culture, so callers do not need manual `.ToString()` calls. The string overload remains appropriate for identifiers that are genuinely strings, such as `"ABC-42"`.

### `CacheInvalidationResult`

| Property | Meaning |
|----------|---------|
| `Scope` | Label (domain, `domain/id`, joined tags, or `(skipped)`) |
| `Tags` | Tags targeted for eviction |
| `DataCacheSucceeded` | All Data Cache removals for this call succeeded |
| `OutputSucceeded` | All Output Cache evictions succeeded |
| `IsSkipped` | No-op (empty domain/tags); nothing was evicted |
| `Succeeded` | Both layers succeeded **and** the call was not skipped. Cluster publish failures do not flip this. |
| `ClusterPublish` | Per-peer bus outcomes for one invalidation operation; `null` when publish was skipped. For a batch, inspect each entry in `Parts`. |
| `Errors` | Non-fatal messages (partial failure or skip reason) |
| `Parts` | Ordered per-domain results represented by `InvalidateDomainsAsync`; empty for a single operation |

Empty input (null domain, no tags) → `CacheInvalidationResult.Skipped(...)` with `IsSkipped == true`, `Succeeded == false`, and no store calls.

`ICacheInvalidationObserver` receives exactly one before/after pair per public invalidation call. `InvalidateDomainsAsync` reports `CacheInvalidationKind.Domains`; inspect `result.Parts` in the after callback for individual domain, store, and cluster outcomes. Call `InvalidateDomainAsync` separately only when separate observer operations are intentional.

Invalidation and a concurrent cache fill can race: a fill that completes after eviction may publish a new entry under the same Version. When a release needs a strong generation boundary, change the domain Version so old and new fills cannot share physical keys.

### Tag formats (`CacheTags`)

| Tag | When applied |
|-----|----------------|
| `domain:{name}` | Every Output Cache policy entry and every Data Cache get-or-set entry |
| `entity:{domain}:{entityKind}:{resourceId}` | Data Cache when using `GetOrSetEntityAsync`; Output Cache when `resourceRouteKey` **and** `entityKind` are set on the policy |
| `entitykind:{domain}:{entityKind}` | Same writes; purge all entries of that kind with `InvalidateEntityKindAsync` |
| Custom | Your tags — purge with `InvalidateTagsAsync` |

### Wiring entity tags

Identity is declared once on the endpoint. Data Cache and Output Cache share it; factories may add members, dependencies, and aliases through `EntityCache` or `EntitySet`.

```csharp
// Minimal API — detail
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache, CancellationToken cancellationToken) =>
{
    var product = await cache.GetOrSetEntityAsync(http, factory, cancellationToken);
    return product is null ? Results.NotFound() : Results.Ok(product);
})
.CacheOutputWithDomain("store", resourceRouteKey: "id", entityKind: "products");

// MVC
[CacheDomain("store", resourceRouteKey: "id", entityKind: "products")]
public class ProductsController : ControllerBase { }

// Collection (kind-scoped)
[CacheDomain("store", entityKind: "products")]
public async Task<ActionResult<IReadOnlyList<Product>>> List(...) { /* GetOrSetEntitySetAsync */ }
```

### Observers (audit / webhooks)

Register one or more `ICacheInvalidationObserver` implementations. They run in DI registration order on the **same process** that called the invalidator. Exceptions from observers are logged and **do not** fail invalidation.

Simple audit hook:

```csharp
public sealed class AuditInvalidationObserver : ICacheInvalidationObserver
{
    public ValueTask OnBeforeInvalidateAsync(CacheInvalidationContext context, CancellationToken cancellationToken)
    {
        // context.Kind, context.Scope, context.Tags
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterInvalidateAsync(CacheInvalidationContext context, CacheInvalidationResult result, CancellationToken cancellationToken)
    {
        // result.Succeeded, result.Errors
        return ValueTask.CompletedTask;
    }
}

// Program.cs
builder.Services.AddSingleton<ICacheInvalidationObserver, AuditInvalidationObserver>();
```

Observers are **audit/webhooks on this process only**. Cross-instance purge is the [cluster bus](cluster-bus.md) or Redis Fusion backplane — see [Multi-instance invalidation](#multi-instance-invalidation).

### Implementation notes

1. Normalize domain / resource id  
2. Resolve the Data Cache instance from domain options for domain and entity APIs
3. `IDataCacheProvider.InvalidateAsync` with the complete tag set and target instance
4. `IOutputCacheStore.EvictByTagAsync`  
5. Best-effort failures → warnings + `CacheInvalidationResult.Errors`  
6. Metrics `cache_orchestrator.invalidate` only when **both** layers succeed for that scope  
7. `InvalidateTagsAsync` fans out to **all** configured Data Cache instances

## When to use which

| Scenario | Approach |
|----------|----------|
| Scheduled content release (tiles, catalog cutover) | Bump `Version` |
| Admin updated one product | `InvalidateEntityAsync` |
| Purge whole domain | `InvalidateDomainAsync` |
| Purge several domains after deploy | `InvalidateDomainsAsync` |
| Emergency “everything for products is wrong” | Domain invalidate and/or Version bump |
| Custom multi-tag purge | `InvalidateTagsAsync` |
| Audit / Slack / webhook | `ICacheInvalidationObserver` |
| Multi-instance InMemory, need immediate purge everywhere | **CacheOrchestrator.HttpBus** or Redis backplane |
| Admin Version/TTL on all InMemory nodes | Bus + Admin `distribute: true` (or Admin Console App fan-out) |

---

## EF Core SaveChanges (`CacheOrchestrator.EFCore.Invalidation`)

After a successful `SaveChanges` the package calls the public invalidator (and the Bus, if configured). Mapping lives in code. Full guide: [ef-core-invalidation.md](ef-core-invalidation.md).

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration);
builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
{
    opt.UseSqlServer(cs);
    opt.AddCacheOrchestratorInvalidation(sp);
});
```

Map types in **code** (not appsettings): `[CacheEntity]`, Fluent `CacheInvalidate` on the EF model, or `Map<T>` at registration. Full guide: [ef-core-invalidation.md](ef-core-invalidation.md).

`ExecuteUpdate` / `ExecuteDelete` / `ExecuteInsert` do **not** go through `ChangeTracker`:

```csharp
await invalidator.InvalidateEntitiesAsync("store", "products", productIds);
// or, unknown / too many ids:
await invalidator.InvalidateEntityKindAsync("store", "products");
```

List/index endpoints tagged only `domain:{name}` are not refreshed by row invalidation.

---

## Multi-instance invalidation

### What the library does on one call

`ICacheOrchestratorInvalidator` always applies **locally** on the calling process. When **`CacheOrchestrator.HttpBus`** is registered and enabled, it then **publishes** an `InvalidateCommand` to peers (peers ApplyLocal only — no echo).

| Layer | Without Redis | With Redis (Output Cache store and/or Fusion L2 + backplane) |
|-------|---------------|-----------------------------------------------------|
| Data Cache L1 (Fusion memory) | Cleared only here (unless HttpBus peers apply too) | Cleared here; **other nodes** clear L1 via **backplane** |
| Fusion L2 | N/A or local only | Shared store purged |
| Output Cache | In-process only (unless HttpBus peers also apply the command) | Shared when the Output Cache provider is Redis |

For origin caches, without a distributed store, a backplane, or HttpBus, invalidation applies on the calling process only. The optional Edge integration below independently queues external invalidation.

When `CacheOrchestrator.Edge` is enabled for a domain, the initiating process also queues an external tag purge after local invalidation. HttpBus peers apply their local invalidation but do not enqueue duplicate edge requests. The built-in bounded queue coalesces and retries in memory; it is best-effort and can lose pending work on a crash. It drains during graceful host shutdown. See [Edge cache integration](../guide/edge.md).

An Edge-enabled domain is also purged when its `Version` changes. Changing `ClientCache:ScheduledUpdateUtc` does not purge Edge; subsequent origin responses calculate fresh TTL from the new schedule, while existing Edge objects retain their stored TTL until they expire or are invalidated independently. Other Edge and Client Cache setting changes are not policy-change purge triggers.

`CacheInvalidationResult.Succeeded` describes local Data Cache/Output Cache eviction, not successful Edge queueing or completed provider purge. Observe Edge metrics and logs separately.

Cluster **configuration** management (shared `appsettings.cache.json`, ConfigMap, env) does **not** by itself purge L1/L2 on other nodes. It only keeps **policy** in sync (Version, TTLs). See [deployment.md — Shared configuration](deployment.md#shared-configuration-across-instances).

### Approaches

| Approach | Immediate purge on all nodes? | When to use |
|----------|-------------------------------|-------------|
| **1. Bump `Version` (shared config)** | No — new key space; old entries expire by TTL | Snapshot / catalog cutover; simplest multi-node story |
| **2. Redis Fusion L2 + backplane** (+ optional Redis Output Cache) | Yes for Fusion L1 through the backplane, plus shared L2 | **Recommended production multi-instance** |
| **3. CacheOrchestrator.HttpBus** (HTTP + Static / ServiceDiscovery) | Yes if every peer has receive endpoints | Multi-instance **InMemory**; also Version/TTL overlays via Admin `distribute`. Full guide: [cluster-bus.md](cluster-bus.md) |
| **4. Rolling restart of all instances** | Yes (cold process) | Emergency only |
| **5. Custom observer + external bus** | Yes if you implement it | Rare; prefer HttpBus package |

```text
Recommended multi-instance (immediate invalidation):

  Invalidate* on any node
       → local Output Cache / Data Cache
       → Redis L2 + pub/sub backplane
       → other nodes drop L1

Without Redis (optional Bus package):

  Invalidate* on node A
       → local purge on A
       → HttpClusterCommandBus → peers ApplyLocal
  Version/settings Admin with distribute:true → VersionBumpCommand / SettingsPatchCommand
```

### Approach 1 — Version + shared config (no bus)

1. Put domain `Version` in shared config ([deployment.md](deployment.md)).  
2. On cutover, set e.g. `"2026-08"` → `"2026-09"` once and deliver to all instances.  
3. New requests use new keys; old entries age out.

No need to call `InvalidateDomainAsync` on every machine for bulk content releases.

### Approach 2 — Redis backplane (library-supported)

Use `CacheOrchestrator.Redis`, `"Provider": "Redis"` for Fusion (and optionally Output Cache).  
`InvalidateDomainAsync` / entity / tags on **any** instance:

- removes tags from shared L2  
- publishes backplane messages so **other instances clear L1**

Details: [deployment.md](deployment.md), [backends.md](backends.md).

### Approach 3 — CacheOrchestrator.HttpBus (optional package)

Full reference: **[cluster-bus.md](cluster-bus.md)** (install, membership Static/ServiceDiscovery, commands, Admin Console App, metrics, security).

```bash
dotnet add package CacheOrchestrator.HttpBus --prerelease
```

```csharp
using CacheOrchestrator.HttpBus;

builder.Services.AddCacheOrchestrator(builder.Configuration, o => o.AddHttpClusterBus());
app.UseCacheOrchestrator();
app.MapCacheOrchestratorHttpBus(); // independent of Admin
```

| Behaviour | Detail |
|-----------|--------|
| `Invalidate*` (app code) | Local apply + publish when bus enabled |
| Admin API mutations | `distribute: true` → peers; default is this process only |
| Peers | `POST …/cluster/apply` ApplyLocal only (anti-echo) |

Prefer Redis L2 and the backplane when instances share Fusion data. Use HttpBus when stores are in-memory and you still need commands on every node, including runtime Version and TTL overlays.

Application `ICacheInvalidationObserver` implementations are for **audit/webhooks only** — not a second cluster bus when HttpBus is registered. The Edge package's built-in observer is origin-aware and intentionally skips remote HttpBus application.

### Choosing an approach (multi-instance)

| Goal | Prefer |
|------|--------|
| Monthly data cutover, long TTL | Shared config **Version** bump ([deployment.md](deployment.md)) |
| Many nodes, shared cache, immediate purge | **Redis** L2 + backplane |
| InMemory only, invalidate everywhere | **CacheOrchestrator.HttpBus** |
| Runtime Version/TTL on all InMemory nodes | HttpBus + Admin `distribute: true` (or Admin Console App fan-out to each node) |
| Sticky sessions + TTL-only | Local invalidation may be enough |

## Related

- [Guide — topologies](../guide/topologies.md) — which approach across instances  
- [cache-keys.md](cache-keys.md) — keys vs tags, Version in key material  
- [domain-profiles.md](../guide/domain-profiles.md) — Snapshot vs Dynamic + config recipes  
- [deployment.md](deployment.md) — multi-instance topologies + shared configuration  
- [configuration.md](configuration.md) — Version, TTLs, and domain options  
- [Output Cache](output-cache.md) — tag stamping and `resourceRouteKey`  
- [Data Cache](data-cache.md) — entity keys and footprint tags  
- [backends.md](backends.md) — Redis L2 / backplane packages  
- [cluster-bus.md](cluster-bus.md) — optional multi-instance command bus  

