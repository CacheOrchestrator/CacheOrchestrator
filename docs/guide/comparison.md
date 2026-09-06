# Comparison

> **Guide** — hand-rolled caching stack vs CacheOrchestrator.

CacheOrchestrator is useful when an application must keep several cache layers and several domains consistent. It replaces repeated policy and coordination code, not the underlying cache engines.

This page compares the responsibilities you own with direct ASP.NET Core and FusionCache usage against the same application using CacheOrchestrator.

## Table of Contents

- [The underlying stack stays the same](#the-underlying-stack-stays-the-same)
- [How much application code you own](#how-much-application-code-you-own)
- [Compare one snapshot endpoint](#compare-one-snapshot-endpoint)
- [Compare dynamic entity invalidation](#compare-dynamic-entity-invalidation)
- [Responsibility matrix](#responsibility-matrix)
- [When CacheOrchestrator is a strong fit](#when-cacheorchestrator-is-a-strong-fit)
- [When direct platform APIs may be simpler](#when-direct-platform-apis-may-be-simpler)
- [Adopt it incrementally](#adopt-it-incrementally)

## The underlying stack stays the same

With or without CacheOrchestrator, the request can still use:

- browser or CDN caching through HTTP `Cache-Control`;
- ASP.NET Core Output Caching for complete responses;
- FusionCache or HybridCache for application objects;
- Redis when stores must be shared;
- application code that loads and updates data.

CacheOrchestrator adds a domain model above those components:

```text
Domain configuration
   ├─ client header policy
   ├─ Output Cache policy and identity
   ├─ Data Cache policy and engine selection
   └─ shared invalidation tags and Version
```

The benefit grows when those policies would otherwise be repeated or allowed to drift.

## How much application code you own

The engines stay. What shrinks is **application-owned coordination** — vary lists, TTLs, Version, client headers, auth alignment, tags, and diagnostics that would otherwise be hard-coded next to each endpoint.

For a concrete GET with Output Cache + Data Cache + query/Accept vary + client max-age, see the side-by-side:

[Worked example: one endpoint with and without CacheOrchestrator](comparison-endpoint-example.md)

The sections below stay shorter: snapshot cutover and entity invalidation at the responsibility level.

## Compare one snapshot endpoint

Consider a tile dataset with these requirements:

- a new generation is published monthly;
- data objects may stay cached for 30 days;
- HTTP responses may stay in Output Cache for 7 days;
- public clients may cache for 30 days, but their TTL must shorten before the next release;
- authenticated requests must not accidentally enter a shared cache;
- operators need one way to inspect and invalidate the domain.

### Using the platform libraries directly

The application owns each concern separately:

```csharp
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("osm-tiles", policy => policy
        .Expire(TimeSpan.FromDays(7))
        .Tag("domain:osm-tiles")
        .SetVaryByHost(true)
        .SetVaryByQuery("format")
        .SetVaryByValue("data-version", _ => "2030-08"));
});

builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(options =>
    {
        options.Duration = TimeSpan.FromDays(30);
        options.SetFailSafe(true, TimeSpan.FromDays(90));
    });
```

The endpoint or surrounding middleware must then own the remaining coordination:

- construct a Data Cache key containing domain, generation, and request identity;
- attach the correct invalidation tags;
- compute and emit client `Cache-Control`;
- implement the cutover ramp;
- keep auth bypass and vary dimensions aligned across Output Cache and Data Cache;
- generate ETags consistently;
- evict Output Cache and FusionCache together;
- add diagnostics that explain which layer served the request.

None of this is impossible. The risk is that each domain grows a slightly different implementation and that a later policy change reaches only one layer.

### Using CacheOrchestrator

The coordinated policy lives in one domain:

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

The endpoint names that policy and reuses its request snapshot:

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

At the next release, changing `Version` moves new server requests and validators to the new generation. Client Cache Schedule prepares browsers and CDNs to request that generation near the cutover. If an immediate server purge is needed, one invalidator addresses the domain:

```csharp
await invalidator.InvalidateDomainAsync("osm-tiles", cancellationToken);
```

The Client Cache Schedule, auth defaults, tags, vary materialization, and `X-CacheOrchestrator` diagnostics come from the resolved domain rather than endpoint-specific code.

## Compare dynamic entity invalidation

For a product detail endpoint, direct cache usage requires the write path to know every store and every tag convention:

```csharp
await fusionCache.RemoveByTagAsync(
    "entity:catalog:products:42",
    cancellationToken);

await outputCache.EvictByTagAsync(
    "entity:catalog:products:42",
    cancellationToken);
```

With CacheOrchestrator, the read declares entity identity once and the write addresses that identity through one abstraction:

```csharp
app.MapGet("/api/products/{id}", GetProductAsync)
   .CacheOutputWithDomain(
       "catalog",
       entityKind: "products",
       resourceRouteKey: "id");

await invalidator.InvalidateEntityAsync(
    "catalog",
    "products",
    42,
    cancellationToken);
```

The invalidator removes matching Output Cache and Data Cache entries. Multi-instance reach still depends on the chosen Redis and bus topology.

## Responsibility matrix

| Concern | Direct platform stack | CacheOrchestrator |
|---------|-----------------------|-------------------|
| Per-domain server TTLs | Named Output Cache policies and engine options | Nested domain settings |
| Client `Cache-Control` | Endpoint or middleware code | `ClientCache` settings |
| Planned client cutover | Custom algorithm and telemetry | Client Cache Schedule |
| Output/data vary parity | Maintain two identity implementations | Shared vary materialization |
| Authenticated traffic | Design and apply bypass rules in each layer | Conservative default with explicit opt-in |
| Domain and entity tags | Define and reproduce string conventions | Built-in identity and footprint |
| Coordinated server invalidation | Call every store correctly | `ICacheOrchestratorInvalidator` |
| Snapshot generation | Add version material to every key and validator | Domain `Version` |
| Data engine selection | Application DI and engine-specific calls | Provider packages behind stable APIs |
| Request diagnostics | Build headers, meters, traces, and logs | Built-in `X-CacheOrchestrator` and telemetry |
| Multi-instance commands | Custom transport or direct fan-out | Optional HttpBus / Admin Console App fan-out |

CacheOrchestrator reduces application-owned coordination. It does not remove the need to choose sensible TTLs, identities, topology, or security boundaries.

## When CacheOrchestrator is a strong fit

Use it when several of these are true:

- an endpoint uses both Output Cache and a Data Cache;
- the application has multiple data domains with different policies;
- writes must invalidate HTTP responses and cached objects together;
- snapshot datasets use controlled version cutovers;
- browser/CDN TTLs must shorten before a planned release;
- several instances need shared stores or distributed commands;
- operators need consistent diagnostics and live domain inspection;
- reusable libraries should stay independent of the host cache engine.

## When direct platform APIs may be simpler

Direct ASP.NET Core or engine APIs can be the clearer choice when:

- only one or two endpoints use one cache layer;
- there is no shared domain policy or cross-layer invalidation;
- the application needs engine-specific behaviour that the provider abstraction does not expose;
- an existing cache-key and invalidation architecture is already mature and consistent;
- adopting another configuration and diagnostics model would add more concepts than it removes.

CacheOrchestrator does not manage Redis operations or general CDN administration, replace FusionCache or HybridCache features, or guarantee cross-instance consistency without the required topology. Its optional [Edge integration](edge.md) does configure response freshness metadata and queue provider-specific tag invalidation.

## Adopt it incrementally

You do not need to enable every layer at once.

1. Start with one Output Cache domain and inspect `X-CacheOrchestrator`.
2. Add `IDomainDataCache` where object creation is expensive.
3. Declare entity identity for dynamic detail endpoints.
4. Move writes to `ICacheOrchestratorInvalidator` or the EF integration.
5. Add Redis when values must be shared or survive process restarts.
6. Add HttpBus only when local peer stores or runtime overlays need commands.

Endpoint domain names and the core invalidation model remain stable as infrastructure grows.

Try the complete first path in [Getting started](getting-started.md), or use the [FAQ](faq.md) when evaluating a specific edge case.

## Related

- [Worked example: one endpoint](comparison-endpoint-example.md) — with CO first, then hand-rolled for the same `GET`  
- [Getting started](getting-started.md) — minimal host wiring  
- [FAQ](faq.md) — boundary questions  
