# Benchmark results

> **Contributor** — recorded benchmark results.

How to run the micro-benchmarks in `tests/CacheOrchestrator.Benchmarks`, and what they measure.

Absolute times depend on CPU, runtime, and load. Compare **ratios**, **Allocated**, and before/after on the **same** machine.

## How to run

```bash
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks
```

Filter examples:

```bash
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *DomainKey*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *CacheOrchestratorHeader*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *ClientCache*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *HttpHelper*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *Policy*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *ProviderHit*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *MetricsRecording*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *DomainDataCache*
dotnet run -c Release --project tests/CacheOrchestrator.Benchmarks -- --filter *DynamicDomain*
```

All classes use a shared short job (`[ShortJob]`: net10.0, warmup 1 / iteration 3 / launch 1) for consistent local runs. Artifacts default under `_local/BenchmarkDotNet.Artifacts` (not committed).

For a release decision, run the affected filter with a longer BenchmarkDotNet job on an otherwise idle machine and compare the same commit range and runtime. CI deliberately runs deterministic allocation tripwires from the unit-test suites; wall-clock benchmark results are machine-specific and remain a maintainer release check.

The release checks also include `HttpOutputCacheBenchmarks` (actual TestServer HTTP hit/miss with a factory-count assertion), `DomainDataCacheHitBenchmarks.EntityFootprint_InvalidateAndRefresh` (purge plus complete rematerialization), and `InvalidationFanOutBenchmarks` (native Fusion invalidation across one/eight named instances). HTTP timings include the test transport; the refresh case includes invalidation and cannot be interpreted as factory-only overhead. The policy-only fixtures reuse prepared request contexts and exclude HTTP transport/setup.

Check every selected case produced a result: BenchmarkDotNet can report a failed case as `NA` even when the process exits successfully. Keep failed logs and do not include those rows in a performance claim. Allocation changes on ordinary hits and deliberate extra marker lookups on Hybrid footprint hits should be reported separately.

## Hot paths covered by benchmarks

| Benchmark | What it measures |
|-----------|------------------|
| `DomainKeyGeneratorBenchmarks` | Data Cache key materialization (path, query, tracking, encoding, host, **resource id**, **route endpoint**) |
| `HttpHelperBenchmarks` | Tracking query detection, `Cache-Control: no-store` scan, Accept-Encoding normalization |
| `ClientCacheHeaderGeneratorBenchmarks` | Client Cache Schedule `Cache-Control` (Calm / Approaching / **Hold** / **must-revalidate** / NoStore / Private) |
| `CacheOrchestratorHeaderFormatterBenchmarks` | Diagnostic `X-CacheOrchestrator` formatting (Hit / Miss / **Stale** / **Bypass** / **Blocked** / Hold phase) |
| `NormalizeDomainBenchmarks` | `DomainName.Normalize` and **`NormalizeResourceId`** / **`NormalizeEntityKind`** |
| `CacheETagFactoryBenchmarks` | Weak ETag from version and version+resource |
| `FusionEntryOptionsBenchmarks` | `GetFusionEntryOptions` build/reuse per domain snapshot |
| `DomainCacheOptionsProviderBenchmarks` | Core domain snapshot process-cache hit paths |
| `DomainTemplateCompilerBenchmarks` | Template `GetOrAdd` + per-request resolve |
| `DomainOutputCachePolicyBenchmarks` | `CacheRequestAsync` + **`CollectQueryKeysForOutputCache`** (real Output Cache query-key path) |
| `DataCacheProviderHitBenchmarks` | End-to-end Fusion and Hybrid L1 hits; includes a control that reproduces the former per-call Fusion configuration binding |
| `MetricsRecordingBenchmarks` | Data Cache metric recording with no listener and with an active listener |
| `DomainDataCacheHitBenchmarks` | Public `IDomainDataCache` domain and entity-footprint hits plus a real FusionCache stale fallback |
| `DynamicDomainResolutionBenchmarks` | Configured and rejected dynamic-domain resolution, including the configured-domain guard |

## Performance engineering notes (library)

These are intentional hot-path choices in the library (not full BDN numbers):

| Area | Approach |
|------|----------|
| Fusion entry options | Effective package settings and prepared entry options are cached by snapshot/override identity; configuration reload invalidates the cache |
| Hybrid entry options | Entry options and the encoded namespace prefix are prepared once per domain snapshot; a replacement snapshot refreshes both |
| Vary material | Lists, sets, and dictionaries are allocated lazily; small header sets use a bounded linear duplicate check before promoting to a `HashSet` |
| Data Cache timing | `Stopwatch.GetTimestamp` avoids allocating a `Stopwatch` object per operation |
| Metrics | Instrument-enabled guards avoid tag and route-label work when no listener is subscribed |
| Tracking query params | `HttpHelper.IsTrackingParameter` uses a fixed prefix array + manual loop (no LINQ/`HashSet` enumerator) |
| `Cache-Control: no-store` | `HttpHelper.ContainsCacheDirective` scans `StringValues` without `ToString()` |
| Domain templates | Parse plans cached per template; resolvers without custom providers are shared; **custom providers are never stored under the template-only key** (avoids provider poisoning) |
| `X-CacheOrchestrator` header | `string.Create` single allocation |
| Output Cache query vary | Non-tracking keys collected without LINQ `Where` |

## Working with results (who publishes what)

**This file is not a dump for every developer’s laptop runs.** Pasting many undated, different-machine tables here becomes noise and false comparisons.

| Audience | What to do |
|----------|------------|
| **Contributors** | Run BDN locally when touching a hot path. Put **before/after** (same machine, Release, filter) in the **PR description** if the change is performance-related. Do **not** commit raw BDN output or `_local/BenchmarkDotNet.Artifacts`. |
| **Maintainers / designated owners** | Optionally add a **curated reference snapshot** below (release, major optimisations, or documenting a known baseline). Always include date, CPU, runtime, OS, and commit/filter. Replace or archive old snapshots; do not accumulate parallel “everyone’s PC” sections. |

Reviewers care about **direction** (faster / fewer allocations on one machine) and **correctness**, not matching absolute ns across developers.

### Template (curated reference only)

```
### YYYY-MM-DD — reference (optional)

- Owner:
- CPU:
- Runtime: .NET x.y
- OS:
- Commit / filter:

| Method | Mean | Allocated |
|--------|------|-----------|
| ...    | ...  | ...       |
```

## Reference snapshots

*(Curated by maintainers only. Leave empty until a deliberate reference run is recorded.)*
