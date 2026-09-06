# Domain profiles

> **Guide** — snapshot vs changing-record domain recipes.

The most important domain decision is how the underlying data changes.

- A **snapshot domain** changes as one coordinated generation: map tiles, annual imagery, a monthly export, or a published price list.
- A **dynamic / CRUD domain** changes one entity at a time: products, accounts, orders, or other CRUD data.

The same application can use both profiles. Give them separate domains because their freshness rules are fundamentally different.

## Table of Contents

- [Choose the freshness boundary first](#choose-the-freshness-boundary-first)
- [Snapshot profile](#snapshot-profile)
- [Dynamic / CRUD profile](#dynamic-crud-profile)
- [Collections and related data](#collections-and-related-data)
- [Choose an ETag policy deliberately](#choose-an-etag-policy-deliberately)
- [Account for authenticated traffic](#account-for-authenticated-traffic)
- [When neither profile fits exactly](#when-neither-profile-fits-exactly)

## Choose the freshness boundary first

Ask one question:

> Can the response for one resource change while the rest of the domain remains on the same release?

If the answer is **no**, use a snapshot profile and make `Version` the cutover boundary. If the answer is **yes**, use a dynamic profile and invalidate the changed entity while keeping `Version` stable.

| Decision | Snapshot | Dynamic / CRUD |
|----------|----------|----------------|
| Content changes | Whole dataset at a cutover | Individual resources at any time |
| Primary freshness control | Change domain `Version` | `InvalidateEntityAsync` |
| TTL role | Cleanup and fallback | Safety bound when invalidation is missed |
| Client TTL | Usually long; schedule it before cutover | Usually short, private, or `no-store` |
| Data Cache API | `GetOrSetAsync` is often enough | `GetOrSetEntityAsync` |
| Endpoint entity identity | Usually unnecessary | `entityKind` + `resourceRouteKey` |
| Typical ETag mode | `Version` | `None` or an application-owned ETag |

## Snapshot profile

Use a snapshot domain when every resource belongs to one immutable generation. Within `Version: "2030-08"`, a tile or export must never silently change. Publishing new content means publishing a new domain version.

This profile rewards long cache lifetimes because old content is not expected to mutate in place.

### Configure the generation

```json
{
  "Cache": {
    "Domains": {
      "osm-tiles": {
        "Version": "2030-08",
        "DataCache": {
          "TtlSeconds": 2592000
        },
        "OutputCache": {
          "TtlSeconds": 604800,
          "ETagMode": "Version"
        },
        "ClientCache": {
          "Cacheability": "Public",
          "TtlSeconds": 2592000,
          "TtlMinSeconds": 900,
          "ScheduledUpdateUtc": "2030-09-01T00:00:00Z",
          "MustRevalidateNearUpdate": true
        }
      }
    }
  }
}
```

This domain keeps Data Cache objects for 30 days, server HTTP responses for 7 days, and client responses for up to 30 days. The [Client Cache Schedule](client-cache-schedule.md) shortens the client `max-age` as the September cutover approaches; it does not change the server TTLs.

Engine-specific FusionCache settings such as hard TTL, fail-safe, jitter, and factory timeouts can be added under `FusionCache`. They are tuning controls, not part of the snapshot identity.

### Apply the domain

```csharp
app.MapGet("/tiles/{z}/{x}/{y}", async (
    HttpContext http,
    int z,
    int x,
    int y,
    IDomainDataCache cache,
    CancellationToken cancellationToken) =>
{
    byte[] tile = await cache.GetOrSetAsync(
        http,
        token => LoadTileAsync(z, x, y, token),
        cancellationToken);

    return Results.Bytes(tile, "image/png");
})
.CacheOutputWithDomain("osm-tiles");
```

The URL already distinguishes tiles, so this endpoint does not need entity identity. Both Output Cache and `GetOrSetAsync` use the request identity and domain version.

### Perform the cutover

For a planned September release:

1. Set `ScheduledUpdateUtc` early enough for client TTLs to ramp down.
2. Prepare the new dataset without changing the current generation in place.
3. At go-live, make the new data available and change `Version` to `"2030-09"`.
4. Set the next scheduled update, or clear `ScheduledUpdateUtc` if no date is known.
5. Watch `X-CacheOrchestrator`, metrics, and origin load while the new generation warms.

Requests use cache keys for the new Version stamp. Entries under the previous stamp are not selected and expire naturally; a domain purge is optional cleanup, not the freshness mechanism.

## Dynamic / CRUD profile

Use a **dynamic / CRUD** domain when one resource can change without releasing the whole dataset. Product `42` may change while products `7` and `99` remain valid.

Keep the domain `Version` stable for ordinary writes. Give each detail endpoint an entity identity and invalidate that identity after the write succeeds.

### Configure bounded lifetimes

```json
{
  "Cache": {
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": {
          "TtlSeconds": 300
        },
        "OutputCache": {
          "TtlSeconds": 120,
          "ETagMode": "None"
        },
        "ClientCache": {
          "Cacheability": "Public",
          "TtlSeconds": 30
        }
      }
    }
  }
}
```

The server entries may live longer because the write path removes them immediately. Their TTLs remain a safety bound if an invalidation is missed.

The 30-second public client TTL is a product decision, not a server invalidation guarantee. A browser may serve the old response for those 30 seconds after a write. Use a shorter TTL or `NoStore` when browsers must observe changes sooner; `Private` prevents shared caching but still permits browser caching. A configured [Edge integration](edge.md) queues tag purges for CDN copies independently of the browser TTL. Until purge completes, or if it fails, the Edge cache follows its own freshness policy.

### Declare identity on the read

```csharp
app.MapGet("/api/products/{id:int}", async (
    HttpContext http,
    int id,
    IDomainDataCache cache,
    CancellationToken cancellationToken) =>
{
    Product? product = await cache.GetOrSetEntityAsync(
        http,
        token => LoadProductAsync(id, token),
        cancellationToken);

    return product is null ? Results.NotFound() : Results.Json(product);
})
.CacheOutputWithDomain("catalog", entityKind: "products", resourceRouteKey: "id");
```

The endpoint declares the identity once:

- domain: `catalog`;
- entity kind: `products`;
- resource id: the value of route key `id`.

`GetOrSetEntityAsync` consumes that identity for its Data Cache key and tags. Output Cache attaches the same entity tag to the HTTP response.

### Invalidate after the write succeeds

```csharp
app.MapPut("/api/products/{id:int}", async (
    int id,
    ProductUpdate product,
    ICacheOrchestratorInvalidator invalidator,
    CancellationToken cancellationToken) =>
{
    await SaveProductAsync(id, product, cancellationToken);
    await invalidator.InvalidateEntityAsync("catalog", "products", id, cancellationToken);

    return Results.NoContent();
});
```

Save first, invalidate second. If invalidation happened before the transaction committed, another request could refill the cache with the old value.

The resulting flow is:

```text
GET /api/products/42  → miss → database says 10.00 → store Output Cache + Data Cache entries
GET /api/products/7   → miss → database says 19.50 → store Output Cache + Data Cache entries
PUT /api/products/42  → database save 12.50 → invalidate product 42
GET /api/products/42  → miss → database says 12.50 → store new entries
GET /api/products/7   → still a hit
```

> The optional Entity Framework Core integration can automatically invalidate changed entities upon a successful `SaveChanges` call. See [EF Core invalidation](../reference/ef-core-invalidation.md).

## Collections and related data

A product change may also affect a cached product list, category page, or promotion. Invalidating only the detail entry is not enough unless those cached results carry a matching footprint.

Use `GetOrSetEntitySetAsync` and `EntitySet` for collections, or extend an entity result with members and dependencies. CacheOrchestrator can then tag a cached result with the entities that influence it.

Keep the primary identity simple and stable. Add relationships only when a change to that related entity really makes the cached result stale. The complete patterns are in [Entity footprint](../reference/entity-footprint.md).

## Choose an ETag policy deliberately

CacheOrchestrator-generated ETags are based on the domain generation, not a hash of the response body.

| `ETagMode` | Behaviour | Use it for |
|------------|-----------|------------|
| `Version` | One generation ETag shared by the domain | Immutable snapshot domains |
| `Resource` | A distinct ETag per resource identity, still derived from domain `Version` | Snapshot resources when intermediaries require distinct validators |
| `None` | CacheOrchestrator emits no ETag | Dynamic resources invalidated under a stable version |

`Resource` makes validators distinct, but it does not make them change when one row is invalidated under the same domain version. For dynamic data, use `None` or implement a true application-owned ETag from a row version or update timestamp, including conditional request handling.

ETags affect browser and CDN revalidation. They do not select ASP.NET Core Output Cache entries.

## Account for authenticated traffic

The safe default bypasses Output Cache and blocks the Client Cache when the request has an authenticated identity or an `Authorization` header. By default, the Data Cache follows that bypass too.

If a domain intentionally caches authenticated traffic, decide whether content is shared, tenant-specific, or user-specific before changing `AuthBypassMode` and vary settings. A wrong vary policy can serve one user's representation to another.

Use the matrix in [Vary and authenticated traffic](../reference/vary.md) before enabling it.

## When neither profile fits exactly

Treat snapshot and dynamic as starting points, not rigid product modes:

- A mostly static catalog can use entity invalidation for daily edits and a `Version` bump for a major schema release.
- A snapshot domain can omit Data Cache when generating the HTTP response is already cheap.
- A dynamic internal API can use `NoStore` for clients while keeping server-side Output Cache and Data Cache.
- A collection can be dynamic at entity-kind level when tracking every member would create too many tags.

Write down the freshness event for each domain: **time passes**, **a known entity changes**, or **a new generation ships**. Then choose TTL, invalidation, and versioning to match that event.

Next: choose the [packages](packages.md) that provide the layers and engines your profiles require.
