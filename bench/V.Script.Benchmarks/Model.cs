namespace V.Script.Benchmarks;

public sealed class PricingContext
{
    public decimal Price { get; init; }
    public int Quantity { get; init; }
    public decimal TaxRate { get; init; }
    public decimal Discount { get; init; }
    public bool IsVip { get; init; }
    public int Threshold { get; init; }
}

public sealed class LoopContext
{
    public int Iterations { get; init; }
    public int Seed { get; init; }
}

public sealed class AsyncContext
{
    public FakeService Service { get; init; } = new();
    public int Seed { get; init; }
}

public sealed class FakeService
{
    /// <summary>Always already completed, so the benchmark measures the await machinery itself.</summary>
    public Task<int> GetAsync(int id) => Task.FromResult(id * 2);
}
