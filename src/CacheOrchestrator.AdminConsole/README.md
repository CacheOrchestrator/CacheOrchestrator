# CacheOrchestrator.AdminConsole

The Admin Console App gives operators one place to inspect multiple CacheOrchestrator instances, review live cache statistics and recommendations, and run domain invalidation or Version/TTL operations.

<img src="../../docs/assets/admin-overview.png" alt="CacheOrchestrator Admin Console overview with instance health and cache statistics" width="800" />

## Table of Contents

- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Choose the next document](#choose-the-next-document)
- [Enable the Admin API on each instance](#enable-the-admin-api-on-each-instance)
- [Configure this host](#configure-this-host)
- [Run locally](#run-locally)
- [Docker](#docker)
- [Recommendation hints](#recommendation-hints)
- [Metrics (Prometheus)](#metrics-prometheus)

## Quick start

From the repository root, start the Playground, Prometheus, and Admin Console App together:

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/01-observability.yml up --build -d
```

Open:

- http://localhost:5188/ — Admin Console
- http://localhost:5289/ — Playground
- http://localhost:9090/ — Prometheus

For a direct host run, continue to [Run locally](#run-locally).

> [!NOTE]
> This host is not a NuGet package and targets .NET 10 only. Monitored applications may run on .NET 8 or .NET 10 because the Admin Console App communicates with them over HTTP.

## How it works

- The **Admin API** on each configured instance provides health, effective domain settings, discovery, and operations such as invalidation and Version/TTL changes.
- **Prometheus** provides time-window statistics, charts, domain and endpoint traffic tables, impact analysis, and recommendation inputs.
- Without a Metrics store, health, configuration, and operations remain available, but traffic statistics and charts do not.

## Choose the next document

| Need | Read |
|------|------|
| Understand the operator workflow | [Guide — operations](../../docs/guide/operations.md) |
| Architecture, security, and API contracts | [Admin reference](../../docs/reference/admin.md) |
| Docker image, volumes, custom hints, and logs | [Admin deployment](../../deploy/admin/README.md) |
| Write and install custom hint rules | [Hints handbook](hints/README.md) |
| Contribute to the hints implementation | [Admin hints contributor guide](../../docs/contributor/admin-hints.md) |
| Coordinate operations across instances | [Cluster bus reference](../../docs/reference/cluster-bus.md) |

---

## Enable the Admin API on each instance

```json
{
  "Cache": {
    "InstanceId": "app-1",
    "Admin": {
      "Enabled": true,
      "ApiKey": "dev-admin-key"
    }
  }
}
```

```csharp
app.MapCacheOrchestratorAdmin();
```

---

## Configure this host

```json
{
  "AdminConsole": {
    "ApiKey": "dev-admin-key",
    "RequestTimeoutMs": 3000,
    "Parallelism": 8,
    "AdminApiPathPrefix": "/cache-admin/local",
    "Instances": [
      { "id": "app-1", "url": "http://localhost:5290" },
      { "id": "app-2", "url": "http://localhost:5291" }
    ],
    "Metrics": {
      "Enabled": true,
      "Provider": "Prometheus",
      "BaseUrl": "http://localhost:9090"
    },
    "Hints": {
      "RuleFiles": [ "hints/*.json" ],
      "DisabledStatePath": "hints/disabled.local.json"
    }
  }
}
```

- **ApiKey** is sent as `X-CacheOrchestrator-Admin-Key` (must match each instance).
- **Instances[].url** is the application base URL only.  
- **AdminApiPathPrefix** must match `Cache:Admin:RoutePrefix`.  
- **Restart required** after changing `Instances`, `ApiKey`, timeouts, or `Metrics` (bound via `IOptions` snapshot). Hint packs (`Hints`) reload without restart.  
- Production keys belong in a secret store; put VPN/SSO in front of this host.  
- Invalidate / Version / TTL change live cache state — see [docs/reference/admin.md — Security](../../docs/reference/admin.md#security-checklist).

### Defaults by environment

| Environment | Instances / Metrics | Custom hints | Disabled file |
|-------------|---------------------|--------------|---------------|
| **Development** (`dotnet run`) | Playground `:5289`, Metrics on | `hints/*.json` | `hints/disabled.local.json` |
| **Production** / Docker image | Empty (you configure) | `data/rules/*.json` | `data/disabled.local.json` |

Product pack **`hints/core-hints.json` is always loaded** in every environment.

---

## Run locally

```bash
dotnet run --project src/CacheOrchestrator.AdminConsole

# Publish
dotnet publish src/CacheOrchestrator.AdminConsole -c Release -o ./publish/admin
```

Requires a **.NET 10** runtime/SDK for this host.

- http://localhost:5188/ — UI  
- http://localhost:5188/health — process health  
- http://localhost:5188/scalar — OpenAPI UI (`MapOpenApi` + Scalar; `/scalar/v1` redirects here)

In Development, `Instances` point at the **Playground** sample (`:5289`), which also exposes `/metrics` for Prometheus.

**Pages:** Overview · Instances · Domains · Endpoints · **Metrics** · **Hints** · Operations · **Settings**

---

## Docker

Published image (GitHub Container Registry, on each GitHub Release):

```text
ghcr.io/cacheorchestrator/cacheorchestrator-admin-console:<version>
```

**Operator volume** (recommended):

```text
/app/data/rules/*.json     ← drop custom hint packs (optional)
/app/data/disabled.local.json  ← Settings UI (survives restart if volume is RW)
```

Mount your instance list as `appsettings.Production.json` (or use env vars). Full how-to, compose example, logging:

→ **[deploy/admin/README.md](../../deploy/admin/README.md)**

```bash
# Build from repo root
docker build -f src/CacheOrchestrator.AdminConsole/Dockerfile -t cacheorchestrator-admin-console:local .

docker run --rm -p 5188:8080 \
  -e AdminConsole__ApiKey=dev-admin-key \
  -v "$PWD/deploy/admin/appsettings.example.json:/app/appsettings.Production.json:ro" \
  -v "$PWD/deploy/admin/data:/app/data" \
  cacheorchestrator-admin-console:local
```

Logs go to **stdout** (`docker logs`). No log agent is bundled in the image.

---

## Recommendation hints

The Admin Console App evaluates **read-only rules** against aggregated live stats (and domain config). Results appear as severity badges, the **Hints** page, and header chips.

| Capability | |
|------------|--|
| Product defaults | `hints/core-hints.json` (always loaded) |
| **Your rules (Development)** | Extra JSON under `hints/` via `RuleFiles` |
| **Your rules (Production/Docker)** | Drop packs under `data/rules/` (volume) |
| No recompile | Drop a file, **Settings → Reload** |
| Disable codes | Settings checkboxes, or `DisabledCodes` in config |
| Inspect a rule | Settings → click a row → JSON definition |
| Compile errors | Settings shows an **ERROR** card with rule code + path inside the rule |
| Compile warnings | Settings **WARN** card (`badge` longer than 3 characters, or duplicate `badge`) — rules still load |

**Customization is the normal path** for team thresholds and extra checks—prefer a pack such as `team-ops.json` over editing `core-hints.json` (cleaner product upgrades).

### Minimal custom pack

```json
{
  "name": "team-ops",
  "rules": [
    {
      "code": "team-high-factory",
      "badge": "FA+",
      "severity": "Warning",
      "category": "Factory",
      "scope": "domain",
      "enabled": true,
      "when": {
        "all": [
          { "path": "domain.requests", "op": ">=", "value": 20 },
          { "path": "domain.dataCache.factoryShare", "op": ">=", "value": 0.30 }
        ]
      },
      "message": "Factory is {domain.dataCache.factoryShare:p1} on {domain.name}."
    }
  ]
}
```

- **Development:** save as `hints/team-ops.json` with `"RuleFiles": [ "hints/*.json" ]`.  
- **Docker:** save as `data/rules/team-ops.json` on the mounted volume.

Open **Settings**, confirm the group, then generate traffic and check **Hints**.

**Full operator handbook** (format, paths, ops, disable, design checklist):

→ **[hints/README.md](hints/README.md)**

Repo architecture notes: [docs/contributor/admin-hints.md](../../docs/contributor/admin-hints.md).

---

## Metrics (Prometheus)

Traffic KPIs, time-window domain and endpoint tables, impact analysis, charts, and recommendation inputs come from Prometheus through `AdminConsole:Metrics` and `GET /api/stats/window`. The Admin API is used for health, effective configuration, discovery, and operations.

Instance process-lifetime `GET …/stats` remains available for diagnostics but is not used by the statistics UI. Prometheus must scrape the `CacheOrchestrator` meter, including measurements such as `cache_orchestrator.dc.requests` and `cache_orchestrator.factory.duration`.

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

Use the [Quick start](#quick-start) for a ready-to-run stack. For additional Redis and multi-instance layouts, continue with the [Playground topology labs](../../samples/CacheOrchestrator.Sample/labs/README.md). Architecture and provider details are in the [Admin reference](../../docs/reference/admin.md).
