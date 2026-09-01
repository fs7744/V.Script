using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>The <c>switch</c> statement, including how <c>break</c> and <c>continue</c> behave inside one.</summary>
public sealed class SwitchStatementTests : ScriptTest
{
    [Fact]
    public void Constant_cases()
    {
        const string source = """
            switch (A)
            {
                case 1: return "one";
                case 2: return "two";
                default: return "other";
            }
            """;

        Assert.Equal("one", Run<NumberGlobals, string>(source, new NumberGlobals { A = 1 }));
        Assert.Equal("two", Run<NumberGlobals, string>(source, new NumberGlobals { A = 2 }));
        Assert.Equal("other", Run<NumberGlobals, string>(source, new NumberGlobals { A = 9 }));
    }

    [Fact]
    public void Without_a_default_an_unmatched_value_falls_out()
    {
        const string source = """
            var result = "none";
            switch (A)
            {
                case 1:
                    result = "one";
                    break;
            }
            return result;
            """;

        Assert.Equal("one", Run<NumberGlobals, string>(source, new NumberGlobals { A = 1 }));
        Assert.Equal("none", Run<NumberGlobals, string>(source, new NumberGlobals { A = 5 }));
    }

    [Fact]
    public void Several_labels_share_one_section()
    {
        const string source = """
            switch (A)
            {
                case 1:
                case 2:
                case 3:
                    return "small";
                default:
                    return "large";
            }
            """;

        foreach (var value in new[] { 1, 2, 3 })
            Assert.Equal("small", Run<NumberGlobals, string>(source, new NumberGlobals { A = value }));

        Assert.Equal("large", Run<NumberGlobals, string>(source, new NumberGlobals { A = 4 }));
    }

    [Fact]
    public void Break_leaves_the_switch_and_execution_continues_after_it()
    {
        const string source = """
            var log = "";
            switch (A)
            {
                case 1:
                    log = log + "a";
                    break;
                default:
                    log = log + "b";
                    break;
            }
            return log + "!";
            """;

        Assert.Equal("a!", Run<NumberGlobals, string>(source, new NumberGlobals { A = 1 }));
        Assert.Equal("b!", Run<NumberGlobals, string>(source, new NumberGlobals { A = 2 }));
    }

    [Fact]
    public void Break_inside_a_switch_inside_a_loop_leaves_only_the_switch()
    {
        const string source = """
            var sum = 0;
            for (var i = 0; i < 5; i++)
            {
                switch (i % 2)
                {
                    case 0:
                        sum = sum + i;
                        break;
                    default:
                        break;
                }
            }
            return sum;
            """;

        Assert.Equal(0 + 2 + 4, Eval<int>(source));
    }

    [Fact]
    public void Continue_inside_a_switch_belongs_to_the_loop()
    {
        const string source = """
            var sum = 0;
            for (var i = 0; i < 5; i++)
            {
                switch (i)
                {
                    case 2:
                        continue;
                    default:
                        break;
                }
                sum = sum + i;
            }
            return sum;
            """;

        Assert.Equal(0 + 1 + 3 + 4, Eval<int>(source));
    }

    [Fact]
    public void Type_patterns_as_case_labels()
    {
        const string source = """
            switch (Value)
            {
                case int n: return n * 2;
                case string s: return s.Length;
                default: return -1;
            }
            """;

        Assert.Equal(84, Run<PatternGlobals, int>(source, new PatternGlobals { Value = 42 }));
        Assert.Equal(3, Run<PatternGlobals, int>(source, new PatternGlobals { Value = "abc" }));
        Assert.Equal(-1, Run<PatternGlobals, int>(source, new PatternGlobals { Value = 1.5 }));
    }

    [Fact]
    public void Relational_and_combined_patterns_as_case_labels()
    {
        const string source = """
            switch (A)
            {
                case < 0: return "neg";
                case 0: return "zero";
                case > 0 and < 10: return "small";
                default: return "big";
            }
            """;

        Assert.Equal("neg", Run<NumberGlobals, string>(source, new NumberGlobals { A = -3 }));
        Assert.Equal("zero", Run<NumberGlobals, string>(source, new NumberGlobals { A = 0 }));
        Assert.Equal("small", Run<NumberGlobals, string>(source, new NumberGlobals { A = 4 }));
        Assert.Equal("big", Run<NumberGlobals, string>(source, new NumberGlobals { A = 40 }));
    }

    [Fact]
    public void When_guards_are_honoured()
    {
        const string source = """
            switch (Value)
            {
                case int n when n > 100: return "big";
                case int n2: return "int";
                default: return "other";
            }
            """;

        Assert.Equal("big", Run<PatternGlobals, string>(source, new PatternGlobals { Value = 200 }));
        Assert.Equal("int", Run<PatternGlobals, string>(source, new PatternGlobals { Value = 5 }));
        Assert.Equal("other", Run<PatternGlobals, string>(source, new PatternGlobals { Value = "x" }));
    }

    [Fact]
    public void Sections_are_tested_in_source_order()
    {
        const string source = """
            switch (A)
            {
                case > 0: return "positive";
                case 5: return "five";
                default: return "other";
            }
            """;

        Assert.Equal("positive", Run<NumberGlobals, string>(source, new NumberGlobals { A = 5 }));
    }

    [Fact]
    public void Default_is_tested_last_wherever_it_is_written()
    {
        const string source = """
            switch (A)
            {
                default: return "other";
                case 1: return "one";
            }
            """;

        Assert.Equal("one", Run<NumberGlobals, string>(source, new NumberGlobals { A = 1 }));
        Assert.Equal("other", Run<NumberGlobals, string>(source, new NumberGlobals { A = 2 }));
    }

    [Fact]
    public void The_governing_expression_is_evaluated_once()
    {
        const string source = """
            switch (Calc.Counter++)
            {
                case 0:
                    break;
                default:
                    break;
            }
            return Calc.Counter;
            """;

        Assert.Equal(1, Run<OrderGlobals, int>(source, new OrderGlobals()));
    }

    [Fact]
    public void A_section_may_declare_its_own_locals()
    {
        const string source = """
            switch (A)
            {
                case 1:
                {
                    var x = 10;
                    return x;
                }
                default:
                {
                    var x = 20;
                    return x;
                }
            }
            """;

        Assert.Equal(10, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1 }));
        Assert.Equal(20, Run<NumberGlobals, int>(source, new NumberGlobals { A = 2 }));
    }

    [Fact]
    public void Switch_statements_nest()
    {
        const string source = """
            switch (A)
            {
                case 1:
                    switch (B)
                    {
                        case 1: return 11;
                        default: return 10;
                    }
                default:
                    return 0;
            }
            """;

        Assert.Equal(11, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1, B = 1 }));
        Assert.Equal(10, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1, B = 2 }));
        Assert.Equal(0, Run<NumberGlobals, int>(source, new NumberGlobals { A = 2 }));
    }

    [Fact]
    public void An_exhaustive_switch_counts_as_returning()
    {
        // Would be VS3005 if the analysis did not see through the switch.
        const string source = """
            switch (A)
            {
                case 1: return 1;
                default: return 0;
            }
            """;

        Assert.Equal(1, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1 }));
    }

    [Fact]
    public void Switch_works_inside_a_lambda()
    {
        const string source = """
            var f = new Func<int, string>(n =>
            {
                switch (n)
                {
                    case 1: return "one";
                    default: return "other";
                }
            });
            return f(1) + f(2);
            """;

        Assert.Equal("oneother", Eval<string>(source));
    }

    [Fact]
    public void An_empty_switch_is_allowed()
    {
        Assert.Equal(7, Run<NumberGlobals, int>("switch (A) { } return 7;", new NumberGlobals()));
    }

    [Fact]
    public void Falling_out_of_a_section_is_an_error() =>
        AssertError<NumberGlobals, int>(
            "switch (A) { case 1: var x = 1; default: return 0; }",
            ErrorCode.SwitchSectionFallsThrough);

    [Fact]
    public void An_empty_section_before_another_label_is_a_fall_through_error() =>
        AssertError<NumberGlobals, int>(
            "switch (A) { case 1: case 2: } return 0;",
            ErrorCode.SwitchSectionFallsThrough);

    [Fact]
    public void Two_default_labels_are_an_error() =>
        AssertError<NumberGlobals, int>(
            "switch (A) { default: return 1; case 1: return 2; default: return 3; }",
            ErrorCode.UnexpectedToken);

    [Fact]
    public void Break_outside_a_loop_or_switch_is_still_an_error() =>
        AssertErrorIn("break;", ErrorCode.BreakOutsideLoop);

    [Fact]
    public void Continue_inside_a_switch_outside_a_loop_is_an_error() =>
        AssertError<NumberGlobals, int>(
            "switch (A) { case 1: continue; default: return 0; }",
            ErrorCode.ContinueOutsideLoop);
}
