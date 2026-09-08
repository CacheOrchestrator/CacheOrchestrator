# CacheOrchestrator documentation

## Product

Start with the [root README](../README.md) for the product overview and quick start. Then pick a path below.

## Guide

1. [Getting started](guide/getting-started.md) — first endpoint and `X-CacheOrchestrator`
2. [Concepts](guide/concepts.md) — domain, three layers, Version vs TTL vs tags
3. [Domain profiles](guide/domain-profiles.md) — snapshot datasets vs changing records
4. [Packages](guide/packages.md) — which NuGet to install
5. [Topologies](guide/topologies.md) — InMemory, Redis, HttpBus, mixed
6. [Edge cache integration](guide/edge.md) — Cloudflare/Varnish edge TTL, opaque tags, and queued invalidation
7. [Client Cache Schedule](guide/client-cache-schedule.md) — client `max-age` before a cutover
8. [Client cache busting and invalidation](guide/client-invalidation.md) — domain version cache busting and entity synchronization
9. [Operations](guide/operations.md) — diagnostics map, Admin API vs Admin Console App
10. [Comparison](guide/comparison.md) — hand-rolled stack vs this library
11. [FAQ](guide/faq.md) — common mistakes

### Learn by running

- [Minimal sample](../samples/CacheOrchestrator.Minimal) — a miss, then a hit
- [Playground sample](../samples/CacheOrchestrator.Sample) — TTLs, schedule, Redis, CRUD
- [Topology labs](../samples/CacheOrchestrator.Sample/labs/README.md) — Docker Compose stages (not part of NuGet)

## How-to

- [Upgrade to 3.0](guide/upgrading-to-3.md) — application and implementer changes, cache isolation, rollout and rollback

- [Package composition](how-to/composition.md) — scenarios 1–8 (typical web, Hybrid, Redis, library, EF, …)

## Reference

The reference describes exact configuration paths, API contracts, key material, and runtime behavior. Read one page for a specific contract, or follow one of the paths below when a concern crosses several parts of the system.

### Configuration and request identity

1. [Configuration](reference/configuration.md) — complete `appsettings` schema, defaults, inheritance, and validation
2. [Endpoint cache identity](reference/cache-identity.md) — method bindings, named contracts, URL identity, and content hashes
3. [Cache keys](reference/cache-keys.md) — namespaces and key material for Output Cache and Data Cache
4. [Domain vary dimensions](reference/vary.md) — query, headers, language, cookies, authentication, and custom contributors

### Cache layers and freshness

- [Core API](reference/core-api.md) — HTTP-free orchestration, management, domain contexts, keys, and entity operations
- [Output Cache](reference/output-cache.md) — policy selection, endpoint metadata, status rules, headers, and authentication
- [Data Cache](reference/data-cache.md) — providers, domain resolution, key generation, results, and entity reads
- [Client Cache Schedule](guide/client-cache-schedule.md) — phase and `max-age` calculation (guide)
- [Entity footprint](reference/entity-footprint.md) — primary entities, members, dependencies, collections, batches, and aliases
- [Invalidation](reference/invalidation.md) — Version cutovers, tags, results, observers, and multi-instance behavior
- [EF Core invalidation](reference/ef-core-invalidation.md) — entity mapping and purge after `SaveChanges`

### Infrastructure and operations

- [Backends](reference/backends.md) — built-in stores and the three custom storage boundaries
- [Extensibility](reference/extensibility.md) — application, provider, cluster, and host extension points
- [Deployment](reference/deployment.md) — single-instance, Redis, InMemory clusters, and shared configuration
- [Cluster command bus](reference/cluster-bus.md) — membership, commands, delivery, authentication, and partial failure
- [Observability](reference/observability.md) — `X-CacheOrchestrator`, metrics, traces, logs, and health checks
- [Admin](reference/admin.md) — Admin API, Admin Console App, trust boundaries, and operational endpoints
- [Admin Console App Docker](../deploy/admin/README.md) — image, volumes, and logs

### Common technical paths

| Goal | Read in this order |
|------|--------------------|
| Explain why two requests share or do not share an entry | [Endpoint identity](reference/cache-identity.md) → [Vary](reference/vary.md) → [Cache keys](reference/cache-keys.md) |
| Invalidate one changed record and every view that contains it | [Entity footprint](reference/entity-footprint.md) → [Invalidation](reference/invalidation.md) → [EF Core invalidation](reference/ef-core-invalidation.md) |
| Design a multi-instance deployment | [Backends](reference/backends.md) → [Deployment](reference/deployment.md) → [Cluster bus](reference/cluster-bus.md) |
| Extend entity invalidation to Cloudflare or Varnish | [Edge cache integration](guide/edge.md) → [Invalidation](reference/invalidation.md) → [Deployment](reference/deployment.md) |
| Refresh browser data after a publication or write | [Client Cache Schedule](guide/client-cache-schedule.md) → [Client cache busting and invalidation](guide/client-invalidation.md) |
| Diagnose hit rate or factory load | [Observability](reference/observability.md) → [Admin](reference/admin.md) |
| Integrate custom identity, storage, health, or command transport | [Extensibility](reference/extensibility.md) → the linked contract page |

## Packages

- [Packages](guide/packages.md) — which package to choose
- [Composition](how-to/composition.md) — copy-paste install / register / config
- [Root README — Packages and applications](../README.md#packages-and-applications) — NuGet catalog, versions, Admin Console App


## For contributor

- [Contributing](../CONTRIBUTING.md)
- [Releasing](contributor/releasing.md)
- [Worklog template](contributor/templates/worklog-template.md)
- [Benchmarks](contributor/benchmarks/results.md)


