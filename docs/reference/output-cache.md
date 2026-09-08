# Output Cache

> **Reference** — Output Cache policy, endpoint binding, headers, auth, and tags.

Output Cache stores the **full HTTP response**. By default that means **GET** and **HEAD** with Url identity (path, query, host, domain vary). Other methods are not cached by Output Cache unless the endpoint opts in with **[endpoint cache identity](cache-identity.md)**. CacheOrchestrator applies ASP.NET Core Output Caching per **domain**: TTL, tags, vary rules, `Cache-Control`, and ETag all come from that domain.

## Table of Contents

- [Register](#register)
- [Base policy and endpoints without a domain](#base-policy-and-endpoints-without-a-domain)
- [Minimal APIs](#minimal-apis)
- [Domain name templates](#domain-name-templates)
- [Controllers](#controllers)
- [Policy](#policy)
- [Endpoint cache identity](#endpoint-cache-identity)
- [Authenticated traffic](#authenticated-traffic)
- [Headers](#headers)

## Register

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
app.UseCacheOrchestrator();
```

`UseCacheOrchestrator` calls `UseOutputCache`. For a Redis store, install `CacheOrchestrator.Redis` and call `AddRedisBackend()` — see [backends.md](backends.md).

## Base policy and endpoints without a domain

ASP.NET Core Output Caching is **policy-driven**. CacheOrchestrator registers a **base policy of `NoCache`**: responses are **not** cached unless an endpoint opts in.

| Endpoint | Output Cache |
|----------|----------------|
| `.CacheOutputWithDomain("…")` / `[CacheDomain("…")]` | **Yes** — domain policy (TTL, tags, Client Cache headers, diagnostics; encoding vary from domain settings) |
| No domain metadata | **No** — base `NoCache` |
| Explicit `.CacheOutput(p => p.NoCache())` / `OutputCacheAttribute { NoStore = true }` | **No** — redundant with the base policy, still fine on Admin / metrics / ops routes |

**Without `.CacheOutputWithDomain` / `[CacheDomain]`, there is no Output Cache entry.** You do not need a separate `.NoCache()` / `NoStore` for that. Built-in Admin and sample `/metrics` may still set `NoStore` explicitly; that is harmless.

Data Cache is separate: `IDomainDataCache` / `ICacheOrchestrator` still need a domain (endpoint metadata, explicit overload, or `CacheDomainContext`) or the factory runs uncached — see [FAQ](../guide/faq.md#data-cache-runs-uncached--why).

## Minimal APIs

```csharp
using CacheOrchestrator.OutputCache;

app.MapGet("/api/products", () => /* ... */)
   .CacheOutputWithDomain("products");

app.MapGet("/api/products/{id}", () => /* ... */)
   .CacheOutputWithDomain("store", resourceRouteKey: "id", entityKind: "products");
```

`resourceRouteKey` and `entityKind` tag the Output Cache entry so `InvalidateEntityAsync` can purge that row.

## Domain name templates

A **domain name template** selects which **configured** domain applies to the request. The profile of each selected domain stays in `Cache:Domains`.

Use a string template when the same endpoint serves a finite set of named domains:

```csharp
group.MapGet("{tileset}/{z:int}/{x:int}/{y:int}.{ext?}", HandleTileAsync)
     .CacheOutputWithDomainTemplate("tiles-{route:tileset}");
```

```json
{
  "Cache": {
    "Domains": {
      "tiles-satellite": { "Version": "1" },
      "tiles-osm": { "Version": "1" },
      "tiles-vehicle": { "Version": "1" }
    }
  }
}
```

The template resolves `{route:tileset}` to `tiles-satellite`, `tiles-osm`, or `tiles-vehicle`. It never creates a domain at request time. An empty or unknown name fails closed: the handler still runs, Output Cache and request-scoped Data Cache are bypassed, the response gets `Cache-Control: no-store`, and diagnostics use `domain=_` rather than echoing the unresolved value.

Templates also support `{host}`, `{header:Name}`, `{query:key}`, and application-defined `{custom:key}` providers; see [Extensibility](extensibility.md#domain-template-token-providers).

For arbitrary selection logic, use a short resolver instead of a string template:

```csharp
app.MapGet("/api/t/{tenant}/items/{id}", (string tenant, string id) => /* ... */)
   .CacheOutputWithDomain(
       http => $"tenant-{http.Request.RouteValues["tenant"]}",
       resourceRouteKey: "id",
       entityKind: "items");
```

List every name the template or resolver may return under `Cache:Domains` (shared defaults can live in `DomainDefaults`).

## Controllers

```csharp
using CacheOrchestrator.OutputCache;

[CacheDomain("products")]
public class ProductsController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(/* ... */);

    [HttpGet("{id:int}")]
    [CacheDomain("store", resourceRouteKey: "id", entityKind: "products")]
    public IActionResult Get(int id) => Ok(/* ... */);
}
```

The action attribute overrides the controller. `AddCacheOrchestrator` registers `CacheDomainConvention`, which attaches `DomainOutputCachePolicy` when `[CacheDomain]` is present.

If a Minimal endpoint only has the attribute in metadata:

```csharp
app.MapGet(...).CacheOutputWithDomainAttribute();
```

## Policy

`DomainOutputCachePolicy` decides lookup and store:

| Condition | Result |
|-----------|--------|
| No identity metadata and not GET/HEAD | No output caching |
| Identity metadata present, method has no binding | No output caching |
| Identity contract / content-hash returns null (or body oversize) | Bypass this request |
| Request `Cache-Control: no-store` | Bypass; client `no-store` |
| Auth signal matches `AuthBypassMode` (default `AuthenticatedOrAuthorization`) | Bypass; client blocked |
| `AuthBypassMode: Never` | Cache allowed; optional `auth-user` vary |
| `OutputCache.Enabled: false` | Off (`oc=off`); Client Cache headers still apply. This is not the same as request-level bypass (auth or `no-store`). |
| Enabled | Lookup and store, locking, TTL from the domain |
| Status not in `CacheableStatusCodes` | No store |
| `Set-Cookie` or response `Authorization` | No store; client blocked |
| Signed-in user and `ClientCache.Cacheability: Public` while `ClientCache.ForcePrivateWhenAuthenticated` is `true` | Client header forced to **private** |

**Vary:** host, query keys (tracking omitted; optional allow/deny lists), `Accept-Encoding`, `Accept` by default, optional `Accept-Language` / headers / cookies, and `data-version` from `Version`. When authenticated traffic is cached and `VaryOutputCacheByUser` is `true`, the policy also adds **`auth-user`**. Endpoint identity material is folded into Output Cache `VaryByValues` as `co-id:*`; see [cache keys](cache-keys.md) and [endpoint cache identity](cache-identity.md). The complete domain vary matrix is in [domain vary dimensions](vary.md).

**Tags:** `domain:{name}`. If `resourceRouteKey` and `entityKind` resolve, also `entity:{domain}:{entityKind}:{id}` and `entitykind:{domain}:{entityKind}`.

**ETag:** domain `ETagMode` — `Version` (generation), `Resource` (per URL or id), `None`. See [domain-profiles.md](../guide/domain-profiles.md).

## Endpoint cache identity

Without identity bindings, Output Cache applies to **GET/HEAD** with Url identity. For read-only POST (search / GraphQL), custom GET keys, or Url identity on POST, bind identity per method.

Full concept, rules, cheat sheet, and mixed Minimal API / MVC DX examples: **[cache-identity.md](cache-identity.md)**.

```csharp
using CacheOrchestrator.Identity;

app.MapPost("/graphql", ...)
   .CacheOutputWithDomain("graphql")
   .WithContentHashCacheIdentity(["POST"], maxBodyBytes: 65_536);
```

## Authenticated traffic

By default any signed-in user or `Authorization` header skips Output Cache (`AuthBypassMode: AuthenticatedOrAuthorization`). That is the safe setting for mixed public and private APIs.

- **AuthBypassMode** — `Never`, `AuthenticatedIdentityOnly`, `AuthorizationHeaderOnly`, or `AuthenticatedOrAuthorization`.
- **VaryOutputCacheByUser** (default `true`) — when you allow authenticated caching, partition entries by user, selected claims, or an Authorization hash.
- **DataCacheRespectAuthBypass** (default `true`) — Data Cache also skips when Output Cache would bypass. Set it to `false` only when the object cached by Data Cache is shared between callers.

The following examples are domain entries placed under `Cache:Domains`.

**Private dashboard (per-user server cache):**

```json
{
"user-dashboard": {
  "AuthBypassMode": "Never",
  "VaryOutputCacheByUser": true,
  "ClientCache": {
    "Cacheability": "Private",
    "TtlSeconds": 60
  },
  "OutputCache": {
    "TtlSeconds": 30
  }
}
}
```

Alice and Bob both call `GET /api/me/summary`. With no `VaryByAuthClaims`, the server stores two entries from the authenticated identity name (`auth-user=u:alice` and `u:bob`). If you set `VaryByAuthClaims`, include a per-user claim (for example `sub`) — a tenant-only list shares one entry across users. The browser may cache privately for 60 seconds. A shared CDN must not treat the response as public.

**Public tiles with an API key (one shared entry):**

```json
{
"osm-tiles": {
  "AuthBypassMode": "Never",
  "VaryOutputCacheByUser": false,
  "TreatAuthorizationAsAuthSignal": false,
  "ClientCache": {
    "Cacheability": "Public",
    "TtlSeconds": 86400
  },
  "OutputCache": {
    "TtlSeconds": 3600
  }
}
}
```

Clients send `Authorization: Bearer <map-key>` for rate limits or billing. The body is the same for everyone. Use this only when the payload does not depend on the caller.

See [vary.md](vary.md), [configuration.md](configuration.md), and [domain-profiles.md](../guide/domain-profiles.md).

## Headers

On response start the policy sets:

- **Cache-Control** — from `ClientCacheHeaderGenerator`, including the [Client Cache Schedule](../guide/client-cache-schedule.md) ramp when `ScheduledUpdateUtc` is set.
- **X-CacheOrchestrator** — domain, client, output (and data / ms when Output Cache missed). Written when `Cache:EmitDiagnosticsHeaders` is `true` (the default). See [observability.md](observability.md).

## Related

- [Guide — concepts](../guide/concepts.md) — domain model and the three cache layers  
- [vary.md](vary.md) — shared vary matrix for Output Cache and Data Cache  
- [cache-keys.md](cache-keys.md) — Output Cache prefix, vary, and Version  
- [cache-identity.md](cache-identity.md) — per-method identity bindings (`WithCacheIdentity`, content-hash)  
- [configuration.md](configuration.md) — `OutputCache` / `ClientCache` domain settings  
- [invalidation.md](invalidation.md) — tags, Version, entity wiring  
- [observability.md](observability.md) — `X-CacheOrchestrator`, metrics, health
