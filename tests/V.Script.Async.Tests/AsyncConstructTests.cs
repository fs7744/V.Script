using V.Script.Diagnostics;
using V.Script.Tests;

namespace V.Script.Async.Tests;

/// <summary>
/// Async cases belonging to constructs that are otherwise exercised synchronously: a lambda, a
/// local function, a throw expression, and the end-to-end smoke tests.
/// </summary>
public sealed class AsyncConstructTests : AsyncScriptTest
{
    [Fact]
    public void Await_inside_a_lambda_is_rejected()
    {
        AssertError<AsyncGlobals, int>(
            "Func<int> f = () => await Service.GetAsync(1); return f();",
            ErrorCode.AwaitInLambda,
            async: true);
    }

    [Fact]
    public async Task A_local_function_works_in_an_async_script()
    {
        const string source = """
            int Twice(int n) => n * 2;
            var value = await Service.CompletedAsync(3);
            return Twice(value);
            """;

        Assert.Equal(8, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task Throw_expression_in_an_async_script()
    {
        const string source = """
            var value = await Service.CompletedAsync(1);
            return value > 0 ? value : throw new InvalidOperationException("neg");
            """;

        Assert.Equal(2, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
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

    [Fact]
    public async Task Deconstruction_works_in_an_async_script()
    {
        const string source = """
            var value = await Service.CompletedAsync(3);
            var (a, b) = (value, value * 2);
            return a + b;
            """;

        Assert.Equal(12, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }
}
