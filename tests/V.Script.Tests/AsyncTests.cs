using V.Script.Diagnostics;

namespace V.Script.Tests;

public sealed class AsyncExecutionTests : ScriptTest
{
    [Fact]
    public async Task Awaiting_a_completed_task()
    {
        var globals = new AsyncGlobals { Seed = 3 };
        Assert.Equal(4, await RunAsync<AsyncGlobals, int>("await Service.CompletedAsync(Seed)", globals));
    }

    [Fact]
    public async Task Awaiting_a_task_that_actually_suspends()
    {
        var globals = new AsyncGlobals { Seed = 5 };
        Assert.Equal(10, await RunAsync<AsyncGlobals, int>("await Service.GetAsync(Seed)", globals));
        Assert.Equal(1, globals.Service.Calls);
    }

    [Fact]
    public async Task Awaiting_a_value_task()
    {
        var globals = new AsyncGlobals { Seed = 5 };
        Assert.Equal(105, await RunAsync<AsyncGlobals, int>("await Service.ValueAsync(Seed)", globals));
    }

    [Fact]
    public async Task Awaiting_a_non_generic_task_as_a_statement()
    {
        const string source = """
            await Service.NoResultAsync();
            return 1;
            """;

        var globals = new AsyncGlobals();
        Assert.Equal(1, await RunAsync<AsyncGlobals, int>(source, globals));
        Assert.Equal(1, globals.Service.Calls);
    }

    [Fact]
    public async Task Await_inside_a_for_loop()
    {
        const string source = """
            var sum = 0;
            for (var i = 0; i < 4; i++)
                sum += await Service.GetAsync(i);
            return sum;
            """;

        // 0 + 2 + 4 + 6
        Assert.Equal(12, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task Await_inside_a_while_loop_with_continue()
    {
        const string source = """
            var i = 0;
            var sum = 0;
            while (i < 5)
            {
                i++;
                if (i % 2 == 0) continue;
                sum += await Service.GetAsync(i);
            }
            return sum;
            """;

        // (1 + 3 + 5) * 2
        Assert.Equal(18, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task Await_inside_a_conditional_branch()
    {
        const string source = """
            if (Seed > 0) return await Service.GetAsync(Seed);
            return -1;
            """;

        Assert.Equal(8, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals { Seed = 4 }));
        Assert.Equal(-1, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals { Seed = 0 }));
    }

    [Fact]
    public async Task Await_inside_a_try_block_is_allowed()
    {
        const string source = """
            var result = 0;
            try { result = await Service.GetAsync(Seed); }
            catch (InvalidOperationException) { result = -1; }
            return result;
            """;

        Assert.Equal(14, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals { Seed = 7 }));
    }

    [Fact]
    public async Task Exception_from_an_awaited_task_is_catchable()
    {
        const string source = """
            try { return await Service.ThrowingAsync(); }
            catch (InvalidOperationException) { return -1; }
            """;

        Assert.Equal(-1, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task Awaits_run_in_order_and_thread_their_results()
    {
        const string source = """
            var first = await Service.GetAsync(2);
            var second = await Service.GetAsync(first);
            return second;
            """;

        Assert.Equal(8, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task Synchronous_script_compiled_as_async_still_works()
    {
        Assert.Equal(7, await RunAsync<AsyncGlobals, int>("3 + 4", new AsyncGlobals()));
    }

    [Fact]
    public async Task Async_delegate_compilation()
    {
        using var engine = new ScriptEngine(Options);
        using var compiled = engine.CompileAsyncDelegate<Func<AsyncService, int, Task<int>>>(
            "await svc.GetAsync(n) + 1", "svc", "n");

        Assert.Equal(11, await compiled.Value(new AsyncService(), 5));
    }

    [Fact]
    public async Task Concurrent_invocations_of_one_script_do_not_interfere()
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.CompileAsync<AsyncGlobals, int>("await Service.GetAsync(Seed)");

        var tasks = Enumerable.Range(0, 50)
            .Select(i => script.RunAsync(new AsyncGlobals { Seed = i }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => i * 2), results);
    }
}

public sealed class AsyncRestrictionTests : ScriptTest
{
    [Fact]
    public void Await_in_a_synchronous_script_is_rejected()
    {
        AssertError<AsyncGlobals, int>(
            "await Service.GetAsync(1)", ErrorCode.AwaitInSynchronousScript);
    }

    /// <summary>
    /// The runtime gives no protection here: a suspension point inside a handler crashes the
    /// process outright rather than throwing. The binder must refuse it unconditionally.
    /// </summary>
    [Fact]
    public void Await_inside_catch_is_rejected()
    {
        const string source = """
            try { return 1; }
            catch (Exception) { return await Service.GetAsync(1); }
            """;

        AssertError<AsyncGlobals, int>(source, ErrorCode.AwaitInExceptionHandler, async: true);
    }

    [Fact]
    public void Await_inside_finally_is_rejected()
    {
        const string source = """
            try { return 1; }
            finally { await Service.NoResultAsync(); }
            """;

        AssertError<AsyncGlobals, int>(source, ErrorCode.AwaitInExceptionHandler, async: true);
    }

    [Fact]
    public void Await_of_a_non_awaitable_is_rejected()
    {
        AssertError<AsyncGlobals, int>("await Seed", ErrorCode.NotAWaitable, async: true);
    }

    [Fact]
    public void Compile_delegate_rejects_a_task_returning_signature()
    {
        using var engine = new ScriptEngine(Options);
        var exception = Assert.Throws<ArgumentException>(
            () => engine.CompileDelegate<Func<int, Task<int>>>("a", "a"));

        Assert.Contains("CompileAsyncDelegate", exception.Message);
    }

    [Fact]
    public void Compile_async_delegate_rejects_a_non_task_signature()
    {
        using var engine = new ScriptEngine(Options);
        Assert.Throws<ArgumentException>(
            () => engine.CompileAsyncDelegate<Func<int, int>>("a", "a"));
    }
}
