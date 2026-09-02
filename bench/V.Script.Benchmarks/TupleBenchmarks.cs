using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Tuples. A tuple is a <c>ValueTuple</c> and nothing else, and deconstruction is field reads,
/// so what matters is that neither allocates and that neither costs much above the fixed price
/// of entering a script at all.
/// </summary>
/// <remarks>
/// There is deliberately no hand-written C# baseline here. These workloads are pure functions of
/// a field, so on the C# side the JIT hoists the whole thing out of the measurement loop and the
/// row reads as free; the script side cannot be hoisted because it is behind a delegate. A ratio
/// between the two would be measuring that, not the lowering. The baseline is instead a script
/// that does nothing, so each row's excess over it is the tuple work itself.
/// </remarks>
[MemoryDiagnoser]
public class TupleBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<CreationGlobals, int> _empty = null!;
    private Script<CreationGlobals, (int, int)> _build = null!;
    private Script<CreationGlobals, (int, int)> _named = null!;
    private Script<CreationGlobals, (int, int)> _deconstruct = null!;

    private readonly CreationGlobals _globals = new() { Id = 4711, Name = "widget" };

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(CreationGlobals))
            .AddImports("V.Script.Benchmarks"));

        _empty = _engine.Compile<CreationGlobals, int>("return Id;");
        _build = _engine.Compile<CreationGlobals, (int, int)>("return (Id, Id + 1);");
        _named = _engine.Compile<CreationGlobals, (int, int)>(
            "var t = (lo: Id, hi: Id + 1); return (t.lo, t.hi);");
        _deconstruct = _engine.Compile<CreationGlobals, (int, int)>(
            "(int, int) Split(int n) => (n / 2, n % 2); var (q, r) = Split(Id); return (r, q);");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _empty.Dispose();
        _build.Dispose();
        _named.Dispose();
        _deconstruct.Dispose();
        _engine.Dispose();
    }

    /// <summary>Just entering the script and reading one global: the floor for every row below.</summary>
    [Benchmark(Baseline = true, Description = "script floor: return Id")]
    public int ScriptFloor() => _empty.Run(_globals);

    [Benchmark(Description = "build a tuple / script")]
    public (int, int) ScriptBuild() => _build.Run(_globals);

    /// <summary>Names are compile-time only, so this should match the positional row.</summary>
    [Benchmark(Description = "build and read by name / script")]
    public (int, int) ScriptNamed() => _named.Run(_globals);

    [Benchmark(Description = "local function returns a tuple, then deconstruct / script")]
    public (int, int) ScriptDeconstruct() => _deconstruct.Run(_globals);
}
