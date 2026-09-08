# Cache backends

> **Reference** — Output Cache / Fusion store registrars and Redis wiring.

Policy (domains, TTLs, invalidation, Client Cache) is separate from **storage**. InMemory ships with the host packages and Redis is supplied by focused integration packages. Custom storage has three distinct boundaries: an Output Cache store, FusionCache L2/backplane, or a complete Data Cache engine. Do not use one registrar as though it configured all three.

## Table of Contents

- [First-party backends](#first-party-backends)
- [Install](#install)
- [Register](#register)
- [Configure](#configure)
- [What a backend implements](#what-a-backend-implements)
- [Example: Fusion L2 on SQL Server](#example-fusion-l2-on-sql-server)
- [Builder API summary](#builder-api-summary)

## First-party backends

| Provider | Package | Registration | Output Cache | Data Cache L2 (Fusion) |
|----------|---------|--------------|--------------|------------------------|
| **InMemory** | `CacheOrchestrator.Core` / `CacheOrchestrator.AspNetCore` / `CacheOrchestrator` meta | Automatic | ASP.NET in-process store (+ size limits) | None (L1 only) |
| **Redis** (meta) | `CacheOrchestrator.Redis` | `o.AddRedisBackend()` | Redis Output Cache store | Keyed Redis L2 + backplane |
| **Redis** (Output Cache only) | `CacheOrchestrator.AspNetCore.Redis` | `o.AddRedisOutputCacheBackend()` | Redis Output Cache store | — |
| **Redis** (Fusion only) | `CacheOrchestrator.FusionCache.Redis` | `o.AddRedisFusionCacheBackend(...)` | — | Keyed Redis L2 + backplane |

`CacheOrchestrator.Redis.Shared` is a **support** package (transitive). Do not install it alone.

## Install

Typical web apps and labs (both surfaces):

```bash
dotnet add package CacheOrchestrator.Redis
```

Install only the storage surface you need:

```bash
dotnet add package CacheOrchestrator.AspNetCore.Redis
dotnet add package CacheOrchestrator.FusionCache.Redis
```

## Register

```csharp
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Redis;

builder.Services.AddCacheOrchestrator(builder.Configuration, o =>
{
    o.AddRedisBackend();
    o.ConfigureOutputCache(oc => oc.DefaultExpirationTimeSpan = TimeSpan.FromMinutes(5));
});
```

## Configure

Redis Output Cache uses a private connection registration, separate from named Data Cache connections. Every Data Cache instance name, including `oc`, remains valid. Health probes use `redis:output-cache` and `redis:data-cache:{instance}` so the two layers also remain distinguishable in diagnostics.

```json
{
"Cache": {
  "OutputCache": { "Provider": "InMemory" },
  "DataCacheInstances": {
    "default": { "Provider": "Redis" }
  },
  "Redis": { "Configuration": "localhost:6379" },
  "Distributed": {
    "SoftTimeoutSeconds": 1,
    "HardTimeoutSeconds": 2,
    "CircuitBreakerSeconds": 5
  }
}
}
```

`Cache:Redis` (and optional `…:OutputCache:Redis` / `…:DataCacheInstances:{name}:Redis`) is read by **`CacheOrchestrator.Redis`** (and the leaf Redis packages), not by core `CacheOrchestratorOptions`.  
Without `AddRedisBackend()`, `"Provider": "Redis"` fails validation.

## What a backend implements

| Responsibility | Package / interface | How |
|----------------|---------------------|-----|
| **Output Cache store** | `CacheOrchestrator.AspNetCore` `IOutputCacheBackendRegistrar` | Implement `RegisterOutputCache` |
| **Data Cache L2 (Fusion)** | FusionCache `IFusionCacheBackendRegistrar` | `RegisterFusionCache` — **keyed** `IDistributedCache` per instance name; register via `AddFusionCacheBackend` or Redis `AddRedisBackend` |
| **Complete Data Cache engine** | Core `IDataCacheProvider` | Implement get/create, set, named-instance isolation, generic values, and tag invalidation; register exactly one provider |
| **Health probes** | Both (optional) | Register `ICacheOrchestratorHealthProbe` from the registrar's main registration method. Redis does this for Output Cache (`oc`) and each Fusion instance. |

### Output Cache registration rules

1. Prefer `context.Configure(o => …)` for `OutputCacheOptions` (size limits, policies).  
   Do **not** call `services.AddOutputCache` yourself — the host does that once.  
2. Prefer `context.RegisterStore(() => …)` for store packages that must run **after** `AddOutputCache`  
   (e.g. `AddStackExchangeRedisOutputCache`).  
3. Implement this interface only when the provider has an ASP.NET Output Cache adapter. A Fusion-only provider implements only `IFusionCacheBackendRegistrar`.
4. Use `context.OutputCacheNamespace` for store key isolation; `CacheOrchestrator.AspNetCore` owns and resolves Output Cache configuration.

### Fusion L2 rules (multi-instance safe)

1. Register a **keyed** `IDistributedCache` with key = `context.InstanceName`.  
2. Call `context.FusionBuilder.WithRegisteredKeyedDistributedCache(context.InstanceName)`.  
3. Register a **keyed** `IDistributedCache` per Data Cache instance name. A single global `AddDistributedSqlServerCache` or `AddStackExchangeRedisCache` would let the last instance overwrite the others.
4. Optional backplane: attach on `context.FusionBuilder` (`CacheOrchestrator.FusionCache.Redis` / Redis meta does this).  
5. Bind settings from `context.BackendSection`  
   (`Cache:DataCacheInstances:{instance}:{Provider}`).

### Config path convention

| Surface | Path |
|---------|------|
| Output backend | `{section}:OutputCache:{Provider}` e.g. `Cache:OutputCache:SqlServer` |
| Data Cache backend | `{section}:DataCacheInstances:{instance}:{Provider}` e.g. `Cache:DataCacheInstances:default:SqlServer` |

Use `context.BackendSection` on the registration context. The Fusion path always reads **`DataCacheInstances`**.

### Distributed resilience (L2)

| Setting | Config | Applied when |
|---------|--------|----------------|
| Soft / hard timeout, circuit breaker | `Cache:Distributed:*` (core) | Data Cache provider ≠ `InMemory` |

---

## Example: Fusion L2 on SQL Server

ASP.NET Core does not ship an Output Cache store for SQL Server. This example therefore keeps Output Cache on `InMemory` (or Redis) and uses SQL Server only as Fusion L2. The same shape works for Memcached, Cosmos, or another `IDistributedCache`.

```csharp
using CacheOrchestrator.FusionCache.Backends;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.SqlServer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZiggyCreatures.Caching.Fusion;

public sealed class SqlServerFusionBackendRegistrar : IFusionCacheBackendRegistrar
{
    public string Name => "SqlServer";

    public void RegisterFusionCache(FusionCacheRegistrationContext context)
    {
        string? connectionString = context.BackendSection["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"DataCacheInstances['{context.InstanceName}']: SqlServer:ConnectionString is required.");

        string table = context.BackendSection["TableName"] ?? $"FusionCache_{context.InstanceName}";

        // One SqlServer cache instance per Data Cache instance name (keyed).
        context.Services.TryAddKeyedSingleton<IDistributedCache>(context.InstanceName, (_, _) =>
        {
            var options = Options.Create(new SqlServerCacheOptions
            {
                ConnectionString = connectionString,
                SchemaName = context.BackendSection["SchemaName"] ?? "dbo",
                TableName = table
            });
            return new SqlServerCache(options);
        });

        context.FusionBuilder.WithRegisteredKeyedDistributedCache(context.InstanceName);
        // No Redis-style backplane unless you add one yourself.
        // Optional: register ICacheOrchestratorHealthProbe through context.Services.
    }
}
```

Startup:

```csharp
builder.Services.AddCacheOrchestratorAspNetCore(builder.Configuration);
builder.Services.AddFusionCacheBackend(new SqlServerFusionBackendRegistrar());
builder.Services.AddCacheOrchestratorFusionCache(builder.Configuration);
```

Config:

```json
{
"Cache": {
  "OutputCache": { "Provider": "InMemory" },
  "DataCacheInstances": {
    "default": {
      "Provider": "SqlServer",
      "SqlServer": {
        "ConnectionString": "Server=.;Database=Cache;Trusted_Connection=True;",
        "SchemaName": "dbo",
        "TableName": "FusionCache"
      }
    }
  },
  "Distributed": {
    "SoftTimeoutSeconds": 2,
    "HardTimeoutSeconds": 5,
    "CircuitBreakerSeconds": 10
  }
}
}
```

---

## Builder API summary

| API | Purpose |
|-----|---------|
| `AddOutputCacheBackend(IOutputCacheBackendRegistrar)` | Register / replace a provider by `Name` |
| `AddRedisBackend()` | From `CacheOrchestrator.Redis` |
| `ConfigureOutputCache(Action<OutputCacheOptions>)` | App-level Output Cache options (after backend defaults) |

`AddOutputCacheBackend` is specifically an Output Cache builder API. Fusion registrars use `AddFusionCacheBackend`; complete Data Cache engines are DI providers. See [Extensibility](extensibility.md#data-cache-engine-idatacacheprovider) for the full contracts and registration rules.

## Related

- [Packages](../guide/packages.md) — which package to choose (including Redis meta vs leaf)
- [Topologies](../guide/topologies.md) — InMemory, Redis, HttpBus, and when to combine them
- [Configuration](configuration.md) — `Distributed`, Redis connection options, and provider settings
- [Deployment](deployment.md) — multi-instance Redis and shared-store layouts
- [Extensibility](extensibility.md) — `IOutputCacheBackendRegistrar`, `IFusionCacheBackendRegistrar`, and `IDataCacheProvider`
- [Architecture](../contributor/architecture.md) — package layout and public API surface
