# 用 runtime-async 写一个支持 async/await 的轻量脚本引擎

## 起因：一个好奇

事情的开头很简单。

给 .NET 做过动态脚本的人大概都碰到过同一堵墙：**表达式树也好、`Reflection.Emit` 也好，都很难支持 `async`/`await`**。原因不在语法，而在 `await` 的实现方式——C# 编译器要为每个 `async` 方法生成一个状态机结构体，把方法体切成若干片，配上 `AsyncTaskMethodBuilder`、awaiter 字段、`MoveNext` 的巨型 switch。这套东西你在编译期用 Roslyn 生成很自然，但要在运行时用 IL 一条条 emit 出来，工作量和出错面都大到不合理。

所以脚本引擎通常二选一：要么放弃 `await`，要么退回到解释执行（然后性能就没得谈了）。

后来看到 runtime-async 这个提案，我的第一反应是——**如果状态机改由 JIT 来生成，那"动态生成的代码"和"编译期生成的代码"不就站在同一起跑线上了吗？** 换句话说：一个运行时 emit 出来的方法，能不能既真正支持 `await`，又保持接近手写 C# 的性能？

这个好奇没法靠读文档回答，只能写代码试。于是有了 [**V.Script**](https://github.com/fs7744/V.Script)：一个编译成委托、兼容 C# 类型系统的轻量脚本引擎。

结论先放这里：**能，但代价在你可能没预料到的地方**。

> ### 关于版本，先说清楚
>
> 这个想法最早是冲着 .NET 10 去的——runtime-async 是在 .NET 10 周期里作为实验特性登场的。
>
> 但**本文所有代码和所有数字，都是在 .NET 11.0.100-preview.7 上跑出来的**，仓库的 [`global.json`](https://github.com/fs7744/V.Script/blob/main/global.json) 也精确 pin 了这个预览版号。我没有在 .NET 10 上完整跑过下面这套验证，所以不替它背书；如果你在 .NET 10 上试，请自己先跑一遍仓库里的 [`tools/V.Script.RuntimeAsyncCheck`](https://github.com/fs7744/V.Script/tree/main/tools/V.Script.RuntimeAsyncCheck)。
>
> 另外 `AsyncHelpers` 目前带着 `SYSLIB5007` 实验性标记，签名随时可能变。

---

## runtime-async 是什么：一处 IL 层面的差异

先看传统的 `async` 方法在 IL 里长什么样。这是 `async Task<int> F() => await GetAsync();` 编译出来的东西的骨架：

```
// 编译器额外生成的类型
.class nested private sealed beforefieldinit '<F>d__0'
       extends [System.Runtime]System.ValueType
       implements [System.Runtime]IAsyncStateMachine
{
    .field public int32 '<>1__state'
    .field public valuetype AsyncTaskMethodBuilder`1<int32> '<>t__builder'
    .field private valuetype TaskAwaiter`1<int32> '<>u__1'

    .method private hidebysig newslot virtual final
            instance void MoveNext() cil managed
    {
        // 一个按 <>1__state 分派的大 switch，
        // 把原方法体切成若干段，每段之间保存/恢复局部变量
    }
}

// 原方法只剩下引导代码
.method public hidebysig static
        class Task`1<int32> F() cil managed
{
    .custom instance void AsyncStateMachineAttribute::.ctor(class Type)
    // 构造状态机、Start、返回 builder.Task
}
```

要用 `Reflection.Emit` 复刻这一坨：你得定义一个额外的类型、实现 `IAsyncStateMachine`、自己做局部变量提升、自己切分基本块、自己写状态分派。这不是"麻烦"，这是重新实现一遍编译器里最难的一个 lowering。

**runtime-async 把这件事整个搬走了。** 同样的语义，IL 变成这样：

```
.method public hidebysig static
        class Task`1<int32> Run(class ScriptHost, !!TGlobals) cil managed async
//                                                                      ^^^^^
//                                    MethodImplAttributes.Async == 0x2000
{
    // 直线 IL，没有状态机，没有额外类型
    call     class Task`1<int32> Service::GetAsync()
    call     !!0 AsyncHelpers::Await<int32>(class Task`1<!!0>)   // ← 挂起点
    ret                                                          // ← 注意这里
}
```

三处关键差异：

1. **方法上多了一个 `async` 实现标志**（`MethodImplAttributes.Async`，值 `0x2000`）。它告诉 JIT：这个方法体需要被改写成状态机。
2. **`await` 变成一次普通的 `call`**，调用 `System.Runtime.CompilerServices.AsyncHelpers.Await<T>`。JIT 认得这个调用，把它变成真正的挂起点。
3. **签名声明返回 `Task<int>`，但 IL 里 `ret` 弹出的是 `int`**。包装成 `Task` 是运行时的事，emit 方不用管。

对一个代码生成器来说，这个差异是决定性的。在 V.Script 里，整个 `await` 的发射逻辑就是这么长：

[`src/V.Script/Emit/IlEmitter.Expressions.cs`](https://github.com/fs7744/V.Script/blob/main/src/V.Script/Emit/IlEmitter.Expressions.cs)：

```csharp
/// <summary>
/// A suspension point. The JIT turns this call into the state machine, so the whole of
/// <c>await</c> support on the emit side is one operand plus one call.
/// </summary>
private void EmitAwait(BoundAwait await)
{
    EmitExpression(await.Operand);
    _il.Emit(OpCodes.Call, await.AwaitHelper);
}
```

**两行。** 绑定器那边也只是根据操作数类型在 `Task` / `Task<T>` / `ValueTask` / `ValueTask<T>` 四个重载里挑一个。原本"重新实现一遍 lowering"的工作量，压缩成了"选个重载再 emit 一条 call"。

---

## 但这里有一个坑：`DynamicMethod` 用不了

好消息说完了，说坏消息。

动态生成方法最轻的载体是 `DynamicMethod`：不需要程序集、不需要类型，委托没人引用时自动回收。V.Script 的同步脚本就用它——一个脚本大约 1.3 KB、几微秒。

问题是：**`DynamicMethod` 没有 `SetImplementationFlags`**。

[`tools/V.Script.RuntimeAsyncCheck/Program.cs`](https://github.com/fs7744/V.Script/blob/main/tools/V.Script.RuntimeAsyncCheck/Program.cs)：

```csharp
Check("DynamicMethod 无法标记 Async",
    typeof(DynamicMethod).GetMethod("SetImplementationFlags") is null,
    "这一个 API 缺口决定了异步脚本必须用独占程序集");
```

上面这行来自仓库里的 [`tools/V.Script.RuntimeAsyncCheck`](https://github.com/fs7744/V.Script/tree/main/tools/V.Script.RuntimeAsyncCheck)——一个专门用来验证 runtime-async 前提条件的小程序。这一个 API 缺口，直接决定了整个异步方案的形状：**异步脚本没法用 `DynamicMethod`，只能退到 `AssemblyBuilder` + `MethodBuilder`**。

于是 V.Script 有了两个载体：

| | 同步脚本 | 异步脚本 |
|---|---|---|
| 载体 | `DynamicMethod` | 独占的 collectible 程序集 |
| 内存 | ~1.3 KB | ~31 KB |
| 回收 | 委托没人引用即回收 | 需要显式 `Dispose`，卸载整个程序集 |
| 能否标记 `Async` | ❌ | ✅ |

这个"一个 API 缺口 → 载体必须换 → 成本涨 24 倍"的因果链，是这个项目里最贵的一课，后面性能一节还会再见到它。

顺带一提，`async` lambda 也踩同一个坑：lambda 平时编译成 `DynamicMethod`，一旦写了 `async`，它就必须搬进程序集。所以**同步脚本里出现 `async` lambda，也会让这个脚本多出一个程序集**（脚本体本身仍是 `DynamicMethod`，只有这些 lambda 搬家）。

---

## 长什么样

```csharp
using var engine = new ScriptEngine(ScriptOptions.Default);

// 同步：编译成委托，DynamicMethod 载体
using var rule = engine.Compile<Order, bool>(
    "Total > 1000 && Customer.IsVip");

bool ok = rule.Run(order);

// 异步：真正的 await，独占程序集载体
using var flow = engine.CompileAsync<Context, int>("""
    var total = 0;
    for (var i = 0; i < Ids.Length; i++)
        total += await Service.GetAsync(Ids[i]);
    return total;
    """);

int result = await flow.RunAsync(context);
```

`Compile` 和 `CompileAsync` 是**两个不同的入口**，不是一个方法加个开关——因为它们背后是两个载体、两套成本模型，让调用方知道自己在付什么代价比"透明"更重要。

---

## 性能实测

环境：Windows 11 x64，.NET 11.0.100-preview.7，BenchmarkDotNet 短任务、进程内 toolchain。

### 同步执行：与手写 C# 持平

| 场景 | 手写 C# | 脚本 | 分配 |
|---|---:|---:|---:|
| decimal 公式 | 20.0 ns | 21.0 ns | 0 B |
| bool 规则 | 5.2 ns | 8.7 ns | 0 B |
| 1000 次循环 | 3100 ns | 3077 ns | 0 B |

这个结果没什么好惊讶的：两侧都是 JIT 编译的 IL，持平才是预期。1000 次循环那行脚本略快是噪声，不必当真。

### 异步执行：多一次委托调用的量级

| 场景 | 手写 C# | 脚本 |
|---|---:|---:|
| 单次 await | 12.4 ns | 25.9 ns |
| 循环内 10 次 await | 12.0 ns | 24.6 ns |

**这是本文最想给出的那个数字。** 一个运行时 emit 出来的异步方法，单次 `await` 是 25.9 ns——同一量级，不是"慢一个数量级"。

两个必须说明的点，否则这张表会骗人：

- **基线也开了 `runtime-async=on` 编译。** 如果拿脚本的 runtime-async 去比手写 C# 的经典状态机，比的是两种 lowering，不是这个引擎做得好不好。
- **被 await 的 Task 都是已完成的**，量的是 runtime-async 的快路径，不含调度开销。真挂起的场景由调度器主导，两边差异会被淹没。

还有一个数字值得单独讲：**在移除超时机制之前，同一场景要 241 ns。** 早期版本给每次异步调用都挂了 linked `CancellationTokenSource` + 定时器，光这套"安全设施"就吃掉了 90% 的时间。把它整个删掉、把取消交还给宿主之后才有上面的 25.9 ns。**异步的开销往往不在 `await` 本身，而在你顺手加在它周围的东西。**

### 编译：代价全在这里

| 场景 | 耗时 |
|---|---:|
| 同步，小脚本 | 8.6 µs |
| 同步，中等（5 条语句） | 31.0 µs |
| **异步，小脚本** | **346 µs** |
| **异步，含 await 的循环** | **629 µs** |
| 缓存命中 | 54.5 ns |

**异步比同步贵约 40 倍**，而且这 40 倍跟 runtime-async 一点关系都没有——它全部来自 collectible 程序集的创建与卸载。也就是说：

> `DynamicMethod` 不能标记 `Async` 这一个 API 缺口，全部代价就体现在这张表上。

如果哪天 `DynamicMethod` 补上 `SetImplementationFlags`，异步脚本的编译开销会直接掉回同步那一档，31 KB 的常驻内存也一起消失。

---

## 局限：请务必读完这一节

前面都是好消息，这一节是买单的地方。

### 1. `await` 不能出现在 `catch` / `finally` 里——**这会让进程直接崩溃**

这是最严重的一条。当前运行时对**处理器块内的挂起点**不提供保护：不是抛异常，不是返回错误，是**整个进程带着 `0xC0000005` 退出**。

验证工具里这条 case 必须开子进程来跑，因为它没法在本进程里安全地测：

```
OK  catch 内真正挂起的 await 会终止进程（预期如此）
    子进程退出码 -1073741819；为 0 说明运行时行为已改变，需重新评估 VS3004
```

所以 V.Script 的绑定器**在编译期无条件拒绝**这种写法（诊断码 `VS3004`），不提供任何开关。

这里有个反直觉的细节值得单独说：`AsyncHelpers.Await` 对**已完成的 Task 走快路径，根本不会到达挂起点**。所以你在 `catch` 里写 `await SomethingAlreadyCompleted()` 很可能跑得好好的——**直到某天那个 Task 真的挂起，然后线上进程没了**。正因为它"偶尔能跑通"，编译期拒绝才更有必要，而不是更没必要。

### 2. 异步脚本 31 KB 常驻，且必须显式释放

每个异步脚本一个 collectible 程序集。一万个异步脚本 ≈ 310 MB，而且只有在你 `Dispose` 掉脚本、且没有任何委托还引用着它时才会卸载。

热更新场景要按代次换新，不能无限编译新版本。同步脚本没有这个问题。

### 3. 生成的程序集失去 `skipVisibility`

`DynamicMethod` 可以用 `skipVisibility: true` 访问 globals 类型的非公开成员;独立程序集不行。

所以**异步脚本（以及带 `async` lambda 的同步脚本）只能访问公开成员**。给 lambda 加个 `async` 会改变它的可见性权限——这个副作用不明显，但确实存在。

### 4. 预览版依赖

- `AsyncHelpers` 带 `SYSLIB5007` 实验性标记，签名可能变。V.Script 把使用点全部收拢在 [`Binding/AwaitHelpers.cs`](https://github.com/fs7744/V.Script/blob/main/src/V.Script/Binding/AwaitHelpers.cs) 一个文件里，就是为了将来好改。
- [`global.json`](https://github.com/fs7744/V.Script/blob/main/global.json) 精确 pin 了预览版号（`rollForward` 没法从正式版号回退到预览版），GA 后需要改。
- **GA 后请重跑 [`tools/V.Script.RuntimeAsyncCheck`](https://github.com/fs7744/V.Script/tree/main/tools/V.Script.RuntimeAsyncCheck)**，整个异步设计都建立在它那 9 条结论上。

### 5. 与载体绑定的其它缺口

这些不是待办项，是载体选择的直接后果：

- **无调试信息**：`DynamicMethod` 和动态程序集都出不了 PDB，调试器进不去脚本。
- **不支持 NativeAOT**：异步载体依赖 `Reflection.Emit`。
- **不支持类型声明与特性**：脚本编译成的是一个方法，不是一个编译单元;同步载体连承载新类型的模块都没有。

### 6. 语言层面还没做的

`yield` 迭代器（runtime-async 只管 `await`，迭代器仍需自己写状态机）、`await foreach` / `await using`、泛型局部函数、匿名类型、`ref` 局部与返回、`Span<T>` / `stackalloc`、`dynamic`。

完整清单在仓库 [`docs/design.md`](https://github.com/fs7744/V.Script/blob/main/docs/design.md) 的「暂不支持」一节，每一项都写了为什么没做。

---

## 结语

回到最初那个好奇：**动态生成的代码能不能借 runtime-async 拿到好性能？**

答案是能。单次 `await` 25.9 ns vs 手写 12.4 ns，同步执行与手写 C# 持平，而发射端的 `await` 支持只有两行代码。runtime-async 确实把"生成异步代码"从一件需要重写编译器 lowering 的事，变成了一件发射一条 `call` 的事。

但真正的收获不在这个结论，而在过程里那几处**意料之外的成本**：

- 最贵的东西不是 runtime-async，是 `DynamicMethod` 少了一个 `SetImplementationFlags`——一个 API 缺口换来 40 倍编译开销和 31 KB 常驻内存。
- 第二贵的东西是我自己加的超时机制，占了 90% 的运行时开销，删掉才看见真实数字。
- 最危险的东西是 `catch` 里的 `await`——它会在测试里"看起来能跑"，然后在生产里带走整个进程。

这三条都不是读文档能读出来的。

代码、完整设计文档、性能基准和那个验证工具都在：

**https://github.com/fs7744/V.Script**

如果你也在 .NET 上做代码生成，[`tools/V.Script.RuntimeAsyncCheck`](https://github.com/fs7744/V.Script/tree/main/tools/V.Script.RuntimeAsyncCheck) 可能是最值得先跑一遍的东西——它会告诉你，在你手上这个运行时版本上，哪些前提还成立。
