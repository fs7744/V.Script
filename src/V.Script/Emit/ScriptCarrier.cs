using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

/// <summary>
/// Produces the executable delegate for a bound script: a <see cref="DynamicMethod"/>, roughly
/// 1.3 KB and a few microseconds per script, reclaimed automatically along with its delegate.
/// <para>
/// A script that can suspend cannot use this carrier — the method has to carry the Async
/// implementation flag, and a <see cref="DynamicMethod"/> has no <c>SetImplementationFlags</c>.
/// <c>V.Script.Async</c> carries those. The one thing that reaches back into this carrier is an
/// <c>async</c> lambda inside an otherwise synchronous script: the script body stays a
/// <see cref="DynamicMethod"/> and only the lambdas go into a generated type, which is fine
/// because the body only ever reaches a lambda through the host's table.
/// </para>
/// </summary>
internal static class ScriptCarrier
{
    public static (Delegate Invoke, IDisposable? Owner) CompileSynchronous(
        BoundScript script,
        Type delegateType,
        Type[] scriptParameterTypes,
        Type ilReturnType,
        ScriptHost host,
        string name,
        IAsyncSupport? asyncSupport)
    {
        var signature = BuildSignature(scriptParameterTypes);

        var method = new DynamicMethod(
            name,
            ilReturnType,
            signature,
            typeof(ScriptCarrier).Module,
            skipVisibility: true);

        // Only async lambdas need somewhere real to live; a script without them costs nothing.
        // The binder rejects an async lambda when no support is installed, so reaching here
        // with one guarantees there is somewhere to put it.
        var needsAssembly = script.Lambdas.Any(l => l.IsAsync);
        var lease = needsAssembly ? asyncSupport!.ReserveType($"{name}.Lambdas") : null;

        var publish = IlEmitter.EmitScript(method.GetILGenerator(), script, host, lease?.Builder);

        var created = lease?.Builder.CreateGeneratedType();
        publish(created);

        if (created is not null) lease!.Publish(created);

        return (method.CreateDelegate(delegateType, host), lease);
    }

    internal static Type[] BuildSignature(Type[] scriptParameterTypes)
    {
        var signature = new List<Type>(scriptParameterTypes.Length + 1) { typeof(ScriptHost) };
        signature.AddRange(scriptParameterTypes);
        return [.. signature];
    }

}
