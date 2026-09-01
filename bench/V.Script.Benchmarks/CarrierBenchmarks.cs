using System.Reflection;
using System.Reflection.Emit;
using BenchmarkDotNet.Attributes;

namespace V.Script.Benchmarks;

/// <summary>
/// Does the carrier affect how fast generated code runs? The same IL body is emitted through a
/// <see cref="DynamicMethod"/>, a non-collectible assembly and a collectible one, and invoked
/// through the same delegate shape. A hand-written C# method compiled into an ordinary assembly
/// is the baseline.
/// </summary>
/// <remarks>
/// The engine picks its carrier for lifetime and async reasons (see docs/design.md §3). This
/// benchmark exists to check that the choice costs nothing at run time — if it ever starts to,
/// the trade-off has to be revisited.
/// </remarks>
[MemoryDiagnoser]
public class CarrierExecutionBenchmarks
{
    private Func<int, int> _dynamicMethod = null!;
    private Func<int, int> _fixedAssembly = null!;
    private Func<int, int> _collectibleAssembly = null!;

    [GlobalSetup]
    public void Setup()
    {
        var method = new DynamicMethod("Body", typeof(int), [typeof(int)],
            typeof(CarrierExecutionBenchmarks).Module, skipVisibility: true);

        CarrierIl.EmitBody(method.GetILGenerator());
        _dynamicMethod = method.CreateDelegate<Func<int, int>>();

        _fixedAssembly = CarrierIl.BuildViaAssembly(AssemblyBuilderAccess.Run);
        _collectibleAssembly = CarrierIl.BuildViaAssembly(AssemblyBuilderAccess.RunAndCollect);
    }

    /// <summary>The same computation written in C# and compiled into an ordinary assembly.</summary>
    private static int Native(int x)
    {
        var sum = 0;
        for (var i = 0; i < 100; i++) sum += (x * i) % 7;
        return sum;
    }

    [Benchmark(Baseline = true, Description = "hand-written C#, ordinary assembly")]
    public int NativeAssembly() => Native(17);

    [Benchmark(Description = "emitted into a DynamicMethod")]
    public int DynamicMethodCarrier() => _dynamicMethod(17);

    [Benchmark(Description = "emitted into a non-collectible assembly")]
    public int FixedAssemblyCarrier() => _fixedAssembly(17);

    [Benchmark(Description = "emitted into a collectible assembly")]
    public int CollectibleAssemblyCarrier() => _collectibleAssembly(17);
}

/// <summary>What each carrier costs to produce, which is where they genuinely differ.</summary>
/// <remarks>
/// Read the two assembly rows together with what happens afterwards. A non-collectible assembly
/// is cheaper here precisely because it is never cleaned up — <c>AssemblyBuilderAccess.Run</c>
/// can never be unloaded, so a host that compiles scripts over its lifetime would grow without
/// bound. The collectible figure includes the unload work that the non-collectible one simply
/// never does, and is noisy for the same reason: assemblies pile up across the measured
/// iterations. The engine's own end-to-end number (compile then retire) is in
/// <see cref="CompilationBenchmarks"/>.
/// </remarks>
[MemoryDiagnoser]
public class CarrierCompilationBenchmarks
{
    private int _counter;

    [Benchmark(Baseline = true, Description = "DynamicMethod")]
    public object BuildDynamicMethod()
    {
        var method = new DynamicMethod($"Body{_counter++}", typeof(int), [typeof(int)],
            typeof(CarrierCompilationBenchmarks).Module, skipVisibility: true);

        CarrierIl.EmitBody(method.GetILGenerator());
        return method.CreateDelegate<Func<int, int>>();
    }

    [Benchmark(Description = "non-collectible assembly")]
    public object BuildFixedAssembly() => CarrierIl.BuildViaAssembly(AssemblyBuilderAccess.Run);

    [Benchmark(Description = "collectible assembly")]
    public object BuildCollectibleAssembly() => CarrierIl.BuildViaAssembly(AssemblyBuilderAccess.RunAndCollect);
}

internal static class CarrierIl
{
    private static int _sequence;

    /// <summary>int f(int x) { int s = 0; for (int i = 0; i &lt; 100; i++) s += (x * i) % 7; return s; }</summary>
    public static void EmitBody(ILGenerator il)
    {
        var sum = il.DeclareLocal(typeof(int));
        var index = il.DeclareLocal(typeof(int));
        var top = il.DefineLabel();
        var check = il.DefineLabel();

        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, sum);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, index);
        il.Emit(OpCodes.Br, check);

        il.MarkLabel(top);
        il.Emit(OpCodes.Ldloc, sum);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldc_I4_7);
        il.Emit(OpCodes.Rem);
        il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, sum);
        il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);

        il.MarkLabel(check);
        il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4, 100); il.Emit(OpCodes.Blt, top);

        il.Emit(OpCodes.Ldloc, sum);
        il.Emit(OpCodes.Ret);
    }

    public static Func<int, int> BuildViaAssembly(AssemblyBuilderAccess access)
    {
        var name = new AssemblyName($"Carrier{Interlocked.Increment(ref _sequence)}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, access);

        var type = assembly.DefineDynamicModule("M").DefineType(
            "Generated", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var method = type.DefineMethod("Body", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]);

        EmitBody(method.GetILGenerator());

        return type.CreateType()!.GetMethod("Body")!.CreateDelegate<Func<int, int>>();
    }
}
