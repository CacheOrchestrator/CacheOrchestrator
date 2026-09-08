# Package consumer checks

This is a small application template for checking the NuGet packages produced by the repository. It references the packages in [ReleaseProjects.props](../ReleaseProjects.props) through `PackageReference`, so it exercises packaged assemblies and the bundled analyzer as an application would.

[verify-release.ps1](../verify-release.ps1) copies the template into a fresh directory for each SDK, selects the SDK with `global.json`, creates an isolated NuGet cache, and maps `CacheOrchestrator*` exclusively to the local package output. It also extracts the provider and settings implementations from the [extension documentation](../../docs/reference/extensibility.md) and compiles them with the consumer.

The checks cover:

- Core, Hybrid web and Fusion web service registrations, including the expected settings catalog and invalidator resolution.
- Compilation of the documented custom provider and settings contracts.
- Acceptance of unrelated attributes named `CacheIdentityAttribute`.
- Rejection of duplicate real identity bindings with `COIDENTITY001`, using a generated negative example.

All release packages are referenced together; the composition checks exercise their service registrations within that reference set.

## Run

Use PowerShell 7 from the repository root. Build and pack first, then run the consumers against that package output:

```powershell
./eng/verify-release.ps1 -Phase Build
./eng/verify-release.ps1 -Phase Pack
./eng/verify-release.ps1 -Phase Consumers
```

`Pack` requires an empty package output directory. For a new run, use `-Artifacts _local/release-check-2` consistently across the commands. `-Phase All` also runs the unit and integration tests; see the [engineering overview](../README.md).

The default consumer matrix uses SDK 8.0.4xx for `net8.0`, and the repository's SDK 10 policy for both `net8.0` and `net10.0`. Install the matching SDKs and .NET/ASP.NET Core runtimes. Add `-ConsumerSdk 8` or `-ConsumerSdk 10` to the Consumers command to run one group.

Results appear under `<Artifacts>/consumer-<sdk>-<id>/`: `sdk.log`, `restore.log`, `build.log`, `run-<framework>.log` and `analyzer-<framework>.log`. The analyzer logs intentionally contain `error COIDENTITY001`; the script succeeds only when the negative example fails as expected.

## Template files

| File | Role |
|---|---|
| [Consumer.csproj](Consumer.csproj) | C# 12 executable with warnings treated as errors. The script supplies `SmokePackageVersion` and `SmokeNet8Only`. |
| [Program.cs](Program.cs) | Runtime composition checks and the unrelated-attribute analyzer example. |
| [Directory.Build.props](Directory.Build.props), [Directory.Build.targets](Directory.Build.targets), [.editorconfig](.editorconfig) | Isolate the consumer from repository build and contributor-style settings. |
| [global.net8.json](global.net8.json) | SDK 8 selection policy. SDK 10 uses the repository's [global.json](../../global.json). |
