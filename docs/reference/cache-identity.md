# Endpoint cache identity

> **Reference** — per-method cache identity bindings (URL, named contracts, content-hash).

Per-endpoint, per-HTTP-method binding that decides **whether** Output Cache may run for a method and **how** lookup identity is built (named contract or bounded body hash). The same identity material is reused for **Data Cache keys** when `IDomainDataCache.GetOrSet*` runs. Domain configuration stays shared policy (TTL, Version, tags, Client Cache, auth) — it does not define identity strategies.

Namespace: `CacheOrchestrator.Identity`.

---

## Table of Contents

- [Mental model](#mental-model)
- [Rules](#rules)
- [API surface](#api-surface)
- [Cheat sheet](#cheat-sheet)
- [DX examples](#dx-examples)
- [GET / HEAD with a custom identity](#get-head-with-a-custom-identity)
- [Fail-fast](#fail-fast)
- [Performance notes](#performance-notes)

## Mental model

```text
Domain     → shared cache policy (TTL, Version, tags, Client Cache, auth, Data Cache instance)
Endpoint   → attaches domain + per-method identity binding(s)
Contract   → reusable named extractor (DI); instance stored on endpoint metadata at startup
```

| Layer | Owns |
|-------|------|
| **Domain** | Shared TTL, Version, tags, client `Cache-Control`, auth bypass, Data Cache instance |
| **Endpoint** | Which HTTP methods are eligible, and which identity strategy each method uses |
| **Contract** | Stable key/value material from query or body (`null` = skip caching for that request) |

**Default (no identity API on the endpoint):** Output Cache applies to **GET** and **HEAD** only. Identity is **Url** (route / path, query, and domain vary rules). The policy does not run contracts, content-hash, or request-body I/O on that path. Ordinary GET catalogues and detail routes stay on domain only — that is the common case.

**Opt-in (identity API on the endpoint):** call `.WithCacheIdentity` / `.WithContentHashCacheIdentity` (or the MVC attributes). A method is then Output-Cached only if it has an identity binding. The usual reason to opt in is a **read-only POST** (search, GraphQL, RPC-style read); you can also bind GET/HEAD when you need a custom strategy. There is no separate “allow methods” API.

> [!IMPORTANT]
> **Caching POST.** HTTP POST is normally **not** cached: many POSTs mutate state, and a body-based identity is easy to get wrong. Enabling Output Cache on POST is an explicit choice — the handler must be a **read**, the identity strategy must match what makes two requests “the same”, and create / update / webhook routes under the same domain must stay **without** an identity binding.

> **Edge boundary.** POST identity applies only to the server-side ASP.NET Core Output Cache and Data Cache key flow. `CacheOrchestrator.Edge` currently permits edge storage only for `GET` and `HEAD`, so a cacheable POST still reaches the origin even when its server response is an Output Cache hit.
>
> When that is clear, the feature set fits the case: named contracts for structured body fields, content-hash for opaque documents, or Url when path/query alone identify the read.
>
> **Content-hash cost:** `.WithContentHashCacheIdentity` / `[ContentHashCacheIdentity]` **buffers** the request body (up to `maxBodyBytes`) and hashes it on the identity path. That adds CPU and temporary memory versus Url-only GET/HEAD. Prefer a named contract when you can extract a few stable fields; use content-hash when the body is opaque (typical GraphQL) and keep `maxBodyBytes` as tight as your largest legitimate query allows. Oversized bodies bypass caching (no silent truncation) and are logged at **Warning**.

---

## Rules

1. Identity helpers **always** take an explicit method list.
2. At most **one** binding per HTTP method (case-insensitive) per endpoint.
3. Content-hash is a dedicated API — not `.WithCacheIdentity("ContentHash")`.
4. Oversized body under content-hash → **do not cache** (no silent truncation); logged at **Warning**. Default limit: **65_536** bytes. Named-contract `null` material stays **Debug**.
5. Named contracts are resolved onto endpoint metadata at host start (and lazily if needed) — **not** looked up by name on each request. Unknown contract names fail at resolve time.
6. **Duplicates** fail early: Roslyn analyzer **COIDENTITY001** on attributes; `InvalidOperationException` on fluent registration. Not first/last wins.
7. Create / webhook / mutating POSTs: keep the domain if you need Data Cache or Client Cache headers, but **omit** identity helpers so those methods stay uncached for Output Cache.

Domain vary toggles are configured separately — [vary.md](vary.md).

### Output Cache and Data Cache

Identity bindings apply to **both** layers when each layer is in use:

| Layer | Effect of identity |
|-------|--------------------|
| **Output Cache** | The method may be cached; material goes into Output Cache `VaryByValues` as `co-id:*` (URL binding adds no `co-id:*`). |
| **Data Cache** | Same material is folded into the Fusion/Hybrid key hash when the handler calls `IDomainDataCache.GetOrSetAsync` (or entity helpers) and Data Cache is enabled for the domain. |

Identity does **not** call the Data Cache by itself. Without `GetOrSet*`, only Output Cache (if enabled) stores the HTTP response.

The Data Cache provider-key shape is `co3:{escapedDomain}:{versionHex}:u:{hash}` (URL) or `…:e:{hash}` (entity); identity adds sorted `co-id:{name}` segments into the hash (named contract / content-hash). `CacheIdentities.Url` adds no extra segments. `null` material / content-hash oversize → Data Cache bypass for that request. Details: [cache-keys.md](cache-keys.md). Example wiring: [With Data Cache (GetOrSet)](#with-data-cache-getorset).

---

## API surface

| Call / attribute | Meaning |
|------------------|---------|
| *(omit — no identity API)* | GET/HEAD + Url (typical catalogues and detail GETs) |
| `.WithCacheIdentity(["POST"], "search-v1")` | POST → named DI contract |
| `.WithContentHashCacheIdentity(["POST"], maxBodyBytes: 65_536)` | POST → bounded body XxHash3 |
| `.WithCacheIdentity(["POST"], CacheIdentities.Url)` | POST → Url identity (path/query; body ignored) |
| `[CacheIdentity(["POST"], "search-v1")]` | MVC form of named DI contract |
| `[ContentHashCacheIdentity(["POST"], MaxBodyBytes = 65536)]` | MVC form of content-hash |
| `[CacheIdentity(["POST"], CacheIdentities.Url)]` | MVC form of Url identity on POST |
| `AddCacheIdentityContract<T>()` | Register a singleton `ICacheIdentityContract` |

The same fluent calls and attributes accept **GET** and **HEAD** in the method list (for example `.WithCacheIdentity(["GET", "HEAD"], "…")`). The table uses **POST** because that is the usual opt-in; see [GET / HEAD with a custom identity](#get-head-with-a-custom-identity).

### Minimal API vs MVC

| Intent | Minimal API | MVC |
|--------|-------------|-----|
| Ordinary GET (default) | `.CacheOutputWithDomain("catalog")` | `[CacheDomain("catalog")]` |
| Named contract on POST | `.WithCacheIdentity(["POST"], "product-search-v1")` | `[CacheIdentity(["POST"], "product-search-v1")]` |
| Body hash on POST | `.WithContentHashCacheIdentity(["POST"], maxBodyBytes: 65_536)` | `[ContentHashCacheIdentity(["POST"], MaxBodyBytes = 65536)]` |
| Url on POST (path/query only) | `.WithCacheIdentity(["POST"], CacheIdentities.Url)` | `[CacheIdentity(["POST"], CacheIdentities.Url)]` |
| Custom identity on GET/HEAD | `.WithCacheIdentity(["GET", "HEAD"], "…")` | `[CacheIdentity(["GET", "HEAD"], "…")]` |

### `CacheIdentities.Url`

`CacheIdentities.Url` is a built-in sentinel for `.WithCacheIdentity` / `[CacheIdentity]` (**not** for content-hash APIs). It selects **Url identity**: route / path, query, and domain vary rules — the same strategy GET/HEAD use when no identity helper is present. The **request body is not part of the key**.

On GET/HEAD you rarely need this sentinel: omitting identity helpers already means Url.

On **POST**, Url identity is correct only when path and query already fully identify the read and the body does not change the answer. Examples that fit:

- RPC-style read with ids in the route or query: `POST /api/reports/run?id=42` (empty or ignored body)
- Clients that POST for a GET-like fetch where the resource is named in the URL

Do **not** use Url on POST when the body carries search criteria, GraphQL documents, or filters. Those requests would share one cache entry per URL (or collide) while responses differ by body. Prefer a **named contract** (field extraction) or **content-hash** (opaque body) instead.

---

## Cheat sheet

| Scenario | What to put on the endpoint |
|----------|-----------------------------|
| Normal GET catalogue / detail | Domain only (`.CacheOutputWithDomain` / `[CacheDomain]`) — **no** identity helper |
| Read-only search POST (criteria in body) | `.WithCacheIdentity(["POST"], "…")` / `[CacheIdentity]` |
| GraphQL query (opaque body) | `.WithContentHashCacheIdentity(["POST"], …)` / `[ContentHashCacheIdentity]` |
| Read-only POST identified by path/query only | `.WithCacheIdentity(["POST"], CacheIdentities.Url)` / `[CacheIdentity(…, CacheIdentities.Url)]` |
| Create / webhook | Domain optional; **no** identity helper |

---

## DX examples

Examples are **POST-oriented** (the common identity opt-in). GET routes in the snippets use domain only. Domain settings under `Cache:Domains:…` are omitted.

Runnable demos: content-hash `POST /echo` in [CacheOrchestrator.Minimal](../../samples/CacheOrchestrator.Minimal); named-contract search + create contrast in the [Sample playground — POST identity](../../samples/CacheOrchestrator.Sample/README.md#post-identity-playground).

### Why a named contract?

Use a contract when **Url or raw body hash is the wrong cache key** for your business rule. Typical POST cases:

- Search criteria live in a **JSON body**, not in the URL — Url identity would ignore them; every POST would collide or miss incorrectly.
- Only a **subset** of body fields matters (e.g. `q` + `sort` + `page`). UI-only fields must not fragment the cache.
- Some requests must **not** be cached (empty query, drafts) — return `null`.

Content-hash keys on the entire raw body (good for opaque GraphQL documents). A contract encodes *your* notion of “same search”.

### Contract registration

Example: product search over **POST**. Business rule — two requests are the same cacheable search when normalized **query text**, **sort**, and **page** match. Empty `q` is not cached. Fields such as `uiHint` are ignored on purpose.

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CacheOrchestrator.Identity;
using Microsoft.AspNetCore.Http;

public sealed class ProductSearchIdentityContract : ICacheIdentityContract
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Name => "product-search-v1";

    public async ValueTask<CacheIdentityMaterial?> BuildAsync(
        CacheIdentityContext context,
        CancellationToken cancellationToken)
    {
        HttpRequest request = context.HttpContext.Request;

        // POST body: { "q": "widgets", "sort": "price", "page": 2, "uiHint": "..." }
        request.EnableBuffering();
        SearchBody? body = await JsonSerializer
            .DeserializeAsync<SearchBody>(request.Body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        request.Body.Position = 0;

        string? q = body?.Q;
        if (string.IsNullOrWhiteSpace(q))
            return null; // no searchable term → do not cache

        string sort = string.IsNullOrWhiteSpace(body?.Sort) ? "relevance" : body!.Sort!;
        int page = body?.Page is > 0 ? body.Page.Value : 1;

        return new CacheIdentityMaterial(
        [
            new("q", q.Trim().ToLowerInvariant()),
            new("sort", sort.Trim().ToLowerInvariant()),
            new("page", page.ToString(CultureInfo.InvariantCulture)),
        ]);
    }

    private sealed class SearchBody
    {
        [JsonPropertyName("q")]
        public string? Q { get; set; }

        [JsonPropertyName("sort")]
        public string? Sort { get; set; }

        [JsonPropertyName("page")]
        public int? Page { get; set; }

        [JsonPropertyName("uiHint")]
        public string? UiHint { get; set; }
    }
}

builder.Services.AddCacheIdentityContract<ProductSearchIdentityContract>();
```

### Minimal APIs

```csharp
using CacheOrchestrator.Identity;
using CacheOrchestrator.OutputCache;

// Ordinary GET — domain only; no contract
app.MapGet("/api/catalog", ...)
   .CacheOutputWithDomain("catalog");

// Read-only search POST — contract extracts q/sort/page from the body
app.MapPost("/api/products/search", ...)
   .CacheOutputWithDomain("product-search")
   .WithCacheIdentity(["POST"], "product-search-v1");

// Create under the same domain — no identity helper → POST is not cached by Output Cache
app.MapPost("/api/products", ...)
   .CacheOutputWithDomain("product-search");

// Url on POST: resource named in query; body not part of identity
app.MapPost("/api/reports/run", ...)
   .CacheOutputWithDomain("reports")
   .WithCacheIdentity(["POST"], CacheIdentities.Url);
```

### With Data Cache (GetOrSet)

Same identity binding feeds the Data Cache key when the handler uses `IDomainDataCache` and Data Cache is enabled on the domain:

```csharp
using CacheOrchestrator.DataCache;
using CacheOrchestrator.Identity;
using CacheOrchestrator.OutputCache;

app.MapPost("/api/products/search", async (HttpContext http, IDomainDataCache cache) =>
{
    // Fusion (± Redis L2): key hash includes co-id:* from product-search-v1
    var data = await cache.GetOrSetAsync(http, cancellationToken => LoadSearchAsync(cancellationToken));
    return Results.Json(data);
})
.CacheOutputWithDomain("product-search")
.WithCacheIdentity(["POST"], "product-search-v1");
```

### Simple GraphQL (content-hash)

GraphQL over HTTP is almost always **POST** with the operation in the body. Url identity would ignore the document; a named contract would have to parse GraphQL. Content-hash treats the body as opaque: same document bytes → same cache entry. Cap `maxBodyBytes` to your largest expected query.

Wire only the **query** path this way. Mutations must not share that binding (separate route, or no identity helper on mutation handlers).

```csharp
using CacheOrchestrator.Identity;
using CacheOrchestrator.OutputCache;

// appsettings: Cache:Domains:graphql — TTL / Version as for any other domain

app.MapPost("/graphql", async (HttpContext http) =>
{
    // Your GraphQL executor; body already buffered when content-hash identity ran.
    string document = await new StreamReader(http.Request.Body).ReadToEndAsync(http.RequestAborted);
    object result = await ExecuteGraphQlAsync(document, http.RequestAborted);
    return Results.Json(result);
})
.CacheOutputWithDomain("graphql")
.WithContentHashCacheIdentity(["POST"], maxBodyBytes: 65536);
```

```csharp
// MVC equivalent
[ApiController]
[Route("graphql")]
public sealed class GraphQlController : ControllerBase
{
    [HttpPost]
    [CacheDomain("graphql")]
    [ContentHashCacheIdentity(["POST"], MaxBodyBytes = 65536)]
    public Task<IActionResult> Query() => ...; // queries only
}
```

Same body twice → Output Cache hit (`oc=hit`). Different query text → different hash → separate entry. Body larger than `MaxBodyBytes` → content-hash identity is skipped for that request (no Output Cache store for the identity path), but the endpoint can still read the **full** body. `MaxBodyBytes` limits only identity hashing, not the host request body size (use Kestrel / server limits for that).

### MVC controllers

```csharp
using CacheOrchestrator.Identity;
using CacheOrchestrator.OutputCache;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/products")]
[CacheDomain("product-search")]
public sealed class ProductsController : ControllerBase
{
    // Ordinary GET — domain only; no identity attribute
    [HttpGet]
    public Task<IActionResult> List() => ...;

    // Read-only search POST
    [HttpPost("search")]
    [CacheIdentity(["POST"], "product-search-v1")]
    public Task<SearchResult> Search(...) => ...;

    // Create — same domain, no identity attribute → POST is not cached by Output Cache
    [HttpPost]
    public Task<IActionResult> Create(...) => ...;
}
```

## GET / HEAD with a custom identity

Most GET and HEAD endpoints need **no** identity helper: domain + default Url identity (and domain vary options) is enough.

You can bind GET/HEAD to a named contract or to `CacheIdentities.Url` when you need behaviour that Url + domain options do not cover — for example a key built from a subset of query fields with custom normalization, or returning `null` to skip caching for some GETs.

```csharp
// Uncommon: custom identity on GET/HEAD
app.MapGet("/api/products/search", ...)
   .CacheOutputWithDomain("product-search")
   .WithCacheIdentity(["GET", "HEAD"], "product-search-v1");
```

```csharp
// MVC equivalent
[HttpGet("search")]
[CacheDomain("product-search")]
[CacheIdentity(["GET", "HEAD"], "product-search-v1")]
public Task<SearchResult> Search(...) => ...;
```

POST examples stay in [DX examples](#dx-examples) and the [Minimal API vs MVC](#minimal-api-vs-mvc) table under API surface.

---

## Fail-fast

| Mechanism | When | Effect |
|-----------|------|--------|
| Roslyn analyzer **COIDENTITY001** | `dotnet build` / IDE, on attributes | Compile-time error if the same method is bound twice on one action |
| Fluent / endpoint registration | `With*` conventions when metadata is applied | `InvalidOperationException` (endpoint + method) before the app serves requests |
| Contract catalog | Host start / lazy resolve | Unknown contract name → `InvalidOperationException` |
| Host build / integration tests | Building a web host with fluent duplicates | Surfaces fluent duplicates that the analyzer does not see in `Program.cs` |

If one request needs both field extraction and hashing, implement that in a **single** contract — do not attach two bindings to the same method.

---

## Performance notes

- No identity metadata: the policy does not allocate an identity map, does not buffer the body, and does not resolve contracts — only the GET/HEAD + Url path runs.
- With identity metadata: O(1) lookup by `request.Method`, then the binding already stored on the endpoint (no per-request name lookup).
- Body I/O runs only for content-hash, or for contracts that read the body themselves. Content-hash buffering and hashing cost CPU and temporary memory proportional to the body size (bounded by `maxBodyBytes`); see the warning above.
- Content-hash oversize → **Warning** log, then bypass. Contract returning `null` → **Debug** log, then bypass.

---

## Related

- [Output Cache](output-cache.md) — domain policies, auth, tags, ETag  
- [Data Cache](data-cache.md) — `IDomainDataCache` / Fusion / Hybrid  
- [Cache keys](cache-keys.md) — how `co-id:*` enters Output Cache and Data Cache keys  
- [Vary](vary.md) — domain vary matrix (separate from endpoint identity)  
- [FAQ — Output Cache methods](../guide/faq.md#can-i-cache-post-search-or-graphql-requests-with-output-cache) — POST / search caching caveats  
- [CacheOrchestrator.Minimal](../../samples/CacheOrchestrator.Minimal) — content-hash `POST /echo` sample  
- [Sample playground — POST identity](../../samples/CacheOrchestrator.Sample/README.md#post-identity-playground) — named-contract search + create contrast  

