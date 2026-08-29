# V.Script

A lightweight script execution engine for .NET 11. Scripts are a subset of C# statements, they
bind against the real CLR type system, and they compile straight to IL — no Roslyn, no
interpreter, and `async`/`await` works for real.

```csharp
using var engine = new ScriptEngine();

using var pricing = engine.Compile<PricingContext, decimal>(
    "Price * Quantity * (1 - Discount) * (1 + TaxRate)");

decimal total = pricing.Run(context);
```

## Requirements

.NET 11 SDK. The engine depends on **runtime async**, which is a .NET 11 runtime feature: the
JIT builds the state machine from a method marked `MethodImplOptions.Async`, so the compiler
only has to emit straight-line IL with a call to `AsyncHelpers.Await`. There is no fallback for
earlier runtimes, and NativeAOT is not supported (the async carrier needs `Reflection.Emit`).

`AsyncHelpers` is still marked `[Experimental("SYSLIB5007")]`; the project suppresses that
warning in one place, and all use of it is confined to `Binding/AwaitHelpers.cs`.

`global.json` pins the exact preview SDK, because `rollForward` cannot move from a release
version number onto a prerelease of the same version. Bump that pin — or delete the file — once
.NET 11 reaches GA.

## Two carriers, one emitter

`IlEmitter` writes to a bare `ILGenerator`, and both carriers supply one. Which carrier is used
depends only on whether the script contains `await`:

| | Carrier | Compile | Memory | Individually unloadable |
|---|---|---:|---:|---|
| Synchronous | `DynamicMethod` | 8.9 µs | ~9.7 KB churn | yes, with the delegate |
| Asynchronous | own collectible assembly | 378 µs | ~31 KB resident | yes, via `Dispose()` |

`DynamicMethod` has no `SetImplementationFlags`, so it cannot be marked `Async` — that single
API gap is the entire reason asynchronous scripts cost 40× more to compile. Retiring an
asynchronous script releases its assembly; a covering test asserts the memory actually comes
back.

## Usage

### Globals object

Public instance members of `TGlobals` are reachable as bare identifiers. They resolve at compile
time to member access, not dictionary lookups, and locals become real IL locals.

```csharp
public sealed class PricingContext
{
    public decimal Price { get; init; }
    public int Quantity { get; init; }
    public decimal TaxRate { get; init; }
}

using var script = engine.Compile<PricingContext, decimal>("""
    var subtotal = Price * Quantity;
    if (Quantity >= 10) subtotal *= 0.95m;
    return subtotal * (1 + TaxRate);
    """);
```

A script whose last statement is a bare expression returns it, so `"Price * Quantity"` needs no
`return` and no trailing semicolon.

### Asynchronous scripts

`Compile` and `CompileAsync` are separate on purpose: a synchronous compile that meets `await`
fails with `VS3001` rather than silently handing back something that must be awaited.

```csharp
using var script = engine.CompileAsync<FetchContext, decimal>("""
    var user = await Users.GetAsync(UserId);

    decimal sum = 0;
    foreach (var id in OrderIds)
        sum += (await Orders.GetAsync(id)).Total;

    return sum * user.DiscountRate;
    """);

decimal value = await script.RunAsync(context, cancellationToken);
```

### Raw delegates

```csharp
var f = engine.CompileDelegate<Func<int, int, int>>("a * b + 1", "a", "b");
int result = f(3, 4);

using var g = engine.CompileAsyncDelegate<Func<HttpClient, string, Task<int>>>(
    "(await http.GetStringAsync(url)).Length", "http", "url");
int length = await g.Value(client, "https://example.com");
```

`CompileAsyncDelegate` returns a disposable wrapper because the generated assembly is owned per
script; `CompileDelegate` returns the delegate directly because a `DynamicMethod` needs no
explicit release.

### Diagnostics

Binding continues after an error, so one compile reports every problem.

```csharp
var result = engine.TryCompile<PricingContext, decimal>(source);
if (!result.Success)
{
    foreach (var d in result.Diagnostics)
        Console.WriteLine($"{d.Severity} {d.Id.Code()} ({d.Line},{d.Column}): {d.Message}");
}
```

```
Error VS2003 (3,17): 方法 'IOrderService.GetAsync' 没有匹配 (string) 的重载；候选: GetAsync(int), ...
Error VS2005 (7,9):  无法将 string 转换为 decimal。
Error VS2002 (9,22): Order 不包含名为 'Totl' 的成员。是否想用 'Total'?
Error VS3004 (14,13): 'await' 不能出现在 catch 或 finally 块中。
```

Codes are grouped: `1xxx` lexical/syntactic, `2xxx` binding and types, `3xxx` async and control
flow, `9xxx` constructs that are not implemented yet.

## Execution limits

Generated IL cannot be interrupted from outside, so limits are cooperative: the compiler injects
a checkpoint at every loop back-edge, and routes every `await` through `Task.WaitAsync` with the
script's token.

```csharp
var options = ScriptOptions.Default.WithLimits(new ScriptLimits
{
    MaxSteps = 5_000_000,
    Timeout  = TimeSpan.FromMilliseconds(200),
});
```

The `WaitAsync` part is a correctness requirement, not an optimization: a script suspended on an
`await` never reaches a loop back-edge, so without it a hung remote call would make the timeout
meaningless.

`ScriptLimits.MaxStackDepth` is reserved and currently not enforced — scripts cannot declare
functions or lambdas, so no script-level recursion is reachable.

## The language

Supported: literals of every C# numeric form, `var` and explicit declarations, assignment and
compound assignment, `++`/`--`, full arithmetic/relational/logical/bitwise operators with C#
numeric promotion and nullable lifting, `?.` `??` `??=` `is` `as` `typeof`, casts, member and
static access, indexers, method calls with overload resolution (`params`, optional and named
arguments), operator overloading and user-defined conversions, object creation, `if`/`while`/
`do`/`for`/`foreach`/`break`/`continue`/`return`, `try`/`catch`/`finally`/`throw`, and `await`.

Not implemented yet, each with its own diagnostic rather than a confusing type error:

- lambdas and closures (`VS9001`)
- generic method type inference (`VS9002`)
- extension methods (`VS9003`), which together with the previous item is why LINQ does not work

Pattern matching (`x is Type y`, `switch` expressions) is not parsed at all and reports an
ordinary syntax error rather than a dedicated code.

Deliberately impossible: `await` inside `catch` or `finally`. The runtime does not protect
suspension points inside a handler — it crashes the process rather than throwing — so the binder
rejects it unconditionally with `VS3004`. `await` inside a `try` *block* is fine.

## Type-system fidelity

The engine has no value model of its own; `System.Type` is the type system. Numeric promotion
follows ECMA-334 §12.4.7, conversions follow §10, and overload resolution implements a
documented subset of §12.6.4 (no lambda return-type inference in betterness, no generic
inference, no `ref`/`out`).

The evidence for that is differential testing: `DifferentialTests` evaluates the same expression
with the real C# compiler and with the engine over a corpus of edge values — `int.MinValue`,
`uint.MaxValue`, `NaN`, infinities, every nullable combination, narrowing casts, shift-count
masking — and asserts the results are identical. Hand-written expectations cannot be trusted
here; this is what actually pins the semantics.

## Measured

Windows 11 x64, .NET 11.0.100-preview.7, BenchmarkDotNet short job.

Execution — both sides are JIT-compiled IL, so parity is the expected result:

| | hand-written C# | script | allocated |
|---|---:|---:|---:|
| decimal formula | 20.2 ns | 21.4 ns | 0 B |
| boolean rule | 5.3 ns | 8.7 ns | 0 B |
| 1000-iteration loop | 3117 ns | 3097 ns | 0 B |
| 1000-iteration loop, limits on | — | 3163 ns | 0 B |

Checkpoints cost about 2% on a tight loop. Compilation, and the cache:

| | |
|---|---:|
| sync, small | 8.9 µs |
| sync, medium (5 statements) | 36.0 µs |
| async, small | 378 µs |
| async, loop with `await` | 440 µs |
| cache hit | 54.5 ns |

Asynchronous execution (all awaited tasks already completed, so this is the await machinery
rather than scheduling; the C# baseline is compiled with `runtime-async=on` for a like-for-like
comparison):

| | hand-written C# | script |
|---|---:|---:|
| single await | 15.2 ns | 47.6 ns |
| 10 awaits in a loop | 20.2 ns | 45.4 ns |
| 10 awaits, limits on | — | 241 ns |

The gap in the last row is the per-invocation `CancellationTokenSource` and timer that enforce
`Timeout`, not the generated code. Leaving `Timeout` null and passing your own token keeps
cancellation working without it.

## Layout

```
src/V.Script/
  Syntax/       Lexer, Parser, syntax tree            — source to AST
  Binding/      Binder, Conversions, NumericPromotion,
                OverloadResolution, TypeResolver      — AST to BoundTree; all semantics live here
  Emit/         IlEmitter, ScriptCarrier              — BoundTree to IL; no type analysis
  Runtime/      ScriptEngine, Script, ScriptState     — public API and per-invocation state
  Diagnostics/  Diagnostic, DiagnosticBag, ErrorCode
tests/V.Script.Tests/       292 tests, including the differential suite
bench/V.Script.Benchmarks/  execution, compilation and async benchmarks
```

The load-bearing invariant: **the binder makes everything explicit, the emitter only picks
opcodes**. Conversions become `BoundConversion` nodes, nullable lifting is expanded, overloads
resolve to a concrete `MethodInfo`, `params` becomes an array node, `foreach` and compound
assignment are lowered into existing nodes. A `if (type == typeof(...))` inside `IlEmitter` is a
design leak.

## Building

```bash
dotnet build V.Script.slnx
dotnet test tests/V.Script.Tests
dotnet run --project bench/V.Script.Benchmarks -c Release -- --filter "*"
```

Benchmarks run in-process: BenchmarkDotNet 0.15.x does not yet recognise the `net11.0` moniker,
so its default out-of-process toolchain cannot launch the runner. Revisit once it ships .NET 11
support.
