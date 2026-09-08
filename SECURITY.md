# Security Policy

## Reporting a vulnerability

**Do not** file a public GitHub issue for security vulnerabilities.

Please report privately so we can fix and, if needed, coordinate a release before disclosure.

### Preferred

1. Open a **[GitHub Security Advisory](https://github.com/CacheOrchestrator/CacheOrchestrator/security/advisories/new)** on this repository (private), **or**
2. Email the maintainer via the address on the [GitHub profile](https://github.com/amarinsek) with subject  
   `CacheOrchestrator security: <short title>`

### Include if possible

- Affected package or application and version (Core, AspNetCore, FusionCache, HybridCache, Redis leaves/meta, HttpBus, EF invalidation, Edge/provider, or Admin Console)
- Target framework and ASP.NET Core version
- Description of the issue and impact
- Minimal reproduction or proof-of-concept
- Whether the issue is already public elsewhere

### What to expect

- Acknowledgement when the report is received (typically within a few days)
- A fix or mitigation plan for confirmed issues
- Credit in the advisory / GitHub Release notes if you want it (optional)

## Non-security bugs

Use [GitHub Issues](https://github.com/CacheOrchestrator/CacheOrchestrator/issues) with the bug template.

## Scope notes for this library

CacheOrchestrator coordinates ASP.NET Core Output Cache, FusionCache or HybridCache data caching, client Cache-Control and optional tag-native Edge caching. Reports may also concern HttpBus command delivery/authentication, EF invalidation, Admin APIs or the Admin Console application. Security-sensitive defaults and boundaries include:

- **Authenticated traffic is not Output-Cached by default** (`AuthBypassMode: AuthenticatedOrAuthorization`). Choose another `AuthBypassMode` value when authenticated responses must be cached.
- Client cache is **blocked** for that traffic unless you explicitly opt in; the HTTP Data Cache path also respects the default authentication bypass.
- Edge response policy bypasses private/authenticated responses; provider credentials and queued destination snapshots are confidential control-plane data.
- Admin and HttpBus endpoints require deployment-appropriate authentication and network boundaries. Admin Console is an operator application; its upstream credentials must remain server-side. See [Admin security](docs/reference/admin.md) and [cluster authentication](docs/reference/cluster-bus.md).
- Core data APIs do not infer an HTTP user or tenant. Applications own domain/context identity and must include every authorization dimension needed to prevent cache sharing across users or tenants.

Misconfiguration (e.g. caching private user data as `public` without per-user vary) is an application responsibility — see [docs/guide/faq.md](docs/guide/faq.md) and [docs/reference/output-cache.md](docs/reference/output-cache.md).

### Diagnostic response headers

By default the library emits **`X-CacheOrchestrator`** (hit/miss, domain, schedule phase). This is useful for operations and debugging but is client-visible. To disable diagnostic headers in production:

```json
"Cache": { "EmitDiagnosticsHeaders": false }
```

Metrics and tracing are unaffected. See [docs/reference/observability.md](docs/reference/observability.md).
