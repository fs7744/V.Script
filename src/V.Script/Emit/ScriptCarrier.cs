using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

/// <summary>
/// Produces the executable delegate for a bound script. Two carriers exist because only one of
/// them can host a suspension point:
/// <list type="bullet">
///   <item><b>Synchronous</b> scripts go into a <see cref="DynamicMethod"/> — roughly 1.3 KB and
///     a few microseconds per script, reclaimed automatically with the delegate.</item>
///   <item><b>Asynchronous</b> scripts need <see cref="MethodImplAttributes.Async"/>, which
///     <see cref="DynamicMethod"/> cannot express (it has no <c>SetImplementationFlags</c>), so
///     each one gets its own collectible assembly — roughly 31 KB, individually unloadable.</item>
/// </list>
/// <para>
/// An <c>async</c> lambda needs the same flag, so a synchronous script that contains one also
/// gets an assembly — for the lambdas alone. The script body stays a <see cref="DynamicMethod"/>,
/// which is fine because it only ever reaches a lambda through the host's table.
/// </para>
/// </summary>
internal static class ScriptCarrier
{
    /// <summary>0x2000. Tells the JIT to build the state machine for this method.</summary>
    private const MethodImplAttributes AsyncImplFlag = MethodImplAttributes.Async;

    public static (Delegate Invoke, IDisposable? Owner) CompileSynchronous(
        BoundScript script,
        Type delegateType,
        Type[] scriptParameterTypes,
        Type ilReturnType,
        ScriptHost host,
        string name,
        GeneratedAssemblyPool pool)
    {
        var signature = BuildSignature(scriptParameterTypes);

        var method = new DynamicMethod(
            name,
            ilReturnType,
            signature,
            typeof(ScriptCarrier).Module,
            skipVisibility: true);

        // Only async lambdas need somewhere real to live; a script without them costs nothing.
        var needsAssembly = script.Lambdas.Any(l => l.IsAsync);
        var lease = needsAssembly ? pool.Define($"{name}.Lambdas") : null;

        var publish = IlEmitter.EmitScript(method.GetILGenerator(), script, host, lease?.Builder);

        var created = lease?.Builder.CreateType();
        publish(created);

        if (created is not null) lease!.Publish(created);

        return (method.CreateDelegate(delegateType, host), lease);
    }

    public static (Delegate Invoke, IDisposable? Owner) CompileAsynchronous(
        BoundScript script,
        Type delegateType,
        Type[] scriptParameterTypes,
        Type ilReturnType,
        ScriptHost host,
        string name,
        GeneratedAssemblyPool pool)
    {
        var signature = BuildSignature(scriptParameterTypes);

        // The declared return type is Task/Task<T>; the IL body returns the unwrapped value and
        // the runtime performs the wrapping. This is the whole of runtime-async on our side.
        var declaredReturnType = ilReturnType == typeof(void)
            ? typeof(Task)
            : typeof(Task<>).MakeGenericType(ilReturnType);

        var lease = pool.Define(name);
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

    private static Type[] BuildSignature(Type[] scriptParameterTypes)
    {
        var signature = new List<Type>(scriptParameterTypes.Length + 1) { typeof(ScriptHost) };
        signature.AddRange(scriptParameterTypes);
        return [.. signature];
    }

}
