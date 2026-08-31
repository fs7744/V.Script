using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

/// <summary>
/// Produces the executable delegate for a bound script. Two carriers exist because only one of
/// them can host a suspension point:
/// <list type="bullet">
///   <item><b>Synchronous</b> scripts go into a <see cref="DynamicMethod"/> — roughly 1.3 KB and
///     3 µs per script, reclaimed automatically with the delegate.</item>
///   <item><b>Asynchronous</b> scripts need <see cref="MethodImplAttributes.Async"/>, which
///     <see cref="DynamicMethod"/> cannot express (it has no <c>SetImplementationFlags</c>), so
///     each one gets its own collectible assembly — roughly 31 KB, individually unloadable.</item>
/// </list>
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
        bool hasCancellationToken,
        ScriptHost host,
        string name)
    {
        var signature = BuildSignature(scriptParameterTypes, hasCancellationToken);

        var method = new DynamicMethod(
            name,
            ilReturnType,
            signature,
            typeof(ScriptCarrier).Module,
            skipVisibility: true);

        IlEmitter.EmitScript(method.GetILGenerator(), script, hasCancellationToken, host);

        return (method.CreateDelegate(delegateType, host), null);
    }

    public static (Delegate Invoke, IDisposable? Owner) CompileAsynchronous(
        BoundScript script,
        Type delegateType,
        Type[] scriptParameterTypes,
        Type ilReturnType,
        bool hasCancellationToken,
        ScriptHost host,
        string name)
    {
        var signature = BuildSignature(scriptParameterTypes, hasCancellationToken);

        // The declared return type is Task/Task<T>; the IL body returns the unwrapped value and
        // the runtime performs the wrapping. This is the whole of runtime-async on our side.
        var declaredReturnType = ilReturnType == typeof(void)
            ? typeof(Task)
            : typeof(Task<>).MakeGenericType(ilReturnType);

        var assemblyName = new AssemblyName($"V.Script.Generated.{name}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("M");

        var type = module.DefineType(
            "Script",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var method = type.DefineMethod(
            "Run",
            MethodAttributes.Public | MethodAttributes.Static,
            declaredReturnType,
            signature);

        method.SetImplementationFlags(
            MethodImplAttributes.IL | MethodImplAttributes.Managed | AsyncImplFlag);

        IlEmitter.EmitScript(method.GetILGenerator(), script, hasCancellationToken, host);

        var created = type.CreateType()!;
        var runtimeMethod = created.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var invoke = runtimeMethod.CreateDelegate(delegateType, host);
        return (invoke, new GeneratedAssembly(assembly, created));
    }

    private static Type[] BuildSignature(Type[] scriptParameterTypes, bool hasCancellationToken)
    {
        var signature = new List<Type>(scriptParameterTypes.Length + 2) { typeof(ScriptHost) };
        if (hasCancellationToken) signature.Add(typeof(CancellationToken));
        signature.AddRange(scriptParameterTypes);
        return [.. signature];
    }

    /// <summary>
    /// Keeps the generated assembly reachable for as long as the script is alive. Dropping this
    /// (plus the delegate) is what lets the runtime unload that one script's code.
    /// </summary>
    private sealed class GeneratedAssembly(AssemblyBuilder assembly, Type type) : IDisposable
    {
        private AssemblyBuilder? _assembly = assembly;
        private Type? _type = type;

        public void Dispose()
        {
            _assembly = null;
            _type = null;
        }

        public override string ToString() => _type?.Assembly.FullName ?? "<unloaded>";
    }
}
