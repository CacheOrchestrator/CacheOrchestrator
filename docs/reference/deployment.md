# Deployment

> **Reference** — single- and multi-instance topologies, Redis, and shared configuration.

How to run CacheOrchestrator on one process or on several: in-memory stores, Redis, the backplane, and the cluster bus.

**`CacheOrchestrator.Redis`:** any topology below that uses `"Provider": "Redis"` requires:

```bash
dotnet add package CacheOrchestrator.Redis --prerelease
```

```csharp
services.AddCacheOrchestrator(configuration, o => o.AddRedisBackend());
```

**Cluster bus package** (optional): multi-instance **InMemory** invalidation / runtime Version-TTL without Redis backplane — see [cluster-bus.md](cluster-bus.md).

```bash
dotnet add package CacheOrchestrator.HttpBus --prerelease
```

```csharp
services.AddCacheOrchestrator(configuration, o => o.AddHttpClusterBus());
// …
app.MapCacheOrchestratorHttpBus();
```

---

## Table of Contents

- [Single instance (in-memory only)](#single-instance-in-memory-only)
- [Multiple instances with Redis](#multiple-instances-with-redis)
- [Multiple instances without Redis (InMemory Data Cache, no backplane)](#multiple-instances-without-redis-inmemory-data-cache-no-backplane)
- [Mixed backends (Output Cache InMemory + Data Cache Redis)](#mixed-backends-output-cache-inmemory-data-cache-redis)
- [Using multiple Data Cache instances](#using-multiple-data-cache-instances)
- [Shared configuration across instances](#shared-configuration-across-instances)
- [Security checklist](#security-checklist)

## Single instance (in-memory only)

The simplest topology. One process, no Redis, no cross-process coordination.

```json
{
  "Cache": {
    "Namespace": "my-app",
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "InMemory" }
    }
  }
}
```

**Limitations:**
- Output Cache and Data Cache entries are **not shared** between process restarts or replicas.
- No invalidation signal reaches other instances -- `InvalidateDomainAsync` only clears the calling process.

---

## Multiple instances with Redis

Redis is the distributed backend that ships with the library (`CacheOrchestrator.Redis`). For another store, use the contract for the required surface: `IOutputCacheBackendRegistrar` for Output Cache, `IFusionCacheBackendRegistrar` for FusionCache L2/backplane, or `IDataCacheProvider` for a complete Data Cache engine. [Backends](backends.md) includes a Fusion L2 example for SQL Server; [Extensibility](extensibility.md) defines every boundary.

Multiple replicas share both Output Cache data and Fusion Data Cache entries through Redis.
Fusion also receives **backplane** invalidation signals so L1 (in-memory) is cleared on all nodes
when any node invalidates a tag.

```json
{
  "Cache": {
    "Namespace": "my-app",
    "OutputCache": {
      "Provider": "Redis",
      "Redis": { "Configuration": "redis-primary:6379" }
    },
    "DataCacheInstances": {
      "default": {
        "Provider": "Redis",
        "Redis": { "Configuration": "redis-primary:6379" }
      }
    }
  }
}
```

```
Instance A (ASP.NET)          Instance B (ASP.NET)
  L1 (memory)                   L1 (memory)
     |                              |
     +--------- Redis L2 -----------+
     |                              |
     +------- Redis Backplane ------+
              (pub/sub channel)

Output Cache:
  Instance A writes -> Redis -> Instance B reads (shared store)

Data Cache (Fusion) invalidation:
  Instance A calls InvalidateDomainAsync
    -> removes tag from Redis L2
    -> publishes backplane message
    -> Instance B receives signal, clears L1 entries for that tag
```

**What the backplane does:** Without a backplane, `InvalidateDomainAsync` would clear Redis L2 but
Instance B's L1 memory would still hold stale data until its TTL expired. The Redis backplane
(pub/sub) delivers the invalidation signal so L1 is cleared immediately on all nodes.

**Backplane channel:** `{DataCacheNamespace}:backplane` (e.g. `my-app-dc:backplane`).
Effective Data Cache namespace is `Cache:DataCacheInstances:{name}:Namespace` if set, else `{Cache:Namespace}-dc` for instance `default`, else `{Cache:Namespace}-dc-{instanceName}`. Multiple apps on the same Redis cluster stay isolated when those prefixes differ.

Runtime Version / TTL / **settings** overlays are **not** carried by the Fusion backplane. Use the [cluster bus](cluster-bus.md) (`CacheOrchestrator.HttpBus`) or Admin Console App fan-out for those.

---

## Multiple instances without Redis (InMemory Data Cache, no backplane)

Possible when Redis is not available, at the cost of stale L1 data across instances.

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "InMemory" }
    }
  }
}
```

| Scenario | Behaviour |
|----------|-----------|
| Request hits Instance A | A serves from its own L1 |
| Request hits Instance B | B serves from its own L1 (may be stale) |
| `InvalidateDomainAsync` on A | Only A's cache cleared; B unaffected **unless** [cluster bus](cluster-bus.md) is enabled |
| Output Cache | Each instance stores its own copy; no cross-sharing **unless** peers receive bus commands |

**Acceptable when:** Load balancer uses sticky sessions, or domains are truly static
(TTL-based expiry is the only invalidation strategy).

### Immediate purge without Redis: cluster bus

Install **`CacheOrchestrator.HttpBus`**, enable `Cache:Cluster:Bus`, use **Static** or **ServiceDiscovery** membership, map `MapCacheOrchestratorHttpBus()` on every instance. Then:

- Programmatic `Invalidate*` on any node → peers ApplyLocal  
- Admin `distribute: true` → Version/TTL overlays cluster-wide  

Full setup and Bus vs Redis matrix: **[cluster-bus.md](cluster-bus.md)**.

### Ops dashboard across instances

1. Enable the **Admin API** on each app (`Cache:Admin:Enabled`, `MapCacheOrchestratorAdmin`).  
2. Run **Admin Console App** (`src/CacheOrchestrator.AdminConsole`) with `AdminConsole:Instances` pointing at every base URL.  
3. With Bus enabled, Operations auto-picks **bus-distribute** vs **fan-out** — [admin.md](admin.md).

---

## Mixed backends (Output Cache InMemory + Data Cache Redis)

A common hybrid: Output Cache stays in-process for maximum response speed,
while the Fusion Data Cache provider uses Redis so object data is shared and invalidation propagates.

```json
{
  "Cache": {
    "Namespace": "my-app",
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": {
        "Provider": "Redis",
        "Redis": { "Configuration": "redis-primary:6379" }
      }
    }
  }
}
```

| Layer | Storage | Cross-instance? |
|-------|---------|-----------------|
| Output Cache (HTTP responses) | Per-process memory | No |
| Data Cache L1 (Fusion) | Per-process memory | No (but invalidated via backplane) |
| Data Cache L2 (Fusion) | Redis | Yes |

Output Cache will eventually diverge between instances (until TTL expires, or that instance has not served the endpoint since the change).
Fusion object data stays consistent because Redis is the shared source of truth and the backplane keeps L1 in sync.

---

## Using multiple Data Cache instances

By default, CacheOrchestrator uses a single `default` entry in `DataCacheInstances` for all domains. This provides isolation via keys and tags, which is sufficient for most applications.

However, you might need separate named instances for:
- Regulatory isolation (GDPR: user PII must not touch product cache Redis)
- Scale isolation (high-write domain should not evict low-write domains)
- Geographic isolation (domain A served from EU Redis, domain B from US Redis)

Configure multiple named instances in `DataCacheInstances` and map domains via `DataCache.Instance`:

```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": {
        "Provider": "Redis",
        "Redis": { "Configuration": "global-redis:6379" }
      },
      "pii": {
        "Provider": "Redis",
        "Redis": { "Configuration": "secure-redis:6379" }
      }
    },
    "Domains": {
      "products": { "DataCache": { "Instance": "default" } },
      "user-profiles": { "DataCache": { "Instance": "pii" } }
    }
  }
}
```

Each named instance maintains:

| Component | Isolation |
|-----------|-----------|
| `IConnectionMultiplexer` | Keyed singleton per instance name |
| `IDistributedCache` (L2) | **Keyed** singleton per instance name (not a single global registration) |
| Redis backplane | Same multiplexer + channel prefix `{DataCacheNamespace}:backplane` |

This means two domains can safely map to **different Redis clusters** (e.g. GDPR isolation) without the last-registered L2 overwriting the first.

---

## Shared configuration across instances

Cache settings that affect **shared** cache behaviour must be the **same** on every app instance that uses the same Output Cache store and/or Fusion L2/backplane. That includes at least:

- `Cache:Namespace` and explicit Output Cache, Data Cache instance and Edge namespaces  
- `Domains:*:Version` (generation stamp / key space)  
- Domain TTLs, Client Cache Schedule, `DataCache.Instance` mapping into `DataCacheInstances`  
- Redis connection targets (when using Redis)

Hand-editing a different `appsettings.json` on each machine causes **desynchronization** (different Version → different keys; different TTLs → inconsistent behaviour). This is a general multi-instance configuration problem, not specific to CacheOrchestrator.

### Prefer one source of truth

| Approach | Role |
|----------|------|
| **Same deploy artifact** | Ship one production config (or env) with every instance of a release |
| **Environment variables** | e.g. `Cache__Domains__catalog__Version=2026-09` set identically by the orchestrator |
| **Central config** | Azure App Configuration, Consul, Kubernetes ConfigMap, etc. |
| **Dedicated cache file** | e.g. `appsettings.cache.json` kept identical on every instance by cluster config management |

Do **not** rely on operators SSH-editing per-server JSON for domain `Version` cutovers.

### Do you need to re-register default JSON files?

**No.** With `WebApplication.CreateBuilder(args)` (default host), ASP.NET Core **already** loads:

- `appsettings.json`  
- `appsettings.{Environment}.json`  
- environment variables, command-line args, and user secrets (Development)

You only **add** extra sources. Example — shared cache file with reload:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Defaults (appsettings.json + environment-specific + env vars) are already registered.
// Add a file that cluster config management keeps identical on every instance:
builder.Configuration.AddJsonFile(
    "appsettings.cache.json",
    optional: true,
    reloadOnChange: true);

builder.Services.AddCacheOrchestrator(builder.Configuration, o => o.AddRedisBackend());
```

`reloadOnChange: true` applies when the **local file on that machine** changes. CacheOrchestrator uses `IOptionsMonitor` and **clears** its in-process domain options cache so the next request rebuilds snapshots (new `Version`, TTLs, …).

If your platform **restarts** processes on config change, reload is optional; the important part remains: **same content on all instances**.

Later configuration sources override earlier ones. Environment variables (already added by `CreateBuilder`) can override JSON, e.g. `Cache__Domains__catalog__Version`.

### Example: `appsettings.cache.json` (shared)

Keep host-specific settings in `appsettings.json` / environment; put **cluster-wide cache policy** in a file (or ConfigMap) available to every instance:

```json
{
  "Cache": {
    "Namespace": "my-app",
    "OutputCache": {
      "Provider": "InMemory"
    },
    "DataCacheInstances": {
      "default": { "Provider": "Redis" }
    },
    "Redis": {
      "Configuration": "redis-primary:6379"
    },
    "DomainDefaults": {
      "ClientCache": {"Cacheability": "Public", "TtlMinSeconds": 60 }
    },
    "Domains": {
      "catalog": {
        "Version": "2026-08",
        "DataCache": { "TtlSeconds": 3800 },
        "OutputCache": { "TtlSeconds": 3700 },
        "ClientCache": { "TtlSeconds": 3600, "ScheduledUpdateUtc": "2026-09-01T00:00:00Z" }
      },
      "live-tracking": {
        "Version": "1",
        "DataCache": { "TtlSeconds": 10 },
        "OutputCache": { "TtlSeconds": 5 },
        "ClientCache": { "TtlSeconds": 5 }
      }
    }
  }
}
```

**Cutover example:** change `"Version": "2026-08"` → `"2026-09"` **once** in the shared file / config system and roll it out to all instances (or let reload pick it up everywhere). Do not leave instance A on `2026-09` and B on `2026-08` for long.

During a **rolling deploy**, a short mixed window is normal (some nodes already on the new Version). That is preferable to permanent per-machine drift.

### What shared config does *not* replace

| Concern | How it is handled |
|---------|-------------------|
| Shared HTTP / object data across nodes | Redis (or other L2) — topologies above |
| L1 memory after invalidation | Redis **backplane** (Fusion) |
| Browser caches near a data cutover | [Client Cache Schedule](../guide/client-cache-schedule.md) |
| Cloudflare or Varnish edge objects after entity/domain invalidation | [Edge cache integration](../guide/edge.md); one origin process queues the external invalidation |
| One product row changed under same Version | [Entity invalidation](invalidation.md) / [domain profiles](../guide/domain-profiles.md) |

## Security checklist

> [!IMPORTANT]
> Multi-instance layouts share stores and (optionally) command paths. Treat configuration and network as part of the threat model:
>
> - [ ] Keep the same `Cache:Namespace`, domain `Version` / TTLs, and Redis targets on every instance that shares a store ([shared configuration](#shared-configuration-across-instances))
> - [ ] Do not expose Admin API or cluster receive routes on the public internet — see [admin.md](admin.md#security-checklist) and [cluster-bus.md](cluster-bus.md#security-checklist)
> - [ ] Put Redis (Output Cache / Fusion L2 / backplane) on a private network; use auth and TLS as your platform requires
> - [ ] Use distinct namespaces (or Redis instances) when apps or environments must not share keyspace
> - [ ] Prefer secret stores for Redis connection strings and Admin/Bus API keys — never commit production secrets
> - [ ] Keep edge-provider credentials in secret configuration, protect Varnish PURGE with an internal ACL/authentication, and allow graceful shutdown time for the in-memory invalidation queue

## Related

- [Guide — topologies](../guide/topologies.md) — which layout to pick  
- [configuration.md](configuration.md) — namespaces, providers, full schema  
- [backends.md](backends.md) — `CacheOrchestrator.Redis` and custom registrars  
- [Data Cache](data-cache.md) — providers, instances, and L2 behaviour  
- [invalidation.md](invalidation.md) — purge strategies across instances  
- [observability.md](observability.md) — metrics, `X-CacheOrchestrator`, health
- [cluster-bus.md](cluster-bus.md) — HTTP fan-out when Redis backplane is not enough  
- [faq.md](../guide/faq.md) — multi-instance and InMemory limitations  
- [comparison.md](../guide/comparison.md) — when Redis Output Cache alone is enough  
- [domain-profiles.md](../guide/domain-profiles.md) — Version vs TTL cutovers  
