# Engineering validation

This directory contains the release checks shared by local development and [CI](../.github/actions/verify/action.yml). They verify the source, public contracts and installable packages. Publication is handled by the [release workflow](../docs/contributor/releasing.md).

| File or directory | Purpose |
|---|---|
| [verify-release.ps1](verify-release.ps1) | Check inventories, build, run unit and integration tests, pack libraries, validate package assets and test NuGet consumers. |
| [verify-docs.ps1](verify-docs.ps1) | Check tracked Markdown files for local links, heading anchors and valid JSON/JSONC examples. External URLs are not fetched. Stage new documents before running it. |
| [smoke-admin.ps1](smoke-admin.ps1) | Exercise the Admin Console Docker image behind an authenticated proxy with healthy and unavailable origins; retain configuration and container logs. |
| [ReleaseProjects.props](ReleaseProjects.props) | Shared inventory of publishable libraries, used by the build, package checks and consumer project. |
| [PublicApi](PublicApi/) | Per-package shipped and unshipped API contracts enforced during builds. See [API baseline maintenance](../docs/contributor/releasing.md#public-api-analyzer-build-time). |
| [PackageConsumer](PackageConsumer/README.md) | Application template for testing freshly packed libraries and their analyzer from a consumer's perspective. |
| [coverage.runsettings](coverage.runsettings) | Cobertura collection settings for the test matrix. |

## Run locally

Run the commands from the repository root in PowerShell 7. Source builds use the SDK selected by [global.json](../global.json). Consumer checks also require SDK 8.0.400 or a later 8.0.4xx servicing patch, plus the .NET/ASP.NET Core runtimes for the target frameworks. Integration and Admin smoke tests require Docker with Linux containers. Package restore and image pulls need access to their feeds and registries.

```powershell
./eng/verify-release.ps1 -Phase All
./eng/verify-docs.ps1
```

`verify-release.ps1` always checks the package inventory and API baseline files, then executes the selected phase:

| Phase | Work and prerequisites |
|---|---|
| `Inventory` | Check package/API inventories and discover test projects. |
| `Build` | Build the solution in Release configuration. |
| `Tests` | Run every declared test framework with TRX and coverage; requires a matching Release build. Empty, skipped or failed runs and missing coverage fail validation. |
| `Pack` | Pack every library and check package assets, versions and analyzer placement; requires a matching Release build and an empty package output directory. |
| `Consumers` | Validate existing packages, then restore, build and run isolated consumers under SDK 8 and SDK 10. |
| `All` (default) | Run Build, Tests, Pack and Consumers in order. |

Output defaults to `.artifacts/release`. Use `-Artifacts _local/release-check-2` for a fresh run; pass the same directory to subsequent phases that consume its packages. See [PackageConsumer](PackageConsumer/README.md) for SDK selection and consumer logs.

Documentation, the [Minimal sample smoke test](../samples/CacheOrchestrator.Minimal/smoke.sh) and Admin Console smoke are separate checks. After the Release build, prepare and test the Admin image with:

```powershell
docker build -f src/CacheOrchestrator.AdminConsole/Dockerfile -t cacheorchestrator-admin-console:verify .
./eng/smoke-admin.ps1
```

Admin smoke uses port 5391 by default (`-Port` overrides it), stores evidence under `.artifacts/release/admin` (`-Artifacts` overrides it), and removes its containers and network on completion. `-KeepRunning` retains a successful fixture for browser inspection and records the resources to clean up in `running.json`.

The [release procedure](../docs/contributor/releasing.md#local-pack-smoke-test) describes the full validation and publication process.
