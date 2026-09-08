# Package composition

> **How-to** — install, register, and configure CacheOrchestrator by scenario.

Copy-paste wiring for common stacks. Domain rules stay the same; only **packages**, **registration**, and **config** change.

Without `.CacheOutputWithDomain` / `[CacheDomain]`, the base Output Cache policy is `NoCache` — there is no Output Cache entry. EF mapping and SaveChanges rules: [EF Core invalidation](../reference/ef-core-invalidation.md).

Decision tables: [packages](../guide/packages.md) · [topologies](../guide/topologies.md).

## Table of Contents

- [1. Typical web app](#scenario-1)
- [2. Output Cache only](#scenario-2)
- [3. Data Cache only](#scenario-3)
- [4. Redis Data Cache L2](#scenario-4)
- [5. HybridCache](#scenario-5)
- [6. Class library + host](#scenario-6)
- [7. EF invalidation in a web app](#scenario-7)
- [8. EF in a library + web host](#scenario-8)
- [9. Edge cache: Cloudflare or Varnish](#scenario-9)

---
<a id="scenario-1"></a>
## 1. Typical web app

**Layers:** Output Cache, Data Cache (Fusion), Client Cache — all InMemory.

**Packages:** meta `CacheOrchestrator` (`CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache`).

```bash
dotnet add package CacheOrchestrator --prerelease
```

Alternatively install the two packages separately and call `AddCacheOrchestratorAspNetCore` then `AddCacheOrchestratorFusionCache`. Per-domain `OutputCache:Enabled` / `DataCache:Enabled` turn layers on or off without changing endpoint code (see also 2 / 3 for fewer packages).

**Registration**

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
```

**Config**

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 },
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code**

```csharp
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache) =>
{
    var data = await cache.GetOrSetAsync(http, ct => LoadProductAsync(id, ct));
    return Results.Json(data);
})
.CacheOutputWithDomain("catalog");
```

---

<a id="scenario-2"></a>
## 2. Output Cache only

**Layers:** Output Cache and Client Cache — InMemory. No Data Cache provider; the handler does not use `IDomainDataCache`.

**Packages:** `CacheOrchestrator.AspNetCore`.

```bash
dotnet add package CacheOrchestrator.AspNetCore --prerelease
```

**Registration**

```csharp
builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration);
```

**Config**

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "Domains": {
      "catalog": {
        "Version": "1",
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code**

```csharp
app.MapGet("/api/products/{id:int}", async (int id) =>
{
    var data = await LoadProductAsync(id);
    return Results.Json(data);
})
.CacheOutputWithDomain("catalog");
```

---

<a id="scenario-3"></a>
## 3. Data Cache only

**Layers:** Data Cache (Fusion) via `IDomainDataCache`. No Output Cache on endpoints (base policy `NoCache`; do not use `.CacheOutputWithDomain`).

**Packages:** `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache` (not the meta package).

```bash
dotnet add package CacheOrchestrator.AspNetCore --prerelease
dotnet add package CacheOrchestrator.FusionCache --prerelease
```

**Registration**

```csharp
builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration);
builder.Services.AddCacheOrchestratorFusionCache(builder.Configuration);
```

**Config**

```json
{
  "Cache": {
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 }
      }
    }
  }
}
```

**Code** — same `GetOrSetAsync` shape as 1; pass the domain because the route has no Output Cache domain metadata:

```csharp
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache) =>
{
    var data = await cache.GetOrSetAsync(http, "catalog", ct => LoadProductAsync(id, ct));
    return Results.Json(data);
});
```

---

<a id="scenario-4"></a>
## 4. Redis Data Cache L2

**Layers:** Output Cache InMemory; Data Cache (Fusion) with Redis L2 and backplane; Client Cache as in 1.

**Packages:** `CacheOrchestrator` + `CacheOrchestrator.Redis`.

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.Redis --prerelease
```

**Registration**

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration, o => o.AddRedisBackend());
```

**Config**

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "Redis" } },
    "Redis": { "Configuration": "localhost:6379" },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 },
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code** (same as 1)

```csharp
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache) =>
{
    var data = await cache.GetOrSetAsync(http, ct => LoadProductAsync(id, ct));
    return Results.Json(data);
})
.CacheOutputWithDomain("catalog");
```

For Redis as the Output Cache store as well, set `"OutputCache": { "Provider": "Redis" }` (same packages and registration).

---

<a id="scenario-5"></a>
## 5. HybridCache

**Layers:** Output Cache and Client Cache — InMemory; Data Cache via Microsoft HybridCache.

**Packages:** `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.HybridCache` (+ `Microsoft.Extensions.Caching.Hybrid`).

```bash
dotnet add package CacheOrchestrator.AspNetCore --prerelease
dotnet add package CacheOrchestrator.HybridCache --prerelease
dotnet add package Microsoft.Extensions.Caching.Hybrid
```

**Registration**

```csharp
builder.Services.AddHybridCache();
builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration);
builder.Services.AddCacheOrchestratorHybridCache();
```

**Config**

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 },
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code** (same as 1)

```csharp
app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache) =>
{
    var data = await cache.GetOrSetAsync(http, ct => LoadProductAsync(id, ct));
    return Results.Json(data);
})
.CacheOutputWithDomain("catalog");
```

---

<a id="scenario-6"></a>
## 6. Class library + host

**Layers:** Library uses Core Data Cache (`ICacheOrchestrator`). Web host adds Output Cache and Client Cache; worker stays HTTP-free.

**Packages:** library `CacheOrchestrator.Core`; web host same as 1; worker `CacheOrchestrator.Core` + `CacheOrchestrator.FusionCache`.

**Library packages**

```bash
dotnet add package CacheOrchestrator.Core --prerelease
```

**Library**

```csharp
public sealed class CatalogService(ICacheOrchestrator cache)
{
    public ValueTask<ProductDto?> GetProductAsync(CacheDomainContext cacheDomain, int id, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(cacheDomain, logicalKey: $"product:{id}", async ct => await LoadProductAsync(id, ct), cancellationToken);
}
```

**Host packages** (web — same as 1)

```bash
dotnet add package CacheOrchestrator --prerelease
```

**Registration** (web host)

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddScoped<CatalogService>();
```

**Config** (same shape as 1)

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 },
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code** (static domain)

```csharp
var catalogDomain = new CacheDomainContext("catalog");

app.MapGet("/api/products/{id:int}", async (int id, CatalogService catalog, CancellationToken ct) =>
{
    var data = await catalog.GetProductAsync(catalogDomain, id, ct);
    return Results.Json(data);
})
.CacheOutputWithDomain(catalogDomain.Domain);
```

**Code** (per-request domain name — same library; see [Output Cache — domain name templates](../reference/output-cache.md#domain-name-templates))

```csharp
CacheDomainContext CatalogDomain(HttpContext http) => new($"tenant-{http.Request.RouteValues["tenant"]}");

app.MapGet("/t/{tenant}/products/{id:int}", async (HttpContext http, int id, CatalogService catalog, CancellationToken ct) =>
{
    var data = await catalog.GetProductAsync(CatalogDomain(http), id, ct);
    return Results.Json(data);
})
.CacheOutputWithDomain(http => CatalogDomain(http).Domain);
```

**Worker** (same library)

```bash
dotnet add package CacheOrchestrator.Core --prerelease
dotnet add package CacheOrchestrator.FusionCache --prerelease
```

```csharp
builder.Services.AddCacheOrchestratorCore(builder.Configuration);
builder.Services.AddCacheOrchestratorFusionCache(builder.Configuration);
builder.Services.AddScoped<CatalogService>();

var domain = new CacheDomainContext($"tenant-{job.TenantId}");
await catalog.GetProductAsync(domain, job.ProductId, cancellationToken);
```

This composition is HTTP-free. `AddCacheOrchestratorCore` registers domain options, `ICacheOrchestrator`, invalidation, and cluster contracts; FusionCache supplies `IDataCacheProvider`. The worker has no middleware or endpoint setup.

---

<a id="scenario-7"></a>
## 7. EF invalidation in a web app

**Layers:** Same as 1, plus SaveChanges → automatic entity tag purge. Domain and `entityKind` on the GET must match the EF mapping (`[CacheEntity]`, Fluent `CacheInvalidate`, or `Map<T>`). Details: [ef-core-invalidation.md](../reference/ef-core-invalidation.md).

**Packages:** `CacheOrchestrator` + `CacheOrchestrator.EFCore.Invalidation`.

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.EFCore.Invalidation --prerelease
```

**Registration**

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
{
    opt.UseSqlServer(connectionString);
    opt.AddCacheOrchestratorInvalidation(sp);
});
```

**Config** (same shape as 1)

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 },
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code**

```csharp
[CacheEntity("catalog", "products")]
public class Product { public int Id { get; set; } public decimal Price { get; set; } }

app.MapGet("/api/products/{id:int}", async (HttpContext http, int id, IDomainDataCache cache, AppDbContext db) =>
{
    var data = await cache.GetOrSetEntityAsync(http, async ct =>
    {
        Product? p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : new ProductDto(p.Id, p.Price);
    });
    return data is null ? Results.NotFound() : Results.Json(data);
})
.CacheOutputWithDomain("catalog", resourceRouteKey: "id", entityKind: "products");

app.MapPut("/api/products/{id:int}", async (int id, UpdatePriceBody body, AppDbContext db, CancellationToken ct) =>
{
    Product product = await db.Products.SingleAsync(x => x.Id == id, ct);
    product.Price = body.Price;
    await db.SaveChangesAsync(ct); // invalidation follows the owning transaction's commit
    return Results.NoContent();
});
```

---

<a id="scenario-8"></a>
## 8. EF in a library + web host

**Layers:** Library owns DbContext and Core Data Cache reads/writes; web host adds Output Cache, Client Cache, and the EF interceptor. Keep the same domain / `entityKind` in mapping, `CacheDomainContext`, and `.CacheOutputWithDomain`.

**Packages:** library `CacheOrchestrator.Core` + `CacheOrchestrator.EFCore.Invalidation` (+ EF Core); host same as 1 plus EF invalidation registration.

**Library packages**

```bash
dotnet add package CacheOrchestrator.Core --prerelease
dotnet add package CacheOrchestrator.EFCore.Invalidation --prerelease
dotnet add package Microsoft.EntityFrameworkCore
```

**Library**

```csharp
[CacheEntity("catalog", "products")]
public class Product { public int Id { get; set; } public decimal Price { get; set; } }

public sealed class CatalogService(ICacheOrchestrator cache, AppDbContext db)
{
    public ValueTask<ProductDto?> GetProductAsync(CacheDomainContext cacheDomain, int id, CancellationToken cancellationToken) =>
        cache.GetOrCreateEntityAsync(cacheDomain, logicalKey: $"product:{id}", resourceId: id, async ct =>
            {
                Product? p = await db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == id, ct);
                return p is null ? null : new ProductDto(p.Id, p.Price);
            },
            defaultEntityKind: "products",
            cancellationToken);

    public async Task UpdatePriceAsync(int id, decimal price, CancellationToken cancellationToken)
    {
        Product product = await db.Products.SingleAsync(x => x.Id == id, cancellationToken);
        product.Price = price;
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

Map the entity in the library model (or let the host call `Map<Product>`):

```csharp
modelBuilder.Entity<Product>().CacheInvalidate("catalog", "products");
```

**Host packages**

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.EFCore.Invalidation --prerelease
```

**Registration**

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEfCoreInvalidation(builder.Configuration);
builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
{
    opt.UseSqlServer(connectionString);
    opt.AddCacheOrchestratorInvalidation(sp);
});
builder.Services.AddScoped<CatalogService>();
```

**Config** (same shape as 1 / 7)

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": { "default": { "Provider": "InMemory" } },
    "Domains": {
      "catalog": {
        "Version": "1",
        "DataCache": { "Enabled": true, "TtlSeconds": 300 },
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 }
      }
    }
  }
}
```

**Code**

```csharp
var catalogDomain = new CacheDomainContext("catalog", entityKind: "products");

app.MapGet("/api/products/{id:int}", async (int id, CatalogService catalog, CancellationToken ct) =>
{
    var data = await catalog.GetProductAsync(catalogDomain, id, ct);
    return data is null ? Results.NotFound() : Results.Json(data);
})
.CacheOutputWithDomain(catalogDomain.Domain, resourceRouteKey: "id", entityKind: "products");

app.MapPut("/api/products/{id:int}", async (int id, UpdatePriceBody body, CatalogService catalog, CancellationToken ct) =>
{
    await catalog.UpdatePriceAsync(id, body.Price, ct);
    return Results.NoContent();
});
```

---

<a id="scenario-9"></a>
## 9. Edge cache: Cloudflare or Varnish

**Layers:** your existing web composition plus Cloudflare edge freshness and tag invalidation.

**Packages:** the normal web host package plus `CacheOrchestrator.Edge.Cloudflare` (which brings the neutral Edge package transitively).

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.Edge.Cloudflare --prerelease
```

**Registration**

```csharp
using CacheOrchestrator.Edge.Cloudflare;
using CacheOrchestrator.Edge.DependencyInjection;

builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEdge(
    builder.Configuration,
    edge => edge.AddCloudflare());
```

**Config**

```json
{
  "Cache": {
    "Namespace": "my-app-production",
    "EdgeInstances": {
      "edge": {
        "Provider": "Cloudflare",
        "Cloudflare": {
          "ZoneId": "from-secret-configuration",
          "ApiToken": "from-secret-configuration"
        }
      }
    },
    "DomainDefaults": {
      "Edge": { "Enabled": false, "Instance": "edge", "TtlSeconds": 300 }
    },
    "Domains": {
      "catalog": {
        "OutputCache": { "Enabled": true, "TtlSeconds": 60 },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 30 },
        "Edge": {
          "Enabled": true,
          "TtlSeconds": 600,
          "StaleWhileRevalidateSeconds": 30,
          "StaleIfErrorSeconds": 300
        }
      }
    }
  }
}
```

Endpoint identity, footprints, EF invalidation, and explicit `Invalidate*Async` calls automatically drive the edge tags and purge queue. Keep credentials in environment variables or a secret provider. Full behavior and delivery guarantees: [Edge cache integration](../guide/edge.md).

For Varnish, install `CacheOrchestrator.Edge.Varnish`, call `edge.AddVarnish()`, and configure the protected xkey endpoint:

```json
{
  "Cache": {
    "EdgeInstances": {
      "edge": {
        "Provider": "Varnish",
        "Varnish": {
          "PurgeUrl": "http://varnish-internal/cache-orchestrator/purge"
        }
      }
    }
  }
}
```

The required `xkey` VMOD and VCL are in the [Edge cache guide](../guide/edge.md#varnish-vcl-contract). Varnish supports `TtlSeconds` and `StaleWhileRevalidateSeconds`; use Cloudflare when the domain also requires the portable `StaleIfErrorSeconds` contract.

---

## Related

- [Packages](../guide/packages.md) — which package to choose for your stack
- [Topologies](../guide/topologies.md) — InMemory, Redis, HttpBus, and when to combine them
- [Configuration](../reference/configuration.md) — `Cache` schema, defaults, and ownership
- [Output Cache](../reference/output-cache.md) — domain binding, including domain name templates
- [Data Cache](../reference/data-cache.md) — `IDomainDataCache`, providers, and domain resolution
- [EF Core invalidation](../reference/ef-core-invalidation.md) — SaveChanges mapping and purge rules
- [Root README — Packages and applications](../../README.md#packages-and-applications) — NuGet catalog and Admin Console App
