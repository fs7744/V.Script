using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>Positional and list patterns.</summary>
public sealed class StructuralPatternTests : ScriptTest
{
    [Fact]
    public void Positional_pattern_over_a_tuple()
    {
        Assert.Equal(3, Eval<int>("var t = (1, 2); return t is (var a, var b) ? a + b : -1;"));
    }

    [Fact]
    public void Positional_pattern_with_constants()
    {
        Assert.True(Eval<bool>("var t = (1, 2); return t is (1, 2);"));
        Assert.False(Eval<bool>("var t = (1, 2); return t is (1, 3);"));
    }

    [Fact]
    public void Positional_pattern_with_nested_patterns()
    {
        Assert.True(Eval<bool>("var t = (5, \"ab\"); return t is (> 3, { Length: 2 });"));
    }

    [Fact]
    public void Positional_pattern_via_a_Deconstruct_method()
    {
        const string source = """
            var kv = new KeyValuePair<string, int>("a", 7);
            return kv is (var key, var value) ? key + value : "no";
            """;

        Assert.Equal("a7", Eval<string>(source));
    }

    [Fact]
    public void Positional_pattern_with_a_type_test()
    {
        const string source = """
            if (Value is (int, int) (var a, var b)) return a + b;
            return -1;
            """;

        Assert.Equal(3, Run<PatternGlobals, int>(source, new PatternGlobals { Value = (1, 2) }));
        Assert.Equal(-1, Run<PatternGlobals, int>(source, new PatternGlobals { Value = "x" }));
    }

    [Fact]
    public void Positional_element_names_must_line_up() =>
        AssertErrorIn("var t = (a: 1, b: 2); return t is (b: 1, a: 2);", ErrorCode.ConstructNotSupported);

    [Fact]
    public void Positional_pattern_in_a_switch_expression()
    {
        const string source = """
            var t = (1, 2);
            return t switch
            {
                (0, _) => "zero",
                (var a, var b) => "sum " + (a + b),
            };
            """;

        Assert.Equal("sum 3", Eval<string>(source));
    }

    [Fact]
    public void List_pattern_matches_an_exact_length()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2] };
        Assert.True(Run<LambdaGlobals, bool>("return Numbers is [1, 2];", globals));
        Assert.False(Run<LambdaGlobals, bool>("return Numbers is [1, 2, 3];", globals));
    }

    [Fact]
    public void List_pattern_binds_elements()
    {
        var globals = new LambdaGlobals { Numbers = [4, 5] };
        Assert.Equal(9, Run<LambdaGlobals, int>("return Numbers is [var a, var b] ? a + b : -1;", globals));
    }

    [Fact]
    public void A_slice_makes_the_length_a_minimum()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(4, Run<LambdaGlobals, int>("return Numbers is [1, .., var last] ? last : -1;", globals));
        Assert.Equal(1, Run<LambdaGlobals, int>("return Numbers is [var first, ..] ? first : -1;", globals));
    }

    [Fact]
    public void A_slice_may_be_named()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(2, Run<LambdaGlobals, int>("return Numbers is [_, ..var rest, _] ? rest.Count : -1;", globals));
    }

    [Fact]
    public void List_patterns_work_on_a_List()
    {
        var globals = new LambdaGlobals { Values = [7, 8, 9] };
        Assert.Equal(9, Run<LambdaGlobals, int>("return Values is [_, _, var last] ? last : -1;", globals));
    }

    [Fact]
    public void An_empty_list_pattern_matches_only_an_empty_sequence()
    {
        Assert.True(Run<LambdaGlobals, bool>("return Numbers is [];", new LambdaGlobals { Numbers = [] }));
        Assert.False(Run<LambdaGlobals, bool>("return Numbers is [];", new LambdaGlobals { Numbers = [1] }));
    }

    [Fact]
    public void A_list_pattern_needs_something_indexable() =>
        AssertErrorIn("var x = 1; return x is [1];", ErrorCode.NotIndexable);

    [Fact]
    public void Two_slices_are_reported() =>
        AssertError<LambdaGlobals, bool>("return Numbers is [.., ..];", ErrorCode.ExpectedPattern);
}

/// <summary>Multi-dimensional arrays.</summary>
public sealed class MultiDimensionalArrayTests : ScriptTest
{
    [Fact]
    public void Creation_and_indexing()
    {
        const string source = """
            var a = new int[2, 3];
            a[1, 2] = 7;
            return a[1, 2];
            """;

        Assert.Equal(7, Eval<int>(source));
    }

    [Fact]
    public void Length_counts_every_element()
    {
        Assert.Equal(6, Eval<int>("var a = new int[2, 3]; return a.Length;"));
    }

    [Fact]
    public void Elements_start_at_their_default()
    {
        Assert.Equal(0, Eval<int>("var a = new int[2, 2]; return a[0, 0];"));
    }

    [Fact]
    public void A_declared_multi_dimensional_type()
    {
        const string source = """
            int[,] a = new int[2, 2];
            a[0, 1] = 3;
            return a[0, 1];
            """;

        Assert.Equal(3, Eval<int>(source));
    }

    [Fact]
    public void Three_dimensions()
    {
        Assert.Equal(24, Eval<int>("var a = new int[2, 3, 4]; return a.Length;"));
    }

    [Fact]
    public void GetLength_reports_each_dimension()
    {
        Assert.Equal(3, Eval<int>("var a = new int[2, 3]; return a.GetLength(1);"));
    }

    [Fact]
    public void Jagged_arrays_still_work()
    {
        const string source = """
            var a = new int[2][];
            a[0] = new int[] { 1, 2 };
            return a[0][1];
            """;

        Assert.Equal(2, Eval<int>(source));
    }

    [Fact]
    public void The_wrong_number_of_subscripts_is_reported() =>
        AssertErrorIn("var a = new int[2, 2]; return a[0];", ErrorCode.NotIndexable);
}

/// <summary>Index and nested initializers, and spreads in collection expressions.</summary>
public sealed class InitializerExtensionTests : ScriptTest
{
    [Fact]
    public void Index_initializer()
    {
        const string source = """
            var d = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
            return d["a"] + d["b"];
            """;

        Assert.Equal(3, Eval<int>(source));
    }

    [Fact]
    public void Nested_object_initializer_writes_into_the_existing_member()
    {
        const string source = """
            var o = new Order { Customer = new Customer(), Code = "x" };
            var p = new Order { Customer = new Customer { Name = "n" } };
            return p.Customer.Name + o.Code;
            """;

        Assert.Equal("nx", Eval<string>(source));
    }

    [Fact]
    public void Nested_initializer_on_a_member_that_already_exists()
    {
        const string source = """
            var o = new Order { Items = { new OrderItem { Price = 2, Quantity = 3 } } };
            return o.Subtotal();
            """;

        Assert.Equal(6m, Eval<decimal>(source));
    }

    [Fact]
    public void Spread_into_an_array()
    {
        var globals = new LambdaGlobals { Numbers = [2, 3] };
        Assert.Equal(4, Run<LambdaGlobals, int>("int[] a = [1, ..Numbers, 4]; return a.Length;", globals));
        Assert.Equal(10, Run<LambdaGlobals, int>("int[] a = [1, ..Numbers, 4]; return a.Sum();", globals));
    }

    [Fact]
    public void Spread_into_a_list()
    {
        var globals = new LambdaGlobals { Numbers = [2, 3] };
        Assert.Equal(3, Run<LambdaGlobals, int>("List<int> l = [..Numbers, 9]; return l.Count;", globals));
    }

    [Fact]
    public void Spread_only()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6, Run<LambdaGlobals, int>("int[] a = [..Numbers]; return a.Sum();", globals));
    }

    [Fact]
    public void A_spread_of_the_wrong_element_type_is_reported() =>
        AssertError<LambdaGlobals, int>("string[] a = [..Numbers]; return 0;", ErrorCode.CannotConvert);
}

/// <summary>Definite assignment.</summary>
public sealed class DefiniteAssignmentTests : ScriptTest
{
    [Fact]
    public void A_pattern_variable_is_not_assigned_on_the_failing_path() =>
        AssertError<PatternGlobals, int>("if (Value is int n) { } return n;", ErrorCode.UseOfUnassignedVariable);

    [Fact]
    public void A_pattern_variable_is_assigned_where_it_matched()
    {
        Assert.Equal(42, Run<PatternGlobals, int>(
            "if (Value is int n) return n; return -1;", new PatternGlobals { Value = 42 }));
    }

    [Fact]
    public void Reading_an_uninitialised_local_is_reported() =>
        AssertErrorIn("int x; return x;", ErrorCode.UseOfUnassignedVariable);

    [Fact]
    public void Assigning_on_every_branch_is_enough()
    {
        const string source = """
            int x;
            if (A > 0) x = 1;
            else x = 2;
            return x;
            """;

        Assert.Equal(1, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1 }));
    }

    [Fact]
    public void Assigning_on_only_one_branch_is_reported() =>
        AssertError<NumberGlobals, int>("int x; if (A > 0) x = 1; return x;", ErrorCode.UseOfUnassignedVariable);

    [Fact]
    public void A_branch_that_returns_does_not_have_to_assign()
    {
        const string source = """
            int x;
            if (A > 0) return -1;
            else x = 2;
            return x;
            """;

        Assert.Equal(2, Run<NumberGlobals, int>(source, new NumberGlobals { A = 0 }));
    }

    [Fact]
    public void An_out_argument_counts_as_an_assignment()
    {
        const string source = """
            int n;
            int.TryParse("5", out n);
            return n;
            """;

        Assert.Equal(5, Eval<int>(source));
    }

    [Fact]
    public void And_narrows_the_true_path()
    {
        const string source = """
            return Value is int n && n > 10 ? n : -1;
            """;

        Assert.Equal(42, Run<PatternGlobals, int>(source, new PatternGlobals { Value = 42 }));
        Assert.Equal(-1, Run<PatternGlobals, int>(source, new PatternGlobals { Value = "x" }));
    }

    [Fact]
    public void A_catch_variable_is_assigned()
    {
        const string source = """
            try { throw new InvalidOperationException("boom"); }
            catch (InvalidOperationException e) { return e.Message; }
            """;

        Assert.Equal("boom", Eval<string>(source));
    }

    [Fact]
    public void A_do_while_body_always_runs()
    {
        const string source = """
            int x;
            do { x = 1; } while (false);
            return x;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void A_while_body_may_not_run() =>
        AssertError<NumberGlobals, int>("int x; while (A > 0) { x = 1; } return x;",
            ErrorCode.UseOfUnassignedVariable);
}

/// <summary>Nullable reference annotations, which are accepted and then ignored.</summary>
public sealed class NullableAnnotationTests : ScriptTest
{
    [Fact]
    public void A_nullable_reference_annotation_compiles()
    {
        Assert.Null(Eval<string>("string? s = null; return s;"));
    }

    [Fact]
    public void The_annotation_does_not_change_the_type()
    {
        Assert.Equal(3, Eval<int>("string? s = \"abc\"; return s.Length;"));
    }

    [Fact]
    public void Nullable_value_types_still_become_Nullable_of_T()
    {
        Assert.Null(Eval<int?>("int? n = null; return n;"));
        Assert.Equal(5, Eval<int>("int? n = 5; return n.Value;"));
    }

    [Fact]
    public void A_nullable_annotation_in_a_generic_argument()
    {
        Assert.Equal(1, Eval<int>("List<string?> l = [null]; return l.Count;"));
    }
}

/// <summary>Inferring one type parameter from several arguments.</summary>
public sealed class CommonTypeInferenceTests : ScriptTest
{
    [Fact]
    public void Two_arguments_agree_on_the_wider_type()
    {
        // T is bound by both arguments; int widens to double.
        Assert.Equal(2.5, Eval<double>("return Math.Max(1, 2.5);"), 10);
    }

    [Fact]
    public void The_first_binding_no_longer_wins_outright()
    {
        var globals = new LambdaGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6.0, Run<LambdaGlobals, double>("return Numbers.Select(n => (double)n).Sum();", globals), 10);
    }
}
