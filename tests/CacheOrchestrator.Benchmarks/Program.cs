using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// Five parent directories from tests/Project/bin/Release/tfm reach the repository root.
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

var artifacts = Path.Combine(repoRoot, "_local", "BenchmarkDotNet.Artifacts");
Directory.CreateDirectory(artifacts);

Console.WriteLine($"Artifacts path: {artifacts}");

ManualConfig config = DefaultConfig.Instance.WithArtifactsPath(artifacts);

BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args, config);
