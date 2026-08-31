using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace V.Script;

/// <summary>
/// Helpers that generated code calls for behaviour that has no direct IL form.
/// </summary>
/// <remarks>Part of the generated-code contract rather than the user-facing API.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ScriptOperations
{
    /// <summary>
    /// Raised when no arm of a <c>switch</c> expression matched. Written as a method returning
    /// <typeparamref name="T"/> so that it can sit in the value position of the conditional
    /// chain the binder lowers a switch expression into.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T NoMatchingSwitchArm<T>(object? value) => throw new SwitchExpressionException(value);
}
