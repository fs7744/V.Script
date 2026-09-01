using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>Explicitly typed lambda parameters, and the natural delegate type they enable.</summary>
public sealed class TypedLambdaParameterTests : ScriptTest
{
    [Fact]
    public void Var_infers_a_delegate_from_written_parameter_types()
    {
        Assert.Equal(4, Eval<int>("var f = (int x) => x + 1; return f(3);"));
    }

    [Fact]
    public void Several_parameters()
    {
        Assert.Equal(6, Eval<int>("var f = (int a, int b) => a * b; return f(2, 3);"));
    }

    [Fact]
    public void No_parameters()
    {
        Assert.Equal(7, Eval<int>("var f = () => 7; return f();"));
    }

    [Fact]
    public void Return_type_comes_from_the_body()
    {
        Assert.Equal("ab", Eval<string>("var f = (string s) => s + \"b\"; return f(\"a\");"));
        Assert.Equal(2.5, Eval<double>("var f = (int x) => x / 2.0; return f(5);"), 10);
    }

    [Fact]
    public void Block_body_with_written_parameter_types()
    {
        const string source = """
            var f = (int x) => { var y = x * 2; return y + 1; };
            return f(3);
            """;

        Assert.Equal(7, Eval<int>(source));
    }

    [Fact]
    public void A_body_with_no_value_infers_an_Action()
    {
        const string source = """
            var add = (int x) => Counter.Add(x);
            add(3);
            add(4);
            return Counter.Total;
            """;

        Assert.Equal(7, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void Written_types_still_work_against_an_explicit_delegate_type()
    {
        Assert.Equal(4, Eval<int>("Func<int, int> f = (int x) => x + 1; return f(3);"));
    }

    [Fact]
    public void A_written_type_that_contradicts_the_target_is_an_error() =>
        AssertErrorIn("Func<int, int> f = (string s) => 1; return 0;", ErrorCode.CannotConvert);

    [Fact]
    public void Bare_parameters_still_need_a_target_type() =>
        AssertErrorIn("var f = x => x + 1; return 0;", ErrorCode.CannotInferType);

    [Fact]
    public void Bare_parameters_still_work_where_a_target_exists()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6, Run<LambdaGlobals, int>("return Numbers.Select(x => x).Sum();", globals));
    }

    [Fact]
    public void An_inferred_delegate_can_be_captured()
    {
        const string source = """
            var factor = 3;
            var f = (int x) => x * factor;
            factor = 4;
            return f(2);
            """;

        Assert.Equal(8, Eval<int>(source));
    }
}

/// <summary>Local functions.</summary>
public sealed class LocalFunctionTests : ScriptTest
{
    [Fact]
    public void Block_bodied_local_function()
    {
        const string source = """
            int Double(int n)
            {
                return n * 2;
            }
            return Double(21);
            """;

        Assert.Equal(42, Eval<int>(source));
    }

    [Fact]
    public void Expression_bodied_local_function()
    {
        Assert.Equal(8, Eval<int>("int Twice(int n) => n * 2; return Twice(4);"));
    }

    [Fact]
    public void Void_local_function()
    {
        const string source = """
            void Bump(int by)
            {
                Counter.Add(by);
            }
            Bump(2);
            Bump(5);
            return Counter.Total;
            """;

        Assert.Equal(7, Run<LambdaGlobals, int>(source, new LambdaGlobals()));
    }

    [Fact]
    public void No_parameters()
    {
        Assert.Equal(9, Eval<int>("int Nine() => 9; return Nine();"));
    }

    [Fact]
    public void Several_parameters()
    {
        Assert.Equal("a1", Eval<string>("string Join(string s, int n) => s + n; return Join(\"a\", 1);"));
    }

    [Fact]
    public void Recursion()
    {
        const string source = """
            int Fact(int n)
            {
                if (n <= 1) return 1;
                return n * Fact(n - 1);
            }
            return Fact(5);
            """;

        Assert.Equal(120, Eval<int>(source));
    }

    [Fact]
    public void Mutual_recursion()
    {
        const string source = """
            bool IsEven(int n) => n == 0 ? true : IsOdd(n - 1);
            bool IsOdd(int n) => n == 0 ? false : IsEven(n - 1);
            return IsEven(10);
            """;

        Assert.True(Eval<bool>(source));
    }

    [Fact]
    public void A_local_function_may_be_called_before_it_is_written()
    {
        Assert.Equal(8, Eval<int>("return Twice(4); int Twice(int n) => n * 2;"));
    }

    [Fact]
    public void A_local_function_captures_the_enclosing_scope()
    {
        const string source = """
            var offset = 10;
            int Shift(int n) => n + offset;
            offset = 20;
            return Shift(1);
            """;

        Assert.Equal(21, Eval<int>(source));
    }

    [Fact]
    public void A_local_function_can_read_globals()
    {
        const string source = """
            int Scaled(int n) => n * Threshold;
            return Scaled(3);
            """;

        Assert.Equal(12, Run<LambdaGlobals, int>(source, new LambdaGlobals { Threshold = 4 }));
    }

    [Fact]
    public void A_local_function_can_be_passed_as_a_delegate()
    {
        const string source = """
            int Twice(int n) => n * 2;
            return Numbers.Select(Twice).Sum();
            """;

        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(12, Run<LambdaGlobals, int>(source, globals));
    }

    [Fact]
    public void Local_functions_nest_inside_a_block()
    {
        const string source = """
            var total = 0;
            for (var i = 0; i < 3; i++)
            {
                int Square(int n) => n * n;
                total = total + Square(i);
            }
            return total;
            """;

        Assert.Equal(0 + 1 + 4, Eval<int>(source));
    }

    [Fact]
    public void A_local_function_inside_a_lambda()
    {
        const string source = """
            var f = (int n) =>
            {
                int Twice(int x) => x * 2;
                return Twice(n) + 1;
            };
            return f(3);
            """;

        Assert.Equal(7, Eval<int>(source));
    }

    [Fact]
    public void A_local_function_may_use_a_switch_statement()
    {
        const string source = """
            string Name(int n)
            {
                switch (n)
                {
                    case 1: return "one";
                    default: return "other";
                }
            }
            return Name(1) + Name(2);
            """;

        Assert.Equal("oneother", Eval<string>(source));
    }

    [Fact]
    public void Two_local_functions_in_the_same_block_may_not_share_a_name() =>
        AssertErrorIn("int F() => 1; int F() => 2; return F();", ErrorCode.VariableAlreadyDefined);

    [Fact]
    public void A_non_void_local_function_must_return_on_every_path() =>
        AssertErrorIn("int F(int n) { if (n > 0) return 1; } return F(1);", ErrorCode.NotAllCodePathsReturn);

    [Fact]
    public void An_unknown_parameter_type_is_reported() =>
        AssertErrorIn("int F(NoSuchType x) => 1; return 0;", ErrorCode.UnknownType);

    [Fact]
    public void Calling_with_the_wrong_argument_type_is_an_error() =>
        AssertErrorIn("int F(int n) => n; return F(\"x\");", ErrorCode.CannotConvert);

    [Fact]
    public void A_local_function_cannot_capture_a_variable_declared_after_it() =>
        AssertErrorIn("int F() => later; var later = 1; return F();", ErrorCode.UndefinedName);

    [Fact]
    public void A_local_function_outside_a_block_is_rejected() =>
        AssertErrorIn("if (true) int F() => 1; return 0;", ErrorCode.ConstructNotSupported);

    [Fact]
    public async Task A_local_function_works_in_an_async_script()
    {
        const string source = """
            int Twice(int n) => n * 2;
            var value = await Service.CompletedAsync(3);
            return Twice(value);
            """;

        Assert.Equal(8, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }
}
