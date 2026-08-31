using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Lambdas and LINQ. Captured variables live in a boxed closure slot, so the interesting number
/// is the gap between a lambda that captures nothing and one that does.
/// </summary>
[MemoryDiagnoser]
public class LambdaBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<ItemContext, int> _plain = null!;
    private Script<ItemContext, int> _capturing = null!;
    private Script<ItemContext, int> _linq = null!;
    private Script<ItemContext, decimal> _projection = null!;
    private Script<ItemContext, int> _blockBody = null!;
    private ItemContext _context = null!;

    private const string PlainSource = "Fn.CountMatching(Numbers, x => x > 2)";
    private const string CapturingSource = "var floor = Threshold; return Fn.CountMatching(Numbers, x => x > floor);";
    private const string LinqSource = "Numbers.Where(x => x > 2).Select(x => x * 3).Sum()";
    private const string ProjectionSource = "Items.Sum(i => i.Price * i.Quantity)";

    private const string BlockBodySource =
        "Fn.CountMatching(Numbers, x => { var doubled = x * 2; return doubled > Threshold; })";

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(ItemContext))
            .AddImports("V.Script.Benchmarks"));

        _plain = _engine.Compile<ItemContext, int>(PlainSource);
        _capturing = _engine.Compile<ItemContext, int>(CapturingSource);
        _linq = _engine.Compile<ItemContext, int>(LinqSource);
        _projection = _engine.Compile<ItemContext, decimal>(ProjectionSource);
        _blockBody = _engine.Compile<ItemContext, int>(BlockBodySource);

        _context = new ItemContext
        {
            Numbers = [1, 2, 3, 4, 5, 6, 7, 8],
            Threshold = 2,
            Items =
            [
                new BenchItem { Price = 10m, Quantity = 2 },
                new BenchItem { Price = 5m, Quantity = 3 },
                new BenchItem { Price = 2.5m, Quantity = 4 },
            ],
        };
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    [Benchmark(Baseline = true, Description = "predicate / hand-written C#")]
    public int PredicateNative() => _context.Fn.CountMatching(_context.Numbers, x => x > 2);

    [Benchmark(Description = "predicate / script, no capture")]
    public int PredicateScript() => _plain.Run(_context);

    [Benchmark(Description = "predicate / script, capturing")]
    public int PredicateCapturingScript() => _capturing.Run(_context);

    [Benchmark(Description = "LINQ chain / hand-written C#")]
    public int LinqNative() => _context.Numbers.Where(x => x > 2).Select(x => x * 3).Sum();

    [Benchmark(Description = "LINQ chain / script")]
    public int LinqScript() => _linq.Run(_context);

    [Benchmark(Description = "decimal projection / hand-written C#")]
    public decimal ProjectionNative() => _context.Items.Sum(i => i.Price * i.Quantity);

    [Benchmark(Description = "decimal projection / script")]
    public decimal ProjectionScript() => _projection.Run(_context);

    [Benchmark(Description = "block-bodied predicate / hand-written C#")]
    public int BlockBodyNative() =>
        _context.Fn.CountMatching(_context.Numbers, x => { var doubled = x * 2; return doubled > _context.Threshold; });

    [Benchmark(Description = "block-bodied predicate / script")]
    public int BlockBodyScript() => _blockBody.Run(_context);
}

/// <summary>
/// Pattern matching. A switch expression lowers to a chain of tests and conditionals, so the
/// question is whether that chain costs more than the equivalent hand-written branching.
/// Both sides classify the same array of inputs, because a single constant input would simply
/// be folded away on the C# side and the comparison would be meaningless.
/// </summary>
[MemoryDiagnoser]
public class PatternBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<ItemContext, string> _switchExpression = null!;
    private Script<ItemContext, bool> _typePattern = null!;

    private int[] _numbers = null!;
    private object?[] _boxed = null!;

    // Pre-built so the measurement is the pattern test, not the globals allocation.
    private ItemContext[] _numberContexts = null!;
    private ItemContext[] _boxedContexts = null!;

    private const string SwitchSource = """
        Threshold switch
        {
            < 0 => "negative",
            0 => "zero",
            > 0 and < 10 => "small",
            _ => "large",
        }
        """;

    private const string TypePatternSource = "Boxed is int n and > 1";

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(ItemContext))
            .AddImports("V.Script.Benchmarks"));

        _switchExpression = _engine.Compile<ItemContext, string>(SwitchSource);
        _typePattern = _engine.Compile<ItemContext, bool>(TypePatternSource);

        _numbers = [-5, 0, 3, 9, 50, -1, 7, 100];
        _boxed = [1, 5, "text", null, 0, 42, 2.5, 9];

        _numberContexts = [.. _numbers.Select(n => new ItemContext { Threshold = n })];
        _boxedContexts = [.. _boxed.Select(b => new ItemContext { Boxed = b })];
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    private static string Classify(int value) => value switch
    {
        < 0 => "negative",
        0 => "zero",
        > 0 and < 10 => "small",
        _ => "large",
    };

    [Benchmark(Baseline = true, Description = "switch expression x8 / hand-written C#")]
    public int SwitchNative()
    {
        var total = 0;
        foreach (var value in _numbers) total += Classify(value).Length;
        return total;
    }

    [Benchmark(Description = "switch expression x8 / script")]
    public int SwitchScript()
    {
        var total = 0;
        foreach (var context in _numberContexts) total += _switchExpression.Run(context).Length;
        return total;
    }

    [Benchmark(Description = "type pattern x8 / hand-written C#")]
    public int TypePatternNative()
    {
        var total = 0;
        foreach (var value in _boxed) if (value is int n and > 1) total++;
        return total;
    }

    [Benchmark(Description = "type pattern x8 / script")]
    public int TypePatternScript()
    {
        var total = 0;
        foreach (var context in _boxedContexts) if (_typePattern.Run(context)) total++;
        return total;
    }
}

public sealed class ItemContext
{
    public int[] Numbers { get; init; } = [];
    public int Threshold { get; init; }
    public object? Boxed { get; init; }
    public List<BenchItem> Items { get; init; } = [];
    public BenchFunctional Fn { get; init; } = new();
}

public sealed class BenchItem
{
    public decimal Price { get; init; }
    public int Quantity { get; init; }
}

public sealed class BenchFunctional
{
    public int CountMatching(int[] values, Func<int, bool> predicate)
    {
        var count = 0;
        foreach (var value in values) if (predicate(value)) count++;
        return count;
    }
}
