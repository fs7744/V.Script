using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

// Verifies the runtime-async capabilities the engine's asynchronous carrier depends on. Every
// claim in the "实证依据" section of docs/design.md comes from this program.
//
// It was written against .NET 11.0.100-preview.7. Re-run it when moving to a newer SDK — in
// particular at GA — because the whole asynchronous design rests on these results.

if (args.Length > 0 && args[0] == "--handler")
{
    // Runs in a child process: a suspension point inside a catch handler is expected to take
    // the process down rather than raise a catchable error.
    HandlerCrashCase.Run();
    return 0;
}

var failures = 0;

void Check(string label, bool condition, string detail = "")
{
    Console.WriteLine($"{(condition ? "OK  " : "FAIL")}  {label,-46}{detail}");
    if (!condition) failures++;
}

Console.WriteLine($".NET {Environment.Version}");
Console.WriteLine();

// ---------------------------------------------------------------- capability surface

Check("MethodImplAttributes.Async == 0x2000",
    (int)MethodImplAttributes.Async == 0x2000,
    $"实际 0x{(int)MethodImplAttributes.Async:X}");

Check("DynamicMethod 无法标记 Async",
    typeof(DynamicMethod).GetMethod("SetImplementationFlags") is null,
    "这一个 API 缺口决定了异步脚本必须用独占程序集");

var awaitInt = typeof(AsyncHelpers)
    .GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Single(m => m.Name == "Await" && m.IsGenericMethodDefinition
                 && m.GetParameters().Length == 1
                 && m.GetParameters()[0].ParameterType.IsGenericType
                 && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
    .MakeGenericMethod(typeof(int));

Check("AsyncHelpers.Await<T>(Task<T>) 可解析", true);

// ---------------------------------------------------------------- emit and run

var assembly = AssemblyBuilder.DefineDynamicAssembly(
    new AssemblyName("RuntimeAsyncCheck"), AssemblyBuilderAccess.RunAndCollect);

var type = assembly.DefineDynamicModule("M").DefineType(
    "Generated", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

MethodBuilder Define(string name)
{
    var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
        typeof(Task<int>), [typeof(Task<int>)]);

    method.SetImplementationFlags(
        MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.Async);

    return method;
}

// await a + 1
{
    var il = Define("Simple").GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, awaitInt);
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Add);
    il.Emit(OpCodes.Ret);
}

// total = 0; for (i = 0; i < 3; i++) total += await a; return total;
{
    var il = Define("Loop").GetILGenerator();
    var total = il.DeclareLocal(typeof(int));
    var index = il.DeclareLocal(typeof(int));
    var top = il.DefineLabel();
    var condition = il.DefineLabel();

    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, total);
    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, index);
    il.Emit(OpCodes.Br, condition);
    il.MarkLabel(top);
    il.Emit(OpCodes.Ldloc, total);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, awaitInt);
    il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, total);
    il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
    il.MarkLabel(condition);
    il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Blt, top);
    il.Emit(OpCodes.Ldloc, total);
    il.Emit(OpCodes.Ret);
}

// try { return await a; } catch (Exception) { return -1; }
{
    var il = Define("InTry").GetILGenerator();
    var result = il.DeclareLocal(typeof(int));

    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, result);
    il.BeginExceptionBlock();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, awaitInt);
    il.Emit(OpCodes.Stloc, result);
    il.BeginCatchBlock(typeof(Exception));
    il.Emit(OpCodes.Pop);
    il.EndExceptionBlock();
    il.Emit(OpCodes.Ldloc, result);
    il.Emit(OpCodes.Ret);
}

var created = type.CreateType()!;

Func<Task<int>, Task<int>> Bind(string name) =>
    (Func<Task<int>, Task<int>>)created.GetMethod(name)!.CreateDelegate(typeof(Func<Task<int>, Task<int>>));

Check("MethodBuilder + Async 标志可创建",
    (created.GetMethod("Simple")!.MethodImplementationFlags & MethodImplAttributes.Async) != 0);

Check("已完成的 Task：await 返回正确结果",
    await Bind("Simple")(Task.FromResult(41)) == 42);

// A pending task forces a real suspension rather than the fast path.
var pendingSource = new TaskCompletionSource<int>();
var pending = Bind("Simple")(pendingSource.Task);
var suspended = !pending.IsCompleted;
pendingSource.SetResult(41);

Check("未完成的 Task：真实挂起后恢复",
    suspended && await pending == 42,
    suspended ? "" : "调用后立即完成，没有真正挂起");

var delayed = Task.Run(async () => { await Task.Delay(20); return 7; });
Check("await 位于循环体内", await Bind("Loop")(delayed) == 21);

Check("await 位于 try 块", await Bind("InTry")(Task.FromResult(5)) == 5);

// ---------------------------------------------------------------- handler crash

// This one cannot be checked in-process: the runtime terminates rather than throwing, so the
// case runs in a child process and the exit code is the observation.
var self = Environment.ProcessPath!;

// Launched through `dotnet run`, the host is dotnet itself and the assembly path has to be
// passed along; a published apphost re-launches directly.
string[] selfArguments = self.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
    ? [Environment.GetCommandLineArgs()[0], "--handler"]
    : ["--handler"];

var child = System.Diagnostics.Process.Start(BuildChildStart(self, selfArguments))!;
var childOutput = await child.StandardError.ReadToEndAsync();
await child.WaitForExitAsync();

var reachedHandler = childOutput.Contains("about to await", StringComparison.Ordinal);

Check("catch 内真正挂起的 await 会终止进程（预期如此）",
    reachedHandler && child.ExitCode != 0,
    reachedHandler
        ? $"子进程退出码 {child.ExitCode}；为 0 说明运行时行为已改变，需重新评估 VS3004"
        : "子进程未执行到检查点，重新启动逻辑有问题");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "全部通过：异步载体的前提条件成立。"
    : $"{failures} 项未通过：异步载体的前提条件已改变，须重新评估设计。");

return failures == 0 ? 0 : 1;

static System.Diagnostics.ProcessStartInfo BuildChildStart(string fileName, string[] arguments)
{
    var start = new System.Diagnostics.ProcessStartInfo
    {
        FileName = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return start;
}

/// <summary>
/// Emits <c>try { throw } catch { await }</c> and runs it. The runtime gives no protection for a
/// suspension point inside a handler, so this is expected to bring the process down.
/// </summary>
/// <remarks>
/// The awaited task must genuinely suspend. With an already-completed task <c>AsyncHelpers.Await</c>
/// takes its fast path, never reaches a suspension point, and the handler runs perfectly well —
/// which is exactly what makes this trap so easy to miss in testing.
/// </remarks>
internal static class HandlerCrashCase
{
    public static void Run()
    {
        var awaitInt = typeof(AsyncHelpers)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Await" && m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType.IsGenericType
                         && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
            .MakeGenericMethod(typeof(int));

        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("HandlerCase"), AssemblyBuilderAccess.RunAndCollect);

        var type = assembly.DefineDynamicModule("M").DefineType(
            "Generated", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var method = type.DefineMethod("InCatch", MethodAttributes.Public | MethodAttributes.Static,
            typeof(Task<int>), [typeof(Task<int>)]);

        method.SetImplementationFlags(
            MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.Async);

        var il = method.GetILGenerator();
        var result = il.DeclareLocal(typeof(int));

        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, result);
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, awaitInt);
        il.Emit(OpCodes.Stloc, result);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);

        var created = type.CreateType()!;
        var run = (Func<Task<int>, Task<int>>)created.GetMethod("InCatch")!
            .CreateDelegate(typeof(Func<Task<int>, Task<int>>));

        Console.Error.WriteLine("child: about to await inside a catch handler");
        Console.Error.Flush();

        var pending = Task.Run(async () => { await Task.Delay(50); return 1; });
        Console.Error.WriteLine($"child: survived, result {run(pending).GetAwaiter().GetResult()}");
    }
}
