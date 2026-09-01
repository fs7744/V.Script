using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Local functions. A local function becomes a local holding a delegate, so every call is a
/// delegate invoke and a recursive one also reads its own delegate back out of the closure. The
/// C# baseline is a real method the JIT can inline, which is the gap being measured.
/// </summary>
[MemoryDiagnoser]
public class LocalFunctionBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<CreationGlobals, int> _recursive = null!;
    private Script<CreationGlobals, int> _helper = null!;
    private Script<CreationGlobals, int> _lambda = null!;

    private readonly CreationGlobals _globals = new() { Id = 20, Name = "widget" };

    private const string RecursiveSource = """
        int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
        return Fib(20);
        """;

    private const string HelperSource = """
        int Scale(int n) => n * 3 + 1;
        var total = 0;
        for (var i = 0; i < 100; i++) total = total + Scale(i);
        return total;
        """;

    private const string LambdaSource = """
        var scale = (int n) => n * 3 + 1;
        var total = 0;
        for (var i = 0; i < 100; i++) total = total + scale(i);
        return total;
        """;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(CreationGlobals))
            .AddImports("V.Script.Benchmarks"));

        _recursive = _engine.Compile<CreationGlobals, int>(RecursiveSource);
        _helper = _engine.Compile<CreationGlobals, int>(HelperSource);
        _lambda = _engine.Compile<CreationGlobals, int>(LambdaSource);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _recursive.Dispose();
        _helper.Dispose();
        _lambda.Dispose();
        _engine.Dispose();
    }

    private static int NativeFib(int n) => n < 2 ? n : NativeFib(n - 1) + NativeFib(n - 2);

    [Benchmark(Baseline = true, Description = "recursive Fib(20) / hand-written C#")]
    public int NativeRecursive() => NativeFib(20);

    [Benchmark(Description = "recursive Fib(20) / script local function")]
    public int ScriptRecursive() => _recursive.Run(_globals);

    [Benchmark(Description = "100 helper calls / hand-written C#")]
    public int NativeHelper()
    {
        static int Scale(int n) => n * 3 + 1;

        var total = 0;
        for (var i = 0; i < 100; i++) total += Scale(i);
        return total;
    }

    [Benchmark(Description = "100 helper calls / script local function")]
    public int ScriptHelper() => _helper.Run(_globals);

    /// <summary>The same helper as a lambda, to show that the two lower to the same thing.</summary>
    [Benchmark(Description = "100 helper calls / script lambda")]
    public int ScriptLambda() => _lambda.Run(_globals);
}
