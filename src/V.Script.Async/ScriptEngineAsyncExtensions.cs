using System.Runtime.CompilerServices;
using V.Script.Async;
using V.Script.Binding;

namespace V.Script;

/// <summary>
/// Compiling scripts that can suspend. Adding this library to a project is all it takes:
/// <c>await</c> becomes compilable, and the engine's synchronous API is unchanged.
/// </summary>
public static class ScriptEngineAsyncExtensions
{
    /// <summary>
    /// Support for engines whose options did not install any. An asynchronous compile is this
    /// library's own work, so it can supply what it needs rather than making callers opt in;
    /// only an <c>async</c> lambda in a <em>synchronous</em> script needs
    /// <see cref="ScriptOptionsAsyncExtensions.WithAsync"/>, because the base library has to know
    /// at bind time.
    /// </summary>
    private static readonly ConditionalWeakTable<ScriptEngine, AsyncSupport> Implicit = new();

    private static IAsyncSupport SupportFor(ScriptEngine engine) =>
        engine.AsyncSupport
        ?? Implicit.GetValue(engine, static _ => new AsyncSupport(new GeneratedAssemblyPool(1)));

    /// <summary>
    /// Compiles an asynchronous script. <c>await</c> is allowed anywhere except inside a
    /// <c>catch</c> or <c>finally</c> block, which the runtime cannot support.
    /// </summary>
    /// <exception cref="ScriptCompilationException">Binding produced errors.</exception>
    public static AsyncScript<TGlobals, TResult> CompileAsync<TGlobals, TResult>(
        this ScriptEngine engine, string source) =>
        engine.TryCompileAsync<TGlobals, TResult>(source).GetScriptOrThrow();

    /// <summary>Compiles an asynchronous script, returning diagnostics instead of throwing.</summary>
    public static CompileResult<AsyncScript<TGlobals, TResult>> TryCompileAsync<TGlobals, TResult>(
        this ScriptEngine engine, string source)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);
        engine.ThrowIfDisposed();

        var key = new ScriptEngine.CacheKey(source, "async", typeof(TGlobals), typeof(TResult), null);
        if (engine.Cache.TryGetValue(key, out var cached))
            return CompileResult<AsyncScript<TGlobals, TResult>>.Ok(
                (AsyncScript<TGlobals, TResult>)cached, cached.Diagnostics);

        ScriptParameter[] parameters = [new("<globals>", typeof(TGlobals), IlIndex: 1, IsGlobals: true)];

        var support = SupportFor(engine);

        var (bound, diagnostics) = engine.Bind(source, parameters, typeof(TResult), isAsync: true, support);
        if (bound is null) return CompileResult<AsyncScript<TGlobals, TResult>>.Failed(diagnostics);

        var host = new ScriptHost(ScriptEngine.Describe(source));

        var (invoke, owner) = AsyncScriptCarrier.Compile(
            bound,
            typeof(Func<TGlobals, Task<TResult>>),
            [typeof(TGlobals)],
            typeof(TResult),
            host,
            host.SourceName,
            support);

        var script = new AsyncScript<TGlobals, TResult>(
            source,
            (Func<TGlobals, Task<TResult>>)invoke,
            owner,
            diagnostics,
            () => engine.Cache.TryRemove(key, out _));

        engine.Cache[key] = script;
        return CompileResult<AsyncScript<TGlobals, TResult>>.Ok(script, diagnostics);
    }

    /// <summary>
    /// Compiles an asynchronous script directly into <typeparamref name="TDelegate"/>, whose
    /// return type must be <see cref="Task"/> or <see cref="Task{TResult}"/>. The result is
    /// disposable because it owns generated code.
    /// </summary>
    /// <exception cref="ScriptCompilationException">Binding produced errors.</exception>
    public static ScriptDelegate<TDelegate> CompileAsyncDelegate<TDelegate>(
        this ScriptEngine engine, string source, params string[] parameters)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parameters);
        engine.ThrowIfDisposed();

        var invokeMethod = ScriptEngine.GetInvokeMethod<TDelegate>(parameters);

        if (!ScriptEngine.IsAwaitableReturn(invokeMethod.ReturnType))
        {
            throw new ArgumentException(
                $"{typeof(TDelegate).Name} 必须返回 Task 或 Task<T>。", nameof(TDelegate));
        }

        var ilReturnType = invokeMethod.ReturnType.IsGenericType
            ? invokeMethod.ReturnType.GetGenericArguments()[0]
            : typeof(void);

        var support = SupportFor(engine);

        var (bound, diagnostics, host, parameterTypes) =
            engine.BindDelegate(source, parameters, invokeMethod, ilReturnType, isAsync: true, support);

        if (bound is null) throw new ScriptCompilationException(diagnostics);

        var (invoke, owner) = AsyncScriptCarrier.Compile(
            bound, typeof(TDelegate), parameterTypes, ilReturnType, host, host.SourceName, support);

        return new ScriptDelegate<TDelegate>((TDelegate)invoke, owner, diagnostics);
    }
}
