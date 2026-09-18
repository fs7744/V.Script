using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;
using V.Script.Emit;

namespace V.Script.Async;

/// <summary>
/// Produces the delegate for a script that can suspend.
/// </summary>
/// <remarks>
/// This is the whole reason the async half is a separate library. A suspension point requires
/// the method to carry <c>MethodImplAttributes.Async</c> (0x2000), which tells the JIT to build
/// the state machine; a <see cref="DynamicMethod"/> has no <c>SetImplementationFlags</c> and so
/// can never carry it. Asynchronous scripts therefore need a real method in a real — and, to stay
/// reclaimable, collectible — assembly, roughly 31 KB and two orders of magnitude more compile
/// time than the synchronous carrier's <see cref="DynamicMethod"/>.
/// </remarks>
internal static class AsyncScriptCarrier
{
    /// <summary>0x2000. Tells the JIT to build the state machine for this method.</summary>
    private const MethodImplAttributes AsyncImplFlag = MethodImplAttributes.Async;

    public static (Delegate Invoke, IDisposable? Owner) Compile(
        BoundScript script,
        Type delegateType,
        Type[] scriptParameterTypes,
        Type ilReturnType,
        ScriptHost host,
        string name,
        IAsyncSupport asyncSupport)
    {
        var signature = ScriptCarrier.BuildSignature(scriptParameterTypes);

        // The declared return type is Task/Task<T>; the IL body returns the unwrapped value and
        // the runtime performs the wrapping. This is the whole of runtime-async on our side.
        var declaredReturnType = ilReturnType == typeof(void)
            ? typeof(Task)
            : typeof(Task<>).MakeGenericType(ilReturnType);

        var lease = asyncSupport.ReserveType(name);
        var type = lease.Builder;

        var method = type.DefineMethod(
            "Run",
            MethodAttributes.Public | MethodAttributes.Static,
            declaredReturnType,
            signature);

        method.SetImplementationFlags(
            MethodImplAttributes.IL | MethodImplAttributes.Managed | AsyncImplFlag);

        // The script's own type hosts its async lambdas too: one assembly, one CreateType.
        var publish = IlEmitter.EmitScript(method.GetILGenerator(), script, host, type);

        var created = type.CreateType()!;
        publish(created);
        lease.Publish(created);

        var runtimeMethod = created.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var invoke = runtimeMethod.CreateDelegate(delegateType, host);
        return (invoke, lease);
    }
}
