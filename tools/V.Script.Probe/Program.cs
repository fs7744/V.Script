using V.Script;
using V.Script.Diagnostics;

// Compiles a fixed list of C# constructs and reports which the engine accepts. This is how the
// language-coverage table in docs/design.md is produced; re-run it after any front-end change
// rather than editing that table by hand.

public sealed class G
{
    public int N { get; init; }
    public int[] Numbers { get; init; } = [];
    public List<int> List { get; init; } = [];
    public string Text { get; init; } = "";
    public object? Value { get; init; }
    public Item Item { get; init; } = new();
}

public sealed class Item
{
    public int Count { get; set; }
    public string Name { get; set; } = "";
}

public static class Program
{
    public static int Main()
    {
        var options = ScriptOptions.Default.AddReferencesFrom(typeof(G)).AddImports("");
        using var engine = new ScriptEngine(options);

        (string Label, string Source)[] cases =
        [
            ("object initializer",        "var i = new Item { Count = 1 }; return i.Count;"),
            ("collection initializer",    "var l = new List<int> { 1, 2 }; return l.Count;"),
            ("collection expression",     "int[] a = [1, 2, 3]; return a.Length;"),
            ("array creation, sized",     "var a = new int[3]; return a.Length;"),
            ("array creation, inline",    "var a = new[] { 1, 2 }; return a.Length;"),
            ("explicit type arguments",   "return Numbers.Cast<int>().Count();"),
            ("interpolated string",       "var s = $\"n={N}\"; return s.Length;"),
            ("verbatim string",           "var s = @\"a\b\"; return s.Length;"),
            ("raw string literal",        "var s = \"\"\"abc\"\"\"; return s.Length;"),
            ("nameof",                    "return nameof(N).Length;"),
            ("default literal",           "int x = default; return x;"),
            ("default(T)",                "var x = default(int); return x;"),
            ("typeof",                    "return typeof(int).Name.Length;"),
            ("tuple literal",             "var t = (1, 2); return t.Item1;"),
            ("deconstruction",            "var (a, b) = (1, 2); return a;"),
            ("switch statement",          "switch (N) { case 1: return 1; default: return 0; }"),
            ("goto",                      "goto done; done: return 1;"),
            ("using statement",           "using (var d = new System.IO.MemoryStream()) { return 1; }"),
            ("lock",                      "lock (Item) { return 1; }"),
            ("checked",                   "return checked(N + 1);"),
            ("throw expression",          "var s = Text ?? throw new Exception(\"x\"); return s.Length;"),
            ("local function",            "int F(int x) { return x + 1; } return F(1);"),
            ("positional pattern",        "return Item is (1, \"a\") ? 1 : 0;"),
            ("list pattern",              "return Numbers is [1, 2] ? 1 : 0;"),
            ("multi-dim array index",     "var a = new int[2, 2]; return a[0, 0];"),
            ("jagged array index",        "int[][] a = null; return a[0][0];"),
            ("conditional member on call","return Item?.Name?.Length ?? 0;"),
            ("string interpolation ctrl", "return string.Format(\"{0}\", N).Length;"),
            ("increment on property",     "Item.Count++; return Item.Count;"),
            ("nested generic type",       "var d = new Dictionary<string, List<int>>(); return d.Count;"),
            ("ternary chain",             "return N > 0 ? 1 : N < 0 ? -1 : 0;"),
            ("bitwise on enum",           "return (int)(System.DayOfWeek.Monday | System.DayOfWeek.Tuesday);"),
            ("out parameter call",        "int x = 0; return int.TryParse(\"1\", out x) ? x : 0;"),
            ("chained null-conditional",  "return Value?.ToString()?.Length ?? 0;"),
            ("qualified static call",     "return System.Math.Max(1, 2);"),
            ("qualified enum member",     "return (int)System.DayOfWeek.Monday;"),
            ("qualified type in decl",    "System.Text.StringBuilder b = new System.Text.StringBuilder(); return b.Length;"),
            ("imported static call",      "return Math.Max(1, 2);"),
            ("nested type access",        "return System.Int32.MaxValue > 0 ? 1 : 0;"),
        ];

        var unsupported = 0;

        foreach (var (label, source) in cases)
        {
            var result = engine.TryCompile<G, int>(source);
            var mark = result.Success ? "OK  " : "FAIL";
            var detail = result.Success
                ? ""
                : "  " + string.Join(" | ", result.Errors.Select(e => $"{e.Id.Code()} {e.Message}"));

            if (detail.Length > 150) detail = detail[..150] + "...";
            Console.WriteLine($"{mark}  {label,-28}{detail}");

            if (!result.Success) unsupported++;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{cases.Length} 项中 {unsupported} 项未实现。注意这份清单是为找缺口而挑选的，" +
            "刻意偏重尚未支持的构造，不能当作 C# 覆盖率来读。");

        return 0;
    }
}
