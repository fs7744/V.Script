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
    public void Block_body_without_a_return_on_every_path_is_rejected()
    {
        AssertError<LambdaGlobals, int>(
            "Fn.Apply(x => { if (x > 0) return x; })", ErrorCode.NotAllCodePathsReturn);
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


/// <summary>
/// Block bodies. These carry their own return epilogue, so <c>return</c> works inside them —
/// including from within a <c>try</c>, which needs a <c>leave</c> rather than a bare <c>ret</c>.
/// </summary>
public sealed class BlockLambdaTests : ScriptTest
{
    [Fact]
    public void Block_body_with_a_local_and_a_return()
    {
        const string source = """
            Func<int, int> f = x => { var doubled = x * 2; return doubled + 1; };
            return f(5);
            """;

        Assert.Equal(11, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Block_body_with_branching_returns()
    {
        const string source = """
            Func<int, string> f = x =>
            {
                if (x > 10) return "big";
                if (x > 0) return "small";
                return "none";
            };
            return f(5) + f(50) + f(0);
            """;

        Assert.Equal("smallbignone", Run<LambdaGlobals, string>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Block_body_may_contain_a_loop()
    {
        const string source = """
            Func<int, int> f = n =>
            {
                var total = 0;
                for (var i = 1; i <= n; i++) total += i;
                return total;
            };
            return f(10);
            """;

        Assert.Equal(55, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Block_body_may_contain_foreach()
    {
        const string source = """
            Func<int[], int> f = xs =>
            {
                var total = 0;
                foreach (var x in xs) total += x;
                return total;
            };
            return f(Numbers);
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(10, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Return_from_inside_try_leaves_the_protected_region()
    {
        const string source = """
            Func<int, int> f = x =>
            {
                try { return x / 0; }
                catch (DivideByZeroException) { return -1; }
            };
            return f(5);
            """;

        Assert.Equal(-1, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Finally_inside_a_block_body_still_runs()
    {
        const string source = """
            var log = "";
            Func<int, int> f = x =>
            {
                try { return x; }
                finally { Counter.Add(1); }
            };
            var result = f(7);
            return result + Counter.Total;
            """;

        Assert.Equal(8, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Void_block_body_needs_no_return()
    {
        const string source = """
            Fn.Each(Numbers, x => { Counter.Add(x); });
            return Counter.Total;
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Block_body_captures_by_reference()
    {
        const string source = """
            var seen = 0;
            Fn.Each(Numbers, x => { seen = seen + x; });
            return seen;
            """;

        var globals = new LambdaGlobals { Numbers = [5, 10] };
        Assert.Equal(15, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Block_body_in_linq_infers_from_its_return_statements()
    {
        // TResult can only come from the return statements inside the block.
        const string source = """
            return Numbers.Select(x => { var scaled = x * 3; return scaled; }).Sum();
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(18, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Block_predicate_in_linq()
    {
        const string source = """
            var floor = Threshold;
            return Numbers.Where(x => { return x > floor; }).Count();
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4], Threshold = 2 };
        Assert.Equal(2, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Nested_lambda_inside_a_block_body()
    {
        const string source = """
            Func<int, int> outer = a =>
            {
                Func<int, int> inner = b => a + b;
                return inner(10);
            };
            return outer(5);
            """;

        Assert.Equal(15, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Loop_inside_a_block_body_can_break_and_continue()
    {
        const string source = """
            Func<int, int> f = n =>
            {
                var total = 0;
                for (var i = 0; i < n; i++)
                {
                    if (i % 2 == 0) continue;
                    if (i > 7) break;
                    total += i;
                }
                return total;
            };
            return f(20);
            """;

        // 1 + 3 + 5 + 7
        Assert.Equal(16, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Block_body_declaring_a_captured_loop_variable()
    {
        const string source = """
            Func<int[], int> f = xs =>
            {
                foreach (var x in xs) Sink.Add(() => x);
                var total = 0;
                for (var i = 0; i < Sink.Count; i++) total = total * 10 + Sink[i]();
                return total;
            };
            return f(Numbers);
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(123, Run<LambdaGlobals, int>(source, globals));
    }
}

/// <summary>
/// Closure slot layout. Up to four captured variables get a typed layout with a field each;
/// past that the engine falls back to boxing everything into an <c>object[]</c>. The boundary
/// is invisible from the language, so these check that it stays that way.
/// </summary>
public sealed class ClosureLayoutTests : ScriptTest
{
    [Fact]
    public void One_captured_variable()
    {
        Assert.Equal(7, Eval<int>("var a = 7; var f = () => a; return f();"));
    }

    [Fact]
    public void Four_captured_variables_the_last_typed_layout()
    {
        const string source = """
            var a = 1; var b = 20; var c = 300; var d = 4000;
            var f = () => a + b + c + d;
            return f();
            """;

        Assert.Equal(4321, Eval<int>(source));
    }

    [Fact]
    public void Five_captured_variables_fall_back_to_the_boxed_layout()
    {
        const string source = """
            var a = 1; var b = 20; var c = 300; var d = 4000; var e = 50000;
            var f = () => a + b + c + d + e;
            return f();
            """;

        Assert.Equal(54321, Eval<int>(source));
    }

    [Fact]
    public void The_boxed_layout_still_captures_by_reference()
    {
        // Five slots, so this runs on the fallback. Writes on either side must be visible on the
        // other, exactly as they are with a typed layout.
        const string source = """
            var a = 1; var b = 2; var c = 3; var d = 4; var e = 5;
            var bump = () => { a = a + 10; return a + b + c + d + e; };
            var first = bump();
            e = 50;
            var second = bump();
            return first * 10000 + second * 100 + a;
            """;

        // first: a becomes 11, 11+2+3+4+5 = 25. second: a becomes 21, 21+2+3+4+50 = 80.
        Assert.Equal(258021, Eval<int>(source));
    }

    [Fact]
    public void A_typed_layout_holds_mixed_value_and_reference_slots()
    {
        // int, string, bool and decimal in one closure — four different slot types.
        const string source = """
            var count = 2;
            var label = "xy";
            var flag = true;
            var scale = 1.5m;
            var f = () => label.Length + (flag ? count * scale : 0m);
            return f();
            """;

        Assert.Equal(5.0m, Eval<decimal>(source));
    }

    [Fact]
    public void A_captured_variable_outlives_the_block_that_declared_it()
    {
        const string source = """
            Func<int> f = () => 0;
            {
                var inner = 9;
                f = () => inner;
            }
            return f();
            """;

        Assert.Equal(9, Eval<int>(source));
    }
}
