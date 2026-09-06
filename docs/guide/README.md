# CacheOrchestrator guide

> **Guide** — domain design, packages, topologies, cutovers, and operations. Product overview: [root README](../../README.md) · Catalog: [docs/README.md](../README.md)

The guide connects the product overview to the technical reference. It teaches how to design a domain, choose packages and topology, prepare client cutovers, and operate the resulting cache.

If you have not used CacheOrchestrator before, begin with [Getting started](getting-started.md). It takes an empty ASP.NET Core project through a visible Output Cache hit, a Data Cache read, and entity invalidation.

## Recommended path

1. [Getting started](getting-started.md) — build and run the first cached endpoints.
2. [Concepts](concepts.md) — understand domains, the three layers, and the difference between TTL, invalidation, and `Version`.
3. [Domain profiles](domain-profiles.md) — decide whether data changes as a snapshot or one entity at a time.
4. [Packages](packages.md) — select host, Data Cache engine, Redis, and optional integrations.
5. [Topologies](topologies.md) — place stores in memory or Redis and decide how instances coordinate.
6. [Client Cache Schedule](client-cache-schedule.md) — prepare browsers and enabled Edge caches for a planned snapshot cutover.
7. [Edge cache integration](edge.md) — extend domain/entity freshness and invalidation to Cloudflare or Varnish.
8. [Operations](operations.md) — read `X-CacheOrchestrator`, use telemetry, and apply safe runtime changes.

The path deliberately chooses the domain policy before infrastructure. The endpoint contract should follow the freshness model; Redis and bus choices follow deployment requirements.

## Choose a page by goal

| Goal | Page |
|------|------|
| See a working miss, hit, and invalidation | [Getting started](getting-started.md) |
| Understand what CacheOrchestrator coordinates | [Concepts](concepts.md) |
| Design a snapshot or CRUD domain | [Domain profiles](domain-profiles.md) |
| Decide which NuGet packages to install | [Packages](packages.md) |
| Compare InMemory, Redis, backplane, and HttpBus | [Topologies](topologies.md) |
| Shorten client and enabled Edge TTLs before a known release | [Client Cache Schedule](client-cache-schedule.md) |
| Coordinate Cloudflare or Varnish edge invalidation | [Edge cache integration](edge.md) |
| Diagnose or change a running deployment | [Operations](operations.md) |
| Evaluate direct platform APIs against the domain model | [Comparison](comparison.md) · [endpoint worked example](comparison-endpoint-example.md) |
| Answer a specific symptom or boundary question | [FAQ](faq.md) |

## Know which documentation layer you need

| Layer | Use it for | Start here |
|-------|------------|------------|
| **Product** | What the library is and whether it fits | [Root README](../../README.md) |
| **Guide** | Mental models, decisions, and operational workflow | This page |
| **How-to** | Copy-paste package compositions | [Package composition](../how-to/composition.md) |
| **Reference** | Exact settings, APIs, keys, identity, deployment, and telemetry | [Documentation index](../README.md) |
| **Contributor** | Architecture, releases, benchmarks, and project work | [Contributor docs](../contributor/) |

The guide avoids reproducing the full configuration schema. When you know the decision you need to implement, follow its link into the how-to or reference layer.

## Learn by running

| Sample | What it demonstrates |
|--------|----------------------|
| [Minimal sample](../../samples/CacheOrchestrator.Minimal) | One-minute Output Cache and Data Cache flow |
| [Playground](../../samples/CacheOrchestrator.Sample) | Domain TTLs, Client Cache Schedule, CRUD, Redis, Admin, and observability |
| [Topology labs](../../samples/CacheOrchestrator.Sample/labs/README.md) | Five Docker stages from one in-memory process to shared Redis and HttpBus |

The topology labs are teaching environments rather than production blueprints. Use the [Deployment reference](../reference/deployment.md) when taking a layout to production.

## Keep the core model nearby

```text
Domain
  └─ resolved policy snapshot
       ├─ Client Cache
       ├─ ASP.NET Core Output Cache
       ├─ Data Cache through FusionCache or HybridCache
       └─ shared Version, vary material, and invalidation tags
```

A domain coordinates the layers; it does not replace them or own a cache store.
