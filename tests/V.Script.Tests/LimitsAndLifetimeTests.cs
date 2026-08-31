using System.Runtime.CompilerServices;
using V.Script.Diagnostics;

namespace V.Script.Tests;

public sealed class LifetimeTests
{
    private static ScriptOptions Options { get; } = ScriptOptions.Default
        .AddReferencesFrom(typeof(Order))
        .AddImports("V.Script.Tests");

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
    /// Each asynchronous script owns a collectible assembly. Retiring one must make its
    /// generated type unreachable, otherwise a service that hot-reloads rules grows without
    /// bound. Reachability is asserted directly rather than by watching process memory, which
    /// is far too noisy a signal to rely on.
    /// </summary>
    [Fact]
    public void Retiring_an_async_script_makes_its_generated_type_collectable()
    {
        var (weakType, weakAssembly) = CompileAndRetire();

        for (var attempt = 0; attempt < 10 && (weakType.IsAlive || weakAssembly.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakType.IsAlive, "生成的类型仍然可达，程序集没有被卸载。");
        Assert.False(weakAssembly.IsAlive, "生成的程序集仍然可达。");
    }

    /// <summary>
    /// Kept in its own non-inlined frame so the strong references genuinely go out of scope
    /// before the caller collects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Type, WeakReference Assembly) CompileAndRetire()
    {
        using var engine = new ScriptEngine(Options);

        var script = engine.CompileAsync<AsyncGlobals, int>("Seed + 1");
        var generated = script.Delegate.Method.DeclaringType!;

        var weakType = new WeakReference(generated);
        var weakAssembly = new WeakReference(generated.Assembly);

        script.Dispose();
        return (weakType, weakAssembly);
    }

    /// <summary>A script that is still in use must of course stay loaded.</summary>
    [Fact]
    public void A_live_async_script_is_not_collected()
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.CompileAsync<AsyncGlobals, int>("Seed + 2");

        var weakType = new WeakReference(script.Delegate.Method.DeclaringType!);

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        Assert.True(weakType.IsAlive);
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
    public void Inference_with_nothing_to_infer_from_is_named_as_such()
    {
        // Array.Empty<T>() has no argument that could fix T.
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
