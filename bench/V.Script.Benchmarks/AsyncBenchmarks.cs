using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Asynchronous execution. Every awaited task here is already completed, so what is measured is
/// the runtime-async fast path rather than scheduling.
/// </summary>
[MemoryDiagnoser]
public class AsyncBenchmarks
{
    private ScriptEngine _engine = null!;
    private AsyncScript<AsyncContext, int> _single = null!;
    private AsyncScript<AsyncContext, int> _loop = null!;
    private AsyncScript<AsyncContext, int> _loopWithLimits = null!;
    private ScriptEngine _limitedEngine = null!;
    private AsyncContext _context = null!;

    private const string SingleSource = "await Service.GetAsync(Seed)";

    private const string LoopSource = """
        var total = 0;
        for (var i = 0; i < 10; i++)
            total += await Service.GetAsync(i);
        return total;
        """;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default.WithLimits(ScriptLimits.Unlimited));
        _single = _engine.CompileAsync<AsyncContext, int>(SingleSource);
        _loop = _engine.CompileAsync<AsyncContext, int>(LoopSource);

        _limitedEngine = new ScriptEngine(ScriptOptions.Default
            .WithLimits(new ScriptLimits { MaxSteps = long.MaxValue, Timeout = TimeSpan.FromHours(1) }));
        _loopWithLimits = _limitedEngine.CompileAsync<AsyncContext, int>(LoopSource);

        _context = new AsyncContext { Seed = 21 };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        _limitedEngine.Dispose();
    }

    [Benchmark(Baseline = true, Description = "single await / hand-written C#")]
    public async Task<int> SingleNative() => await _context.Service.GetAsync(_context.Seed);

    [Benchmark(Description = "single await / script")]
    public async Task<int> SingleScript() => await _single.RunAsync(_context);

    [Benchmark(Description = "10 awaits in a loop / hand-written C#")]
    public async Task<int> LoopNative()
    {
        var total = 0;
        for (var i = 0; i < 10; i++)
            total += await _context.Service.GetAsync(i);
        return total;
    }

    [Benchmark(Description = "10 awaits in a loop / script")]
    public async Task<int> LoopScript() => await _loop.RunAsync(_context);

    /// <summary>
    /// With limits on, every await is routed through <c>Task.WaitAsync</c> so a suspended
    /// script can still be interrupted. This measures what that costs.
    /// </summary>
    [Benchmark(Description = "10 awaits in a loop / script, limits on")]
    public async Task<int> LoopScriptWithLimits() => await _loopWithLimits.RunAsync(_context);
}
