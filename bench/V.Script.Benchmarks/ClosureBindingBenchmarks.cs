using System.Reflection;
using System.Reflection.Emit;
using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// How should a capturing lambda's delegate be produced on the hot path?
/// </summary>
/// <remarks>
/// <see cref="ClosureBinder"/> builds an <em>open</em> delegate once at compile time and then
/// wraps it per evaluation, because binding with <c>CreateDelegate</c> every time was measured
/// as costing hundreds of nanoseconds. That measurement predates the open delegate: by the time
/// a script runs, the generated method has already been turned into a delegate once and is
/// JITted. This asks whether the original reason still holds.
/// <para>
/// The wrapper costs an extra allocation and, more importantly, an extra hop on every single
/// call of the lambda — the predicate here is invoked eight times per bind, as it would be
/// inside a <c>Where</c> or a host method taking a <see cref="Func{T, TResult}"/>.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ClosureBindingBenchmarks
{
    private const int Invocations = 8;

    private ScriptHost _host = null!;
    private ScriptClosure<int> _closure = null!;

    private DynamicMethod _dynamic = null!;
    private MethodInfo _fromAssembly = null!;

    private Func<ScriptClosure, Delegate> _factory = null!;
    private Func<ScriptClosure, Delegate> _factoryFromAssembly = null!;

    private Func<int, bool> _preboundWrapper = null!;
    private Func<int, bool> _preboundDirect = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host = new ScriptHost("closure-binding");
        _closure = new ScriptClosure<int>(_host, null) { Slot0 = 2 };

        _dynamic = new DynamicMethod("Predicate", typeof(bool), [typeof(ScriptClosure), typeof(int)],
            typeof(ClosureBindingBenchmarks).Module, skipVisibility: true);
        EmitPredicate(_dynamic.GetILGenerator());

        _fromAssembly = BuildInAssembly();

        _factory = ClosureBinder.TryCreateFactory(_dynamic, typeof(Func<int, bool>))!;
        _factoryFromAssembly = ClosureBinder.TryCreateFactory(_fromAssembly, typeof(Func<int, bool>))!;

        // Bind once up front: whatever one-off work either path does is then behind us, which is
        // the situation a running script is actually in.
        _preboundWrapper = (Func<int, bool>)_factory(_closure);
        _preboundDirect = (Func<int, bool>)_dynamic.CreateDelegate(typeof(Func<int, bool>), _closure);

        Verify();
    }

    /// <summary>bool f(ScriptClosure c, int x) =&gt; x &gt; ((ScriptClosure&lt;int&gt;)c).Slot0;</summary>
    /// <remarks>The same shape the emitter produces for a captured <c>int</c>.</remarks>
    private static void EmitPredicate(ILGenerator il)
    {
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeof(ScriptClosure<int>));
        il.Emit(OpCodes.Ldfld, typeof(ScriptClosure<int>).GetField(nameof(ScriptClosure<int>.Slot0))!);
        il.Emit(OpCodes.Cgt);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>The same method, but hosted in a collectible assembly rather than a DynamicMethod.</summary>
    private static MethodInfo BuildInAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("V.Script.Bench.ClosureBinding"), AssemblyBuilderAccess.RunAndCollect);

        var type = assembly.DefineDynamicModule("M").DefineType(
            "Host", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var method = type.DefineMethod("Predicate", MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool), [typeof(ScriptClosure), typeof(int)]);

        EmitPredicate(method.GetILGenerator());

        return type.CreateType()!.GetMethod("Predicate", BindingFlags.Public | BindingFlags.Static)!;
    }

    /// <summary>Guards against measuring something that does not actually compute the predicate.</summary>
    private void Verify()
    {
        Func<int, bool>[] all =
        [
            _preboundWrapper,
            _preboundDirect,
            (Func<int, bool>)_factory(_closure),
            (Func<int, bool>)_dynamic.CreateDelegate(typeof(Func<int, bool>), _closure),
            (Func<int, bool>)_fromAssembly.CreateDelegate(typeof(Func<int, bool>), _closure),
            (Func<int, bool>)_factoryFromAssembly(_closure),
        ];

        foreach (var predicate in all)
            if (!predicate(3) || predicate(1))
                throw new InvalidOperationException("谓词行为不一致，测的不是同一件事。");
    }

    private static int Count(Func<int, bool> predicate)
    {
        var matched = 0;
        for (var i = 0; i < Invocations; i++) if (predicate(i)) matched++;
        return matched;
    }

    // ---------------------------------------------------------------- binding only

    [Benchmark(Baseline = true, Description = "bind / wrapper over an open delegate (current)")]
    public object BindWrapper() => _factory(_closure);

    [Benchmark(Description = "bind / CreateDelegate on the DynamicMethod")]
    public object BindCreateDelegate() => _dynamic.CreateDelegate(typeof(Func<int, bool>), _closure);

    [Benchmark(Description = "bind / CreateDelegate on an assembly-hosted method")]
    public object BindCreateDelegateFromAssembly() =>
        _fromAssembly.CreateDelegate(typeof(Func<int, bool>), _closure);

    // ---------------------------------------------------------------- bind, then call it 8 times

    [Benchmark(Description = "bind + 8 calls / wrapper (current)")]
    public int BindAndCallWrapper() => Count((Func<int, bool>)_factory(_closure));

    [Benchmark(Description = "bind + 8 calls / CreateDelegate")]
    public int BindAndCallCreateDelegate() =>
        Count((Func<int, bool>)_dynamic.CreateDelegate(typeof(Func<int, bool>), _closure));

    // ---------------------------------------------------------------- the extra hop, isolated

    [Benchmark(Description = "8 calls only / pre-bound wrapper")]
    public int CallPreboundWrapper() => Count(_preboundWrapper);

    [Benchmark(Description = "8 calls only / pre-bound direct")]
    public int CallPreboundDirect() => Count(_preboundDirect);
}
