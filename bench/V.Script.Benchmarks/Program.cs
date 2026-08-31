using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using V.Script.Benchmarks;

// BenchmarkDotNet 0.15.x does not yet know the net11.0 moniker, so its default out-of-process
// toolchain cannot build or launch the runner project. Running in-process sidesteps both the
// SDK lookup and the moniker validation. Revisit once BenchmarkDotNet ships net11.0 support.
var job = Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance);

if (args.Any(a => a.StartsWith("--long", StringComparison.Ordinal)))
{
    job = Job.Default.WithToolchain(InProcessEmitToolchain.Instance);
    args = args.Where(a => !a.StartsWith("--long", StringComparison.Ordinal)).ToArray();
}

var config = DefaultConfig.Instance.AddJob(job);

BenchmarkSwitcher
    .FromTypes([
        typeof(ExecutionBenchmarks),
        typeof(CompilationBenchmarks),
        typeof(CacheBenchmarks),
        typeof(AsyncBenchmarks),
        typeof(LambdaBenchmarks),
    ])
    .Run(args, config);
