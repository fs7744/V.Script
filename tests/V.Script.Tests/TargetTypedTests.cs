using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>
/// Collection expressions. Like a lambda, <c>[a, b, c]</c> has no type of its own — every test
/// here is really a test of what the conversion to the target type produces.
/// </summary>
public sealed class CollectionExpressionTests : ScriptTest
{
    [Fact]
    public void Array_target()
    {
        Assert.Equal(3, Eval<int>("int[] a = [1, 2, 3]; return a.Length;"));
        Assert.Equal(6, Eval<int>("int[] a = [1, 2, 3]; return a[0] + a[1] + a[2];"));
    }

    [Fact]
    public void Empty_collection_expression()
    {
        Assert.Equal(0, Eval<int>("int[] a = []; return a.Length;"));
        Assert.Equal(0, Eval<int>("List<int> l = []; return l.Count;"));
    }

    [Fact]
    public void Elements_convert_to_the_element_type()
    {
        Assert.Equal(3.5, Eval<double>("double[] a = [1, 2.5]; return a[0] + a[1];"), 10);
    }

    [Fact]
    public void List_target_is_built_with_Add()
    {
        Assert.Equal(3, Eval<int>("List<int> l = [1, 2, 3]; return l.Count;"));
        Assert.Equal(2, Eval<int>("List<int> l = [1, 2, 3]; return l[1];"));
    }

    [Fact]
    public void Interface_targets_are_satisfied_by_an_array()
    {
        Assert.Equal(6, Eval<int>("IEnumerable<int> e = [1, 2, 3]; return e.Sum();"));
        Assert.Equal(3, Eval<int>("IReadOnlyList<int> r = [1, 2, 3]; return r.Count;"));
        Assert.Equal(3, Eval<int>("IList<int> l = [1, 2, 3]; return l.Count;"));
    }

    [Fact]
    public void Collection_expression_as_an_argument()
    {
        Assert.Equal(6, Run<OrderGlobals, int>("return Calc.Sum([1, 2, 3]);", new OrderGlobals()));
    }

    [Fact]
    public void Collection_expression_as_a_return_value()
    {
        Assert.Equal([1, 2], Eval<int[]>("return [1, 2];"));
    }

    [Fact]
    public void Collection_expression_in_an_object_initializer()
    {
        const string source = """
            var o = new Order { Items = [new OrderItem { Price = 2, Quantity = 3 }] };
            return o.Subtotal();
            """;

        Assert.Equal(6m, Eval<decimal>(source));
    }

    [Fact]
    public void Collection_expression_assigned_to_an_existing_variable()
    {
        Assert.Equal(2, Eval<int>("int[] a = [1]; a = [1, 2]; return a.Length;"));
    }

    [Fact]
    public void Collection_expression_in_a_conditional()
    {
        Assert.Equal(2, Eval<int>("int[] a = true ? [1, 2] : [1]; return a.Length;"));
    }

    [Fact]
    public void Elements_may_be_arbitrary_expressions()
    {
        var globals = new NumberGlobals { A = 3, B = 4 };
        Assert.Equal(7, Run<NumberGlobals, int>("int[] a = [A, B]; return a[0] + a[1];", globals));
    }

    [Fact]
    public void Var_cannot_infer_a_collection_expression() =>
        AssertErrorIn("var a = [1, 2]; return 0;", ErrorCode.CannotInferType);

    [Fact]
    public void A_target_that_is_not_a_collection_is_an_error() =>
        AssertErrorIn("int a = [1, 2]; return 0;", ErrorCode.CannotConvert);

    [Fact]
    public void An_element_that_does_not_convert_is_an_error() =>
        AssertErrorIn("int[] a = [1, \"x\"]; return 0;", ErrorCode.CannotConvert);

    [Fact]
    public void An_untyped_expression_alone_in_statement_position_is_an_error()
    {
        AssertErrorIn("[1, 2]; return 0;", ErrorCode.CannotInferType);
        AssertErrorIn("default; return 0;", ErrorCode.CannotInferType);
    }
}

/// <summary><c>default</c> and <c>default(T)</c>.</summary>
public sealed class DefaultExpressionTests : ScriptTest
{
    [Fact]
    public void Explicit_default_of_a_value_type()
    {
        Assert.Equal(0, Eval<int>("return default(int);"));
        Assert.Equal(0m, Eval<decimal>("return default(decimal);"));
        Assert.False(Eval<bool>("return default(bool);"));
    }

    [Fact]
    public void Explicit_default_of_a_reference_type()
    {
        Assert.Null(Eval<string>("return default(string);"));
    }

    [Fact]
    public void Explicit_default_of_a_nullable()
    {
        Assert.Null(Eval<int?>("return default(int?);"));
    }

    [Fact]
    public void Explicit_default_of_a_struct()
    {
        Assert.Equal(0m, Eval<decimal>("return default(Money).Amount;"));
    }

    [Fact]
    public void Bare_default_takes_the_target_type()
    {
        Assert.Equal(0, Eval<int>("int x = default; return x;"));
        Assert.Null(Eval<string>("string s = default; return s;"));
        Assert.Equal(0, Eval<int>("return default;"));
    }

    [Fact]
    public void Bare_default_in_a_conditional()
    {
        var globals = new NumberGlobals { A = 5 };
        Assert.Equal(5, Run<NumberGlobals, int>("return A > 0 ? A : default;", globals));
        Assert.Equal(0, Run<NumberGlobals, int>("return A > 0 ? default : A;", globals));
    }

    [Fact]
    public void Bare_default_as_an_argument()
    {
        Assert.Equal(3, Run<OrderGlobals, int>("return Calc.Add(3, default);", new OrderGlobals()));
    }

    [Fact]
    public void Var_cannot_infer_a_bare_default() =>
        AssertErrorIn("var x = default; return 0;", ErrorCode.CannotInferType);

    [Fact]
    public void Unknown_type_in_default_is_reported() =>
        AssertErrorIn("return default(NoSuchType);", ErrorCode.UnknownType);
}

/// <summary><c>nameof</c>.</summary>
public sealed class NameOfTests : ScriptTest
{
    [Fact]
    public void Local_variable()
    {
        Assert.Equal("count", Eval<string>("var count = 1; return nameof(count);"));
    }

    [Fact]
    public void Globals_member()
    {
        Assert.Equal("A", Run<NumberGlobals, string>("return nameof(A);", new NumberGlobals()));
    }

    [Fact]
    public void Member_access_yields_the_last_identifier()
    {
        Assert.Equal("Code", Run<OrderGlobals, string>("return nameof(Order.Code);", new OrderGlobals()));
    }

    [Fact]
    public void Type_name()
    {
        Assert.Equal("Order", Eval<string>("return nameof(Order);"));
    }

    [Fact]
    public void Result_is_a_compile_time_constant()
    {
        // Folded to a literal, so it concatenates with another literal at bind time.
        Assert.Equal("A!", Run<NumberGlobals, string>("return nameof(A) + \"!\";", new NumberGlobals()));
    }

    [Fact]
    public void The_operand_is_never_evaluated()
    {
        const string source = """
            return nameof(Calc.Counter) + Calc.Counter;
            """;

        Assert.Equal("Counter0", Run<OrderGlobals, string>(source, new OrderGlobals()));
    }

    [Fact]
    public void An_unknown_name_is_an_error() =>
        AssertErrorIn("return nameof(nothingHere);", ErrorCode.UndefinedName);
}

/// <summary>throw as an expression.</summary>
public sealed class ThrowExpressionTests : ScriptTest
{
    [Fact]
    public void Null_coalescing_to_a_throw()
    {
        const string source = "return Text ?? throw new InvalidOperationException(\"missing\");";

        Assert.Equal("ok", Run<NumberGlobals, string>(source, new NumberGlobals { Text = "ok" }));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => Run<NumberGlobals, string>(source, new NumberGlobals { Text = null }));

        Assert.Equal("missing", thrown.Message);
    }

    [Fact]
    public void Conditional_branch_that_throws()
    {
        const string source = "return A > 0 ? A : throw new InvalidOperationException(\"neg\");";

        Assert.Equal(5, Run<NumberGlobals, int>(source, new NumberGlobals { A = 5 }));
        Assert.Throws<InvalidOperationException>(
            () => Run<NumberGlobals, int>(source, new NumberGlobals { A = -1 }));
    }

    [Fact]
    public void Throw_expression_in_a_lambda_body()
    {
        const string source = """
            var f = new Func<int, int>(n => n > 0 ? n : throw new InvalidOperationException("neg"));
            return f(3);
            """;

        Assert.Equal(3, Eval<int>(source));
    }

    [Fact]
    public void Throw_expression_in_a_switch_expression()
    {
        const string source = """
            return A switch
            {
                1 => "one",
                _ => throw new InvalidOperationException("no"),
            };
            """;

        Assert.Equal("one", Run<NumberGlobals, string>(source, new NumberGlobals { A = 1 }));
        Assert.Throws<InvalidOperationException>(
            () => Run<NumberGlobals, string>(source, new NumberGlobals { A = 2 }));
    }

    [Fact]
    public void Throwing_a_non_exception_is_an_error() =>
        AssertErrorIn("string s = null; return s ?? throw \"oops\";", ErrorCode.CannotConvert);

    [Fact]
    public async Task Throw_expression_in_an_async_script()
    {
        const string source = """
            var value = await Service.CompletedAsync(1);
            return value > 0 ? value : throw new InvalidOperationException("neg");
            """;

        Assert.Equal(2, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }
}
