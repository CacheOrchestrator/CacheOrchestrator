# CacheOrchestrator Admin

> **Reference** — Management API, Admin API, and Admin Console App.

The management model has three layers:

| Piece | What it is | Where it runs |
|-------|------------|----------------|
| **Management API** | Transport-independent queries and operations through `ICacheOrchestratorManagement` | `CacheOrchestrator.Core`; available to web apps, workers, command handlers, and custom adapters |
| **Admin API** | Opt-in HTTP routes that delegate to the Management API | `CacheOrchestrator.AspNetCore` (`Cache:Admin` + `MapCacheOrchestratorAdmin`) |
| **Admin Console App** | Dashboard that fans out to each instance Admin API | Separate process / Docker image — not a NuGet package, [deploy/admin](../../deploy/admin/README.md), [Admin Console README](../../src/CacheOrchestrator.AdminConsole/README.md) |

Use the **Management API** from application code or a custom transport. Use the **Admin API** for scripts and HTTP automation. Use the **Admin Console App** for multi-instance UI.

## Table of Contents

- [Architecture](#architecture)
- [Distribution](#distribution)
- [Management API](#management-api)
- [Admin API](#admin-api)
- [Admin Console App](#admin-console-app)
- [Common pitfalls](#common-pitfalls)
- [Out of scope](#out-of-scope)
- [Security checklist](#security-checklist)

## Architecture

```
┌─────────────────────┐    HTTP fan-out      ┌──────────────────────────┐
│ Admin Console App   │ ──────────────────►  │ App instance A           │
│ - AdminConsole:*    │  X-CacheOrchestrator-Admin-Key   │ MapCacheOrchestratorAdmin│
│ - /api/* + SPA      │                      │ /cache-admin/local/*     │
│ (browser → open)    │ ──────────────────►  │ App instance B           │
└─────────────────────┘                      └──────────────────────────┘
         ▲
         │  no built-in login
         │  protect with network / SSO proxy
      Operators
```

- The Admin Console App is **never** on the end-user caching hot path.  
- **Admin Console App traffic stats** come only from the OTEL meter scraped into Prometheus (`increase()` over the selected Range per domain/route/`instance_id`). Admin API `/stats` is a compact process-lifetime raw snapshot for diagnostics.
- Runtime **Version** and **TTL** overlays are **process-local** on each node unless the optional [cluster bus](cluster-bus.md) publishes them (`distribute: true` / Admin Console App **bus-distribute**). Without bus, Admin Console App **fan-out** must hit every instance that should change.

---

## Distribution

| Piece | How users get it |
|-------|------------------|
| Management API | Ships in **`CacheOrchestrator.Core`** and is registered by `AddCacheOrchestratorCore`. |
| Admin API | Ships inside **`CacheOrchestrator`** / **`CacheOrchestrator.AspNetCore`**. No extra package. Routes default to disabled (`Cache:Admin:Enabled` = false). |
| Admin Console App | Source in repo; `dotnet run` / `dotnet publish`; **Docker image** on GHCR with each GitHub Release. Not published to nuget.org. |

| Image | |
|-------|--|
| Registry | `ghcr.io/cacheorchestrator/cacheorchestrator-admin-console` |
| Tags | Release version (e.g. `1.2.3`), plus `latest` for stable releases |
| Docs | **[deploy/admin/README.md](../../deploy/admin/README.md)** — config mount, `data/` volume for custom hints + disabled state, logs |

Run the Admin Console App as an internal ops service (Docker or Helm, VPN only). Configure **instances** and **API key** per environment; product hint pack stays in the image (`hints/core-hints.json`).

---

## Management API

`AddCacheOrchestratorCore` registers `ICacheOrchestratorManagement`. The Management API supports:

- health, cluster identity, domain configuration, host resource discovery, and diagnostic statistics;
- domain, entity, entity-kind, and tag invalidation;
- runtime Version and settings changes, with optional cluster distribution;
- the domain settings catalog used to validate runtime patches.

```csharp
using CacheOrchestrator.Admin;

public sealed class CacheOperations(ICacheOrchestratorManagement management)
{
    public Task<AdminDomainMutationResultDto> MoveCatalogToVersionAsync(
        string version,
        CancellationToken cancellationToken) =>
        management.SetVersionAsync(
            "catalog",
            new AdminVersionRequest { Version = version, Distribute = true },
            cancellationToken);
}
```

Core supplies a Data Cache domain view and an empty resource catalog. Host packages can enrich these through `IAdminDomainConfigProvider` and `IAdminEndpointCatalog`; `CacheOrchestrator.AspNetCore` supplies both adapters.

Mutation methods validate input with `ArgumentException`. A distributed Version or settings result carries `ClusterPublish` so a host can report partial peer failure; how that appears on the wire is up to the host (for HTTP, see [Admin API](#admin-api)).

## Admin API

HTTP adapter over the Management API. Opt in per app instance with `Cache:Admin` and `MapCacheOrchestratorAdmin`. `Cache:Admin:Enabled` controls these HTTP routes and live Admin counters; it does not affect whether application code can resolve the Management API.

### Enable (each app instance)

```json
{
"Cache": {
  "InstanceId": "app-1",
  "Admin": {
    "Enabled": true,
    "ApiKey": "use-a-strong-secret-in-production",
    "RoutePrefix": "/cache-admin/local",
    "TrackEndpoints": true,
    "TrackLatency": false,
    "TrackResultSize": false
  }
}
}
```

```csharp
app.UseCacheOrchestrator();
// …
app.MapCacheOrchestratorAdmin(); // after routing is available; safe no-op when Admin disabled
```

| Option | Default | Notes |
|--------|---------|--------|
| `Cache:InstanceId` | machine name | Single process id for Admin, cluster bus, diagnostics |
| `Admin:Enabled` | `false` | No routes, no counter cost when false |
| `Admin:ApiKey` | empty | Empty + Enabled ⇒ **open** endpoints (dev only; logs a warning) |
| `Admin:RoutePrefix` | `/cache-admin/local` | Must match Admin Console App `AdminApiPathPrefix` |
| `Admin:TrackEndpoints` | `true` | Per-route counters |
| `Admin:TrackLatency` | `false` | Extra cost if true (Admin `/stats` factory duration sums) |
| `Admin:TrackResultSize` | `false` | Extra cost if true (Admin `/stats` factory size sums). Does not gate OTEL `factory.result_size`. |

Process identity is **`Cache:InstanceId`** (not under Admin). Same id is used by the optional cluster bus.

### Cluster distribute (with CacheOrchestrator.HttpBus)

When the HTTP bus is enabled, Admin API mutation bodies accept **`distribute`** (default `false`):

| Endpoint | `distribute: false` | `distribute: true` |
|----------|---------------------|--------------------|
| `POST …/invalidate` | This process only | Local + peers via bus |
| `POST …/domains/{d}/version` | Local Version overlay | Local + `VersionBumpCommand` |
| `PATCH …/domains/{d}/settings` | Local overlay | Local + `SettingsPatchCommand` |

With **`distribute: true`**, Admin API applies the change on the origin first, then publishes to peers. If **any peer fails**, the HTTP response is **409 Conflict** with `localApplied: true` and `peerFailures[]` (cluster may already be inconsistent — no automatic rollback).

**Admin Console App** probes `GET …/cluster/info` on each configured instance (`GET /api/distribution`):

| Capability | Write behaviour |
|------------|-----------------|
| No bus | **fan-out** — HTTP to every target with `distribute:false` |
| Bus enabled (Static/ServiceDiscovery) | **bus-distribute** — one healthy origin with `distribute:true` (peers via bus) |

Admin Console App write APIs return **200** only when every contacted instance succeeded; otherwise **409** with `outcome` (`partialFailure` / `failed`), `failedInstanceIds`, and `warning`. Operations UI confirms before Run when probes show down instances, and shows a critical alert on incomplete writes.

Never combine full Admin Console App fan-out **and** `distribute:true` for the same action — the Admin Console App chooses one path automatically.

Receive path for peers: `MapCacheOrchestratorHttpBus()` (not gated on `Admin:Enabled`).

Deep dive (membership, commands, metrics, security): **[cluster-bus.md](cluster-bus.md)**.

### Auth header

When `ApiKey` is set, every Admin API call must send:

```http
X-CacheOrchestrator-Admin-Key: <same-as-Cache:Admin:ApiKey>
```

Comparison is fixed-time. Wrong/missing key ⇒ `401 Unauthorized`.

### Important endpoints

Base path = `RoutePrefix` (default `/cache-admin/local`).

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | `Healthy` (probes + counters), `InstanceId`, `StartedAtUtc`, `UptimeSeconds`, `Requests` |
| GET | `/cluster/info` | Bus/membership snapshot (mapped even **without** the Bus package) |
| GET | `/stats` | Process-lifetime raw counters — diagnostics / external tools only; the Admin Console App does **not** use this for the traffic UI |
| GET | `/endpoints` | Discovered + counted routes |
| GET | `/domains` | Effective domain options snapshot |
| GET | `/domains/{name}` | One domain; **404** if unknown |
| GET | `/domain-settings/catalog` | Overlay field catalog for PATCH settings |
| POST | `/invalidate` | Domain / entity invalidation |
| POST | `/domains/{name}/version` | Runtime version overlay |
| PATCH | `/domains/{name}/settings` | **Primary** runtime overlay (TTLs, vary, flags, …) |

Responses are **not** stored in Output Cache (`NoStore` on the admin group).

The Management API and Admin API expose the same `AdminLiveStatsRawSnapshot` shape. Time-window analytics should use the OTEL meter `CacheOrchestrator` (Prometheus) and the Admin Console App `GET /api/stats/window`.

### Mutation request bodies

Invalidate a domain:

```http
POST /cache-admin/local/invalidate
Content-Type: application/json
X-CacheOrchestrator-Admin-Key: <key>

{
  "scope": "domain",
  "domain": "catalog",
  "distribute": false
}
```

The four accepted invalidation shapes are:

```text
{ "scope": "domain", "domain": "catalog", "distribute": false }
{ "scope": "entity", "domain": "catalog", "entityKind": "products", "entityId": "42", "distribute": false }
{ "scope": "entityKind", "domain": "catalog", "entityKind": "products", "distribute": false }
{ "scope": "tags", "tags": [ "custom:import-2030-08" ], "distribute": false }
```

`entityId` is a string in this HTTP DTO, so numeric IDs are quoted on the wire. In C# application code, use the generic invalidation overload and pass `42` directly.

Set a specific runtime Version, or omit `version` to generate a unique `rt-{utcTicksHex}` value:

```json
{ "version": "release-2030-08-15", "distribute": true }
```

```json
{ "distribute": true }
```

Patch settings with a sparse dictionary of catalog IDs:

```http
PATCH /cache-admin/local/domains/catalog/settings
Content-Type: application/json
X-CacheOrchestrator-Admin-Key: <key>

{
  "settings": {
    "outputCache.ttlSeconds": 60,
    "dataCache.ttlSeconds": 300,
    "clientCache.ttlSeconds": 30,
    "varyByHeaders": [ "X-Tenant" ],
    "fusionCache.failSafeSeconds": 900
  },
  "distribute": true,
  "applyImmediately": true
}
```

Use `GET /domain-settings/catalog` as the canonical list. A setting is writable only when its catalog entry has `runtimeOverlay: true`; IDs are matched case-insensitively, values are validated by their declared kind, and omitted settings keep their current values. Fusion IDs appear only when the FusionCache package has registered its catalog section and patch contributor.

`applyImmediately` defaults to `false`. Most policy changes then affect newly stored entries while existing entries keep the policy with which they were created until natural expiration. Key-shaping changes are an exception: Output Cache and HTTP-derived Data Cache switch to a deterministic new key generation immediately, without scanning or purging the old generation. Edge safety changes are also purged automatically. With `applyImmediately: true`, supported policy reductions additionally invalidate the affected domain layer once; increases do not evict entries. See [configuration — change activation and invalidation](configuration.md#domain-setting-change-activation-and-invalidation) for the complete table.

`distribute: true` carries both the patch and `applyImmediately` to HttpBus peers. Every peer applies its local Output/Data Cache action, while only the originating instance queues the external Edge purge. This avoids one Edge purge per application instance.

A successful Version or settings mutation returns the normalized `domain` and complete effective domain snapshot. With `distribute: true`, a peer failure returns `409` with `localApplied: true`, command metadata, and `peerFailures`; the local mutation is not rolled back.

### Admin API `/stats` (process-lifetime raw snapshot)

Designed for curl/scripts and Management API hosts: process-lifetime counters without presentation shares or rates. When `Cache:Admin:TrackLatency` / `TrackResultSize` are on, factory duration and result-size sums are included.

**Prefer** Prometheus for multi-instance and time windows. Admin Console App traffic UI is Prom-only.

The Admin Console App derives presentation shares and rates from Prometheus window counters, not from the Admin API raw snapshot. Its request denominator is:

```text
requests = (outputCacheHits+outputCacheMisses+outputCacheBypass+outputCacheOff) if > 0
         else (dataCacheHits+dataCacheMisses+dataCacheStale+dataCacheBypass)
factoryShare = factoryRuns / requests
```

### Stats model (shares vs rates)

1. **Output Cache** events on HTTP responses (hit / miss / bypass / off).
2. **Data Cache** events on the data path (hit / miss / stale / fail / bypass / off).
3. **Factory** events whenever application/origin work runs, whether direct (`dc=n/a`) or through Data Cache.


| Metric type | Question it answers |
|-------------|---------------------|
| **Request share** (`hitShare`, `factoryShare`, …) | Of **all** requests, what fraction? |
| **Layer rate** (`hitRate`, …) | Of traffic that **reached that layer**, what fraction? |

#### Factory share (also known as origin)

The application/origin work needed to produce a response is the **factory**. Admin UI labels this **FA run**. A factory invocation is counted for direct endpoints without Data Cache (`dc=n/a`) and whenever a Data Cache callback runs — miss, fail-safe stale, hard fail, Data Cache **disabled** (`off`), unresolved domain, and auth/no-store bypass. It is **also known as origin** in CDN/proxy language.

| Admin label | API / JSON field | Formula |
|-------------|------------------|---------|
| **Output Cache hit share** | `oc.hitShare` / pipeline `outputCacheHitShare` | `outputCacheHits / requests` |
| **Data Cache hit share** | `dataCache.hitShare` / pipeline `dataCacheHitShare` | `dataCacheHits / requests` (fresh hits only) |
| **FA run / Factory share** | `fc.factoryShare` | `factoryRuns / requests` |
| **Data Cache stale %** (overlay) | `dataCache.staleShare` / pipeline `staleShare` | `stale / requests` (also included in factory run) |

These three shares (Output Cache hit, Data Cache hit, factory run) use the same request denominator and form the exclusive pipeline bar. **Data Cache stale %** is extra information, not a fourth segment. Layer **bypass** is an authentication or `no-store` skip; disabled Output Cache is `off`. **Layer rates**, such as Data Cache miss rate among requests that reached Data Cache, remain on detail views. Prefer factory share when asking “how often did the origin run?”; see [admin hints](../contributor/admin-hints.md).

**Low sample flags**

| Flag | Based on | Apply to |
|------|----------|----------|
| `lowRequestSample` | total **requests** &lt; 20 | request **shares** (Output Cache / Data Cache hit share, factory share, …) |
| `lowSample` | **layer** hits+misses &lt; 20 | **layer rates** (Output Cache / Data Cache hit and miss rate) |

If Output Cache absorbs almost all traffic, Data Cache hit **share** is still trustworthy once requests ≥ 20, while Data Cache hit **rate** may show a low sample because few requests reached that layer.

### Health semantics (Admin Console App mapping)

| Probe result | Instance status in UI |
|--------------|------------------------|
| HTTP OK + `Healthy == true` | **Healthy** |
| HTTP OK + `Healthy == false` | **Degraded** |
| Timeout / connection error / 401 | **Down** |

`Requests` / uptime on the instance row come from health when the probe succeeds.

`Healthy` is **not** “the HTTP endpoint answered”. It is `true` only when live counters can be read **and** every registered `ICacheOrchestratorHealthProbe` succeeds (InMemory with no probes stays `true`). A failed Redis (or other backend) probe returns HTTP 200 with `Healthy: false` so the Admin Console App can show **Degraded**.

### Limitations (Admin API)

- Counters are **process lifetime** (reset on restart), not sliding windows.  
- Version/TTL overlays do **not** replicate to other nodes by themselves.  
- Several instances stay coherent through Redis L2 and the backplane, shared configuration, or the [cluster bus](cluster-bus.md).

---

## Admin Console App

Standalone host targeting **net10.0** only (ops tool). Target apps may still run on **net8.0** or **net10.0** independently — the Admin Console App talks HTTP to each instance Admin API and does not need to match instance runtimes.

**Traffic UI, windowed stats, impact, and hints require a metrics store.** Configure `AdminConsole:Metrics` against a **Prometheus-compatible** HTTP API that scrapes meter `CacheOrchestrator` from your apps (Prometheus, or Mimir / VictoriaMetrics / Thanos Query with the same API). The only supported `Provider` value today is **`Prometheus`**. Without Metrics enabled, health, domain config, and operations still work via the Admin API; charts and recommendation inputs stay offline.

### Configuration

Section: `AdminConsole` → `AdminConsoleOptions`.

```json
{
  "AdminConsole": {
    "ApiKey": "use-a-strong-secret-in-production",
    "RequestTimeoutMs": 3000,
    "Parallelism": 8,
    "AdminApiPathPrefix": "/cache-admin/local",
    "Instances": [
      { "id": "app-1", "url": "https://app-1.internal:8080" },
      { "id": "app-2", "url": "https://app-2.internal:8080" }
    ]
  }
}
```

| Key | Description |
|-----|-------------|
| `ApiKey` | Sent as `X-CacheOrchestrator-Admin-Key` to every instance (**must match** each app’s `Cache:Admin:ApiKey`) |
| `RequestTimeoutMs` | Per-call timeout (1–120000) |
| `Parallelism` | Max concurrent fan-out (1–64) |
| `DownReprobeSeconds` | After an instance is Down, skip HTTP to it for this many seconds (5–300, default 15), then re-probe |
| `AdminApiPathPrefix` | Path on each instance (must match `RoutePrefix`) |
| `Instances[].id` | Stable UI / filter id |
| `Instances[].url` | **Base URL only** (scheme + host + port) — no `/cache-admin/...` path |
| `Metrics` | Optional Prometheus-compatible store for the **Metrics** page (see below) |
| `Hints` | Declarative rule packs + disable list ([admin-hints.md](../contributor/admin-hints.md), operator guide [Admin hints/README](../../src/CacheOrchestrator.AdminConsole/hints/README.md)) |

### Metrics store (time series)

Admin Console App can query an external Prometheus-compatible HTTP API (Prometheus, Mimir, VictoriaMetrics, Thanos Query) for windowed charts. **No core library changes** — the apps only need the usual OTel/Prometheus scrape of meter `CacheOrchestrator`.

Minimal config (everything else has defaults). The Admin Console App in this repo defaults to local Prometheus:

```json
{
"AdminConsole": {
  "Metrics": {
    "Enabled": true,
    "Provider": "Prometheus",
    "BaseUrl": "http://localhost:9090"
  }
}
}
```

Dev stack (Playground + Prometheus + Admin Console App labs): [samples/CacheOrchestrator.Sample/labs/README.md](../../samples/CacheOrchestrator.Sample/labs/README.md) (sample only, not a library dependency).

| Key | Default | Notes |
|-----|---------|--------|
| `Enabled` | `false` | Off → UI shows “not configured”, no probe |
| `Provider` | `Prometheus` | Prometheus HTTP API v1 only |
| `BaseUrl` | empty | Required when enabled |
| `TimeoutMs` | `5000` | Probe / query timeout |
| `DefaultRange` | `1h` | UI default (`15m` / `1h` / `6h` / `24h` / `7d`) |
| `BearerToken` | empty | Optional `Authorization: Bearer` |
| `PathPrefix` | empty | e.g. `/prometheus` behind a reverse proxy |

When **not configured**, statistics and charts are unavailable (UI shows Metrics offline); health, domain config, and operations still work via Admin API. When **configured but unreachable**, the UI shows **Disconnected** with the same **Provider · host** (from `BaseUrl`) plus not connected / error text — so the target is always visible even when the probe fails (no fake zeros). Metrics store status also appears on **Instances**.

#### Windowed stats (Prometheus) — Admin Console App traffic source

| Admin Console App API | Role |
|-------------|------|
| `GET /api/stats/window` | Domain/endpoint counters + shares + impact + hints for the selected window (`range` and/or `from`/`to`, optional `domains`) |

| `GET /api/metrics/series` | Chart panels (`range`, `from`/`to`, `panels`, `domains`, `instances`, `routes`) |
| `GET /api/metrics/summary` | Compact rates/shares for the window |

Overview, Domains, Endpoints, detail **traffic**, header KPIs, and **Hints** use `/api/stats/window` only. **Green underline** = current config/identity (Version, TTL, …), not the window. Admin API process counters are **not** used for Admin Console App stats.

Window aggregates use Prometheus **`increase(metric[range])`** over the selected Range (the same principle as chart `rate` / `increase`), so domains and endpoints that had traffic mid-window still count even if OTEL later stops exporting those labels. Instant `now − offset` is not used for that reason. Rows with **zero requests** and no invalidations in the window are omitted from domain and endpoint tables; charts may still draw historical curves for series present in TSDB. A new series with only one scrape may under-count until the next scrape (fallback: current value when the series did not exist at the start of the window). Output Cache, Data Cache, and invalidation series group by `domain` / `result`; endpoints group by `route`. Factory duration uses histogram `_sum` / `_count`. Data Cache meter `result=fail` maps a hard factory exception to a factory failure. Per-instance views use scrape label `instance_id` (lab: `playground-1`); missing labels become **`undefined`**.

`HintEngine` runs on window domain/endpoint rows. Config-only rules still receive Admin domain config when fan-out succeeds. Rules needing factory-failure rates need `result=fail` or `stale` samples in the window.

Endpoint window rows need core `Cache:Metrics:IncludeEndpointLabel` (default true). If disabled, domain-level window stats still work; endpoint rows stay empty.

### Run (local)

```bash
dotnet run --project src/CacheOrchestrator.AdminConsole
```

Default UI: `http://localhost:5188/` (see launchSettings). Default `Instances` point at the Playground sample (`:5289`). Metrics time series use Playground scrape via Prometheus.

Quick operator steps: [Admin Console App README](../../src/CacheOrchestrator.AdminConsole/README.md).

### What the SPA shows

- Chrome: brand → **metrics strip** (health from Admin; traffic KPIs from Prometheus Range; optional metrics store pill) → **menu**  
- Overview: instances (Admin health); **top 5 domains/endpoints** from Prometheus window; charts when Metrics connected  
- Lists: filters, search, sort; detail pages; Hints page (same rules on Prometheus window rows)  
- **Metrics** (`#/metrics`): window charts from Prometheus; multi-select domains; global Range (relative + absolute from/to)  
- **Live** (`#/live`): near-real-time health/performance; fixed **1m** Prometheus lookback and **5s** refresh (not Range-scoped). `HintEngine` also runs on this snapshot.  
- **Operations** (`#/operations`): invalidate / version / **Patch settings**, including the optional **Apply immediately** control; banner **HTTP fan-out** vs **Cluster bus (distribute)**; cluster probe table; last-run mode in result
- **Settings** (`#/settings`): hint rule catalog, enable/disable, reload  
- Auto-refresh interval in `localStorage`  

### Recommendation hints

Evaluated **only in the Admin Console App** on Prometheus window stats (`HintEngine` + JSON packs), plus domain config for config-only rules.  
**Customizable:** product defaults in `hints/core-hints.json`; extra packs via `AdminConsole:Hints:RuleFiles`; enable/disable in **Settings**. UI does not invent rules.

Step-by-step custom rules (ships with the Admin Console App): [hints/README.md](../../src/CacheOrchestrator.AdminConsole/hints/README.md).  
Repo overview: [admin-hints.md](../contributor/admin-hints.md).

### Admin Console App HTTP API (for the SPA / automation)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/overview` | Instance health / connectivity only (no traffic counters) |
| GET | `/api/instances` | Health probe fan-out |
| GET | `/api/distribution` | Probe `…/cluster/info`; recommended write mode (fan-out vs bus-distribute) |
| GET | `/api/live` | **Live** snapshot: fixed 1m rates + instance health (not Range-scoped) |
| GET | `/api/stats/window?range=&from=&to=&domains=` | **Traffic stats** (Prometheus): domains/endpoints + impact + Peak RPS + hints |
| GET | `/api/about` | Admin Console App host version (UI pill) |
| GET | `/api/domains` | Domain config fan-out |
| GET | `/api/domain-settings/catalog` | Overlay field catalog |
| GET | `/api/metrics/status` | Metrics store probe (`NotConfigured` / `Disconnected` / `Connected`) |
| GET | `/api/metrics/catalog` | Allowlisted chart panels |
| GET | `/api/metrics/series?range=&panels=&domains=` | Range series for panels |
| GET | `/api/metrics/summary?range=` | Window KPI snapshot |
| GET | `/api/hints/rules` | Hint catalog (also [admin-hints.md](../contributor/admin-hints.md)) |
| POST | `/api/hints/reload` | Reload hint packs |
| PUT | `/api/hints/rules/{code}/enabled` | Enable/disable a code |
| POST | `/api/invalidate` | Invalidate (auto fan-out or bus-distribute) |
| POST | `/api/domains/{domain}/version` | Version overlay write |
| PATCH | `/api/domains/{domain}/settings` | **Primary** overlay write (Operations “Patch settings”) |

Write responses include `distributionMode`, `distribute`, `distributionSummary`, optional `busOriginInstanceId`, and per-instance `results[]`.

**Warning:** These `/api/*` routes have **no application-level authentication**. Anyone who can reach the Admin Console App can read stats and run operations. Protect the host (see [Security checklist](#security-checklist)).

---

## Common pitfalls

| Symptom | Likely cause |
|---------|----------------|
| All instances **Down** | Wrong URL/port; Admin API not mapped; firewall; **401** wrong/missing ApiKey |
| Empty domains/endpoints | No traffic yet; all targets down; filters set to **None** |
| Version/TTL “didn’t stick” cluster-wide | Overlay is **process-local** without bus; use fan-out to all nodes, or bus-distribute; node down during write |
| High Fusion miss rate while everything appears healthy | Prefer **factory share** (also known as origin) and Output Cache hit share; see shares vs rates |
| Scalar OpenAPI missing | OpenAPI + Scalar are mapped in **all** environments on the Admin Console App (`/scalar`; requires net10 runtime for the Admin Console App host) |
| CORS issues calling Admin API from a browser | Prefer Admin Console App fan-out; Admin API is for server-side callers |

---

## Out of scope

- Built-in time-series database inside Admin Console App (use Prometheus / compatible store).  
- Free-form PromQL from the browser (panels are allowlisted server-side).  
- Redis topology management.  
- Publishing Admin Console App as a NuGet library.  
- Built-in OIDC login UI inside Admin Console App (use edge / reverse-proxy auth).

---

## Security checklist

API key is the **intended** machine-to-machine credential for Admin API — not a temporary mock. Sample values like `dev-admin-key` are **dev-only**.

### Two different trust boundaries

| Path | Protected by? |
|------|---------------------|
| **Admin Console App → Admin API** on each app | Optional shared secret `X-CacheOrchestrator-Admin-Key` (required in production) |
| **Browser / user → Admin Console App** | **No built-in login** — network / reverse-proxy auth only |

The key stops strangers from calling `/cache-admin/local` on your apps **if** they cannot reach Admin Console App *or* guess the key. It does **not** by itself decide which humans may open the dashboard.

> [!IMPORTANT]
> - [ ] Keep Admin API off the public internet (private mesh; allowlist Admin Console App only)
> - [ ] Protect Admin Console App with VPN, bastion, internal ingress, or zero-trust access — not a public anonymous URL
> - [ ] Use a strong shared API key (e.g. 32+ bytes, base64) on every instance (`Cache:Admin:ApiKey`) and Admin Console App (`AdminConsole:ApiKey`)
> - [ ] Load keys from a secret store — **never** commit production keys or leave `ApiKey` empty with `Enabled=true` outside local dev
> - [ ] Put human auth in front of Admin Console App `/` and `/api` (OAuth2/OIDC proxy, mTLS/mesh, or VPN-only)
> - [ ] Use HTTPS for browser → Console and Console → instances so the key is not sent in clear text
> - [ ] Limit who can open Operations (invalidate / version / TTL are mutations); prefer platform audit logs for admin host access
> - [ ] Do not rely on API key alone without network isolation, or on sample `dev-admin-key` in shared environments
>
> You may enable Admin API for scripts only (no Console App). Still set `ApiKey` and lock down network if the process is reachable outside localhost. Peer bus receive auth is covered in [cluster-bus.md](cluster-bus.md#security-checklist).

## Related

- [Guide — operations](../guide/operations.md) — enabling Admin, health, and day-2 workflows  
- [admin-hints.md](../contributor/admin-hints.md) — recommendation hints + customization  
- [observability.md](observability.md) — metrics / `X-CacheOrchestrator` / health checks
- [invalidation.md](invalidation.md) — domain/entity invalidation model  
- [configuration.md](configuration.md) — domain options binding and `Cache:Admin`  
- [architecture.md](../contributor/architecture.md) — library layers  
- [deployment.md](deployment.md) — multi-instance topologies  
- [cluster-bus.md](cluster-bus.md) — Admin `distribute` and peer fan-out  
