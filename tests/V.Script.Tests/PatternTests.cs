using V.Script.Diagnostics;

namespace V.Script.Tests;

public sealed class TypePatternTests : ScriptTest
{
    [Fact]
    public void Type_pattern_binds_a_variable()
    {
        const string source = """
            if (Shape is Circle c) return c.Radius;
            return -1.0;
            """;

        var globals = new PatternGlobals { Shape = new Circle { Radius = 2.5 } };
        Assert.Equal(2.5, Run<PatternGlobals, double>(source, globals), 10);

        var other = new PatternGlobals { Shape = new Rectangle { Width = 1, Height = 2 } };
        Assert.Equal(-1.0, Run<PatternGlobals, double>(source, other), 10);
    }

    [Fact]
    public void Type_pattern_without_a_designation_is_a_plain_test()
    {
        var globals = new PatternGlobals { Shape = new Circle() };
        Assert.True(Run<PatternGlobals, bool>("Shape is Circle", globals));
        Assert.False(Run<PatternGlobals, bool>("Shape is Rectangle", globals));
    }

    [Fact]
    public void Type_pattern_against_null_does_not_match()
    {
        var globals = new PatternGlobals { Shape = null };
        Assert.False(Run<PatternGlobals, bool>("Shape is Circle", globals));
    }

    [Fact]
    public void Value_type_pattern_over_object()
    {
        const string source = """
            if (Value is int n) return n * 2;
            return -1;
            """;

        Assert.Equal(84, Run<PatternGlobals, int>(source, new PatternGlobals { Value = 42 }));
        Assert.Equal(-1, Run<PatternGlobals, int>(source, new PatternGlobals { Value = "text" }));
        Assert.Equal(-1, Run<PatternGlobals, int>(source, new PatternGlobals { Value = null }));
    }

    [Fact]
    public void Nullable_subject_matches_its_underlying_type()
    {
        const string source = """
            if (MaybeNumber is int n) return n;
            return -1;
            """;

        Assert.Equal(7, Run<PatternGlobals, int>(source, new PatternGlobals { MaybeNumber = 7 }));
        Assert.Equal(-1, Run<PatternGlobals, int>(source, new PatternGlobals { MaybeNumber = null }));
    }

    [Fact]
    public void String_pattern_over_object()
    {
        const string source = """
            if (Value is string s) return s.Length;
            return -1;
            """;

        Assert.Equal(3, Run<PatternGlobals, int>(source, new PatternGlobals { Value = "abc" }));
    }

    [Fact]
    public void Pattern_variable_is_visible_after_the_test()
    {
        const string source = """
            var total = 0;
            if (Value is int n) total = n;
            if (Value is string s) total = s.Length;
            return total;
            """;

        Assert.Equal(5, Run<PatternGlobals, int>(source, new PatternGlobals { Value = 5 }));
    }
}

public sealed class ConstantPatternTests : ScriptTest
{
    [Fact]
    public void Null_and_not_null()
    {
        var present = new PatternGlobals { Text = "x" };
        var missing = new PatternGlobals { Text = null };

        Assert.False(Run<PatternGlobals, bool>("Text is null", present));
        Assert.True(Run<PatternGlobals, bool>("Text is null", missing));
        Assert.True(Run<PatternGlobals, bool>("Text is not null", present));
        Assert.False(Run<PatternGlobals, bool>("Text is not null", missing));
    }

    [Fact]
    public void Numeric_and_string_constants()
    {
        Assert.True(Run<PatternGlobals, bool>("Number is 5", new PatternGlobals { Number = 5 }));
        Assert.False(Run<PatternGlobals, bool>("Number is 5", new PatternGlobals { Number = 6 }));
        Assert.True(Run<PatternGlobals, bool>("Text is \"abc\"", new PatternGlobals { Text = "abc" }));
    }

    [Fact]
    public void Negative_constant()
    {
        Assert.True(Run<PatternGlobals, bool>("Number is -3", new PatternGlobals { Number = -3 }));
    }

    [Fact]
    public void Enum_member_is_a_constant_not_a_type()
    {
        var globals = new PatternGlobals { State = Status.Active };
        Assert.True(Run<PatternGlobals, bool>("State is Status.Active", globals));
        Assert.False(Run<PatternGlobals, bool>("State is Status.Suspended", globals));
    }

    [Fact]
    public void Nullable_against_a_constant()
    {
        Assert.True(Run<PatternGlobals, bool>("MaybeNumber is 4", new PatternGlobals { MaybeNumber = 4 }));
        Assert.False(Run<PatternGlobals, bool>("MaybeNumber is 4", new PatternGlobals { MaybeNumber = null }));
    }
}

public sealed class RelationalPatternTests : ScriptTest
{
    private static PatternGlobals N(int value) => new() { Number = value };

    [Theory]
    [InlineData(5, "Number is > 3", true)]
    [InlineData(3, "Number is > 3", false)]
    [InlineData(3, "Number is >= 3", true)]
    [InlineData(2, "Number is < 3", true)]
    [InlineData(3, "Number is <= 3", true)]
    public void Simple_relational(int value, string source, bool expected) =>
        Assert.Equal(expected, Run<PatternGlobals, bool>(source, N(value)));

    [Theory]
    [InlineData(50, true)]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void Range_with_and(int value, bool expected) =>
        Assert.Equal(expected, Run<PatternGlobals, bool>("Number is >= 0 and <= 100", N(value)));

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, false)]
    [InlineData(100, true)]
    public void Alternatives_with_or(int value, bool expected) =>
        Assert.Equal(expected, Run<PatternGlobals, bool>("Number is 0 or 100", N(value)));

    [Fact]
    public void Not_combined_with_a_range()
    {
        Assert.True(Run<PatternGlobals, bool>("Number is not (>= 0 and <= 10)", N(50)));
        Assert.False(Run<PatternGlobals, bool>("Number is not (>= 0 and <= 10)", N(5)));
    }

    [Fact]
    public void And_binds_tighter_than_or()
    {
        // 'is 1 or > 5 and < 10' means '1 or (>5 and <10)'
        Assert.True(Run<PatternGlobals, bool>("Number is 1 or > 5 and < 10", N(1)));
        Assert.True(Run<PatternGlobals, bool>("Number is 1 or > 5 and < 10", N(7)));
        Assert.False(Run<PatternGlobals, bool>("Number is 1 or > 5 and < 10", N(20)));
    }

    [Fact]
    public void Relational_over_decimal()
    {
        var globals = new OrderGlobals { TaxRate = 0.2m };
        Assert.True(Run<OrderGlobals, bool>("TaxRate is > 0.1m", globals));
    }
}

public sealed class VarAndDiscardPatternTests : ScriptTest
{
    [Fact]
    public void Var_pattern_always_matches_and_names_the_value()
    {
        const string source = """
            if (Number is var n) return n * 2;
            return -1;
            """;

        Assert.Equal(14, Run<PatternGlobals, int>(source, new PatternGlobals { Number = 7 }));
    }

    [Fact]
    public void Discard_always_matches()
    {
        Assert.True(Run<PatternGlobals, bool>("Number is _", new PatternGlobals()));
    }

    [Fact]
    public void Underscore_is_still_usable_as_an_identifier_elsewhere()
    {
        Assert.Equal(3, Eval<int>("var _ = 3; return _;"));
    }
}

public sealed class PropertyPatternTests : ScriptTest
{
    private static PatternGlobals WithOrder(int count, string code) => new()
    {
        Order = new Order { Count = count, Code = code },
    };

    [Fact]
    public void Property_pattern_tests_members()
    {
        Assert.True(Run<PatternGlobals, bool>("Order is { Count: > 2 }", WithOrder(5, "a")));
        Assert.False(Run<PatternGlobals, bool>("Order is { Count: > 2 }", WithOrder(1, "a")));
    }

    [Fact]
    public void Several_subpatterns_must_all_match()
    {
        Assert.True(Run<PatternGlobals, bool>(
            "Order is { Count: > 2, Code: \"a\" }", WithOrder(5, "a")));
        Assert.False(Run<PatternGlobals, bool>(
            "Order is { Count: > 2, Code: \"b\" }", WithOrder(5, "a")));
    }

    [Fact]
    public void Property_pattern_never_matches_null()
    {
        var globals = new PatternGlobals { Customer = null };
        Assert.False(Run<PatternGlobals, bool>("Customer is { Name: \"x\" }", globals));
    }

    [Fact]
    public void Typed_property_pattern()
    {
        const string source = """
            if (Shape is Circle { Radius: > 1.0 } c) return c.Radius;
            return -1.0;
            """;

        Assert.Equal(2.0, Run<PatternGlobals, double>(source,
            new PatternGlobals { Shape = new Circle { Radius = 2.0 } }), 10);

        Assert.Equal(-1.0, Run<PatternGlobals, double>(source,
            new PatternGlobals { Shape = new Circle { Radius = 0.5 } }), 10);

        Assert.Equal(-1.0, Run<PatternGlobals, double>(source,
            new PatternGlobals { Shape = new Rectangle() }), 10);
    }

    [Fact]
    public void Nested_property_pattern()
    {
        var globals = new PatternGlobals
        {
            Customer = new Customer { Name = "amy", Referrer = new Customer { Name = "bob" } },
        };

        Assert.True(Run<PatternGlobals, bool>(
            "Customer is { Referrer: { Name: \"bob\" } }", globals));

        Assert.False(Run<PatternGlobals, bool>(
            "Customer is { Referrer: { Name: \"cat\" } }", globals));
    }

    [Fact]
    public void Property_pattern_with_a_designation()
    {
        const string source = """
            if (Order is { Count: > 0 } o) return o.Count;
            return -1;
            """;

        Assert.Equal(4, Run<PatternGlobals, int>(source, WithOrder(4, "a")));
    }

    [Fact]
    public void Empty_property_pattern_is_a_null_check()
    {
        Assert.True(Run<PatternGlobals, bool>("Customer is { }",
            new PatternGlobals { Customer = new Customer() }));
        Assert.False(Run<PatternGlobals, bool>("Customer is { }",
            new PatternGlobals { Customer = null }));
    }

    [Fact]
    public void Unknown_property_is_reported()
    {
        AssertError<PatternGlobals, bool>("Order is { Nope: 1 }", ErrorCode.UndefinedMember);
    }
}

public sealed class SwitchExpressionTests : ScriptTest
{
    [Fact]
    public void Constant_arms()
    {
        const string source = """
            return Number switch
            {
                0 => "zero",
                1 => "one",
                _ => "many",
            };
            """;

        Assert.Equal("zero", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 0 }));
        Assert.Equal("one", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 1 }));
        Assert.Equal("many", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 9 }));
    }

    [Fact]
    public void Relational_arms()
    {
        const string source = """
            return Number switch
            {
                < 0 => "negative",
                0 => "zero",
                > 0 and < 10 => "small",
                _ => "large",
            };
            """;

        Assert.Equal("negative", Run<PatternGlobals, string>(source, new PatternGlobals { Number = -5 }));
        Assert.Equal("zero", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 0 }));
        Assert.Equal("small", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 5 }));
        Assert.Equal("large", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 50 }));
    }

    [Fact]
    public void Type_arms_with_their_own_variables()
    {
        // Both arms name their variable `s`; each arm is its own name scope.
        const string source = """
            return Shape switch
            {
                Circle s => s.Radius,
                Rectangle s => s.Width * s.Height,
                _ => 0.0,
            };
            """;

        Assert.Equal(2.0, Run<PatternGlobals, double>(source,
            new PatternGlobals { Shape = new Circle { Radius = 2.0 } }), 10);

        Assert.Equal(6.0, Run<PatternGlobals, double>(source,
            new PatternGlobals { Shape = new Rectangle { Width = 2, Height = 3 } }), 10);

        Assert.Equal(0.0, Run<PatternGlobals, double>(source, new PatternGlobals()), 10);
    }

    [Fact]
    public void When_guard_narrows_an_arm()
    {
        const string source = """
            return Shape switch
            {
                Circle c when c.Radius > 10.0 => "big circle",
                Circle => "circle",
                _ => "other",
            };
            """;

        Assert.Equal("big circle", Run<PatternGlobals, string>(source,
            new PatternGlobals { Shape = new Circle { Radius = 20 } }));

        Assert.Equal("circle", Run<PatternGlobals, string>(source,
            new PatternGlobals { Shape = new Circle { Radius = 1 } }));
    }

    [Fact]
    public void Arms_are_tried_in_order()
    {
        const string source = """
            return Number switch
            {
                > 0 => "positive",
                > 100 => "unreachable",
                _ => "other",
            };
            """;

        Assert.Equal("positive", Run<PatternGlobals, string>(source, new PatternGlobals { Number = 500 }));
    }

    [Fact]
    public void Unmatched_input_throws()
    {
        const string source = """
            return Number switch
            {
                0 => "zero",
                1 => "one",
            };
            """;

        var exception = Assert.Throws<System.Runtime.CompilerServices.SwitchExpressionException>(
            () => Run<PatternGlobals, string>(source, new PatternGlobals { Number = 7 }));

        Assert.Equal(7, exception.UnmatchedValue);
    }

    [Fact]
    public void Arm_results_find_a_common_type()
    {
        const string source = """
            return Number switch
            {
                0 => 1,
                _ => 2.5,
            };
            """;

        Assert.Equal(1.0, Run<PatternGlobals, double>(source, new PatternGlobals { Number = 0 }), 10);
        Assert.Equal(2.5, Run<PatternGlobals, double>(source, new PatternGlobals { Number = 3 }), 10);
    }

    [Fact]
    public void Incompatible_arm_types_are_rejected()
    {
        const string source = """
            return Number switch
            {
                0 => 1,
                _ => "two",
            };
            """;

        AssertError<PatternGlobals, object>(source, ErrorCode.SwitchArmTypeMismatch);
    }

    [Fact]
    public void Switch_expression_nests()
    {
        const string source = """
            return Number switch
            {
                > 0 => Text switch { null => "positive", _ => "positive " + Text },
                _ => "other",
            };
            """;

        Assert.Equal("positive", Run<PatternGlobals, string>(source,
            new PatternGlobals { Number = 1, Text = null }));

        Assert.Equal("positive x", Run<PatternGlobals, string>(source,
            new PatternGlobals { Number = 1, Text = "x" }));
    }

    [Fact]
    public void Switch_expression_inside_a_lambda()
    {
        const string source = """
            Func<int, string> classify = n => n switch { 0 => "zero", _ => "other" };
            return classify(0) + "/" + classify(1);
            """;

        Assert.Equal("zero/other", Run<PatternGlobals, string>(source, new PatternGlobals()));
    }

    [Fact]
    public void Switch_expression_over_a_property_pattern()
    {
        const string source = """
            return Order switch
            {
                { Count: 0 } => "empty",
                { Count: < 10 } => "small",
                _ => "large",
            };
            """;

        Assert.Equal("empty", Run<PatternGlobals, string>(source,
            new PatternGlobals { Order = new Order { Count = 0 } }));

        Assert.Equal("small", Run<PatternGlobals, string>(source,
            new PatternGlobals { Order = new Order { Count = 3 } }));

        Assert.Equal("large", Run<PatternGlobals, string>(source,
            new PatternGlobals { Order = new Order { Count = 30 } }));
    }
}

/// <summary>
/// The engine no longer manages cancellation, so these pin the two host-side routes that
/// replace it: a token on the globals object, and a token as an ordinary delegate parameter.
/// </summary>
public sealed class CancellationTests : ScriptTest
{
    [Fact]
    public async Task Token_passed_through_globals_reaches_the_awaited_call()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        var globals = new CancellableGlobals { Token = cts.Token };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync<CancellableGlobals, int>("await Service.WaitForeverAsync(Token)", globals));
    }

    [Fact]
    public async Task Token_through_globals_is_per_invocation()
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.CompileAsync<CancellableGlobals, int>(
            "await Service.EchoAsync(7, Token)");

        Assert.Equal(7, await script.RunAsync(new CancellableGlobals { Token = CancellationToken.None }));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => script.RunAsync(new CancellableGlobals { Token = cancelled.Token }));
    }

    [Fact]
    public async Task Token_as_a_delegate_parameter()
    {
        using var engine = new ScriptEngine(Options);
        using var compiled = engine.CompileAsyncDelegate<Func<CancellableService, CancellationToken, Task<int>>>(
            "await svc.EchoAsync(42, ct)", "svc", "ct");

        Assert.Equal(42, await compiled.Value(new CancellableService(), CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => compiled.Value(new CancellableService(), cancelled.Token));
    }

    [Fact]
    public void Token_works_in_a_synchronous_delegate_too()
    {
        using var engine = new ScriptEngine(Options);
        var f = engine.CompileDelegate<Func<CancellationToken, bool>>(
            "ct.IsCancellationRequested", "ct");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.False(f(CancellationToken.None));
        Assert.True(f(cancelled.Token));
    }
}
