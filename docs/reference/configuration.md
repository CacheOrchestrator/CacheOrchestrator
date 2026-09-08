# Configuration reference

> **Reference** — `Cache` appsettings schema, defaults, inheritance, and validation.

Schema for the `Cache` configuration section (or another root you pass to `AddCacheOrchestrator`).

- Section name defaults to **`Cache`**. Override with a named argument, e.g. `services.AddCacheOrchestrator(configuration, configSection: "MySection")` (same for `AddCacheOrchestratorAspNetCore`).
- Domain lifetimes use nested objects (`DataCache`, `OutputCache`, `ClientCache`, Fusion-only `FusionCache`) with **integer seconds** (`TtlSeconds`, …) — not TimeSpan strings.
- Runtime snapshots often expose `TimeSpan` for server TTLs; client max-age fields stay `int` seconds.

For “which package do I need?”, start with [packages](../guide/packages.md), not this page.

## Table of Contents

- [Root shape](#root-shape)
- [Root properties and package ownership](#root-properties-and-package-ownership)
- [Provider options (`OutputCache` / `DataCacheInstances` entry)](#provider-options-outputcache-datacacheinstances-entry)
- [Redis connection (`CacheOrchestrator.Redis` package)](#redis-connection-cacheorchestratorredis-package)
- [Edge cache (`CacheOrchestrator.Edge` package)](#edge-cache-cacheorchestrator-edge-package)
- [Distributed resilience (`Cache:Distributed`)](#distributed-resilience-cachedistributed)
- [Domain settings (`DomainDefaults` and each `Domains` entry)](#domain-settings-domaindefaults-and-each-domains-entry)
- [Admin API (`Cache:Admin`)](#admin-api-cacheadmin)
- [Cluster bus (`Cache:Cluster:Bus`)](#cluster-bus-cacheclusterbus)
- [EF Core invalidation (`Cache:EFCore:Invalidation`)](#ef-core-invalidation-cacheefcoreinvalidation)
- [Validation](#validation)
- [Runtime model](#runtime-model)
- [Domain name normalization](#domain-name-normalization)
- [Example domains](#example-domains)

## Root shape

```json
{
  "Cache": {
    "Namespace": "app-cache",
    "InstanceId": "",
    "Distributed": { },
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "InMemory" }
    },
    "EdgeInstances": { },
    "EdgeQueue": { },
    "DomainDefaults": {
      "DataCache": { "Enabled": true, "TtlSeconds": 3800 },
      "OutputCache": { "Enabled": true, "TtlSeconds": 3700 },
      "ClientCache": { "Cacheability": "Public", "TtlSeconds": 3600, "TtlMinSeconds": 60 },
      "FusionCache": { "HardTtlSeconds": 43200, "FailSafeSeconds": 86400 }
    },
    "Domains": {
      "products": { }
    },
    "Admin": { },
    "Cluster": { "Bus": { } }
  }
}
```

## Root properties and package ownership

| Property | Owner | Type | Default | Description |
|----------|-------|------|---------|-------------|
| `Namespace` | `CacheOrchestrator.Core` + consuming adapters | string | `app-cache` | Global key prefix; isolates multi-app shared stores **and** cluster command isolation |
| `InstanceId` | `CacheOrchestrator.Core` | string | machine name | Stable process id (management, cluster anti-echo, diagnostics) |
| `EmitDiagnosticsHeaders` | `CacheOrchestrator.AspNetCore` | bool | `true` | When `true`, emit client-visible diagnostic headers (`X-CacheOrchestrator`). Set `false` in production if you do not want hit/miss/domain details exposed to clients. Does **not** affect metrics, tracing, or logs. |
| `Metrics` | `CacheOrchestrator.AspNetCore` | object | see below | HTTP meter label options (OpenTelemetry / Prometheus) |
| `Distributed` | `CacheOrchestrator.Core` / Data Cache provider | object | soft 1s / hard 2s / circuit 5s | L2 resilience for **non-InMemory** Data Cache providers (Fusion Redis, …) |
| `OutputCache` | `CacheOrchestrator.AspNetCore` | object | Provider `InMemory` | Output Cache provider + optional namespace |
| `DataCacheInstances` | `CacheOrchestrator.Core` / Data Cache provider | map | `default` instance `InMemory` | Named Data Cache engines (Fusion L1±L2; Hybrid supports only `default`) |
| `EdgeInstances` | `CacheOrchestrator.Edge` + provider packages | map | empty | Named tag-native edge providers |
| `EdgeQueue` | `CacheOrchestrator.Edge` | object | see below | In-memory purge batching and retry policy |
| `DomainDefaults` | `CacheOrchestrator.Core` + feature packages | object | — | Fallbacks for every domain; each package binds its owned nested settings |
| `Domains` | `CacheOrchestrator.Core` + feature packages | map | — | Per-domain overrides (keys are domain names) |
| `Admin` | `CacheOrchestrator.Core` + `CacheOrchestrator.AspNetCore` / `CacheOrchestrator.HttpBus` | object | disabled | Management policy plus Admin API route/auth settings (see [admin.md](admin.md)) |
| `Cluster` | `CacheOrchestrator.Core` command handling + `CacheOrchestrator.HttpBus` transport | object | bus disabled | Cluster command and optional HttpBus settings (see below / [cluster-bus.md](cluster-bus.md)) |

The JSON tree is stable even though no single public Core options type owns every row. Feature and provider packages bind package-specific projections from the same section.

### Metrics (`CacheOrchestrator.AspNetCore`)

Bound from `Cache:Metrics`. Controls labels on the meter named `CacheOrchestrator` (not Admin Console App storage).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `IncludeEndpointLabel` | bool | `true` | When `true`, Output Cache and Data Cache instruments include a stable `route` tag (`METHOD` + route template, the same shape as Admin endpoint keys). Set `false` to emit only `domain` / `result` and lower Prometheus cardinality. Keep the same value on all cluster nodes. |

Endpoint time series need a scrape of the meter and Admin Console App Metrics store; empty charts mean no samples in range (traffic, flag off for part of the window, or label mismatch)—not a separate “feature bit” from Prometheus history.

**Redis connection settings are not part of core options.** They are owned by **CacheOrchestrator.Redis** (see below).

Effective namespaces:

- Output: `OutputCache.Namespace` ?? `{Namespace}-oc`
- Data Cache **`default`** instance: `DataCacheInstances.default.Namespace` ?? `{Namespace}-dc`
  (**no** `-default` suffix — keys look like `app-cache-dc:…`, not `app-cache-dc-default:…`.)
- Data Cache **named** instance (e.g. `pii`): `…Namespace` ?? `{Namespace}-dc-{name}`

## Provider options (`OutputCache` / `DataCacheInstances` entry)

| Property | Description |
|----------|-------------|
| `Provider` | Must match a registered backend (`InMemory` always; `Redis` after `AddRedisBackend()`; custom via `AddOutputCacheBackend` or `AddFusionCacheBackend`) |
| `Namespace` | Optional key prefix override |
| `{Backend}.*` | Backend-specific block (read by the backend package, e.g. `Redis`, `SqlServer`) |

## Edge cache (`CacheOrchestrator.Edge` package)

Edge support is opt-in and requires a tag-native provider package. Register the providers referenced by configuration:

```csharp
services.AddCacheOrchestratorEdge(configuration, edge =>
{
    edge.AddCloudflare();
    edge.AddVarnish();
});
```

| Path | Type | Default | Description |
|------|------|---------|-------------|
| `EdgeInstances:{name}:Provider` | string | — | Registered provider name; v1 requires tag purge |
| `EdgeInstances:{name}:Namespace` | string? | `{Cache:Namespace}-edge-{name}` | Isolation input for opaque edge tags |
| `EdgeInstances:{name}:Cloudflare:ZoneId` | string | — | Cloudflare zone; required by the Cloudflare provider |
| `EdgeInstances:{name}:Cloudflare:ApiToken` | string | — | Secret token with cache purge permission |
| `EdgeInstances:{name}:Varnish:PurgeUrl` | absolute URL | — | Protected Varnish xkey invalidation endpoint |
| `EdgeInstances:{name}:Varnish:ApiKey` | string? | null | Optional secret sent to the invalidation endpoint |
| `EdgeInstances:{name}:Varnish:ApiKeyHeaderName` | string | `X-CacheOrchestrator-Key` | Header carrying the optional API key |
| `EdgeQueue:Capacity` | int | `1024` | Pending in-memory jobs before producers apply backpressure |
| `EdgeQueue:MaxAttempts` | int | `3` | Total attempts for a transient provider failure |
| `EdgeQueue:FlushIntervalSeconds` | int | `1` | Coalescing window |
| `EdgeQueue:RetryBaseDelaySeconds` | int | `1` | Exponential retry base when no `Retry-After` is supplied |

`DomainDefaults:Edge` and `Domains:{name}:Edge` use the normal default/override merge:

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `false` | Emit provider metadata and coordinate purge for this domain |
| `PurgeOnStartup` | `false` | Enqueue a domain-tag purge on host startup when Edge is enabled |
| `Instance` | empty | Named `EdgeInstances` entry; required when enabled |
| `TtlSeconds` | `300` | Edge fresh lifetime and Edge maximum during an active Client Cache Schedule |
| `StaleWhileRevalidateSeconds` | null | Optional edge stale/revalidation window |
| `StaleIfErrorSeconds` | null | Optional edge stale/error window |

All durations are non-negative integer seconds. Queue capacity and attempts must be positive. Startup fails for an unknown provider, missing instance reference, missing provider settings, or a stale setting the selected provider cannot preserve. Cloudflare supports both stale settings; Varnish maps stale-while-revalidate to grace and rejects stale-if-error. See [Edge cache integration](../guide/edge.md) for response eligibility, VCL, limits, and delivery behavior.

## Redis connection (`CacheOrchestrator.Redis` package)

Read **only** after a Redis backend is registered. The binding implementation lives in the transitive `CacheOrchestrator.Redis.Shared` support package and is intentionally not public API.

Configure Redis **primarily through the StackExchange.Redis connection string** (`Configuration`): host, password, SSL, `defaultDatabase`, and most wire options belong there. The integer fields below are a thin overlay for common timeouts and keep-alive; they do not replace the connection string. This section is **connection / store wiring** — not Fusion L2 call resilience (that is [`Cache:Distributed`](#distributed-resilience-cachedistributed)).

| Section | Role |
|---------|------|
| `Cache:Redis` | Global fallback connection |
| `Cache:OutputCache:Redis` | Override for Output Cache store |
| `Cache:DataCacheInstances:{name}:Redis` | Override for one Data Cache instance |

| Property | Default | Description |
|----------|---------|-------------|
| `Configuration` | — | StackExchange.Redis connection string (primary Redis configuration surface) |
| `ConnectTimeout` | 5000 | ms |
| `SyncTimeout` | 5000 | ms |
| `KeepAliveSeconds` | 60 | TCP keep-alive |

## Distributed resilience (`Cache:Distributed`)

Core setting. Applied when a Data Cache instance `Provider` is **not** `InMemory` (Fusion L2 path).

These knobs are **application-side L2 resilience** (how long Fusion waits on distributed cache I/O and how long the circuit stays open after failures). They are **not** Redis connection settings — put host, auth, SSL, and StackExchange timeouts in the [Redis connection string](#redis-connection-cacheorchestratorredis-package) (and the optional overlay fields there). Factory timeouts stay under per-domain `FusionCache.*`.

| Property | Default | Description |
|----------|---------|-------------|
| `SoftTimeoutSeconds` | 1 | Distributed soft timeout |
| `HardTimeoutSeconds` | 2 | Distributed hard timeout |
| `CircuitBreakerSeconds` | 5 | Distributed circuit breaker |

## Domain settings (`DomainDefaults` and each `Domains` entry)

Nullable fields **inherit** from defaults (then hard-coded library defaults). Nested sections merge the same way.

### Resolution precedence

For one domain, the Core and ASP.NET Core options providers resolve values in this order, from highest to lowest priority:

1. Runtime overlay created through the Admin API, for settings that support overlays.
2. `Cache:Domains:{name}`.
3. `Cache:DomainDefaults`.
4. Library defaults.

`CacheOrchestrator.Core` produces an immutable `DomainCacheOptions` snapshot for domain identity and Data Cache policy. `CacheOrchestrator.AspNetCore` composes it into `DomainHttpCacheOptions` for Output Cache, Client Cache, authentication, vary, ETag, and HTTP Data Cache key policy. A request reuses its HTTP snapshot; configuration reloads and later overlays affect newly resolved requests, not values already attached to the current request. Core, HTTP and Fusion replace their snapshot-cache generation on reload. Resolutions already in flight may finish against the previous generation, but cannot repopulate the current generation with stale settings. Provider and connection sections such as `OutputCache`, `DataCacheInstances`, and `Redis` are host composition settings and do not participate in this per-domain merge.

### Domain-setting change activation and invalidation

A setting change is a policy change, not a declaration that cached content changed. Unless the table below says otherwise, existing entries retain the policy with which they were created and expire naturally; newly stored entries use the new policy. This makes a configured TTL an actual upper bound on how long an entry may keep its creation policy.

There are three exceptions:

1. `Version` is the explicit content-generation boundary. A changed Version selects new OC/DC generations and automatically purges an enabled Edge domain.
2. OC/DC key-shaping changes immediately select a deterministic policy generation in the cache key. Old entries are no longer read under the new policy and expire naturally; no store scan or physical purge is required.
3. Edge changes that could leave an unsafe shared representation automatically purge the domain. Configuration reload also purges the old and new placement when Edge is disabled, enabled, or moved to another instance/provider namespace.

The Admin settings PATCH accepts `applyImmediately: true` for the optional actions below. If several changed settings target the same cache layer, that domain layer is invalidated once. With HttpBus distribution, every instance invalidates its local OC/DC state, but only the command origin queues the external Edge purge. Configuration-file reload has no `applyImmediately` switch: optional rows keep natural-expiration behavior.

| Domain setting(s) | Default behavior after change | With Admin `applyImmediately: true` |
|-------------------|-------------------------------|------------------------------------|
| `Version` | New OC/DC generation; enabled Edge domain purged | Same; the flag is not needed |
| `AuthBypassMode`, `VaryOutputCacheByUser`, `TreatAuthorizationAsAuthSignal`, `AuthVaryIncludeAuthorizationHash`, `VaryByAuthClaims` | New OC/DC key-policy generation; stricter bypass also takes effect on new requests | Same; Edge is additionally purged where the change is an Edge safety rule |
| `VaryByAccept`, `AcceptNormalizationList`, `VaryByAcceptLanguage`, `AcceptLanguageNormalizationList`, `VaryByHeaders`, `VaryByQueryKeys`, `IgnoreQueryKeys`, `VaryByCookies` | New OC/DC key-policy generation. Edge is automatically purged for changed native header-vary dimensions (`VaryByAccept`, `VaryByAcceptLanguage`, `VaryByHeaders`) | Same |
| `EmitResponseVary` | `false → true` automatically purges enabled Edge; other changes affect new responses | Same |
| `DataCache.RespectAuthBypass`, `DataCache.VaryOnPublicAddress`, `DataCache.VaryOnEncoding` | Relevant DC bypass/key-policy changes apply immediately; old key generation expires naturally | Same |
| `OutputCache.VaryByHost`, `OutputCache.EncodingNormalizationList` | New OC key-policy generation; old generation expires naturally | Same |
| `DataCache.Enabled`, `OutputCache.Enabled` | Disabling bypasses the layer immediately, but retained entries expire naturally | Disabling also evicts that domain from the affected layer; enabling does not evict |
| `DataCache.Instance` | New requests use the new instance; entries in the old instance expire naturally | Same |
| `DataCache.TtlSeconds`, `OutputCache.TtlSeconds` | Existing entries retain their creation TTL | A decrease evicts the affected domain layer once; an increase does not |
| `FusionCache.HardTtlSeconds`, `FailSafeSeconds`, `JitterSeconds` | Existing entries retain their creation policy | A decrease evicts the domain from Data Cache once; an increase does not |
| Other `FusionCache` settings | Affect subsequent cache operations/factories; existing entries expire naturally | No additional invalidation |
| `OutputCache.ETagMode` | Existing OC/Edge responses retain their original ETag until expiration | Evicts OC and purges enabled Edge |
| `OutputCache.CacheableStatusCodes` | Affects new storage decisions; existing responses expire naturally | Not runtime-patchable; no additional reload invalidation |
| `ClientCache.Cacheability` | `Public → Private/NoStore` automatically purges enabled Edge; other changes affect new responses | Same |
| `ClientCache.ForcePrivateWhenAuthenticated` | `false → true` automatically purges enabled Edge; other changes affect new responses | Same |
| `ClientCache.TtlMinSeconds` | Existing Edge objects retain their assigned TTL | A decrease purges enabled Edge; an increase does not |
| `ClientCache.TtlSeconds` | New responses use the new CCS calculation; existing Edge objects retain their assigned TTL | `0 → positive` purges enabled Edge because it enables CCS and may shorten Edge freshness; other changes do not |
| `ClientCache.ScheduledUpdateUtc` | Recalculates CCS for new responses; does **not** purge by default | Moving the schedule earlier (or setting the first schedule) purges enabled Edge; moving it later does not |
| `ClientCache.MustRevalidateNearUpdate` | Affects new client responses; existing responses expire naturally | No additional invalidation |
| `Edge.Enabled` | Reload purges the old placement when disabled and the new placement when enabled | Not runtime-patchable |
| `Edge.Instance` / effective provider namespace | Reload purges both old and new placements | Not runtime-patchable |
| `Edge.TtlSeconds`, `StaleWhileRevalidateSeconds`, `StaleIfErrorSeconds`, `PurgeOnStartup` | Affect newly stored Edge responses or the next startup; existing objects expire naturally | Not runtime-patchable |

If an operator needs stronger semantics than the automatic or optional rules provide, use the explicit domain/tag/entity [invalidation API](invalidation.md). A manual domain invalidation remains the unambiguous way to declare that all cached content for a domain is obsolete now.

### Nested sections

| JSON section | Portable? | Meaning |
|--------------|-----------|---------|
| `DataCache` | `CacheOrchestrator.Core` + `CacheOrchestrator.AspNetCore` | Core: enable, instance, TTL. AspNetCore HTTP keys under the same object: `RespectNoStore`, `VaryOnEncoding`, `VaryOnPublicAddress` — Fusion **or** Hybrid |
| `OutputCache` | `CacheOrchestrator.AspNetCore` | HTTP response cache TTL and Output Cache behavior |
| `ClientCache` | `CacheOrchestrator.AspNetCore` | Browser / CDN `Cache-Control` (+ schedule) |
| `FusionCache` | `CacheOrchestrator.FusionCache` only | Hard TTL, fail-safe, factory timeouts, jitter, … |
| `Edge` | `CacheOrchestrator.Edge` | Edge-only TTL/stale policy and named provider routing |

### Feature flags and vary (domain root)

| Property | Default* | Description |
|----------|----------|-------------|
| `AuthBypassMode` | `AuthenticatedOrAuthorization` | `Never` / `AuthenticatedIdentityOnly` / `AuthorizationHeaderOnly` / `AuthenticatedOrAuthorization` |
| `VaryOutputCacheByUser` | true | When authentication is not bypassed, vary Output Cache (and Data Cache when intentional) by user, claims, or API-key hash |
| `TreatAuthorizationAsAuthSignal` | true | `Authorization` counts as auth signal for OR-mode |
| `AuthVaryIncludeAuthorizationHash` | true | Hash `Authorization` into auth-user when no identity |
| `VaryByAuthClaims` | null | Claim types for auth-user material |
| `DataCacheRespectAuthBypass` | **true** | Data Cache skips when the Output Cache authentication bypass would fire. Set `false` only for caller-independent shared data. |
| `VaryByAccept` / `VaryByAcceptLanguage` | true / false | Content negotiation / locale vary |
| `AcceptNormalizationList` / `AcceptLanguageNormalizationList` | null (raw header) | Canonical single-token spellings; composite negotiation, quality, wildcards, and parameters remain intact — [vary.md](vary.md) |
| `VaryByHeaders` / `VaryByCookies` | null | Header/cookie **name** allowlists — [vary.md](vary.md) |
| `VaryByQueryKeys` | null | `null` = all non-tracking query keys; `[]` = none; non-empty = allowlist |
| `IgnoreQueryKeys` | null | Extra deny list on top of built-in tracking prefixes |
| `EmitResponseVary` | **true** | Emit HTTP response `Vary` for non-secret headers (set `false` to omit) |

\*After merge with defaults.

### Versioning

| Property | Description |
|----------|-------------|
| `Version` | Bulk invalidation stamp string (e.g. "v1", "2026-08"). Missing → stable default "1" + warning log (stable keys, no auto-invalidate on restart) |

### `DataCache` (portable `CacheOrchestrator.Core`)

| Property | Default* | Description |
|----------|----------|-------------|
| `Enabled` | true | Enable Data Cache for the domain |
| `Instance` | `default` | Key in `DataCacheInstances` |
| `TtlSeconds` | `3800` | Logical Data Cache TTL in seconds (Fusion soft/`Duration`; Hybrid expiration) |

### `DataCache` (HTTP-only `CacheOrchestrator.AspNetCore`)

Same JSON object as portable `DataCache`; bound by `CacheOrchestrator.AspNetCore`:

| Property | Default* | Description |
|----------|----------|-------------|
| `RespectNoStore` | true | Skip Data Cache when request has `Cache-Control: no-store` |
| `VaryOnEncoding` | true | Include Accept-Encoding in the Data Cache key |
| `VaryOnPublicAddress` | true | Include scheme + host in the Data Cache key |

### `OutputCache` (nested under domain)

| Property | Default* | Description |
|----------|----------|-------------|
| `Enabled` | true | Enable Output Cache for the domain |
| `TtlSeconds` | `3700` | Server-side output entry TTL in seconds |
| `VaryByHost` | **true** | Output Cache `VaryByHost` (host + port) |
| `CacheableStatusCodes` | `[200]` | Status codes allowed to store |
| `EncodingNormalizationList` | `br`, `gzip` | Prefer these Accept-Encoding values |
| `ETagMode` | `Version` | `Version`, `None`, or `Resource`. How Output Cache policy sets the HTTP `ETag` header. See [domain-profiles.md](../guide/domain-profiles.md). |

### `ClientCache` — [Client Cache Schedule](../guide/client-cache-schedule.md)

| Property | Default* | Description |
|----------|----------|-------------|
| `Cacheability` | `Public` | `Public`, `Private`, `NoStore` |
| `TtlSeconds` | `3600` | Target max-age far from schedule; `0` emits `max-age=0` and disables the schedule ramp |
| `TtlMinSeconds` | `60` | Floor max-age near/at update and during hold; also the enabled Edge fresh-TTL floor, clamped to the Edge maximum; `0` is valid and the value is ignored when `TtlSeconds` is `0` |
| `ScheduledUpdateUtc` | null | Planned cutover; linear ramp of client and enabled Edge fresh TTLs toward min; changes normally apply to subsequent responses; moving the schedule earlier with `ApplyImmediately` also purges Edge |
| `MustRevalidateNearUpdate` | false | Append `must-revalidate` at min floor |
| `ForcePrivateWhenAuthenticated` | true | Force client Private for signed-in Identity + Public |

See **[Client Cache Schedule](../guide/client-cache-schedule.md)** for phases, formula, and operational playbook.

### `FusionCache` (`CacheOrchestrator.FusionCache` only)

Bound from `Cache:DomainDefaults:FusionCache` / `Cache:Domains:{name}:FusionCache` by **CacheOrchestrator.FusionCache**. Ignored when Hybrid is the `IDataCacheProvider`.

| Property | Default* | Description |
|----------|----------|-------------|
| `HardTtlSeconds` | `43200` | Caps base `DataCache.TtlSeconds`; 0 disables this cap. It does not cap jitter or fail-safe retention |
| `FailSafeSeconds` | `86400` | Total fail-safe retention horizon from materialization, not an additional stale duration; 0 disables fail-safe. When enabled, must be at least the capped base Data Cache duration |
| `EagerRefreshRatio` | 0.9 | Eager refresh threshold. **`0` = disabled**; values in `(0, 1)` allowed; `>= 1` fails validation |
| `JitterSeconds` | `60` | Max jitter on duration (seconds) |
| `FactorySoftTimeoutSeconds` | `1` | Factory soft timeout (seconds) |
| `FactoryHardTimeoutSeconds` | `5` | Factory hard timeout (seconds) |
| `AllowBackgroundDistributed` | true | Fusion may complete L2 I/O in the background |
| `AllowBackgroundBackplane` | true | Fusion may publish backplane messages in the background |

`MaxItemBytes` and its runtime-overlay key were removed for 3.0. The beta setting assigned a fixed native cache weight; it never measured or limited payload bytes. Remove it from configuration (including a value of `0`); Fusion configuration validation rejects the old key. CacheOrchestrator exposes no replacement size setting or size-limit capability. Applications that require memory budgets must configure and own them through the underlying cache engine. The native [memory-cache size contract](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory#use-setsize-size-and-sizelimit-to-limit-cache-size) uses application-defined units.

## Resolved runtime snapshots

Resolved `DomainCacheOptions`, `DomainHttpCacheOptions` and provider-returned `DomainFusionCacheSettings` are shared snapshots. Treat them as immutable: create a new value or use configuration/runtime management to change policy. HTTP collection properties expose `IReadOnlyList<T>` and copy input at initialization; they retain the difference between a null query allowlist (all eligible keys) and an empty one (no keys). Public collection reads do not clone data. Fusion settings use init-only scalar properties, which continue to support configuration binding. Custom providers and contributors must not mutate request tag collections or retain mutable sections in the runtime store.

## Admin API (`Cache:Admin`)

Opt-in ops API on each application process. **Disabled by default** (no routes, no live counters).  
Details: [admin.md](admin.md). Map with `MapCacheOrchestratorAdmin()`.

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `false` | When false, Null stats collector and no admin routes |
| `ApiKey` | empty | `X-CacheOrchestrator-Admin-Key`; empty + Enabled = open (dev only) |
| `RoutePrefix` | `/cache-admin/local` | Base path for Admin API (and cluster receive path prefix) |
| `TrackEndpoints` | `true` | Per-route counters when Enabled |
| `TrackLatency` | `false` | Sum/count factory latency on Admin `/stats` (extra cost) |
| `TrackResultSize` | `false` | Sum/count factory result size on Admin `/stats` (string / byte[] / seekable stream). Does **not** gate the OTEL `factory.result_size` histogram. |

Process id is **`Cache:InstanceId`** (root), not under `Admin`.

Admin Console App (`AdminConsole` section) is configured only in `src/CacheOrchestrator.AdminConsole` — see [admin.md](admin.md#admin-console-app).

## Cluster bus (`Cache:Cluster:Bus`)

Optional multi-instance **command** distribution. Requires package **`CacheOrchestrator.HttpBus`** + `AddHttpClusterBus()` + `MapCacheOrchestratorHttpBus()`.  
Without the package, core registers a Null bus (no peer traffic). Details: **[cluster-bus.md](cluster-bus.md)**.

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `false` | When false, no peer publish even if the HttpBus package is registered |
| `Membership` | `Null` | `Null` · `Static` · `ServiceDiscovery` |
| `PeerTimeoutMs` | `2000` | Per-peer HTTP timeout (clamped **100–120_000** ms at publish) |
| `MaxParallelism` | `32` | Max concurrent peer deliveries (clamped **1–64**) |
| `DedupeWindowSeconds` | `330` | Receive-side `CommandId` window; must cover `CommandMaxAgeSeconds + ClockSkewSeconds` while HttpBus is enabled |
| `ApiKey` | empty | `X-CacheOrchestrator-Admin-Key` for receive endpoints; falls back to `Admin:ApiKey`; required when enabled unless unauthenticated mode is explicitly allowed |
| `AllowUnauthenticated` | `false` | Explicit opt-in for an open bus on an isolated development network |
| `CommandMaxAgeSeconds` | `300` | Reject commands older than this receive-side freshness window |
| `ClockSkewSeconds` | `30` | Maximum accepted future clock skew |
| `Static.Instances[]` | `[]` | `{ Id, Url }` peers when Membership is Static |
| `ServiceDiscovery.ServiceName` | empty | Logical name for SD (normalized to `http://{name}` when bare) |
| `ServiceDiscovery.DefaultScheme` | `http` | Scheme for peer URLs |
| `ServiceDiscovery.CacheSeconds` | `15` | In-process peer list cache (clamped **0–300**; **`0` = no cache**) |

ServiceDiscovery also needs host config endpoints under `Services:{name}` (see [cluster-bus.md](cluster-bus.md#servicediscovery-k8s--aspire--config-endpoints)).

## EF Core invalidation (`Cache:EFCore:Invalidation`)

Optional. Requires package **`CacheOrchestrator.EFCore.Invalidation`**. Type → `(domain, entityKind)` mapping is **code** (`[CacheEntity]`, Fluent `CacheInvalidate`, or `Map<T>`), not this section. Details: [ef-core-invalidation.md](ef-core-invalidation.md).

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `true` | Master switch |
| `BulkThreshold` | `20` | Id count that triggers `OnBulk` |
| `OnBulk` | `Kind` | `Entities` · `Kind` · `Domain` (domain wipes every kind in that policy group) |

## Validation

Package-owned validators run on start (`ValidateOnStart`). Core validates portable domain and Data Cache configuration; ASP.NET Core validates Output Cache, Client Cache, HTTP Data Cache behavior, authentication, vary, and ETag settings; provider packages validate their own sections.

- `DataCacheInstances` must contain **`default`**
- `DomainDefaults.DataCache.Instance` and each domain `DataCache.Instance` must name a registered instance (default `"default"`)
- Output Cache provider must match an `IOutputCacheBackendRegistrar` (`InMemory`, `Redis`, or custom via `AddOutputCacheBackend`)
- Fusion instance providers must match an `IFusionCacheBackendRegistrar` (`InMemory`, `Redis`, or custom via `AddFusionCacheBackend`)
- Redis provider requires a connection string (`Cache:Redis:Configuration` or the scoped `OutputCache:Redis` / `DataCacheInstances:{name}:Redis` override)
- Negative TTLs and Fusion durations fail; `FusionCache.EagerRefreshRatio` must be in `[0, 1)` when present
- Effective Fusion factory timeouts must be positive with soft &lt; hard; fail-safe must be disabled (`0`) or cover the effective Data Cache duration
- Effective `ClientCache.TtlMinSeconds` must not exceed a positive `TtlSeconds`, including inherited default/domain combinations; it is ignored when `TtlSeconds` is `0`
- An enabled HttpBus requires an API key unless unauthenticated mode is explicitly allowed; command freshness and clock-skew windows must be valid
- edge-enabled domains must reference a registered tag-native instance; Cloudflare requires a zone ID/token and Varnish requires an absolute protected purge URL
- Allowlists have max lengths (headers, cookies, query, claims, Accept lists)
- `AuthBypassMode` must be a defined enum value  

## Runtime model

Resolved settings use two immutable runtime snapshots:

- Core `DomainCacheOptions` contains `Domain`, `Version`, `VersionHex`, `DataCacheEnabled`, `DataCacheInstanceName`, `DataCacheTtl`, and `DataCacheNamespace`.
- `CacheOrchestrator.AspNetCore` `DomainHttpCacheOptions` exposes the Core snapshot through `CoreOptions` and adds the HTTP-owned settings.

Nested JSON seconds map to:

| JSON | Runtime |
|------|---------|
| `OutputCache.TtlSeconds` | `DomainHttpCacheOptions.OutputTtl` (`TimeSpan`) |
| `DataCache.TtlSeconds` | `DomainCacheOptions.DataCacheTtl` (`TimeSpan`) |
| `ClientCache.TtlSeconds` / `TtlMinSeconds` | `DomainHttpCacheOptions.ClientTtlSeconds` / `ClientTtlMinSeconds` (`int`) |
| `DataCache.Instance` | `DomainCacheOptions.DataCacheInstanceName` |
| `DataCache.Enabled` | `DomainCacheOptions.DataCacheEnabled` |
| `OutputCache.Enabled` | `DomainHttpCacheOptions.OutputCacheEnabled` |
| `ClientCache.Cacheability` | `DomainHttpCacheOptions.ClientCacheability` |

Fusion-only knobs stay on `DomainFusionCacheSettings` (`CacheOrchestrator.FusionCache`), not on Core `DomainCacheOptions`.

The JSON shape remains unified. `Cache:DomainDefaults` and `Cache:Domains:{domain}` are bound to package-owned models from the same configuration section:

- `CacheOrchestrator.Core` owns `Version` and portable `DataCache.Enabled`, `Instance`, and `TtlSeconds`.
- `CacheOrchestrator.AspNetCore` owns `OutputCache`, `ClientCache`, authentication/vary/ETag settings, and HTTP-only `DataCache.RespectNoStore`, `VaryOnPublicAddress`, and `VaryOnEncoding`.
- `CacheOrchestrator.FusionCache` owns the nested `FusionCache` tuning section.

This split does not change any `appsettings.json` key.

## Domain name normalization

`DomainName.Normalize` (public helper):

- lowercases  
- allows `a-z`, `0-9`, `-`, `:`, `_`, `@`  
- replaces other chars with `-`, collapses dashes  
- empty → `default`  

**Prefer already-normalized names** in `Domains` keys, `[CacheDomain("…")]`, and `.CacheOutputWithDomain("…")` (lowercase + allowed characters only). That hits the zero-allocation `IsNormalized` fast path on every request.

Case-only variants such as `MyStore` / `Products` still work: `Domains` is `OrdinalIgnoreCase`, and startup validation does **not** fail the host. Options validation logs a **warning** once at startup (and on options reload) recommending the normalized form. Keys that change beyond case after normalization (spaces, invalid characters, collapsed dashes) **fail** validation, because runtime lookup uses the normalized name and would miss that dictionary entry. Keys that normalize to `default` unintentionally (for example `!!!`) also fail.

Resource ids: `DomainName.NormalizeResourceId` trims surrounding whitespace but otherwise preserves opaque identity material, including case and punctuation. GUID input is canonicalized to lowercase `D` format. Null/whitespace becomes empty, **not** `default`. Visible key and tag segments are percent-encoded by their builders.

Entity kinds: `DomainName.NormalizeEntityKind` uses the restricted normalized-name rules because kinds are schema names rather than opaque identifiers. Unusable kinds do not share the domain name `default`.

## Example domains

See **[domain-profiles.md](../guide/domain-profiles.md)** for full **osm-tiles** (snapshot) and **product-detail** (CRUD) recipes.

## Related

- [Packages](../guide/packages.md) — which package owns which settings  
- [Guide](../guide/README.md) — orientation and learning path  
- [Topologies](../guide/topologies.md) — InMemory vs Redis vs HttpBus (when to share stores)  
- [Deployment](deployment.md) — multi-instance layouts and **[shared configuration across instances](deployment.md#shared-configuration-across-instances)** (`appsettings.cache.json`, ConfigMap, identical policy on every node)  
- [cache-keys.md](cache-keys.md) — Namespace and key composition  
- [cluster-bus.md](cluster-bus.md) — optional HTTP command fan-out (invalidation / Admin distribute); not a substitute for shared Redis or shared policy config  
- [invalidation.md](invalidation.md) — Version, tags, multi-instance purge  
- [domain-profiles.md](../guide/domain-profiles.md) — example domain recipes  
- [architecture.md](../contributor/architecture.md) — package layout and runtime snapshots  
