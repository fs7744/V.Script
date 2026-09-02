using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary><c>ref</c> and <c>out</c> arguments.</summary>
public sealed class RefArgumentTests : ScriptTest
{
    [Fact]
    public void Out_variable_declared_inline()
    {
        Assert.Equal(1, Eval<int>("return int.TryParse(\"1\", out var n) ? n : -1;"));
    }

    [Fact]
    public void Out_variable_with_a_written_type()
    {
        Assert.Equal(1, Eval<int>("return int.TryParse(\"1\", out int n) ? n : -1;"));
    }

    [Fact]
    public void Out_to_an_existing_variable()
    {
        const string source = """
            int n = 0;
            var ok = int.TryParse("42", out n);
            return ok ? n : -1;
            """;

        Assert.Equal(42, Eval<int>(source));
    }

    [Fact]
    public void A_failed_TryParse_still_assigns()
    {
        Assert.Equal(0, Eval<int>("return int.TryParse(\"x\", out var n) ? -1 : n;"));
    }

    [Fact]
    public void An_out_variable_is_usable_after_the_call()
    {
        const string source = """
            Calc.TryHalve(9, out var half);
            return half;
            """;

        Assert.Equal(4, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Ref_argument_writes_back()
    {
        const string source = """
            var n = 1;
            Calc.Bump(ref n);
            Calc.Bump(ref n);
            return n;
            """;

        Assert.Equal(3, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Two_ref_arguments()
    {
        const string source = """
            var a = 1;
            var b = 2;
            Calc.Swap(ref a, ref b);
            return a * 10 + b;
            """;

        Assert.Equal(21, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Out_inside_a_condition()
    {
        const string source = """
            if (Calc.TryHalve(8, out var half)) return half;
            return -1;
            """;

        Assert.Equal(4, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Out_inside_a_loop_body()
    {
        const string source = """
            var total = 0;
            for (var i = 0; i < 3; i++)
            {
                Calc.TryHalve(i * 2, out var half);
                total = total + half;
            }
            return total;
            """;

        Assert.Equal(0 + 1 + 2, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Overload_resolution_still_distinguishes_by_ref_ness()
    {
        // Bump only exists as a ref overload, so calling it without ref must fail.
        AssertError<OrderGlobals, int>("var n = 1; Calc.Bump(n); return n;", ErrorCode.NoMatchingOverload);
    }

    [Fact]
    public void A_ref_argument_must_be_a_variable() =>
        AssertError<OrderGlobals, int>("Calc.Bump(ref 1); return 0;", ErrorCode.NotAssignable);

    [Fact]
    public void A_captured_variable_cannot_be_passed_by_reference()
    {
        const string source = """
            var n = 1;
            var f = () => n;
            Calc.Bump(ref n);
            return n;
            """;

        AssertError<OrderGlobals, int>(source, ErrorCode.ConstructNotSupported);
    }

    [Fact]
    public void An_out_variable_may_not_shadow_an_existing_one() =>
        AssertError<OrderGlobals, int>(
            "var half = 1; Calc.TryHalve(2, out var half); return half;",
            ErrorCode.VariableAlreadyDefined);
}

/// <summary>Method groups converted to delegates.</summary>
public sealed class MethodGroupTests : ScriptTest
{
    [Fact]
    public void A_static_method_group_infers_through_LINQ()
    {
        var globals = new LambdaGlobals { Numbers = [-1, 2, -3] };
        Assert.Equal(6, Run<LambdaGlobals, int>("return Numbers.Select(int.Abs).Sum();", globals));
    }

    [Fact]
    public void A_method_group_assigned_to_a_delegate()
    {
        Assert.Equal(3, Eval<int>("Func<int, int> f = int.Abs; return f(-3);"));
    }

    [Fact]
    public void An_instance_method_group_on_a_receiver()
    {
        const string source = """
            Func<int, int, int> add = Calc.Add;
            return add(2, 3);
            """;

        Assert.Equal(5, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void A_globals_method_group()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(12, Run<LambdaGlobals, int>("return Numbers.Select(Fn.Double).Sum();", globals));
    }

    [Fact]
    public void The_overload_is_chosen_by_the_delegate_signature()
    {
        const string source = """
            Func<double, double, double> add = Calc.Add;
            return add(1.5, 2.5);
            """;

        Assert.Equal(4.0, Run<OrderGlobals, double>(source, new OrderGlobals()), 10);
    }

    [Fact]
    public void A_method_group_that_fits_no_overload_is_an_error() =>
        AssertErrorIn("Func<string, string> f = int.Abs; return f(\"x\");", ErrorCode.NoMatchingOverload);

    [Fact]
    public void A_method_group_cannot_convert_to_a_non_delegate() =>
        AssertErrorIn("int f = int.Abs; return f;", ErrorCode.CannotConvert);
}
