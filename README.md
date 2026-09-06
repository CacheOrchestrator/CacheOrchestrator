<img src="docs/assets/logo-small.png" height="100" alt="CacheOrchestrator logo" />

# CacheOrchestrator

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-blueviolet.svg?style=flat-square)](https://www.nuget.org/packages/CacheOrchestrator/3.0.0-beta.3)
[![Build Status](https://img.shields.io/github/actions/workflow/status/CacheOrchestrator/CacheOrchestrator/build.yml?branch=main&style=flat-square)](https://github.com/CacheOrchestrator/CacheOrchestrator/actions)
[![NuGet](https://img.shields.io/nuget/vpre/CacheOrchestrator.svg?style=flat-square)](https://www.nuget.org/packages/CacheOrchestrator/3.0.0-beta.3)

**CacheOrchestrator is a multi-tier cache coordination and synchronized invalidation library for .NET.**

**CacheOrchestrator** defines **Client Cache**, **Edge Cache**, **Output Cache**, and **Data Cache** policies through a **single domain** model. Each cache keeps its own responsibility, while the domain coordinates lifetimes, cache identity, and server-side invalidation.

<img src="docs/assets/drawing-01.svg" height="350" alt="A request passing through Client Cache, Edge Cache, Output Cache, and Data Cache" />

A request can pass through the following cache layers:

* **Client Cache (CC)** — prevents unnecessary requests from leaving the client.
* **Edge Cache (EC)** — serves the stored HTTP response from an edge node (a CDN or a reverse proxy) so the request need not hit your server.
* **Output Cache (OC)** — serves the stored HTTP response so the server endpoint need not run.
* **Data Cache (L1/L2)** — serves the stored object so the factory (your database or service) need not run.


In real-world applications, caching layers are often fragmented. You might have an in-memory Output Cache on one end, a Redis-backed Data Cache on the other, and optionally an Edge Cache—each operating with different TTLs. When these isolated layers aren't synchronized, they easily work against each other. CacheOrchestrator unifies them under a single domain model to coordinate their lifecycles and policies. This is especially critical for cache invalidation: clearing the Data Cache is pointless if the Output Cache or Edge Cache continues serving stale HTTP responses. CacheOrchestrator applies the same logical invalidation to Output Cache and Data Cache and, when the optional Edge integration is configured, queues a provider-specific tag purge. Edge delivery is asynchronous and best-effort; responses already cached by browsers remain subject to their client cache policy.

---

## Table of Contents

- [Why CacheOrchestrator](#why-cacheorchestrator)
- [Quick start](#quick-start)
- [Why domains](#why-domains)
- [Playground topology labs](#playground-topology-labs)
- [Packages and applications](#packages-and-applications)
- [Prerelease status](#prerelease-status)
- [Documentation](#documentation)

---

## Why CacheOrchestrator

- **One domain, four cache layers** — Define caching policy once per domain and coordinate Client Cache, Edge Cache, Output Cache, and Data Cache with independent lifetimes.

- **Synchronized invalidation** — Invalidate related cache layers together so stale data does not survive in one layer after another has been cleared.

- **Entity-aware caching** — Associate cached resources with logical entities and invalidate only the affected entries instead of flushing an entire domain.

- **Planned cache cutovers** — Change cache policies and versions through controlled domain generations, avoiding disruptive global cache flushes.

- **Distributed topologies** — Scale from a single in-memory instance to multi-instance deployments with shared caches and distributed invalidation.

- **Observable and extensible** — Expose cache and orchestration behavior through diagnostics and metrics, while keeping cache engines, transports, and integrations replaceable.

---

## Quick start

### Try it in one minute

Clone the repository and run the minimal sample:

```bash
dotnet run --project samples/CacheOrchestrator.Minimal
```

In another terminal, test the endpoint:

```bash
curl -i http://localhost:5290/hello
curl -i http://localhost:5290/hello
```

The first response is an Output Cache miss (the sample waits about 200 ms). The second is a hit. Look at the `X-CacheOrchestrator` header: `oc=miss`, then `oc=hit`.

Sample notes: [samples/CacheOrchestrator.Minimal](samples/CacheOrchestrator.Minimal/README.md)

### Add it to your app

Install the `CacheOrchestrator` meta package (`CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache`):

```bash
dotnet add package CacheOrchestrator --prerelease
```

Configure a domain in `appsettings.json`:

```json
{
  "Cache": {
    "Namespace": "my-app",
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "promotions": {
        "Version": "1",
        "OutputCache": { "TtlSeconds": 120 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 60 }
      },
      "catalog": {
        "Version": "1",
        "DataCache": { "TtlSeconds": 300 },
        "OutputCache": { "TtlSeconds": 120 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 60 }
      }
    }
  }
}
```

Register the library in your `Program.cs`:

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);

var app = builder.Build();
app.UseCacheOrchestrator();
```

Start with an Output Cache endpoint:

```csharp
app.MapGet("/api/promotions", () => new
{
    Title = "Summer sale",
    DiscountPercent = 20,
    GeneratedAtUtc = DateTimeOffset.UtcNow
})
.CacheOutputWithDomain("promotions");
```

Next, apply the `catalog` domain to a product endpoint. Declare entity identity once on the route (`entityKind` + `resourceRouteKey`); `IDomainDataCache` reuses the same domain policies:

```csharp
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache) =>
{
    // Caches the object and HTTP response, and applies Client Cache headers.
    var product = await cache.GetOrSetEntityAsync(http, ct => LoadProductAsync(id, ct));
    return product is null ? Results.NotFound() : Results.Json(product);
})
.CacheOutputWithDomain("catalog", entityKind: "products", resourceRouteKey: "id");
```

When that product changes, invalidate only that entity across Output Cache and Data Cache:

```csharp
app.MapPut("/api/products/{id:int}", async (int id, Product updatedProduct, ICacheOrchestratorInvalidator invalidator) =>
{
    await UpdateProductAsync(id, updatedProduct);
    // Invalidates matching Output Cache and Data Cache entries.
    await invalidator.InvalidateEntityAsync("catalog", "products", id);
    return Results.NoContent();
});
```

> **Note for Controllers & Class Libraries:**
> On a traditional controller, use the `[CacheDomain("catalog", "id", "products")]` attribute and inject `IDomainDataCache` in the same way. Class libraries can depend directly on `ICacheOrchestrator` from the Core package instead, using the exact same domain policies, without needing an `HttpContext`.

> **Tip for Entity Framework Core users:**
> For tracked changes saved through `SaveChanges`, the EF Core integration can eliminate manual invalidation.

See [samples/CacheOrchestrator.Sample](samples/CacheOrchestrator.Sample/README.md)
for a playground with TTLs, scheduling, and CRUD.

### Scale it with Redis

To add Redis-backed caching, install `CacheOrchestrator.Redis`, register its backends, and update the configuration. Your endpoint code stays completely **unchanged**.

Install the Redis meta package:

```bash
dotnet add package CacheOrchestrator.Redis --prerelease
```

Register the library in your `Program.cs`:
```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration, o => o.AddRedisBackend());
```

Merge these settings into `appsettings.json` to use Redis for Fusion Data Cache L2 and its backplane:

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "Redis" }
    },
    "Redis": { "Configuration": "localhost:6379" }
  }
}
```

This keeps Output Cache in memory and moves the Fusion Data Cache L2 and backplane to Redis. Set `OutputCache:Provider` to `Redis` when instances should share complete HTTP responses as well.

---

## Why domains

A domain is a named set of cache rules: lifetimes, which layers to use, and how they are backed. For example, in a fleet tracking application, different types of data require different cache configurations:

- **Static mapping assets** may change once a year. Long Output Cache and Client Cache lifetimes are enough; Data Cache is optional.
- **Map tiles and batched datasets** change on a published schedule. Client lifetimes can stay long during the calm period and automatically shorten as the cutover approaches. The [Client Cache Schedule](docs/guide/client-cache-schedule.md) coordinates that countdown without changing Output Cache or Data Cache TTLs.
- **Fleet telemetry** ages in minutes. A short lifetime, in-memory Output Cache, and a shared Redis Data Cache with a backplane keep several instances consistent.
- **Live vehicle positions** age in seconds. FusionCache locking and fail-safe stop a stampede when many callers miss at once; Output Cache stays off or very short.

The endpoint code is the same shape in every case. The domain is what differs.

Domains are the unit of configuration. Within a domain you can optionally use **entity identity** (`entityKind` + id, and related footprints) so per-entity keys and invalidation are possible.

---

## Playground topology labs

To try **multi-layer layouts** (Admin Console App, Prometheus, Redis L2, multiple instances, cluster bus, and Varnish Edge) without wiring Docker yourself, use the playground **topology labs** — one Compose command per stage.

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/01-observability.yml up --build
```

Stages climb from a single in-memory playground to a dual Redis + HTTP bus architecture, then add a focused Varnish Edge exercise.

**Full guide & diagrams:** [samples/CacheOrchestrator.Sample/labs/README.md](samples/CacheOrchestrator.Sample/labs/README.md) — See what each stage teaches and how they evolve.

---

## Packages and applications

The library is **modular**. `CacheOrchestrator.Core` provides the foundational policies, `ICacheOrchestrator`, and the transport-independent Management API. From there, you can opt into specific packages to match your stack: Data Cache providers, ASP.NET Core, Redis, HttpBus, EF invalidation, and tag-native Edge cache integration. See the [Packages and composition](docs/guide/packages.md) guide to learn how to wire them together.

| Package | Purpose |
|---------|---------|
| [CacheOrchestrator](https://www.nuget.org/packages/CacheOrchestrator/3.0.0-beta.3) | Meta package: `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache` (for typical web apps). |
| [CacheOrchestrator.Core](https://www.nuget.org/packages/CacheOrchestrator.Core/3.0.0-beta.3) | Domain models, orchestration, invalidation, and management contracts (no ASP.NET dependency). |
| [CacheOrchestrator.AspNetCore](https://www.nuget.org/packages/CacheOrchestrator.AspNetCore/3.0.0-beta.3) | Output Cache, Client Cache, HTTP helpers, and the Admin API. |
| [CacheOrchestrator.FusionCache](https://www.nuget.org/packages/CacheOrchestrator.FusionCache/3.0.0-beta.3) | ZiggyCreatures FusionCache Data Cache provider. |
| [CacheOrchestrator.HybridCache](https://www.nuget.org/packages/CacheOrchestrator.HybridCache/3.0.0-beta.3) | Microsoft HybridCache Data Cache provider. |
| [CacheOrchestrator.Redis](https://www.nuget.org/packages/CacheOrchestrator.Redis/3.0.0-beta.3) | Meta Redis: Output Cache store and Fusion L2 / backplane (`AddRedisBackend`). |
| [CacheOrchestrator.AspNetCore.Redis](https://www.nuget.org/packages/CacheOrchestrator.AspNetCore.Redis/3.0.0-beta.3) | Redis Output Cache store only (`AddRedisOutputCacheBackend`). |
| [CacheOrchestrator.FusionCache.Redis](https://www.nuget.org/packages/CacheOrchestrator.FusionCache.Redis/3.0.0-beta.3) | Redis Fusion L2 / backplane only (`AddRedisFusionCacheBackend`). |
| [CacheOrchestrator.HttpBus](https://www.nuget.org/packages/CacheOrchestrator.HttpBus/3.0.0-beta.3) | Syncs invalidations, versions, and settings across all instances via HTTP cluster bus. |
| [CacheOrchestrator.EFCore.Invalidation](https://www.nuget.org/packages/CacheOrchestrator.EFCore.Invalidation/3.0.0-beta.3) | Automatic cache invalidation after a successful Entity Framework Core `SaveChanges`. |
| `CacheOrchestrator.Edge` | Provider-neutral opaque response tags and queued edge invalidation contracts. - upcoming 3.0.0-beta.4 |
| `CacheOrchestrator.Edge.Cloudflare` | Cloudflare `Cache-Tag`, edge cache-control, and purge API provider. - upcoming 3.0.0-beta.4 |
| `CacheOrchestrator.Edge.Varnish` | Varnish `xkey`, edge TTL/grace metadata, and protected PURGE provider. - upcoming 3.0.0-beta.4 |


| Application | Purpose |
|-------------|---------|
| [CacheOrchestrator.AdminConsole](src/CacheOrchestrator.AdminConsole/) | Standalone Admin Console App for live stats, domain configuration, triggering invalidations, and adjusting Versions or TTLs on the fly. Available as a Docker image. See [Admin Console README](src/CacheOrchestrator.AdminConsole/) · [Deploy Admin Console App](deploy/admin/README.md). |

---

## Prerelease status

> [!IMPORTANT]
> CacheOrchestrator v3 is a **full redesign**, not an incremental evolution of 1.x / 2.x. Previous published lines (v1.0.0 and v2.1.x) are maintained for legacy continuity only. The v3 does not preserve a direct migration story or API compatibility with them; please treat v3 as a new architectural surface under the same name.
>
>**v3 is in prerelease (beta)**
>
> This documentation describes **CacheOrchestrator v3**. Public APIs may change until the stable **v3.0.0** release. Install the prerelease with the Quick start above (`dotnet add package … --prerelease`).<br>
> **Help test the prerelease.** Reports from real ASP.NET Core applications, standalone workers, Redis deployments, browsers, and playground labs are especially valuable. Successful results and confusing behavior are welcome too — see [Contributing](CONTRIBUTING.md#help-test-v3).<br>
> To build from source or contribute code, clone this repository — `main` tracks the same v3 work and may move faster than the latest beta package.

---

## Documentation

- [Getting started](docs/guide/getting-started.md) — first endpoint, `X-CacheOrchestrator`, what to read next
- [Guide](docs/guide/README.md) — concepts, topologies, operations
- [Documentation index](docs/README.md) — configuration, keys, deployment, architecture
- [FAQ](docs/guide/faq.md) — common mistakes and limits
- [Comparison](docs/guide/comparison.md) — the usual stack versus CacheOrchestrator
- [CONTRIBUTING](CONTRIBUTING.md)
- [SECURITY](SECURITY.md)
- [LICENSE](LICENSE.md) (MIT)

