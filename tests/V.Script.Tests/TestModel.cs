namespace V.Script.Tests;

// ---------------------------------------------------------------- globals shapes

public sealed class EmptyGlobals;

public sealed class NumberGlobals
{
    public int A { get; init; }
    public int B { get; init; }
    public long BigA { get; init; }
    public double D { get; init; }
    public decimal M { get; init; }
    public float F { get; init; }
    public byte Small { get; init; }
    public uint U { get; init; }
    public int? MaybeA { get; init; }
    public int? MaybeB { get; init; }
    public decimal? MaybeM { get; init; }
    public bool Flag { get; init; }
    public bool? MaybeFlag { get; init; }
    public string? Text { get; init; }
    public char Ch { get; init; }
}

public sealed class OrderGlobals
{
    public Order Order { get; init; } = new();
    public decimal TaxRate { get; init; }
    public int[] Numbers { get; init; } = [];
    public List<string> Names { get; init; } = [];
    public Dictionary<string, int> Lookup { get; init; } = [];
    public Customer? Customer { get; init; }
    public Status State { get; init; }
    public Money Wallet { get; init; }
    public Calculator Calc { get; init; } = new();
}

// ---------------------------------------------------------------- model types

public sealed class Order
{
    public List<OrderItem> Items { get; init; } = [];
    public string Code { get; set; } = "";
    public int Count { get; set; }
    public Customer? Customer { get; set; }

    public decimal Subtotal()
    {
        decimal sum = 0;
        foreach (var item in Items) sum += item.Price * item.Quantity;
        return sum;
    }
}

public sealed class OrderItem
{
    public decimal Price { get; init; }
    public int Quantity { get; init; }
    public string Sku { get; init; } = "";
}

public sealed class Customer
{
    public string Name { get; set; } = "";
    public bool IsVip { get; init; }
    public Customer? Referrer { get; init; }
    public int? Age { get; init; }
}

public enum Status
{
    None = 0,
    Active = 1,
    Suspended = 2,
}

/// <summary>Exercises operator overloading, user-defined conversions and equality.</summary>
public readonly struct Money(decimal amount) : IEquatable<Money>
{
    public decimal Amount { get; } = amount;

    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);
    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount);
    public static Money operator *(Money left, int factor) => new(left.Amount * factor);
    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;
    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;
    public static bool operator ==(Money left, Money right) => left.Amount == right.Amount;
    public static bool operator !=(Money left, Money right) => left.Amount != right.Amount;

    public static implicit operator decimal(Money money) => money.Amount;
    public static explicit operator Money(decimal amount) => new(amount);

    public bool Equals(Money other) => Amount == other.Amount;
    public override bool Equals(object? obj) => obj is Money other && Equals(other);
    public override int GetHashCode() => Amount.GetHashCode();
    public override string ToString() => Amount.ToString("0.##");
}

/// <summary>Exercises overload resolution, params, optional arguments and static members.</summary>
public sealed class Calculator
{
    public int Add(int a, int b) => a + b;
    public double Add(double a, double b) => a + b;
    public decimal Add(decimal a, decimal b) => a + b;
    public long Add(long a, long b) => a + b;

    public int Sum(params int[] values)
    {
        var total = 0;
        foreach (var value in values) total += value;
        return total;
    }

    public string Describe(string label, int count = 1, string suffix = "!") => $"{label}:{count}{suffix}";

    public int Ambiguous(int a, long b) => 1;
    public int Ambiguous(long a, int b) => 2;

    public static int Doubled(int value) => value * 2;
    public static readonly int Magic = 42;

    public int this[int index] => index * 10;
    public string this[string key] => key.ToUpperInvariant();

    public int Counter { get; set; }
    public int ReadOnlyValue => 7;
}

// ---------------------------------------------------------------- async shapes

public sealed class AsyncGlobals
{
    public AsyncService Service { get; init; } = new();
    public int[] Ids { get; init; } = [];
    public int Seed { get; init; }
}

public sealed class AsyncService
{
    public int Calls;

    public async Task<int> GetAsync(int id)
    {
        Interlocked.Increment(ref Calls);
        await Task.Yield();
        return id * 2;
    }

    public async Task<int> DelayedAsync(int id, int milliseconds)
    {
        await Task.Delay(milliseconds).ConfigureAwait(false);
        return id;
    }

    public Task<int> CompletedAsync(int id) => Task.FromResult(id + 1);

    public async ValueTask<int> ValueAsync(int id)
    {
        await Task.Yield();
        return id + 100;
    }

    public async Task NoResultAsync()
    {
        Interlocked.Increment(ref Calls);
        await Task.Yield();
    }

    public Task<int> NeverAsync() => new TaskCompletionSource<int>().Task;

    public async Task<int> ThrowingAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }
}
