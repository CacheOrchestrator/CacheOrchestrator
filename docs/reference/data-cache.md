# Data Cache

> **Reference** — Data Cache providers, domain resolution, keys, and entity reads.

The **Data Cache** stores **application objects** from your factory (DTOs, tiles, aggregates) — not full HTTP responses. CacheOrchestrator scopes it to the same **domain** as Output Cache and Client Cache. A registered **`IDataCacheProvider`** owns the store (Fusion or Hybrid).

- Portable policy: nested **`DataCache`** (`TtlSeconds`, `Enabled`, `Instance`, …).
- Web: **`IDomainDataCache`**. Libraries / workers: Core **`ICacheOrchestrator`** + `CacheDomainContext`.
- Which NuGet: [packages](../guide/packages.md). Copy-paste stacks: [composition](../how-to/composition.md).

## Table of Contents

- [Providers](#providers)
- [How the Data Cache finds the domain](#how-the-data-cache-finds-the-domain)
- [When the factory runs uncached](#when-the-factory-runs-uncached)
- [Cache key](#cache-key)
- [Results (`X-CacheOrchestrator` `dc=` and `DataCacheResult`)](#results-x-cacheorchestrator-dc-and-datacacheresult)
- [FusionCache provider](#fusioncache-provider)
- [HybridCache provider](#hybridcache-provider)

## Providers

| Package | Engine | Config |
|---------|--------|--------|
| **CacheOrchestrator.FusionCache** | ZiggyCreatures FusionCache (default in the meta package) | `DataCache.*` + nested **`FusionCache.*`** (hard TTL, fail-safe, jitter, factory timeouts, …) |
| **CacheOrchestrator.HybridCache** | Microsoft HybridCache | `DataCache.TtlSeconds` only — ignores `FusionCache` |

Register exactly one provider. Meta `AddCacheOrchestrator` = `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache`.

An Output-Cache-only host does not need to configure `DataCache.Enabled`. Without a Data Cache provider, startup and health remain healthy; only an actual `IDomainDataCache` / `ICacheOrchestrator` operation logs a one-time warning and runs its factory uncached.

**Hybrid instead of Fusion:** call `AddHybridCache()`, then `AddCacheOrchestratorAspNetCore`, then `AddCacheOrchestratorHybridCache` (replaces any prior `IDataCacheProvider`). Nested `FusionCache.*` domain knobs are ignored. Full sample: [composition.md — 5](../how-to/composition.md#scenario-5).

Package READMEs: [FusionCache](../../src/CacheOrchestrator.FusionCache/README.md), [HybridCache](../../src/CacheOrchestrator.HybridCache/README.md). Domain resolution, keys, entity identity, and `dc=` results below are **shared** across providers.

---

## How the Data Cache finds the domain

`IDomainDataCache.GetOrSetAsync` looks for domain options in this order:

1. The overload `GetOrSetAsync(http, domain, factory)` — same domain reuses the request snapshot; a **different** name replaces it (so `products` and `catalog` never share an entry).
2. Already on the request — usually set by Output Cache when you use `.CacheOutputWithDomain` or `[CacheDomain]`.
3. Endpoint metadata (the same attribute or extension), then the options are loaded.
4. If none of those apply, the factory runs uncached.

### With Output Cache

```csharp
app.MapGet("/api/products", async (HttpContext http, IDomainDataCache cache) =>
{
    var data = await cache.GetOrSetAsync(http, LoadAsync);
    return Results.Json(data);
})
.CacheOutputWithDomain("products");
```

```csharp
[CacheDomain("products")]
public class ProductsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromServices] IDomainDataCache cache, CancellationToken cancellationToken)
    {
        var data = await cache.GetOrSetAsync(HttpContext, LoadAsync, cancellationToken);
        return Ok(data);
    }
}
```

### Data Cache only (no Output Cache domain)

When the endpoint has no Output Cache domain, pass the name:

```csharp
await cache.GetOrSetAsync(http, "products", factory, cancellationToken);
```

Equivalent:

```csharp
domains.EnsureDomainOptions(http, "products");
await cache.GetOrSetAsync(http, factory, cancellationToken);
```

If you omit the domain:

- a Warning is logged (`Data cache skipped: no domain resolved…`)
- metric `cache_orchestrator.dc.requests` is recorded with `domain=_`, `result=unresolved`
- `X-CacheOrchestrator` may show `dc=unresolved; fa=run` when Output Cache still writes headers

After endpoint policy resolution or `EnsureDomainOptions`, advanced handler code can inspect the immutable request snapshot:

```csharp
DomainHttpCacheOptions? options = http.GetDomainCacheOptions();
```

`null` means no domain has been resolved for the request. Read this snapshot for diagnostics or downstream decisions; do not treat it as mutable configuration.

### Entity identity

Entity identity is optional and lives **inside** a domain (domains stay the configuration unit). Endpoint metadata owns domain + primary kind/id; the Data Cache HTTP helpers consume that identity on the happy path.

```csharp
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache, CancellationToken cancellationToken) =>
{
    var product = await cache.GetOrSetEntityAsync(
        http,
        token => LoadProductAsync(id, token),
        cancellationToken);
    return product is null ? Results.NotFound() : Results.Ok(product);
})
.CacheOutputWithDomain("store", resourceRouteKey: "id", entityKind: "products");

await invalidator.InvalidateEntityAsync("store", "products", 42, cancellationToken);
```

Tags for that detail entry: `domain:store`, `entity:store:products:42`, `entitykind:store:products`.

The same footprint model also covers lists, references, aggregates, nested collections, batch ids, aliases, derived data, and composites — via `EntityCache` / `EntitySet` (`Members`, `DependsOn`, `Alias`, `Miss`) and `GetOrSetEntitySetAsync`. Cookbook with use cases: **[entity-footprint.md](entity-footprint.md)**.

Also: [domain-profiles.md](../guide/domain-profiles.md), [invalidation.md](invalidation.md), [cache-keys.md](cache-keys.md).

For a Data-Cache-only endpoint, bind the domain and a natural typed ID, then call `GetOrSetEntityAsync(http, factory)`:

```csharp
domains.EnsureDomainOptions(http, "store");
cache.SetEntityIdentity(http, "products", 42);
await cache.GetOrSetEntityAsync(http, factory, cancellationToken);
```

`SetEntityIdentity` formats `IFormattable` values with invariant culture. Use a string only when the identifier itself is a string, for example `"ABC-42"`.

## When the factory runs uncached

- No domain on the request or metadata — disposition `Unresolved` (Warning + metric `result=unresolved`).
- `DataCache.Enabled: false` — `Off`.
- Request `no-store` and `DataCache.RespectNoStore` — `Bypass`.
- Authentication bypass would fire **and** `DataCacheRespectAuthBypass` is `true` (the default) — `Bypass` (Debug: Data Cache skipped due to auth bypass). Set it to `false` only when the Data Cache value is intentionally shared between authenticated callers.

## Cache key

`DefaultDomainKeyGenerator` builds a deterministic key (XxHash3).

With entity identity (`GetOrSetEntityAsync`):

```text
co3:{escapedDomain}:{versionHex}:e:{hash}
```

URL-shaped (`GetOrSetAsync` / collections):

```text
co3:{escapedDomain}:{versionHex}:u:{hash}
```

Hash material for URL-shaped keys includes:

1. Route pattern and route values (or path)
2. Query string (tracking parameters omitted: `utm_*`, `gclid`, …)
3. `Accept-Encoding` if `DataCache.VaryOnEncoding`
4. Scheme and host if `DataCache.VaryOnPublicAddress`

Entity keys hash `entityKind` + `resourceId` (plus the same encoding / public-address flags when enabled), not path or query. The lookup string carries only the `u` / `e` marker and hash — kind and id stay in tags for invalidation.

The string includes **domain** and **Version**. Every entry is tagged `domain:{name}`.

Details: [cache-keys.md](cache-keys.md).

### Custom key generator

Implement `IDomainKeyGenerator` when you must vary on something the default ignores (tenant claim, extra header). Register it before `AddCacheOrchestrator` (`TryAddSingleton` will keep yours), or `Replace` it afterwards.

```csharp
using CacheOrchestrator.DataCache;

services.AddSingleton<IDomainKeyGenerator, TenantKeyGenerator>();
services.AddCacheOrchestrator(configuration);
```

```csharp
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.Vary;

public sealed class TenantKeyGenerator : IDomainKeyGenerator
{
    private readonly DefaultDomainKeyGenerator _inner;

    public TenantKeyGenerator(CacheVaryMaterializer materializer)
        => _inner = new DefaultDomainKeyGenerator(materializer);

    public string Generate(DomainCacheKeyContext context)
    {
        var baseKey = _inner.Generate(context);
        var tenantId = context.HttpContext.User.FindFirst("tenant_id")?.Value ?? "anon";
        return $"{baseKey}|t:{tenantId}";
    }
}
```

`new DefaultDomainKeyGenerator()` (no materializer) skips `ICacheVaryContributor`, Accept, and auth-user material. Prefer the DI constructor above, or replace `IDomainKeyGenerator` after `AddCacheOrchestrator`.

Keys must be deterministic, must not contain secrets (they land in Redis and in logs), and should stay short.
`context.Shape` is part of the contract: URL-shaped and collection calls must not accidentally inherit entity identity already present on the request.

## Results (`X-CacheOrchestrator` `dc=` and `DataCacheResult`)

| Result | Meaning |
|--------|---------|
| `Hit` | Served from cache |
| `Miss` | Factory ran; value stored |
| `Stale` | Factory failed; fail-safe may serve stale (**Fusion**; Hybrid does not expose the same fail-safe model) |
| `Bypass` | Skipped (for example `no-store`, or auth bypass when `DataCacheRespectAuthBypass`) |
| `Off` | Data Cache disabled for the domain. The factory still runs and counts as a factory invocation (FA run). |
| `Unresolved` | No domain resolved; factory ran uncached (also a factory invocation). |

There is **no** `DataCacheResult.Fail` and **no** `dc=fail` on `X-CacheOrchestrator`. A hard factory throw with no fail-safe value is recorded on the meter as `cache_orchestrator.dc.requests` `result=fail` (and `factory.duration`), then the exception propagates.

When `dc` is not `hit`, `X-CacheOrchestrator` also includes `fa=run`. `dc=n/a` means the endpoint generated the response without making a Data Cache operation; it is a header-only state, not a `DataCacheResult` enum value. Admin and factory instruments still count that application/origin work. An Output Cache `hit` omits `dc` and `fa`.

Admin Console App's exclusive pipeline mix is **Output Cache hit + fresh Data Cache hit + factory run**. Factory run is the share of requests that require application/origin work, including direct `dc=n/a`, `off`, `unresolved`, bypass with factory, miss, and stale. **Data Cache stale %** is an overlay on requests, not a fourth mix segment. Layer `bypass` remains an authentication or `no-store` skip, not “caching disabled”.

---

## FusionCache provider

**CacheOrchestrator.FusionCache** registers ZiggyCreatures FusionCache as `IDataCacheProvider`. Entries are **serializable objects** (JSON via System.Text.Json): L1 in memory, optional L2 via a distributed store, optional backplane. Named engines: root **`DataCacheInstances`**. Redis L2 / backplane: [backends.md](backends.md), [deployment.md](deployment.md).

### Entry options

| Domain setting | FusionCache |
|----------------|-------------|
| `DataCache.TtlSeconds` → `DataCacheTtl` | `Duration` (capped by `FusionCache.HardTtlSeconds` if soft is larger) |
| `FusionCache.FailSafeSeconds` | `FailSafeMaxDuration` (+ fail-safe enabled when &gt; 0) |
| `FusionCache.JitterSeconds` | `JitterMaxDuration` |
| `FusionCache.EagerRefreshRatio` | `EagerRefreshThreshold` (`0` = disabled) |
| `FusionCache.FactorySoftTimeoutSeconds` / `FactoryHardTimeoutSeconds` | `FactorySoftTimeout` / `FactoryHardTimeout` |
| `FusionCache.AllowBackground*` | distributed and backplane background work |

Stampede protection and fail-safe stale serve come from FusionCache itself. Nested JSON schema: [configuration.md](configuration.md#fusioncache-cacheorchestratorfusioncache-only).

Effective Fusion settings are merged and cached per normalized domain and runtime-override stamp. Prepared `FusionCacheEntryOptions` are also reused while the Core and Fusion snapshots are unchanged, so a normal L1 hit does not traverse Configuration Binder or rebuild entry options. Configuration reload and Admin overrides replace the cached snapshots.

Footprint factories select their complete tags inside the Fusion factory context, before Fusion publishes that value. There is no second Core overwrite after a miss; background refresh carries the new members, dependencies and aliases with its value.

The provider stores a small typed envelope around the application value so it can distinguish a value materialized by the current call from a cached or fail-safe stale value. Every node that shares that L2 store must run a build that understands the same envelope.

---

## HybridCache provider

**CacheOrchestrator.HybridCache** registers Microsoft HybridCache as `IDataCacheProvider`.

```csharp
builder.Services.AddHybridCache();
builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration);
builder.Services.AddCacheOrchestratorHybridCache();
```

- Uses portable **`DataCache.TtlSeconds`** (expiration / local expiration).
- Applies the resolved `DataCacheNamespace` to physical keys and tags, including optional distributed HybridCache storage.
- Supports only `DataCacheInstances:default`; startup validation rejects named instances instead of allowing them to share one DI HybridCache.
- Nested **`FusionCache`** settings are ignored (no fail-safe / hard TTL / factory timeouts / named Fusion instances).
- Optional L2: configure HybridCache / `IDistributedCache` as usual (outside this package) — not Fusion `AddRedisBackend`.
- Prefer **Fusion** when you need fail-safe, eager refresh, or the full Fusion surface.

Footprint-aware entries use a unique validity marker stored with their complete tags before the payload is published. A footprint hit checks that marker through HybridCache; a missing or invalidated marker causes a refresh. The marker and payload both use the domain TTL and native L1/L2 storage. This costs one extra native lookup for footprint hits and one marker write for each materialization; ordinary `GetOrSetAsync` hits keep the direct path. After three consecutive invalidated generations, the current call runs its factory uncached to bound retry work. Native HybridCache tag propagation and expiration guarantees still apply.

Like the Fusion provider, HybridCache stores an internal typed envelope used to report whether this call materialized the returned value. Run the same package build on every node that shares that distributed store.

Package README: [CacheOrchestrator.HybridCache](../../src/CacheOrchestrator.HybridCache/README.md).

---

## Related

- [Guide — concepts](../guide/concepts.md) — domain model and Data Cache role  
- [Packages](../guide/packages.md) — Fusion vs Hybrid vs meta packages  
- [cache-keys.md](cache-keys.md) — `u:` / `e:` provider keys and hash material  
- [configuration.md](configuration.md) — `DataCache` / `DataCacheInstances` / `Distributed`  
- [invalidation.md](invalidation.md) — domain and entity tag purge  
- [entity-footprint.md](entity-footprint.md) — members, depends-on, aliases  
- [Output Cache](output-cache.md) — HTTP policy and domain binding on endpoints  
- [architecture.md](../contributor/architecture.md) — request flow and package layout  
