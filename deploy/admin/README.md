# Run CacheOrchestrator Admin Console App (Docker)

> **Runbook (Docker).** Orientation: [Guide — operations](../../docs/guide/operations.md). Local `dotnet run`: [Admin Console README](../../src/CacheOrchestrator.AdminConsole/README.md). Architecture/security: [docs/reference/admin.md](../../docs/reference/admin.md). Writing rules: [hints/README.md](../../src/CacheOrchestrator.AdminConsole/hints/README.md).

The Admin Console App is an **ops host**: it fans out to your application instances (Admin API) and optionally queries Prometheus for Metrics. It is **not** a NuGet package.

You do **not** need a git checkout to run it — only Docker, a small config file, and the image.

Published image (after a GitHub Release):

```text
ghcr.io/cacheorchestrator/cacheorchestrator-admin-console:<version>
```

Use the same version as the NuGet packages when possible (e.g. `1.2.3` from tag `v1.2.3`).

If you cannot pull the GHCR image, use a **local image name** instead (see [Local image name](#local-image-name) at the end).

---

## Table of Contents

- [What you must configure](#what-you-must-configure)
- [Layout inside the container](#layout-inside-the-container)
- [Quick start](#quick-start)
- [Environment variables](#environment-variables)
- [Logs](#logs)
- [Security](#security)
- [Custom hint rules](#custom-hint-rules)
- [Local image name](#local-image-name)
- [Local development (without Docker)](#local-development-without-docker)

## What you must configure

| Setting | Purpose |
|---------|---------|
| **`AdminConsole:Instances`** | Base URLs of apps that expose `MapCacheOrchestratorAdmin` |
| **`AdminConsole:ApiKey`** | Same value as **`Cache:Admin:ApiKey` on every application instance** (sent as `X-CacheOrchestrator-Admin-Key`) |
| **`AdminConsole:Metrics`** | Optional Prometheus base URL for the Metrics UI |
| **Custom hints** | Optional JSON packs under `data/rules/` |
| **Disabled codes** | Settings UI → `data/disabled.local.json` (persists if `data/` is a volume) |

The image ships **product** rules in `hints/core-hints.json` (always loaded). It does **not** ship your instance list.

---

## Layout inside the container

```text
/app/
  hints/
    core-hints.json          ← product defaults (in the image)
    README.md
  data/                      ← mount a host volume here (recommended)
    rules/
      *.json                 ← your custom packs (0..N files)
    disabled.local.json      ← written by Settings UI (created on first toggle)
  appsettings.json
  appsettings.Production.json
```

Production defaults:

```json
{
  "Hints": {
    "RuleFiles": [ "data/rules/*.json" ],
    "DisabledStatePath": "data/disabled.local.json"
  }
}
```

| If… | Result |
|-----|--------|
| `data/rules/` is empty or missing | Only **core** hints |
| You add one or more `*.json` packs | Core + your rules |
| `data/` is a **writable** volume | Disable checkboxes survive restart |
| You mount only config, not `data/` | Custom rules lost on recreate; disables ephemeral |

Do **not** mount over all of `/app/hints` — that would hide `core-hints.json` from the image.

`*.sample.json` files are **ignored** by the loader (safe to keep examples in the folder). Rename `team-ops.sample.json` → `team-ops.json` to activate the sample pack in this repo’s example `data/` folder.

---

## Quick start

Create a small folder (anywhere), e.g. `admin/`:

```text
admin/
  admin-appsettings.json
  data/
    rules/          ← may be empty
```

### 1. Config file

Save the following as **`admin-appsettings.json`** (same content as [appsettings.example.json](appsettings.example.json)).

**Defaults are ready for the Playground sample** (`samples/CacheOrchestrator.Sample` on port **5289**, `ApiKey` **`dev-admin-key`**).  
For your own environment, change **`ApiKey`** and **`Instances`** (and Metrics if you use it).  
**`ApiKey` must always match `Cache:Admin:ApiKey` on each monitored application.**

```jsonc
{
  "AdminConsole": {
    // Defaults below match the Playground sample (samples/CacheOrchestrator.Sample).
    // For your own environment: set ApiKey + Instances (+ Metrics) to your apps.

    // Must match Cache:Admin:ApiKey on every monitored application instance
    // (sent as X-CacheOrchestrator-Admin-Key). Playground default: "dev-admin-key".
    // Prefer env AdminConsole__ApiKey in real deploys.
    "ApiKey": "dev-admin-key",

    "RequestTimeoutMs": 3000,
    "Parallelism": 8,
    "AdminApiPathPrefix": "/cache-admin/local",

    // Playground listens on host port 5289. From the Admin container use host.docker.internal
    // (Docker Desktop / Linux with host-gateway). For custom apps: service DNS or real host URLs.
    // localhost inside the container is the Admin process itself — not the host machine.
    "Instances": [
      {
        "id": "local-playground",
        "url": "http://host.docker.internal:5289"
      }
    ],

    // Optional. Playground can expose /metrics; point BaseUrl at your Prometheus if you scrape it.
    // Set Enabled false if you have no Prometheus-compatible store.
    "Metrics": {
      "Enabled": false,
      "Provider": "Prometheus",
      "BaseUrl": "http://host.docker.internal:9090"
    },

    // Custom packs: data/rules/*.json (empty = product core-hints only).
    // Disables from Settings UI: data/disabled.local.json
    "Hints": {
      "RuleFiles": [ "data/rules/*.json" ],
      "DisabledCodes": [],
      "DisabledStatePath": "data/disabled.local.json"
    }
  }
}
```

URLs must be reachable **from inside the Admin container**. Playground-on-host → `host.docker.internal`; apps in Compose/K8s → service DNS names.

### 2. Operator data directory

```bash
mkdir -p data/rules
```

Optional: add your own hint packs as `data/rules/*.json`.

### 3. Run (`docker run`)

From the folder that contains `admin-appsettings.json` and `data/`:

```bash
docker run --rm -p 5188:8080 \
  -e AdminConsole__ApiKey=dev-admin-key \
  -v "$PWD/admin-appsettings.json:/app/appsettings.Production.json:ro" \
  -v "$PWD/data:/app/data" \
  --add-host=host.docker.internal:host-gateway \
  ghcr.io/cacheorchestrator/cacheorchestrator-admin-console:latest
```

- Playground: keep `dev-admin-key` and start the sample on port 5289. For your apps, change the env key to match their `Cache:Admin:ApiKey` (overrides the file if both are set).  
- `$PWD` = current folder in the terminal (or use a full path to the files).  
- `--add-host=host.docker.internal:host-gateway` helps Linux Docker resolve the host; Docker Desktop usually works without it.  
- If you cannot pull from GHCR, replace the last line with `cacheorchestrator-admin-console:local` (see below).

Open http://localhost:5188/ — health at http://localhost:5188/health .

### 4. Run with Compose (optional)

Same folder layout. Save as `docker-compose.yml` (or use [docker-compose.example.yml](docker-compose.example.yml) and point the volume at `admin-appsettings.json`):

```yaml
# Defaults target the Playground sample (dev-admin-key, host:5289).
# For your environment: change admin-appsettings.json and ADMIN_API_KEY.
# ApiKey must match Cache:Admin:ApiKey on every application instance.

services:
  admin:
    image: ghcr.io/cacheorchestrator/cacheorchestrator-admin-console:latest
    ports:
      - "5188:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      # Playground: dev-admin-key. Custom env: same as each app's Cache:Admin:ApiKey.
      AdminConsole__ApiKey: ${ADMIN_API_KEY:-dev-admin-key}
    volumes:
      - ./admin-appsettings.json:/app/appsettings.Production.json:ro
      - ./data:/app/data
    # Reach Playground (or other apps) on the Docker host
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

```bash
# optional override: export ADMIN_API_KEY=your-secret
docker compose up -d
```

Compose is optional: use it if you prefer a file over a long `docker run` line. Behaviour is the same.

---

## Environment variables

ASP.NET Core `__` nesting works for scalars and secrets:

```text
AdminConsole__ApiKey=...          # same as Cache:Admin:ApiKey on app instances
AdminConsole__Metrics__Enabled=true
AdminConsole__Metrics__BaseUrl=http://prometheus:9090
AdminConsole__Metrics__BearerToken=...
AdminConsole__Instances__0__Id=app-1
AdminConsole__Instances__0__Url=http://app-1:8080
```

For several instances, a **mounted JSON file** is usually clearer than long env lists.

---

## Logs

The process logs to **stdout/stderr**. No log agent is required inside the image.

```bash
docker logs -f <container>
```

Optional structured console logs:

```text
Logging__Console__FormatterName=json
```

Attach Loki, Fluent Bit, Datadog, etc. at the host/cluster level (Docker logging driver or sidecar), not inside this image.

---

## Security

- Put VPN / SSO reverse proxy in front of the Admin UI (no built-in login).
- Treat `ApiKey` as a secret (env or secret store). It must match each instance’s `Cache:Admin:ApiKey`.
- Invalidate / Version / TTL change **live** cache state on target instances.

See [docs/reference/admin.md](../../docs/reference/admin.md).

---

## Custom hint rules

Full JSON format and path catalog: **[src/CacheOrchestrator.AdminConsole/hints/README.md](../../src/CacheOrchestrator.AdminConsole/hints/README.md)**.

After adding or editing files under `data/rules/`, open **Settings → Reload** (or restart the container).

---

## Local image name

**Normal users:** pull from GHCR after a release — you do **not** build from source.

**Only if** you cannot use the GHCR image, or you are changing the Admin Console App code:

1. Clone this repository.  
2. From the **repository root**:

```bash
docker build -f src/CacheOrchestrator.AdminConsole/Dockerfile -t cacheorchestrator-admin-console:local .
```

3. Use the same `docker run` / Compose steps as above, but set the image to:

```text
cacheorchestrator-admin-console:local
```

instead of `ghcr.io/cacheorchestrator/cacheorchestrator-admin-console:latest`.

---

## Local development (without Docker)

```bash
dotnet run --project src/CacheOrchestrator.AdminConsole
```

Development uses playground defaults (`Instances` → `:5289`, hints under `hints/`). See [src/CacheOrchestrator.AdminConsole/README.md](../../src/CacheOrchestrator.AdminConsole/README.md).
