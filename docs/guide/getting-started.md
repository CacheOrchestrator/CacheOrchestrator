# Getting started

> **Guide** — first endpoint, domain binding, and reading `X-CacheOrchestrator`.

This page takes you from an empty ASP.NET Core project to a working cached endpoint.

You will first cache a simple promotions response and see an Output Cache hit. Then you will add product reads, cache the product object as well as its HTTP response, and invalidate both when the product changes. Everything runs in memory, so you do not need Redis or any other service.

## Table of Contents

- [1. Create the application](#1-create-the-application)
- [2. Define the cache domains](#2-define-the-cache-domains)
- [3. Register CacheOrchestrator](#3-register-cacheorchestrator)
- [4. See the first cache hit](#4-see-the-first-cache-hit)
- [5. Add cached product reads](#5-add-cached-product-reads)
- [6. Update the product price and invalidate its caches](#6-update-the-product-price-and-invalidate-its-caches)
- [What to read next](#what-to-read-next)

## 1. Create the application

Create an empty ASP.NET Core application and move into its directory:

```bash
dotnet new web -n CacheDemo
cd CacheDemo
```

Install the `CacheOrchestrator` meta package:

```bash
dotnet add package CacheOrchestrator --prerelease
```

The meta package combines `CacheOrchestrator.AspNetCore` with `CacheOrchestrator.FusionCache`, which is the usual starting point for a web application. Both the Output Cache and Data Cache can run in memory, so this is all you need for the tutorial.

## 2. Define the cache domains

Replace `appsettings.json` with:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Cache": {
    "Namespace": "cache-demo",
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "InMemory" }
    },
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

A **domain** is a named set of cache rules. It does not own a cache store. Here, both domains use the same in-memory Output Cache provider, while `catalog` also configures the default in-memory Data Cache instance.

The three TTLs are independent:

- `DataCache:TtlSeconds` controls how long the application object can be reused.
- `OutputCache:TtlSeconds` controls how long ASP.NET Core can serve the complete HTTP response without running the endpoint.
- `ClientCache:TtlSeconds` becomes the client-facing `Cache-Control: max-age` value.

Configuration durations use integer seconds. `Version` is part of the cache identity; changing it starts a new generation of entries without changing endpoint code.

## 3. Register CacheOrchestrator

Replace `Program.cs` with this first version:

```csharp
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.OutputCache;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddCacheOrchestrator(builder.Configuration);

WebApplication app = builder.Build();

app.UseCacheOrchestrator();

app.MapGet("/api/promotions", () => new
{
    Title = "Summer sale",
    DiscountPercent = 20,
    GeneratedAtUtc = DateTimeOffset.UtcNow
})
.CacheOutputWithDomain("promotions");

app.Run();
```

There are three important lines:

1. `AddCacheOrchestrator` reads the `Cache` section, registers the configured providers, and adds the services used by endpoints.
2. `UseCacheOrchestrator` adds the middleware that coordinates the request with ASP.NET Core Output Caching and emits diagnostics.
3. `.CacheOutputWithDomain("promotions")` applies the domain to this endpoint. The endpoint itself does not need to know its TTL or provider.

Keep `UseCacheOrchestrator` before the mapped endpoints.

## 4. See the first cache hit

Start the application:

```bash
dotnet run
```

The command prints the local URL, for example `http://localhost:5000`. In another terminal, request the endpoint twice using that URL:

```bash
curl -i http://localhost:5000/api/promotions
curl -i http://localhost:5000/api/promotions
```

On the first request, the endpoint runs and ASP.NET Core stores the response. On the second request, Output Cache can return the stored response without running the endpoint. The body proves it too: `GeneratedAtUtc` stays the same.

Look at the `X-CacheOrchestrator` response header. The relevant part changes from:

```http
X-CacheOrchestrator: domain=promotions; ...; oc=miss; ...
```

to:

```http
X-CacheOrchestrator: domain=promotions; ...; oc=hit; ...
```

Use `curl` while learning the flow. A browser may satisfy the second request from its own cache because the domain also emits a public `Cache-Control` header, in which case the request never reaches your application.

At this point one domain coordinates two layers: Client Cache and Output Cache. Next, you will add the Data Cache layer and invalidate an individual entity across the server-side layers.

## 5. Add cached product reads

Replace `Program.cs` with the complete example below:

```csharp
using System.Collections.Concurrent;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddCacheOrchestrator(builder.Configuration);

WebApplication app = builder.Build();

app.UseCacheOrchestrator();

app.MapGet("/api/promotions", () => new
{
    Title = "Summer sale",
    DiscountPercent = 20,
    GeneratedAtUtc = DateTimeOffset.UtcNow
})
.CacheOutputWithDomain("promotions");

var products = new ConcurrentDictionary<int, Product>(
    new[]
    {
        new KeyValuePair<int, Product>(42, new(42, "Demo Widget", 10.00m)),
        new KeyValuePair<int, Product>(7, new(7, "Sample Gadget", 19.50m))
    });

app.MapGet("/api/products/{id:int}", async (
    HttpContext http,
    int id,
    IDomainDataCache cache,
    CancellationToken cancellationToken) =>
{
    Product? product = await cache.GetOrSetEntityAsync(http, async token =>
    {
        // Pretend this is a database or remote-service call.
        await Task.Delay(200, token);
        products.TryGetValue(id, out Product? value);
        return value;
    }, cancellationToken);

    return product is null ? Results.NotFound() : Results.Json(product);
})
.CacheOutputWithDomain("catalog", entityKind: "products", resourceRouteKey: "id");

app.MapPut("/api/products/{id:int}", async (
    int id,
    UpdateProduct request,
    ICacheOrchestratorInvalidator invalidator,
    CancellationToken cancellationToken) =>
{
    products[id] = new Product(id, request.Name, request.Price);

    await invalidator.InvalidateEntityAsync("catalog", "products", id, cancellationToken);

    return Results.NoContent();
});

app.Run();

public sealed record Product(int Id, string Name, decimal Price);
public sealed record UpdateProduct(string Name, decimal Price);
```

The dictionary stands in for a database so the example remains self-contained. In an application, the factory passed to `GetOrSetEntityAsync` would query your database or call another service.

The GET endpoint adds two ideas:

- `GetOrSetEntityAsync` caches the `Product` object in the `catalog` Data Cache. It runs the factory only on a Data Cache miss.
- `entityKind: "products"` and `resourceRouteKey: "id"` declare the entity identity once on the endpoint. CacheOrchestrator reads `id` from the route and uses the identity for both caching and targeted invalidation.

You do not pass `"catalog"` to `GetOrSetEntityAsync`. `.CacheOutputWithDomain(...)` places the resolved domain options and entity identity on the request before the handler runs, and `IDomainDataCache` reuses that request snapshot.

## 6. Update the product price and invalidate its caches

Restart the application and request product `42` twice:

```bash
curl -i http://localhost:5000/api/products/42
curl -i http://localhost:5000/api/products/42
```

The first request reaches the endpoint and the Data Cache factory. Its `X-CacheOrchestrator` header typically includes `oc=miss`, `dc=miss`, and `fa=run`. The second request is served as an Output Cache hit, so the endpoint and Data Cache are not consulted.

Now change the product price from `10.00` to `12.50`:

```bash
curl -i -X PUT http://localhost:5000/api/products/42 \
  -H "Content-Type: application/json" \
  -d '{"name":"Demo Widget","price":12.50}'
```

On PowerShell, use backticks for line continuation, or send the command on one line.

The PUT endpoint first saves the new value, then calls:

```csharp
await invalidator.InvalidateEntityAsync("catalog", "products", id, cancellationToken);
```

That one operation invalidates entries tagged for this product in both Output Cache and the Data Cache. It does not flush other products in the `catalog` domain.

Request product `42` again:

```bash
curl -i http://localhost:5000/api/products/42
```

The response now contains price `12.50`. Because the old HTTP response and cached object were both invalidated, the request misses both server-side layers, runs the factory, and caches the updated product again.

You have now used one domain definition to coordinate Client Cache, Output Cache, and Data Cache, then invalidated one logical entity across the server-side layers.

> [!NOTE]
> A browser that already cached a public GET response may keep it until its `max-age` expires; server invalidation cannot recall that copy. Choose the client TTL to match how quickly clients must observe unscheduled changes. For planned cutovers, see [Client Cache Schedule](client-cache-schedule.md). CDN copies can be invalidated through the optional [Edge cache integration](edge.md), which queues provider-specific tag purges; this quick start does not configure it.

## What to read next

| Goal | Page |
|------|------|
| Understand domains, versions, and the three layers | [Concepts](concepts.md) |
| Choose policies for snapshots and CRUD data | [Domain profiles](domain-profiles.md) |
| Bypass browser cache or refresh client views after a write | [Client cache busting and invalidation](client-invalidation.md) |
| Use a real database and automatic EF Core invalidation | [EF Core invalidation](../reference/ef-core-invalidation.md) |
| Move the cache stores to Redis | [Packages](packages.md) · [Composition](../how-to/composition.md) |
| Inspect every `X-CacheOrchestrator` field and metric | [Observability](../reference/observability.md) |
| Troubleshoot common mistakes | [FAQ](faq.md) |

The playground maps the same `/api/promotions` and generic `GET`/`PUT /api/products/{id}` flow in [GettingStartedEndpoints.cs](../../samples/CacheOrchestrator.Sample/Endpoints/GettingStartedEndpoints.cs), with shorter TTLs so cache expiry is quick to observe. It then adds Redis, scheduling, observability, and advanced endpoint examples; see [CacheOrchestrator.Sample](../../samples/CacheOrchestrator.Sample).
