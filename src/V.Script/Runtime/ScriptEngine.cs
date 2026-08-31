using System.Collections.Concurrent;
using System.Reflection;
using V.Script.Binding;
using V.Script.Diagnostics;
using V.Script.Emit;
using V.Script.Syntax;

namespace V.Script;

/// <summary>
/// Compiles scripts into delegates. Instances are thread-safe and cache compiled scripts by
/// source, so the same rule text compiles once no matter how often it is requested.
/// </summary>
/// <remarks>
/// The engine owns every script it hands out. Disposing a script releases just that script
/// (an asynchronous one unloads its own generated assembly); disposing the engine releases all.
/// </remarks>
public sealed class ScriptEngine : IDisposable
{
    private readonly ScriptOptions _options;
    private readonly TypeResolver _resolver;
    private readonly ConcurrentDictionary<CacheKey, ICompiledScript> _cache = new();
    private int _disposed;

    public ScriptEngine() : this(ScriptOptions.Default) { }

    public ScriptEngine(ScriptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _resolver = new TypeResolver(options.References, options.Imports);
    }

    public ScriptOptions Options => _options;

    // ============================================================ synchronous

    /// <summary>
    /// Compiles a synchronous script whose bare identifiers resolve against
    /// <typeparamref name="TGlobals"/>'s public instance members.
    /// </summary>
    /// <exception cref="ScriptCompilationException">Binding produced errors.</exception>
    public Script<TGlobals, TResult> Compile<TGlobals, TResult>(string source) =>
        TryCompile<TGlobals, TResult>(source).GetScriptOrThrow();

    /// <summary>Compiles a synchronous script, returning diagnostics instead of throwing.</summary>
    public CompileResult<Script<TGlobals, TResult>> TryCompile<TGlobals, TResult>(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var key = new CacheKey(source, "sync", typeof(TGlobals), typeof(TResult), null);
        if (_cache.TryGetValue(key, out var cached))
            return CompileResult<Script<TGlobals, TResult>>.Ok(
                (Script<TGlobals, TResult>)cached, cached.Diagnostics);

        ScriptParameter[] parameters = [new("<globals>", typeof(TGlobals), IlIndex: 1, IsGlobals: true)];

        var (bound, diagnostics) = Bind(source, parameters, typeof(TResult), isAsync: false);
        if (bound is null) return CompileResult<Script<TGlobals, TResult>>.Failed(diagnostics);

        var host = new ScriptHost(Describe(source));

        var (invoke, owner) = ScriptCarrier.CompileSynchronous(
            bound,
            typeof(Func<TGlobals, TResult>),
            [typeof(TGlobals)],
            typeof(TResult),
            host,
            host.SourceName);

        var script = new Script<TGlobals, TResult>(
            source,
            (Func<TGlobals, TResult>)invoke,
            owner,
            diagnostics,
            () => _cache.TryRemove(key, out _));

        _cache[key] = script;
        return CompileResult<Script<TGlobals, TResult>>.Ok(script, diagnostics);
    }

    // ============================================================ asynchronous

    /// <summary>
    /// Compiles an asynchronous script. <c>await</c> is allowed anywhere except inside a
    /// <c>catch</c> or <c>finally</c> block, which the runtime cannot support.
    /// </summary>
    /// <exception cref="ScriptCompilationException">Binding produced errors.</exception>
    public AsyncScript<TGlobals, TResult> CompileAsync<TGlobals, TResult>(string source) =>
        TryCompileAsync<TGlobals, TResult>(source).GetScriptOrThrow();

    /// <summary>Compiles an asynchronous script, returning diagnostics instead of throwing.</summary>
    public CompileResult<AsyncScript<TGlobals, TResult>> TryCompileAsync<TGlobals, TResult>(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var key = new CacheKey(source, "async", typeof(TGlobals), typeof(TResult), null);
        if (_cache.TryGetValue(key, out var cached))
            return CompileResult<AsyncScript<TGlobals, TResult>>.Ok(
                (AsyncScript<TGlobals, TResult>)cached, cached.Diagnostics);

        ScriptParameter[] parameters = [new("<globals>", typeof(TGlobals), IlIndex: 1, IsGlobals: true)];

        var (bound, diagnostics) = Bind(source, parameters, typeof(TResult), isAsync: true);
        if (bound is null) return CompileResult<AsyncScript<TGlobals, TResult>>.Failed(diagnostics);

        var host = new ScriptHost(Describe(source));

        var (invoke, owner) = ScriptCarrier.CompileAsynchronous(
            bound,
            typeof(Func<TGlobals, Task<TResult>>),
            [typeof(TGlobals)],
            typeof(TResult),
            host,
            host.SourceName);

        var script = new AsyncScript<TGlobals, TResult>(
            source,
            (Func<TGlobals, Task<TResult>>)invoke,
            owner,
            diagnostics,
            () => _cache.TryRemove(key, out _));

        _cache[key] = script;
        return CompileResult<AsyncScript<TGlobals, TResult>>.Ok(script, diagnostics);
    }

    // ============================================================ raw delegates

    /// <summary>
    /// Compiles a synchronous script directly into <typeparamref name="TDelegate"/>.
    /// Parameter names are supplied separately because delegate types do not carry them.
    /// </summary>
    /// <exception cref="ScriptCompilationException">Binding produced errors.</exception>
    public TDelegate CompileDelegate<TDelegate>(string source, params string[] parameters)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var invokeMethod = GetInvokeMethod<TDelegate>(parameters);

        if (IsAwaitableReturn(invokeMethod.ReturnType))
        {
            throw new ArgumentException(
                $"{typeof(TDelegate).Name} 返回 Task，请改用 {nameof(CompileAsyncDelegate)}，" +
                "以便按脚本释放生成的程序集。", nameof(TDelegate));
        }

        var (bound, diagnostics, host, parameterTypes) =
            BindDelegate(source, parameters, invokeMethod, invokeMethod.ReturnType, isAsync: false);

        if (bound is null) throw new ScriptCompilationException(diagnostics);

        var (invoke, _) = ScriptCarrier.CompileSynchronous(
            bound, typeof(TDelegate), parameterTypes, invokeMethod.ReturnType, host, host.SourceName);

        return (TDelegate)invoke;
    }

    /// <summary>
    /// Compiles an asynchronous script into <typeparamref name="TDelegate"/>, whose return type
    /// must be <see cref="Task"/> or <see cref="Task{TResult}"/>. The result is disposable
    /// because the generated assembly is owned per script.
    /// </summary>
    /// <exception cref="ScriptCompilationException">Binding produced errors.</exception>
    public ScriptDelegate<TDelegate> CompileAsyncDelegate<TDelegate>(string source, params string[] parameters)
        where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var invokeMethod = GetInvokeMethod<TDelegate>(parameters);

        if (!IsAwaitableReturn(invokeMethod.ReturnType))
        {
            throw new ArgumentException(
                $"{typeof(TDelegate).Name} 必须返回 Task 或 Task<T>。", nameof(TDelegate));
        }

        var ilReturnType = invokeMethod.ReturnType.IsGenericType
            ? invokeMethod.ReturnType.GetGenericArguments()[0]
            : typeof(void);

        var (bound, diagnostics, host, parameterTypes) =
            BindDelegate(source, parameters, invokeMethod, ilReturnType, isAsync: true);

        if (bound is null) throw new ScriptCompilationException(diagnostics);

        var (invoke, owner) = ScriptCarrier.CompileAsynchronous(
            bound, typeof(TDelegate), parameterTypes, ilReturnType, host, host.SourceName);

        return new ScriptDelegate<TDelegate>((TDelegate)invoke, owner, diagnostics);
    }

    private static MethodInfo GetInvokeMethod<TDelegate>(string[] parameters) where TDelegate : Delegate
    {
        var invokeMethod = typeof(TDelegate).GetMethod("Invoke")
            ?? throw new ArgumentException($"{typeof(TDelegate).Name} 不是有效的委托类型。", nameof(TDelegate));

        var declared = invokeMethod.GetParameters();
        if (declared.Length != parameters.Length)
        {
            throw new ArgumentException(
                $"{typeof(TDelegate).Name} 有 {declared.Length} 个参数，但提供了 {parameters.Length} 个参数名。",
                nameof(parameters));
        }

        return invokeMethod;
    }

    private static bool IsAwaitableReturn(Type type) =>
        type == typeof(Task) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>));

    private (BoundScript? Bound, IReadOnlyList<Diagnostic> Diagnostics, ScriptHost Host, Type[] ParameterTypes)
        BindDelegate(string source, string[] names, MethodInfo invokeMethod, Type returnType, bool isAsync)
    {
        var declared = invokeMethod.GetParameters();
        var parameterTypes = declared.Select(p => p.ParameterType).ToArray();

        var parameters = new ScriptParameter[names.Length];
        for (var i = 0; i < names.Length; i++)
            parameters[i] = new ScriptParameter(names[i], parameterTypes[i], IlIndex: i + 1, IsGlobals: false);

        var (bound, diagnostics) = Bind(source, parameters, returnType, isAsync);
        return (bound, diagnostics, new ScriptHost(Describe(source)), parameterTypes);
    }

    // ============================================================ pipeline

    private (BoundScript? Bound, IReadOnlyList<Diagnostic> Diagnostics) Bind(
        string source,
        IReadOnlyList<ScriptParameter> parameters,
        Type returnType,
        bool isAsync)
    {
        var diagnostics = new DiagnosticBag();

        var tokens = new Lexer(source, diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).ParseCompilationUnit();

        if (diagnostics.HasErrors) return (null, diagnostics.ToImmutable());

        var binder = new Binding.Binder(diagnostics, _resolver, parameters, returnType, isAsync);
        var bound = binder.BindScript(unit);

        return diagnostics.HasErrors
            ? (null, diagnostics.ToImmutable())
            : (bound, diagnostics.ToImmutable());
    }

    /// <summary>A short stable name derived from the source, used for the generated assembly.</summary>
    private static string Describe(string source)
    {
        var hash = (uint)string.GetHashCode(source, StringComparison.Ordinal);
        return $"S{hash:X8}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var script in _cache.Values.ToArray()) script.Dispose();
        _cache.Clear();
    }

    private readonly record struct CacheKey(
        string Source,
        string Kind,
        Type GlobalsType,
        Type ResultType,
        string? Extra);
}

/// <summary>
/// A compiled asynchronous delegate together with ownership of the assembly holding its code.
/// Dispose it when the script is retired to release that memory.
/// </summary>
public sealed class ScriptDelegate<TDelegate> : IDisposable where TDelegate : Delegate
{
    private readonly IDisposable? _owner;
    private int _disposed;

    internal ScriptDelegate(TDelegate value, IDisposable? owner, IReadOnlyList<Diagnostic> diagnostics)
    {
        Value = value;
        _owner = owner;
        Diagnostics = diagnostics;
    }

    /// <summary>The compiled delegate. Thread-safe and reusable.</summary>
    public TDelegate Value { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner?.Dispose();
    }
}
