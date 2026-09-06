# FAQ

> **Guide** — common mistakes and symptom-oriented answers.

These are short answers to common mistakes and boundary questions. Follow the linked guide or reference page for the complete model.

## Table of Contents

- [Hits, misses, and domain resolution](#hits-misses-and-domain-resolution)
- [Authentication and request variation](#authentication-and-request-variation)
- [Freshness, invalidation, and ETags](#freshness-invalidation-and-etags)
- [Packages and topology](#packages-and-topology)
- [Admin and multi-instance operations](#admin-and-multi-instance-operations)
- [Output Cache methods and identity](#output-cache-methods-and-identity)
- [Product boundaries](#product-boundaries)

## Hits, misses, and domain resolution

### Why is a route cached when I never set a domain?

CacheOrchestrator does not cache an endpoint with Output Cache merely because the services and middleware are registered. Its base Output Cache policy is `NoCache`.

Full-response caching requires one of these:

- `.CacheOutputWithDomain(...)`;
- `[CacheDomain(...)]`;
- another explicit ASP.NET Core Output Cache policy added by the application.

If an unmarked route still appears cached, check for host-level `AddBasePolicy`, `.CacheOutput(...)`, reverse-proxy or CDN caching, and the browser's own cache.

See [Output Cache](../reference/output-cache.md#base-policy-and-endpoints-without-a-domain).

### Data Cache runs uncached — why?

`IDomainDataCache` needs a resolved domain. It looks in this order:

1. an explicit domain overload such as `GetOrSetAsync(http, "catalog", factory)`;
2. a domain snapshot already attached to the request;
3. endpoint metadata from `.CacheOutputWithDomain(...)` or `[CacheDomain]`;
4. otherwise, it runs the factory uncached.

The unresolved path produces a Warning, `result=unresolved` metrics, and typically `dc=unresolved` in `X-CacheOrchestrator`.

For the normal HTTP path, put the domain on the endpoint. For a route that uses only Data Cache, pass the domain explicitly or call `EnsureDomainOptions`.

See [Concepts — resolved snapshot](concepts.md#one-request-uses-one-resolved-snapshot) and [Data Cache](../reference/data-cache.md).

### Why do I not see a Data Cache hit on the second request?

An Output Cache hit returns the complete HTTP response before the endpoint runs. The Data Cache is therefore not consulted, and `dc` is omitted from `X-CacheOrchestrator`.

To inspect Data Cache behaviour, temporarily disable Output Cache for that domain, wait for its entry to expire, or make a request whose Output Cache identity misses while the Data Cache identity still matches.

The normal sequence is:

```text
first request:  oc=miss; dc=miss; fa=run
second request: oc=hit
later Output Cache miss:  oc=miss; dc=hit
```

### Why does the response still look cached after I invalidate it?

First identify the layer:

- a browser may still hold a response until its `max-age` ends;
- an Edge purge may still be queued, retrying, or failed, or the CDN may not have a configured CO Edge integration;
- another application instance may have local Output Cache without HttpBus;
- the invalidated entry may lack the expected entity footprint;
- a conditional request may receive `304 Not Modified` from a generation-bound ETag;
- the write may have invalidated before the database transaction committed, allowing old data to refill the cache.

Reproduce with Client Cache disabled, inspect `X-CacheOrchestrator`, and verify the topology and invalidation result. See [Operations](operations.md#use-a-short-incident-checklist) and [Invalidation](../reference/invalidation.md).

### Should I expose `X-CacheOrchestrator` in production?

It is useful in development and staging, but it exposes domain names, hit/miss state, schedule phase, and timing to clients.

Set `Cache:EmitDiagnosticsHeaders` to `false` for public production endpoints when that information should remain internal. Metrics, traces, logs, `Cache-Control`, and ETags continue to work.

See [Operations](operations.md#read-one-request-with-x-cacheorchestrator) and [Observability](../reference/observability.md).

## Authentication and request variation

### What happens to authenticated requests by default?

An authenticated identity or an `Authorization` header triggers the safe default:

- Output Cache bypasses the request;
- Client Cache is blocked;
- the Data Cache also bypasses by default through `DataCacheRespectAuthBypass: true`.

This prevents a shared cache from accidentally serving one user's response to another.

### How do I cache private per-user responses?

This is an explicit opt-in. A typical starting shape is:

```json
{
  "AuthBypassMode": "Never",
  "VaryOutputCacheByUser": true,
  "ClientCache": {
    "Cacheability": "Private"
  }
}
```

Also include any claims that change the representation, such as tenant id or role. Review the complete [Vary and authenticated traffic](../reference/vary.md) matrix before enabling it.

### What about a public API that uses an Authorization header as an API key?

The default bypass still applies because the header is an auth signal. If the response is truly public and identical for every key, set `AuthBypassMode: Never` deliberately and ensure no user-specific value affects the response.

Do not disable auth bypass merely to improve hit rate. Confirm the security boundary first.

### Why is Data Cache also bypassed under Authorization?

`DataCacheRespectAuthBypass` defaults to `true` so Output Cache and Data Cache make the same safety decision. Set it to `false` only when the endpoint response cannot be shared but the underlying cached object intentionally can be.

### JSON and XML share one URL. How do I keep them separate?

Enable `VaryByAccept: true`, optionally with `AcceptNormalizationList`. Output Cache and Data Cache then use the normalized media type as vary material.

### How do I vary by tenant claim?

Set `AuthBypassMode: Never`, enable user variation, and add the claim name to `VaryByAuthClaims`, or register an `ICacheVaryContributor` for a custom identity rule.

See [Vary](../reference/vary.md).

### What happens to tracking query parameters?

Known campaign keys such as `utm_*`, common click ids, `_ga`, `_ga_*`, `_gl`, and `_gl_*` are removed from cache identity so analytics parameters do not fragment entries. They remain available to application code on the request.

Other query parameters vary the domain endpoint by default. See [Vary](../reference/vary.md) for the exact list and customization options.

## Freshness, invalidation, and ETags

### When should I change Version instead of invalidating?

Change `Version` when the whole domain moves to a new coordinated generation: a monthly dataset, a schema change, or a large catalog release.

Invalidate an entity when one logical record changes under the same generation. Invalidating the whole domain for every row update creates unnecessary cold-cache events.

See [Domain profiles](domain-profiles.md).

### ETag stays the same after entity invalidation. Is that a bug?

No. CacheOrchestrator-generated ETags are generation-bound, not body hashes.

- `ETagMode: Version` changes when the domain `Version` changes.
- `ETagMode: Resource` is distinct per resource but is still derived from domain `Version`.
- `ETagMode: None` emits no CacheOrchestrator ETag.

For dynamic resources changing under a stable version, use `None` or implement an application-owned ETag and conditional request flow based on a row version or timestamp.

See [Domain profiles — ETag policy](domain-profiles.md#choose-an-etag-policy-deliberately).

### Does Client Cache Schedule change server TTLs?

No. `ScheduledUpdateUtc`, `ClientCache.TtlSeconds`, and `TtlMinSeconds` affect only browser/CDN `Cache-Control`.

Output Cache, Data Cache, and FusionCache engine TTLs remain independent. See [Client Cache Schedule](client-cache-schedule.md).

### Can server invalidation purge a browser or CDN?

**Browser: no.** Server invalidation cannot remove a response already stored in the browser; it may be reused until its current `max-age` expires.

**CDN/Edge: yes, with the optional Edge integration configured for the domain.** CO projects the same domain/entity tags and queues provider-specific invalidation for Cloudflare or Varnish. A custom tag-native provider can use the same extension points. Without that integration, use the CDN's own purge mechanism.

Edge purge runs asynchronously. The built-in queue batches requests and retries transient failures, but is in-memory and best-effort. `CacheInvalidationResult.Succeeded` reports local Data Cache/Output Cache success, not completion of the external purge. See [Edge cache integration](edge.md) for setup and delivery behaviour.

Choose client TTLs based on the maximum acceptable client staleness. Use Client Cache Schedule for known snapshot cutovers.

If your application needs to refresh sooner, see [Client cache busting and invalidation](client-invalidation.md) for domain version cache busting and entity synchronization.

### Why did an EF Core bulk update not invalidate anything?

The EF interceptor observes tracked entries after a successful `SaveChanges`. `ExecuteUpdate`, `ExecuteDelete`, and similar bulk operations do not create those ChangeTracker entries.

After a bulk operation, call `InvalidateEntitiesAsync`, `InvalidateEntityKindAsync`, or another appropriate invalidation explicitly. See [EF Core invalidation](../reference/ef-core-invalidation.md).

### Should I invalidate before or after saving?

After the write commits successfully. Invalidating first creates a race in which another request can reload and cache the old value before the transaction completes.

If invalidation fails after the commit, the database is still authoritative. Inspect `CacheInvalidationResult`, alert on partial failures, and rely on bounded TTLs as a safety net while retrying or reconciling.

## Packages and topology

### Which package should a typical ASP.NET Core app install?

Start with the `CacheOrchestrator` meta package. It combines `CacheOrchestrator.AspNetCore` and `CacheOrchestrator.FusionCache`.

Use focused packages when you need Output Cache only, `CacheOrchestrator.HybridCache` instead of `CacheOrchestrator.FusionCache`, or an HTTP-free `CacheOrchestrator.Core` dependency in a reusable library. See [Packages](packages.md).

### Why does `Provider: Redis` fail during startup?

Configuration selects a registered provider; it does not install one. Add the matching Redis package and registrar:

- `CacheOrchestrator.Redis` + `AddRedisBackend()` for Redis Output Cache and Fusion L2;
- `CacheOrchestrator.AspNetCore.Redis` + `AddRedisOutputCacheBackend()` for Output Cache only;
- `CacheOrchestrator.FusionCache.Redis` + `AddRedisFusionCacheBackend()` for Fusion L2 only.

`CacheOrchestrator.Redis.Shared` is transitive support and should not be installed alone.

### Redis backplane or HttpBus—which do I need?

| Need | Use |
|------|-----|
| Share FusionCache L2 values and clear peer L1 entries | Redis L2 + backplane |
| Share Output Cache responses | Redis Output Cache store |
| Purge in-memory Output Cache on peer nodes | HttpBus |
| Coordinate several in-memory nodes without Redis | HttpBus |
| Distribute runtime Version/TTL/settings overlays | HttpBus or Admin Console App fan-out |

HttpBus carries commands, not cache values. Using it alongside the Redis backplane is safe but can be redundant for Fusion tag invalidation. See [Topologies](topologies.md).

### Can domains use different Redis connections?

Yes. Define named `DataCacheInstances`, give each its own Redis configuration, and select one with `DataCache.Instance` in the domain.

Use separate instances for a real infrastructure boundary such as PII isolation, region, or workload capacity. Domains already isolate keys and tags, so most applications do not need one Redis connection per domain.

See [Named Data Cache instances](topologies.md#named-data-cache-instances-isolate-workloads).

### What does Namespace do?

Root `Cache:Namespace` separates applications sharing the same backend. By default, Output Cache and each named Data Cache instance derive their own store namespace from it.

It is not a domain and does not replace entity identity. Keep it stable for one deployed application and distinct between unrelated applications.

### Can I use SQL Server, Memcached, or another custom backend?

Yes, but the extension point depends on what the storage system does:

- implement `IOutputCacheBackendRegistrar` for an ASP.NET Core Output Cache store;
- implement `IFusionCacheBackendRegistrar` for FusionCache L2 storage or a backplane;
- implement `IDataCacheProvider` only for a complete Data Cache engine.

The same provider name may have separate registrars for Output Cache and Fusion L2. `IOutputCacheBackendRegistrar` does not configure `DataCacheInstances`. `AddRedisBackend` / `AddRedisFusionCacheBackend` apply to Fusion, not HybridCache.

> With HybridCache as the Data Cache engine, configure distributed storage through Microsoft HybridCache / `IDistributedCache` (outside CacheOrchestrator) — there is no Hybrid-specific backend registrar;

See [Cache backends](../reference/backends.md), [Data Cache — HybridCache](../reference/data-cache.md#hybridcache-provider), and the complete [extensibility catalog](../reference/extensibility.md).

## Admin and multi-instance operations

### What is the difference between Admin API and Admin Console App?

- **Admin API** is an opt-in route group inside each application process. It exposes health, effective domains, invalidation, and runtime settings operations.
- **Admin Console App** is a separate application that discovers and controls the Admin API on each instance through one UI.

Traffic charts in the Admin Console App use Prometheus. The Admin Console App has no built-in user login, so protect it with private networking and external authentication.

See [Operations](operations.md#choose-the-admin-surface) and [Admin](../reference/admin.md).

### Why did an Admin setting change affect only one instance?

Runtime overlays are local unless the operation is distributed.

- With HttpBus, use `distribute: true` so the origin publishes to peers.
- Without HttpBus, the Admin Console App must fan out to every target instance.
- The FusionCache Redis backplane does not carry Version, TTL, or settings overlays.

A down peer can leave the deployment partially updated. Inspect the operation result and reconcile failed instances.

### Does an Admin runtime change replace configuration?

No. Use runtime overlays for operational response and testing. Put permanent changes into the deployed configuration so restarts and new instances converge on the intended policy.

## Output Cache methods and identity

### Can I cache POST search or GraphQL requests with Output Cache?

Yes, but only with an explicit cache identity binding.

- Use `.WithCacheIdentity(...)` or `[CacheIdentity]` with a named contract when selected request fields define identity.
- Use `.WithContentHashCacheIdentity(...)` or `[ContentHashCacheIdentity]` for a bounded body hash.

Without identity metadata, Output Cache supports `GET` and `HEAD` with URL identity. Applying a domain alone does not cache `POST`, `PUT`, or other methods.

See [Endpoint cache identity](../reference/cache-identity.md).

## Product boundaries

### Does CacheOrchestrator replace FusionCache or HybridCache?

No. It configures and scopes those engines through domains. Engine-specific features remain engine-specific, and the provider abstraction intentionally does not expose every underlying API.

### Does it guarantee consistency across instances?

Only when the topology supplies the required shared store, backplane, or command bus. Several independent in-memory processes cannot invalidate each other by themselves.

### Does it operate Redis for me?

No. It resolves connection options and uses Redis as a backend. Provisioning, access control, TLS, persistence, failover, monitoring, and capacity remain platform responsibilities.

### Are default service implementations public?

No. Application code should depend on public interfaces such as `IDomainDataCache`, `ICacheOrchestrator`, and `ICacheOrchestratorInvalidator`, then obtain implementations through dependency injection.


