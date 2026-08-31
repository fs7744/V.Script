using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>
/// LINQ is the meeting point of three features: lambdas, generic type inference and extension
/// method lookup. These tests exist to pin that combination, not the BCL operators themselves.
/// </summary>
public sealed class LinqTests : ScriptTest
{
    private static OrderGlobals Orders(params (decimal Price, int Quantity)[] items) => new()
    {
        Order = new Order
        {
            Items = { },
            Customer = new Customer { Name = "amy" },
        },
        Numbers = [1, 2, 3, 4, 5],
        Names = ["alpha", "beta", "gamma"],
        TaxRate = 0.1m,
    };

    [Fact]
    public void Where_and_count_over_an_array()
    {
        Assert.Equal(3, Run<OrderGlobals, int>("Numbers.Where(x => x > 2).Count()", Orders()));
    }

    [Fact]
    public void Sum_infers_the_element_type()
    {
        Assert.Equal(15, Run<OrderGlobals, int>("Numbers.Sum()", Orders()));
    }

    [Fact]
    public void Sum_with_a_selector()
    {
        Assert.Equal(30, Run<OrderGlobals, int>("Numbers.Sum(x => x * 2)", Orders()));
    }

    [Fact]
    public void Select_infers_the_result_type_from_the_lambda_body()
    {
        // TSource comes from the sequence; TResult can only come from binding the lambda body.
        Assert.Equal(30, Run<OrderGlobals, int>("Numbers.Select(x => x * 2).Sum()", Orders()));
    }

    [Fact]
    public void Select_can_change_the_element_type()
    {
        Assert.Equal(5, Run<OrderGlobals, int>("Numbers.Select(x => x.ToString()).Count()", Orders()));
    }

    [Fact]
    public void Chained_operators()
    {
        Assert.Equal(18, Run<OrderGlobals, int>(
            "Numbers.Where(x => x % 2 == 0).Select(x => x * 3).Sum()", Orders()));
    }

    [Fact]
    public void Any_and_all()
    {
        Assert.True(Run<OrderGlobals, bool>("Numbers.Any(x => x > 4)", Orders()));
        Assert.False(Run<OrderGlobals, bool>("Numbers.All(x => x > 4)", Orders()));
    }

    [Fact]
    public void First_and_first_or_default()
    {
        Assert.Equal(3, Run<OrderGlobals, int>("Numbers.First(x => x > 2)", Orders()));
        Assert.Equal(0, Run<OrderGlobals, int>("Numbers.FirstOrDefault(x => x > 99)", Orders()));
    }

    [Fact]
    public void Ordering_and_taking()
    {
        Assert.Equal(9, Run<OrderGlobals, int>(
            "Numbers.OrderByDescending(x => x).Take(2).Sum()", Orders()));
    }

    [Fact]
    public void Operators_over_a_generic_list()
    {
        Assert.Equal(2, Run<OrderGlobals, int>(
            "Names.Where(n => n.Length > 4).Count()", Orders()));
    }

    [Fact]
    public void Max_and_min()
    {
        Assert.Equal(5, Run<OrderGlobals, int>("Numbers.Max()", Orders()));
        Assert.Equal(1, Run<OrderGlobals, int>("Numbers.Min()", Orders()));
    }

    [Fact]
    public void Aggregate_with_a_two_parameter_lambda()
    {
        Assert.Equal(120, Run<OrderGlobals, int>(
            "Numbers.Aggregate(1, (acc, x) => acc * x)", Orders()));
    }

    [Fact]
    public void Captured_variable_inside_a_linq_lambda()
    {
        const string source = """
            var floor = 3;
            return Numbers.Where(x => x >= floor).Sum();
            """;

        Assert.Equal(12, Run<OrderGlobals, int>(source, Orders()));
    }

    [Fact]
    public void Linq_over_a_projection_of_model_objects()
    {
        var globals = new OrderGlobals
        {
            Order = new Order
            {
                Items =
                {
                    new OrderItem { Price = 10m, Quantity = 2, Sku = "a" },
                    new OrderItem { Price = 5m, Quantity = 3, Sku = "b" },
                },
            },
        };

        Assert.Equal(35m, Run<OrderGlobals, decimal>(
            "Order.Items.Sum(i => i.Price * i.Quantity)", globals));

        Assert.Equal(1, Run<OrderGlobals, int>(
            "Order.Items.Where(i => i.Quantity > 2).Count()", globals));
    }

    [Fact]
    public void ToList_materialises_a_projection()
    {
        Assert.Equal(3, Run<OrderGlobals, int>(
            "Numbers.Where(x => x > 2).ToList().Count", Orders()));
    }

    [Fact]
    public void Contains_and_distinct()
    {
        Assert.True(Run<OrderGlobals, bool>("Numbers.Contains(3)", Orders()));
        Assert.Equal(5, Run<OrderGlobals, int>("Numbers.Distinct().Count()", Orders()));
    }

    [Fact]
    public void Inference_failure_is_reported_as_such()
    {
        // Array.Empty<T>() has nothing to infer from.
        AssertError<OrderGlobals, int>(
            "Array.Empty().Length", ErrorCode.GenericMethodInferenceNotSupported);
    }

    [Fact]
    public void Extension_lookup_does_not_shadow_a_real_instance_method()
    {
        // List<T>.Count is a property and Enumerable.Count() an extension; both must work.
        Assert.Equal(3, Run<OrderGlobals, int>("Names.Count", Orders()));
        Assert.Equal(3, Run<OrderGlobals, int>("Names.Count()", Orders()));
    }

    [Fact]
    public void Unknown_method_is_still_reported_as_unknown()
    {
        AssertError<OrderGlobals, int>("Numbers.NoSuchOperator()", ErrorCode.UndefinedMember);
    }
}
