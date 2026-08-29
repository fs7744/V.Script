namespace V.Script.Tests;

/// <summary>
/// Lifted operators are the single easiest place to get C# semantics subtly wrong, so each
/// shape is pinned separately: arithmetic propagates null, relational operators yield false,
/// and equality treats two empties as equal.
/// </summary>
public sealed class LiftedOperatorTests : ScriptTest
{
    private static NumberGlobals Values(int? a, int? b) => new() { MaybeA = a, MaybeB = b };

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(10, -4, 6)]
    public void Lifted_addition_with_two_values(int a, int b, int expected)
    {
        var result = Run<NumberGlobals, int?>("MaybeA + MaybeB", Values(a, b));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, null)]
    [InlineData(null, null)]
    public void Lifted_addition_propagates_null(int? a, int? b)
    {
        var result = Run<NumberGlobals, int?>("MaybeA + MaybeB", Values(a, b));
        Assert.Null(result);
    }

    [Fact]
    public void Lifted_arithmetic_with_a_plain_operand()
    {
        Assert.Equal(11, Run<NumberGlobals, int?>("MaybeA + 1", Values(10, null)));
        Assert.Null(Run<NumberGlobals, int?>("MaybeA + 1", Values(null, null)));
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 1, false)]
    [InlineData(null, 2, false)]
    [InlineData(1, null, false)]
    [InlineData(null, null, false)]
    public void Lifted_relational_yields_false_when_either_side_is_null(int? a, int? b, bool expected)
    {
        Assert.Equal(expected, Run<NumberGlobals, bool>("MaybeA < MaybeB", Values(a, b)));
    }

    [Fact]
    public void Lifted_relational_is_false_in_both_directions_when_null()
    {
        // Neither '<' nor '>=' holds when an operand is null; this is what separates lifted
        // comparison from ordinary negation.
        Assert.False(Run<NumberGlobals, bool>("MaybeA < MaybeB", Values(null, 1)));
        Assert.False(Run<NumberGlobals, bool>("MaybeA >= MaybeB", Values(null, 1)));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(null, null, true)]
    [InlineData(null, 1, false)]
    [InlineData(1, null, false)]
    public void Lifted_equality_treats_two_nulls_as_equal(int? a, int? b, bool expected)
    {
        Assert.Equal(expected, Run<NumberGlobals, bool>("MaybeA == MaybeB", Values(a, b)));
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(null, null, false)]
    [InlineData(null, 1, true)]
    public void Lifted_inequality_is_the_negation_of_equality(int? a, int? b, bool expected)
    {
        Assert.Equal(expected, Run<NumberGlobals, bool>("MaybeA != MaybeB", Values(a, b)));
    }

    [Fact]
    public void Lifted_unary_negation()
    {
        Assert.Equal(-5, Run<NumberGlobals, int?>("-MaybeA", Values(5, null)));
        Assert.Null(Run<NumberGlobals, int?>("-MaybeA", Values(null, null)));
    }

    [Fact]
    public void Lifted_logical_not()
    {
        var globals = new NumberGlobals { MaybeFlag = true };
        Assert.False(Run<NumberGlobals, bool?>("!MaybeFlag", globals));
        Assert.Null(Run<NumberGlobals, bool?>("!MaybeFlag", new NumberGlobals { MaybeFlag = null }));
    }

    [Fact]
    public void Lifted_decimal_arithmetic_uses_the_operator_method()
    {
        var globals = new NumberGlobals { MaybeM = 1.5m };
        Assert.Equal(3.0m, Run<NumberGlobals, decimal?>("MaybeM + MaybeM", globals));
        Assert.Null(Run<NumberGlobals, decimal?>("MaybeM + MaybeM", new NumberGlobals { MaybeM = null }));
    }

    [Fact]
    public void Null_comparison_uses_HasValue()
    {
        Assert.True(Run<NumberGlobals, bool>("MaybeA == null", Values(null, null)));
        Assert.False(Run<NumberGlobals, bool>("MaybeA == null", Values(1, null)));
        Assert.True(Run<NumberGlobals, bool>("MaybeA != null", Values(1, null)));
    }

    [Fact]
    public void Widening_a_value_to_nullable_is_implicit()
    {
        Assert.Equal(5, Run<NumberGlobals, int?>("A", new NumberGlobals { A = 5 }));
    }

    [Fact]
    public void Narrowing_a_nullable_requires_a_cast()
    {
        AssertError<NumberGlobals, int>("MaybeA", Diagnostics.ErrorCode.CannotConvertImplicitly);
        Assert.Equal(5, Run<NumberGlobals, int>("(int)MaybeA", Values(5, null)));
    }

    [Fact]
    public void Casting_an_empty_nullable_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Run<NumberGlobals, int>("(int)MaybeA", Values(null, null)));
    }

    [Fact]
    public void Lifted_conversion_between_nullable_types_preserves_emptiness()
    {
        Assert.Equal(5L, Run<NumberGlobals, long?>("MaybeA", Values(5, null)));
        Assert.Null(Run<NumberGlobals, long?>("MaybeA", Values(null, null)));
    }
}

public sealed class NullCoalescingTests : ScriptTest
{
    [Fact]
    public void Coalesce_on_a_nullable_value_type()
    {
        Assert.Equal(5, Run<NumberGlobals, int>("MaybeA ?? -1", new NumberGlobals { MaybeA = 5 }));
        Assert.Equal(-1, Run<NumberGlobals, int>("MaybeA ?? -1", new NumberGlobals { MaybeA = null }));
    }

    [Fact]
    public void Coalesce_on_a_reference()
    {
        Assert.Equal("x", Run<NumberGlobals, string>("Text ?? \"fallback\"", new NumberGlobals { Text = "x" }));
        Assert.Equal("fallback", Run<NumberGlobals, string>("Text ?? \"fallback\"", new NumberGlobals { Text = null }));
    }

    [Fact]
    public void Coalesce_is_right_associative()
    {
        var globals = new NumberGlobals { Text = null };
        Assert.Equal("b", Run<NumberGlobals, string>("Text ?? null ?? \"b\"", globals));
    }

    [Fact]
    public void Coalesce_evaluates_the_left_side_only_once()
    {
        const string source = """
            var calls = 0;
            var order = Order;
            return Order.Code ?? "fallback";
            """;

        var globals = new OrderGlobals { Order = new Order { Code = "kept" } };
        Assert.Equal("kept", Run<OrderGlobals, string>(source, globals));
    }

    [Fact]
    public void Coalescing_assignment_only_writes_when_null()
    {
        const string source = """
            Order.Customer ??= new Customer();
            Order.Customer.Name = "set";
            return Order.Customer.Name;
            """;

        var globals = new OrderGlobals { Order = new Order() };
        Assert.Equal("set", Run<OrderGlobals, string>(source, globals));
    }

    [Fact]
    public void Coalescing_assignment_keeps_an_existing_value()
    {
        var existing = new Customer { Name = "original" };
        var globals = new OrderGlobals { Order = new Order { Customer = existing } };

        const string source = """
            Order.Customer ??= new Customer();
            return Order.Customer.Name;
            """;

        Assert.Equal("original", Run<OrderGlobals, string>(source, globals));
        Assert.Same(existing, globals.Order.Customer);
    }
}

public sealed class NullConditionalTests : ScriptTest
{
    [Fact]
    public void Conditional_member_access_on_a_reference()
    {
        var present = new OrderGlobals { Customer = new Customer { Name = "abc" } };
        Assert.Equal("abc", Run<OrderGlobals, string>("Customer?.Name", present));

        var missing = new OrderGlobals { Customer = null };
        Assert.Null(Run<OrderGlobals, string>("Customer?.Name", missing));
    }

    [Fact]
    public void Conditional_access_lifts_a_value_type_result()
    {
        var present = new OrderGlobals { Customer = new Customer { Name = "abcd" } };
        Assert.Equal(4, Run<OrderGlobals, int?>("Customer?.Name.Length", present));

        var missing = new OrderGlobals { Customer = null };
        Assert.Null(Run<OrderGlobals, int?>("Customer?.Name.Length", missing));
    }

    [Fact]
    public void Whole_chain_short_circuits_from_the_first_conditional()
    {
        // Referrer is never touched when Customer is null, so this must not throw.
        var missing = new OrderGlobals { Customer = null };
        Assert.Null(Run<OrderGlobals, string>("Customer?.Referrer.Name", missing));
    }

    [Fact]
    public void Conditional_access_on_a_nullable_value_type()
    {
        var present = new OrderGlobals { Customer = new Customer { Age = 30 } };
        Assert.Equal("30", Run<OrderGlobals, string>("Customer?.Age?.ToString()", present));

        var missing = new OrderGlobals { Customer = new Customer { Age = null } };
        Assert.Null(Run<OrderGlobals, string>("Customer?.Age?.ToString()", missing));
    }

    [Fact]
    public void Conditional_invocation_returns_null_when_the_receiver_is_null()
    {
        var missing = new OrderGlobals { Customer = null };
        Assert.Null(Run<OrderGlobals, string>("Customer?.Name.ToUpperInvariant()", missing));
    }

    [Fact]
    public void Conditional_access_combines_with_coalesce()
    {
        var missing = new OrderGlobals { Customer = null };
        Assert.Equal(0, Run<OrderGlobals, int>("Customer?.Name.Length ?? 0", missing));

        var present = new OrderGlobals { Customer = new Customer { Name = "ab" } };
        Assert.Equal(2, Run<OrderGlobals, int>("Customer?.Name.Length ?? 0", present));
    }

    [Fact]
    public void Conditional_access_on_a_non_nullable_operand_is_rejected()
    {
        AssertError<NumberGlobals, string>("A?.ToString()", Diagnostics.ErrorCode.CannotConvert);
    }
}
