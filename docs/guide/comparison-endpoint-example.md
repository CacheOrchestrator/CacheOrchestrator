# Comparison: same endpoint with and without CacheOrchestrator

> **Guide** — side-by-side application code for the same cached read.

This page is a **worked example** next to [Comparison](comparison.md). It does not replace the engines (Output Cache, FusionCache, `Cache-Control`). It shows how much **policy and identity** stays in application C# when you wire those engines by hand, versus declaring the same policy once in domain configuration.

Read **with CacheOrchestrator first** (short), then **without** (the surface you would otherwise own). The “without” snippets are deliberately complete enough to be honest, not a full production host — they omit Redis registration and OpenTelemetry so the **per-endpoint surface** stays readable.

## Scenario

`GET /api/catalog/items?page=&sort=` returns a JSON (or XML) page of catalogue items.

| Requirement | Choice |
|-------------|--------|
| Output Cache TTL | 10 minutes |
| Data Cache soft TTL | 30 minutes |
| Fusion fail-safe / hard cap | fail-safe 2 hours; hard TTL 12 hours |
| Client `Cache-Control` | `public`; calm `max-age=3600`, floor `900` before cutover |
| Client Cache Schedule | ramp down toward `2026-09-01T00:00:00Z` (`MustRevalidateNearUpdate`) |
| Vary | `Accept` (prefer `application/json`) + query `page`, `sort` only |
| Authenticated callers | do not share the public Output Cache entry (auth bypass) |
| Generation | `Version` `2026-08` in keys / validators |
| ETag | weak validator from Version |
| Diagnostics | `X-CacheOrchestrator` (domain, oc/dc, phase, ms) |

---

## With CacheOrchestrator

Almost all of the table is **domain configuration**. The endpoint names the domain and loads data.

### Configuration

```json
{
  "Cache": {
    "Domains": {
      "catalog": {
        "Version": "2026-08",
        "DataCache": { "TtlSeconds": 1800 },
        "OutputCache": { "TtlSeconds": 600, "ETagMode": "Version" },
        "ClientCache": { "Cacheability": "Public", "TtlSeconds": 3600, "TtlMinSeconds": 900, "ScheduledUpdateUtc": "2026-09-01T00:00:00Z", "MustRevalidateNearUpdate": true },
        "FusionCache": { "HardTtlSeconds": 43200, "FailSafeSeconds": 7200 },
        "VaryByAccept": true,
        "AcceptNormalizationList": [ "application/json" ],
        "VaryByQueryKeys": [ "page", "sort" ]
      }
    }
  }
}
```

Default auth bypass keeps authenticated / `Authorization` traffic out of the shared Output Cache unless you opt into caching it. Vary and Accept normalization apply to **both** Output Cache and Data Cache keys. Client Cache Schedule drives `max-age` and `phase=` on `X-CacheOrchestrator`.

### Endpoint

```csharp
builder.Services.AddCacheOrchestrator(builder.Configuration);
// …
app.UseCacheOrchestrator();

app.MapGet("/api/catalog/items", async (HttpContext http, IDomainDataCache cache, CancellationToken cancellationToken) =>
{
    var page = await cache.GetOrSetAsync(
        http,
        token => LoadPageAsync(http.Request, token),
        cancellationToken);

    return Results.Json(page);
})
.CacheOutputWithDomain("catalog");
```

### Cutover / purge

```csharp
await invalidator.InvalidateDomainAsync("catalog", cancellationToken);
// or bump Domains:catalog:Version in shared config
```

`Cache-Control` (including schedule ramp), Output Cache policy, Data Cache TTL/Version/vary/fail-safe, ETag, tags, and `X-CacheOrchestrator` come from the domain snapshot — not from endpoint-local helpers.

---

## Without CacheOrchestrator

The same requirements mean **C# policy builders, key helpers, header math, and dual-store purge** — copied (and kept aligned) for every similar endpoint.

### Host registration (sketch)

```csharp
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("catalog", policy => policy
        .Expire(TimeSpan.FromMinutes(10))
        .Tag("domain:catalog")
        .SetVaryByHeader("Accept")
        .SetVaryByQuery("page", "sort")
        .SetVaryByValue("data-version", _ => "2026-08")
        // Auth: exclude authenticated traffic or vary by user — must stay aligned with Data Cache.
        // ETag: add your own middleware / filter using Version.
        );
});

builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(o =>
    {
        // Soft / logical TTL (CO DataCache.TtlSeconds), capped by hard TTL (CO FusionCache.HardTtlSeconds).
        TimeSpan soft = TimeSpan.FromMinutes(30);
        TimeSpan hardTtl = TimeSpan.FromHours(12);
        if (soft > hardTtl)
            soft = hardTtl;

        o.Duration = soft;
        // Fail-safe window after logical expiry (CO FusionCache.FailSafeSeconds → FailSafeMaxDuration).
        o.SetFailSafe(true, TimeSpan.FromHours(2));
    });
```

### Endpoint (sketch)

```csharp
app.MapGet("/api/catalog/items", async (HttpContext http, IFusionCache fusion, CancellationToken cancellationToken) =>
{
    if (http.User.Identity?.IsAuthenticated == true
        || http.Request.Headers.ContainsKey("Authorization"))
    {
        http.Response.Headers.CacheControl = "private, max-age=0";
        // Skip Output Cache policy / shared Fusion entry yourself.
        return Results.Json(await LoadPageAsync(http.Request, cancellationToken));
    }

    const string version = "2026-08";

    // Prefer-list must match whatever you intended for Output Cache vary.
    string rawAccept = http.Request.Headers.Accept.ToString();
    string accept = rawAccept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
        ? "application/json"
        : rawAccept;

    // Must mirror Output Cache: normalized Accept + page + sort + version.
    string pageKey = http.Request.Query["page"].ToString();
    string sort = http.Request.Query["sort"].ToString();
    string key = $"catalog:items:{version}:a={accept}|p={pageKey}|s={sort}";

    var page = await fusion.GetOrSetAsync(
        key,
        async (_, token) => await LoadPageAsync(http.Request, token),
        tags: ["domain:catalog"],
        token: cancellationToken);

    // Hand-rolled Client Cache Schedule: calm → approaching → hold toward cutover.
    var cutover = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    const int calm = 3600, floor = 900;
    // … linear ramp / hold logic, must-revalidate near update …
    int maxAge = calm; // placeholder — real code is non-trivial

    http.Response.Headers.CacheControl = $"public, max-age={maxAge}, must-revalidate";
    http.Response.Headers.ETag = $"W/\"{version}\"";
    http.Response.Headers["X-CacheOrchestrator"] = $"domain=catalog; version={version}; /* oc/dc/phase/ms — build yourself */";

    return Results.Json(page);
})
.CacheOutput("catalog");
```

### Cutover / purge

```csharp
await fusion.RemoveByTagAsync("domain:catalog", cancellationToken);
await outputCache.EvictByTagAsync("domain:catalog", cancellationToken);
```

You still own copying this policy to the next catalogue endpoint without drift (vary lists, schedule math, ETag, fail-safe, auth branch, diagnostics).

---

## Side-by-side

| Piece | With CacheOrchestrator | Without |
|-------|------------------------|---------|
| TTLs (OC / DC / client) | nested `*Seconds` | OC policy + Fusion options + header |
| Fail-safe / hard TTL | `FusionCache` section | Fusion entry options in C# |
| Accept prefer-list | `AcceptNormalizationList` | helper + keep OC/DC in sync |
| Query vary | `VaryByQueryKeys` | OC builder **and** key helper |
| Client schedule ramp | `ScheduledUpdateUtc` + min TTL | custom `ClientMaxAgeSeconds` |
| ETag from Version | `ETagMode` | manual `ETag` header |
| Auth vs shared cache | domain defaults | endpoint / policy branch |
| `X-CacheOrchestrator` + phase | built-in | hand-built header |
| Purge OC + Data Cache | `InvalidateDomainAsync` | two tag APIs |
| Endpoint body | `GetOrSetAsync` + `.CacheOutputWithDomain` | key + tags + headers + auth + diagnostics |

---

## Related

- [Comparison](comparison.md) — when the library is a strong fit vs direct APIs  
- [Client Cache Schedule](client-cache-schedule.md) — calm / approaching / hold  
- [Getting started](getting-started.md) — minimal host wiring  
- [Domain vary dimensions](../reference/vary.md) — query / Accept / auth settings  
- [Configuration](../reference/configuration.md) — full `Cache` schema  
- [Invalidation](../reference/invalidation.md) — domain and entity purge  
