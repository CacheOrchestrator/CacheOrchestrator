# Architecture

> **Contributor** — package layout, request flow, and internal boundaries.

How the library is put together.

A **domain** is a named group of data (`products`, `reports`, …) with its own TTLs, flags, and Version. Core resolves its HTTP-free identity and Data Cache policy as `DomainCacheOptions`. ASP.NET Core composes that snapshot into `DomainHttpCacheOptions` for Output Cache, Client Cache, authentication, vary, ETag, and HTTP key policy.

1. **ASP.NET Core Output Caching** — full HTTP responses; GET/HEAD + Url without identity bindings, other methods via endpoint [cache identity](../reference/cache-identity.md) (`CacheOrchestrator.AspNetCore`).
2. **Data Cache** — objects from your factory via `ICacheOrchestrator` / `IDomainDataCache` (Fusion or Hybrid as `IDataCacheProvider`; L1 memory, optional L2 / backplane for Fusion).
3. **Client Cache** — browser and CDN headers, including Client Cache Schedule.

## Table of Contents

- [Design principles](#design-principles)
- [High-level diagram](#high-level-diagram)
- [Source layout (`src/`)](#source-layout-src)
- [Public API surface](#public-api-surface)
- [Request flow — Output Cache](#request-flow--output-cache)
- [Request flow — Data Cache](#request-flow--data-cache)
- [Backends](#backends)

## Design principles

- **Configuration over code** — change TTLs and providers without redeploying handlers.
- **One domain model, package-owned snapshots** — HTTP policy wraps the Core snapshot instead of extending Core with ASP.NET concerns.
- **Pluggable engines** — Core owns portable `DataCache` policy; Fusion / Hybrid packages supply `IDataCacheProvider`.
- **Safe defaults** — fail-safe, stampede protection, and jitter come from Fusion (when that provider is registered) and the domain defaults.
- **Observable** — `X-CacheOrchestrator` (when enabled), meter and activity source `CacheOrchestrator`.

## High-level diagram

```
┌──────────────────────────── Application ────────────────────────────┐
│  Minimal APIs  ·  Controllers  ·  Workers / libraries                 │
└───────────────┬──────────────────────────────┬──────────────────────┘
                │                              │
                ▼                              ▼
     DomainOutputCachePolicy          IDomainDataCache (HTTP)
     (IOutputCachePolicy)             └─► ICacheOrchestrator (Core)
                │                              │
                ▼                              ▼
     ASP.NET Output Cache              IDataCacheProvider
                                       (FusionDataCacheProvider
                                        or HybridDataCacheProvider)
                │                              │
                └──────────┬───────────────────┘
                           ▼
              IDomainCacheOptionsProvider (Core)
                           │
                           ▼
                  DomainCacheOptions
                           │
                           ▼
              IRequestDomainCacheOptions (CacheOrchestrator.AspNetCore)
              (ICacheOrchestratorFeature + process cache)
                           │
                           ▼
                 DomainHttpCacheOptions
```

## Source layout (`src/`)

| Project | Responsibility |
|---------|----------------|
| `CacheOrchestrator.Core` | Domains, Version, portable `DataCache` settings, entity footprint, `ICacheOrchestrator`, invalidation and cluster **contracts**, diagnostics |
| `CacheOrchestrator.FusionCache` | ZiggyCreatures Fusion as `IDataCacheProvider`; JSON `FusionCache` knobs |
| `CacheOrchestrator.HybridCache` | Microsoft HybridCache as `IDataCacheProvider` |
| `CacheOrchestrator.AspNetCore` | Output Cache, Client Cache, HTTP vary/diagnostics/Admin settings, Admin API, `IDomainDataCache`, host `AddCacheOrchestratorAspNetCore` |
| `CacheOrchestrator` | Meta package: `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache` (`AddCacheOrchestrator`) |
| `CacheOrchestrator.Redis` | Redis Output Cache store + Fusion L2 + backplane |
| `CacheOrchestrator.HttpBus` | HTTP cluster command bus + transport, authentication, Static / ServiceDiscovery membership settings |
| `CacheOrchestrator.EFCore.Invalidation` | SaveChanges interceptor → entity invalidation — [ef-core-invalidation.md](../reference/ef-core-invalidation.md) |
| `CacheOrchestrator.AdminConsole` | Admin Console App (operator UI); not a NuGet package |

Dependency rule: arrows point at **Core**. Core never references ASP.NET, Fusion, Hybrid, Redis, HttpBus, or EF. Details: [packages.md](../guide/packages.md).

The user-facing JSON remains one `Cache` tree, but each package binds its own internal projection. Core's public snapshot does not expose Output Cache, Client Cache, HTTP Admin credentials, metrics labels, or HttpBus transport configuration.

## Public API surface

Prefer **interfaces and DI entry points**. Concrete services are `internal`.

| Public (stable contract) | Internal (not for app code) |
|--------------------------|-----------------------------|
| `AddCacheOrchestratorCore` / `ICacheOrchestrator` / `IDataCacheProvider` (**Core**) | Core host registration / orchestrator / provider boundary |
| `AddCacheOrchestrator` (**meta `CacheOrchestrator`**) / `AddCacheOrchestratorAspNetCore` / `UseCacheOrchestrator` / `ICacheOrchestratorBuilder` (**`CacheOrchestrator.AspNetCore`**) | Meta wires `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache`; `CacheOrchestrator.AspNetCore` owns host composition and `DefaultCacheOrchestratorBuilder` |
| `IDomainDataCache`, `IDomainKeyGenerator`, `DefaultDomainKeyGenerator` | `DomainDataCacheService` |
| `IDomainCacheOptionsProvider`, `DomainCacheOptions`, `DomainDataCacheSettings`, `DomainName` (**Core**) | `DomainCacheOptionsProvider`, `CacheOrchestratorOptionsValidator` |
| `IRequestDomainCacheOptions`, `DomainHttpCacheOptions`, HTTP domain setting types, `ICacheOrchestratorFeature` (**`CacheOrchestrator.AspNetCore`**) | `RequestDomainCacheOptionsProvider`, `CacheOrchestratorHttpOptions`, `CacheOrchestratorHttpOptionsValidator`, `CacheOrchestratorFeature` |
| `ICacheOrchestratorInvalidator`, `CacheInvalidationResult`, `ICacheInvalidationObserver`, `CacheTags` | `CacheOrchestratorInvalidator` |
| `IClusterCommandBus`, `IClusterMembership`, `IClusterCommandHandler`, `IInstanceIdProvider`, command records (`InvalidateCommand`, `VersionBumpCommand`, `SettingsPatchCommand`, …) | `DefaultClusterCommandHandler` |
| — | `NullClusterCommandBus`, `NullClusterMembership` |
| `IOutputCacheBackendRegistrar` | `InMemoryCacheBackendRegistrar` |
| Redis: `AddRedisBackend` (**CacheOrchestrator.Redis**) | `RedisCacheBackendRegistrar`, `RedisCacheHealthProbe`, all `Redis.Shared` implementation types |
| HttpBus: `AddHttpClusterBus` / `MapCacheOrchestratorHttpBus` (**CacheOrchestrator.HttpBus**) | `HttpClusterCommandBus`, versioned HTTP wire DTOs, `ClusterEndpointAuth` |
| `ICacheOrchestratorManagement`, management DTOs and host adapter contracts (**Core**) | `CacheOrchestratorManagement`, `CoreAdminDomainConfigProvider` |
| `MapCacheOrchestratorAdmin` (**`CacheOrchestrator.AspNetCore` Admin API**) | `AdminApi`, `HttpAdminDomainConfigProvider`, `InMemoryAdminStatsCollector` |
| `AuthBypassMode`, `ETagMode`, `ClientCacheability`, `DomainAuthEvaluator` (**`CacheOrchestrator.AspNetCore`**) | — |
| `ICacheVaryContributor`, `CacheVaryMaterializer`, `ICacheVaryBuilder` | — |
| `DomainOutputCachePolicy`, `[CacheDomain]`, `CacheOutputWithDomain` / `CacheOutputWithDomainTemplate` / `CacheOutputWithDomainAttribute` | `CacheDomainConvention` |
| Identity: `.WithCacheIdentity` / `.WithContentHashCacheIdentity`, `[CacheIdentity]` / `[ContentHashCacheIdentity]`, `ICacheIdentityContract`, `AddCacheIdentityContract<T>()`, `CacheIdentities.Url` | `CacheIdentityResolutionHostedService`, binding applicators |
| Health: `AddCacheOrchestrator()`, `ICacheOrchestratorHealthProbe` | `CacheOrchestratorHealthCheck` |
| Meter/activity **names** (`CacheOrchestrator`) | `CacheOrchestratorMetrics.Record*` |

Request state lives on **`ICacheOrchestratorFeature`** via `HttpContext.Features` (domain options, entity identity, disposition, pending footprint). Read the resolved snapshot with `http.GetDomainCacheOptions()`.

## Request flow — Output Cache

1. Request hits an endpoint with domain metadata (`.CacheOutputWithDomain(...)` or `[CacheDomain]`).  
2. `DomainOutputCachePolicy.CacheRequestAsync` runs.  
3. Domain is resolved (fixed string, func, or template).  
4. `EnsureDomainOptions` loads effective config (request L1 → process L2 → bind from options).  
5. Policy enables/disables lookup/storage, sets vary rules, tags `domain:{name}`, TTL.  
6. On hit → `ServeFromCacheAsync` marks disposition.  
7. On response start → client `Cache-Control` + `X-CacheOrchestrator` headers.

**Not cached:** methods without an identity binding when identity metadata is present (and non-GET/HEAD when identity metadata is absent), identity material null / content-hash oversize, `Cache-Control: no-store`, authenticated / `Authorization`, disabled domain, non-cacheable status codes, `Set-Cookie` responses. Opt-in non-GET via endpoint cache identity — [cache-identity.md](../reference/cache-identity.md).

## Request flow — Data Cache

1. Code calls `IDomainDataCache.GetOrSetAsync` or Core `ICacheOrchestrator.GetOrCreateAsync`.  
2. If domain still missing or Data Cache disabled → factory runs uncached.
3. Optional respect for request `no-store` / auth bypass.  
4. Key from `IDomainKeyGenerator` (HTTP) or caller-supplied key (orchestrator).  
5. Domain config selects a named **`DataCacheInstances`** entry (`default` by default).  
6. Registered `IDataCacheProvider` (Fusion: L1 → L2 → factory with soft/hard timeouts, fail-safe, jitter; Hybrid: expiration from `DataCache.TtlSeconds`).  
7. Disposition (`Hit` / `Miss` / `Stale` / …) stored for `X-CacheOrchestrator` (`dc=`).

See [Data Cache](../reference/data-cache.md) for HTTP resolution order and entity identity.

## Backends

| Provider | Package | Output Cache | Data Cache (Fusion path) |
|----------|---------|--------------|--------------------------|
| `InMemory` | Core / host | ASP.NET default store | L1 only |
| `Redis` | **`CacheOrchestrator.Redis`** | StackExchange Redis store | Keyed L2 + Redis backplane per instance |
| *(Custom)* | Your app | Custom | Keyed L2 recommended for multi-instance |

Register Redis with `AddRedisBackend()` (see [backends.md](../reference/backends.md)). Custom backends use `IOutputCacheBackendRegistrar` + `AddBackend`.

Output Cache and Data Cache providers can differ (for example, InMemory Output Cache with Fusion Redis).

**Multi-instance Redis:** each `DataCacheInstances` entry gets a keyed `IConnectionMultiplexer` and keyed `IDistributedCache` under the instance name. Do not share one global `IDistributedCache` across named caches — the last registration would win and mis-route L2 writes.

## Related

- [Packages](../guide/packages.md) — NuGet layout and ownership  
- [Guide — concepts](../guide/concepts.md) — product mental model  
- [cluster-bus.md](../reference/cluster-bus.md) — HttpBus and membership  
- [cache-keys.md](../reference/cache-keys.md) — key and tag composition  
- [configuration.md](../reference/configuration.md) — options binding and runtime snapshots  
- [Output Cache](../reference/output-cache.md) — HTTP policy layer  
- [Data Cache](../reference/data-cache.md) — Fusion / Hybrid orchestration  
- [vary.md](../reference/vary.md) — shared vary materializer  
- [deployment.md](../reference/deployment.md) — multi-instance topologies

### Provider materialization contract

`IDataCacheProvider.GetOrCreateWithTagsAsync` selects complete tags from each newly produced value before publishing that materialization. The selector is not a hit-time operation. Providers must apply this to background refresh as well as foreground misses; implementing it with a post-return `SetAsync` permits stale overwrites and loses background tags. Fusion uses its factory execution context. Hybrid publishes a unique tagged validity marker first and persists the marker key and tags with the payload, validating it on footprint hits. Ordinary provider lookups retain their direct path.

### Atomic runtime settings

`IDomainRuntimeOverrideStore` owns one immutable set of typed sections per domain. Its `Update` callback receives a private `DomainSettingsPatchContext`; it serializes writers to that domain and publishes one revision after the callback succeeds. Exceptions discard the working copy. Cache reads require only dictionary and volatile-reference reads. Clearing advances the revision to prevent an update/clear race from looking unchanged.

Package implementations of `IDomainSettingsPatchContributor` must implement `Prepare` and `Validate`. Prepare parses owned keys and stages an immutable merged section with `context.Set`; Validate runs after all packages have prepared and checks the effective combination, including unchanged fields and dependent Core settings. Both phases must avoid external side effects and nested store mutations. Consumers retrieve their immutable section with `GetSettings<T>`. Low-level `Update` callers own validation; the management/cluster applicator performs package validation for wire patches. Never mutate a returned section or retain a working context.

This replaces the beta contributor `Apply` contract: custom contributors and custom stores must adopt atomic preparation/publication before sharing the 3.0 runtime. The HTTP snapshot resolver detects a publication between its Core and HTTP reads and retries construction; an unstable domain is rejected after bounded retries rather than exposing a mixed revision.
