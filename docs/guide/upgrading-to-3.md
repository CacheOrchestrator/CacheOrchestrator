# Upgrading to 3.0

3.0 establishes a new package and extension contract. Upgrade the application and all CacheOrchestrator satellite packages together. This guide applies both to 2.x applications and to applications running an earlier 3.0 beta; beta cache entries and custom implementations are not a compatibility baseline for the final contract.

## Choose the application composition

Libraries target .NET 8 and .NET 10. Admin Console is a separate .NET 10 application. The [composition recipes](../how-to/composition.md) show complete registrations for web applications, HTTP-free workers, FusionCache, HybridCache, Redis and EF invalidation.

For .NET 8 application builds using the packaged identity analyzer, use .NET SDK 8.0.400 or newer with Roslyn 4.11 or newer. Package-consumer checks exercise the 8.0.4xx servicing line with C# 12 and the .NET 10 SDK with both target frameworks. Building this repository itself uses the SDK policy in `global.json`.

`CacheOrchestrator` is the web + FusionCache convenience package. For HybridCache, use the explicit ASP.NET Core composition with `AddHybridCache` and `AddCacheOrchestratorHybridCache`. Core owns HTTP-free `ICacheOrchestrator`; `IDomainDataCache` belongs to ASP.NET Core. HTTP policy is in `DomainHttpCacheOptions`, which contains the shared Core `DomainCacheOptions`.

Configure engines under `DataCacheInstances` and select them with `DataCache.Instance`. Keep provider-specific Fusion settings under `FusionCache`. Cache durations in JSON use integer `*Seconds` properties. Redis leaf extension methods are in the `CacheOrchestrator.Redis` namespace even when only the corresponding leaf package is installed.

## Changes visible to application code

| Area | 3.0 behavior and migration |
|------|---------------------------|
| HTTP identity | URL-derived Data Cache keys include the HTTP method. Explicit entity keys continue to describe the declared entity. Declare bindings for every method intentionally Output Cached. |
| Negotiation | Default Accept normalization is disabled. A configured normalization list canonicalizes only a single exact token without parameters. Multi-value headers, quality weights, wildcards and regional language variants retain their representation identity. |
| Response `Vary` | Configured non-secret varied headers are advertised even when absent on the current request. Verify compression middleware order and upstream cache policy with the application's formatters. |
| Request body hashing | Identity bypass at a size limit leaves the request body readable, including unknown-length bodies. Existing body size limits remain application policy. |
| Authentication | Opting into caching requires a stable user/tenant identity. A configured claim list defines the sharing boundary; a tenant claim alone permits sharing among that tenant's users. Claim types and values now use unambiguous length-prefixed `claims2:` material, changing keys from earlier betas. `Cache-Control: private` does not partition server caches. See [authenticated vary](../reference/vary.md). |
| Runtime snapshots | HTTP collections are copied, read-only `IReadOnlyList<T>` values. Fusion settings have init-only scalar properties. Initialize new values instead of mutating shared snapshots. Preserve null versus empty query allowlists. |
| Size configuration | Remove `FusionCache.MaxItemBytes`, including `0`, and the `fusionCache.maxItemBytes` runtime key. The setting never enforced bytes and has no replacement. Its size-limit capability and health field are removed. |
| Vary contexts | Replace `CacheVarySurface.Fusion` with `CacheVarySurface.DataCache`; the numeric value remains 1. The name applies to either data engine. |
| Settings catalog | Resolve `DomainSettingCatalog` from DI. Its read methods are instance members. `RegisterSection` now takes the host's `IServiceCollection`; register sections before building the provider. |
| Settings validation | Undefined enums, non-finite ratios, invalid inherited client minima and incompatible effective Fusion timeouts/fail-safe values are rejected before publication. A failed multi-package patch changes no section or revision. |
| EF invalidation | Successful saves in observed explicit or ambient transactions invalidate after commit. Rollback discards work. Register through the existing EF extension to obtain both interceptors; the package now depends on EF Core Relational. |

Fusion `HardTtlSeconds` caps base freshness, not the complete stale lifetime. Jitter and the fail-safe horizon are separate. A long `DataCache.TtlSeconds` requires a matching cap, and an enabled fail-safe horizon must cover the effective base duration. The [domain profiles](domain-profiles.md) include validated snapshot and CRUD configurations.

## Changes for custom implementations

Application consumers usually obtain interfaces through DI. Implementing an interface takes responsibility for the complete behavior below; recompiling a custom provider alone is not sufficient validation.

| Contract | Required implementation change |
|----------|--------------------------------|
| `IDataCacheProvider` | Implement `GetOrCreateWithTagsAsync`: publish each materialization with the final tags selected from its value. This includes eager and background factory completion. A second `SetAsync` after returning is unsafe. |
| `IDomainRuntimeOverrideStore` | Implement typed `GetSettings<T>` and atomic `Update`. Prepare a private copy, publish all sections and the revision together only after success, and advance revisions on clear. Sections must be immutable. |
| `IDomainSettingsPatchContributor` | Replace direct `Apply` mutation with `Prepare` and `Validate` using `DomainSettingsPatchContext`. Validate the complete effective state, including inherited values and changes supplied by other packages. |
| `IClusterCommandHandler` | Return `ClusterCommandResult`. Distinguish applied, already applied, ignored, rejected and failed execution. Do not acknowledge an incomplete local invalidation as successful delivery. |
| `IEdgeInvalidationProvider` | Implement `CaptureTarget`. Deliver using `request.Target`, including its original route and credentials, rather than resolving current configuration again. Capture performs no network I/O and runs outside the response path. |
| Custom Edge outbox | Persist the complete `EdgeInvalidationJob` target and copied tags. Respect provider/target format versions and batch only equal targets. The enqueue-only replacement owns its dispatcher. Protect stored credentials and retain old provider implementations until their jobs drain. |
| Key generators and request extensions | Use the current context-based key-generation contract and complete method/vary material. Never retain `HttpContext` or request-scoped dependencies for background factory completion. |

Low-level store `Update` is an advanced primitive: direct callers own effective-value validation and any resulting invalidation. Management and HttpBus perform coordinated validation and retain unfinished purge plans for retries. EF applications that commit through raw ADO.NET APIs outside EF's observable wrapper must explicitly invalidate after the actual commit. Crash-safe database-to-cache delivery requires an application outbox; see [EF transaction boundaries](../reference/ef-core-invalidation.md).

## Cache, wire and diagnostic compatibility

Cache keys, serialized provider envelopes, cluster messages and public CLR interfaces are separate contracts. The `co3:` provider-key prefix, `coe1-` Edge tags, HttpBus protocol version 1 and Edge target format version 1 describe different formats. A matching prefix or wire version does not establish compatibility with older beta behavior.

Fusion footprint materializations assign tags before native publication. Hybrid footprint envelopes also contain a tagged validity-marker reference; readers must honor that marker. Do not share a data-cache namespace between older beta envelope readers and 3.0 readers. Ordinary and footprint APIs can share entries within the compatible 3.0 deployment.

HttpBus returns 400 for rejected commands and 503 for incomplete execution. Successful duplicate delivery is explicitly already applied. In-flight duplicates share completion, and failed commands can retry unfinished local invalidation without replaying an old settings mutation over newer state. This state is process-local and bounded by the deduplication window; the bus makes one attempt per peer and provides no durable exactly-once guarantee. See [cluster delivery](../reference/cluster-bus.md).

Admin settings can return 503 with `localApplied=true` and `LocalInvalidationErrors`: settings were published but a local purge failed. Inspect the result and repair/retry the invalidation; blindly sending a new settings mutation is a different operation. Update custom clients that previously treated every non-200 response as proof that nothing changed.

Redis health probe names are now `redis:output-cache` and `redis:data-cache:{instance}`. Update dashboards that match the previous names. The removed size-limit health field must not be treated as an active capability. Existing schedule/header and metric semantics are documented in [observability](../reference/observability.md); schedule freshness also applies to enabled Edge caching.

## Deployment and rollback

1. Build and test the complete application against one 3.0 package set. Check custom implementations, formatter/compression behavior, authentication claims, entity footprints, transaction boundaries and Admin clients.
2. Allocate fresh origin cache namespaces. Changing `Cache:Namespace` changes derived defaults, but explicit `OutputCache.Namespace` and `DataCacheInstances:{name}:Namespace` must also be changed. Keep old and new HttpBus memberships/namespace scopes separate.
3. Warm the new deployment against current data. While both pools can serve writes, invalidate both pools through an application-owned bridge or quiesce writes during cutover; a new namespace does not propagate invalidation between versions.
4. Purge old Edge placements at cutover. Edge namespace changes affect tags, not the URL cache key, so they do not by themselves remove CDN objects. Preserve old credentials until pending purges finish. Browser caches require expiry, revalidation, or application-owned versioned URLs.
5. Route traffic to the new pool, verify origin/cache behavior, then drain the old pool. Graceful Edge shutdown attempts remaining batches until the host deadline; expired deadlines and process crashes can leave work undelivered. Observe queue failures and external purge completion.
6. Retain a tested rollback artifact and its configuration. Before routing back, purge or advance the old pool's cache generation against current data; its retained entries may have missed writes while it was inactive. Purge affected Edge placements again. Database/schema rollback is application-owned.

Do not use a shared live namespace as the rollback mechanism. Keep old cache data only for bounded cleanup after the rollback window, and keep control-plane credentials out of logs and release artifacts.

## Release acceptance

The repository's release gates cover both library TFMs, unit and Docker integration tests, all packaged libraries, and external package consumers including analyzer-positive and analyzer-negative cases. Passing those gates establishes the shipped contracts; it does not replace the application's load test, its own database transaction models, or verification against a live Edge account.

See the [release procedure](../contributor/releasing.md) for API baseline promotion, package versioning and publication. MinVer continues to derive versions from Git tags; creating a release tag and publishing packages are separate maintainer actions.
