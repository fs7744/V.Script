using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>Local constants, which fold into their uses rather than occupying a slot.</summary>
public sealed class LocalConstTests : ScriptTest
{
    [Fact]
    public void A_constant_is_usable_like_a_variable()
    {
        Assert.Equal(6, Eval<int>("const int k = 2; return k * 3;"));
    }

    [Fact]
    public void A_constant_folds_into_the_expression()
    {
        // If it were a variable this would not be a compile-time constant string concat.
        Assert.Equal("ab", Eval<string>("const string a = \"a\"; return a + \"b\";"));
    }

    [Fact]
    public void A_constant_may_be_captured_without_a_closure()
    {
        Assert.Equal(10, Eval<int>("const int k = 5; var f = () => k * 2; return f();"));
    }

    [Fact]
    public void A_non_constant_initializer_is_reported() =>
        AssertError<NumberGlobals, int>("const int k = A; return k;", ErrorCode.CannotInferType);

    [Fact]
    public void A_constant_cannot_be_assigned() =>
        AssertErrorIn("const int k = 1; k = 2; return k;", ErrorCode.NotAssignable);
}

/// <summary><c>static</c> local functions.</summary>
public sealed class StaticLocalFunctionTests : ScriptTest
{
    [Fact]
    public void A_static_local_function_runs_like_any_other()
    {
        Assert.Equal(6, Eval<int>("static int F(int x) => x * 2; return F(3);"));
    }

    [Fact]
    public void It_may_still_use_its_own_locals()
    {
        const string source = """
            static int F(int x)
            {
                var doubled = x * 2;
                return doubled + 1;
            }
            return F(3);
            """;

        Assert.Equal(7, Eval<int>(source));
    }

    [Fact]
    public void It_may_still_read_a_constant()
    {
        Assert.Equal(8, Eval<int>("const int k = 4; static int F(int x) => x * k; return F(2);"));
    }

    [Fact]
    public void Capturing_an_outer_variable_is_reported() =>
        AssertErrorIn("var n = 1; static int F() => n; return F();", ErrorCode.ConstructNotSupported);

    [Fact]
    public void A_non_static_local_function_still_captures()
    {
        Assert.Equal(1, Eval<int>("var n = 1; int F() => n; return F();"));
    }
}

/// <summary>Index-from-end and range subscripts.</summary>
public sealed class IndexAndRangeTests : ScriptTest
{
    [Fact]
    public void Index_from_the_end_of_an_array()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(3, Run<LambdaGlobals, int>("return Numbers[^1];", globals));
        Assert.Equal(2, Run<LambdaGlobals, int>("return Numbers[^2];", globals));
    }

    [Fact]
    public void Index_from_the_end_of_a_string()
    {
        var globals = new NumberGlobals { Text = "abc" };
        Assert.Equal('c', Run<NumberGlobals, char>("return Text[^1];", globals));
    }

    [Fact]
    public void An_index_may_be_computed()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3], Threshold = 2 };
        Assert.Equal(2, Run<LambdaGlobals, int>("return Numbers[^Threshold];", globals));
    }

    [Fact]
    public void Range_over_an_array_copies()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(2, Run<LambdaGlobals, int>("return Numbers[1..3].Length;", globals));
        Assert.Equal(5, Run<LambdaGlobals, int>("return Numbers[1..3].Sum();", globals));
    }

    [Fact]
    public void Open_ended_ranges()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(3, Run<LambdaGlobals, int>("return Numbers[1..].Length;", globals));
        Assert.Equal(1, Run<LambdaGlobals, int>("return Numbers[..1].Length;", globals));
        Assert.Equal(4, Run<LambdaGlobals, int>("return Numbers[..].Length;", globals));
    }

    [Fact]
    public void A_range_may_count_from_the_end()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(2, Run<LambdaGlobals, int>("return Numbers[^2..].Length;", globals));
    }

    [Fact]
    public void Range_over_a_string_slices()
    {
        var globals = new NumberGlobals { Text = "abcdef" };
        Assert.Equal("bcd", Run<NumberGlobals, string>("return Text[1..4];", globals));
    }

    [Fact]
    public void A_range_value_can_be_held_in_a_variable()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(2, Run<LambdaGlobals, int>("var r = 1..3; return Numbers[r].Length;", globals));
    }

    [Fact]
    public void The_receiver_is_evaluated_once()
    {
        const string source = """
            Calc.Counter = 0;
            var a = new int[] { 1, 2, 3 };
            return a[^1];
            """;

        Assert.Equal(3, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void Indexing_something_without_a_length_is_reported() =>
        AssertErrorIn("var d = new Dictionary<string, int>(); return d[^1];", ErrorCode.NotIndexable);
}

/// <summary><c>with</c> expressions.</summary>
public sealed class WithExpressionTests : ScriptTest
{
    [Fact]
    public void With_copies_and_replaces_one_member()
    {
        const string source = """
            var a = new Point(1, 2);
            var b = a with { X = 9 };
            return b.X * 10 + b.Y;
            """;

        Assert.Equal(92, Eval<int>(source));
    }

    [Fact]
    public void The_original_is_untouched()
    {
        const string source = """
            var a = new Point(1, 2);
            var b = a with { X = 9 };
            return a.X;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void Several_members_at_once()
    {
        const string source = """
            var a = new Point(1, 2);
            var b = a with { X = 3, Y = 4 };
            return b.X * 10 + b.Y;
            """;

        Assert.Equal(34, Eval<int>(source));
    }

    [Fact]
    public void An_empty_with_is_a_copy()
    {
        Assert.Equal(1, Eval<int>("var a = new Point(1, 2); var b = a with { }; return b.X;"));
    }

    [Fact]
    public void With_on_a_non_record_is_reported() =>
        AssertErrorIn("var o = new Order(); var p = o with { Code = \"x\" }; return 0;",
            ErrorCode.ConstructNotSupported);
}

/// <summary>Native-sized integers.</summary>
public sealed class NativeIntegerTests : ScriptTest
{
    [Fact]
    public void An_int_converts_implicitly()
    {
        Assert.Equal(1, Eval<int>("nint x = 1; return (int)x;"));
    }

    [Fact]
    public void Arithmetic_stays_native_sized()
    {
        Assert.Equal(6, Eval<int>("nint x = 2; nint y = 3; return (int)(x * y);"));
    }

    [Fact]
    public void Unsigned_native_integers()
    {
        Assert.Equal(3u, Eval<uint>("nuint x = 3; return (uint)x;"));
    }

    [Fact]
    public void Widening_to_long_is_implicit()
    {
        Assert.Equal(5L, Eval<long>("nint x = 5; return x;"));
    }
}

/// <summary>Raw interpolated strings.</summary>
public sealed class RawInterpolationTests : ScriptTest
{
    [Fact]
    public void Two_dollars_means_two_braces()
    {
        var globals = new NumberGlobals { A = 7 };
        var source = "return $$\"\"\"a{{A}}b\"\"\";";

        Assert.Equal("a7b", Run<NumberGlobals, string>(source, globals));
    }

    [Fact]
    public void A_single_brace_is_literal_text()
    {
        var globals = new NumberGlobals { A = 7 };
        var source = "return $$\"\"\"{x}={{A}}\"\"\";";

        Assert.Equal("{x}=7", Run<NumberGlobals, string>(source, globals));
    }

    [Fact]
    public void One_dollar_keeps_the_ordinary_rule()
    {
        var globals = new NumberGlobals { A = 7 };
        var source = "return $\"\"\"a{A}b\"\"\";";

        Assert.Equal("a7b", Run<NumberGlobals, string>(source, globals));
    }

    [Fact]
    public void Multi_line_raw_interpolation_is_reindented()
    {
        var globals = new NumberGlobals { A = 7 };
        var source = "return $$\"\"\"\n    a{{A}}\n    b\n    \"\"\";";

        Assert.Equal("a7\nb", Run<NumberGlobals, string>(source, globals));
    }
}

/// <summary>Conditional compilation.</summary>
public sealed class PreprocessorTests : ScriptTest
{
    private static TResult RunWith<TResult>(string source, params string[] symbols)
    {
        var options = Options.AddPreprocessorSymbols(symbols);

        using var engine = new ScriptEngine(options);
        using var script = engine.Compile<EmptyGlobals, TResult>(source);

        return script.Run(new EmptyGlobals());
    }

    [Fact]
    public void A_defined_symbol_selects_the_first_branch()
    {
        const string source = "#if FOO\nreturn 1;\n#else\nreturn 0;\n#endif";

        Assert.Equal(1, RunWith<int>(source, "FOO"));
        Assert.Equal(0, RunWith<int>(source));
    }

    [Fact]
    public void Elif_chains()
    {
        const string source = "#if A\nreturn 1;\n#elif B\nreturn 2;\n#else\nreturn 3;\n#endif";

        Assert.Equal(1, RunWith<int>(source, "A"));
        Assert.Equal(2, RunWith<int>(source, "B"));
        Assert.Equal(3, RunWith<int>(source));
        Assert.Equal(1, RunWith<int>(source, "A", "B"));
    }

    [Fact]
    public void Operators_in_the_condition()
    {
        Assert.Equal(1, RunWith<int>("#if A && !B\nreturn 1;\n#else\nreturn 0;\n#endif", "A"));
        Assert.Equal(0, RunWith<int>("#if A && !B\nreturn 1;\n#else\nreturn 0;\n#endif", "A", "B"));
        Assert.Equal(1, RunWith<int>("#if A || B\nreturn 1;\n#else\nreturn 0;\n#endif", "B"));
        Assert.Equal(1, RunWith<int>("#if (A)\nreturn 1;\n#else\nreturn 0;\n#endif", "A"));
    }

    [Fact]
    public void Excluded_code_is_not_even_parsed()
    {
        // The excluded branch is not valid C#, and that has to be fine.
        Assert.Equal(1, RunWith<int>("#if FOO\nthis is not code\n#endif\nreturn 1;"));
    }

    [Fact]
    public void Line_numbers_survive_exclusion()
    {
        var diagnostics = Errors<EmptyGlobals, int>("#if FOO\n#endif\nreturn nothingHere;");
        Assert.Contains(diagnostics, d => d.Position.Line == 3);
    }

    [Fact]
    public void A_missing_endif_is_reported() =>
        AssertErrorIn("#if FOO\nreturn 1;", ErrorCode.UnexpectedToken);
}

/// <summary>Query expressions, which are rewritten into the calls they stand for.</summary>
public sealed class QueryExpressionTests : ScriptTest
{
    private static readonly LambdaGlobals Data = new() { Numbers = [3, 1, 2, 4] };

    [Fact]
    public void Where_and_select()
    {
        Assert.Equal(2, Run<LambdaGlobals, int>(
            "return (from n in Numbers where n > 2 select n).Count();", Data));
    }

    [Fact]
    public void Select_projects()
    {
        Assert.Equal(20, Run<LambdaGlobals, int>(
            "return (from n in Numbers select n * 2).Sum();", Data));
    }

    [Fact]
    public void A_trivial_select_is_the_identity()
    {
        Assert.Equal(10, Run<LambdaGlobals, int>("return (from n in Numbers select n).Sum();", Data));
    }

    [Fact]
    public void Orderby_ascending_and_descending()
    {
        Assert.Equal(1, Run<LambdaGlobals, int>(
            "return (from n in Numbers orderby n select n).First();", Data));

        Assert.Equal(4, Run<LambdaGlobals, int>(
            "return (from n in Numbers orderby n descending select n).First();", Data));
    }

    [Fact]
    public void Orderby_with_several_keys()
    {
        const string source = """
            return (from n in Numbers orderby n % 2, n descending select n).First();
            """;

        Assert.Equal(4, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void Let_introduces_a_second_range_variable()
    {
        const string source = """
            return (from n in Numbers let d = n * 2 where d > 4 select d).Sum();
            """;

        Assert.Equal(6 + 8, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void Let_twice()
    {
        const string source = """
            return (from n in Numbers let a = n + 1 let b = a * 2 select n + a + b).Sum();
            """;

        // n + (n+1) + 2(n+1) over 3,1,2,4
        Assert.Equal(10 + 14 + 28, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void A_second_from_is_a_cross_product()
    {
        const string source = """
            return (from a in Numbers from b in Numbers select a * b).Count();
            """;

        Assert.Equal(16, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void A_second_from_can_use_the_first_variable()
    {
        const string source = """
            var pairs = from a in Numbers from b in Numbers where b > a select a + b;
            return pairs.Count();
            """;

        Assert.Equal(6, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void Group_by()
    {
        Assert.Equal(2, Run<LambdaGlobals, int>(
            "return (from n in Numbers group n by n % 2).Count();", Data));
    }

    [Fact]
    public void Group_by_with_an_element_selector()
    {
        const string source = """
            return (from n in Numbers group n * 10 by n % 2 into g select g.Sum()).Sum();
            """;

        Assert.Equal(100, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void Into_continues_the_query()
    {
        const string source = """
            return (from n in Numbers group n by n % 2 into g orderby g.Key select g.Key).First();
            """;

        Assert.Equal(0, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void Join_pairs_matching_elements()
    {
        const string source = """
            return (from a in Numbers join b in Numbers on a equals b select a).Count();
            """;

        Assert.Equal(4, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void Join_into_groups_the_matches()
    {
        const string source = """
            return (from a in Numbers
                    join b in Numbers on a % 2 equals b % 2 into matches
                    select matches.Count()).Sum();
            """;

        Assert.Equal(8, Run<LambdaGlobals, int>(source, Data));
    }

    [Fact]
    public void A_query_over_a_typed_range_variable()
    {
        var globals = new OrderGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6, Run<OrderGlobals, int>(
            "return (from int n in Numbers select n).Sum();", globals));
    }

    [Fact]
    public void From_is_still_usable_as_a_name()
    {
        Assert.Equal(3, Eval<int>("var from = 3; return from;"));
    }

    [Fact]
    public void A_query_without_select_is_reported() =>
        AssertError<LambdaGlobals, int>("return (from n in Numbers where n > 1).Count();",
            ErrorCode.ExpectedToken);
}
