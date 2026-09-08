# Contributing to CacheOrchestrator

Thanks for helping improve **CacheOrchestrator**. Code is only one way to contribute: testing v3 in a real application, reporting surprising behavior, improving an example, or pointing out unclear documentation is just as valuable.

You do not need to understand the whole project before getting involved. Start with the path that matches what you want to contribute.

## Ways to contribute

- **Test v3** in an application or one of the playground labs and share what worked, what failed, or what was difficult to understand.
- **Report a bug** with a small reproduction, configuration, logs, or relevant `X-CacheOrchestrator` headers.
- **Improve documentation** when an explanation, example, or link could be clearer.
- **Propose a feature** by describing the use case before designing the API.
- **Submit a code change** for a focused issue or improvement.

Not sure whether something is a bug? Open an issue anyway. Unexpected behavior and confusing configuration are useful feedback even when the implementation is technically working as designed.

## Help test v3

CacheOrchestrator v3 is in prerelease, and feedback from real applications is especially useful. You can make a valuable contribution without writing library code.

We are particularly interested in:

- ASP.NET Core applications using Output Cache with FusionCache;
- standalone workers using Core with FusionCache;
- ASP.NET Core applications using HybridCache;
- Redis-backed and multi-instance deployments;
- Client Cache behavior in real browsers, CDNs, and reverse proxies;
- automatic EF Core invalidation after `SaveChanges`;
- configuration, diagnostics, package composition, and documentation that feel unclear.

### Try it in your application

Install the appropriate prerelease package:

```bash
dotnet add package CacheOrchestrator --prerelease
```

The `CacheOrchestrator` meta package is the typical `CacheOrchestrator.AspNetCore` + `CacheOrchestrator.FusionCache` path. Other supported compositions are listed in the [package guide](docs/guide/packages.md) and [composition how-to](docs/how-to/composition.md).

Please do not test a new cache setup against production traffic first. Start in a local, development, or staging environment.

### Try a guided playground lab

The numbered Docker Compose labs progress from a single in-memory instance to multi-instance Redis and HTTP bus topologies:

```bash
docker compose -f samples/CacheOrchestrator.Sample/labs/compose/01-observability.yml up --build
```

Continue with the [playground lab guide](samples/CacheOrchestrator.Sample/labs/README.md) when you want to test Redis, multiple instances, invalidation propagation, or separate Output Cache and Data Cache stores.

### Share the result

Use the [**v3 testing feedback** issue template](https://github.com/CacheOrchestrator/CacheOrchestrator/issues/new?template=v3_testing_feedback.md). A useful report includes as much of the following as is practical:

- CacheOrchestrator package names and versions;
- .NET version, operating system, and hosting model;
- Data Cache and Output Cache backends;
- a minimal configuration with secrets removed;
- what you tried, what you expected, and what happened;
- relevant logs, response headers such as `X-CacheOrchestrator`, or a small reproduction.

Successful reports are welcome too. Knowing which compositions work in real environments helps establish confidence before the stable release.

## Report bugs and request features

- For a bug, use the [**bug report** template](https://github.com/CacheOrchestrator/CacheOrchestrator/issues/new?template=bug_report.md) and include a minimal reproduction when possible.
- For v3 evaluation results, including usability or documentation feedback, use the [**v3 testing feedback** template](https://github.com/CacheOrchestrator/CacheOrchestrator/issues/new?template=v3_testing_feedback.md).
- For a feature, lead with the problem and use case. An API proposal is helpful but not required.
- For security vulnerabilities, do **not** open a public issue. Follow [SECURITY.md](SECURITY.md).

Before starting a large implementation, open an issue so the scope and package ownership can be agreed without wasting your time.

## Development setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download), which can build the library targets `net8.0` and `net10.0`.
- .NET SDK 8.0.400 or a later 8.0.4xx servicing patch for the native SDK 8 package-consumer gate.
- Docker when running Redis integration tests or the playground topology labs.

The Admin Console App, its tests, and the samples target `net10.0`. Packaged libraries and their tests target both `net8.0` and `net10.0`.

### Clone and build

```bash
git clone https://github.com/CacheOrchestrator/CacheOrchestrator.git
cd CacheOrchestrator

dotnet restore CacheOrchestrator.slnx
dotnet build CacheOrchestrator.slnx -c Release
```

### Run relevant tests

Run the tests for the package you changed. For example:

```bash
dotnet test tests/CacheOrchestrator.Core.UnitTests/CacheOrchestrator.Core.UnitTests.csproj -c Release
dotnet test tests/CacheOrchestrator.AspNetCore.UnitTests/CacheOrchestrator.AspNetCore.UnitTests.csproj -c Release
dotnet test tests/CacheOrchestrator.FusionCache.UnitTests/CacheOrchestrator.FusionCache.UnitTests.csproj -c Release
dotnet test tests/CacheOrchestrator.HybridCache.UnitTests/CacheOrchestrator.HybridCache.UnitTests.csproj -c Release
```

Use `-f net8.0` or `-f net10.0` to run one target framework. The Admin Console App tests are `net10.0` only:

```bash
dotnet test tests/CacheOrchestrator.AdminConsole.UnitTests/CacheOrchestrator.AdminConsole.UnitTests.csproj -c Release -f net10.0
```

Integration tests exercise both in-memory and Redis behavior. Docker must be running because Redis coverage uses Testcontainers and is not silently skipped:

```bash
dotnet test tests/CacheOrchestrator.IntegrationTests/CacheOrchestrator.IntegrationTests.csproj -c Release
```

The complete project-to-test mapping and CI commands are visible in [`.github/workflows/build.yml`](.github/workflows/build.yml). Optional micro-benchmarks are documented in [docs/contributor/benchmarks/results.md](docs/contributor/benchmarks/results.md).

## Find your way around

| Path | Role |
|------|------|
| `src/CacheOrchestrator.Core` | HTTP-free domain orchestration, invalidation, management, and shared contracts |
| `src/CacheOrchestrator.AspNetCore` | Output Cache, Client Cache, HTTP Data Cache helpers, and Admin API |
| `src/CacheOrchestrator.FusionCache` | FusionCache Data Cache provider |
| `src/CacheOrchestrator.HybridCache` | HybridCache Data Cache provider |
| `src/CacheOrchestrator` | Meta package for the typical ASP.NET Core + FusionCache composition |
| `src/CacheOrchestrator.Redis*` | Redis meta package and focused Output Cache / FusionCache integrations |
| `src/CacheOrchestrator.HttpBus` | HTTP cluster bus transport |
| `src/CacheOrchestrator.EFCore.Invalidation` | EF Core `SaveChanges` invalidation integration |
| `src/CacheOrchestrator.AdminConsole` | Admin Console App; not a NuGet package |
| `tests` | Unit, integration, analyzer, and benchmark projects |
| `samples` | Minimal example, interactive playground, and topology labs |
| `docs` | Guide, how-to, reference, and contributor documentation |

For deeper context, see the [contributor architecture](docs/contributor/architecture.md). You do not need to read it for a documentation fix or a focused bug report.

## Coding conventions

Follow [`.editorconfig`](.editorconfig). The repository conventions that are easiest to miss are:

- use English for code comments and XML documentation;
- preserve public configuration keys unless the change has an explicit breaking-change plan;
- keep interfaces beside their implementations rather than creating an Abstractions assembly;
- use `ConfigureAwait(false)` in library code;
- keep default implementations internal when applications can depend on an interface and DI;
- prefer small, focused public APIs.

Before opening a code PR, run:

```bash
dotnet format CacheOrchestrator.slnx
dotnet build CacheOrchestrator.slnx -c Release
```

Packable libraries declare their API contract under `eng/PublicApi/{Project}`. Add reviewed new APIs to `PublicAPI.Unshipped.txt`; removals and signature changes require an explicit breaking-change decision. Maintainers move the release baseline to `PublicAPI.Shipped.txt`.

## Documentation changes

Put information in the narrowest appropriate documentation tier:

1. root [README.md](README.md) for the product overview and quick start;
2. [docs/guide](docs/guide/README.md) for learning and concepts;
3. [docs/how-to](docs/how-to/composition.md) for task-oriented package composition;
4. [docs/reference](docs/reference/) for precise technical behavior;
5. [docs/contributor](docs/contributor/) for architecture and maintainer procedures.

Update examples, samples, and package READMEs when a public API or configuration surface changes. Record the user-facing outcome in the PR description or worklog so the maintainer can assemble the GitHub Release notes (and `PACKAGE_RELEASE_NOTES.md` for NuGet).

## Pull requests

1. Fork the repository, or branch from `main` if you have write access.
2. Keep the change focused and explain the problem before the implementation details.
3. Add or update tests in proportion to the change.
4. Update the relevant documentation or sample when public behavior changes.
5. Run the relevant tests and state exactly what you verified.
6. Avoid committing secrets, production endpoints, generated artifacts, or personal worklogs.

Small external contributions only need a clear PR description covering context, the resulting change, and verification. Maintainers and larger contributions should use the [worklog template](docs/contributor/templates/worklog-template.md); keep the working copy under `_local/` and paste its contents into the PR rather than committing the file.

The detailed release and NuGet publishing procedure belongs in [docs/contributor/releasing.md](docs/contributor/releasing.md), not in individual PRs.

## Community expectations

Be respectful and assume good intent. Questions are welcome, and asking for guidance is better than silently getting stuck. Harassment, spam, and personal attacks are not accepted.

## License

By contributing, you agree that your contributions are licensed under the same [MIT License](LICENSE.md) as the project.
