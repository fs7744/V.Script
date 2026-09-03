using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Compilation cost per carrier. Each benchmark compiles a script and then retires it, which is
/// the real lifecycle and also keeps generated assemblies from accumulating across iterations.
/// The gap between the carriers is the price of runtime-async: a <c>DynamicMethod</c> cannot be
/// marked <c>Async</c>, so every asynchronous script needs its own collectible assembly.
/// </summary>
[MemoryDiagnoser]
public class CompilationBenchmarks
{
    private ScriptEngine _engine = null!;
    private ScriptEngine _batched = null!;
    private int _counter;

    /// <summary>How many scripts share a generated assembly in the batched engine.</summary>
    private const int BatchSize = 16;

    private const string SmallSource = "Price * Quantity";

    private const string MediumSource = """
        var subtotal = Price * Quantity;
        var discounted = subtotal * (1 - Discount);
        if (IsVip) discounted = discounted * 0.95m;
        var taxed = discounted * (1 + TaxRate);
        if (Quantity >= Threshold) taxed = taxed - 1m;
        return taxed;
        """;

    private const string AsyncLoopSource = """
        var total = 0;
        for (var i = 0; i < 3; i++)
            total += await Service.GetAsync(Seed + i);
        return total;
        """;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default);
        _batched = new ScriptEngine(ScriptOptions.Default with { ScriptsPerGeneratedAssembly = BatchSize });

        // Warm the reflection caches so the first measured iteration is not an outlier.
        _engine.Compile<PricingContext, decimal>(Unique(SmallSource)).Dispose();
        _engine.CompileAsync<AsyncContext, int>(Unique("Seed")).Dispose();
        _batched.CompileAsync<AsyncContext, int>(Unique("Seed")).Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        _batched.Dispose();
    }

    /// <summary>Appending a unique comment keeps every iteration a genuine cache miss.</summary>
    private string Unique(string source) => $"{source} // {Interlocked.Increment(ref _counter)}";

    [Benchmark(Baseline = true, Description = "sync, small (DynamicMethod)")]
    public void CompileSmallSync() =>
        _engine.Compile<PricingContext, decimal>(Unique(SmallSource)).Dispose();

    [Benchmark(Description = "sync, medium (DynamicMethod)")]
    public void CompileMediumSync() =>
        _engine.Compile<PricingContext, decimal>(Unique(MediumSource)).Dispose();

    [Benchmark(Description = "async, small (collectible assembly)")]
    public void CompileSmallAsync() =>
        _engine.CompileAsync<AsyncContext, int>(Unique("Seed")).Dispose();

    [Benchmark(Description = "async, loop with await (collectible assembly)")]
    public void CompileAsyncLoop() =>
        _engine.CompileAsync<AsyncContext, int>(Unique(AsyncLoopSource)).Dispose();

    /// <summary>
    /// The same small asynchronous script, but with sixteen of them sharing one generated
    /// assembly. Creating the assembly is nearly the whole cost, and it is charged per assembly,
    /// so this is what the per-script price becomes once it is amortised.
    /// </summary>
    /// <remarks>
    /// Retiring the script immediately, as the other rows do, would defeat the point: the
    /// generation would be full and gone before the next iteration reuses it. These are kept
    /// until a whole batch has been compiled, which is the usage the setting exists for.
    /// </remarks>
    [Benchmark(Description = "async, small (16 scripts per assembly)")]
    public void CompileSmallAsyncBatched()
    {
        _batch.Add(_batched.CompileAsync<AsyncContext, int>(Unique("Seed")));

        if (_batch.Count < BatchSize) return;

        foreach (var script in _batch) script.Dispose();
        _batch.Clear();
    }

    private readonly List<IDisposable> _batch = [];
}

/// <summary>Separated out so the cache hit is not measured against a churning engine.</summary>
[MemoryDiagnoser]
public class CacheBenchmarks
{
    private ScriptEngine _engine = null!;

    private const string Source = "Price * Quantity * (1 + TaxRate)";

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default);
        _engine.Compile<PricingContext, decimal>(Source);
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    [Benchmark(Description = "cache hit for an already-compiled source")]
    public object CacheHit() => _engine.Compile<PricingContext, decimal>(Source);
}
