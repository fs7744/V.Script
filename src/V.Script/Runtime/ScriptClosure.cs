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
/// <para>
/// The slots themselves live in a derived class. A script's closure layout is only known once
/// binding finishes, and the synchronous carrier has no module to emit a layout type into, so
/// the layouts are pre-declared here as generics: <see cref="ScriptClosure{T0}"/> and friends
/// give each captured variable a field of its own type. Beyond the arity they cover,
/// <see cref="ArrayClosure"/> takes over and stores everything boxed.
/// </para>
/// <para>
/// Part of the generated-code contract rather than the user-facing API.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ScriptClosure(ScriptHost host, ScriptClosure? parent)
{
    /// <summary>The largest number of captured variables a typed layout exists for.</summary>
    /// <remarks>
    /// A scope that captures more than this falls back to <see cref="ArrayClosure"/>. Four
    /// covers what scripts actually do — the globals object plus a couple of locals — and every
    /// step past it is another generic instantiation for the runtime to JIT.
    /// </remarks>
    public const int MaxTypedSlots = 4;

    /// <summary>
    /// The owning host. A lambda method receives its closure as argument 0, so this is how a
    /// nested lambda reaches the host in order to build its own delegate.
    /// </summary>
    public ScriptHost Host { get; } = host;

    /// <summary>The closure of the nearest enclosing scope that also captured something.</summary>
    public ScriptClosure? Parent { get; } = parent;
}

/// <summary>Fallback layout for a scope that captures more variables than there are typed slots.</summary>
/// <remarks>
/// Value types are boxed on the way in and unboxed on the way out, and a slot has no address,
/// so a variable stored here cannot be passed by reference.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ArrayClosure : ScriptClosure
{
    public ArrayClosure(ScriptHost host, ScriptClosure? parent, int size)
        : base(host, parent) => Values = size == 0 ? [] : new object?[size];

    /// <summary>Captured values, boxed. Generated IL indexes this directly.</summary>
    public object?[] Values { get; }
}

/// <summary>Typed layout for a scope that captures one variable.</summary>
/// <remarks>
/// The slots are fields rather than properties on purpose: generated IL reads them with
/// <c>ldfld</c>, writes them with <c>stfld</c>, and can take their address with <c>ldflda</c>.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptClosure<T0>(ScriptHost host, ScriptClosure? parent)
    : ScriptClosure(host, parent)
{
    public T0 Slot0 = default!;
}

/// <summary>Typed layout for a scope that captures two variables.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptClosure<T0, T1>(ScriptHost host, ScriptClosure? parent)
    : ScriptClosure(host, parent)
{
    public T0 Slot0 = default!;
    public T1 Slot1 = default!;
}

/// <summary>Typed layout for a scope that captures three variables.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptClosure<T0, T1, T2>(ScriptHost host, ScriptClosure? parent)
    : ScriptClosure(host, parent)
{
    public T0 Slot0 = default!;
    public T1 Slot1 = default!;
    public T2 Slot2 = default!;
}

/// <summary>Typed layout for a scope that captures four variables.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptClosure<T0, T1, T2, T3>(ScriptHost host, ScriptClosure? parent)
    : ScriptClosure(host, parent)
{
    public T0 Slot0 = default!;
    public T1 Slot1 = default!;
    public T2 Slot2 = default!;
    public T3 Slot3 = default!;
}
