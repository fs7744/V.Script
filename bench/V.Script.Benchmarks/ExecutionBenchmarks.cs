using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Compares compiled scripts against the equivalent hand-written C#. Both end up as JIT-compiled
/// IL, so the expected result is parity — anything materially slower points at a bad opcode
/// choice in the emitter.
/// </summary>
[MemoryDiagnoser]
public class ExecutionBenchmarks
{
    private ScriptEngine _engine = null!;
    private Script<PricingContext, decimal> _pricing = null!;
    private Script<PricingContext, bool> _rule = null!;
    private Script<LoopContext, int> _loop = null!;

    private PricingContext _pricingContext = null!;
    private LoopContext _loopContext = null!;

    private const string PricingSource =
        "Price * Quantity * (1 - Discount) * (1 + TaxRate)";

    private const string RuleSource =
        "IsVip && Quantity >= Threshold && Price * Quantity > 100m";

    private const string LoopSource = """
        var acc = Seed;
        for (var i = 0; i < Iterations; i++)
            acc = (acc * 31 + i) % 1000003;
        return acc;
        """;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new ScriptEngine();

        _pricing = _engine.Compile<PricingContext, decimal>(PricingSource);
        _rule = _engine.Compile<PricingContext, bool>(RuleSource);
        _loop = _engine.Compile<LoopContext, int>(LoopSource);

        _pricingContext = new PricingContext
        {
            Price = 19.99m,
            Quantity = 3,
            TaxRate = 0.0825m,
            Discount = 0.1m,
            IsVip = true,
            Threshold = 2,
        };

        _loopContext = new LoopContext { Iterations = 1000, Seed = 12345 };
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    // ---------------------------------------------------------------- formula

    [Benchmark(Baseline = true, Description = "formula / hand-written C#")]
    public decimal FormulaNative()
    {
        var c = _pricingContext;
        return c.Price * c.Quantity * (1 - c.Discount) * (1 + c.TaxRate);
    }

    [Benchmark(Description = "formula / script")]
    public decimal FormulaScript() => _pricing.Run(_pricingContext);

    // ---------------------------------------------------------------- rule

    [Benchmark(Description = "rule / hand-written C#")]
    public bool RuleNative()
    {
        var c = _pricingContext;
        return c.IsVip && c.Quantity >= c.Threshold && c.Price * c.Quantity > 100m;
    }

    [Benchmark(Description = "rule / script")]
    public bool RuleScript() => _rule.Run(_pricingContext);


    // ---------------------------------------------------------------- loop

    [Benchmark(Description = "loop x1000 / hand-written C#")]
    public int LoopNative()
    {
        var acc = _loopContext.Seed;
        for (var i = 0; i < _loopContext.Iterations; i++)
            acc = (acc * 31 + i) % 1000003;
        return acc;
    }

    [Benchmark(Description = "loop x1000 / script, no limits")]
    public int LoopScript() => _loop.Run(_loopContext);

}
