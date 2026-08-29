using System.Diagnostics;
using V.Script.Diagnostics;

namespace V.Script.Tests;

public sealed class ExecutionLimitTests
{
    private static ScriptOptions With(ScriptLimits limits) => ScriptOptions.Default
        .AddReferencesFrom(typeof(Order))
        .AddImports("V.Script.Tests")
        .WithLimits(limits);

    [Fact]
    public void Step_budget_stops_an_infinite_loop()
    {
        using var engine = new ScriptEngine(With(new ScriptLimits { MaxSteps = 100_000 }));
        using var script = engine.Compile<EmptyGlobals, int>("var i = 0; while (true) { i++; } return i;");

        Assert.Throws<ScriptBudgetExceededException>(() => script.Run(new EmptyGlobals()));
    }

    [Fact]
    public void Step_budget_allows_a_loop_that_fits()
    {
        using var engine = new ScriptEngine(With(new ScriptLimits { MaxSteps = 100_000 }));
        using var script = engine.Compile<EmptyGlobals, int>(
            "var sum = 0; for (var i = 0; i < 1000; i++) sum += i; return sum;");

        Assert.Equal(499_500, script.Run(new EmptyGlobals()));
    }

    [Fact]
    public void Budget_is_per_invocation_not_cumulative()
    {
        using var engine = new ScriptEngine(With(new ScriptLimits { MaxSteps = 10_000 }));
        using var script = engine.Compile<EmptyGlobals, int>(
            "var sum = 0; for (var i = 0; i < 1000; i++) sum += i; return sum;");

        for (var run = 0; run < 5; run++)
            Assert.Equal(499_500, script.Run(new EmptyGlobals()));
    }

    [Fact]
    public void Timeout_stops_a_long_running_synchronous_loop()
    {
        using var engine = new ScriptEngine(With(new ScriptLimits { Timeout = TimeSpan.FromMilliseconds(200) }));
        using var script = engine.Compile<EmptyGlobals, long>("var i = 0L; while (true) { i++; } return i;");

        var stopwatch = Stopwatch.StartNew();
        Assert.Throws<ScriptTimeoutException>(() => script.Run(new EmptyGlobals()));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 10_000,
            $"超时未生效，耗时 {stopwatch.ElapsedMilliseconds} ms。");
    }

    [Fact]
    public void Cancellation_token_stops_a_synchronous_loop()
    {
        using var engine = new ScriptEngine(With(new ScriptLimits { MaxSteps = long.MaxValue }));
        using var script = engine.Compile<EmptyGlobals, long>("var i = 0L; while (true) { i++; } return i;");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        Assert.ThrowsAny<OperationCanceledException>(() => script.Run(new EmptyGlobals(), cts.Token));
    }

    [Fact]
    public void Unlimited_skips_checkpoint_emission_entirely()
    {
        using var engine = new ScriptEngine(With(ScriptLimits.Unlimited));
        using var script = engine.Compile<EmptyGlobals, int>(
            "var sum = 0; for (var i = 0; i < 100000; i++) sum += 1; return sum;");

        Assert.Equal(100_000, script.Run(new EmptyGlobals()));
    }
}

public sealed class LifetimeTests
{
    private static ScriptOptions Options { get; } = ScriptOptions.Default
        .AddReferencesFrom(typeof(Order))
        .AddImports("V.Script.Tests")
        .WithLimits(ScriptLimits.Unlimited);

    [Fact]
    public void Identical_sources_share_one_compilation()
    {
        using var engine = new ScriptEngine(Options);

        var first = engine.Compile<NumberGlobals, int>("A + B");
        var second = engine.Compile<NumberGlobals, int>("A + B");

        Assert.Same(first, second);
    }

    [Fact]
    public void Different_result_types_are_cached_separately()
    {
        using var engine = new ScriptEngine(Options);

        var asInt = engine.Compile<NumberGlobals, int>("A + B");
        var asLong = engine.Compile<NumberGlobals, long>("A + B");

        Assert.NotSame(asInt, (object)asLong);
    }

    [Fact]
    public void Disposing_a_script_removes_it_from_the_cache()
    {
        using var engine = new ScriptEngine(Options);

        var first = engine.Compile<NumberGlobals, int>("A + B");
        first.Dispose();

        var second = engine.Compile<NumberGlobals, int>("A + B");
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Running_a_disposed_script_throws()
    {
        using var engine = new ScriptEngine(Options);
        var script = engine.Compile<NumberGlobals, int>("A + B");
        script.Dispose();

        Assert.Throws<ObjectDisposedException>(() => script.Run(new NumberGlobals()));
    }

    [Fact]
    public void Disposing_the_engine_disposes_its_scripts()
    {
        var engine = new ScriptEngine(Options);
        var script = engine.Compile<NumberGlobals, int>("A + B");
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => script.Run(new NumberGlobals()));
    }

    [Fact]
    public void Compiling_after_engine_disposal_throws()
    {
        var engine = new ScriptEngine(Options);
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.Compile<NumberGlobals, int>("A"));
    }

    /// <summary>
    /// Each asynchronous script owns a collectible assembly. Retiring a generation must return
    /// its memory, otherwise a service that hot-reloads rules grows without bound.
    /// </summary>
    [Fact]
    public void Retiring_async_scripts_returns_their_memory()
    {
        const int Count = 300;

        static long Snapshot()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            Process.GetCurrentProcess().Refresh();
            return Process.GetCurrentProcess().PrivateMemorySize64;
        }

        using var engine = new ScriptEngine(Options);

        var baseline = Snapshot();

        var generation = new List<AsyncScript<AsyncGlobals, int>>(Count);
        for (var i = 0; i < Count; i++)
            generation.Add(engine.CompileAsync<AsyncGlobals, int>($"Seed + {i}"));

        var loaded = Snapshot();
        var grew = loaded - baseline;
        Assert.True(grew > 0, "生成程序集后内存没有增长，测试本身失效。");

        foreach (var script in generation) script.Dispose();
        generation.Clear();

        var released = Snapshot();

        // Collectible assemblies unload asynchronously and loader heaps are page-granular, so
        // this asserts that the bulk comes back rather than every last byte.
        Assert.True(released - baseline < grew * 0.75,
            $"卸载后仅返还 {(grew - (released - baseline)) / 1024.0 / 1024:F1} MB，" +
            $"占用 {grew / 1024.0 / 1024:F1} MB。");
    }

    [Fact]
    public void Synchronous_scripts_are_reusable_across_threads()
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.Compile<NumberGlobals, int>("A * 2 + B");

        var results = Enumerable.Range(0, 200)
            .AsParallel()
            .Select(i => script.Run(new NumberGlobals { A = i, B = 1 }))
            .ToArray();

        Assert.All(results, r => Assert.True(r % 2 == 1));
    }

    [Fact]
    public void Concurrent_compilation_of_distinct_sources_succeeds()
    {
        using var engine = new ScriptEngine(Options);

        var scripts = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(i => engine.Compile<NumberGlobals, int>($"A + {i}"))
            .ToArray();

        Assert.Equal(64, scripts.Length);
        Assert.All(scripts, s => Assert.Equal(1, s.Run(new NumberGlobals { A = 1 }) - int.Parse(s.Source.Split("+ ")[1])));
    }
}

public sealed class DiagnosticsTests : ScriptTest
{
    [Fact]
    public void All_errors_are_reported_from_one_compilation()
    {
        const string source = """
            var a = Missing1;
            var b = Missing2;
            var c = Missing3;
            return 0;
            """;

        var diagnostics = Errors<EmptyGlobals, int>(source);
        Assert.Equal(3, diagnostics.Count(d => d.Id == ErrorCode.UndefinedName));
    }

    [Fact]
    public void Diagnostics_carry_line_and_column()
    {
        const string source = """
            var a = 1;
            var b = Missing;
            return 0;
            """;

        var diagnostics = Errors<EmptyGlobals, int>(source);
        var undefined = diagnostics.Single(d => d.Id == ErrorCode.UndefinedName);

        Assert.Equal(2, undefined.Line);
        Assert.Equal(9, undefined.Column);
    }

    [Fact]
    public void Diagnostics_are_ordered_by_position()
    {
        const string source = """
            var a = MissingB;
            var b = MissingA;
            return 0;
            """;

        var diagnostics = Errors<EmptyGlobals, int>(source);
        Assert.True(diagnostics[0].Line <= diagnostics[^1].Line);
    }

    [Fact]
    public void Error_codes_render_in_the_documented_form()
    {
        Assert.Equal("VS2001", ErrorCode.UndefinedName.Code());
        Assert.Equal("VS3004", ErrorCode.AwaitInExceptionHandler.Code());
    }

    [Fact]
    public void Compilation_exception_lists_every_error()
    {
        using var engine = new ScriptEngine(Options);
        var exception = Assert.Throws<ScriptCompilationException>(
            () => engine.Compile<EmptyGlobals, int>("var a = X; var b = Y; return 0;"));

        Assert.Equal(2, exception.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("2 个错误", exception.Message);
    }

    [Fact]
    public void Try_compile_reports_without_throwing()
    {
        using var engine = new ScriptEngine(Options);
        var result = engine.TryCompile<EmptyGlobals, int>("var a = Missing; return 0;");

        Assert.False(result.Success);
        Assert.Null(result.Script);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Unsupported_lambda_gets_a_dedicated_code()
    {
        AssertError<OrderGlobals, int>("Calc.Sum(x => x)", ErrorCode.LambdaNotSupported);
    }

    [Fact]
    public void Unknown_type_names_are_reported()
    {
        AssertErrorIn("NoSuchType x = null; return 0;", ErrorCode.UnknownType);
    }

    [Fact]
    public void Syntax_errors_stop_before_binding()
    {
        var diagnostics = Errors<EmptyGlobals, int>("var a = (1 + ;");
        Assert.Contains(diagnostics, d => d.Id is ErrorCode.ExpectedExpression or ErrorCode.ExpectedToken);
    }
}

/// <summary>
/// Constructs the engine does not implement yet must say so specifically. A vague
/// "no matching overload" for a LINQ call would send the reader hunting for a type error
/// that does not exist.
/// </summary>
public sealed class UnsupportedConstructTests : ScriptTest
{
    [Fact]
    public void Lambda_reports_its_own_code()
    {
        AssertError<OrderGlobals, int>("Calc.Sum(x => x)", ErrorCode.LambdaNotSupported);
    }

    [Fact]
    public void Linq_extension_method_is_named_as_such()
    {
        var diagnostics = Errors<OrderGlobals, int>("Numbers.Count()");
        var reported = diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error);

        Assert.Equal(ErrorCode.ExtensionMethodNotSupported, reported.Id);
        Assert.Contains("扩展方法", reported.Message);
    }

    [Fact]
    public void Generic_method_needing_inference_is_named_as_such()
    {
        // Array.Empty<T>() is generic and has no argument to infer from.
        AssertError<OrderGlobals, int>("Array.Empty().Length", ErrorCode.GenericMethodInferenceNotSupported);
    }

    [Fact]
    public void Non_generic_overload_failure_still_reports_the_candidates()
    {
        var diagnostics = Errors<OrderGlobals, int>("Calc.Add(\"a\", \"b\")");
        var reported = diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error);

        Assert.Equal(ErrorCode.NoMatchingOverload, reported.Id);
    }
}
