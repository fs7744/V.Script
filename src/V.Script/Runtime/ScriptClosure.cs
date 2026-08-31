using System.ComponentModel;

namespace V.Script;

/// <summary>
/// Holds the variables a lambda captured from its enclosing scope. One instance exists per
/// entry into a scope that declares captured variables, so a loop body produces a fresh
/// instance per iteration — which is what gives <c>foreach</c> variables per-iteration capture.
/// </summary>
/// <remarks>
/// Capture is by reference: the enclosing method reads and writes the same slots the lambda
/// does, so mutating a captured variable is visible on both sides, as in C#.
/// Part of the generated-code contract rather than the user-facing API.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptClosure
{
    public ScriptClosure(ScriptHost host, ScriptClosure? parent, int size)
    {
        Host = host;
        Parent = parent;
        Values = size == 0 ? [] : new object?[size];
    }

    /// <summary>
    /// The owning host. A lambda method receives its closure as argument 0, so this is how a
    /// nested lambda reaches the host in order to build its own delegate.
    /// </summary>
    public ScriptHost Host { get; }

    /// <summary>The closure of the nearest enclosing scope that also captured something.</summary>
    public ScriptClosure? Parent { get; }

    /// <summary>
    /// Captured values, boxed. Generated IL indexes this directly; the slot layout is decided
    /// by the binder.
    /// </summary>
    public object?[] Values { get; }
}
