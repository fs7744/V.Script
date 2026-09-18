using System.Reflection;
using System.Runtime.CompilerServices;

namespace V.Script.Async;

/// <summary>
/// The runtime-async half of the compiler: how to await something, and where to put methods that
/// are allowed to suspend.
/// </summary>
/// <remarks>
/// Runtime-async replaces the compiler-generated state machine with a single call the JIT
/// understands, so awaiting is entirely a matter of picking the right <see cref="AsyncHelpers"/>
/// overload. This is the only place in the engine that touches that (experimental) API.
/// </remarks>
internal sealed class AsyncSupport(GeneratedAssemblyPool assemblies) : IAsyncSupport
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
            $"当前运行时的 AsyncHelpers 缺少 Await({awaitableDefinition.Name}) 重载。" +
            "V.Script.Async 需要 .NET 11 的 runtime-async 支持。");
    }

    public Type? UnwrapAsyncReturnType(Type declaredReturnType)
    {
        if (declaredReturnType == typeof(Task)) return typeof(void);

        return declaredReturnType.IsGenericType &&
               declaredReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? declaredReturnType.GetGenericArguments()[0]
            : null;
    }

    public (Type ResultType, MethodInfo Suspend)? DescribeAwaitable(Type awaitable)
    {
        if (awaitable == typeof(Task)) return (typeof(void), AwaitTask);
        if (awaitable == typeof(ValueTask)) return (typeof(void), AwaitValueTask);

        if (awaitable.IsGenericType)
        {
            var definition = awaitable.GetGenericTypeDefinition();
            var result = awaitable.GetGenericArguments()[0];

            if (definition == typeof(Task<>)) return (result, AwaitTaskOfT.MakeGenericMethod(result));
            if (definition == typeof(ValueTask<>)) return (result, AwaitValueTaskOfT.MakeGenericMethod(result));
        }

        // Task subclasses still await as Task.
        if (typeof(Task).IsAssignableFrom(awaitable)) return (typeof(void), AwaitTask);

        return null;
    }

    public IGeneratedType ReserveType(string name) => assemblies.Define(name);
}
