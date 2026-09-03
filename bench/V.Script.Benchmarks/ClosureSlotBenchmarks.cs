using System.Reflection;
using System.Reflection.Emit;
using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// What a captured variable costs, boxed in an <see cref="ArrayClosure"/> against typed in a
/// <see cref="ScriptClosure{T0}"/>.
/// </summary>
/// <remarks>
/// The engine picks one representation for the whole script, so an end-to-end comparison means
/// two separate benchmark processes — and on a machine that drifts, two processes minutes apart
/// cannot resolve a few nanoseconds. Both representations are built by hand here so the
/// comparison happens inside one run, against one set of untouched baselines.
/// <para>
/// The IL is what the emitter produces: a lambda receives the base <see cref="ScriptClosure"/>
/// as argument 0 and casts, so both sides pay that cast.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ClosureSlotBenchmarks
{
    private const int Invocations = 8;

    private ScriptHost _host = null!;

    private Func<int, bool> _typedSlot = null!;
    private Func<int, bool> _arraySlot = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host = new ScriptHost("closure-slots");

        var typedClosure = new ScriptClosure<int>(_host, null) { Slot0 = 2 };

        var arrayClosure = new ArrayClosure(_host, null, 1);
        arrayClosure.Values[0] = 2;

        _typedSlot = Bind(EmitTypedRead, typedClosure);
        _arraySlot = Bind(EmitArrayRead, arrayClosure);

        // Same predicate either way, or the two rows are not measuring the same work.
        foreach (var predicate in new[] { _typedSlot, _arraySlot })
            if (!predicate(3) || predicate(1))
                throw new InvalidOperationException("两种槽位读法的结果不一致。");
    }

    private static Func<int, bool> Bind(Action<ILGenerator> emitBody, ScriptClosure closure)
    {
        var method = new DynamicMethod("Predicate", typeof(bool), [typeof(ScriptClosure), typeof(int)],
            typeof(ClosureSlotBenchmarks).Module, skipVisibility: true);

        emitBody(method.GetILGenerator());

        // Through the production binder, so the delegate shape matches a real capturing lambda.
        var factory = ClosureBinder.TryCreateFactory(method, typeof(Func<int, bool>))!;
        return (Func<int, bool>)factory(closure);
    }

    /// <summary>x &gt; ((ScriptClosure&lt;int&gt;)c).Slot0</summary>
    private static void EmitTypedRead(ILGenerator il)
    {
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeof(ScriptClosure<int>));
        il.Emit(OpCodes.Ldfld, typeof(ScriptClosure<int>).GetField(nameof(ScriptClosure<int>.Slot0))!);
        il.Emit(OpCodes.Cgt);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>x &gt; (int)((ArrayClosure)c).Values[0]</summary>
    private static void EmitArrayRead(ILGenerator il)
    {
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeof(ArrayClosure));
        il.Emit(OpCodes.Callvirt, typeof(ArrayClosure).GetProperty(nameof(ArrayClosure.Values))!.GetMethod!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, typeof(int));
        il.Emit(OpCodes.Cgt);
        il.Emit(OpCodes.Ret);
    }

    private static int Count(Func<int, bool> predicate)
    {
        var matched = 0;
        for (var i = 0; i < Invocations; i++) if (predicate(i)) matched++;
        return matched;
    }

    // ---------------------------------------------------------------- reading a slot

    [Benchmark(Baseline = true, Description = "8 reads / boxed slot in an object[]")]
    public int ReadArraySlot() => Count(_arraySlot);

    [Benchmark(Description = "8 reads / typed slot")]
    public int ReadTypedSlot() => Count(_typedSlot);

    // ---------------------------------------------------------------- creating one, as every Run does

    [Benchmark(Description = "create + store an int / object[]")]
    public object CreateArrayClosure()
    {
        var closure = new ArrayClosure(_host, null, 1);
        closure.Values[0] = 7;
        return closure;
    }

    [Benchmark(Description = "create + store an int / typed")]
    public object CreateTypedClosure() => new ScriptClosure<int>(_host, null) { Slot0 = 7 };

    [Benchmark(Description = "create + store a reference / object[]")]
    public object CreateArrayClosureReference()
    {
        var closure = new ArrayClosure(_host, null, 1);
        closure.Values[0] = _host;
        return closure;
    }

    [Benchmark(Description = "create + store a reference / typed")]
    public object CreateTypedClosureReference() => new ScriptClosure<ScriptHost>(_host, null) { Slot0 = _host };
}
