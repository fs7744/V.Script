using V.Script.Diagnostics;

namespace V.Script.Tests;

/// <summary>
/// What the base library does with the async half of the grammar when <c>V.Script.Async</c> is
/// not present.
/// </summary>
/// <remarks>
/// It still <em>parses</em> <c>await</c> and <c>async</c> — they are part of the language, and a
/// split that made them syntax errors would give confusing messages. What it cannot do is compile
/// them, because that needs runtime-async. Every one of these must be a clean diagnostic rather
/// than a crash: the binder holds no async support, and anything that reached the emitter
/// expecting one would fail far less legibly.
/// </remarks>
public sealed class WithoutAsyncExtensionTests : ScriptTest
{
    [Fact]
    public void Await_in_a_script_is_reported()
    {
        AssertError<AsyncGlobals, int>(
            "await Service.GetAsync(1)", ErrorCode.AwaitInSynchronousScript);
    }

    [Fact]
    public void Await_inside_a_lambda_is_reported()
    {
        AssertError<AsyncGlobals, int>(
            "Func<int> f = () => await Service.GetAsync(1); return f();",
            ErrorCode.AwaitInLambda);
    }

    [Fact]
    public void An_async_lambda_is_reported()
    {
        AssertError<AsyncGlobals, int>(
            "Func<Task<int>> f = async () => 1; return 0;",
            ErrorCode.AsyncNotAvailable);
    }

    [Fact]
    public void An_async_lambda_with_an_await_inside_reports_once_and_does_not_crash()
    {
        // The guard has to fire before the body is bound: binding it would look for async
        // support that is not there.
        AssertError<AsyncGlobals, int>(
            "Func<Task<int>> f = async () => await Service.GetAsync(1); return 0;",
            ErrorCode.AsyncNotAvailable);
    }

    [Fact]
    public void An_async_local_function_is_reported()
    {
        AssertError<AsyncGlobals, int>(
            "async Task<int> F() => 1; return 0;",
            ErrorCode.AsyncNotAvailable);
    }

    [Fact]
    public void A_delegate_returning_Task_points_at_the_extension()
    {
        using var engine = new ScriptEngine(Options);

        var error = Assert.Throws<ArgumentException>(
            () => engine.CompileDelegate<Func<int, Task<int>>>("a", "a"));

        Assert.Contains("V.Script.Async", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Everything that does not suspend is unaffected, which is the point of the split.</summary>
    [Fact]
    public void Ordinary_scripts_are_untouched()
    {
        Assert.Equal(7, Run<NumberGlobals, int>("A + B", new NumberGlobals { A = 3, B = 4 }));
    }
}
