using V.Script.Diagnostics;

namespace V.Script.Tests;

public sealed class LambdaTests : ScriptTest
{
    private static LambdaGlobals Globals(params int[] numbers) =>
        new() { Numbers = numbers, Values = [.. numbers] };

    [Fact]
    public void Lambda_assigned_to_a_typed_local_and_invoked()
    {
        const string source = """
            Func<int, int> f = x => x * 2;
            return f(21);
            """;

        Assert.Equal(42, Run<LambdaGlobals, int>(source, Globals()));
    }

    [Fact]
    public void Lambda_with_two_parameters()
    {
        const string source = """
            Func<int, int, int> add = (a, b) => a + b;
            return add(3, 4);
            """;

        Assert.Equal(7, Run<LambdaGlobals, int>(source, Globals()));
    }

    [Fact]
    public void Lambda_with_no_parameters()
    {
        const string source = """
            Func<int> answer = () => 42;
            return answer();
            """;

        Assert.Equal(42, Run<LambdaGlobals, int>(source, Globals()));
    }

    [Fact]
    public void Lambda_passed_as_an_argument()
    {
        Assert.Equal(9, Run<LambdaGlobals, int>("Fn.Apply(x => x * 3, 3)", Globals()));
    }

    [Fact]
    public void Lambda_argument_drives_overload_selection()
    {
        // Apply(int), Apply(Func<int,int>) and Apply(Func<int,int>, int) all exist.
        Assert.Equal(500, Run<LambdaGlobals, int>("Fn.Apply(5)", Globals()));
        Assert.Equal(3, Run<LambdaGlobals, int>("Fn.Apply(x => x * 3)", Globals()));
        Assert.Equal(12, Run<LambdaGlobals, int>("Fn.Apply(x => x * 3, 4)", Globals()));
    }

    [Fact]
    public void Predicate_lambda_over_an_array()
    {
        Assert.Equal(2, Run<LambdaGlobals, int>(
            "Fn.CountMatching(Numbers, x => x > 2)", Globals(1, 2, 3, 4)));
    }

    [Fact]
    public void Two_parameter_lambda_folds()
    {
        Assert.Equal(10, Run<LambdaGlobals, int>(
            "Fn.Fold(Numbers, 0, (acc, x) => acc + x)", Globals(1, 2, 3, 4)));
    }

    [Fact]
    public void Action_lambda_returns_void()
    {
        var globals = Globals(1, 2, 3);
        Run<LambdaGlobals, int>("Fn.Each(Numbers, x => Counter.Add(x)); return Counter.Total;", globals);
        Assert.Equal(6, globals.Counter.Total);
    }

    [Fact]
    public void Lambda_returning_string()
    {
        var globals = new LambdaGlobals { Label = "hi" };
        Assert.Equal("hi!", Run<LambdaGlobals, string>("Fn.Produce(() => Label + \"!\")", globals));
    }

    [Fact]
    public void Delegate_valued_globals_member_is_invocable()
    {
        var globals = new LambdaGlobals { Transform = x => x + 100 };
        Assert.Equal(105, Run<LambdaGlobals, int>("Transform(5)", globals));
    }
}

public sealed class ClosureTests : ScriptTest
{
    [Fact]
    public void Lambda_captures_an_enclosing_local()
    {
        const string source = """
            var factor = 3;
            Func<int, int> f = x => x * factor;
            return f(5);
            """;

        Assert.Equal(15, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Capture_is_by_reference_so_later_writes_are_visible()
    {
        const string source = """
            var factor = 3;
            Func<int, int> f = x => x * factor;
            factor = 10;
            return f(5);
            """;

        Assert.Equal(50, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Lambda_writes_through_to_the_captured_variable()
    {
        const string source = """
            var total = 0;
            Fn.Each(Numbers, x => total = total + x);
            return total;
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(10, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Lambda_captures_a_globals_member()
    {
        var globals = new LambdaGlobals { Numbers = [1, 5, 9], Threshold = 4 };
        Assert.Equal(2, Run<LambdaGlobals, int>(
            "Fn.CountMatching(Numbers, x => x > Threshold)", globals));
    }

    [Fact]
    public void Capturing_a_string_reference()
    {
        var globals = new LambdaGlobals { Label = "abc" };
        Assert.Equal("abc/x", Run<LambdaGlobals, string>(
            "Fn.Produce(() => Label + \"/x\")", globals));
    }

    /// <summary>
    /// Since C# 5 the <c>foreach</c> variable is a fresh variable per iteration, so lambdas that
    /// outlive the loop each see their own value. The engine matches that by instantiating the
    /// body's closure once per pass.
    /// </summary>
    [Fact]
    public void Foreach_variable_is_captured_per_iteration()
    {
        const string source = """
            foreach (var n in Numbers)
                Sink.Add(() => n);

            var total = 0;
            for (var i = 0; i < Sink.Count; i++)
                total = total * 10 + Sink[i]();
            return total;
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(123, Run<LambdaGlobals, int>(source, globals));
    }

    /// <summary>
    /// A <c>for</c> loop variable, by contrast, is one variable for the whole loop — every
    /// lambda sees the final value. This is C#'s behaviour and the engine reproduces it.
    /// </summary>
    [Fact]
    public void For_loop_variable_is_shared_across_iterations()
    {
        const string source = """
            for (var i = 0; i < 3; i++)
                Sink.Add(() => i);

            var total = 0;
            for (var j = 0; j < Sink.Count; j++)
                total = total * 10 + Sink[j]();
            return total;
            """;

        var globals = new LambdaGlobals();
        Assert.Equal(333, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Block_scoped_variable_is_captured_per_entry()
    {
        const string source = """
            var i = 0;
            while (i < 3)
            {
                var copy = i;
                Sink.Add(() => copy);
                i++;
            }

            var total = 0;
            for (var j = 0; j < Sink.Count; j++)
                total = total * 10 + Sink[j]();
            return total;
            """;

        Assert.Equal(12, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Nested_lambda_captures_the_outer_lambda_parameter()
    {
        const string source = """
            Func<int, Func<int, int>> adder = a => b => a + b;
            var add10 = adder(10);
            return add10(5);
            """;

        Assert.Equal(15, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Nested_lambda_reaches_through_two_levels()
    {
        const string source = """
            var outer = 100;
            Func<int, Func<int, int>> f = a => b => outer + a + b;
            return f(20)(3);
            """;

        Assert.Equal(123, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Two_lambdas_share_one_captured_variable()
    {
        const string source = """
            var counter = 0;
            Func<int> bump = () => counter = counter + 1;
            Func<int> read = () => counter;
            bump();
            bump();
            return read();
            """;

        Assert.Equal(2, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Non_capturing_lambda_still_works_when_a_sibling_captures()
    {
        const string source = """
            var factor = 2;
            Func<int, int> scaled = x => x * factor;
            Func<int, int> plain = x => x + 1;
            return scaled(5) + plain(5);
            """;

        Assert.Equal(16, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Captured_decimal_round_trips_through_boxing()
    {
        const string source = """
            var rate = 1.5m;
            Func<int, decimal> f = x => x * rate;
            return f(4);
            """;

        Assert.Equal(6.0m, Run<LambdaGlobals, decimal>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Captured_nullable_preserves_emptiness()
    {
        const string source = """
            int? maybe = null;
            Func<int> f = () => maybe ?? -1;
            return f();
            """;

        Assert.Equal(-1, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }
}

public sealed class LambdaDiagnosticTests : ScriptTest
{
    [Fact]
    public void Block_bodied_lambda_is_rejected_with_its_own_code()
    {
        AssertError<LambdaGlobals, int>(
            "Fn.Apply(x => { return x; })", ErrorCode.LambdaBodyNotSupported);
    }

    [Fact]
    public void Var_cannot_infer_a_lambda_type()
    {
        AssertError<LambdaGlobals, int>("var f = x => x; return 1;", ErrorCode.CannotInferType);
    }

    [Fact]
    public void Wrong_parameter_count_is_reported()
    {
        AssertError<LambdaGlobals, int>(
            "Func<int, int> f = (a, b) => a; return f(1);", ErrorCode.WrongArgumentCount);
    }

    [Fact]
    public void Lambda_converted_to_a_non_delegate_is_rejected()
    {
        AssertError<LambdaGlobals, int>("int f = x => x; return f;", ErrorCode.CannotConvert);
    }

    [Fact]
    public void Await_inside_a_lambda_is_rejected()
    {
        AssertError<AsyncGlobals, int>(
            "Func<int> f = () => await Service.GetAsync(1); return f();",
            ErrorCode.AwaitInLambda,
            async: true);
    }

    [Fact]
    public void Delegate_invoked_with_the_wrong_argument_count()
    {
        AssertError<LambdaGlobals, int>(
            "Func<int, int> f = x => x; return f(1, 2);", ErrorCode.WrongArgumentCount);
    }

    [Fact]
    public void Body_type_must_convert_to_the_delegate_return_type()
    {
        AssertError<LambdaGlobals, int>(
            "Func<int, int> f = x => \"a\"; return f(1);", ErrorCode.CannotConvert);
    }
}
