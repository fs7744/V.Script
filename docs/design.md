# V.Script 设计文档

面向 .NET 11 的轻量脚本执行引擎：C# 语句子集，编译为强类型委托，直接复用 CLR 类型系统。
lambda 与闭包、LINQ、模式匹配，以及基于 runtime-async 的真 `async`/`await` 均已实现。

| | |
|---|---|
| 目标框架 | net11.0 |
| 后端 | 手写 IL，无 Roslyn、无解释器 |
| 代码量 | `src` 9.4k 行 / `tests` 4.0k 行 |
| 测试 | 399 全绿，构建零警告 |

本文是仓库内的正本。使用说明见 [README](../README.md)；本文讲的是**为什么这样设计**，以及每条决定背后的实测依据。

---

## 1. 决策记录

每条决定附带它排除掉的选项。标注**已修订**的是实现过程中被实测数据推翻的早期决定。

| 议题 | 决定 | 被排除的选项及原因 |
|---|---|---|
| 语法定位 | C# 语句子集 | 自定义 DSL——放弃与 C# 的直觉一致性不划算 |
| 目标框架 | 仅 net11.0 | 兼容 .NET 10——runtime-async 是 .NET 11 独有，兼容层会让后端分裂成两套 |
| NativeAOT | 不支持 | AOT 下无 `Reflection.Emit`，与真 async 硬冲突；二选一，选 async |
| 后端 | 单一手写 IL 后端 | Expression Tree 后端——无法承载 async（§2），也无法编译进 `MethodBuilder` |
| 异步支持 | 真 async（runtime-async） | 阻塞降级 `GetAwaiter().GetResult()`——占用线程 |
| 同步/异步入口 | `Compile` / `CompileAsync` 分开 | 自动推断——调用方在运行时才发现产物是异步的 |
| 卸载粒度 | 单脚本可独立回收 | 整引擎批量回收——规则频繁增删场景下内存持续增长 |
| **执行限制**<br>*已修订* | **全部移除**：无步数预算、无超时、无检查点 | 原方案为「半可信 + 资源限制」。实测表明同步侧检查点只占 1.6%，但异步侧的超时机制（每次调用一个链接 CTS 加一个定时器）让单次 await 从 26 ns 涨到 241 ns，5.3 倍。取消后异步性能翻倍，见 §12。 |
| **取消令牌**<br>*已修订* | 不进引擎 API；由宿主经 globals 或委托参数传入 | 限制移除后同步侧已无机制响应 token，保留只会让同步骗人、异步收费。见 §9。 |

---

## 2. 实证依据

整个方案的地基。全部结论由 [`tools/V.Script.RuntimeAsyncCheck`](../tools/V.Script.RuntimeAsyncCheck)
验证，可随时重跑：

```bash
dotnet run --project tools/V.Script.RuntimeAsyncCheck -c Release
```

环境：Windows 11 x64，SDK 11.0.100-preview.7.26381.103，运行时 11.0.0。

| 验证项 | 结果 | 说明 |
|---|---|---|
| `MethodImplAttributes.Async` | `0x2000` | 与 `MethodImplOptions.Async` 同值 |
| `DynamicMethod` 标记 Async | **不可能** | 类型上没有 `SetImplementationFlags`；强行调 `AsyncHelpers.Await` 得到 `NullReferenceException` |
| `AssemblyBuilder` + `MethodBuilder` | 可行 | `Run` 与 `RunAndCollect` 均成功 |
| 真实挂起 / 恢复 | 正确 | 未完成的 `TaskCompletionSource`：调用后 `IsCompleted=False`，完成后返回正确结果 |
| await 位于循环体内 | 正确 | 状态机由 JIT 生成，引擎不写一行状态机代码 |
| await 位于 `try` 块 | 正确 | — |
| await 位于 `catch` 处理器 | **进程崩溃** | 退出码 `0xC0000005`（访问冲突），无托管异常 |
| `Expression.Await` / `CompileToMethod` | 不存在 | 表达式树既不能表达挂起点，也不能编译进 `MethodBuilder` |
| `RuntimeFeature.IsSupported("Async")` | 返回 `False` | 功能可用却探测不到，无可靠特性检测 API |

### 最关键的一条，以及它的陷阱

`catch` / `finally` 内的挂起点会让进程**直接崩溃**，而不是抛出可捕获的异常。运行时不提供任何
安全网，因此这条限制由 Binder 在编译期无条件拒绝（`VS3004`），不设开关。

真正阴险的地方在于**它不总是崩**：

- 被 await 的任务若**已完成**，`AsyncHelpers.Await` 走快路径，根本不到挂起点，`catch` 里的
  await 完全正常。
- 只有任务**真的挂起**时才崩溃。

也就是说这个 bug 在单元测试里（通常 mock 成已完成的任务）大概率是绿的，到生产环境遇到真实
网络延迟才炸。同理，`finally` 里的 await 在正常路径上也能跑通，只在异常展开时崩溃。这三重
偶然性正是「无条件拒绝、不给开关」的理由。

### 一条推论

生成的动态程序集**不需要**宿主项目开启 `<Features>runtime-async=on</Features>`——那个开关只
影响 C# 编译器。引擎直接发 IL 并设置实现标志，只依赖 .NET 11 运行时本身。

---

## 3. 架构分层

工程量分布很不均匀：Binder 占七成。

```
Lexer  →  Parser  →  Binder  →  BoundTree  →  IlEmitter
零分配扫描  Pratt 解析   类型解析、转换、    每节点带 Type   写入 ILGenerator
           + 回溯消歧   重载决议、泛型推断、  所有隐式语义     与载体无关
                      闭包捕获、模式降低    已显式化
```

### 核心不变量

**Binder 把一切显式化，发射器只挑指令。**

隐式转换变成 `BoundConversion`，可空提升展开，重载定为具体 `MethodInfo`，`params` 变成数组
节点，`foreach` 与复合赋值降低为已有节点，哪些变量被捕获、捕获进哪个作用域在绑定期决定，模式
匹配降低为类型测试 + 比较 + 短路逻辑。

验收标准很直白：**`IlEmitter` 里出现一行 `if (type == typeof(...))` 就是设计泄漏。**
模式匹配是这条原则的最好例证——发射器里没有任何模式相关代码。

### 单发射器，双载体

`DynamicMethod` 与 `MethodBuilder` 都提供 `ILGenerator`，同一套发射代码打两个靶子。Binder
绑定完成时已知脚本是否含 `await`，据此选载体。

| | 载体 | 编译耗时 | 单独卸载 |
|---|---|---:|---|
| 同步脚本 | `DynamicMethod` | 7.5 µs | 随委托自动回收 |
| 异步脚本 | 独占 collectible 程序集 | 556 µs | `Dispose()` |

**lambda 永远是 `DynamicMethod`**，无论所属脚本用哪种载体——lambda 体内不允许挂起点，所以
不需要 Async 标志。

---

## 4. 产物与生命周期

```csharp
// 同步脚本 → DynamicMethod
static TResult __Script(ScriptHost host, TGlobals g);

// 异步脚本 → 独占 collectible 程序集中的静态方法
[MethodImpl(MethodImplOptions.Async)]     // 经 SetImplementationFlags(0x2000)
static Task<TResult> __Script(ScriptHost host, TGlobals g);

// lambda → 独立 DynamicMethod，首参为闭包
static TResult __lambda0(ScriptClosure closure, T0 p0);
```

### 为什么 lambda 需要宿主构造委托

生成代码**无法用 `ldftn` 引用 `DynamicMethod`**，所以不能直接构造指向 lambda 的委托。解法是
把编译好的方法登记在 `ScriptHost` 的表里，运行期由宿主构造：

- **不捕获**任何变量的 lambda：编译期就建好委托并缓存，每次求值零开销。
- **捕获型**：走 `ClosureBinder`——编译期建好「以闭包为首参」的开放委托，运行期只做一次闭包包装。

直接用 `DynamicMethod.CreateDelegate(type, closure)` 每次求值需 **419 ns**；改为预建开放委托后
降到 **68 ns**，6 倍。捕获型 lambda 在 LINQ 谓词里极常见，这个悬崖必须填平。

### 回收语义

同步脚本随委托被 GC 回收，无需管理。异步脚本每个独占一个 `RunAndCollect` 程序集，委托、
`Type`、`AssemblyBuilder` 全部失去引用后运行时异步卸载。

覆盖测试用 `WeakReference` 直接断言生成类型与程序集变为不可达，而非测量进程内存——后者在大量
分配后噪声太大，是不可靠的信号（早先的内存测试正因如此而 flaky）。

```csharp
// 按代次换新
var next = new ScriptEngine(options);
foreach (var r in newRules) next.CompileAsync<Ctx, bool>(r.Expr);
Interlocked.Exchange(ref _current, next).Dispose();   // 旧代整体回收
```

---

## 5. 类型系统

脚本不引入自有类型模型——`System.Type` 就是类型系统。这是工程量的主体，也是 bug 的主要来源。

已实现：

- **转换分类**——Identity / ImplicitNumeric / ImplicitReference / Boxing / ImplicitNullable /
  ImplicitEnum / UserDefined / Explicit，数值转换表用 `FrozenDictionary` 预建
- **二元数值提升**——ECMA-334 §12.4.7 全表，含常量表达式转换（`u / 2` 中字面量重解释为 `uint`）
- **可空提升**——算术传播 null、关系运算返回 `false`、相等运算两个 null 相等
- **重载决议**——§12.6.4 子集：适用性筛选、`params` 展开、可选与命名参数、betterness 比较
- **泛型方法类型推断**——§12.6.3 的两轮形式：先由普通实参定型参，再用 lambda 体的自然返回类型
  定剩余型参
- **扩展方法**——按 imports 建索引，仅在普通成员查找失败后参与
- 运算符重载与用户定义转换、成员/静态/索引器访问、完全限定名、枚举运算

### 让 LINQ 重载集能解析的关键一条

lambda 实参**必须参与** betterness 比较，否则 `Sum(Func<T,int>)` 与 `Sum(Func<T,double>)` 等
一系列重载会全部「同样好」而判为歧义。实现方式是探测 lambda 的自然返回类型，再用它比较各候选的
委托返回类型。另需两条 tie-break：

1. 非泛型优于推断出的泛型（解决 `Max()`）
2. 转换种类排序：引用转换优于用户定义转换，这样 `int[]` 绑到 `IEnumerable<T>` 而不是
   `ReadOnlySpan<T>`（解决 `Contains(3)`）

### 验收手段：差分测试

同一段表达式分别用真 C# 编译器与引擎求值，断言结果一致。语料覆盖 `int.MinValue`、
`uint.MaxValue`、`NaN`、正负无穷、全部可空组合、窄化转换、移位掩码。

**这是保证类型语义正确的唯一可靠手段**——数值提升与可空提升靠人工用例覆盖不住。实现期发现的
三个真实缺陷（`uint / 2` 缺常量收窄、枚举成员是 literal 字段不能 `ldsfld`、嵌套 `?.` 只短路
一次）全部由差分测试逼出。

---

## 6. lambda 与闭包

lambda、泛型推断、扩展方法三者合起来才让 LINQ 可用；缺任何一个都不行。

### 闭包按作用域实例化

捕获是**按引用**的：外层脚本与 lambda 共用同一个存储槽，之后的写入对两侧都可见。闭包对象在
**进入作用域时**创建，而不是按方法创建——这样 C# 的作用域寿命语义才对：

| 构造 | C# 语义 | 测试结果 |
|---|---|---|
| `foreach (var n in ...)` | 每轮新变量 | `123`——三个 lambda 各自看到 1/2/3 |
| `for (var i = 0; ...)` | 整个循环一个变量 | `333`——都看到终值 |
| 块内 `var copy = i;` | 每次进入块新变量 | 每个 lambda 独立 |

### 块体 lambda

表达式体与块体都支持，块体内可含循环、`try`/`catch`/`finally`、多分支 `return`。块体不比
表达式体贵——两者编译成同一种方法。

块体的返回类型同样参与泛型推断：探测器会收集块内 `return` 语句的类型并取公共类型，所以
`Select(x => { var s = x * 3; return s; })` 能推出 `TResult`。

```csharp
var floor = MinimumQuantity;

return Order.Items
    .Where(i => i.Quantity >= floor)
    .Sum(i => i.Price * i.Quantity);
```

一句话里同时用到：从 imports 找到的扩展方法、从序列**和** lambda 体两个方向推出的泛型参数、
以及从外层作用域捕获的 `floor`。

---

## 7. 模式匹配

全部降低为普通有界表达式——类型测试、null 测试、比较、短路逻辑。发射器里没有任何模式相关代码。

覆盖：类型模式（可带变量绑定）、常量模式（含枚举成员）、关系模式、`and`/`or`/`not`、`var`、
`_`、属性模式（可带类型与变量，可嵌套）、`switch` 表达式含 `when` 守卫。

```csharp
return Shape switch
{
    Circle c when c.Radius > 10.0 => "big circle",
    Circle => "circle",
    Rectangle { Width: > 0, Height: > 0 } r => "rect " + r.Width,
    null => "none",
    _ => "other",
};
```

两个语义细节：

- **合取收窄**：`x is int n and > 1` 中右侧比较的是收窄后的 `int`，不是原始 `object`。
  `not` 不收窄（否定不提供类型信息），`or` 也不收窄（两侧可能收窄到不同类型）。
- **每个分支是独立命名作用域**，两个分支可以都把变量叫 `s`。闭包作用域则与外层语句共用，因为
  switch 表达式没有自己的运行期作用域可供实例化。

无分支匹配时抛 `SwitchExpressionException`，而不是静默返回默认值。

**与 C# 的一处偏差**：引擎不做明确赋值分析。在模式未匹配的路径上读取模式变量会得到 `default`，
而不是像 C# 那样报编译错误。

---

## 8. 异步

状态机由 JIT 生成，引擎只发直线 IL。这消除了传统方案中最难的部分：控制流切分、跨挂起点的活跃
变量分析、局部变量提升。

```
// 脚本
var o = await Orders.GetAsync(id);

// 发射
callvirt IOrderService.GetAsync(int)            // Task<Order>
call     AsyncHelpers.Await<Order>(Task<Order>) // 返回 Order
stloc    o
```

方法签名声明返回 `Task<T>`，IL 体返回未包装的 `T`，包装由运行时完成。整个 await 支持在发射侧
只有一个操作数加一次调用。

### RunAsync 不做任何包装

`RunAsync` 原样返回生成方法自己的 `Task`——没有包装状态机，没有 `ValueTask`，没有定时器。这是
异步调用能和同步调用一样便宜的原因。早期版本的 `async ValueTask` 包装本身就是白付的开销：去掉后
单次 await 从 47.6 ns 降到 25.9 ns。

**硬限制**：`await` 不得出现在 `catch` 或 `finally` 中（`VS3004`，见 §2），也不得出现在
lambda 内（`VS9006`，lambda 编译为独立同步方法）。

---

## 9. 取消与安全边界

引擎**不施加任何执行限制**：没有步数预算、没有超时、没有注入检查点。`Run` 与 `RunAsync` 就是
一次委托调用，两者都不接受 `CancellationToken`。

这是为吞吐做的取舍。循环回边上的检查点只占 1.6%，但让**挂起中**的脚本可被打断的那套机制代价
远大于此——每次调用一个链接 CTS 加一个定时器注册，实测使单次 await 从 26 ns 涨到 241 ns，而且
只对 `await` 有效。

### 取消交给宿主，两条路径

**① 经 globals**（每次调用传入，因此是 per-invocation 的）：

```csharp
public sealed class Ctx
{
    public CancellationToken Token { get; init; }
    public IOrderService Orders { get; init; }
}
```
```csharp
var order = await Orders.GetAsync(id, Token);   // 脚本
```
```csharp
await script.RunAsync(new Ctx { Token = cts.Token, Orders = orders });
```

**② 作为委托参数**（`CancellationToken` 只是普通类型，无需特殊处理）：

```csharp
using var f = engine.CompileAsyncDelegate<Func<IOrderService, CancellationToken, Task<int>>>(
    "(await svc.GetAsync(1, ct)).Total", "svc", "ct");

int total = await f.Value(orders, cts.Token);
```

两条路径都有测试覆盖。这比引擎代管更灵活——脚本自己决定哪些调用可取消——且不用时零开销。

### 必须明确的后果

**死循环脚本不会自己停下来。** 不可信或可被人编辑的脚本，要跑在你愿意放弃的线程上，或在编译前
自行校验。引擎不再管这件事。

---

## 10. API 契约

```csharp
using var engine = new ScriptEngine(ScriptOptions.Default
    .AddReferencesFrom(typeof(Order))
    .AddImports("MyApp.Model"));

using var script = engine.Compile<PricingCtx, decimal>(
    "Order.Items.Sum(i => i.Price * i.Qty) * (1 + TaxRate)");

decimal v = script.Run(ctx);
```

Globals 的公开实例成员可作为裸标识符访问，编译期解析为成员访问，**不是字典查找**；局部变量是
真正的 IL 局部。脚本最后一条语句若是裸表达式即为返回值，无需 `return` 也无需结尾分号。

四个编译入口：

```csharp
Script<TG, TR>       Compile<TG, TR>(source)               // 同步，遇 await 报 VS3001
AsyncScript<TG, TR>  CompileAsync<TG, TR>(source)          // 异步
TDelegate            CompileDelegate<TD>(source, names)    // 同步，直接返回委托
ScriptDelegate<TD>   CompileAsyncDelegate<TD>(src, names)  // 异步，可释放
```

`CompileAsyncDelegate` 返回可释放的包装，因为生成程序集按脚本持有；`CompileDelegate` 直接返回
委托，`DynamicMethod` 无需显式释放。

### 诊断一次报全

Binder 遇错产出错误节点并继续绑定，一次编译报出全部问题：

```
Error VS2003 (3,17): 方法 'IOrderService.GetAsync' 没有匹配 (string) 的重载；候选: ...
Error VS2005 (7,9):  无法将 string 转换为 decimal。
Error VS2002 (9,22): Order 不包含名为 'Totl' 的成员。是否想用 'Total'?
Error VS3004 (14,13): 'await' 不能出现在 catch 或 finally 块中。
```

编码分组：`1xxx` 词法/语法，`2xxx` 绑定与类型，`3xxx` 异步与控制流，`9xxx` 尚未实现的构造。
每个未实现构造都有专属码，而不是笼统的类型错误。

---

## 11. 语言覆盖面

下表由 [`tools/V.Script.Probe`](../tools/V.Script.Probe) 逐条编译验证得出，不是凭印象罗列。
前端改动后重跑它，不要手改本表：

```bash
dotnet run --project tools/V.Script.Probe -c Release
```

### 已支持

全部 C# 数值字面量形式、`var` 与显式声明、赋值与复合赋值（含 `<<=` `>>=`）、`++`/`--`、完整
算术/关系/逻辑/位运算含数值提升与可空提升、`?.` `??` `??=` `is` `as` `typeof`、强制转换、成员
与静态访问、**完全限定名**、索引器、含重载决议的方法调用（`params`、可选与命名参数）、运算符
重载与用户定义转换、对象创建、`if`/`while`/`do`/`for`/`foreach`/`break`/`continue`/`return`、
`try`/`catch`/`finally`/`throw`、`await`、lambda 与闭包（表达式体与块体）、泛型方法推断、扩展
方法、模式匹配与 `switch` 表达式、锯齿数组。

### 未实现

| 构造 | 示例 | 性质 |
|---|---|---|
| 对象初始化器 | `new Item { Count = 1 }` | 语法未支持 |
| 集合初始化器 | `new List<int> { 1, 2 }` | 语法未支持 |
| 集合表达式 | `int[] a = [1, 2, 3]` | 语法未支持 |
| 数组创建 | `new int[3]` / `new[] { 1, 2 }` | 语法未支持 |
| 显式类型实参 | `xs.Cast<int>()` | 语法未支持（推断可用时无影响） |
| 插值字符串 | `$"n={N}"` | 词法未支持 |
| 逐字 / 原始字符串 | `@"a\b"` / `"""abc"""` | 词法未支持 |
| `nameof` | `nameof(N)` | 未支持 |
| `default` | `default` / `default(T)` | 未支持 |
| 元组与解构 | `(1, 2)` / `var (a, b) = ...` | 语法未支持 |
| `switch` **语句** | `switch (x) { case ... }` | 仅表达式形式可用 |
| `goto` / `lock` / `using` 语句 | — | 未支持 |
| `checked` / `unchecked` | `checked(a + b)` | 未支持，算术一律 unchecked |
| throw 表达式 | `x ?? throw new ...` | `throw` 只能作语句 |
| 局部函数 | `int F(int x) { ... }` | 未支持（用 lambda 替代） |
| 位置模式 / 列表模式 | `is (a, b)` / `is [1, 2]` | 未支持 |
| 多维数组 | `a[0, 0]` | 仅一维与锯齿数组 |
| `ref` / `out` 参数 | `int.TryParse(s, out x)` | 重载决议直接跳过这类候选 |

### 非语法层面的缺口

- **方法组推断**：`xs.Select(Foo)` 不支持，必须写成 lambda
- **公共类型推断**：一个型参由多个实参推断时取首次绑定，不计算 best common type
- **明确赋值分析**：未实现，模式变量在未匹配路径上读到 `default`
- **无调试信息**：`DynamicMethod` 与动态程序集都出不了 PDB，调试器不能单步进脚本
- **无 NativeAOT**：异步载体依赖 `Reflection.Emit`

---

## 12. 实测性能

Windows 11 x64，.NET 11.0.100-preview.7，BenchmarkDotNet 短任务，进程内 toolchain。

```bash
dotnet run --project bench/V.Script.Benchmarks -c Release -- --filter "*"
```

### 执行：与手写 C# 持平

| 场景 | 手写 C# | 脚本 | 分配 |
|---|---:|---:|---:|
| decimal 公式 | 20.0 ns | 21.0 ns | 0 B |
| bool 规则 | 5.2 ns | 8.7 ns | 0 B |
| 1000 次循环 | 3100 ns | 3077 ns | 0 B |

两侧都是 JIT 编译的 IL，持平是预期结果。

### 异步

| 场景 | 手写 C# | 脚本 |
|---|---:|---:|
| 单次 await | 12.4 ns | 25.9 ns |
| 循环内 10 次 await | 12.0 ns | 24.6 ns |

基线同样开启 `runtime-async=on` 编译，否则比较的是 runtime-async 与经典状态机，不公平。移除
超时机制前，同一场景开限制需 241 ns。

### lambda、LINQ 与模式匹配

| 场景 | 手写 C# | 脚本 | 分配 |
|---|---:|---:|---:|
| 谓词，无捕获 | 3.9 ns | 19.9 ns | 0 B |
| 谓词，有捕获 | — | 63.8 ns | 184 B |
| 块体谓词，有捕获 | 13.2 ns | 58.4 ns | 160 B |
| LINQ `Where`/`Select`/`Sum` | 33.8 ns | 50.2 ns | 104 B |
| `switch` 表达式 ×8 | 9.3 ns | 23.4 ns | 0 B |
| `is` 类型模式 ×8 | 8.0 ns | 18.2 ns | 0 B |

不捕获的 lambda 每次求值零开销，委托在编译期建好；捕获型每次分配闭包并绑定委托。模式匹配约合
每次分类 1.8 ns、每次类型测试 1.3 ns，零分配。无捕获谓词剩下的差距是内联——JIT 会把 C# lambda
内联进调用方，脚本的委托对它不透明。

### 编译

| 场景 | 耗时 |
|---|---:|
| 同步，小脚本 | 7.5 µs |
| 同步，中等（5 条语句） | 26.7 µs |
| 异步，小脚本 | 556 µs |
| 异步，含 await 的循环 | 597 µs |
| 缓存命中 | 54.5 ns |

异步比同步贵约 70 倍，全部来自 collectible 程序集的创建与卸载。这就是 `DynamicMethod` 无法
标记 `Async` 这一个 API 缺口的全部代价。

---

## 13. 风险与未决

| 项 | 影响 | 应对 |
|---|---|---|
| **.NET 11 尚未 GA** | 目前基于 preview 7；GA 预计 2026 年 11 月 | GA 后重跑 `tools/V.Script.RuntimeAsyncCheck`。`global.json` 精确 pin 了预览版号（`rollForward` 无法从正式版号回退到预览版），GA 后需改 |
| **`AsyncHelpers` 标记 `SYSLIB5007`** | 实验性 API，签名可能变更 | 使用点集中在 `Binding/AwaitHelpers.cs` 单个文件 |
| **无执行限制** | 死循环脚本会占住线程直到进程结束 | 不可信脚本需宿主侧校验，或跑在可放弃的线程上 |
| **无调试器支持** | 脚本无法单步调试 | 行号映射 + 结构化诊断；必要时提供脚本级 trace |
| **异步脚本 31 KB 固定开销** | 一万个异步脚本约 310 MB | 按代次换新；同步脚本无此开销 |
| **捕获值经装箱存取** | 闭包槽是 `object[]`，值类型捕获有装箱开销 | 可改为按 arity 特化的强类型闭包，尚未实现 |

### 下一步候选

按实用性排序：

1. **对象与集合初始化器**——脚本里构造对象目前很别扭，这是最常被绊到的一处
2. **插值字符串**——规则脚本生成消息文本的常见需求
3. **显式类型实参**与**方法组推断**——补齐 LINQ 的边角
4. **`switch` 语句**与**局部函数**——块体 lambda 已部分替代后者
