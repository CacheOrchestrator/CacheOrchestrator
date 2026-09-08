# Releasing CacheOrchestrator

> **Contributor** — versioning, packaging, and release procedure.

How versions, NuGet packages, and GitHub Releases fit together.

## Table of Contents

- [Versioning (MinVer)](#versioning-minver)
- [Release notes](#release-notes)
- [Package READMEs (NuGet vs GitHub)](#package-readmes-nuget-vs-github)
- [Compatibility and package gates](#compatibility-and-package-gates)
- [Checklist](#checklist)
- [Optional: package signing](#optional-package-signing)
- [Local pack smoke test](#local-pack-smoke-test)

## Versioning (MinVer)

Package version is **not** hardcoded. [MinVer](https://github.com/adamralph/minver) reads **Git tags**.

| Situation | Resulting package version (typical) |
|-----------|-------------------------------------|
| Tag `v3.0.0` on the commit you build | `3.0.0` |
| Commits after `v3.0.0` without a new tag | `3.0.1-rc.0.N` |
| Tag `v3.0.1` | `3.0.1` |

Tag prefix is **`v`** (`MinVerTagPrefix` in `Directory.Build.props`).

```bash
dotnet build src/CacheOrchestrator/CacheOrchestrator.csproj -c Release -v:m
```

**Requires full Git history** on CI (`fetch-depth: 0` — already set in workflows).

## Release notes

| Surface | Audience | Role |
|---------|----------|------|
| [GitHub Releases](https://github.com/CacheOrchestrator/CacheOrchestrator/releases) | Humans | Full history per tag (canonical) |
| [PACKAGE_RELEASE_NOTES.md](../../PACKAGE_RELEASE_NOTES.md) | nuget.org | Short notes for **this** version only; link to the matching `releases/tag/v…` |

## Package READMEs (NuGet vs GitHub)

| Surface | File |
|---------|------|
| GitHub (full story, logo) | Root [README.md](../../README.md) |
| Guide (orientation) | [docs/guide/README.md](../guide/README.md) |
| NuGet `CacheOrchestrator` (meta) | [src/CacheOrchestrator/README.md](../../src/CacheOrchestrator/README.md) |
| NuGet `CacheOrchestrator.Core` | [src/CacheOrchestrator.Core/README.md](../../src/CacheOrchestrator.Core/README.md) |
| NuGet `CacheOrchestrator.AspNetCore` | [src/CacheOrchestrator.AspNetCore/README.md](../../src/CacheOrchestrator.AspNetCore/README.md) |
| NuGet `CacheOrchestrator.FusionCache` | [src/CacheOrchestrator.FusionCache/README.md](../../src/CacheOrchestrator.FusionCache/README.md) |
| NuGet `CacheOrchestrator.HybridCache` | [src/CacheOrchestrator.HybridCache/README.md](../../src/CacheOrchestrator.HybridCache/README.md) |
| NuGet `CacheOrchestrator.Edge` | [src/CacheOrchestrator.Edge/README.md](../../src/CacheOrchestrator.Edge/README.md) |
| NuGet `CacheOrchestrator.Edge.Cloudflare` | [src/CacheOrchestrator.Edge.Cloudflare/README.md](../../src/CacheOrchestrator.Edge.Cloudflare/README.md) |
| NuGet `CacheOrchestrator.Edge.Varnish` | [src/CacheOrchestrator.Edge.Varnish/README.md](../../src/CacheOrchestrator.Edge.Varnish/README.md) |
| NuGet `CacheOrchestrator.Redis` (meta) | [src/CacheOrchestrator.Redis/README.md](../../src/CacheOrchestrator.Redis/README.md) |
| NuGet `CacheOrchestrator.AspNetCore.Redis` | [src/CacheOrchestrator.AspNetCore.Redis/README.md](../../src/CacheOrchestrator.AspNetCore.Redis/README.md) |
| NuGet `CacheOrchestrator.FusionCache.Redis` | [src/CacheOrchestrator.FusionCache.Redis/README.md](../../src/CacheOrchestrator.FusionCache.Redis/README.md) |
| NuGet `CacheOrchestrator.Redis.Shared` (support) | [src/CacheOrchestrator.Redis.Shared/README.md](../../src/CacheOrchestrator.Redis.Shared/README.md) — transitive only; do not promote as install target |
| NuGet `CacheOrchestrator.HttpBus` | [src/CacheOrchestrator.HttpBus/README.md](../../src/CacheOrchestrator.HttpBus/README.md) |
| NuGet `CacheOrchestrator.EFCore.Invalidation` | [src/CacheOrchestrator.EFCore.Invalidation/README.md](../../src/CacheOrchestrator.EFCore.Invalidation/README.md) |

Do **not** pack the root README into library packages (HTML/logo does not render well on nuget.org). Admin Console App is **not** a NuGet package (Docker / GHCR only). `Redis.Shared` is published so leaf/meta packages restore; apps should not reference it directly.

Each packable library README keeps a short Install / Usage story for that package. The **Documentation** section is the same everywhere: root README, docs index, and repository URL (deep links belong in prose above that section or in `docs/`).

## Compatibility and package gates

Three separate gates cover different failure modes. A successful `dotnet build` does not replace a package validation run, and analyzer unit tests do not prove that the analyzer reached a NuGet consumer.

### Public API analyzer (build time)

All packable libraries are listed in `ReleasePackageNames` in [`eng/ReleaseProjects.props`](../../eng/ReleaseProjects.props), imported by [`Directory.Build.props`](../../Directory.Build.props). They use `Microsoft.CodeAnalysis.PublicApiAnalyzers` and keep their contracts under:

```text
eng/PublicApi/{Project}/PublicAPI.Shipped.txt
eng/PublicApi/{Project}/PublicAPI.Unshipped.txt
```

This gate runs during a normal build:

```bash
dotnet build CacheOrchestrator.slnx -c Release
```

Add reviewed API additions to `PublicAPI.Unshipped.txt`. Record removals and changed signatures with the analyzer-generated `*REMOVED*` entries only after making an explicit breaking-change decision. Maintainers promote the accepted release surface to `PublicAPI.Shipped.txt` when establishing a release baseline. Do not suppress the analyzer merely to make a build pass.

### NuGet package validation (pack time)

The same public-project list enables the .NET SDK package validator with:

```xml
<EnablePackageValidation>true</EnablePackageValidation>
<EnableStrictModeForCompatibleTfms>true</EnableStrictModeForCompatibleTfms>
```

It runs as part of `dotnet pack`, validates the produced package assets, and strictly checks that compatible target frameworks expose compatible APIs. For this repository that includes the `net8.0` / `net10.0` relationship. Run a Release build first, then pack with `--no-build`:

```bash
dotnet restore CacheOrchestrator.slnx
dotnet build CacheOrchestrator.slnx -c Release --no-restore
dotnet pack src/CacheOrchestrator.Core/CacheOrchestrator.Core.csproj \
  -c Release --no-build -o ./nupkg
```

The repository does not set `PackageValidationBaselineVersion`, so this gate does **not** compare packages against nuget.org by default. To compare a release against a chosen baseline version:

```bash
dotnet pack src/CacheOrchestrator.Core/CacheOrchestrator.Core.csproj \
  -c Release --no-build -o ./nupkg \
  -p:PackageValidationBaselineVersion=3.0.0
```

Use the latest applicable stable version from the same major line. Baseline validation may restore that package from configured NuGet sources and therefore requires network access when it is not already cached. Intentional major-version breaks belong in the public API baseline and release notes, not in blanket package-validation suppressions.

### Packaged analyzer consumer smoke

Analyzer unit tests verify `COIDENTITY001` logic. The shared verification action used by [`.github/workflows/build.yml`](../../.github/workflows/build.yml) additionally verifies delivery from generated packages on every pull request to `main` and every push to `main`.

Both workflows invoke [the shared verification action](../../.github/actions/verify/action.yml) and [eng/verify-release.ps1](../../eng/verify-release.ps1). It checks the package inventory against all packable source projects, builds the solution, discovers every test project and its declared target frameworks, runs unit and container integration tests, packs all fourteen libraries, and validates package assets and analyzer placement. Test runs must execute every discovered test; skips or missing coverage fail the gate.

A fresh external consumer uses source mapping and an isolated package cache so every CacheOrchestrator assembly comes from the just-built packages. Each SDK gets its own directory, SDK policy and package cache. The matrix runs net8.0 under the actual .NET 8.0.4xx SDK and runs both net8.0 and net10.0 under the source-build SDK 10 policy. Consumer code uses C# 12; `sdk.log` records the selected SDK and a major-version mismatch fails validation. Each matrix entry resolves Core, Hybrid web and Fusion web compositions, compiles the documented provider/settings examples, accepts unrelated same-name attributes, and requires duplicate real identity bindings to fail with COIDENTITY001. The analyzer is physically packed only in CacheOrchestrator.AspNetCore and flows transitively to meta consumers.

Both workflows also run the Minimal sample, check local Markdown paths/anchors and JSON examples, and build the Admin Console image before publication is possible. The image smoke runs a real Minimal origin behind an authenticated nginx proxy and checks partial/all-origin outages; browser interaction remains a local release-candidate check. Logs, TRX results and Cobertura coverage are retained for 14 days even on failure. Coverage is evidence for review, not a universal percentage target: prioritize failure, concurrency and lifecycle branches and explain uncovered external-system paths. No production Cloudflare account is needed for deterministic provider tests.

## Checklist

1. Merge all release work to `main`.
2. Update **PACKAGE_RELEASE_NOTES.md** (short NuGet blurb + link to the release tag you are about to create). Draft the **GitHub Release** body from merged PR worklogs.
3. Commit on `main`; wait for **Build and Test** to pass, including the packaged-analyzer consumer smoke.
4. Create an **annotated tag**:

   ```bash
   git tag -a v3.0.0 -m "CacheOrchestrator 3.0.0"
   git push origin v3.0.0
   ```

5. Create a **GitHub Release** for that tag (**not** marked pre-release for a stable release).  
   This triggers [`.github/workflows/publish.yml`](../../.github/workflows/publish.yml):
   - unit tests (`Core` / `FusionCache` / `HybridCache` / `AspNetCore` / `Edge` / `Edge.Cloudflare` / `Edge.Varnish` / `Redis.Shared` / `AspNetCore.Redis` / `FusionCache.Redis` / Redis meta / `HttpBus` / EF) on net8 + net10; Admin Console App on net10
   - integration tests on net8/net10 + Redis and Varnish Testcontainers; Minimal sample smoke
   - `dotnet pack` for **all** packable NuGet libraries → `.nupkg` + `.snupkg` (includes Redis.Shared as support; see `eng/ReleaseProjects.props`); each pack runs SDK package validation
   - **NuGet Trusted Publishing** (OIDC via `NuGet/login@v1`)
   - **Admin Console App Docker image** → `ghcr.io/cacheorchestrator/cacheorchestrator-admin-console` (same version tags)

6. Confirm nuget.org for **all** packages produced by `publish.yml` (including support `Redis.Shared`); optionally **unlist** old pre-release versions.  
   Confirm GHCR package **cacheorchestrator-admin-console** (see [deploy/admin/README.md](../../deploy/admin/README.md)).  
   First-time: set the package **visibility** to Public if anonymous `docker pull` is desired.

### NuGet Trusted Publishing

1. nuget.org → **Trusted Publishing**
2. Policy (GitHub repo after org transfer):
   - **Repository Owner:** `CacheOrchestrator`
   - **Repository:** `CacheOrchestrator`
   - **Workflow file:** `publish.yml`
   - **Environment:** empty
   - Package glob: `CacheOrchestrator*`
   - Scopes: at least **Push new packages and package versions**
3. `NuGet/login` in `publish.yml` uses nuget.org username `amarinsek` (package owner on nuget.org — not the GitHub org name). Optional secret **`NUGET_USER`** if you prefer not to hardcode it.
4. First successful OIDC login/publish within 7 days fully activates a new policy when the UI shows a temporary window (common for private repos).

## Optional: package signing

Not enabled in CI. Sign locally with `dotnet nuget sign` if you have a certificate.

## Local pack smoke test

Use PowerShell 7 on Windows, Linux or macOS. Docker is required for integration tests. The repository pins the .NET SDK feature band in [global.json](../../global.json), accepts servicing patches within that band and uses C# 14 explicitly. Install SDK 8.0.400 or a later 8.0.4xx servicing patch as well; the consumer SDK policy is in [global.net8.json](../../eng/PackageConsumer/global.net8.json). Update global.json and both Dockerfile SDK tags together when changing the source-build toolchain. `-Phase Consumers -ConsumerSdk 8` or `-ConsumerSdk 10` runs one SDK group locally; the default and CI run both groups.

```powershell
./eng/verify-release.ps1 -Phase All
./eng/verify-docs.ps1
docker build -f src/CacheOrchestrator.AdminConsole/Dockerfile -t cacheorchestrator-admin-console:verify .
./eng/smoke-admin.ps1
```

The default output is `.artifacts/release`; use `-Artifacts _local/release-check-2` for a fresh run. The script refuses to mix packages in a nonempty package output directory. Individual phases are `Inventory`, `Build`, `Tests`, `Pack`, and `Consumers`. Tests and packing require a matching Release build. All phases run locally without publishing anything; only the release-triggered workflow owns publication credentials.

The package gate expects one nupkg and one snupkg per inventory entry, both framework assemblies and XML documentation, the package README/icon, and exactly one analyzer placement. SDK compatibility diagnostics fail packing. The complete test/consumer logs are the evidence to review before tagging.
