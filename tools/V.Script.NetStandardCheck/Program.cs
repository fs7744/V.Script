// Verifies the netstandard2.0 asset: that the core language runs on it, that the two constructs
// it cannot support are reported as diagnostics rather than crashing, and that the one documented
// behavioural difference holds. Run it after touching anything under #if NETSTANDARD2_0.
//
//   dotnet run --project tools/V.Script.NetStandardCheck -c Release

using V.Script;

var ok = 0;
var bad = 0;

void Check(string label, Func<string> run, string expected)
{
    string actual;
    try { actual = run(); }
    catch (Exception e) { actual = e.GetType().Name + ": " + e.Message.Split('\n')[0]; }

    var pass = actual.Contains(expected, StringComparison.Ordinal);
    Console.WriteLine($"{(pass ? "OK  " : "FAIL")}  {label,-46} {actual}");
    if (pass) ok++; else bad++;
}

using var engine = new ScriptEngine(ScriptOptions.Default);

Check("基本算术", () => engine.Compile<Ctx, int>("A * 2 + B").Run(new Ctx { A = 20, B = 2 }).ToString(), "42");
Check("lambda 与闭包", () => engine.Compile<Ctx, int>("var k = A; var f = (int x) => x + k; return f(B);")
    .Run(new Ctx { A = 40, B = 2 }).ToString(), "42");
Check("LINQ", () => engine.Compile<Ctx, int>("Numbers.Where(n => n > 1).Sum()")
    .Run(new Ctx { Numbers = [1, 2, 3] }).ToString(), "5");
Check("模式匹配", () => engine.Compile<Ctx, string>("A switch { < 0 => \"neg\", 0 => \"zero\", _ => \"pos\" }")
    .Run(new Ctx { A = 7 }), "pos");
Check("插值（带格式，走 string.Format）",
    () => engine.Compile<Ctx, string>("$\"[{A,4}]\"").Run(new Ctx { A = 7 }), "[   7]");
Check("元组与解构", () => engine.Compile<Ctx, int>("var (a, b) = (A, B); return a + b;")
    .Run(new Ctx { A = 40, B = 2 }).ToString(), "42");
Check("原始字符串（走改写后的数组切片）",
    () => engine.Compile<Ctx, string>("\"\"\"\n    hi\n    \"\"\""). Run(new Ctx()), "hi");

// 明确不支持的两项：必须是编译诊断，不是崩溃
Check("^1 被拒绝（编译期）", () =>
{
    var r = engine.TryCompile<Ctx, int>("Numbers[^1]");
    return r.Success ? "意外成功" : string.Join(" | ", r.Errors.Select(d => d.Id.ToString()));
}, "ConstructNotSupported");

Check("a..b 被拒绝（编译期）", () =>
{
    var r = engine.TryCompile<Ctx, int>("Numbers[0..2].Length");
    return r.Success ? "意外成功" : string.Join(" | ", r.Errors.Select(d => d.Id.ToString()));
}, "ConstructNotSupported");

Check("switch 无匹配（netstandard2.0 抛 InvalidOperationException）",
    () => engine.Compile<Ctx, string>("A switch { 1 => \"one\" }").Run(new Ctx { A = 9 }),
    "InvalidOperationException");

Console.WriteLine();
Console.WriteLine($"通过 {ok}，失败 {bad}");
return bad == 0 ? 0 : 1;

public sealed class Ctx
{
    public int A { get; set; }
    public int B { get; set; }
    public int[] Numbers { get; set; } = [];
}
