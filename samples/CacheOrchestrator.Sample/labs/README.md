# Playground topology labs (Docker Compose)

> **Guide (learn by running).** Orientation: [Guide — topologies](../../../docs/guide/topologies.md). Product overview: [root README](../../../README.md). Production wiring: [deployment.md](../../../docs/reference/deployment.md).

These numbered **Compose stacks** run the **Playground sample** together with Prometheus, Admin Console, Redis, single or multiple app instances, and the cluster bus. Use them to learn how cache layers fit together and how CacheOrchestrator ties them to domain model using simple configuration.

For a single process on your machine without Docker, use the [Playground sample](../README.md) alone.

| Stage | Compose file | Stack |
|-------|----------------|--------|
| **01** | [`compose/01-observability.yml`](compose/01-observability.yml) | Playground + Prometheus + Admin Console (InMemory) |
| **02** | [`compose/02-redis.yml`](compose/02-redis.yml) | Stage 01 + Redis as Fusion **L2** |
| **03** | [`compose/03-multi.yml`](compose/03-multi.yml) | **Two** playgrounds + shared Redis L2 |
| **04** | [`compose/04-bus.yml`](compose/04-bus.yml) | Stage 03 + **HTTP cluster bus** |
| **05** | [`compose/05-dual-redis-bus.yml`](compose/05-dual-redis-bus.yml) | **Two Redis** (OC store vs Fusion L2/backplane) + bus |
| **06** | [`compose/06-varnish-edge.yml`](compose/06-varnish-edge.yml) | Stage 02 + **Varnish Edge** with `xkey` invalidation |

---

## Table of Contents

- [Experiment (this is the point)](#experiment-this-is-the-point)
- [Stage 01 — Observability](#stage-01--observability)
- [Stage 02 — Redis as Fusion L2](#stage-02--redis-as-fusion-l2)
- [Stage 03 — Two playgrounds + shared Redis L2](#stage-03--two-playgrounds--shared-redis-l2)
- [Stage 04 — Cluster bus](#stage-04--cluster-bus)
- [Stage 05 — OC Redis + FC Redis + Cluster bus](#stage-05--oc-redis--fc-redis--cluster-bus)
- [Stage 06 — Varnish Edge](#stage-06--varnish-edge)
- [Lab vs production](#lab-vs-production)
- [Troubleshooting](#troubleshooting)
- [Where to read more](#where-to-read-more)

## Experiment (this is the point)

Each lab gives you a **running playground** (and Admin Console + Prometheus). Treat it as a sandpit:

1. Open the **Playground** UI: follow the **Getting started** tab, then try the focused **Vary** and **POST identity** playgrounds.
2. Click **Fetch** / **Search** more than once — watch badges and `X-CacheOrchestrator` (OC-HIT, DC-HIT, FACTORY, schedule phase, …).
3. Open **appsettings** in the UI, change a TTL, Version, or Client Cache Schedule value, save, then fetch again.  
4. Open **Admin Console** — Overview, Domains, Hints, Metrics — and relate numbers to what you just did.  
5. Move to the **next stage** when you want Redis, a second node, the bus, or a Varnish edge; the playground behaviour stays familiar, the **topology** changes.

There is no single “correct” click path. Change settings, invalidate, compare two URLs, then look at Admin Console. That loop is how the domain model becomes concrete.

**TTLs are playground-tuned on purpose.** Domain values are shorter than the [Getting started](../../../docs/guide/getting-started.md) values so you can **see expiry at each layer** without waiting minutes. They are still long enough to fetch a few times, read badges, and think. Domains differ by intent: **promotions** expires quickly, while **catalog**, **product-crud**, and **product-search** stay warmer so multi-step flows work. Open **appsettings** → `Cache:Domains` to inspect or change them.

Product data lives in **SQLite**. Stages **01–02** (and local `dotnet run`) use a file under the app’s `Data/` folder. Stages **03–05** point both playgrounds at `/shared/playground.db` on the same named volume as appsettings, so a price `PUT` on A is what B’s factory reads after cache invalidation — like replicas sharing a database. The labs still teach **cache and command** topology; SQLite is only the shared source of truth for demo products.

If behaviour looks surprising, see **[Troubleshooting](#troubleshooting)** (browser cache toggle, TTLs, keys, multi-instance gaps, …).

---

## Stage 01 — Observability

**Stack:** `playground` · `prometheus` · `admin`. OC InMemory · FC InMemory

<img src="../../../docs/assets/lab-01.svg" height="300" />

<br>

**Cache config:**
```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "InMemory" }
    },
    "Cluster": {
      "Bus": { "Enabled": false }
    }
  }
}
```

**Compose:** `compose/01-observability.yml`  

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/01-observability.yml up --build
```

| URL | |
|-----|--|
| Playground | http://localhost:5289 |
| Admin Console | http://localhost:5188 |

### In this stage

One app process with full **ops surface**: Admin API (health, config, invalidate; optional process-lifetime `/stats`), Prometheus (`/metrics` from the `CacheOrchestrator` meter), Admin Console (dashboard / fan-out / **Prom-only** stats & hints / impact). Caching itself is still InMemory — observability does not change how OC/FC store data. Rebuild images after library changes (`docker compose … up --build`) so Console and playground pick up OTEL instruments and window-stats BFF.

→ [observability.md](../../../docs/reference/observability.md) · [admin.md](../../../docs/reference/admin.md)

### When this layout fits

- Single instance (or first cloud deploy)  
- You want hit rates, factory share, schedule phase visibility  
- You are not ready for Redis

### What to try

1. Fetch until hits and misses are clear  
2. Shorten a domain TTL, save, fetch again  
3. Admin → **Overview** / **Hints** / **Metrics** (scrape ~5s)  

### Limits

- Cache dies with the process  
- A second replica would not see this process’s OC/FC entries  

### Clean Docker for next lab

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/01-observability.yml down -v
```

---

## Stage 02 — Redis as Fusion L2

**Stack:** Stage 01 + `redis`. OC InMemory · FC **Redis** (L2 + backplane on that Redis).

<img src="../../../docs/assets/lab-02.svg" height="300" />

<br>

**Cache config:**
```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "Redis" }
    },
    "Redis": {
      "Configuration": "redis:6379"
    },
    "Cluster": {
      "Bus": { "Enabled": false }
    }
  }
}
```

**Compose:** `compose/02-redis.yml`  

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/02-redis.yml up --build
```

| URL | |
|-----|--|
| Playground | http://localhost:5289 |
| Admin Console | http://localhost:5188 |

### In this stage

**L2** = Redis for Fusion objects (survives restart / later multi-node). OC stays InMemory on purpose — layers can use different providers. With one instance the backplane is quiet; registration already matches multi-node (Stage 03).

→ [backends.md](../../../docs/reference/backends.md) · [data-cache.md](../../../docs/reference/data-cache.md)

### When this layout fits

- You can lose HTTP response cache on recycle, but not expensive object rebuilds  
- Single-process rehearsal for a second replica  


### What to try

1. Fetch until **DC-HIT** / factory quiet  
2. `docker compose … restart playground`  
3. Fetch again — Fusion may hit **L2** without a factory  
4. Tweak Fusion soft/hard TTL; compare Admin metrics before/after restart  

### Limits

- Still one app process  
- OC is not shared (by design here)  

### Clean Docker for next lab

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/02-redis.yml down -v
```

---

## Stage 03 — Two playgrounds + shared Redis L2

**Stack:** `playground-a`, `playground-b`, `redis`, `prometheus`, `admin`. OC InMemory per process · FC Redis shared.

<img src="../../../docs/assets/lab-03.svg" height="300" />

<br>

**Cache config:**
```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "Redis" }
    },
    "Redis": {
      "Configuration": "redis:6379"
    },
    "Cluster": {
      "Bus": { "Enabled": false }
    }
  }
}
```

**Compose:** `compose/03-multi.yml`  

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/03-multi.yml up --build
```

| URL | |
|-----|--|
| Playground A | http://localhost:5289 |
| Playground B | http://localhost:5290 |
| Admin Console | http://localhost:5188 (both instances listed) |

### In this stage

Two processes, one Redis L2. **Shared:** Fusion L2 objects + Fusion **backplane** (L1 invalidation), domain policy JSON, and **SQLite** products (`/shared/playground.db`). **Not shared:** InMemory OC bodies and process-local overlays.

**Bus is off on purpose.** Redis L2 + backplane ≠ full multi-node consistency:

| After invalidate on A | On B |
|-----------------------|------|
| **Fusion** | L1 cleared via backplane; refill from L2 (or factory if L2 purged) |
| **Output Cache (InMemory)** | **Not** cleared — B can still **OC-HIT** the old body until OC TTL or invalidate **on B** |

That gap is real multi-instance behaviour with OC InMemory and no command bus. Stage **04** adds the bus; Stage **05** shares OC via Redis.

→ [deployment.md](../../../docs/reference/deployment.md) · [cache-keys.md](../../../docs/reference/cache-keys.md)

### When this layout fits

- Multiple replicas; shared object cache is the main win  
- HTTP cache can stay node-local (or short OC TTL / OC Redis later)  

### What to try

1. Warm on **A** past factory → same path on **B**: expect **OC-MISS**, **FC** from L2 (no second factory)  
2. Warm **both** so each has its own OC entry  
3. Invalidate on A only → fetch **B**: often still **OC-HIT** (stale OC body)  
4. Getting started: `PUT` price on **A**, then `GET` on **B** (no extra GET on A) — factory on B returns the new price from shared SQLite  
5. Admin **Instances** — two rows; change TTL on A’s UI → both nodes load shared policy  
6. When you want OC cleared on every node too → **Stage 04**  

### Limits

- **No bus** — process-local OC / Version / TTL overlays not pushed to peers (see **04**)  
- OC not shared across nodes (see **05**)  

### Clean Docker for next lab

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/03-multi.yml down -v
```

---

## Stage 04 — Cluster bus

**Stack:** Stage 03 + **`Cache:Cluster:Bus` enabled** (static peers)

<img src="../../../docs/assets/lab-04.svg" height="300" />

<br>

**Cache config:**
```json
{
  "Cache": {
    "OutputCache": { "Provider": "InMemory" },
    "DataCacheInstances": {
      "default": { "Provider": "Redis" }
    },
    "Redis": {
      "Configuration": "redis:6379"
    },
    "Cluster": {
      "Bus": {
        "Enabled": true,
        "Membership": "Static",
        "ApiKey": "dev-admin-key",
        "Static": {
          "Instances": [
            { "Id": "playground-a", "Url": "http://playground-a:8080" },
            { "Id": "playground-b", "Url": "http://playground-b:8080" }
          ]
        }
      }
    }
  }
}
```

**Compose:** `compose/04-bus.yml`  

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/04-bus.yml up --build
```

| URL | |
|-----|--|
| Playground A | http://localhost:5289 |
| Playground B | http://localhost:5290 |
| Admin Console | http://localhost:5188 (both instances listed) |

### In this stage

Redis L2 + backplane handle **data / Fusion L1**. The **bus** carries **commands** (invalidate, Version, TTL patch) — critical for process-local state such as InMemory OC and runtime overlays. 

→ [cluster-bus.md](../../../docs/reference/cluster-bus.md) · [deployment.md](../../../docs/reference/deployment.md)

### When this layout fits

- Several instances with InMemory OC or local overlays  
- Admin Console bus-distribute mode  
- Policy actions must hit **all** nodes immediately  

### What to try

1. Admin → Operations / distribution (bus capability)  
2. Invalidate or Version with distribute → both A and B  
3. Compare badges before/after; contrast Stage 03 (no bus)  

### Limits

- Still one Redis for Fusion  
- OC still InMemory (shared OC → Stage **05**)  

### Clean Docker for next lab

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/04-bus.yml down -v
```

---

## Stage 05 — OC Redis + FC Redis + Cluster bus 

**Stack:** 2× playground · **redis-oc** · **redis-fc** · bus · prometheus · admin.

<img src="../../../docs/assets/lab-05.svg" height="300" />

<br>

**Cache config:**
```json
{
  "Cache": {
    "OutputCache": {
      "Provider": "Redis",
      "Redis": { "Configuration": "redis-oc:6379" }
    },
    "DataCacheInstances": {
      "default": {
        "Provider": "Redis",
        "Redis": { "Configuration": "redis-fc:6379" }
      }
    },
    "Cluster": {
      "Bus": {
        "Enabled": true,
        "Membership": "Static",
        "ApiKey": "dev-admin-key",
        "Static": {
          "Instances": [
            { "Id": "playground-a", "Url": "http://playground-a:8080" },
            { "Id": "playground-b", "Url": "http://playground-b:8080" }
          ]
        }
      }
    }
  }
}
```

**Compose:** `compose/05-dual-redis-bus.yml`  

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/05-dual-redis-bus.yml up --build
```

| URL | |
|-----|--|
| Playground A | http://localhost:5289 |
| Playground B | http://localhost:5290 |
| Admin Console | http://localhost:5188 (both instances listed) |

| Redis service | Role in this lab |
|---------------|------------------|
| **redis-oc** | Output Cache **distributed store** (shared HTTP responses) |
| **redis-fc** | Fusion **L2** + **backplane** |

### In this stage

| Concern | Mechanism |
|---------|-----------|
| Share **full responses** | OC Redis → `redis-oc` |
| Share **objects** / skip factory | FC L2 → `redis-fc` |
| Drop remote **L1** | Fusion backplane on `redis-fc` |
| Apply **commands** everywhere | HTTP bus |
| Share **product rows** (demo DB) | SQLite → `/shared/playground.db` |
| Operate | Admin Console + Prometheus |

Shared OC store ≠ bus: Redis OC shares **payloads**; the bus still distributes **commands** (Version/TTL overlays, Admin distribute, other process-local state). Two Redis instances in the lab so you **see** separate roles — production may use one Redis (keys/DBs) or two (isolation). Per-layer connection strings: `OutputCache:Redis` vs `DataCacheInstances:…:Redis`.

Host/port vary is off in multi-instance labs (same note as Stage 03).

→ [deployment.md](../../../docs/reference/deployment.md) · [backends.md](../../../docs/reference/backends.md) · [cluster-bus.md](../../../docs/reference/cluster-bus.md)

### When this layout fits

- Multiple web nodes with shared HTTP + object cache  
- Fast L1 invalidation + consistent commands  

### What to try

1. Warm on A until **OC-HIT** → same route on B → **OC-HIT** from shared Redis  
2. Invalidate; both nodes stay coherent  
3. Getting started: `PUT` on A → `GET` on B → new price from shared SQLite after miss/factory  
4. Admin distribute / bus for Version or domain invalidate  
5. Optional: `redis-cli` on host **6380** (OC) vs **6381** (FC)  

### Limits

- One region / one compose network  
- Topology teaching, not HA Redis ops  

### Clean Docker for next lab

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/05-dual-redis-bus.yml down -v
```

---

## Stage 06 — Varnish Edge

**Stack:** Stage 02 + `varnish`. Varnish Edge · OC InMemory · FC Redis L2/backplane.

<img src="../../../docs/assets/lab-06.svg" height="370" />

<br>

**Edge config:**

```json
{
  "Cache": {
    "EdgeInstances": {
      "varnish": {
        "Provider": "Varnish",
        "Varnish": {
          "PurgeUrl": "http://playground/cache-orchestrator/purge",
          "ApiKey": "dev-edge-key",
          "ApiKeyHeaderName": "X-CacheOrchestrator-Key"
        }
      }
    },
    "DomainDefaults": {
      "Edge": {
        "Enabled": true,
        "Instance": "varnish",
        "TtlSeconds": 30,
        "StaleWhileRevalidateSeconds": 10
      }
    }
  }
}
```

**Compose:** `compose/06-varnish-edge.yml`

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/06-varnish-edge.yml up --build
```

| URL | |
|-----|--|
| Playground | http://localhost:5289 |
| Admin Console | http://localhost:5188 |

### In this stage

Varnish stores only responses explicitly marked cacheable by `CacheOrchestrator.Edge.Varnish`. The origin supplies opaque `xkey` tags and edge-only TTL/grace headers; the mounted VCL consumes those headers, hides them from clients, and reports the result with standard `Cache-Status` values. Domain and entity invalidation use protected `PURGE` requests carrying `xkey-purge`.

The badges show the request path from outermost to innermost cache. `EDGE-HIT` means a fresh cached response completed the request. `EDGE-REFRESH` means cached content was served through Varnish grace while the object was stale and refresh work involved the origin. `EDGE-MISS` is followed by the applicable origin badges, such as `OC-HIT` or `OC-MISS`. The exact Varnish status remains available in the badge tooltip.

The origin application is internal to the Compose network. All browser traffic goes through the Playground service on `:5289`, which runs Varnish.

→ [Edge cache guide](../../../docs/guide/edge.md) · [invalidation.md](../../../docs/reference/invalidation.md)

### What to try

1. Keep **Disable browser HTTP cache** enabled and open the Edge URL on `:5289`.
2. Fetch promotions twice. The first response shows **EDGE-MISS** followed by the server cache path; the second shows **EDGE-HIT**.
3. Use **Invalidate domain** for `promotions`, wait about a second for the edge queue, then fetch through `:5289`: expect a miss followed by another edge hit.
4. Repeat the product update flow. Entity invalidation removes the tagged Varnish object as well as the server-side entries.
5. Open **POST identity** and submit the same search twice. The second request can be an origin Output Cache hit, but no Edge badge is shown: Edge storage is limited to `GET` and `HEAD`.

### Limits

- Plain local HTTP, a lab-only API key, one Varnish process, and no TLS or hardened purge ACL.
- The Varnish image and VCL are pinned for a reproducible exercise, not offered as a production deployment template.
- The edge invalidation queue is in-memory and best-effort, as described in the Edge guide.

### Clean Docker

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/06-varnish-edge.yml down -v
```

---

## Lab vs production

These stacks are **teaching environments**, not blueprints for a production edge. Real multi-instance setups usually look different around the apps:

| In production (typical) | In these labs (on purpose) |
|-------------------------|----------------------------|
| Reverse proxy / load balancer (HAProxy, nginx, cloud LB) in front of several identical nodes | Stages **01–05** publish each playground directly; Stage **06** adds one Varnish edge in front of one origin |
| One public URL; you rarely care which instance handled the request | Open **A** and **B** in separate browser tabs (`:5289` / `:5290`) so you can **see each instance alone** (hits, factory, local L1) |
| TLS, auth at the edge, private networks | Plain HTTP on localhost |
| Managed Redis, HA, monitoring as a platform concern | Single-container Redis (or two) for clarity |
| **Same cache policy on every node** (shared config / ConfigMap / vault, rolling deploy) | Stages **03–05**: both nodes share one Docker **named volume** (`/shared/appsettings.json`, seeded from `appsettings.seed.json`); entrypoint symlinks it to `/app/appsettings.json`. **Instance id** stays per process (`Cache__InstanceId`) |
| Shared application database behind every replica | Stages **03–05**: same volume also holds `/shared/playground.db` (`Sample__SqlitePath`); Getting started + CRUD product APIs share that file |
| Config change process is controlled (pipeline, not two writers racing a JSON file) | Lab volume is shared RW — fine for demos; last save wins if you edit A and B at once |

**Why separate browser URLs for each instance?**  
With a load balancer you would not know which process answered. Here the goal is to compare **playground-a** vs **playground-b** side by side: warm A, fetch B, invalidate on one side, watch Admin rows per `instance_id`. That is harder if traffic is round-robined through one URL.

**Why shared settings on multi-instance labs?**  
In production you must not run different TTLs or Versions on different replicas of the same app. The lab keeps **one shared policy file** so a UI save on A is what B loads too. On stages **03–05** a Docker **named volume** holds that file (seeded from `config/0N/appsettings.seed.json`); both playgrounds use it, the peer reloads within about a second after Save. Topology (Redis, bus, providers) stays in read-only `playground.Production.json`.

**Why shared SQLite on 03–05?**  
So product `PUT`/`GET` across A and B matches real replicas (shared DB + cache invalidation), without teaching people to keep traffic on one node. `down -v` resets both policy and the product DB.

Reset policy/DB to seed / leave a stage: each stage ends with **Clean Docker for next lab** (`down -v`).  
Normal rebuild (keep edited policy and product DB on stages 03–05): `docker compose -f …/0N-….yml up --build -d` — no `-v`.

Other simplifications exist for the same reason: **focus on cache behaviour** (OC / Fusion / client headers, backplane, bus, Admin). The labs omit production-grade networking, certificate management, auto-scaling, and hardened Redis. After the cache story is clear, map the same ideas onto your real proxy, cluster, and ops stack — CacheOrchestrator’s domain model does not require the lab’s simplified front door.

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| `Bind for 0.0.0.0:9090` (or `5289` / `5290` / `5188` / `6379`) **failed: port is already allocated** | Another lab still holds the port. Run that stage’s **Clean Docker** command (`down -v`) first. Only one lab stack at a time. |
| Admin instance Down | Playground healthy? ApiKey `dev-admin-key`? |
| Metrics empty | Traffic generated? Wait ~5–10s scrape; Prometheus targets UP? |
| Redis connection errors | Stage config uses service name `redis` / `redis-oc` / `redis-fc`, not `localhost` inside containers |
| Settings back to defaults after `down` (stages 01–02) | Expected for single-node labs |
| Multi-lab settings still changed after `down` (no `-v`) | Named volume keeps policy **and** SQLite products — use `down -v` then `up` to re-seed policy and demo DB |
| Product price wrong across A/B after PUT | Rebuild playground image so SQLite store is present; stages **03–05** need `Sample__SqlitePath=/shared/playground.db` and writable `/shared` |
| POST identity tab: unknown domain `product-search` / options miss | Stages **03–05** shared volume may predate that domain — `down -v` then `up --build` to re-seed. Stages **01–02**: rebuild playground image so baked `appsettings.json` includes `product-search`. |
| `Error response from daemon: open /var/lib/docker/tmp/...` on `up --build` | Compose must mount `/shared` as a **directory** (file subpath mounts break on Docker Desktop); labs use a directory mount plus an entrypoint symlink. Run `down -v`, then `up --build -d`. |
| Bus not distributing | Stages 04–05 only; peers use Docker DNS URLs in lab config |
| No **BROWSER-CACHE**, or always server hits | Header toggle **Disable browser HTTP cache** is **on by default** (for server OC/FC demos). Uncheck only when you want client `max-age` / BROWSER-CACHE. The Playground uses a per-request echo to distinguish browser responses from cached responses whose old `X-CacheOrchestrator` header says `oc=hit`. |
| No Edge status badge in Stage 06 | Fetch the same cacheable GET route twice through `http://localhost:5289`; inspect `Cache-Status`; check `docker compose ... logs playground origin`. |
| OC-HIT then sudden MISS / FACTORY | Domain **TTL** expired — check `OutputCache.TtlSeconds` vs `DataCache.TtlSeconds` / `fusionCache.hardTtlSeconds` for that domain in `Cache:Domains` |
| Always FACTORY, never hits | Domain disabled? Wrong endpoint/domain? Keys differ (query, host — multi-lab keys note in Stage 03)? |
| Client headers not what you expect | `ClientCache.TtlSeconds` / schedule / **Disable browser HTTP cache** (Fetch uses `no-store` when on) |
| A vs B disagree on **cache** badges | Topology (bus off, OC InMemory local) — not a TTL bug; see Stages 03–05 |
| A vs B disagree on **product price** after factory | Should not happen on 03–05 with shared SQLite; confirm both use `/shared/playground.db` |
| appsettings Save: `cat` in container is new, HTTP GET / UI still old | Demo control routes must use `NoStore` so `/api/demo/appsettings` is not served from Output Cache — rebuild the playground image if badges look stale after Save. |
| A Save applies on A; B settings UI shows new JSON but Fetch still old Version/TTL | B reads the file for the editor, but runtime options need a config **reload**. Sample polls the shared volume (~1s) and reloads; rebuild playground image. Stage 03 has no bus — file share + poll is intentional. |

---

## Where to read more

Labs stay short on purpose: stages cover **topology**; use the sample README and technical docs for **how a feature works**.

| Topic | Start here |
|-------|------------|
| Playground UI, badges, CRUD, POST identity, Client Cache Schedule, host `dotnet run` | [Sample README](../README.md) · [POST identity](../README.md#post-identity-playground) |
| Endpoint cache identity (contracts, content-hash) | [cache-identity.md](../../../docs/reference/cache-identity.md) |
| Which layout / package | [Guide — topologies](../../../docs/guide/topologies.md) |
| Docs index (getting started, config, OC/FC, invalidation, ops) | [docs/README.md](../../../docs/README.md) |
| Product overview | [root README](../../../README.md) |
| Deployment / multi-instance / Redis / bus | [deployment.md](../../../docs/reference/deployment.md), [cluster-bus.md](../../../docs/reference/cluster-bus.md) |
| Admin API + Admin Console | [admin.md](../../../docs/reference/admin.md) · [Guide — operations](../../../docs/guide/operations.md) |
| Observability (`X-CacheOrchestrator`, metrics) | [observability.md](../../../docs/reference/observability.md) |
| Edge cache, Varnish VCL, tag invalidation | [edge.md](../../../docs/guide/edge.md) |
| Cache keys (host/port vary, query params) | [cache-keys.md](../../../docs/reference/cache-keys.md) |

