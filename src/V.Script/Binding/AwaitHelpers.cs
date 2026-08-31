using System.Reflection;
using System.Runtime.CompilerServices;
namespace V.Script.Binding;

/// <summary>
/// Locates the <see cref="AsyncHelpers"/> entry points the emitter calls at suspension points.
/// Runtime-async replaces the compiler-generated state machine with a single call the JIT
/// understands, so all the binder has to do is pick the right overload.
/// </summary>
internal static class AwaitHelpers
{
    private static readonly MethodInfo AwaitTask = FindAwait(typeof(Task), generic: false);
    private static readonly MethodInfo AwaitTaskOfT = FindAwait(typeof(Task<>), generic: true);
    private static readonly MethodInfo AwaitValueTask = FindAwait(typeof(ValueTask), generic: false);
    private static readonly MethodInfo AwaitValueTaskOfT = FindAwait(typeof(ValueTask<>), generic: true);

    private static MethodInfo FindAwait(Type awaitableDefinition, bool generic)
    {
        foreach (var method in typeof(AsyncHelpers).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != nameof(AsyncHelpers.Await)) continue;
            if (method.IsGenericMethodDefinition != generic) continue;

            var parameters = method.GetParameters();
            if (parameters.Length != 1) continue;

            var parameterType = parameters[0].ParameterType;
            var matches = generic
                ? parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == awaitableDefinition
                : parameterType == awaitableDefinition;

            if (matches) return method;
        }

        throw new InvalidOperationException(
            $"当前运行时的 AsyncHelpers 缺少 Await({awaitableDefinition.Name}) 重载。V.Script 需要 .NET 11 的 runtime-async 支持。");
    }

    /// <summary>Classifies an awaitable operand, or returns null when the type cannot be awaited.</summary>
    public static (AwaitKind Kind, Type ResultType)? Describe(Type type)
    {
        if (type == typeof(Task)) return (AwaitKind.Task, typeof(void));
        if (type == typeof(ValueTask)) return (AwaitKind.ValueTask, typeof(void));

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>)) return (AwaitKind.TaskOfT, type.GetGenericArguments()[0]);
            if (definition == typeof(ValueTask<>)) return (AwaitKind.ValueTaskOfT, type.GetGenericArguments()[0]);
        }

        // Task subclasses still await as Task.
        if (typeof(Task).IsAssignableFrom(type)) return (AwaitKind.Task, typeof(void));

        return null;
    }

    public static MethodInfo GetAwaitMethod(AwaitKind kind, Type resultType) => kind switch
    {
        AwaitKind.Task => AwaitTask,
        AwaitKind.ValueTask => AwaitValueTask,
        AwaitKind.TaskOfT => AwaitTaskOfT.MakeGenericMethod(resultType),
        AwaitKind.ValueTaskOfT => AwaitValueTaskOfT.MakeGenericMethod(resultType),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

}
