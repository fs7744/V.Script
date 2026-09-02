using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>Verbatim and raw string literals.</summary>
public sealed class StringLiteralTests : ScriptTest
{
    [Fact]
    public void Verbatim_takes_backslashes_literally()
    {
        Assert.Equal(@"a\b", Eval<string>("return @\"a\\b\";"));
    }

    [Fact]
    public void Verbatim_doubles_quotes_to_escape_them()
    {
        Assert.Equal(@"say ""hi""", Eval<string>("return @\"say \"\"hi\"\"\";"));
    }

    [Fact]
    public void Verbatim_spans_lines()
    {
        Assert.Equal("a\nb", Eval<string>("return @\"a\nb\";"));
    }

    [Fact]
    public void Verbatim_interpolated_in_either_order()
    {
        var globals = new NumberGlobals { A = 7 };

        Assert.Equal(@"a\7", Run<NumberGlobals, string>("return $@\"a\\{A}\";", globals));
        Assert.Equal(@"a\7", Run<NumberGlobals, string>("return @$\"a\\{A}\";", globals));
    }

    [Fact]
    public void Raw_single_line()
    {
        Assert.Equal("abc", Eval<string>("return \"\"\"abc\"\"\";"));
    }

    [Fact]
    public void Raw_keeps_quotes_and_backslashes()
    {
        Assert.Equal(@"a""b\c", Eval<string>("return \"\"\"a\"b\\c\"\"\";"));
    }

    [Fact]
    public void Raw_multi_line_strips_the_closing_indentation()
    {
        const string source = "return \"\"\"\n    one\n    two\n    \"\"\";";
        Assert.Equal("one\ntwo", Eval<string>(source));
    }

    [Fact]
    public void Raw_multi_line_keeps_relative_indentation()
    {
        const string source = "return \"\"\"\n    a\n      b\n    \"\"\";";
        Assert.Equal("a\n  b", Eval<string>(source));
    }

    [Fact]
    public void A_longer_fence_lets_three_quotes_be_content()
    {
        // """"a"""b"""" — a four-quote fence, so three quotes are ordinary content.
        var fence = new string('"', 4);
        var source = $"return {fence}a{new string('"', 3)}b{fence};";

        Assert.Equal("a\"\"\"b", Eval<string>(source));
    }

    [Fact]
    public void Under_indented_content_is_reported() =>
        AssertErrorIn("return \"\"\"\n  a\n    \"\"\";", ErrorCode.UnterminatedString);

    [Fact]
    public void An_unterminated_verbatim_string_is_reported() =>
        AssertErrorIn("return @\"abc;", ErrorCode.UnterminatedString);
}

/// <summary><c>checked</c> and <c>unchecked</c>.</summary>
public sealed class CheckedTests : ScriptTest
{
    [Fact]
    public void Arithmetic_is_unchecked_by_default()
    {
        var globals = new NumberGlobals { A = int.MaxValue, B = 1 };
        Assert.Equal(int.MinValue, Run<NumberGlobals, int>("return A + B;", globals));
    }

    [Fact]
    public void Checked_addition_throws_on_overflow()
    {
        var globals = new NumberGlobals { A = int.MaxValue, B = 1 };
        Assert.Throws<OverflowException>(() => Run<NumberGlobals, int>("return checked(A + B);", globals));
    }

    [Fact]
    public void Checked_subtraction_and_multiplication()
    {
        var globals = new NumberGlobals { A = int.MinValue, B = 1 };
        Assert.Throws<OverflowException>(() => Run<NumberGlobals, int>("return checked(A - B);", globals));

        var big = new NumberGlobals { A = int.MaxValue, B = 2 };
        Assert.Throws<OverflowException>(() => Run<NumberGlobals, int>("return checked(A * B);", big));
    }

    [Fact]
    public void Checked_unsigned_arithmetic_uses_the_unsigned_opcodes()
    {
        var globals = new NumberGlobals { U = uint.MaxValue };
        Assert.Throws<OverflowException>(() => Run<NumberGlobals, uint>("return checked(U + 1);", globals));
    }

    [Fact]
    public void Checked_conversion_throws()
    {
        var globals = new NumberGlobals { BigA = long.MaxValue };
        Assert.Throws<OverflowException>(() => Run<NumberGlobals, int>("return checked((int)BigA);", globals));
    }

    [Fact]
    public void Unchecked_conversion_truncates()
    {
        var globals = new NumberGlobals { BigA = 0x1_0000_0001L };
        Assert.Equal(1, Run<NumberGlobals, int>("return unchecked((int)BigA);", globals));
    }

    [Fact]
    public void A_checked_block_covers_everything_inside_it()
    {
        const string source = """
            checked
            {
                var x = A + B;
                return x;
            }
            """;

        var globals = new NumberGlobals { A = int.MaxValue, B = 1 };
        Assert.Throws<OverflowException>(() => Run<NumberGlobals, int>(source, globals));
    }

    [Fact]
    public void Unchecked_inside_checked_wins_again()
    {
        const string source = """
            checked
            {
                return unchecked(A + B);
            }
            """;

        var globals = new NumberGlobals { A = int.MaxValue, B = 1 };
        Assert.Equal(int.MinValue, Run<NumberGlobals, int>(source, globals));
    }

    [Fact]
    public void Floating_point_never_traps()
    {
        Assert.True(double.IsInfinity(Eval<double>("return checked(1e308 * 10.0);")));
    }
}
