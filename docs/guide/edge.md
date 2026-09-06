# Edge cache integration

> **Guide** — extend domain cache policy and invalidation to a tag-native edge cache.

`CacheOrchestrator.Edge` projects the domain and entity tags already used by Output Cache and Data Cache into opaque edge tags. Provider packages write the appropriate origin metadata and invalidate matching objects in the background. Endpoint code and invalidation calls do not change. Built-in providers are `CacheOrchestrator.Edge.Cloudflare` and `CacheOrchestrator.Edge.Varnish`.

## Scope

The first provider contract is deliberately tag-native. It requires an edge cache that can attach several tags to a response and invalidate all objects carrying any of those tags. This preserves the existing semantics for domains, entities, entity kinds, collection members, `DependsOn`, and aliases without maintaining a second URL index.

[Azure Front Door](https://learn.microsoft.com/en-us/azure/frontdoor/front-door-caching#cache-purge) is not included. Its path/wildcard purge cannot represent arbitrary entity footprints. A correct automatic fallback would purge an entire domain for every entity change; selective parity would require a durable tag-to-public-URL reverse index, which is outside CacheOrchestrator's store-free boundary.

## Packages and registration

Install the normal web package plus one or both provider packages:

```bash
dotnet add package CacheOrchestrator --prerelease
dotnet add package CacheOrchestrator.Edge.Cloudflare --prerelease
dotnet add package CacheOrchestrator.Edge.Varnish --prerelease
```

```csharp
using CacheOrchestrator.Edge.Cloudflare;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Varnish;

builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEdge(
    builder.Configuration,
    edge =>
    {
        edge.AddCloudflare();
        edge.AddVarnish();
    });
```

The Edge call is a sibling registration so the existing meta package does not acquire provider dependencies. Register only providers referenced by configuration.

## Configuration

```json
{
  "Cache": {
    "Namespace": "store-production",
    "EdgeInstances": {
      "public-edge": {
        "Provider": "Cloudflare",
        "Cloudflare": {
          "ZoneId": "from-secret-configuration",
          "ApiToken": "from-secret-configuration"
        }
      },
      "private-varnish": {
        "Provider": "Varnish",
        "Varnish": {
          "PurgeUrl": "http://varnish-internal/cache-orchestrator/purge",
          "ApiKey": "from-secret-configuration",
          "ApiKeyHeaderName": "X-CacheOrchestrator-Key"
        }
      }
    },
    "EdgeQueue": {
      "Capacity": 1024,
      "MaxAttempts": 3,
      "FlushIntervalSeconds": 1,
      "RetryBaseDelaySeconds": 1
    },
    "DomainDefaults": {
      "Edge": {
        "Enabled": false,
        "Instance": "public-edge",
        "TtlSeconds": 300,
        "StaleWhileRevalidateSeconds": 30,
        "StaleIfErrorSeconds": 300
      }
    },
    "Domains": {
      "catalog": {
        "Edge": {
          "Enabled": true,
          "TtlSeconds": 600
        }
      }
    }
  }
}
```

Supply Cloudflare and Varnish credentials through environment variables, user secrets, or another secret provider. Do not commit production credentials. The Cloudflare token must be able to purge cache content for its zone; a Varnish purge URL must be internal and protected by an ACL, mTLS, an authenticating proxy, or the configured API-key header.

Edge TTL is independent of Output Cache, Data Cache, and browser TTLs. Cloudflare receives `Cloudflare-CDN-Cache-Control`; Varnish receives private origin-to-VCL headers. Neither provider replaces the ordinary browser `Cache-Control` header. The common model offers TTL, `stale-while-revalidate`, and `stale-if-error`, but each provider declares which stale semantics it can preserve and unsupported combinations fail startup validation. It intentionally omits `no-transform`, `s-maxage`, and arbitrary directives because their behavior is not safely portable and some combinations disable stale serving.

## Response flow

For an eligible public response, the Cloudflare provider emits:

- `Cloudflare-CDN-Cache-Control` with the configured edge TTL and optional stale windows;
- `Cache-Tag` containing deterministic opaque keys for the domain and complete entity footprint.

The Varnish provider emits the same projected footprint in `xkey`, plus `X-CacheOrchestrator-Edge-Ttl`, optional `X-CacheOrchestrator-Edge-Grace`, and a cacheability marker consumed and removed by the required VCL.

Canonical IDs are never written directly. The stable `coe1-...` projection hashes both the edge namespace and the canonical tag, preserving case-sensitive identity on providers whose tags are case-insensitive and isolating applications that share an edge service.

Edge storage is limited to `GET` and `HEAD`. `CacheIdentity` can opt a read-only `POST` into ASP.NET Core Output Cache, but it does not make that response edge-cacheable; POST and all other methods receive provider-specific bypass metadata and no edge tags.

Authenticated/private/no-store responses, responses with `Set-Cookie` or `Authorization`, and non-cacheable status codes also receive provider-specific edge bypass metadata and no tags. If the complete tag set would exceed the provider header limit, edge storage is disabled for that response. Partial or domain-only tagging would make later entity invalidation incomplete, so the implementation fails closed instead.

CacheOrchestrator stores internal staged footprint metadata with an Output Cache entry. On a hit it removes that internal header before sending the response and regenerates the provider headers from the original complete tag set, rather than rebuilding a smaller footprint from endpoint metadata alone.

## Invalidation and delivery

All existing invalidation entry points continue to be authoritative. After local invalidation, the edge observer projects the same canonical tags and enqueues them. A bounded background worker coalesces duplicates, respects the provider batch limit, and retries transient errors and rate limits with exponential backoff, jitter, and `Retry-After`.

With `CacheOrchestrator.HttpBus`, only the process that initiated the logical invalidation queues the external call. Peers still apply their local cache invalidation, but remote application is marked and does not duplicate provider calls. Local-only Admin invalidation still invalidates the edge cache.

Changing the domain `Version` alone does not enqueue a purge; use an explicit domain invalidation when existing Edge URLs must be retired. `CacheInvalidationResult.Succeeded` reports local Data Cache/Output Cache success, not successful Edge queueing or provider completion. Inspect Edge metrics and logs for those outcomes. Edge invalidation does not remove responses already cached by browsers.

The built-in queue is in-memory and best-effort. It drains within the host's graceful shutdown deadline, but a process crash can lose queued work. Invalidations are idempotent. Applications requiring crash-safe delivery can register their own `IEdgeInvalidationQueue` before `AddCacheOrchestratorEdge` and persist `EdgeInvalidationJob` records in an outbox. Because the replacement contract is enqueue-only, that application also owns the durable outbox dispatcher; the built-in worker drains only its built-in channel.

Cloudflare currently documents a 16 KB aggregate `Cache-Tag` header limit and up to 100 tag operations per purge request. The provider encodes those limits and the worker splits batches accordingly. See [Cloudflare cache-tag limits](https://developers.cloudflare.com/cache/how-to/purge-cache/purge-by-tags/), [purge availability and rate limits](https://developers.cloudflare.com/cache/how-to/purge-cache/), and [CDN cache-control precedence](https://developers.cloudflare.com/cache/concepts/cdn-cache-control/).

## Custom providers

General contracts and models live in `CacheOrchestrator.Edge`. A custom tag-native integration registers a response provider and an invalidation provider with the same `Name`:

```csharp
services.AddSingleton<IEdgeResponseProvider, MyEdgeResponseProvider>();
services.AddSingleton<IEdgeInvalidationProvider, MyEdgeInvalidationProvider>();
services.AddCacheOrchestratorEdge(configuration);
```

`IEdgeResponseProvider` performs only synchronous response-header work. `IEdgeInvalidationProvider.InvalidateAsync` performs the remote operation and returns a structured transient/permanent result; batching and retries remain in the neutral package. Both providers declare limits and stale capabilities through `EdgeProviderCapabilities`, and startup rejects a configured policy that the selected response provider cannot preserve.

### Minimal custom provider

A normal provider does **not** need its own worker. Implement both provider interfaces on one singleton. The response method runs in the HTTP response path and must only write headers; the invalidation method may perform network I/O because the built-in Edge worker calls it after invalidation has been queued.

Install the neutral package alongside the application's normal web composition:

```bash
dotnet add package CacheOrchestrator.Edge --prerelease
```

The following deliberately small provider targets an imaginary proxy that consumes `X-Example-Edge-Control` and `X-Example-Edge-Tags`, and accepts tag purges at `POST /purge/tags`:

```csharp
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CacheOrchestrator.Edge.Providers;
using Microsoft.AspNetCore.Http;

public sealed class ExampleEdgeProvider : IEdgeResponseProvider, IEdgeInvalidationProvider
{
    public const string ProviderName = "Example";
    public const string HttpClientName = ProviderName;

    private readonly IHttpClientFactory _httpClientFactory;

    public ExampleEdgeProvider(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public string Name => ProviderName;

    public EdgeProviderCapabilities Capabilities { get; } = new()
    {
        SupportsTagInvalidation = true,
        MaxResponseTagBytes = 8 * 1024,
        MaxInvalidationBatchSize = 50,
        SupportsStaleWhileRevalidate = false,
        SupportsStaleIfError = false
    };

    // HTTP/Output Cache path: headers only. Never call the provider API here.
    public void ApplyResponseMetadata(HttpResponse response, EdgeResponseMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!metadata.IsCacheable)
        {
            response.Headers.Remove("X-Example-Edge-Tags");
            response.Headers["X-Example-Edge-Control"] = "no-store";
            return;
        }

        response.Headers["X-Example-Edge-Control"] =
            $"max-age={Math.Max(0, (long)metadata.Ttl.TotalSeconds)}";
        response.Headers["X-Example-Edge-Tags"] = string.Join(' ', metadata.Tags);
    }

    // Background worker path: network I/O is expected here.
    public async ValueTask<EdgeInvalidationResult> InvalidateAsync(
        EdgeInvalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "purge/tags",
                new { tags = request.Tags },
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return EdgeInvalidationResult.Success;

            bool transient = response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            return new EdgeInvalidationResult
            {
                IsTransient = transient,
                RetryAfter = response.Headers.RetryAfter?.Delta,
                Error = $"Example Edge returned HTTP {(int)response.StatusCode}."
            };
        }
        catch (HttpRequestException)
        {
            return new EdgeInvalidationResult
            {
                IsTransient = true,
                Error = "Example Edge transport request failed."
            };
        }
    }
}
```

Register the same singleton for both roles, then register the neutral Edge integration:

```csharp
using CacheOrchestrator.DependencyInjection;
using CacheOrchestrator.Edge.DependencyInjection;
using CacheOrchestrator.Edge.Providers;
using Microsoft.Extensions.DependencyInjection;

Uri purgeBaseUrl = new(
    builder.Configuration["Example:PurgeBaseUrl"]
    ?? throw new InvalidOperationException("Example:PurgeBaseUrl is required."));

builder.Services.AddHttpClient(ExampleEdgeProvider.HttpClientName, client =>
    client.BaseAddress = purgeBaseUrl);
builder.Services.AddSingleton<ExampleEdgeProvider>();
builder.Services.AddSingleton<IEdgeResponseProvider>(services =>
    services.GetRequiredService<ExampleEdgeProvider>());
builder.Services.AddSingleton<IEdgeInvalidationProvider>(services =>
    services.GetRequiredService<ExampleEdgeProvider>());

builder.Services.AddCacheOrchestrator(builder.Configuration);
builder.Services.AddCacheOrchestratorEdge(builder.Configuration);
```

The example deliberately uses `Example` for the provider's `Name`, named `HttpClient`, and settings section. The `Provider` value must match `Name`; `public-edge` is only the independently chosen Edge instance name:

```json
{
  "Example": {
    "PurgeBaseUrl": "https://edge-control.internal/"
  },
  "Cache": {
    "EdgeInstances": {
      "public-edge": {
        "Provider": "Example"
      }
    },
    "Domains": {
      "catalog": {
        "Edge": {
          "Enabled": true,
          "Instance": "public-edge",
          "TtlSeconds": 300
        }
      }
    }
  }
}
```

The runtime path is intentionally split:

1. A cacheable `GET`/`HEAD` response calls `ApplyResponseMetadata`; this is the only provider code on the response and Output Cache path.
2. A domain/entity invalidation projects tags and enqueues an `EdgeInvalidationJob`; it does not call the provider network API.
3. The built-in hosted worker coalesces jobs, applies `MaxInvalidationBatchSize`, and calls `InvalidateAsync`; transient results are retried according to `Cache:EdgeQueue`.

The response path is not zero-cost: neutral tag projection, string formatting, and header writes still occur. The important boundary is that it performs no provider network I/O and starts no detached work, so provider latency is kept out of Output Cache response processing.

Do not start background work with `Task.Run` from `ApplyResponseMetadata` or `InvalidateAsync`. Let `InvalidateAsync` represent one awaited provider batch. This single-endpoint example intentionally does not use `request.InstanceName`; a reusable production package should use it to resolve strongly typed per-instance settings and should add startup validation, authentication, sanitized errors, timeout handling, and provider-focused tests. Those details are omitted here to keep the execution boundary visible.

## Varnish VCL contract

The Varnish provider uses the official `xkey` VMOD convention: origin responses contain space-separated `xkey` values and invalidation sends `PURGE` with `xkey-purge`. The VMOD is an additional Varnish module and its current documentation calls out maintenance-mode scalability limitations, so validate it against the expected object and key count before a large deployment. See the [official xkey contract](https://github.com/varnish/varnish-modules/blob/master/src/vmod_xkey.vcc) and [Varnish grace behavior](https://varnish-cache.org/docs/6.5/users-guide/vcl-grace.html).

The application emits edge-only TTL headers so browser `Cache-Control` remains unchanged. Adapt the following minimum VCL to your ACL and topology:

```vcl
vcl 4.1;
import std;
import xkey;

acl edge_purgers {
    "127.0.0.1";
    # Add only the application network that may call PurgeUrl.
}

sub vcl_recv {
    if (req.method == "PURGE" && req.url == "/cache-orchestrator/purge") {
        if (client.ip !~ edge_purgers) {
            return (synth(403, "Forbidden"));
        }
        if (!req.http.xkey-purge) {
            return (synth(400, "Missing xkey-purge"));
        }
        set req.http.n-gone = xkey.purge(req.http.xkey-purge);
        return (synth(200, "Invalidated"));
    }

    if (req.method != "GET" && req.method != "HEAD") {
        return (pass);
    }
}

sub vcl_backend_response {
    if (beresp.http.X-CacheOrchestrator-Edge-Cacheable == "0") {
        set beresp.uncacheable = true;
        set beresp.ttl = 0s;
    } else if (beresp.http.X-CacheOrchestrator-Edge-Cacheable == "1") {
        set beresp.ttl = std.duration(
            beresp.http.X-CacheOrchestrator-Edge-Ttl + "s", 0s);
        if (beresp.http.X-CacheOrchestrator-Edge-Grace) {
            set beresp.grace = std.duration(
                beresp.http.X-CacheOrchestrator-Edge-Grace + "s", 0s);
        }
    }
    unset beresp.http.X-CacheOrchestrator-Edge-Cacheable;
    unset beresp.http.X-CacheOrchestrator-Edge-Ttl;
    unset beresp.http.X-CacheOrchestrator-Edge-Grace;
}

sub vcl_deliver {
    if (req.method != "GET" && req.method != "HEAD") {
        set resp.http.Cache-Status = "Varnish; fwd=method";
    } else if (obj.hits > 0 && obj.ttl < 0s) {
        set resp.http.Cache-Status = "Varnish; hit; detail=stale";
    } else if (obj.hits > 0) {
        set resp.http.Cache-Status = "Varnish; hit";
    } else {
        set resp.http.Cache-Status = "Varnish; fwd=uri-miss";
    }
    unset resp.http.xkey;
}
```

[`Cache-Status`](https://www.rfc-editor.org/rfc/rfc9211.html) is the standard RFC 9211 response field and lets diagnostics distinguish a Varnish hit from an origin response. Varnish does not use Cloudflare's proprietary status names; the VCL reports a grace delivery as `hit; detail=stale`, which the Playground maps to `EDGE-REFRESH`, and a method bypass as `fwd=method`. `StaleWhileRevalidateSeconds` maps to `beresp.grace`. `StaleIfErrorSeconds` is rejected at startup for Varnish because `grace` does not provide the same portable, independently configurable stale-if-error contract. Cloudflare supports both settings.

## Integration test

The integration suite starts the pinned official `varnish:9.0.3-5` image with the `xkey` VMOD and an nginx origin. `VarnishEdgeDockerTests` verifies the complete `MISS` → `HIT` → tag `PURGE` → `MISS` flow through the public `IEdgeInvalidationProvider`, and verifies that origin-only edge headers do not reach the client. Docker must be running; execute:

```bash
dotnet test tests/CacheOrchestrator.IntegrationTests/CacheOrchestrator.IntegrationTests.csproj \
  --filter FullyQualifiedName~VarnishEdgeDockerTests
```

For an interactive version of the same flow, run [Playground Lab 06](../../samples/CacheOrchestrator.Sample/labs/README.md#stage-06-varnish-edge). It places Varnish in front of the Stage 02 single-origin/Redis-L2 topology; the origin remains internal to the Compose network.

## Operational signals

The `CacheOrchestrator.Edge` meter emits:

- `cache_orchestrator.edge.invalidation.queued`;
- `cache_orchestrator.edge.invalidation.keys`;
- `cache_orchestrator.edge.invalidation.failures`;
- `cache_orchestrator.edge.tags.fallback` (the response was made edge `no-store`).

Labels contain domain, instance, provider, and bounded reason values where applicable. Raw resource IDs, projected tags, URLs, and credentials are not metric labels or log fields.
