using V.Script.Tests;

namespace V.Script.Async.Tests;

/// <summary>
/// The engine no longer manages cancellation, so these pin the two host-side routes that
/// replace it: a token on the globals object, and a token as an ordinary delegate parameter.
/// </summary>
public sealed class CancellationTests : AsyncScriptTest
{
    [Fact]
    public async Task Token_passed_through_globals_reaches_the_awaited_call()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        var globals = new CancellableGlobals { Token = cts.Token };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync<CancellableGlobals, int>("await Service.WaitForeverAsync(Token)", globals));
    }

    [Fact]
    public async Task Token_through_globals_is_per_invocation()
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.CompileAsync<CancellableGlobals, int>(
            "await Service.EchoAsync(7, Token)");

        Assert.Equal(7, await script.RunAsync(new CancellableGlobals { Token = CancellationToken.None }));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => script.RunAsync(new CancellableGlobals { Token = cancelled.Token }));
    }

    [Fact]
    public async Task Token_as_a_delegate_parameter()
    {
        using var engine = new ScriptEngine(Options);
        using var compiled = engine.CompileAsyncDelegate<Func<CancellableService, CancellationToken, Task<int>>>(
            "await svc.EchoAsync(42, ct)", "svc", "ct");

        Assert.Equal(42, await compiled.Value(new CancellableService(), CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => compiled.Value(new CancellableService(), cancelled.Token));
    }

    [Fact]
    public void Token_works_in_a_synchronous_delegate_too()
    {
        using var engine = new ScriptEngine(Options);
        var f = engine.CompileDelegate<Func<CancellationToken, bool>>(
            "ct.IsCancellationRequested", "ct");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.False(f(CancellationToken.None));
        Assert.True(f(cancelled.Token));
    }
}
