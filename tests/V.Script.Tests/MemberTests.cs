namespace V.Script.Tests;

public sealed class MemberAccessTests : ScriptTest
{
    [Fact]
    public void Instance_property_chain()
    {
        var globals = new OrderGlobals
        {
            Order = new Order { Customer = new Customer { Name = "amy" } },
        };

        Assert.Equal("amy", Run<OrderGlobals, string>("Order.Customer.Name", globals));
    }

    [Fact]
    public void Property_assignment_writes_through()
    {
        var globals = new OrderGlobals { Order = new Order() };
        Run<OrderGlobals, int>("Order.Code = \"set\"; return 0;", globals);
        Assert.Equal("set", globals.Order.Code);
    }

    [Fact]
    public void Compound_assignment_on_a_property()
    {
        var globals = new OrderGlobals { Order = new Order { Count = 5 } };
        Assert.Equal(8, Run<OrderGlobals, int>("Order.Count += 3; return Order.Count;", globals));
        Assert.Equal(8, globals.Order.Count);
    }

    [Fact]
    public void Increment_on_a_property()
    {
        var globals = new OrderGlobals { Order = new Order { Count = 5 } };
        Assert.Equal(6, Run<OrderGlobals, int>("Order.Count++; return Order.Count;", globals));
    }

    [Fact]
    public void Postfix_increment_yields_the_old_value()
    {
        Assert.Equal(5, Eval<int>("var i = 5; var j = i++; return j;"));
        Assert.Equal(6, Eval<int>("var i = 5; var j = i++; return i;"));
    }

    [Fact]
    public void Prefix_increment_yields_the_new_value()
    {
        Assert.Equal(6, Eval<int>("var i = 5; var j = ++i; return j;"));
    }

    [Fact]
    public void Static_field_and_method_resolve_through_the_type_name()
    {
        var globals = new OrderGlobals();
        Assert.Equal(42, Run<OrderGlobals, int>("Calculator.Magic", globals));
        Assert.Equal(10, Run<OrderGlobals, int>("Calculator.Doubled(5)", globals));
    }

    [Fact]
    public void Bcl_static_members_resolve()
    {
        Assert.Equal(int.MaxValue, Eval<int>("int.MaxValue"));
        Assert.Equal(4, Eval<double>("Math.Sqrt(16.0)"), 10);
        Assert.Equal("a,b", Eval<string>("string.Join(\",\", \"a\", \"b\")"));
    }

    [Fact]
    public void Calling_a_static_member_through_an_instance_is_rejected()
    {
        AssertError<OrderGlobals, int>("Calc.Doubled(5)", Diagnostics.ErrorCode.MemberIsStatic);
    }

    [Fact]
    public void Read_only_property_cannot_be_assigned()
    {
        AssertError<OrderGlobals, int>("Calc.ReadOnlyValue = 1; return 0;", Diagnostics.ErrorCode.NotAssignable);
    }

    [Fact]
    public void Unknown_member_reports_a_suggestion()
    {
        var diagnostics = Errors<OrderGlobals, int>("Order.Cont");
        var message = diagnostics[0].Message;
        Assert.Contains("Count", message);
    }

    [Fact]
    public void Methods_declared_on_globals_are_callable_by_bare_name()
    {
        var globals = new OrderGlobals
        {
            Order = new Order { Items = { new OrderItem { Price = 2m, Quantity = 3 } } },
        };

        Assert.Equal(6m, Run<OrderGlobals, decimal>("Order.Subtotal()", globals));
    }

    [Fact]
    public void Struct_receiver_methods_use_an_address()
    {
        var globals = new OrderGlobals { Wallet = new Money(12.5m) };
        Assert.Equal("12.5", Run<OrderGlobals, string>("Wallet.ToString()", globals));
    }

    [Fact]
    public void Value_type_boxes_when_converted_to_object()
    {
        Assert.Equal("5", Eval<string>("object o = 5; return o.ToString();"));
    }
}

public sealed class IndexerTests : ScriptTest
{
    [Fact]
    public void Array_element_read()
    {
        var globals = new OrderGlobals { Numbers = [10, 20, 30] };
        Assert.Equal(20, Run<OrderGlobals, int>("Numbers[1]", globals));
    }

    [Fact]
    public void Array_element_write()
    {
        var globals = new OrderGlobals { Numbers = [10, 20, 30] };
        Run<OrderGlobals, int>("Numbers[1] = 99; return 0;", globals);
        Assert.Equal(99, globals.Numbers[1]);
    }

    [Fact]
    public void Array_compound_assignment()
    {
        var globals = new OrderGlobals { Numbers = [10, 20, 30] };
        Assert.Equal(25, Run<OrderGlobals, int>("Numbers[1] += 5; return Numbers[1];", globals));
    }

    [Fact]
    public void List_indexer_read_and_write()
    {
        var globals = new OrderGlobals { Names = ["a", "b"] };
        Assert.Equal("b", Run<OrderGlobals, string>("Names[1]", globals));

        Run<OrderGlobals, int>("Names[0] = \"z\"; return 0;", globals);
        Assert.Equal("z", globals.Names[0]);
    }

    [Fact]
    public void Dictionary_indexer_resolves_the_string_overload()
    {
        var globals = new OrderGlobals { Lookup = new Dictionary<string, int> { ["k"] = 7 } };
        Assert.Equal(7, Run<OrderGlobals, int>("Lookup[\"k\"]", globals));
    }

    [Fact]
    public void Overloaded_indexers_select_by_argument_type()
    {
        var globals = new OrderGlobals();
        Assert.Equal(30, Run<OrderGlobals, int>("Calc[3]", globals));
        Assert.Equal("AB", Run<OrderGlobals, string>("Calc[\"ab\"]", globals));
    }

    [Fact]
    public void Indexing_a_non_indexable_type_is_rejected()
    {
        AssertError<NumberGlobals, int>("A[0]", Diagnostics.ErrorCode.NotIndexable);
    }

    [Fact]
    public void Out_of_range_index_throws_at_run_time()
    {
        var globals = new OrderGlobals { Numbers = [1] };
        Assert.Throws<IndexOutOfRangeException>(() => Run<OrderGlobals, int>("Numbers[5]", globals));
    }
}

public sealed class OverloadResolutionTests : ScriptTest
{
    [Fact]
    public void Exact_match_beats_a_widening_conversion()
    {
        var globals = new OrderGlobals();
        Assert.Equal(3, Run<OrderGlobals, int>("Calc.Add(1, 2)", globals));
    }

    [Fact]
    public void Double_arguments_select_the_double_overload()
    {
        var globals = new OrderGlobals();
        Assert.Equal(3.5, Run<OrderGlobals, double>("Calc.Add(1.0, 2.5)", globals), 10);
    }

    [Fact]
    public void Decimal_arguments_select_the_decimal_overload()
    {
        var globals = new OrderGlobals();
        Assert.Equal(3.5m, Run<OrderGlobals, decimal>("Calc.Add(1.0m, 2.5m)", globals));
    }

    [Fact]
    public void Mixed_int_and_long_selects_the_long_overload()
    {
        var globals = new OrderGlobals();
        Assert.Equal(3L, Run<OrderGlobals, long>("Calc.Add(1, 2L)", globals));
    }

    [Fact]
    public void Params_expansion_collects_trailing_arguments()
    {
        var globals = new OrderGlobals();
        Assert.Equal(6, Run<OrderGlobals, int>("Calc.Sum(1, 2, 3)", globals));
        Assert.Equal(0, Run<OrderGlobals, int>("Calc.Sum()", globals));
    }

    [Fact]
    public void Params_accepts_an_array_directly()
    {
        var globals = new OrderGlobals { Numbers = [4, 5] };
        Assert.Equal(9, Run<OrderGlobals, int>("Calc.Sum(Numbers)", globals));
    }

    [Fact]
    public void Optional_parameters_are_filled_in()
    {
        var globals = new OrderGlobals();
        Assert.Equal("a:1!", Run<OrderGlobals, string>("Calc.Describe(\"a\")", globals));
        Assert.Equal("a:5!", Run<OrderGlobals, string>("Calc.Describe(\"a\", 5)", globals));
        Assert.Equal("a:5?", Run<OrderGlobals, string>("Calc.Describe(\"a\", 5, \"?\")", globals));
    }

    [Fact]
    public void Named_arguments_map_to_the_right_parameter()
    {
        var globals = new OrderGlobals();
        Assert.Equal("a:1?", Run<OrderGlobals, string>("Calc.Describe(\"a\", suffix: \"?\")", globals));
        Assert.Equal("b:2!", Run<OrderGlobals, string>("Calc.Describe(count: 2, label: \"b\")", globals));
    }

    [Fact]
    public void Genuinely_ambiguous_call_is_reported()
    {
        AssertError<OrderGlobals, int>("Calc.Ambiguous(1, 2)", Diagnostics.ErrorCode.AmbiguousOverload);
    }

    [Fact]
    public void No_applicable_overload_lists_the_candidates()
    {
        var diagnostics = Errors<OrderGlobals, int>("Calc.Add(\"a\", \"b\")");
        Assert.Contains("候选", diagnostics[0].Message);
    }

    [Fact]
    public void Unknown_method_is_reported()
    {
        AssertError<OrderGlobals, int>("Calc.Nope()", Diagnostics.ErrorCode.UndefinedMember);
    }

    [Fact]
    public void Constructor_overloads_resolve()
    {
        Assert.Equal("abc", Eval<string>("new string('a', 3)").Replace("aaa", "abc"));
        Assert.Equal(3, Eval<int>("new string('a', 3).Length"));
    }

    [Fact]
    public void Object_creation_for_a_value_type_without_arguments()
    {
        Assert.Equal(0m, Eval<decimal>("new decimal()"));
    }
}
