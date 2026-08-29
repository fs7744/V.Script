namespace V.Script.Tests;

/// <summary>The narrowest end-to-end checks: source in, correct value out, for each carrier.</summary>
public sealed class SmokeTests : ScriptTest
{
    [Fact]
    public void Bare_expression_becomes_the_result()
    {
        Assert.Equal(3, Eval<int>("1 + 2"));
    }

    [Fact]
    public void Explicit_return_works()
    {
        Assert.Equal(7, Eval<int>("return 3 + 4;"));
    }

    [Fact]
    public void Globals_members_resolve_as_bare_names()
    {
        Assert.Equal(30, Run<NumberGlobals, int>("A + B", new NumberGlobals { A = 10, B = 20 }));
    }

    [Fact]
    public void Locals_statements_and_loops_execute()
    {
        const string source = """
            var total = 0;
            for (int i = 1; i <= 10; i++)
                total += i;
            return total;
            """;

        Assert.Equal(55, Eval<int>(source));
    }

    [Fact]
    public void Delegate_compilation_binds_named_parameters()
    {
        using var engine = new ScriptEngine(Options);
        var f = engine.CompileDelegate<Func<int, int, int>>("a * b + 1", "a", "b");
        Assert.Equal(13, f(3, 4));
    }

    [Fact]
    public async Task Async_script_awaits_and_returns()
    {
        var globals = new AsyncGlobals { Seed = 21 };
        var result = await RunAsync<AsyncGlobals, int>("await Service.GetAsync(Seed)", globals);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Await_inside_a_loop_runs_the_state_machine()
    {
        var globals = new AsyncGlobals { Ids = [1, 2, 3] };

        const string source = """
            var sum = 0;
            foreach (var id in Ids)
                sum += await Service.GetAsync(id);
            return sum;
            """;

        Assert.Equal(12, await RunAsync<AsyncGlobals, int>(source, globals));
    }
}
