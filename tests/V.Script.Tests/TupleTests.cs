using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>Tuple literals, tuple types and element names.</summary>
public sealed class TupleTests : ScriptTest
{
    [Fact]
    public void Positional_elements()
    {
        Assert.Equal(3, Eval<int>("var t = (1, 2); return t.Item1 + t.Item2;"));
    }

    [Fact]
    public void Mixed_element_types()
    {
        Assert.Equal("x1", Eval<string>("var t = (\"x\", 1); return t.Item1 + t.Item2;"));
    }

    [Fact]
    public void Named_elements()
    {
        Assert.Equal(3, Eval<int>("var t = (a: 1, b: 2); return t.a + t.b;"));
    }

    [Fact]
    public void Named_elements_keep_their_positional_names_too()
    {
        Assert.Equal(3, Eval<int>("var t = (a: 1, b: 2); return t.Item1 + t.Item2;"));
    }

    [Fact]
    public void Element_names_are_inferred_from_the_expression()
    {
        const string source = """
            var count = 5;
            var t = (count, Order.Code);
            return t.count + t.Code.Length;
            """;

        var globals = new OrderGlobals { Order = new Order { Code = "abc" } };
        Assert.Equal(8, Run<OrderGlobals, int>(source, globals));
    }

    [Fact]
    public void A_written_tuple_type_supplies_the_names()
    {
        Assert.Equal(1, Eval<int>("(int a, string b) t = (1, \"x\"); return t.a;"));
    }

    [Fact]
    public void A_written_tuple_type_renames_the_elements()
    {
        // The literal calls them x/y; the declared type calls them a/b, and that is what wins.
        Assert.Equal(2, Eval<int>("(int a, int b) t = (x: 1, y: 2); return t.b;"));
    }

    [Fact]
    public void Tuples_are_value_types()
    {
        const string source = """
            var a = (1, 2);
            var b = a;
            b.Item1 = 9;
            return a.Item1;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void Tuples_compare_by_value()
    {
        Assert.True(Eval<bool>("var a = (1, \"x\"); var b = (1, \"x\"); return a.Equals(b);"));
    }

    [Fact]
    public void Seven_elements()
    {
        const string source = """
            var t = (1, 2, 3, 4, 5, 6, 7);
            return t.Item1 + t.Item7;
            """;

        Assert.Equal(8, Eval<int>(source));
    }

    [Fact]
    public void Nested_tuples()
    {
        Assert.Equal(4, Eval<int>("var t = (1, (2, 3)); return t.Item1 + t.Item2.Item2;"));
    }

    [Fact]
    public void A_tuple_flows_through_a_lambda()
    {
        const string source = """
            var f = (int x) => (x, x * 2);
            var t = f(3);
            return t.Item1 + t.Item2;
            """;

        Assert.Equal(9, Eval<int>(source));
    }

    [Fact]
    public void A_local_function_may_return_a_tuple()
    {
        const string source = """
            (int, int) Split(int n) => (n / 2, n % 2);
            var t = Split(7);
            return t.Item1 * 10 + t.Item2;
            """;

        Assert.Equal(31, Eval<int>(source));
    }

    [Fact]
    public void A_tuple_in_a_collection()
    {
        const string source = """
            var list = new List<(int, int)>();
            list.Add((1, 2));
            list.Add((3, 4));
            return list[1].Item1;
            """;

        Assert.Equal(3, Eval<int>(source));
    }

    [Fact]
    public void An_unknown_element_name_is_an_error() =>
        AssertErrorIn("var t = (a: 1, b: 2); return t.c;", ErrorCode.UndefinedMember);

    [Fact]
    public void A_single_element_tuple_is_just_a_parenthesized_expression()
    {
        Assert.Equal(1, Eval<int>("return (1);"));
    }

    [Fact]
    public void Eight_or_more_elements_nest_into_Rest()
    {
        const string source = """
            var t = (1, 2, 3, 4, 5, 6, 7, 8, 9);
            return t.Item1 + t.Item7 + t.Item8 + t.Item9;
            """;

        Assert.Equal(1 + 7 + 8 + 9, Eval<int>(source));
    }

    [Fact]
    public void Named_elements_past_the_seventh()
    {
        const string source = """
            var t = (a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8);
            return t.a + t.h;
            """;

        Assert.Equal(9, Eval<int>(source));
    }

    [Fact]
    public void A_nested_tuple_is_still_reachable_through_Rest()
    {
        Assert.Equal(8, Eval<int>("var t = (1, 2, 3, 4, 5, 6, 7, 8); return t.Rest.Item1;"));
    }

    [Fact]
    public void An_element_without_a_type_is_reported() =>
        AssertErrorIn("var t = (1, null); return 0;", ErrorCode.CannotInferType);
}

/// <summary>Deconstruction, both the declaring form and assignment to existing variables.</summary>
public sealed class DeconstructionTests : ScriptTest
{
    [Fact]
    public void Var_form()
    {
        Assert.Equal(3, Eval<int>("var (a, b) = (1, 2); return a + b;"));
    }

    [Fact]
    public void Typed_form()
    {
        Assert.Equal("x1", Eval<string>("(string s, int n) = (\"x\", 1); return s + n;"));
    }

    [Fact]
    public void Typed_form_may_widen()
    {
        Assert.Equal(3L, Eval<long>("(long a, long b) = (1, 2); return a + b;"));
    }

    [Fact]
    public void Deconstructing_a_variable()
    {
        const string source = """
            var t = (1, "x");
            var (n, s) = t;
            return n + s;
            """;

        Assert.Equal("1x", Eval<string>(source));
    }

    [Fact]
    public void Deconstructing_a_call_result()
    {
        const string source = """
            (int, int) Split(int n) => (n / 2, n % 2);
            var (q, r) = Split(7);
            return q * 10 + r;
            """;

        Assert.Equal(31, Eval<int>(source));
    }

    [Fact]
    public void The_source_is_evaluated_once()
    {
        const string source = """
            (int, int) Next() { Calc.Counter = Calc.Counter + 1; return (Calc.Counter, 0); }
            var (a, b) = Next();
            return Calc.Counter;
            """;

        Assert.Equal(1, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Three_elements()
    {
        Assert.Equal(6, Eval<int>("var (a, b, c) = (1, 2, 3); return a + b + c;"));
    }

    [Fact]
    public void Nine_elements()
    {
        const string source = """
            var (a, b, c, d, e, f, g, h, i) = (1, 2, 3, 4, 5, 6, 7, 8, 9);
            return a + h + i;
            """;

        Assert.Equal(1 + 8 + 9, Eval<int>(source));
    }

    [Fact]
    public void Declarations_and_assignments_may_be_mixed()
    {
        const string source = """
            var existing = 0;
            (var fresh, existing) = (1, 2);
            return fresh * 10 + existing;
            """;

        Assert.Equal(12, Eval<int>(source));
    }

    [Fact]
    public void A_written_type_may_be_mixed_with_a_bare_name()
    {
        const string source = """
            var existing = 0;
            (long widened, existing) = (1, 2);
            return (int)widened * 10 + existing;
            """;

        Assert.Equal(12, Eval<int>(source));
    }

    [Fact]
    public void Deconstruction_via_a_Deconstruct_method()
    {
        const string source = """
            var (key, value) = new KeyValuePair<string, int>("a", 7);
            return key + value;
            """;

        Assert.Equal("a7", Eval<string>(source));
    }

    [Fact]
    public void Assignment_to_existing_variables()
    {
        Assert.Equal(3, Eval<int>("var a = 0; var b = 0; (a, b) = (1, 2); return a + b;"));
    }

    [Fact]
    public void Assignment_reads_the_whole_right_side_first()
    {
        const string source = """
            var a = 1;
            var b = 2;
            (a, b) = (b, a);
            return a * 10 + b;
            """;

        Assert.Equal(21, Eval<int>(source));
    }

    [Fact]
    public void Assignment_to_a_property()
    {
        const string source = """
            var n = 0;
            (Order.Code, n) = ("z", 5);
            return Order.Code + n;
            """;

        Assert.Equal("z5", Run<OrderGlobals, string>(source, new OrderGlobals()));
    }

    [Fact]
    public void Deconstructed_names_are_ordinary_variables()
    {
        const string source = """
            var (a, b) = (1, 2);
            a = a + b;
            return a;
            """;

        Assert.Equal(3, Eval<int>(source));
    }

    [Fact]
    public void Deconstruction_inside_a_loop_body()
    {
        const string source = """
            var total = 0;
            for (var i = 0; i < 3; i++)
            {
                var (a, b) = (i, i * 2);
                total = total + a + b;
            }
            return total;
            """;

        Assert.Equal(9, Eval<int>(source));
    }

    [Fact]
    public void A_deconstructed_variable_can_be_captured()
    {
        const string source = """
            var (a, b) = (2, 3);
            var f = () => a * b;
            return f();
            """;

        Assert.Equal(6, Eval<int>(source));
    }

    [Fact]
    public void Arity_mismatch_is_reported() =>
        AssertErrorIn("var (a, b, c) = (1, 2); return 0;", ErrorCode.WrongArgumentCount);

    [Fact]
    public void Deconstructing_something_that_cannot_be_is_reported() =>
        AssertErrorIn("var (a, b) = 1; return 0;", ErrorCode.UndefinedMember);

    [Fact]
    public void A_repeated_name_is_reported() =>
        AssertErrorIn("var (a, a) = (1, 2); return 0;", ErrorCode.VariableAlreadyDefined);

    [Fact]
    public async Task Deconstruction_works_in_an_async_script()
    {
        const string source = """
            var value = await Service.CompletedAsync(3);
            var (a, b) = (value, value * 2);
            return a + b;
            """;

        Assert.Equal(12, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }
}
