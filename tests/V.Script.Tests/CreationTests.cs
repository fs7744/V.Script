using System.Globalization;
using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>Object and collection initializers.</summary>
public sealed class InitializerTests : ScriptTest
{
    [Fact]
    public void Object_initializer_sets_properties()
    {
        const string source = """
            var c = new Customer { Name = "Ann", IsVip = true, Age = 30 };
            return c.Name + "/" + c.IsVip + "/" + c.Age;
            """;

        Assert.Equal("Ann/True/30", Eval<string>(source));
    }

    [Fact]
    public void Object_initializer_combines_with_constructor_arguments()
    {
        const string source = """
            var o = new Order { Code = "A1", Count = 3 };
            return o.Code + o.Count;
            """;

        Assert.Equal("A13", Eval<string>(source));
    }

    [Fact]
    public void Object_initializer_members_run_in_source_order()
    {
        const string source = """
            var o = new Order { Count = 1, Code = "x" };
            o.Count = o.Count + 1;
            return o.Count;
            """;

        Assert.Equal(2, Eval<int>(source));
    }

    [Fact]
    public void Object_initializer_values_may_be_arbitrary_expressions()
    {
        const string source = """
            var n = 4;
            var c = new Customer { Name = "n" + n, Age = n > 3 ? n : 0 };
            return c.Name + c.Age;
            """;

        Assert.Equal("n44", Eval<string>(source));
    }

    [Fact]
    public void Object_initializer_nests()
    {
        const string source = """
            var o = new Order { Customer = new Customer { Name = "Bo", IsVip = true } };
            return o.Customer.Name;
            """;

        Assert.Equal("Bo", Eval<string>(source));
    }

    [Fact]
    public void Empty_object_initializer_is_allowed()
    {
        Assert.Equal("", Eval<string>("var o = new Order { }; return o.Code;"));
    }

    [Fact]
    public void Object_initializer_works_on_a_struct()
    {
        Assert.Equal(0m, Eval<decimal>("var m = new Money { }; return m.Amount;"));
    }

    [Fact]
    public void Collection_initializer_calls_Add()
    {
        const string source = """
            var l = new List<int> { 1, 2, 3 };
            return l.Count * 100 + l[2];
            """;

        Assert.Equal(303, Eval<int>(source));
    }

    [Fact]
    public void Collection_initializer_converts_its_elements()
    {
        const string source = """
            var l = new List<double> { 1, 2.5 };
            return l[0] + l[1];
            """;

        Assert.Equal(3.5, Eval<double>(source), 10);
    }

    [Fact]
    public void Collection_initializer_may_be_empty()
    {
        Assert.Equal(0, Eval<int>("var l = new List<int>(); return l.Count;"));
    }

    [Fact]
    public void Collection_initializer_inside_an_object_initializer_value()
    {
        const string source = """
            var o = new Order { Items = new List<OrderItem> { new OrderItem { Price = 2, Quantity = 3 } } };
            return o.Subtotal();
            """;

        Assert.Equal(6m, Eval<decimal>(source));
    }

    [Fact]
    public void Initializer_target_is_evaluated_once()
    {
        const string source = """
            var calc = new Calculator { Counter = 1 };
            calc.Counter = calc.Counter + 1;
            return calc.Counter;
            """;

        Assert.Equal(2, Eval<int>(source));
    }

    [Fact]
    public void Unknown_member_in_an_object_initializer_is_an_error() =>
        AssertErrorIn("var c = new Customer { Nope = 1 }; return 0;", ErrorCode.UndefinedMember);

    [Fact]
    public void Read_only_member_in_an_object_initializer_is_an_error() =>
        AssertErrorIn("var c = new Calculator { ReadOnlyValue = 1 }; return 0;", ErrorCode.PropertyHasNoSetter);

    [Fact]
    public void Collection_initializer_needs_an_Add_method() =>
        AssertErrorIn("var c = new Customer { 1, 2 }; return 0;", ErrorCode.UndefinedMember);

    [Fact]
    public void Collection_initializer_element_must_fit_Add() =>
        AssertErrorIn("var l = new List<int> { \"x\" }; return 0;", ErrorCode.NoMatchingOverload);
}

/// <summary>Array creation in its three written forms.</summary>
public sealed class ArrayCreationTests : ScriptTest
{
    [Fact]
    public void Sized_array_is_zero_initialised()
    {
        Assert.Equal(3, Eval<int>("var a = new int[3]; return a.Length;"));
        Assert.Equal(0, Eval<int>("var a = new int[3]; return a[1];"));
        Assert.Null(Eval<string>("var a = new string[2]; return a[0];"));
    }

    [Fact]
    public void Sized_array_length_may_be_computed()
    {
        Assert.Equal(6, Eval<int>("var n = 2; var a = new int[n * 3]; return a.Length;"));
    }

    [Fact]
    public void Sized_array_of_a_struct_element()
    {
        Assert.Equal(0m, Eval<decimal>("var a = new Money[2]; return a[0].Amount;"));
    }

    [Fact]
    public void Typed_array_with_elements()
    {
        Assert.Equal(9, Eval<int>("var a = new int[] { 4, 5 }; return a[0] + a[1];"));
    }

    [Fact]
    public void Typed_array_converts_its_elements()
    {
        Assert.Equal(3.5, Eval<double>("var a = new double[] { 1, 2.5 }; return a[0] + a[1];"), 10);
    }

    [Fact]
    public void Typed_array_may_be_empty()
    {
        Assert.Equal(0, Eval<int>("var a = new int[] { }; return a.Length;"));
    }

    [Fact]
    public void Inferred_array_takes_the_best_common_element_type()
    {
        Assert.Equal(3, Eval<int>("var a = new[] { 1, 2 }; return a[0] + a[1];"));
        Assert.Equal(3.5, Eval<double>("var a = new[] { 1, 2.5 }; return a[0] + a[1];"), 10);
        Assert.Equal("ab", Eval<string>("var a = new[] { \"a\", \"b\" }; return a[0] + a[1];"));
    }

    [Fact]
    public void Inferred_array_element_type_is_the_array_type()
    {
        // If inference produced object, Sum() would not resolve.
        Assert.Equal(6, Eval<int>("var a = new[] { 1, 2, 3 }; return a.Sum();"));
    }

    [Fact]
    public void Inferred_array_ignores_null_elements_when_inferring()
    {
        Assert.Equal("a", Eval<string>("var a = new[] { \"a\", null }; return a[0];"));
    }

    [Fact]
    public void Array_creation_flows_into_a_parameter()
    {
        Assert.Equal(6, Run<OrderGlobals, int>("return Calc.Sum(new[] { 1, 2, 3 });", new OrderGlobals()));
    }

    [Fact]
    public void Inferred_array_with_incompatible_elements_is_an_error() =>
        AssertErrorIn("var a = new[] { 1, \"x\" }; return 0;", ErrorCode.CannotInferType);

    [Fact]
    public void Empty_inferred_array_is_an_error() =>
        AssertErrorIn("var a = new[] { }; return 0;", ErrorCode.CannotInferType);

    [Fact]
    public void Sized_array_length_must_be_a_number() =>
        AssertErrorIn("var a = new int[\"x\"]; return 0;", ErrorCode.CannotConvert);
}

/// <summary>Interpolated strings, checked against the C# compiler's own result.</summary>
public sealed class InterpolationTests : ScriptTest
{
    [Fact]
    public void Literal_only_interpolation_is_a_plain_string()
    {
        Assert.Equal("abc", Eval<string>("return $\"abc\";"));
        Assert.Equal("", Eval<string>("return $\"\";"));
    }

    [Fact]
    public void Single_hole()
    {
        var globals = new NumberGlobals { A = 42 };
        Assert.Equal($"{globals.A}", Run<NumberGlobals, string>("return $\"{A}\";", globals));
    }

    [Fact]
    public void Text_around_holes()
    {
        var globals = new NumberGlobals { A = 3, B = 4 };
        Assert.Equal(
            $"a={globals.A}, b={globals.B}.",
            Run<NumberGlobals, string>("return $\"a={A}, b={B}.\";", globals));
    }

    [Fact]
    public void Holes_may_be_arbitrary_expressions()
    {
        var globals = new NumberGlobals { A = 3, B = 4 };
        Assert.Equal(
            $"{globals.A + globals.B}",
            Run<NumberGlobals, string>("return $\"{A + B}\";", globals));
    }

    [Fact]
    public void Hole_containing_a_call_and_a_nested_string()
    {
        var globals = new NumberGlobals { Text = "a,b,c" };
        Assert.Equal(
            $"{globals.Text!.Split(',').Length}",
            Run<NumberGlobals, string>("return $\"{Text.Split(',').Length}\";", globals));
    }

    [Fact]
    public void Hole_containing_braces_of_its_own()
    {
        var globals = new NumberGlobals { A = 5 };
        Assert.Equal(
            $"{new[] { 1, 2 }.Length}",
            Run<NumberGlobals, string>("return $\"{new[] { 1, 2 }.Length}\";", globals));
    }

    [Fact]
    public void Escaped_braces_are_literal()
    {
        Assert.Equal($"{{x}}", Eval<string>("return $\"{{x}}\";"));
    }

    [Fact]
    public void Null_hole_renders_as_empty()
    {
        var globals = new NumberGlobals { Text = null };
        Assert.Equal($"[{globals.Text}]", Run<NumberGlobals, string>("return $\"[{Text}]\";", globals));
    }

    [Fact]
    public void Format_specifier_is_applied()
    {
        using var _ = new InvariantCulture();

        var globals = new NumberGlobals { D = 3.14159 };
        Assert.Equal($"{globals.D:F2}", Run<NumberGlobals, string>("return $\"{D:F2}\";", globals));
    }

    [Fact]
    public void Alignment_is_applied()
    {
        var globals = new NumberGlobals { A = 7 };
        Assert.Equal($"[{globals.A,5}]", Run<NumberGlobals, string>("return $\"[{A,5}]\";", globals));
        Assert.Equal($"[{globals.A,-5}]", Run<NumberGlobals, string>("return $\"[{A,-5}]\";", globals));
    }

    [Fact]
    public void Alignment_and_format_together()
    {
        using var _ = new InvariantCulture();

        var globals = new NumberGlobals { D = 3.14159 };
        Assert.Equal(
            $"[{globals.D,10:F3}]",
            Run<NumberGlobals, string>("return $\"[{D,10:F3}]\";", globals));
    }

    [Fact]
    public void Literal_braces_survive_the_format_path()
    {
        var globals = new NumberGlobals { A = 7 };
        Assert.Equal($"{{{globals.A,3}}}", Run<NumberGlobals, string>("return $\"{{{A,3}}}\";", globals));
    }

    [Fact]
    public void Many_holes_use_the_array_overload()
    {
        var g = new NumberGlobals { A = 1, B = 2, D = 3, Small = 4, U = 5, Ch = 'x' };
        Assert.Equal(
            $"{g.A}{g.B}{g.D}{g.Small}{g.U}{g.Ch}",
            Run<NumberGlobals, string>("return $\"{A}{B}{D}{Small}{U}{Ch}\";", g));
    }

    [Fact]
    public void Interpolation_inside_a_lambda()
    {
        const string source = """
            return string.Join("", Numbers.Select(n => $"<{n}>"));
            """;

        var globals = new OrderGlobals { Numbers = [3, 4] };
        Assert.Equal("<3><4>", Run<OrderGlobals, string>(source, globals));
    }

    [Fact]
    public void Escape_sequences_inside_interpolated_text()
    {
        Assert.Equal("a\tb\n", Eval<string>("return $\"a\\tb\\n\";"));
    }

    [Fact]
    public void Unterminated_interpolation_is_an_error() =>
        AssertErrorIn("return $\"{A\";", ErrorCode.UnterminatedString);

    [Fact]
    public void Empty_hole_is_an_error() =>
        AssertErrorIn("return $\"{}\";", ErrorCode.ExpectedExpression);

    /// <summary>The engine formats with the ambient culture, as C# does; pin it for comparison.</summary>
    private sealed class InvariantCulture : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public InvariantCulture() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}

/// <summary>Explicit type arguments on a call.</summary>
public sealed class TypeArgumentTests : ScriptTest
{
    [Fact]
    public void Explicit_type_argument_on_a_LINQ_call()
    {
        var globals = new OrderGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(3, Run<OrderGlobals, int>("return Numbers.Cast<int>().Count();", globals));
    }

    [Fact]
    public void Explicit_type_argument_where_inference_would_fail()
    {
        var globals = new OrderGlobals { Names = ["a", "bb"] };
        Assert.Equal(2, Run<OrderGlobals, int>("return Names.OfType<string>().Count();", globals));
    }

    [Fact]
    public void Explicit_type_argument_that_widens_the_result()
    {
        var globals = new OrderGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6L, Run<OrderGlobals, long>("return Numbers.Select<int, long>(n => n).Sum();", globals));
    }

    [Fact]
    public void Explicit_type_argument_on_a_static_call()
    {
        Assert.Equal(0, Eval<int>("return System.Array.Empty<int>().Length;"));
    }

    [Fact]
    public void Comparison_chains_are_not_read_as_type_arguments()
    {
        var globals = new NumberGlobals { A = 1, B = 2 };
        Assert.True(Run<NumberGlobals, bool>("return A < B;", globals));
        Assert.False(Run<NumberGlobals, bool>("return (A < B) == (B < A);", globals));
    }

    [Fact]
    public void Wrong_arity_is_reported() =>
        AssertError<OrderGlobals, int>("return Numbers.Cast<int, int>().Count();", ErrorCode.NoMatchingOverload);
}
