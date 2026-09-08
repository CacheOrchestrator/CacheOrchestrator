# Cluster command bus (`CacheOrchestrator.HttpBus`)

> **Reference** — HttpBus membership, commands, delivery, and peer HTTP.

When several instances must apply the same invalidate, Version, or TTL change, this package delivers those **commands** over HTTP. It does not move cache payloads. Peers run the same local purge or overlay they would have run if the call had been made on that process.

Package README: [src/CacheOrchestrator.HttpBus/README.md](../../src/CacheOrchestrator.HttpBus/README.md). See also [invalidation.md](invalidation.md), [deployment.md](deployment.md), [admin.md](admin.md), [configuration.md](configuration.md).

---

## Table of Contents

- [When to use it](#when-to-use-it)
- [Install](#install)
- [Register](#register)
- [Configuration](#configuration)
- [Membership](#membership)
- [Commands](#commands)
- [HTTP endpoints](#http-endpoints)
- [Admin Console App interaction](#admin-console-app-interaction)
- [Bus vs Redis backplane](#bus-vs-redis-backplane)
- [Observability](#observability)
- [Zero effect / performance](#zero-effect-performance)
- [Security checklist](#security-checklist)

## When to use it

| Situation | Prefer |
|-----------|--------|
| Multi-instance **InMemory** Output Cache and Data Cache, with immediate purge required everywhere | **HttpBus** |
| Runtime **Version / TTL** overlays on all InMemory nodes | **HttpBus** + Admin `distribute` (or Admin Console App auto mode) |
| Shared Redis L2 + backplane | **`CacheOrchestrator.Redis`** (or Fusion Redis leaf) — `CacheOrchestrator.HttpBus` optional / redundant for tag invalidate |
| Sticky sessions + TTL-only expiry | Local invalidation may be enough |
| Single instance | Do not install HttpBus (or leave `Enabled: false`) |

**HttpBus carries commands, not cache values.** Peers re-run local tag purge / override apply.

```text
Origin: Invalidate* / Admin distribute
   → local apply
   → IClusterCommandBus.PublishAsync → ClusterPublishResult (per-peer)
        → membership peers (except self)
        → POST {peer}{prefix}/cluster/apply
             → ApplyLocal only (no re-publish)
```

Peer HTTP/timeout failures are reported in `ClusterPublishResult` (not swallowed). Admin mutations with `distribute: true` return **409** when any peer failed; local apply may already have succeeded (`localApplied: true`).

---

## Install

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.HttpBus --prerelease
```

## Register

```csharp
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.HttpBus;

builder.Services.AddCacheOrchestrator(builder.Configuration, o =>
{
    o.AddHttpClusterBus();
});

var app = builder.Build();
app.UseRouting();
app.UseCacheOrchestrator();
app.MapCacheOrchestratorHttpBus();
```

Receive endpoints are independent of the Admin API. Call `AddHttpClusterBus()` inside the `AddCacheOrchestrator` builder callback so it registers before the core Null defaults.

| API | Assembly / namespace |
|-----|----------------------|
| `AddHttpClusterBus` | `CacheOrchestrator.HttpBus` |
| `MapCacheOrchestratorHttpBus` | `CacheOrchestrator.HttpBus` |
| `IClusterCommandBus` / `IClusterMembership` / `IClusterCommandHandler` | `CacheOrchestrator.Cluster` (core; Null by default) |
| `IInstanceIdProvider` | `CacheOrchestrator.Cluster` |

Call `AddHttpClusterBus()` **inside** the `AddCacheOrchestrator` builder callback so it registers before core `TryAdd` Null defaults.

---

## Configuration

Root identity and bus options live under **`Cache`**:

```json
{
  "Cache": {
    "Namespace": "app1",
    "InstanceId": "app1-a",
    "Cluster": {
      "Bus": {
        "Enabled": true,
        "Membership": "Static",
        "PeerTimeoutMs": 2000,
        "MaxParallelism": 32,
        "DedupeWindowSeconds": 330,
        "CommandMaxAgeSeconds": 300,
        "ClockSkewSeconds": 30,
        "ApiKey": "use-a-strong-secret",
        "Static": {
          "Instances": [
            { "Id": "app1-a", "Url": "http://10.0.0.1:8080" },
            { "Id": "app1-b", "Url": "http://10.0.0.2:8080" }
          ]
        },
        "ServiceDiscovery": {
          "ServiceName": "app1",
          "DefaultScheme": "http",
          "CacheSeconds": 15
        }
      }
    },
    "Admin": {
      "Enabled": false,
      "ApiKey": "…",
      "RoutePrefix": "/cache-admin/local"
    }
  }
}
```

### Options reference (`Cache:Cluster:Bus`)

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `false` | Master switch; when false, `IClusterCommandBus.IsEnabled` is false (no peer calls) |
| `Membership` | `Null` | `Null` · `Static` · `ServiceDiscovery` |
| `PeerTimeoutMs` | `2000` | Per-peer HTTP timeout |
| `MaxParallelism` | `32` | Max concurrent peer deliveries |
| `DedupeWindowSeconds` | `330` | Receive-side `CommandId` window; must be at least `CommandMaxAgeSeconds + ClockSkewSeconds` |
| `ApiKey` | empty | Auth for receive endpoints; falls back to `Cache:Admin:ApiKey`; required when the bus is enabled unless `AllowUnauthenticated` is true |
| `AllowUnauthenticated` | `false` | Explicitly allow open receive endpoints; isolated development networks only |
| `CommandMaxAgeSeconds` | `300` | Reject commands older than this; valid range 1–86400 |
| `ClockSkewSeconds` | `30` | Accept commands this many seconds into the future; valid range 0–3600 |
| `Static.Instances` | `[]` | `{ Id, Url }` peers (Static membership) |
| `ServiceDiscovery.ServiceName` | empty | Logical service name for SD |
| `ServiceDiscovery.DefaultScheme` | `http` | Scheme when building peer URLs / query |
| `ServiceDiscovery.CacheSeconds` | `15` | In-process peer list cache |

### `Cache:InstanceId`

Single process identity for Admin, bus anti-echo, and diagnostics.  
When empty, the machine name is used. Process identity lives at `Cache:InstanceId`.

### `Cache:Namespace`

Isolation boundary for keys **and** cluster commands. Namespace mismatch → **409**. Origin-is-self (anti-echo) → **200** `{ applied: false, reason: "origin-is-self" }`.  
Namespace is **not** membership discovery — do not mix app1 and app2 peers in one Static list.

---

## Membership

| Kind | Package | Behaviour |
|------|---------|-----------|
| **Null** | core | Empty peers; local only |
| **Static** | Bus | Peer URLs from `Cluster:Bus:Static` |
| **ServiceDiscovery** | Bus | `Microsoft.Extensions.ServiceDiscovery` (config, platform DNS, Aspire, …) |

### Static (VM / IIS / simple LAN)

List every instance **of this app**. `Id` should match each process’s `Cache:InstanceId` so the origin can skip itself.

### ServiceDiscovery (K8s / Aspire / config endpoints)

```json
{
"Membership": "ServiceDiscovery",
"ServiceDiscovery": {
  "ServiceName": "app1",
  "DefaultScheme": "http"
}
}
```

Host configuration should expose endpoints, for example:

```json
{
  "Services": {
    "app1": {
      "http": [ "10.0.0.1:8080", "10.0.0.2:8080" ]
    }
  }
}
```

Bare names are normalized to `http://{ServiceName}` so the configuration endpoint provider matches (scheme-required query).  
Peer `Id` values are synthetic (`app1-0`, …); anti-echo still uses `OriginInstanceId` on receive.

---

## Commands

Core command records are transport-independent semantic operations. `CacheOrchestrator.HttpBus` maps them to an internal, versioned JSON envelope with `protocolVersion: 1` and a `commandType` discriminator:

| `commandType` | Type | ApplyLocal effect |
|---------------|------|-------------------|
| `invalidate` | `InvalidateCommand` | Domain / entity / tags via invalidator |
| `versionBump` | `VersionBumpCommand` | Runtime Version overlay |
| `settingsPatch` | `SettingsPatchCommand` | Sparse runtime overlay (same shape as Admin `PATCH …/domains/{d}/settings`) |

Never carries response bodies or cache entries.

Every command also carries the common envelope below:

| Field | Contract |
|-------|----------|
| `protocolVersion` | HTTP wire protocol version; v1 receivers reject unsupported versions |
| `commandId` | Globally unique id used for receive-side deduplication |
| `originInstanceId` | Stable origin process id; receivers use it for anti-echo |
| `namespace` | Cache isolation boundary; a mismatch is rejected |
| `timestampUtc` | UTC creation time |
| `correlationId` | Optional cross-process trace correlation |

`InvalidateCommand` adds `kind`, human-readable `scope`, final `tags`, and optional domain/entity fields. `VersionBumpCommand` adds `domain` and `version`. `SettingsPatchCommand` adds `domain` and the same sparse setting dictionary accepted by the Admin PATCH endpoint.

For a custom transport or discovery integration, implement the Core contracts rather than reusing the HTTP JSON: `IClusterMembership` discovers `ClusterPeer` records, `IClusterCommandBus` publishes and returns per-peer outcomes, and the receive path calls `IClusterCommandHandler.ApplyLocalAsync` and honors its `ClusterCommandResult`. Define and version that transport's own wire contract, preserve semantic command metadata, report individual peer failures in `ClusterPublishResult`, and never re-publish a received command. See [Extensibility](extensibility.md#cluster-contracts).

### Who publishes

| Entry point | Publish? |
|-------------|----------|
| `ICacheOrchestratorInvalidator.Invalidate*` | **Yes** when bus enabled (unless remote/local-only scope) |
| Admin `POST …/invalidate` | Only if body `distribute: true` |
| Admin `POST …/domains/{d}/version` | Only if body `distribute: true` |
| Admin `PATCH …/domains/{d}/settings` | `distribute: true` → `settingsPatch` |
| Peer `POST …/cluster/apply` | **Never** (ApplyLocal only) |

Internally, command application uses two scopes:

- **Remote** — receive path; suppresses re-publish  
- **LocalOnly** — Admin without distribute; local apply only  

---

## HTTP endpoints

Base path = `Cache:Admin:RoutePrefix` (default `/cache-admin/local`), **even if Admin is disabled**.

| Method | Path | Role |
|--------|------|------|
| `POST` | `…/cluster/apply` | ApplyLocal command body |
| `GET` | `…/cluster/info` | instance id, namespace, bus enabled, membership, peer count. When **Admin is enabled**, Admin API maps this route (Bus does not duplicate it). When Admin is off, Bus maps it. |

### Auth

Header **`X-CacheOrchestrator-Admin-Key`** (same as Admin API):

1. `Cache:Cluster:Bus:ApiKey` if set  
2. Else `Cache:Admin:ApiKey`  
3. Else startup validation fails, unless `AllowUnauthenticated: true` explicitly opens the endpoints

Authentication uses a constant-time key comparison. Receive handling also rejects commands older than `CommandMaxAgeSeconds` or further in the future than `ClockSkewSeconds`; `CommandId` deduplication then blocks repeated delivery inside its window. Prefer TLS plus a private network or mTLS in front of instances for production.

### Partial failure

The receiver returns **400** for rejected commands and **503** for incomplete local invalidation; neither is reported as applied. Successful execution returns **200** with `status: "Applied"`. A completed duplicate returns **200** with `applied: false`, `succeeded: true` and `status: "AlreadyApplied"`. The response includes invalidation details or settings purge errors. A settings command may return `localMutationApplied: true` with a failure: its overlay was published, but required purging is unfinished.

Concurrent deliveries of the same command share the in-flight execution. Running work does not expire while it is executing. Failed or canceled work can be retried with the same immutable command and ID; successful outcomes are retained for the deduplication window measured from completion. A canceled duplicate waiter does not cancel the owner. For settings, the receiver retains the original purge plan and resumes only unfinished layers, preserving any newer runtime mutation. This state is process-local and bounded by the window; it is not a durable delivery log, a distributed transaction or an ordering guarantee across restarts.

The bus performs one delivery attempt per peer and reports peer errors in `ClusterPublishResult` and `publish_failures`. It does not automatically retry failures. Callers that retry must retain the original command ID and payload and remain within the timestamp freshness window. Caller cancellation propagates; a peer timeout is a failed peer outcome. Admin distribution returns **409** for incomplete peer delivery after local mutation. A local post-settings purge failure returns **503** with the published result and local errors; repair the failed backend and explicitly invalidate the affected domain if retrying through a new Admin request.

Settings publication and capture of its invalidation plan are serialized with other management/cluster settings mutations for the same domain. Network invalidation runs after releasing that control-plane lock. Cache-hit paths are unaffected.

---

## Admin Console App interaction

The Admin Console App probes `GET …/cluster/info` on configured instances (`GET /api/distribution`):

| Capability | Write mode |
|------------|------------|
| No bus | **fan-out** — HTTP to each target, `distribute: false` |
| Bus enabled | **bus-distribute** — one healthy origin, `distribute: true` |

Never combine full Admin Console App fan-out **and** `distribute: true` for the same action — the Admin Console App chooses one path.  
Operations UI shows mode banner + last-run summary.

Details: [Admin — cluster distribution](admin.md#cluster-distribute-with-cacheorchestratorhttpbus).

---

## Bus vs Redis backplane

| Concern | Redis L2 + backplane | HTTP Bus |
|---------|----------------------|----------|
| Shared object store | Yes (L2) | No |
| L1 drop on other nodes after tag remove | Yes (pub/sub) | Yes (command → local purge) |
| InMemory multi-instance | Not applicable | Primary tool |
| Runtime Version/TTL overlay cluster-wide | Not covered | Yes |
| Hot-path read latency | L2 cost | No read path cost |

Using **both** for tag invalidation is safe (the duplicate purge is idempotent) but often unnecessary for Fusion when the Redis backplane is already enabled. The bus remains useful with InMemory Output Cache for Version, TTL, and **settings** overlays. The Redis backplane does **not** distribute runtime overlays.

---

## Observability

Meter: **`CacheOrchestrator`**

| Instrument | Description |
|------------|-------------|
| `cache_orchestrator.cluster.commands_published` | Origin publish attempts (`command_type` is the **CLR name**: `InvalidateCommand`, `VersionBumpCommand`, `SettingsPatchCommand`) |
| `cache_orchestrator.cluster.commands_received` | Receive path entered |
| `cache_orchestrator.cluster.commands_applied` | ApplyLocal success |
| `cache_orchestrator.cluster.publish_failures` | Per-peer failure (`reason`: `http_status` / `timeout` / `transport` / `exception`) |
| `cache_orchestrator.cluster.command_dedupe_hits` | Duplicate `CommandId` within window |

Logs: command type, namespace, origin, commandId (Debug for ignore paths; Warning for peer failures).

See [observability.md](observability.md).

---

## Zero effect / performance

| Condition | Behaviour |
|-----------|-----------|
| Bus package not referenced | Null bus only |
| Package present, no `AddHttpClusterBus()` | Null bus |
| `Enabled: false` | No peer HTTP |
| GetOrSet / Output Cache policy | Never touch bus |

When enabled: `IHttpClientFactory`, parallel peer posts, per-peer timeout, capped parallelism, small JSON commands.

---

## Security checklist

> [!IMPORTANT]
> - [ ] Set `Cluster:Bus:ApiKey` or `Admin:ApiKey`; never use `AllowUnauthenticated` on a reachable production network
> - [ ] Restrict peer HTTP to private networks / mesh
> - [ ] Use TLS or mTLS between peers
> - [ ] Treat apply endpoints as admin-level (can purge cache)
> - [ ] Do not expose Admin API / cluster routes on the public internet without auth

---

## Related

- [Guide — topologies](../guide/topologies.md) — Bus vs Redis backplane  
- [invalidation.md](invalidation.md) — multi-instance strategies  
- [deployment.md](deployment.md) — topologies  
- [admin.md](admin.md) — Admin API + Admin Console App  
- [configuration.md](configuration.md) — options tables  
- [backends.md](backends.md) — `CacheOrchestrator.Redis` and leaf Redis packages  
- [Extensibility](extensibility.md) — custom membership, command bus, and host identity contracts
- [architecture.md](../contributor/architecture.md) — layout  
