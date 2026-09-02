using V.Script;
using V.Script.Diagnostics;

// Compiles a fixed list of C# constructs and reports which the engine accepts. This is how the
// language-coverage table in docs/design.md is produced; re-run it after any front-end change
// rather than editing that table by hand.

public sealed record Recd(int X);

public sealed class G
{
    public Recd Rec { get; init; } = new(1);

    public void Bump(ref int value) => value++;

    public int N { get; init; }
    public int[] Numbers { get; init; } = [];
    public List<int> List { get; init; } = [];
    public string Text { get; init; } = "";
    public object? Value { get; init; }
    public Item Item { get; init; } = new();
}

public sealed class Item
{
    public void Deconstruct(out int count, out string name) { count = Count; name = Name; }

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
            ("query, where select",       "return (from n in Numbers where n > 1 select n * 2).Count();"),
            ("query, orderby",            "return (from n in Numbers orderby n descending select n).First();"),
            ("query, let",                "return (from n in Numbers let d = n * 2 where d > 2 select d).Count();"),
            ("query, two froms",          "return (from a in Numbers from b in Numbers select a * b).Count();"),
            ("query, group by",           "return (from n in Numbers group n by n % 2).Count();"),
            ("query, into",               "return (from n in Numbers group n by n % 2 into g select g.Key).Count();"),
            ("query, join",               "return (from a in Numbers join b in Numbers on a equals b select a).Count();"),
            ("async lambda",              "Func<int, Task<int>> f = async x => await Task.FromResult(x); return f(1).Result;"),
            ("async local function",      "async Task<int> F(int x) => await Task.FromResult(x); return F(2).Result;"),
            ("local const",               "const int k = 2; return k * 3;"),
            ("static local function",     "static int F(int x) => x * 2; return F(3);"),
            ("index from end",            "return Numbers[^1];"),
            ("range slice",               "return Numbers[1..].Length;"),
            ("range on string",           "return Text[0..2].Length;"),
            ("with expression",           "var r = Rec with { X = 2 }; return r.X;"),
            ("nint",                      "nint x = 1; return (int)x;"),
            ("raw interpolation",         "var s = $$\"\"\"a{{N}}b\"\"\"; return s.Length;"),
            ("preprocessor",              "#if SCRIPT_DEBUG\nreturn 1;\n#else\nreturn 0;\n#endif"),
            ("object initializer",        "var i = new Item { Count = 1 }; return i.Count;"),
            ("collection initializer",    "var l = new List<int> { 1, 2 }; return l.Count;"),
            ("collection expression",     "int[] a = [1, 2, 3]; return a.Length;"),
            ("nested initializer",        "var i = new Item { Name = { } }; return 0;"),
            ("collection expr, List",     "List<int> l = [1, 2, 3]; return l.Count;"),
            ("collection expr, iface",    "IEnumerable<int> e = [1, 2]; return e.Count();"),
            ("index initializer",         "var d = new Dictionary<string, int> { [\"k\"] = 1 }; return d.Count;"),
            ("typed lambda parameter",    "var f = (int x) => x + 1; return f(1);"),
            ("typed lambda, two params",  "var f = (int a, int b) => a * b; return f(2, 3);"),
            ("local function, recursive", "int Fact(int n) { return n <= 1 ? 1 : n * Fact(n - 1); } return Fact(5);"),
            ("local function, expr body", "int Twice(int n) => n * 2; return Twice(4);"),
            ("local function, void",      "void Log(int n) { } Log(1); return 0;"),
            ("local function, forward",   "return Twice(4); int Twice(int n) => n * 2;"),
            ("array creation, sized",     "var a = new int[3]; return a.Length;"),
            ("array creation, inline",    "var a = new[] { 1, 2 }; return a.Length;"),
            ("explicit type arguments",   "return Numbers.Cast<int>().Count();"),
            ("interpolated string",       "var s = $\"n={N}\"; return s.Length;"),
            ("verbatim string",           "var s = @\"a\b\"; return s.Length;"),
            ("raw string literal",        "var s = \"\"\"abc\"\"\"; return s.Length;"),
            ("verbatim interpolated",     "var s = $@\"n={N}\"; return s.Length;"),
            ("raw string, multi-line",    "var s = \"\"\"\n  ab\n  \"\"\"; return s.Length;"),
            ("nameof",                    "return nameof(N).Length;"),
            ("default literal",           "int x = default; return x;"),
            ("default(T)",                "var x = default(int); return x;"),
            ("typeof",                    "return typeof(int).Name.Length;"),
            ("tuple literal",             "var t = (1, 2); return t.Item1;"),
            ("tuple, named elements",     "var t = (a: 1, b: \"x\"); return t.a + t.b.Length;"),
            ("tuple type in decl",        "(int a, string b) t = (1, \"x\"); return t.a;"),
            ("tuple, nine elements",      "var t = (1,2,3,4,5,6,7,8,9); return t.Item1 + t.Item8 + t.Item9;"),
            ("deconstruct nine",          "var (a,b,c,d,e,f,g,h,i) = (1,2,3,4,5,6,7,8,9); return a + i;"),
            ("mixed deconstruction",      "var y = 0; (var x, y) = (1, 2); return x + y;"),
            ("deconstruct assignment",    "var x = 1; var y = 2; (x, y) = (y, x); return x * 10 + y;"),
            ("deconstruct via method",    "var (k, v) = new KeyValuePair<string, int>(\"a\", 1); return v;"),
            ("deconstruction",            "var (a, b) = (1, 2); return a;"),
            ("switch statement",          "switch (N) { case 1: return 1; default: return 0; }"),
            ("goto",                      "goto done; done: return 1;"),
            ("using statement",           "using (var d = new System.IO.MemoryStream()) { return 1; }"),
            ("lock",                      "lock (Item) { return 1; }"),
            ("using declaration",         "using var d = new System.IO.MemoryStream(); return (int)d.Length;"),
            ("goto case",                 "switch (N) { case 1: goto case 2; case 2: return 2; default: return 0; }"),
            ("labelled loop exit",        "var i = 0; top: i++; if (i < 3) goto top; return i;"),
            ("checked",                   "return checked(N + 1);"),
            ("unchecked",                 "return unchecked(N + 1);"),
            ("checked block",             "checked { var x = N + 1; return x; }"),
            ("throw expression",          "var s = Text ?? throw new Exception(\"x\"); return s.Length;"),
            ("local function",            "int F(int x) { return x + 1; } return F(1);"),
            ("positional pattern",        "return Item is (1, \"a\") ? 1 : 0;"),
            ("list pattern",              "return Numbers is [1, 2] ? 1 : 0;"),
            ("positional, typed",         "return Item is Item (var c, var n) ? c : -1;"),
            ("list pattern with slice",   "return Numbers is [1, .., var last] ? last : -1;"),
            ("list pattern on List",      "return List is [_, _] ? 2 : -1;"),
            ("multi-dim array index",     "var a = new int[2, 2]; return a[0, 0];"),
            ("multi-dim array create",    "var a = new int[2, 3]; a[1, 2] = 7; return a[1, 2] + a.Length;"),
            ("collection expr spread",    "int[] a = [0, ..Numbers, 9]; return a.Length;"),
            ("jagged array index",        "int[][] a = null; return a[0][0];"),
            ("conditional member on call","return Item?.Name?.Length ?? 0;"),
            ("string interpolation ctrl", "return string.Format(\"{0}\", N).Length;"),
            ("increment on property",     "Item.Count++; return Item.Count;"),
            ("nested generic type",       "var d = new Dictionary<string, List<int>>(); return d.Count;"),
            ("ternary chain",             "return N > 0 ? 1 : N < 0 ? -1 : 0;"),
            ("bitwise on enum",           "return (int)(System.DayOfWeek.Monday | System.DayOfWeek.Tuesday);"),
            ("out parameter call",        "int x = 0; return int.TryParse(\"1\", out x) ? x : 0;"),
            ("method group inference",    "return Numbers.Select(Twice).Count(); int Twice(int n) => n * 2;"),
            ("method group, static",      "return Numbers.Select(int.Abs).Sum();"),
            ("ref parameter call",        "var n = 1; Bump(ref n); return n;"),
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
