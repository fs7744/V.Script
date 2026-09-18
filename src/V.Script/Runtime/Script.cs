using V.Script.Diagnostics;

namespace V.Script;

/// <summary>Common surface for the two compiled script shapes.</summary>
public interface ICompiledScript : IDisposable
{
    /// <summary>The source this script was compiled from.</summary>
    string Source { get; }

    /// <summary>Warnings produced while compiling. Errors would have prevented compilation.</summary>
    IReadOnlyList<Diagnostic> Diagnostics { get; }
}

/// <summary>
/// A compiled synchronous script. The delegate is thread-safe and may be invoked concurrently;
/// scripts hold no mutable state of their own between calls.
/// </summary>
public sealed class Script<TGlobals, TResult> : ICompiledScript
{
    private readonly Func<TGlobals, TResult> _invoke;
    private readonly IDisposable? _owner;
    private readonly Action? _onDispose;
    private int _disposed;

    internal Script(
        string source,
        Func<TGlobals, TResult> invoke,
        IDisposable? owner,
        IReadOnlyList<Diagnostic> diagnostics,
        Action? onDispose)
    {
        Source = source;
        _invoke = invoke;
        _owner = owner;
        Diagnostics = diagnostics;
        _onDispose = onDispose;
    }

    public string Source { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Runs the script. This is a direct delegate call — nothing is wrapped around it.</summary>
    public TResult Run(TGlobals globals)
    {
        Guard.NotDisposed(_disposed != 0, this);
        return _invoke(globals);
    }

    /// <summary>The compiled delegate itself, for callers that want to skip even the disposed check.</summary>
    public Func<TGlobals, TResult> Delegate => _invoke;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _onDispose?.Invoke();
        _owner?.Dispose();
    }
}

/// <summary>The outcome of a <c>TryCompile</c> call: either a script or the diagnostics explaining why not.</summary>
public sealed class CompileResult<TScript> where TScript : class, ICompiledScript
{
    private CompileResult(TScript? script, IReadOnlyList<Diagnostic> diagnostics)
    {
        Script = script;
        Diagnostics = diagnostics;
    }

    public TScript? Script { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool Success => Script is not null;

    /// <summary>Errors only, in source order.</summary>
    public IEnumerable<Diagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    internal static CompileResult<TScript> Ok(TScript script, IReadOnlyList<Diagnostic> diagnostics) =>
        new(script, diagnostics);

    internal static CompileResult<TScript> Failed(IReadOnlyList<Diagnostic> diagnostics) =>
        new(null, diagnostics);

    /// <summary>Returns the script, or throws <see cref="ScriptCompilationException"/>.</summary>
    public TScript GetScriptOrThrow() =>
        Script ?? throw new ScriptCompilationException(Diagnostics);
}
