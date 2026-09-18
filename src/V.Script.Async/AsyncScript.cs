using V.Script.Diagnostics;

namespace V.Script;

/// <summary>A compiled asynchronous script.</summary>
/// <remarks>
/// Disposable because it owns a slice of a generated assembly; see
/// <see cref="ScriptOptionsAsyncExtensions.WithAsync"/> for what disposing actually reclaims.
/// </remarks>
public sealed class AsyncScript<TGlobals, TResult> : ICompiledScript
{
    private readonly Func<TGlobals, Task<TResult>> _invoke;
    private readonly IDisposable? _owner;
    private readonly Action? _onDispose;
    private int _disposed;

    internal AsyncScript(
        string source,
        Func<TGlobals, Task<TResult>> invoke,
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

    /// <summary>Runs the script and returns its task directly.</summary>
    public Task<TResult> RunAsync(TGlobals globals)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _invoke(globals);
    }

    /// <summary>The compiled delegate itself, for callers that want to skip even the disposed check.</summary>
    public Func<TGlobals, Task<TResult>> Delegate => _invoke;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _onDispose?.Invoke();
        _owner?.Dispose();
    }
}

/// <summary>
/// A script compiled straight into a delegate type, together with the generated code it owns.
/// </summary>
/// <remarks>
/// Only the asynchronous form needs this wrapper: a synchronous script compiles into a
/// <c>DynamicMethod</c> that the garbage collector reclaims along with its delegate,
/// whereas this one holds a slice of a generated assembly that has to be released explicitly.
/// </remarks>
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

    public TDelegate Value { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner?.Dispose();
    }
}
