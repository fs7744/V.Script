namespace V.Script.Tests;

public sealed class ArithmeticTests : ScriptTest
{
    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("7 - 9", -2)]
    [InlineData("6 * 7", 42)]
    [InlineData("7 / 2", 3)]
    [InlineData("7 % 3", 1)]
    [InlineData("-(3 + 4)", -7)]
    [InlineData("+5", 5)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("1 - 2 - 3", -4)]
    [InlineData("100 / 5 / 2", 10)]
    [InlineData("~5", -6)]
    [InlineData("5 & 3", 1)]
    [InlineData("5 | 3", 7)]
    [InlineData("5 ^ 3", 6)]
    [InlineData("1 << 4", 16)]
    [InlineData("32 >> 2", 8)]
    [InlineData("-8 >> 1", -4)]
    public void Integer_arithmetic_matches_csharp(string source, int expected) =>
        Assert.Equal(expected, Eval<int>(source));

    [Theory]
    [InlineData("1.5 + 2.25", 3.75)]
    [InlineData("10.0 / 4.0", 2.5)]
    [InlineData("1e3 + 1", 1001.0)]
    [InlineData("2.5 * 4", 10.0)]
    public void Double_arithmetic_matches_csharp(string source, double expected) =>
        Assert.Equal(expected, Eval<double>(source), 10);

    [Fact]
    public void Decimal_arithmetic_uses_operator_methods()
    {
        Assert.Equal(3.30m, Eval<decimal>("1.10m + 2.20m"));
        Assert.Equal(2.5m, Eval<decimal>("10m / 4m"));
        Assert.Equal(-1.5m, Eval<decimal>("-1.5m"));
        Assert.Equal(1m, Eval<decimal>("7m % 3m"));
    }

    [Fact]
    public void Unsigned_division_and_shift_use_unsigned_opcodes()
    {
        var globals = new NumberGlobals { U = 4_000_000_000 };
        Assert.Equal(2_000_000_000u, Run<NumberGlobals, uint>("U / 2", globals));
        Assert.Equal(2_000_000_000u, Run<NumberGlobals, uint>("U >> 1", globals));
    }

    [Fact]
    public void Long_arithmetic_does_not_truncate()
    {
        var globals = new NumberGlobals { BigA = 4_000_000_000L };
        Assert.Equal(8_000_000_000L, Run<NumberGlobals, long>("BigA * 2", globals));
    }

    [Fact]
    public void Integer_division_by_zero_throws_at_run_time()
    {
        Assert.Throws<DivideByZeroException>(() => Eval<int>("var z = 0; return 1 / z;"));
    }
}

public sealed class NumericPromotionTests : ScriptTest
{
    [Fact]
    public void Byte_plus_byte_is_int()
    {
        // In C# byte + byte promotes to int, so the sum does not wrap at 255.
        var globals = new NumberGlobals { Small = 200 };
        Assert.Equal(400, Run<NumberGlobals, int>("Small + Small", globals));
    }

    [Fact]
    public void Int_plus_long_is_long()
    {
        var globals = new NumberGlobals { A = 1, BigA = long.MaxValue - 1 };
        Assert.Equal(long.MaxValue, Run<NumberGlobals, long>("A + BigA", globals));
    }

    [Fact]
    public void Int_plus_double_is_double()
    {
        var globals = new NumberGlobals { A = 1, D = 0.5 };
        Assert.Equal(1.5, Run<NumberGlobals, double>("A + D", globals), 10);
    }

    [Fact]
    public void Uint_plus_int_is_long()
    {
        // uint + int has no common unsigned type, so C# promotes both to long.
        var globals = new NumberGlobals { U = uint.MaxValue, A = 1 };
        Assert.Equal(4_294_967_296L, Run<NumberGlobals, long>("U + A", globals));
    }

    [Fact]
    public void Float_plus_double_is_double()
    {
        var globals = new NumberGlobals { F = 0.5f, D = 0.25 };
        Assert.Equal(0.75, Run<NumberGlobals, double>("F + D", globals), 10);
    }

    [Fact]
    public void Decimal_with_double_has_no_promotion()
    {
        AssertError<NumberGlobals, decimal>("M + D", Diagnostics.ErrorCode.OperatorNotDefined);
    }

    [Fact]
    public void Char_promotes_to_int_in_arithmetic()
    {
        var globals = new NumberGlobals { Ch = 'A' };
        Assert.Equal(66, Run<NumberGlobals, int>("Ch + 1", globals));
    }

    [Fact]
    public void Small_integral_literal_narrows_to_byte()
    {
        Assert.Equal((byte)7, Eval<byte>("byte b = 7; return b;"));
    }

    [Fact]
    public void Out_of_range_literal_does_not_narrow()
    {
        AssertErrorIn("byte b = 300; return b;", Diagnostics.ErrorCode.CannotConvertImplicitly);
    }
}

public sealed class ComparisonTests : ScriptTest
{
    [Theory]
    [InlineData("1 < 2", true)]
    [InlineData("2 < 1", false)]
    [InlineData("2 <= 2", true)]
    [InlineData("3 > 2", true)]
    [InlineData("2 >= 3", false)]
    [InlineData("2 == 2", true)]
    [InlineData("2 != 2", false)]
    [InlineData("1.5 < 2.5", true)]
    [InlineData("1.5m > 0.5m", true)]
    public void Comparisons_match_csharp(string source, bool expected) =>
        Assert.Equal(expected, Eval<bool>(source));

    [Fact]
    public void Unsigned_comparison_uses_unsigned_opcodes()
    {
        var globals = new NumberGlobals { U = 4_000_000_000 };
        Assert.True(Run<NumberGlobals, bool>("U > 1", globals));
    }

    [Fact]
    public void Nan_compares_false_in_every_direction()
    {
        const string source = """
            var nan = 0.0 / 0.0;
            return nan < 1.0 || nan > 1.0 || nan <= 1.0 || nan >= 1.0 || nan == nan;
            """;

        Assert.False(Eval<bool>(source));
    }

    [Fact]
    public void String_equality_compares_by_value()
    {
        const string source = """
            var a = "he" + "llo";
            return a == "hello";
            """;

        Assert.True(Eval<bool>(source));
    }

    [Fact]
    public void Reference_equality_for_objects()
    {
        var globals = new OrderGlobals();
        Assert.True(Run<OrderGlobals, bool>("Order == Order", globals));
    }

    [Fact]
    public void Enum_comparison_and_equality()
    {
        var globals = new OrderGlobals { State = Status.Active };
        Assert.True(Run<OrderGlobals, bool>("State == Status.Active", globals));
        Assert.False(Run<OrderGlobals, bool>("State == Status.Suspended", globals));
        Assert.True(Run<OrderGlobals, bool>("State < Status.Suspended", globals));
    }
}

public sealed class LogicalTests : ScriptTest
{
    [Theory]
    [InlineData("true && true", true)]
    [InlineData("true && false", false)]
    [InlineData("false || true", true)]
    [InlineData("false || false", false)]
    [InlineData("!true", false)]
    [InlineData("true & false", false)]
    [InlineData("true | false", true)]
    [InlineData("true ^ true", false)]
    public void Boolean_operators_match_csharp(string source, bool expected) =>
        Assert.Equal(expected, Eval<bool>(source));

    [Fact]
    public void And_short_circuits_before_dividing_by_zero()
    {
        Assert.False(Eval<bool>("var z = 0; return z != 0 && 1 / z > 0;"));
    }

    [Fact]
    public void Or_short_circuits_before_dividing_by_zero()
    {
        Assert.True(Eval<bool>("var z = 0; return z == 0 || 1 / z > 0;"));
    }

    [Fact]
    public void Non_boolean_condition_is_rejected()
    {
        AssertErrorIn("1 && true", Diagnostics.ErrorCode.ConditionMustBeBool);
    }
}

public sealed class StringTests : ScriptTest
{
    [Fact]
    public void Concatenation_of_two_strings()
    {
        Assert.Equal("ab", Eval<string>("\"a\" + \"b\""));
    }

    [Fact]
    public void Concatenation_boxes_non_string_operands()
    {
        Assert.Equal("a1", Eval<string>("\"a\" + 1"));
        Assert.Equal("1a", Eval<string>("1 + \"a\""));
        Assert.Equal("a1.5", Eval<string>("\"a\" + 1.5m"));
    }

    [Fact]
    public void Concatenation_with_null_yields_empty()
    {
        Assert.Equal("a", Eval<string>("\"a\" + null"));
    }

    [Fact]
    public void Escapes_are_decoded()
    {
        Assert.Equal("a\tb\nc\"d\\e", Eval<string>(@"""a\tb\nc\""d\\e"""));
        Assert.Equal("A", Eval<string>(@"""A"""));
    }

    [Fact]
    public void Instance_methods_on_string_resolve()
    {
        Assert.Equal("ABC", Eval<string>("\"abc\".ToUpperInvariant()"));
        Assert.Equal(3, Eval<int>("\"abc\".Length"));
        Assert.True(Eval<bool>("\"abc\".StartsWith(\"ab\")"));
    }
}

public sealed class OperatorOverloadTests : ScriptTest
{
    [Fact]
    public void User_defined_addition_is_selected()
    {
        var globals = new OrderGlobals { Wallet = new Money(10m) };
        var result = Run<OrderGlobals, Money>("Wallet + Wallet", globals);
        Assert.Equal(20m, result.Amount);
    }

    [Fact]
    public void User_defined_comparison_is_selected()
    {
        var globals = new OrderGlobals { Wallet = new Money(10m) };
        Assert.True(Run<OrderGlobals, bool>("Wallet > new Money(5m)", globals));
        Assert.False(Run<OrderGlobals, bool>("Wallet < new Money(5m)", globals));
    }

    [Fact]
    public void User_defined_equality_is_selected()
    {
        var globals = new OrderGlobals { Wallet = new Money(10m) };
        Assert.True(Run<OrderGlobals, bool>("Wallet == new Money(10m)", globals));
        Assert.True(Run<OrderGlobals, bool>("Wallet != new Money(11m)", globals));
    }

    [Fact]
    public void Implicit_user_defined_conversion_applies()
    {
        var globals = new OrderGlobals { Wallet = new Money(10m) };
        Assert.Equal(10m, Run<OrderGlobals, decimal>("Wallet", globals));
    }

    [Fact]
    public void Explicit_user_defined_conversion_requires_a_cast()
    {
        var globals = new OrderGlobals();
        Assert.Equal(5m, Run<OrderGlobals, decimal>("((Money)5m).Amount", globals));
    }

    [Fact]
    public void Mixed_operand_overload_is_selected()
    {
        var globals = new OrderGlobals { Wallet = new Money(3m) };
        Assert.Equal(9m, Run<OrderGlobals, decimal>("(Wallet * 3).Amount", globals));
    }
}
