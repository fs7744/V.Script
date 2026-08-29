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
    private readonly Func<CancellationToken, TGlobals, TResult> _invoke;
    private readonly IDisposable? _owner;
    private readonly Action? _onDispose;
    private int _disposed;

    internal Script(
        string source,
        Func<CancellationToken, TGlobals, TResult> invoke,
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

    /// <summary>Runs the script. Throws <see cref="ScriptTimeoutException"/> or
    /// <see cref="ScriptBudgetExceededException"/> when a configured limit is hit.</summary>
    public TResult Run(TGlobals globals, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _invoke(cancellationToken, globals);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _onDispose?.Invoke();
        _owner?.Dispose();
    }
}

/// <summary>
/// A compiled asynchronous script. Its code lives in a dedicated collectible assembly, so
/// disposing this instance releases that one script's memory without affecting the others.
/// </summary>
public sealed class AsyncScript<TGlobals, TResult> : ICompiledScript
{
    private readonly Func<CancellationToken, TGlobals, Task<TResult>> _invoke;
    private readonly IDisposable? _owner;
    private readonly Action? _onDispose;
    private readonly ScriptLimits _limits;
    private int _disposed;

    internal AsyncScript(
        string source,
        Func<CancellationToken, TGlobals, Task<TResult>> invoke,
        IDisposable? owner,
        IReadOnlyList<Diagnostic> diagnostics,
        ScriptLimits limits,
        Action? onDispose)
    {
        Source = source;
        _invoke = invoke;
        _owner = owner;
        Diagnostics = diagnostics;
        _limits = limits;
        _onDispose = onDispose;
    }

    public string Source { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Runs the script. A configured timeout is enforced by cancelling the token the script
    /// threads into every <c>await</c>, so a suspended script is interrupted rather than
    /// waiting forever for a checkpoint it will never reach.
    /// </summary>
    public async ValueTask<TResult> RunAsync(TGlobals globals, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_limits.Timeout is not { } timeout)
            return await _invoke(cancellationToken, globals).ConfigureAwait(false);

        // Linking is only needed when the caller actually supplied a cancellable token;
        // a plain timer source is measurably cheaper for the common default(CancellationToken).
        using var timer = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        timer.CancelAfter(timeout);

        try
        {
            return await _invoke(timer.Token, globals).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScriptTimeoutException(timeout);
        }
    }

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
