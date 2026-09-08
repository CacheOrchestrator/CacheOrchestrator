# CacheOrchestrator.Edge

[**CacheOrchestrator**](https://github.com/CacheOrchestrator/CacheOrchestrator) is a multi-tier cache coordination and synchronized invalidation library for .NET.

This package provides provider-neutral, tag-native edge orchestration: finalized response metadata, opaque domain/entity tag projection, origin-aware invalidation, and a bounded background worker. Install `CacheOrchestrator.Edge.Cloudflare`, `CacheOrchestrator.Edge.Varnish`, or a custom provider; this package does not contact an edge service by itself.

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEdge(
    builder.Configuration,
    edge => edge.AddCloudflare());
```

An active Client Cache Schedule shortens the Edge fresh TTL from its configured maximum toward the client minimum. Changing `ScheduledUpdateUtc` normally affects subsequent origin metadata; moving it earlier with `ApplyImmediately` also queues an Edge purge. Authentication, representation and cacheability safety changes trigger mandatory purges. Runtime and configuration Version changes automatically enqueue a domain-tag purge for Edge-enabled domains. `PurgeOnStartup` optionally purges an explicitly configured domain when a host starts. The built-in queue is in-memory and best-effort; it attempts to drain during graceful shutdown until the host deadline. Jobs retain an immutable destination and credentials across configuration changes. Register an `IEdgeInvalidationQueue` first to route jobs to a durable outbox; an enqueue-only replacement also owns its durable dispatcher. Custom integrations implement `IEdgeResponseProvider` and `IEdgeInvalidationProvider`. Only tag-native providers are supported in v1.

## Documentation

- [Edge cache integration and custom provider example](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/docs/guide/edge.md#minimal-custom-provider)
- [Package composition](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/docs/how-to/composition.md)
- [Repository](https://github.com/CacheOrchestrator/CacheOrchestrator)

## License

MIT — [LICENSE.md](https://github.com/CacheOrchestrator/CacheOrchestrator/blob/main/LICENSE.md)
