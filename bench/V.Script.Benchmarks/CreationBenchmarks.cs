using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Interpolated strings. Without a format specifier the binder lowers to <c>string.Concat</c>,
/// which is what the C# compiler does too; with one it lowers to <c>string.Format</c>, where the
/// C# compiler instead uses <c>DefaultInterpolatedStringHandler</c>. The formatted pair is the
/// one to watch — it is the only place the two lowerings genuinely differ.
/// </summary>
[MemoryDiagnoser]
public class InterpolationBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<CreationGlobals, string> _concat = null!;
    private Script<CreationGlobals, string> _formatted = null!;

    private readonly CreationGlobals _globals = new() { Id = 4711, Amount = 1234.5, Name = "widget" };

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(CreationGlobals))
            .AddImports("V.Script.Benchmarks"));

        _concat = _engine.Compile<CreationGlobals, string>("return $\"{Name}#{Id}\";");
        _formatted = _engine.Compile<CreationGlobals, string>("return $\"{Name,-10}#{Amount:F2}\";");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _concat.Dispose();
        _formatted.Dispose();
        _engine.Dispose();
    }

    [Benchmark(Baseline = true, Description = "$\"{Name}#{Id}\" / hand-written C#")]
    public string NativeConcat() => $"{_globals.Name}#{_globals.Id}";

    [Benchmark(Description = "$\"{Name}#{Id}\" / script")]
    public string ScriptConcat() => _concat.Run(_globals);

    /// <summary>What the binder lowers the first script to, written out by hand.</summary>
    [Benchmark(Description = "string.Concat(object, object) / hand-written C#")]
    public string NativeConcatLowering() => string.Concat(_globals.Name, (object)_globals.Id);

    [Benchmark(Description = "$\"{Name,-10}#{Amount:F2}\" / hand-written C#")]
    public string NativeFormatted() => $"{_globals.Name,-10}#{_globals.Amount:F2}";

    [Benchmark(Description = "$\"{Name,-10}#{Amount:F2}\" / script")]
    public string ScriptFormatted() => _formatted.Run(_globals);
}

/// <summary>
/// Object and collection initializers and array creation. Every method returns the object it
/// built: if the result were discarded the JIT would prove the allocation non-escaping and
/// remove it on the C# side but not on the script side, and the comparison would be measuring
/// escape analysis rather than the lowering.
/// </summary>
[MemoryDiagnoser]
public class InitializerBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<CreationGlobals, Ticket> _objectInitializer = null!;
    private Script<CreationGlobals, List<int>> _collectionInitializer = null!;
    private Script<CreationGlobals, int[]> _arrayCreation = null!;
    private Script<CreationGlobals, int[]> _arrayExpression = null!;
    private Script<CreationGlobals, List<int>> _listExpression = null!;

    private readonly CreationGlobals _globals = new() { Id = 4711, Amount = 1234.5, Name = "widget" };

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(CreationGlobals))
            .AddImports("V.Script.Benchmarks"));

        _objectInitializer = _engine.Compile<CreationGlobals, Ticket>(
            "return new Ticket { Id = Id, Name = Name };");
        _collectionInitializer = _engine.Compile<CreationGlobals, List<int>>(
            "return new List<int> { 1, 2, 3, 4 };");
        _arrayCreation = _engine.Compile<CreationGlobals, int[]>(
            "return new[] { Id, Id + 1, Id + 2 };");
        _arrayExpression = _engine.Compile<CreationGlobals, int[]>(
            "return [Id, Id + 1, Id + 2];");
        _listExpression = _engine.Compile<CreationGlobals, List<int>>(
            "return [1, 2, 3, 4];");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _objectInitializer.Dispose();
        _collectionInitializer.Dispose();
        _arrayCreation.Dispose();
        _arrayExpression.Dispose();
        _listExpression.Dispose();
        _engine.Dispose();
    }

    [Benchmark(Baseline = true, Description = "object initializer / hand-written C#")]
    public Ticket NativeObjectInitializer() => new() { Id = _globals.Id, Name = _globals.Name };

    [Benchmark(Description = "object initializer / script")]
    public Ticket ScriptObjectInitializer() => _objectInitializer.Run(_globals);

    // Written as an initializer rather than a collection expression on purpose: `[1, 2, 3, 4]`
    // lowers to a span copy, which would compare the two languages' surface syntax instead of
    // the two compilers' lowering of the same construct.
    [Benchmark(Description = "collection initializer / hand-written C#")]
    public List<int> NativeCollectionInitializer() => new() { 1, 2, 3, 4 };

    [Benchmark(Description = "collection initializer / script")]
    public List<int> ScriptCollectionInitializer() => _collectionInitializer.Run(_globals);

    [Benchmark(Description = "new[] { ... } / hand-written C#")]
    public int[] NativeArrayCreation() => [_globals.Id, _globals.Id + 1, _globals.Id + 2];

    [Benchmark(Description = "new[] { ... } / script")]
    public int[] ScriptArrayCreation() => _arrayCreation.Run(_globals);

    [Benchmark(Description = "[a, b, c] to int[] / hand-written C#")]
    public int[] NativeArrayExpression() => [_globals.Id, _globals.Id + 1, _globals.Id + 2];

    [Benchmark(Description = "[a, b, c] to int[] / script")]
    public int[] ScriptArrayExpression() => _arrayExpression.Run(_globals);

    // C# lowers a collection expression to a List<T> through a span copy; the engine lowers it
    // the way a collection initializer works, one Add per element. This row is what that costs.
    [Benchmark(Description = "[1, 2, 3, 4] to List<int> / hand-written C#")]
    public List<int> NativeListExpression() => [1, 2, 3, 4];

    [Benchmark(Description = "[1, 2, 3, 4] to List<int> / script")]
    public List<int> ScriptListExpression() => _listExpression.Run(_globals);
}

public sealed class CreationGlobals
{
    public int Id { get; init; }
    public double Amount { get; init; }
    public string Name { get; init; } = "";
}

public sealed class Ticket
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
