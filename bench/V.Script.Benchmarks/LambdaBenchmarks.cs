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
    private ItemContext _context = null!;

    private const string PlainSource = "Fn.CountMatching(Numbers, x => x > 2)";
    private const string CapturingSource = "var floor = Threshold; return Fn.CountMatching(Numbers, x => x > floor);";
    private const string LinqSource = "Numbers.Where(x => x > 2).Select(x => x * 3).Sum()";
    private const string ProjectionSource = "Items.Sum(i => i.Price * i.Quantity)";

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine(ScriptOptions.Default
            .AddReferencesFrom(typeof(ItemContext))
            .AddImports("V.Script.Benchmarks")
            .WithLimits(ScriptLimits.Unlimited));

        _plain = _engine.Compile<ItemContext, int>(PlainSource);
        _capturing = _engine.Compile<ItemContext, int>(CapturingSource);
        _linq = _engine.Compile<ItemContext, int>(LinqSource);
        _projection = _engine.Compile<ItemContext, decimal>(ProjectionSource);

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
}

public sealed class ItemContext
{
    public int[] Numbers { get; init; } = [];
    public int Threshold { get; init; }
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
