using V.Script.Diagnostics;
using V.Script.Tests;

namespace V.Script.Async.Tests;

/// <summary>
/// Where the two halves meet: what referencing this library gives you for free, and what still
/// has to be asked for.
/// </summary>
public sealed class AsyncOptInTests
{
    private static ScriptOptions Plain { get; } = ScriptOptions.Default
        .AddReferencesFrom(typeof(Order), typeof(AsyncOptInTests))
        .AddImports("V.Script.Tests");

    [Fact]
    public async Task CompileAsync_needs_no_opt_in()
    {
        // An asynchronous compile is this library's own work, so it supplies its own support.
        using var engine = new ScriptEngine(Plain);
        using var script = engine.CompileAsync<AsyncGlobals, int>("await Service.CompletedAsync(Seed)");

        Assert.Equal(4, await script.RunAsync(new AsyncGlobals { Seed = 3 }));
    }

    [Fact]
    public void An_async_lambda_in_a_synchronous_script_needs_WithAsync()
    {
        // The base library decides at bind time whether it can compile one, so referencing this
        // library is not enough on its own — the options have to say so.
        using var engine = new ScriptEngine(Plain);

        var diagnostics = engine
            .TryCompile<AsyncGlobals, int>("Func<Task<int>> f = async () => 1; return 0;")
            .Diagnostics;

        Assert.Contains(diagnostics, d => d.Id == ErrorCode.AsyncNotAvailable);
    }

    [Fact]
    public void WithAsync_enables_an_async_lambda_in_a_synchronous_script()
    {
        using var engine = new ScriptEngine(Plain.WithAsync());

        var result = engine.TryCompile<AsyncGlobals, int>(
            "Func<Task<int>> f = async () => 1; return f().Result;");

        Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
    }

    [Fact]
    public void WithAsync_rejects_a_batch_smaller_than_one_script()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plain.WithAsync(0));
    }
}
