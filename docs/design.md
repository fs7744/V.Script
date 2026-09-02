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

探针目前 71 项全部通过。覆盖面按类别：

| 类别 | 内容 |
|---|---|
| 字面量 | 全部数值形式、字符、字符串、逐字 `@"..."`、原始 `"""..."""`、插值（含 `$@""`、`$$"""..."""`、对齐与格式说明符） |
| 表达式 | 完整算术/关系/逻辑/位运算含数值提升与可空提升、`?.` `??` `??=` `is` `as` `typeof`、`nameof`、`default` / `default(T)`、throw 表达式、`checked` / `unchecked`、**`^i` 与 `a..b`**、**`with`**、强制转换、条件表达式 |
| 名称与成员 | 局部变量、globals 成员、完全限定名、嵌套类型、静态成员、索引器、扩展方法 |
| 类型 | 泛型、可空值类型 `int?`、可空引用注解 `string?`（接受后忽略）、**`nint` / `nuint`**、数组、多维数组、元组 |
| 调用 | 重载决议（`params`、可选与命名参数）、泛型推断（含公共类型）、显式类型实参、**`ref` / `out` 实参**（含 `out var x`）、**方法组转委托** |
| 构造 | 对象创建、委托创建 `new Func<...>(f)`、对象/集合/**索引**/**嵌套**初始化器、数组三种写法、**多维数组**、集合表达式（含 **`..` 展开**） |
| 函数 | lambda（表达式体与块体，形参可写类型，可推断自然委托类型）、**`async` lambda**、闭包、局部函数（含递归、互递归、**`static`**、**`async`**） |
| 元组 | 字面量、类型、元素名、**任意元数**（超过 7 元自动嵌套 `Rest`）、解构（`var (a, b)`、`(a, b) =`、混合、`Deconstruct` 方法） |
| 语句 | `if`/`while`/`do`/`for`/`foreach`/`break`/`continue`/`return`、`switch`、`try`/`catch`/`finally`/`throw`、`using`（含 `using var`）、`lock`、标签与 `goto`（含 `goto case` / `goto default`）、**局部 `const`** |
| 模式 | 常量、类型、关系、`and`/`or`/`not`、属性、`var`、丢弃、**位置**、**列表**（含 `..` 切片），以及 `switch` 表达式 |
| 分析 | 必定返回检查、`switch` 落空检查、**明确赋值分析** |
| 查询 | **LINQ 查询语法**：`from` / `where` / `select` / `orderby` / `let` / 多重 `from` / `join`（含 `into`）/ `group by` / `into` |
| 预处理 | **`#if` / `#elif` / `#else` / `#endif`**，符号由 `ScriptOptions.AddPreprocessorSymbols` 提供 |
| 异步 | `await`，真正的 runtime-async 状态机 |

### 与 C# 的行为差异

这些构造可用，但语义不完全等同，写脚本时需要知道：

- **`switch` 分支不能落空**：C# 里落空是编译错误，引擎同样报错（VS3006），但引擎的检查更保守——
  只有 `return`/`throw`/`break`/`continue`/`goto` 以及必定跳转的 `if`/`try` 才算合法结尾
- **`case` 的模式变量落在外层作用域**，与 `if (x is T t)` 一致。因此两个 `case` 不能重用同一个
  指示符名字，而 C# 允许
- **`nameof(T.Member)` 不校验成员是否存在**，只取最后一个标识符；裸名字则会校验
- **`nameof` 在调用位置是保留的**：脚本里名为 `nameof` 的委托变量不能被调用（读取仍可以）
- **集合表达式转 `List<T>` 走 `Add`**，带 `..` 展开时先收进 `List<T>` 再交给目标——都比 C# 的
  长度计数降级多一次拷贝，见 §12 的实测差距
- **局部函数是委托，不是方法**：签名必须能表示成 `Func`/`Action`（≤16 个参数、无 `ref`/`out`、
  无泛型形参、无默认值与 `params`）。好处是它可以直接当委托传给 LINQ
- **局部函数只能声明在语句块中**，写在 `if`/`for` 的单语句体里会报错
- **局部函数的委托在所属块的开头统一赋值**，因此在它上面调用是合法的（与 C# 一致）；但函数体
  按书写位置绑定，所以引用声明在它下面的变量会报错，这也与 C# 的 CS0841 一致
- **`var f = (int x) => ...` 的自然类型**只在每个形参都写出类型时才成立，与 C# 相同；返回类型
  从函数体推断，无值即为 `Action`
- **元组元素名只在能静态看见来源时可用**：字面量、声明该变量的元组类型，或成员上的
  `TupleElementNamesAttribute`。名字经过一次 `object` 转换后就丢了，此时只能用 `ItemN`
- **元组元素类型必须确定**：`(1, null)` 报错，C# 会目标类型化。写成 `(1, (string)null)` 即可
- **`ref` / `out` 实参必须是未被捕获的局部变量**：闭包槽是 `object[]`，没有地址可取。被 lambda
  捕获过的变量会报错，提示改用临时变量
- **明确赋值分析在看不清的地方偏向放行**：被捕获的变量不参与判断（赋值可能发生在另一个函数里），
  而函数里一旦出现标签，其后不再跟踪——`goto` 可以从任何地方跳来。宁可漏报也不误报
- **`goto` 不能跳进 `try`/`catch`/`finally`**，与 IL 的规则一致，编译期就会报错
- **`async` lambda 所在的脚本会多一个程序集**：`async` 需要 `MethodImplAttributes.Async`，
  而 `DynamicMethod` 表达不了它（§3）。同步脚本里出现 `async` lambda 时，脚本体仍是
  `DynamicMethod`，只有这些 lambda 被放进一个可回收程序集。副作用是这些 lambda 失去了
  `skipVisibility`，访问 globals 的非公开成员会失败——与异步脚本本来的限制相同
- **查询语法用元组做透明标识符**：`let` 与多重 `from` 之后，后续子句的 lambda 形参是一个元组，
  函数体先把它拆成局部变量。C# 用匿名类型，结果一样，但错误信息里会出现 `<>q` 这个名字
- **局部 `const` 没有存储**：它在每个使用点被折叠成字面量，因此也不能被赋值
- **`static` 局部函数只禁止捕获**，它自己的局部变量与嵌套函数照常工作
- **`nint` / `nuint` 的常量按 32 位判断是否越界**，这是 C# 在所有平台都保证的范围

### 非语法层面的缺口

- **无调试信息**：`DynamicMethod` 与动态程序集都出不了 PDB，调试器不能单步进脚本。这不是待办
  项而是载体选择的后果——见 §3
- **无 NativeAOT**：异步载体依赖 `Reflection.Emit`，同样是载体选择的后果

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

### 对象构造与插值字符串

| 场景 | 手写 C# | 脚本 | 分配 |
|---|---:|---:|---:|
| 对象初始化器 | 4.7 ns | 5.6 ns | 32 B（两侧相同） |
| 集合初始化器 ×4 | 12.2 ns | 13.6 ns | 72 B（两侧相同） |
| `new[] { a, b, c }` | 4.3 ns | 5.2 ns | 40 B（两侧相同） |
| `$"{Name}#{Id}"` | 28.4 ns | 18.1 ns | 72 B / 80 B |
| `$"{Name,-10}#{Amount:F2}"` | 141.3 ns | 178.3 ns | 88 B（两侧相同） |
| `[a, b, c]` → `int[]` | 4.5 ns | 5.3 ns | 40 B（两侧相同） |
| `[1, 2, 3, 4]` → `List<int>` | 9.0 ns | 13.0 ns | 72 B（两侧相同） |

构造型写法都只比手写多一次委托调用，分配量逐字节相同——它们降级成的 IL 与 C# 编译器产出的
是同一份。唯一的例外是集合表达式转 `List<T>`：C# 把它降级成一次 span 拷贝，引擎按集合初始化器
的方式逐个 `Add`，所以 4 个元素多花约 4 ns。转数组（以及数组能满足的接口）两侧完全一致。

两行插值字符串则是**降级方式不同**，值得单独看：

- 无格式说明符时脚本降级为 `string.Concat`，C# 编译器降级为 `DefaultInterpolatedStringHandler`。
  手写同一个 `string.Concat(object, object)` 是 14.4 ns，脚本 18.1 ns——差的 3.7 ns 是委托与
  globals 读取。脚本比 C# 快，是因为这个规模下 handler 的缓冲区租借比直接 `Concat` 更贵，代价是
  多一次装箱（80 B vs 72 B）。
- 有格式说明符时脚本降级为 `string.Format`，比 handler 慢约 30%。这是已知取舍：走 handler 需要
  发射 `ref struct` 上的一串 `AppendFormatted<T>` 调用，绑定器要为此引入泛型实例化与 `ref` 局部，
  当前不值得。真正在意这一路的脚本可以改用无格式的插值加显式 `ToString(...)`。

**基准写法注意**：三个构造型基准都把建好的对象返回出去。若丢弃返回值，JIT 会证明分配不逃逸并
整个消除掉——C# 一侧消除得掉、脚本一侧消除不掉，量到的就成了逃逸分析而不是降级质量。

### 局部函数

| 场景 | 手写 C# | 脚本 | 分配 |
|---|---:|---:|---:|
| 递归 `Fib(20)`（约 1.3 万次调用） | 16.5 µs | 78.3 µs | 160 B |
| 100 次非递归辅助调用 | 48.7 ns | 178.6 ns | 0 B |
| 同上，写成 lambda | — | 177.7 ns | 0 B |

后两行几乎相同，这正是设计意图：局部函数与 lambda 降级成同一样东西，写法不同而已。

每次调用比手写多约 1.3 ns（委托调用），递归时再加一次闭包读取，所以 `Fib` 这种全是调用的负载
差到 4.7 倍。**不捕获也不递归**的局部函数零分配——委托在编译期建好并共享；递归的那 160 B 是
每次 `Run` 一个闭包，不是每次调用。

热路径上的递归请改写成循环，或把工作交给宿主方法。

### 元组与解构

这里没有手写 C# 基线：这些负载都是字段的纯函数，C# 一侧会被 JIT 整个提到测量循环外、读起来像
免费，脚本一侧因为隔着委托提不出去——比出来的是这件事，不是降级质量。基线换成一个什么都不做的
脚本，每行超出它的部分才是元组本身的开销。

| 场景 | 耗时 | 超出基线 | 分配 |
|---|---:|---:|---:|
| 基线：`return Id;` | 0.87 ns | — | 0 B |
| `return (Id, Id + 1);` | 0.94 ns | +0.07 ns | 0 B |
| 具名元素读写 | 0.95 ns | +0.08 ns | 0 B |
| 局部函数返回元组后解构 | 3.32 ns | +2.45 ns | 0 B |

前三行说明两件事：构造一个二元组几乎不要钱（就是几条 `ldloc` 加一次 `newobj` 到栈上的
`ValueTuple`），**具名与位置写法完全等价**——名字只活在编译期。最后一行的 2.45 ns 主要是局部
函数那次委托调用（见上一节），不是解构。全部零分配。

### 编译

| 场景 | 耗时 |
|---|---:|
| 同步，小脚本 | 8.6 µs |
| 同步，中等（5 条语句） | 31.0 µs |
| 异步，小脚本 | 346 µs |
| 异步，含 await 的循环 | 629 µs |
| 缓存命中 | 54.5 ns |

其中明确赋值分析约占中等脚本的 4%（关掉它是 29.6 µs）。它按语句遍历绑定后的树，用的是持久化
集合而不是每次赋值拷贝一份——先用 `HashSet` 写时代价是 34%，换成 `ImmutableHashSet` 后降到 4%。

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
| **闭包槽没有地址** | 被捕获的变量不能作 `ref`/`out` 实参，也不参与明确赋值分析 | 同上：强类型闭包会一并解决这两处 |

### 暂不支持

下面这些经过逐条编译验证确认不支持。列在这里是为了**明确边界**，不是待办清单——每一项后面
写的是它为什么没做。

**需要一台状态机**

| 构造 | 说明 |
|---|---|
| 迭代器 `yield return` / `yield break` | runtime-async 只管 `await`，迭代器要自己写状态机 |
| `await foreach` / `await using` | 需要 `IAsyncEnumerable` / `IAsyncDisposable` 的降级，前提是上一条 |

**签名超出 `Func` / `Action` 的表达范围**

局部函数与 lambda 都编译成委托（见 §11 的行为差异），所以下面这些签名无处安放：

| 构造 | 说明 |
|---|---|
| 泛型局部函数 `T F<T>(T x)` | 委托类型必须是具化的 |
| 局部函数 / lambda 的默认参数值 | `Func` / `Action` 没有默认值的概念 |
| 局部函数 / lambda 的 `params` | 同上 |
| 超过 16 个参数的局部函数 | `Func` / `Action` 的最大元数 |

**需要发射类型，而同步载体没有模块**

| 构造 | 说明 |
|---|---|
| 匿名类型 `new { X = 1 }` | 每种形状要一个类型 |
| 类型声明 `class` / `struct` / `record` | 脚本编译成的是一个方法，不是编译单元 |
| 特性 | 没有可以附着的声明 |

**需要新的语义机制**

| 构造 | 说明 |
|---|---|
| `ref` 局部与 `ref` 返回 | 绑定器需要"引用值"概念，不只是实参位置上的地址 |
| `Span<T>` / `stackalloc` / `unsafe` / 指针 | 需要栈上生命周期规则，否则很容易发出不安全的 IL |
| `dynamic` | 需要运行时绑定器，代价与收益不成比例 |
| 可空性分析 | `string?` 被接受但注解被丢弃，不做流敏感的空值检查 |

**其它**

| 构造 | 说明 |
|---|---|
| `#define` / `#region` / `#line` 等指令 | 只实现了条件编译；符号从 `ScriptOptions` 传入，脚本内不能定义 |
| 查询语法的 `join ... on ... equals ... into` 之外的形态 | `orderby` 的自定义比较器、`group ... by ... into` 的多级延续等未覆盖 |

### 下一步候选

已经没有"补一个语法"就能拿下的项了。真要继续，按投入产出排：

1. **迭代器**——手写状态机的工作量最大，但它是唯一一个脚本作者会反复撞上的缺口
2. **泛型局部函数与默认参数**——需要把局部函数从"委托变量"改成真正发射的方法，会顺带解开
   `params`、默认值和 16 元上限
3. **匿名类型**——同步载体也发一个程序集就能做，代价是每个脚本都多一份 31 KB
