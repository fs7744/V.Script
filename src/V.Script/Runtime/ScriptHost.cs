using System.ComponentModel;
using System.Reflection.Emit;

namespace V.Script;

/// <summary>
/// The object bound as the first argument of every compiled delegate. It gives generated IL
/// access to the table of compiled lambdas.
/// </summary>
/// <remarks>
/// Part of the generated-code contract rather than the user-facing API.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptHost
{
    private LambdaEntry[] _lambdas = [];

    internal ScriptHost(string sourceName)
    {
        SourceName = sourceName;
        EmptyClosure = new ScriptClosure(this, null, 0);
    }

    public string SourceName { get; }

    /// <summary>
    /// Handed to lambdas that capture nothing. A lambda method always receives a closure, even
    /// an empty one, so that a nested lambda can still reach the host through it.
    /// </summary>
    public ScriptClosure EmptyClosure { get; }

    /// <summary>
    /// Registers the lambdas the emitter produced. A lambda is a separate
    /// <see cref="DynamicMethod"/> whose first parameter is its <see cref="ScriptClosure"/>;
    /// generated code cannot reference one with <c>ldftn</c>, so it asks the host to build the
    /// delegate instead.
    /// </summary>
    internal void SetLambdas(LambdaEntry[] lambdas) => _lambdas = lambdas;

    internal readonly record struct LambdaEntry(
        DynamicMethod Method,
        Type DelegateType,
        Delegate? Shared,
        Func<ScriptClosure, Delegate>? Factory);

    /// <summary>Called by generated IL for a lambda that captures nothing; the delegate is built once.</summary>
    public Delegate GetLambda(int index) => _lambdas[index].Shared!;

    /// <summary>Called by generated IL for a lambda that captures enclosing variables.</summary>
    public Delegate BindLambda(int index, ScriptClosure closure)
    {
        var entry = _lambdas[index];

        // The factory is a pre-built open delegate; binding is then one small allocation rather
        // than a fresh CreateDelegate, which costs a couple of hundred nanoseconds per call.
        return entry.Factory is not null
            ? entry.Factory(closure)
            : entry.Method.CreateDelegate(entry.DelegateType, closure);
    }
}
