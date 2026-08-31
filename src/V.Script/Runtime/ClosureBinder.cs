using System.Reflection;
using System.Reflection.Emit;

namespace V.Script;

/// <summary>
/// Turns a lambda's generated method into a delegate bound to a live closure.
/// </summary>
/// <remarks>
/// The obvious implementation, calling <c>DynamicMethod.CreateDelegate(type, closure)</c> on
/// every evaluation, costs several hundred nanoseconds — enough to dominate a rule that uses a
/// capturing lambda. Instead the open delegate (closure as its first argument) is built once at
/// compile time, and binding becomes an ordinary C# closure allocation.
/// <para>
/// Only <see cref="Func{TResult}"/> and <see cref="Action"/> shapes up to three parameters are
/// specialised; anything else falls back to <c>CreateDelegate</c>, which is always correct.
/// </para>
/// </remarks>
internal static class ClosureBinder
{
    /// <summary>
    /// Builds a factory that produces the target delegate for a given closure, or returns null
    /// when the delegate shape has no specialisation.
    /// </summary>
    public static Func<ScriptClosure, Delegate>? TryCreateFactory(DynamicMethod method, Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke");
        if (invoke is null) return null;

        var parameters = invoke.GetParameters();
        if (parameters.Length > 3) return null;
        if (parameters.Any(p => p.ParameterType.IsByRef)) return null;

        var isAction = invoke.ReturnType == typeof(void);

        // Only the exact Func/Action shapes are specialised; a custom delegate type would need
        // its own binder and is rare enough not to be worth one.
        if (!IsExactShape(delegateType, parameters, invoke.ReturnType, isAction)) return null;

        var openType = BuildOpenDelegateType(parameters, invoke.ReturnType, isAction);

        Delegate open;
        try
        {
            open = method.CreateDelegate(openType);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var binder = SelectBinder(parameters.Length, isAction);
        var typeArguments = isAction
            ? parameters.Select(p => p.ParameterType).ToArray()
            : [.. parameters.Select(p => p.ParameterType), invoke.ReturnType];

        var concrete = typeArguments.Length == 0 ? binder : binder.MakeGenericMethod(typeArguments);

        return (Func<ScriptClosure, Delegate>)Delegate.CreateDelegate(
            typeof(Func<ScriptClosure, Delegate>), open, concrete);
    }

    private static bool IsExactShape(Type delegateType, ParameterInfo[] parameters, Type returnType, bool isAction)
    {
        if (isAction)
        {
            if (parameters.Length == 0) return delegateType == typeof(Action);
            return delegateType == ActionDefinition(parameters.Length)
                .MakeGenericType([.. parameters.Select(p => p.ParameterType)]);
        }

        return delegateType == FuncDefinition(parameters.Length)
            .MakeGenericType([.. parameters.Select(p => p.ParameterType), returnType]);
    }

    private static Type BuildOpenDelegateType(ParameterInfo[] parameters, Type returnType, bool isAction)
    {
        var types = new List<Type> { typeof(ScriptClosure) };
        types.AddRange(parameters.Select(p => p.ParameterType));

        if (isAction) return ActionDefinition(types.Count).MakeGenericType([.. types]);

        types.Add(returnType);
        return FuncDefinition(types.Count - 1).MakeGenericType([.. types]);
    }

    private static Type FuncDefinition(int parameterCount) => parameterCount switch
    {
        0 => typeof(Func<>),
        1 => typeof(Func<,>),
        2 => typeof(Func<,,>),
        3 => typeof(Func<,,,>),
        4 => typeof(Func<,,,,>),
        _ => throw new ArgumentOutOfRangeException(nameof(parameterCount)),
    };

    private static Type ActionDefinition(int parameterCount) => parameterCount switch
    {
        1 => typeof(Action<>),
        2 => typeof(Action<,>),
        3 => typeof(Action<,,>),
        4 => typeof(Action<,,,>),
        _ => throw new ArgumentOutOfRangeException(nameof(parameterCount)),
    };

    private static MethodInfo SelectBinder(int parameterCount, bool isAction)
    {
        var name = (isAction ? "BindAction" : "BindFunc") + parameterCount;
        return typeof(ClosureBinder).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    }

    // The first argument of each binder is bound at compile time, leaving a
    // Func<ScriptClosure, Delegate> that allocates one small closure per call.

    private static Delegate BindFunc0<TResult>(Func<ScriptClosure, TResult> open, ScriptClosure closure) =>
        (Func<TResult>)(() => open(closure));

    private static Delegate BindFunc1<T0, TResult>(Func<ScriptClosure, T0, TResult> open, ScriptClosure closure) =>
        (Func<T0, TResult>)(a => open(closure, a));

    private static Delegate BindFunc2<T0, T1, TResult>(Func<ScriptClosure, T0, T1, TResult> open, ScriptClosure closure) =>
        (Func<T0, T1, TResult>)((a, b) => open(closure, a, b));

    private static Delegate BindFunc3<T0, T1, T2, TResult>(Func<ScriptClosure, T0, T1, T2, TResult> open, ScriptClosure closure) =>
        (Func<T0, T1, T2, TResult>)((a, b, c) => open(closure, a, b, c));

    private static Delegate BindAction0(Action<ScriptClosure> open, ScriptClosure closure) =>
        (Action)(() => open(closure));

    private static Delegate BindAction1<T0>(Action<ScriptClosure, T0> open, ScriptClosure closure) =>
        (Action<T0>)(a => open(closure, a));

    private static Delegate BindAction2<T0, T1>(Action<ScriptClosure, T0, T1> open, ScriptClosure closure) =>
        (Action<T0, T1>)((a, b) => open(closure, a, b));

    private static Delegate BindAction3<T0, T1, T2>(Action<ScriptClosure, T0, T1, T2> open, ScriptClosure closure) =>
        (Action<T0, T1, T2>)((a, b, c) => open(closure, a, b, c));
}
