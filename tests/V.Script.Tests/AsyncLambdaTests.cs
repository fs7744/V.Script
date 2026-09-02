using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>
/// <c>async</c> lambdas and local functions. They need the same runtime-async flag the script
/// body does, so a synchronous script containing one still gets a generated assembly for it.
/// </summary>
public sealed class AsyncLambdaTests : ScriptTest
{
    [Fact]
    public async Task An_async_lambda_inside_an_async_script()
    {
        const string source = """
            Func<int, Task<int>> f = async x => await Service.GetAsync(x);
            return await f(3);
            """;

        Assert.Equal(6, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_lambda_inside_a_synchronous_script()
    {
        // The script itself never suspends; only the lambda does.
        const string source = """
            Func<int, Task<int>> f = async x => await Service.GetAsync(x);
            return f(4).GetAwaiter().GetResult();
            """;

        Assert.Equal(8, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void Var_infers_a_Task_returning_delegate()
    {
        const string source = """
            var f = async (int x) => await Service.GetAsync(x);
            return f(5).GetAwaiter().GetResult();
            """;

        Assert.Equal(10, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_lambda_with_no_parameters()
    {
        const string source = """
            Func<Task<int>> f = async () => await Service.GetAsync(7);
            return f().GetAwaiter().GetResult();
            """;

        Assert.Equal(14, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_lambda_with_a_block_body()
    {
        const string source = """
            Func<int, Task<int>> f = async x =>
            {
                var doubled = await Service.GetAsync(x);
                return doubled + 1;
            };
            return f(3).GetAwaiter().GetResult();
            """;

        Assert.Equal(7, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_lambda_returning_a_plain_Task()
    {
        const string source = """
            Func<Task> f = async () => await Service.NoResultAsync();
            f().GetAwaiter().GetResult();
            return Service.Calls;
            """;

        Assert.Equal(1, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_lambda_captures_its_enclosing_scope()
    {
        const string source = """
            var offset = 100;
            Func<int, Task<int>> f = async x => await Service.GetAsync(x) + offset;
            return f(1).GetAwaiter().GetResult();
            """;

        Assert.Equal(102, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task Several_async_lambdas_in_one_script()
    {
        const string source = """
            Func<int, Task<int>> a = async x => await Service.GetAsync(x);
            Func<int, Task<int>> b = async x => await Service.GetAsync(x) + 1;
            return await a(1) + await b(1);
            """;

        Assert.Equal(2 + 3, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_local_function()
    {
        const string source = """
            async Task<int> Twice(int n) => await Service.GetAsync(n);
            return Twice(6).GetAwaiter().GetResult();
            """;

        Assert.Equal(12, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public async Task An_async_lambda_that_genuinely_suspends()
    {
        const string source = """
            Func<int, Task<int>> f = async x => await Service.DelayedAsync(x, 1);
            return await f(9);
            """;

        Assert.Equal(9, await RunAsync<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void An_async_lambda_propagates_exceptions_through_its_task()
    {
        const string source = """
            Func<Task<int>> f = async () => await Service.ThrowingAsync();
            try
            {
                return f().GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
            """;

        Assert.Equal(-1, Run<AsyncGlobals, int>(source, new AsyncGlobals()));
    }

    [Fact]
    public void A_sync_lambda_still_rejects_await() =>
        AssertError<AsyncGlobals, int>(
            "Func<int, int> f = x => await Service.GetAsync(x); return f(1);",
            ErrorCode.AwaitInLambda,
            async: true);

    [Fact]
    public void An_async_lambda_must_return_a_task() =>
        AssertError<AsyncGlobals, int>(
            "Func<int, int> f = async x => 1; return f(1);",
            ErrorCode.CannotConvert);

    [Fact]
    public void A_name_called_async_still_works_as_a_variable()
    {
        Assert.Equal(3, Eval<int>("var async = 3; return async;"));
    }
}
