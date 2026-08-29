namespace V.Script.Tests;

public sealed class ControlFlowTests : ScriptTest
{
    [Fact]
    public void If_else_chain()
    {
        const string source = """
            if (A > 10) return "big";
            else if (A > 5) return "medium";
            else return "small";
            """;

        Assert.Equal("big", Run<NumberGlobals, string>(source, new NumberGlobals { A = 20 }));
        Assert.Equal("medium", Run<NumberGlobals, string>(source, new NumberGlobals { A = 7 }));
        Assert.Equal("small", Run<NumberGlobals, string>(source, new NumberGlobals { A = 1 }));
    }

    [Fact]
    public void While_loop_with_break()
    {
        const string source = """
            var i = 0;
            while (true)
            {
                i++;
                if (i == 5) break;
            }
            return i;
            """;

        Assert.Equal(5, Eval<int>(source));
    }

    [Fact]
    public void While_loop_with_continue_skips()
    {
        const string source = """
            var sum = 0;
            var i = 0;
            while (i < 10)
            {
                i++;
                if (i % 2 == 0) continue;
                sum += i;
            }
            return sum;
            """;

        Assert.Equal(25, Eval<int>(source)); // 1+3+5+7+9
    }

    [Fact]
    public void Do_while_runs_the_body_at_least_once()
    {
        const string source = """
            var count = 0;
            do { count++; } while (false);
            return count;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void For_loop_continue_still_runs_the_incrementor()
    {
        const string source = """
            var sum = 0;
            for (var i = 0; i < 10; i++)
            {
                if (i % 2 == 0) continue;
                sum += i;
            }
            return sum;
            """;

        Assert.Equal(25, Eval<int>(source));
    }

    [Fact]
    public void For_loop_with_multiple_initializers_and_incrementors()
    {
        const string source = """
            var total = 0;
            for (int i = 0, j = 10; i < j; i++, j--)
                total++;
            return total;
            """;

        Assert.Equal(5, Eval<int>(source));
    }

    [Fact]
    public void Nested_loops_break_only_the_inner_one()
    {
        const string source = """
            var count = 0;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 10; j++)
                {
                    if (j == 2) break;
                    count++;
                }
            }
            return count;
            """;

        Assert.Equal(6, Eval<int>(source));
    }

    [Fact]
    public void Block_scoping_allows_shadow_free_reuse()
    {
        const string source = """
            var total = 0;
            { var x = 1; total += x; }
            { var x = 2; total += x; }
            return total;
            """;

        Assert.Equal(3, Eval<int>(source));
    }

    [Fact]
    public void Redeclaring_in_the_same_scope_is_an_error()
    {
        AssertErrorIn("var x = 1; var x = 2; return x;", Diagnostics.ErrorCode.VariableAlreadyDefined);
    }

    [Fact]
    public void Ternary_selects_the_right_branch()
    {
        Assert.Equal(1, Run<NumberGlobals, int>("Flag ? 1 : 2", new NumberGlobals { Flag = true }));
        Assert.Equal(2, Run<NumberGlobals, int>("Flag ? 1 : 2", new NumberGlobals { Flag = false }));
    }

    [Fact]
    public void Ternary_finds_a_common_type()
    {
        Assert.Equal(1.0, Run<NumberGlobals, double>("Flag ? 1 : 2.0", new NumberGlobals { Flag = true }), 10);
    }

    [Fact]
    public void Ternary_with_incompatible_branches_is_rejected()
    {
        AssertErrorIn("true ? 1 : \"a\"", Diagnostics.ErrorCode.CannotConvert);
    }

    [Fact]
    public void Break_outside_a_loop_is_rejected()
    {
        AssertErrorIn("break; return 1;", Diagnostics.ErrorCode.BreakOutsideLoop);
    }

    [Fact]
    public void Missing_return_on_some_path_is_rejected()
    {
        AssertErrorIn("if (true) return 1;", Diagnostics.ErrorCode.NotAllCodePathsReturn);
    }

    [Fact]
    public void Both_branches_returning_satisfies_the_check()
    {
        Assert.Equal(1, Eval<int>("if (true) return 1; else return 2;"));
    }

    [Fact]
    public void Infinite_loop_without_break_counts_as_returning()
    {
        Assert.Equal(3, Eval<int>("while (true) { return 3; }"));
    }
}

public sealed class ForEachTests : ScriptTest
{
    [Fact]
    public void Iterating_an_array_uses_indexed_access()
    {
        const string source = """
            var sum = 0;
            foreach (var n in Numbers) sum += n;
            return sum;
            """;

        var globals = new OrderGlobals { Numbers = [1, 2, 3, 4] };
        Assert.Equal(10, Run<OrderGlobals, int>(source, globals));
    }

    [Fact]
    public void Iterating_a_generic_list_uses_the_enumerator()
    {
        const string source = """
            var joined = "";
            foreach (var name in Names) joined += name;
            return joined;
            """;

        var globals = new OrderGlobals { Names = ["a", "b", "c"] };
        Assert.Equal("abc", Run<OrderGlobals, string>(source, globals));
    }

    [Fact]
    public void Iterating_a_dictionary_exposes_key_and_value()
    {
        const string source = """
            var total = 0;
            foreach (var pair in Lookup) total += pair.Value;
            return total;
            """;

        var globals = new OrderGlobals { Lookup = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 } };
        Assert.Equal(3, Run<OrderGlobals, int>(source, globals));
    }

    [Fact]
    public void Explicit_element_type_is_honoured()
    {
        const string source = """
            var sum = 0L;
            foreach (long n in Numbers) sum += n;
            return sum;
            """;

        var globals = new OrderGlobals { Numbers = [1, 2, 3] };
        Assert.Equal(6L, Run<OrderGlobals, long>(source, globals));
    }

    [Fact]
    public void Break_and_continue_work_inside_foreach()
    {
        const string source = """
            var sum = 0;
            foreach (var n in Numbers)
            {
                if (n == 2) continue;
                if (n == 4) break;
                sum += n;
            }
            return sum;
            """;

        var globals = new OrderGlobals { Numbers = [1, 2, 3, 4, 5] };
        Assert.Equal(4, Run<OrderGlobals, int>(source, globals));
    }

    [Fact]
    public void Iterating_an_empty_collection_runs_the_body_zero_times()
    {
        const string source = """
            var count = 0;
            foreach (var n in Numbers) count++;
            return count;
            """;

        Assert.Equal(0, Run<OrderGlobals, int>(source, new OrderGlobals { Numbers = [] }));
    }

    [Fact]
    public void Non_enumerable_source_is_rejected()
    {
        AssertError<NumberGlobals, int>(
            "foreach (var n in A) { } return 0;", Diagnostics.ErrorCode.NotEnumerable);
    }
}

public sealed class ExceptionTests : ScriptTest
{
    [Fact]
    public void Catch_handles_a_thrown_exception()
    {
        const string source = """
            var result = 0;
            try { result = 1 / A; }
            catch (DivideByZeroException) { result = -1; }
            return result;
            """;

        Assert.Equal(-1, Run<NumberGlobals, int>(source, new NumberGlobals { A = 0 }));
        Assert.Equal(1, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1 }));
    }

    [Fact]
    public void Catch_binds_the_exception_variable()
    {
        const string source = """
            try { throw new InvalidOperationException("boom"); }
            catch (InvalidOperationException e) { return e.Message; }
            """;

        Assert.Equal("boom", Eval<string>(source));
    }

    [Fact]
    public void Finally_runs_on_both_paths()
    {
        const string source = """
            var log = "";
            try { log += "t"; }
            finally { log += "f"; }
            return log;
            """;

        Assert.Equal("tf", Eval<string>(source));
    }

    [Fact]
    public void Finally_runs_while_an_exception_unwinds()
    {
        const string source = """
            var log = "";
            try
            {
                try { throw new InvalidOperationException("x"); }
                finally { log += "f"; }
            }
            catch (InvalidOperationException) { log += "c"; }
            return log;
            """;

        Assert.Equal("fc", Eval<string>(source));
    }

    [Fact]
    public void Return_from_inside_try_still_runs_finally()
    {
        const string source = """
            var order = new Order();
            try { return 1; }
            finally { order.Count = 9; }
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void Catch_clauses_are_matched_in_order()
    {
        const string source = """
            try { throw new InvalidOperationException("x"); }
            catch (ArgumentException) { return 1; }
            catch (InvalidOperationException) { return 2; }
            catch (Exception) { return 3; }
            """;

        Assert.Equal(2, Eval<int>(source));
    }

    [Fact]
    public void Break_from_inside_try_leaves_the_loop()
    {
        const string source = """
            var count = 0;
            for (var i = 0; i < 10; i++)
            {
                try { if (i == 3) break; count++; }
                finally { count += 100; }
            }
            return count;
            """;

        // three normal iterations plus four finally passes
        Assert.Equal(3 + 400, Eval<int>(source));
    }

    [Fact]
    public void Throwing_a_non_exception_is_rejected()
    {
        AssertErrorIn("throw 1;", Diagnostics.ErrorCode.CannotConvert);
    }
}
