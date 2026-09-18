using System.Reflection;
using System.Runtime.CompilerServices;
using V.Script.Tests;

namespace V.Script.Async.Tests;

/// <summary>
/// What an asynchronous script owns, and when it lets go. Every one of these is about generated
/// assemblies, which only this library produces.
/// </summary>
public sealed class AsyncLifetimeTests : AsyncScriptTest
{
    /// <summary>
    /// Each asynchronous script owns a collectible assembly. Retiring one must make its
    /// generated type unreachable, otherwise a service that hot-reloads rules grows without
    /// bound. Reachability is asserted directly rather than by watching process memory, which
    /// is far too noisy a signal to rely on.
    /// </summary>
    [Fact]
    public void Retiring_an_async_script_makes_its_generated_type_collectable()
    {
        var (weakType, weakAssembly) = CompileAndRetire();

        for (var attempt = 0; attempt < 10 && (weakType.IsAlive || weakAssembly.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakType.IsAlive, "生成的类型仍然可达，程序集没有被卸载。");
        Assert.False(weakAssembly.IsAlive, "生成的程序集仍然可达。");
    }

    /// <summary>
    /// Kept in its own non-inlined frame so the strong references genuinely go out of scope
    /// before the caller collects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Type, WeakReference Assembly) CompileAndRetire()
    {
        using var engine = new ScriptEngine(Options);

        var script = engine.CompileAsync<AsyncGlobals, int>("Seed + 1");
        var generated = script.Delegate.Method.DeclaringType!;

        var weakType = new WeakReference(generated);
        var weakAssembly = new WeakReference(generated.Assembly);

        script.Dispose();
        return (weakType, weakAssembly);
    }

    /// <summary>A script that is still in use must of course stay loaded.</summary>
    [Fact]
    public void A_live_async_script_is_not_collected()
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.CompileAsync<AsyncGlobals, int>("Seed + 2");

        var weakType = new WeakReference(script.Delegate.Method.DeclaringType!);

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        Assert.True(weakType.IsAlive);
    }

    /// <summary>
    /// Several scripts can be asked to share one generated assembly, which is what makes an
    /// asynchronous compile cheap in a batch. The scripts themselves must not notice.
    /// </summary>
    [Fact]
    public async Task Scripts_may_share_one_generated_assembly()
    {
        using var engine = new ScriptEngine(Options.WithAsync(4));

        using var first = engine.CompileAsync<AsyncGlobals, int>("Seed + 1");
        using var second = engine.CompileAsync<AsyncGlobals, int>("Seed + 2");
        using var third = engine.CompileAsync<AsyncGlobals, int>("Seed + 3");

        Assert.Same(AssemblyOf(first), AssemblyOf(second));
        Assert.Same(AssemblyOf(second), AssemblyOf(third));

        var globals = new AsyncGlobals { Seed = 10 };
        Assert.Equal(11, await first.RunAsync(globals));
        Assert.Equal(12, await second.RunAsync(globals));
        Assert.Equal(13, await third.RunAsync(globals));
    }

    [Fact]
    public void By_default_each_script_still_gets_its_own_assembly()
    {
        using var engine = new ScriptEngine(Options);

        using var first = engine.CompileAsync<AsyncGlobals, int>("Seed + 1");
        using var second = engine.CompileAsync<AsyncGlobals, int>("Seed + 2");

        Assert.NotSame(AssemblyOf(first), AssemblyOf(second));
    }

    /// <summary>Retiring one script must not pull the code out from under its siblings.</summary>
    [Fact]
    public async Task Retiring_one_script_leaves_the_others_in_its_assembly_working()
    {
        using var engine = new ScriptEngine(Options.WithAsync(3));

        var first = engine.CompileAsync<AsyncGlobals, int>("Seed + 1");
        using var second = engine.CompileAsync<AsyncGlobals, int>("Seed + 2");

        first.Dispose();

        Assert.Equal(12, await second.RunAsync(new AsyncGlobals { Seed = 10 }));
    }

    /// <summary>
    /// A full generation is retired straight away, so once its scripts are gone nothing holds
    /// it — not even the engine that owns the pool. The engine is deliberately kept alive across
    /// the assertion: if the pool still referenced the generation, this would fail.
    /// </summary>
    [Fact]
    public void A_shared_assembly_unloads_once_every_script_in_it_is_retired()
    {
        using var engine = new ScriptEngine(Options.WithAsync(2));

        var weakAssembly = CompileAndRetirePair(engine);

        for (var attempt = 0; attempt < 10 && weakAssembly.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakAssembly.IsAlive, "共享程序集里的脚本都已释放，程序集仍未卸载。");
    }

    /// <summary>Its own frame, so the two scripts are unreachable by the time the caller collects.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompileAndRetirePair(ScriptEngine engine)
    {
        var first = engine.CompileAsync<AsyncGlobals, int>("Seed + 1");
        var second = engine.CompileAsync<AsyncGlobals, int>("Seed + 2");

        var weakAssembly = new WeakReference(AssemblyOf(first));

        first.Dispose();
        second.Dispose();

        return weakAssembly;
    }

    private static Assembly AssemblyOf<TGlobals, TResult>(AsyncScript<TGlobals, TResult> script) =>
        script.Delegate.Method.DeclaringType!.Assembly;

    [Fact]
    public void A_generated_assembly_must_hold_at_least_one_script()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Options.WithAsync(0));
    }
}
