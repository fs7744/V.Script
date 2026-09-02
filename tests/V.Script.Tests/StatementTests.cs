using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary><c>using</c>, <c>lock</c>, labels and <c>goto</c>.</summary>
public sealed class UsingStatementTests : ScriptTest
{
    [Fact]
    public void The_resource_is_disposed_on_the_way_out()
    {
        const string source = """
            var probe = new DisposeProbe();
            using (probe)
            {
                probe.Touch();
            }
            return probe.Disposed;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void The_declaration_form_declares_and_disposes()
    {
        const string source = """
            var outer = new DisposeProbe();
            using (var p = outer)
            {
                p.Touch();
            }
            return outer.Disposed * 10 + outer.Touched;
            """;

        Assert.Equal(11, Eval<int>(source));
    }

    [Fact]
    public void The_using_declaration_covers_the_rest_of_the_block()
    {
        const string source = """
            var probe = new DisposeProbe();
            var seen = 0;
            if (true)
            {
                using var p = probe;
                p.Touch();
                seen = probe.Disposed;
            }
            return seen * 10 + probe.Disposed;
            """;

        // Not yet disposed inside the block; disposed once it is left.
        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void Disposal_happens_even_when_the_body_throws()
    {
        const string source = """
            var probe = new DisposeProbe();
            try
            {
                using (probe)
                {
                    throw new InvalidOperationException("x");
                }
            }
            catch (InvalidOperationException)
            {
            }
            return probe.Disposed;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void A_null_resource_is_not_disposed()
    {
        Assert.Equal(0, Eval<int>("DisposeProbe p = null; using (p) { } return 0;"));
    }

    [Fact]
    public void A_non_disposable_resource_is_reported() =>
        AssertErrorIn("using (var x = 1) { } return 0;", ErrorCode.CannotConvert);
}

public sealed class LockStatementTests : ScriptTest
{
    [Fact]
    public void The_body_runs_and_the_lock_is_released()
    {
        const string source = """
            var gate = new object();
            var total = 0;
            lock (gate)
            {
                total = 1;
            }
            lock (gate)
            {
                total = total + 1;
            }
            return total;
            """;

        Assert.Equal(2, Eval<int>(source));
    }

    [Fact]
    public void The_lock_is_released_even_when_the_body_throws()
    {
        const string source = """
            var gate = new object();
            try
            {
                lock (gate) { throw new InvalidOperationException("x"); }
            }
            catch (InvalidOperationException)
            {
            }
            lock (gate) { return 7; }
            """;

        Assert.Equal(7, Eval<int>(source));
    }

    [Fact]
    public void Locking_a_value_type_is_reported() =>
        AssertErrorIn("lock (1) { } return 0;", ErrorCode.CannotConvert);
}

public sealed class GotoTests : ScriptTest
{
    [Fact]
    public void A_backward_jump_forms_a_loop()
    {
        const string source = """
            var i = 0;
            top:
            i = i + 1;
            if (i < 5) goto top;
            return i;
            """;

        Assert.Equal(5, Eval<int>(source));
    }

    [Fact]
    public void A_forward_jump_skips_statements()
    {
        const string source = """
            var n = 1;
            goto done;
            n = 99;
            done:
            return n;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void A_jump_out_of_a_loop()
    {
        const string source = """
            var total = 0;
            for (var i = 0; i < 10; i++)
            {
                if (i == 3) goto after;
                total = total + i;
            }
            after:
            return total;
            """;

        Assert.Equal(0 + 1 + 2, Eval<int>(source));
    }

    [Fact]
    public void A_jump_out_of_a_try_runs_the_finally()
    {
        const string source = """
            var log = 0;
            try
            {
                goto done;
            }
            finally
            {
                log = 1;
            }
            done:
            return log;
            """;

        Assert.Equal(1, Eval<int>(source));
    }

    [Fact]
    public void Goto_case_transfers_to_another_section()
    {
        const string source = """
            switch (A)
            {
                case 1: goto case 2;
                case 2: return 20;
                default: return 0;
            }
            """;

        Assert.Equal(20, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1 }));
        Assert.Equal(20, Run<NumberGlobals, int>(source, new NumberGlobals { A = 2 }));
        Assert.Equal(0, Run<NumberGlobals, int>(source, new NumberGlobals { A = 3 }));
    }

    [Fact]
    public void Goto_default_transfers_to_the_default_section()
    {
        const string source = """
            switch (A)
            {
                case 1: goto default;
                default: return -1;
            }
            """;

        Assert.Equal(-1, Run<NumberGlobals, int>(source, new NumberGlobals { A = 1 }));
    }

    [Fact]
    public void An_unknown_label_is_reported() =>
        AssertErrorIn("goto nowhere; return 0;", ErrorCode.UndefinedName);

    [Fact]
    public void A_duplicate_label_is_reported() =>
        AssertErrorIn("a: var x = 1; a: return x;", ErrorCode.VariableAlreadyDefined);

    [Fact]
    public void Jumping_into_a_try_block_is_reported() =>
        AssertErrorIn("goto inside; try { inside: return 1; } finally { }", ErrorCode.ConstructNotSupported);

    [Fact]
    public void Goto_case_outside_a_switch_is_reported() =>
        AssertErrorIn("goto case 1; return 0;", ErrorCode.ConstructNotSupported);

    [Fact]
    public void Goto_case_for_a_missing_section_is_reported() =>
        AssertError<NumberGlobals, int>(
            "switch (A) { case 1: goto case 9; default: return 0; }", ErrorCode.UndefinedName);
}
