# Cache keys

> **Reference** — Namespace and lookup key composition for Output Cache and Data Cache.

How the **Data Cache** and Output Cache decide that two requests are the **same** resource. That is lookup identity. Eviction — tags and Version — is [invalidation.md](invalidation.md).

- **Namespace** — isolates applications that share Redis: `my-app` becomes `my-app-oc` (Output Cache) and `my-app-dc` (Data Cache default instance suffix).
- **Domain** — the policy group (`products`, `product-detail`).
- **Request material** — what varies inside a domain: path, route, query, host, encoding, resource id, and optional **endpoint cache identity** (`co-id:*`).

**Tags** (`domain:{name}`, `entity:{domain}:{entityKind}:{id}`) group entries for purge. They are not part of lookup. A tag can delete an entry whose key never contains the domain name.

```
Lookup  → key identity (this document)
Purge   → tags + optional Version bump ([invalidation.md](invalidation.md))
Policy  → Core + HTTP domain snapshots resolved before store ([configuration.md](configuration.md))
```

## Table of Contents

- [Namespace (top-level classifier)](#namespace-top-level-classifier)
- [Data Cache keys](#data-cache-keys)
- [Core API keys](#core-api-keys)
- [Output Cache keys](#output-cache-keys)
- [Side-by-side](#side-by-side)
- [Tracking query parameters](#tracking-query-parameters)
- [Design rationale (summary)](#design-rationale-summary)
- [Custom Data Cache keys](#custom-data-cache-keys)

## Namespace (top-level classifier)

Root config:

```json
{
  "Cache": {
    "Namespace": "my-app"
  }
}
```

Default: `app-cache`. Effective store prefixes:

| Target | Effective name | Override |
|--------|----------------|----------|
| Output Cache | `{Namespace}-oc` | `OutputCache.Namespace` |
| Data Cache **default** instance | `{Namespace}-dc` | `DataCacheInstances.default.Namespace` |
| Data Cache **named** instance `pii` | `{Namespace}-dc-pii` | `DataCacheInstances.pii.Namespace` |

**Purpose:** isolate multiple applications or environments that share the same Redis (or other L2) so keys and backplane channels do not collide. Namespace is **not** a domain and is **not** per-endpoint.

### Where Namespace appears

| Surface | Used? | How |
|---------|-------|-----|
| **Output Cache key** | Yes | `CacheKeyPrefix` = effective Output Cache namespace |
| **Output Cache Redis store** | Yes | `InstanceName` = effective Output Cache namespace |
| **Data Cache provider key** (`DefaultDomainKeyGenerator`) | **No** | Key is `co3:{escapedDomain}:{versionHex}:u:{hash}` or `:e:{hash}` only |
| **Fusion `CacheKeyPrefix`** | Yes | Effective Data Cache namespace + `:` (e.g. `my-app-dc:`) on every named Fusion instance |
| **HybridCache key and tags** | Yes | The provider prefixes both with the effective default Data Cache namespace + `:` |
| **Fusion Redis L2** | Yes (via Fusion) | Redis `InstanceName` is **not** set; Fusion prefix is the single isolation layer (do not also set `InstanceName` to the same namespace) |
| **Fusion backplane** | Yes | Channel prefix `{dcNamespace}:backplane` |

HTTP Data Cache provider keys use the Version-stamped form `co3:{escapedDomain}:{versionHex}:u:{hash}` (URL-shaped) or `…:e:{hash}` (entity-shaped). The escaped domain makes segment boundaries unambiguous; Namespace is still applied by the provider (Fusion `CacheKeyPrefix` / backplane or the Hybrid provider prefix), not inside the CO key string. Core API keys keep a caller-supplied logical suffix (no `u`/`e` marker); see [Core API keys](#core-api-keys).

---

## Data Cache keys

### Who builds them

`IDomainDataCache.GetOrSetAsync` → `IDomainKeyGenerator.Generate(context)`.

Default implementation: **`DefaultDomainKeyGenerator`** (XxHash3 over request material, then a short string key).

Replace with a custom `IDomainKeyGenerator` when you must vary on dimensions the default ignores (tenant claim, custom header). See [Data Cache](data-cache.md#custom-key-generator).

### Logical form

| Mode | Key shape |
|------|-----------|
| URL-shaped (default) | `co3:{escapedDomain}:{versionHex}:u:{hash}` |
| Entity / resource id | `co3:{escapedDomain}:{versionHex}:e:{hash}` |

Examples:

```text
co3:products:a1b2c3d4e5f60708:u:7f3e9c1a2b4d6e08
co3:store:a1b2c3d4e5f60708:e:9c8b7a6d5e4f3210
```

- **`domain`** — normalized domain name from the Core `DomainCacheOptions` (explicit partition).
- **`versionHex`** — stable hex of the domain `Version` stamp. Bumping `Version` changes every new key for that domain; old entries age out by TTL.
- **`u` / `e`** — shape marker (`u` = URL-shaped, `e` = entity-shaped).
- **`hash`** — 16 hex chars of XxHash3 over the material below (for entity keys that includes `entityKind` + `resourceId`).

### What goes into the hash

#### URL-shaped keys (no resource id)

| Input | Included when | Notes |
|-------|---------------|--------|
| Route pattern + route parameter values | Endpoint is a `RouteEndpoint` | Pattern text + each route value with value casing preserved |
| Path | No route endpoint | Full path |
| HTTP method | Always (URL-shaped only) | Upper-invariant (`GET`, `POST`, …). Prevents GET/POST (and other methods) from sharing one Data Cache entry on the same URL. Entity-shaped keys omit the method. |
| Query string | Per `VaryByQueryKeys` / `IgnoreQueryKeys` | Default: all non-tracking keys sorted; tracking params excluded (`utm_*`, `gclid`, `fbclid`, …) |
| `Accept-Encoding` | `DataCache.VaryOnEncoding` | Domain setting |
| `Accept` / `Accept-Language` | `VaryByAccept` / `VaryByAcceptLanguage` | Optional normalization lists (default: raw header) |
| Extra headers / cookies | `VaryByHeaders` / `VaryByCookies` | Sensitive values hashed; see [vary.md](vary.md) |
| Auth-user / claims | `AuthBypassMode: Never` (or claim list) + `VaryOutputCacheByUser` | Not applied under default auth-bypass modes (key stability) |
| Scheme + host | `DataCache.VaryOnPublicAddress` | Domain setting |
| `ICacheVaryContributor` values | When registered | After built-in material |
| Endpoint cache identity | Identity metadata on the endpoint and material resolved for this method | Sorted `co-id:{name}` segments from `CacheIdentityMaterial` (named contract or content-hash). Absent / Url-only identity adds nothing. See [cache-identity.md](cache-identity.md). |

Order of query keys does not matter: `?a=1&b=2` and `?b=2&a=1` produce the same hash.
Value casing remains significant for path, route, query, header, and custom vary values. String components are UTF-8 length-prefixed, and multi-value query/header inputs include an explicit count, so boundaries cannot collapse (`["a,b"]` is different from `["a", "b"]`). Protocol-insensitive material such as scheme, host, and header names is canonicalized separately.

#### Entity keys (`GetOrSetEntityAsync`)

Primary kind and resource id come from the request (`[CacheDomain]` / `CacheOutputWithDomain` with `resourceRouteKey`, or `SetEntityIdentity`).

| Input | Included |
|-------|----------|
| Normalized `entityKind` + opaque `resourceId` | Always (hash material only; lookup string is `:e:{hash}`) |
| Accept-Encoding / scheme+host | Same flags as above |
| Path / query / route | **Not** used for the key |

`GetOrSetEntitySetAsync` and URL-shaped `GetOrSetAsync` temporarily ignore stamped entity identity while building the key so a request that also has primary kind/id (from Output Cache or `SetEntityIdentity`) still gets a path/query key. Entity tags for collections come from `EntitySet`, not from the lookup key.

An unusable kind or id (null/whitespace) does **not** become `default`; invalidators skip it. Resource ids are otherwise opaque, so punctuation such as `!!!`, separators, and case are preserved. GUIDs are the exception and use canonical lowercase `D` format. `GetOrSetEntityAsync(http, factory)` throws if identity is missing on the request.

Use entity keys for CRUD-style resources so invalidation can target `entity:{domain}:{entityKind}:{id}` without depending on the full URL shape. Wire the matching Output Cache route with `resourceRouteKey` **and** `entityKind`, or kind-only for collections ([invalidation.md](invalidation.md#wiring-entity-tags)).

### Why domain is in the Data Cache provider key

`IDomainDataCache` / `ICacheOrchestrator` accept an explicit domain (or resolve one per request). The same `HttpContext` can theoretically touch different domains, and domain is the primary policy partition (TTL, instance, Version). Embedding domain in the CacheOrchestrator provider key (`co3:…`) guarantees:

```text
GetOrSetAsync(http, "products", …)  →  co3:products:…
GetOrSetAsync(http, "catalog",  …)  →  co3:catalog:…
```

never share an entry even if path and query are identical. Fusion/Hybrid still apply their own instance namespace outside this key.

### Tags (Data Cache)

Every stored entry is also tagged `domain:{name}` (and `entity:{domain}:{entityKind}:{id}` + `entitykind:{domain}:{entityKind}` when entity identity was used). Each tag segment is percent-encoded independently, so `:` or `/` inside an opaque id cannot change tag structure. Tags are for `ICacheOrchestratorInvalidator`, not for lookup.

---

## Core API keys

The HTTP-free `ICacheOrchestrator` does not have a request from which to derive route, query, header, or user material. Its caller supplies a stable logical key:

```csharp
await cache.GetOrCreateAsync(
    new CacheEntryRequest
    {
        Domain = "catalog",
        Key = $"product:{productId}"
    },
    factory,
    cancellationToken);
```

The default physical key is:

```text
co3:{escapedDomain}:{versionHex}:{logicalKey}
```

For product `42`, that might be `co3:catalog:a1b2c3d4e5f60708:product:42`. FusionCache still adds its configured instance namespace outside this key. Public Core callers always supply a logical key; CacheOrchestrator owns the domain and Version prefix.

Core and HTTP keys deliberately have different lookup shapes. They coordinate invalidation through the same domain and entity tags. Full API: [Core API](core-api.md).

---

## Output Cache keys

### Who builds them

ASP.NET Core Output Caching builds the **final** store key. CacheOrchestrator configures **prefix + vary rules** in `DomainOutputCachePolicy` so that material aligns with domain policy.

The library does **not** emit a single custom string of the form `{domain}:{version}:{hash}`. Conceptually the lookup identity is:

```text
{outputNamespace} + method + path + host + query + data-version
  [+ Accept-Encoding] [+ auth-user]
```

| Piece | Source in policy |
|-------|------------------|
| Prefix | `CacheKeyPrefix` = `OutputCacheNamespace` (from the root or Output Cache `Namespace`) |
| Method + path | Framework Output Cache |
| Host | `OutputCache.VaryByHost` (default `true`) |
| `Accept` / language / headers / cookies / query allowlists | Domain vary settings | Shared materializer — [vary.md](vary.md) |
| Query | `CollectQueryKeysForOutputCache` — `VaryByQueryKeys` / `IgnoreQueryKeys` plus tracking prefixes stripped |
| Version | `VaryByValues["data-version"]` = `VersionHex` |
| Encoding | `Accept-Encoding` in header vary when present |
| Auth user | `VaryByValues["auth-user"]` when authenticated traffic is cached and `VaryOutputCacheByUser` is true |
| Endpoint cache identity | `VaryByValues["co-id:{name}"]` from `CacheIdentityMaterial` when a named contract or content-hash binding produced values. Url identity (default GET/HEAD, or `CacheIdentities.Url`) adds no `co-id:*` entries. |

### Tags (Output Cache)

| Tag | When |
|-----|------|
| `domain:{name}` | Every cached Output Cache entry for that domain |
| `entity:{domain}:{entityKind}:{id}` | When **both** `resourceRouteKey` and `entityKind` are set on the policy/attribute **and** the route value resolves |
| `entitykind:{domain}:{entityKind}` | Same writes as the entity tag |

Output Cache stamps early tags in `CacheRequestAsync` (domain; primary entity when the route id resolves; `entitykind` when a kind is set without an id). The Data Cache path stages an `EntityFootprint` on the request; `ServeResponseAsync` merges members, dependencies, and aliases into Output Cache tags before storage.

---

## Side-by-side

| | Data Cache | Output Cache |
|--|------------|--------------|
| **Logical shape** | `co3:{escapedDomain}:{versionHex}:u\|e:{hash}` | Framework key from prefix + vary |
| **Namespace** | Logical key: no; Fusion `CacheKeyPrefix` + backplane: yes (`-dc`) | Yes (`CacheKeyPrefix`) |
| **Domain in key** | Yes | No (tag only) |
| **Version in key** | Yes (`versionHex`) | Yes (`data-version` vary) |
| **Route / path** | In hash (unless entity id mode) | Path in framework key |
| **Query** | In hash (no tracking) | `QueryKeys` (no tracking) |
| **Entity id** | Optional key mode + entity tag | Entity tag via `resourceRouteKey` |
| **Builder** | `DefaultDomainKeyGenerator` | `DomainOutputCachePolicy` + ASP.NET Output Cache |

### Same request, two layers

```text
GET /api/products/42?page=1&utm_source=ads
Domain: product-detail
Version: v1
```

| Layer | Identity (conceptually) |
|-------|-------------------------|
| Data Cache (URL-shaped) | `co3:product-detail:{versionHex}:u:{hash(route id=42, query page=1)}` — `utm_source` ignored |
| Data Cache (entity-shaped, kind `products`, id `42`) | `co3:product-detail:{versionHex}:e:{hash(kind+id(+vary))}` |
| Output Cache | prefix `…-oc` + path `/api/products/42` + query `page` + host + `data-version` — `utm_source` ignored |
| Tags | `domain:product-detail`; with Output Cache `resourceRouteKey` + `entityKind`: also `entity:product-detail:products:42` and `entitykind:product-detail:products` |

---

## Tracking query parameters

Both Data Cache and Output Cache **exclude** known marketing and tracking parameters from key material, so cache hit rates stay high when only a tracker changes.

Implementation: shared helper used by `DefaultDomainKeyGenerator` and `DomainOutputCachePolicy` (e.g. `utm_*`, `gclid`, `fbclid`). Business query parameters (`id`, `page`, `sort`, …) remain part of the key.

---

## Design rationale (summary)

1. **Namespace** isolates **applications** on shared infrastructure; it is not a substitute for domain.  
2. **Domain** is the unit of policy and purge. The Data Cache embeds it in the key because the data API is domain-first. Output Cache typically binds one fixed domain per route, so path + tags suffice; a **domain name template** (or resolver) needs an extra vary dimension so each selected name partitions correctly.
3. **Version** partitions **generations** inside a domain without mass-deleting keys (prefer bump + TTL expiry for bulk cutovers).  
4. **Request material** (path, query, host, encoding, optional identity) partitions **resources** inside a generation.  
5. **Tags** answer “what to delete,” not “what to return on GET.”  
6. **Entity id mode** gives stable Data Cache identity and entity-level invalidation for CRUD without encoding the full URL into the key.
7. **Endpoint cache identity** is a per-method binding on the route, not domain vary configuration. It folds stable contract or body-hash material into Output Cache `VaryByValues` and the Data Cache hash when present; see [endpoint cache identity](cache-identity.md).

---

## Custom Data Cache keys

Implement `IDomainKeyGenerator` when default material is insufficient (multi-tenant claim, non-URL locale, etc.):

- Deterministic: same inputs → same key  
- No secrets in the key (may land in Redis and logs)  
- Prefer wrapping `DefaultDomainKeyGenerator` and appending a short suffix  

See [Data Cache](data-cache.md#custom-key-generator).

---

## Related

- [Guide — concepts](../guide/concepts.md) — domain, layers, and identity overview  
- [Data Cache](data-cache.md) — `GetOrSetAsync`, domain resolution, custom generator  
- [Output Cache](output-cache.md) — policy, auth vary, `resourceRouteKey`  
- [cache-identity.md](cache-identity.md) — `co-id:*` material in keys  
- [invalidation.md](invalidation.md) — Version, domain/entity tags  
- [configuration.md](configuration.md) — `Namespace`, domain `Version`, vary flags  
- [architecture.md](../contributor/architecture.md) — request flow  
- [domain-profiles.md](../guide/domain-profiles.md) — snapshot vs dynamic / CRUD profiles  

