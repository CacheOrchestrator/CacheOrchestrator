# Client cache busting and invalidation

> **Guide** — extend the [README quick start](../../README.md#quick-start) with application-owned browser cache busting and TanStack Query synchronization. These examples use existing CO APIs; no additional CO feature or client package is required.

## Start with the right client TTL

When a browser receives a response with a long `max-age`, it can reuse that response without sending another request. Invalidating Output Cache, Data Cache, or Edge Cache cannot remove the copy already stored in that browser. A product saved on the server can therefore remain unchanged on an open client's next read.

First choose a client TTL that matches the acceptable age of the data. A long TTL suits a stable published catalog; frequently changing records usually need a shorter one. Server TTLs can remain different: CO configures each layer independently.

For a planned publication, use **[Client Cache Schedule](client-cache-schedule.md)**. `ScheduledUpdateUtc`, `TtlSeconds`, and `TtlMinSeconds` reduce the client lifetime as the cutover approaches. This affects headers on responses being served; it cannot rewrite headers on copies already in the browser, and it does not publish data or invalidate server entries by itself.

Unscheduled corrections and interactive applications still need a way to bypass browser cache or refresh application state. The following examples cover two cases:

| Example | Application | Approach |
|---------|-------------|----------|
| [1. Catalog at startup](#1-catalog-cache-busting-at-application-startup) | A published snapshot | Read its domain version once; use a versioned URL or bypass browser HTTP cache. |
| [2. Client cache invalidation for changing products](#2-client-cache-invalidation-for-changing-products) | Users editing and viewing changing products | Refresh after saving, then observe external changes through polling or notifications. |

## Before the examples

Keep the meta-package registration from the quick start:

```csharp
using CacheOrchestrator.Configuration;
using CacheOrchestrator.DataCache;
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Invalidation;
using CacheOrchestrator.OutputCache;

builder.Services.AddCacheOrchestrator(builder.Configuration);

var app = builder.Build();
app.UseCacheOrchestrator();
// Map the example endpoints before app.Run().
```

Code blocks are integration fragments. `CatalogRepository`, `ProductRepository`, and `ProductUpdate` represent your application's registered data services and write DTO. Commit writes before invalidating. The examples use public data on one origin instance. Apply your normal input validation and authorization to write endpoints.

## 1. Catalog cache busting at application startup

Consider an application that loads a published catalog when it starts. It is acceptable for the application to keep using the version discovered at startup for the rest of that session; it does not need to detect a new publication while the user is working. On the next startup, it should discover the latest version and avoid reusing the previous publication's browser-cached response.

The application requests the domain version once, keeps it in memory, and adds it to subsequent catalog URLs. This avoids a version lookup before every read. It is suitable when updates can wait until the next application session; use periodic discovery or the synchronization approach in example 2 when open sessions must notice changes.

This is a cache-freshness policy, not a guarantee that a session reads an immutable dataset. The sample endpoint always serves current data when a request reaches the origin. Applications requiring an exact historical snapshot must also implement version-specific data selection on the server.

### Configure the catalog domain

Replace the quick start's `catalog` domain with the following snapshot policy. Retain its root provider/namespace settings.

```json
"catalog": {
  "Version": "2026-09",
  "IgnoreQueryKeys": ["co_v"],
  "DataCache": { "TtlSeconds": 3600 },
  "OutputCache": { "TtlSeconds": 300 },
  "ClientCache": {
    "Cacheability": "Public",
    "TtlSeconds": 86400
  }
}
```

`IgnoreQueryKeys` belongs at the domain root. Here, `co_v` changes the URL seen by the browser and CDN, while CO excludes it from query-based OC/DC key material. The effective domain `Version` already selects the server cache generation, so an additional query variation would create redundant entries for that generation.

Leave `VaryByQueryKeys` at its default (`null`) to retain other non-tracking query dimensions, such as `language` or `category`. If your application inherits an explicit allowlist, include all parameters that actually change the response. `co_v` is an application convention, not a reserved CO parameter. Only ignore it because the endpoint does not use it to select content; an API that serves a requested historical version must include that selection in its cache identity. See [vary dimensions](../reference/vary.md).

### Expose the current version

Add an application-owned endpoint using CO's effective domain options:

```csharp
app.MapGet("/api/cache/catalog-version", (
    HttpContext http, IDomainCacheOptionsProvider options) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new
    {
        version = options.GetOrCreateDomainOptions("catalog").Version
    });
});

app.MapGet("/api/catalog", async (
    HttpContext http, IDomainDataCache cache, CatalogRepository repository,
    CancellationToken cancellationToken) =>
{
    var catalog = await cache.GetOrSetAsync(
        http, token => repository.ReadAsync(token), cancellationToken);
    return Results.Ok(catalog);
}).CacheOutputWithDomain("catalog");
```

Do not attach an Output Cache policy to the version endpoint. Exclude it from any global OC policy and Edge cache rule as well. It exposes only the public domain the application needs; the Admin API is not a browser discovery endpoint.

A cached header advertising all current domain versions can itself become stale at OC or Edge. This example instead uses an explicitly uncached endpoint as its source of current state.

### Option A: add the version to the URL

At startup, request the version once and retain it for subsequent catalog reads:

```javascript
async function createCatalogClient() {
  const response = await fetch('/api/cache/catalog-version', {
    cache: 'no-store'
  });
  if (!response.ok) throw new Error(`Version lookup failed: ${response.status}`);
  const { version } = await response.json();

  return {
    async read() {
      const url = new URL('/api/catalog', location.origin);
      url.searchParams.set('co_v', version);
      const response = await fetch(url);
      if (!response.ok) throw new Error(`Catalog read failed: ${response.status}`);
      return response.json();
    }
  };
}

// Create once at application startup and share this client.
const catalogClient = await createCatalogClient();
const catalog = await catalogClient.read();
```

```text
Application startup → GET /api/cache/catalog-version → version 2026-09
Catalog reads       → GET /api/catalog?co_v=2026-09
Next release/start  → GET /api/catalog?co_v=2026-10
```

There is one extra startup request, not an extra request before every read. If your app can stay open across releases, repeat discovery on explicit refresh or at an appropriate interval. Startup discovery alone does not detect changes during a long-running session.

Publish the data and configured `Version` coherently across instances. Change the version for corrections that must change client URLs; `InvalidateDomainAsync` alone does not change `Version`. Do not reuse a version for different content. The URL is a cache identity, not historical data selection: an old URL reaching the origin can return current application data.

Configure the CDN to include `co_v` in its cache key. CO's `IgnoreQueryKeys` setting applies to its server key material; do not copy that exclusion to the CDN. Changing the browser URL cannot bypass an Edge rule that ignores that parameter.

### Option B: keep the URL and control fetch caching

Fetch has no `cache: 'none'` option. Use `no-store` to bypass the browser HTTP cache for a request without storing its response there:

```javascript
// At application startup, load current data without a version lookup.
const response = await fetch('/api/catalog', { cache: 'no-store' });
if (!response.ok) throw new Error(`Catalog read failed: ${response.status}`);
const catalog = await response.json();
```

This is simpler if the application reads the catalog once at startup and keeps it in application memory. No version is needed to bypass the browser HTTP cache. Repeated calls using `no-store` each make a network request; server-side CO caching still follows its configured policy.

If you want to refresh the browser's HTTP cache for later ordinary reads, use `cache: 'reload'` instead. `cache: 'no-cache'` asks for revalidation and can reuse a response validated with `304`. Neither option deletes arbitrary browser cache entries, and `no-store` does not remove previously stored copies. See [Fetch cache modes](https://developer.mozilla.org/en-US/docs/Web/API/Request/cache).

These options control browser HTTP caching. They do not independently purge a CDN, CO cache, or service-worker-managed cache. Coordinate those layers when fresh origin data is required. For option B alone, the `co_v` ignore setting is unnecessary because the client sends no version parameter.

## 2. Client cache invalidation for changing products

Consider a product screen where a user edits a price. After saving, that screen should display the updated product. A second user viewing the same product should also see the change, either on the next periodic check or promptly after a notification.

There are two steps to coordinate: retire the server's cached product after the write, then make each relevant client fetch and display it again. A startup version check alone does not keep an already open product screen current.

### Use a client query cache to manage the view

As a concrete example, we will use **TanStack Query**, a library for fetching and caching server data in the application. It tracks loading and error states, reuses query results, and can mark selected results stale and refetch active views. Its cache holds application data in JavaScript memory; it is separate from the browser's HTTP cache.

CO owns the server domain policy and invalidation. TanStack Query owns the displayed query state. The application connects them through the save-success callback, with polling or SignalR when other users can change the data. The same pattern can be implemented with another client-state library. See [TanStack query invalidation](https://tanstack.com/query/latest/docs/framework/react/guides/query-invalidation).

The example progresses from refreshing the current user's screen after a save to observing external changes. It uses domain **`product`** and entity kind **`products`**, replacing the quick start's product routes; do not register both versions of the same route.

### Configure and invalidate the server caches

Add this domain beside `catalog`:

```json
"product": {
  "Version": "1",
  "DataCache": { "TtlSeconds": 300 },
  "OutputCache": { "TtlSeconds": 120 },
  "ClientCache": { "Cacheability": "NoStore" }
}
```

The browser HTTP cache is disabled here; TanStack still retains application data in memory. The query function also uses `no-store` so previously cached browser responses are not reused.

```csharp
app.MapGet("/api/products/{id:int}", async (
    HttpContext http, int id, IDomainDataCache cache,
    ProductRepository repository, CancellationToken cancellationToken) =>
{
    var product = await cache.GetOrSetEntityAsync(
        http, token => repository.FindAsync(id, token), cancellationToken);
    return product is null ? Results.NotFound() : Results.Ok(product);
}).CacheOutputWithDomain("product", entityKind: "products", resourceRouteKey: "id");

app.MapPut("/api/products/{id:int}", async (
    int id, ProductUpdate update, ProductRepository repository,
    ICacheOrchestratorInvalidator invalidator,
    CancellationToken cancellationToken) =>
{
    await repository.UpdateAndCommitAsync(id, update, cancellationToken);
    var result = await invalidator.InvalidateEntityAsync(
        "product", "products", id, cancellationToken);
    return result.Succeeded
        ? Results.NoContent()
        : Results.Problem("Data was saved, but cache invalidation failed.");
});
```

Entity identity is declared once on the read endpoint; the data-cache helper reuses it. The write invalidates both matching server caches. A failed invalidation needs application recovery; retrying the write blindly may apply the mutation twice.

### Refresh this tab after a successful write

Install `@tanstack/react-query` and mount the application under a `QueryClientProvider` with one stable `QueryClient`. Inside a React component with a numeric `id`, use:

```tsx
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

const queryClient = useQueryClient();
const queryKey = ['product', id];

const product = useQuery({
  queryKey,
  queryFn: async ({ signal }) => {
    const response = await fetch(`/api/products/${id}`, {
      cache: 'no-store', signal
    });
    if (!response.ok) throw new Error(`Read failed: ${response.status}`);
    return response.json();
  },
  staleTime: Infinity
});

const save = useMutation({
  mutationFn: async (update: { name: string; price: number }) => {
    await queryClient.cancelQueries({ queryKey, exact: true });
    const response = await fetch(`/api/products/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(update)
    });
    if (!response.ok) throw new Error(await response.text());
  },
  onSuccess: () => queryClient.invalidateQueries({ queryKey, exact: true })
});

// Render product.data and loading/error states in your component.
// On form submission: save.mutate({ name: 'Updated product', price: 12 });
```

```text
First read → GET → OC MISS → DC MISS → repository → caches → UI
Manual refetch → GET → OC HIT (while valid) → UI

Save → PUT → commit → CO invalidation → successful response
     → TanStack onSuccess → query invalidation → GET → updated UI
```

The active query refetches even with infinite stale time because it was explicitly invalidated. TanStack's abort signal allows old reads to be cancelled before the mutation. There are two requests per save: a write and a read. No version discovery or additional client token is involved.

CO does not automatically invalidate TanStack: the mutation callback is the bridge. Another user's write does not run this tab's callback. Add one of the following to synchronize external changes.

### Simple extension: polling

Replace `staleTime: Infinity` in the query with:

```tsx
staleTime: 15_000,
refetchInterval: 15_000,
refetchIntervalInBackground: false,
refetchOnWindowFocus: 'always',
refetchOnReconnect: 'always'
```

Keep `onSuccess` for immediate refresh after this user's write. Another user's write invalidates CO; the next poll retrieves updated data. Polling runs independently of stale time. The interval is approximate, not a maximum staleness guarantee: offline operation and browser throttling affect timing.

Use this when several seconds of delay are acceptable. It adds regular GETs for active queries but no notification infrastructure. Those reads can still hit fresh server caches.

### Prompt extension: SignalR

SignalR can carry CO invalidation tags to other open tabs. Register this application-owned bridge before `Build()` and map its hub after `Build()`:

```csharp
builder.Services.AddSignalR();
builder.Services.AddSingleton<ICacheInvalidationObserver, ProductObserver>();

// After Build():
app.MapHub<ProductHub>("/events/products");
```

```csharp
using Microsoft.AspNetCore.SignalR;
using CacheOrchestrator.Invalidation;

public sealed class ProductHub : Hub { }

public sealed class ProductObserver(IHubContext<ProductHub> hub)
    : ICacheInvalidationObserver
{
    public ValueTask OnBeforeInvalidateAsync(
        CacheInvalidationContext context,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public async ValueTask OnAfterInvalidateAsync(
        CacheInvalidationContext context, CacheInvalidationResult result,
        CancellationToken cancellationToken = default)
    {
        if (!result.Succeeded) return;
        var tags = context.Tags.Where(tag =>
            tag == "domain:product" || tag == "entitykind:product:products" ||
            tag.StartsWith("entity:product:products:", StringComparison.Ordinal))
            .ToArray();
        if (tags.Length > 0)
            await hub.Clients.All.SendAsync("invalidated", tags, cancellationToken)
                .ConfigureAwait(false);
    }
}
```

The broadcast is appropriate only for the public product data in this example. Private data requires authorized, scoped subscriptions.

Install `@microsoft/signalr` and initialize this connection once at the application root, using the same `queryClient` as the provider:

```typescript
import { HubConnectionBuilder } from '@microsoft/signalr';

const connection = new HubConnectionBuilder()
  .withUrl('/events/products')
  .withAutomaticReconnect()
  .build();

async function refresh(queryKey: readonly unknown[], exact = false) {
  await queryClient.cancelQueries({ queryKey, exact });
  await queryClient.invalidateQueries({ queryKey, exact });
}

connection.on('invalidated', async (tags: string[]) => {
  if (tags.includes('domain:product') || tags.includes('entitykind:product:products')) {
    await refresh(['product']);
    return;
  }
  for (const tag of tags) {
    const match = /^entity:product:products:(\d+)$/.exec(tag);
    if (match) await refresh(['product', Number(match[1])], true);
  }
});

connection.onreconnected(() => { void refresh(['product']); });

try {
  await connection.start();
  await refresh(['product']); // Covers reads made before subscribing.
} catch (error) {
  console.warn('Live updates unavailable; polling/focus refresh remains available.', error);
}

// At application teardown: await connection.stop();
```

This tag mapping assumes positive integer product IDs. Active matching queries refetch; inactive ones become stale for their next use. Disabled queries need explicit application handling.

```text
User A saves → CO invalidates → observer sends tags
User B receives tags → TanStack invalidates → GET → updated UI
```

Keep mutation `onSuccess` even with SignalR: it works when the event connection is unavailable. The event and callback can cause an extra/cancelled read depending on timing. This example does not promise exactly one refetch per write.

For recovery, retain focus refresh and optional slower polling (for example 60 seconds). SignalR automatic reconnect does not retry the initial start failure and can exhaust its reconnect attempts. Reconnect refresh replaces event replay here; observer failures are logged by CO, not durably retried. See [SignalR client behaviour](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0).

## What synchronization guarantees

The product example connects writes, server invalidation, notifications, and UI refresh. It provides eventual synchronization, not atomic or lossless synchronization of every browser.

- **Lists and dependencies:** invalidate collection dependencies when changes affect membership, sorting, or totals, and invalidate corresponding list query keys. An entity detail example does not infer these relationships. See [Entity footprint](../reference/entity-footprint.md).
- **Multiple instances:** HttpBus can apply remote invalidations, allowing each instance's observer to notify local clients. Delivery failure, hub routing/affinity, and stale peer recovery still need a topology-specific design. Local `Succeeded` does not certify all peers or Edge purge completion.
- **Edge and service workers:** a client refetch must not be satisfied by another stale layer. Coordinate existing Edge invalidation and service-worker policy; browser fetch options are not a universal purge.
- **Identity changes:** scope private query state to the user/tenant and clear obsolete state on logout or identity changes.

CO already supplies the building blocks: effective domain versions, query vary settings, client cache policies, Client Cache Schedule, entity invalidation, and observer hooks. The application chooses how its clients discover versions and react to changes; it does not need a new CO client cache subsystem.

## Related reading

- [Client Cache Schedule](client-cache-schedule.md)
- [Invalidation](../reference/invalidation.md)
- [TanStack query invalidation](https://tanstack.com/query/latest/docs/framework/react/guides/query-invalidation)
- [TanStack polling and refetch options](https://tanstack.com/query/latest/docs/framework/react/reference/useQuery)
