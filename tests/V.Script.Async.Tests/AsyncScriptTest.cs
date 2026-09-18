using V.Script.Diagnostics;
using V.Script.Tests;

namespace V.Script.Async.Tests;

/// <summary>
/// Shared helpers for the tests that need the async extension.
/// </summary>
/// <remarks>
/// The options here call <c>WithAsync()</c>, which is what lets a <em>synchronous</em> script
/// contain an <c>async</c> lambda. <c>CompileAsync</c> itself does not need it — the extension
/// supplies its own support — but running both through the same options keeps the two cases from
/// drifting apart.
/// </remarks>
public abstract class AsyncScriptTest
{
    protected static ScriptOptions Options { get; } = ScriptOptions.Default
        .AddReferencesFrom(typeof(Order), typeof(AsyncScriptTest))
        .AddImports("V.Script.Tests")
        .WithAsync();

    protected static TResult Run<TGlobals, TResult>(string source, TGlobals globals)
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.Compile<TGlobals, TResult>(source);
        return script.Run(globals);
    }

    protected static TResult Eval<TResult>(string source) =>
        Run<EmptyGlobals, TResult>(source, new EmptyGlobals());

    protected static async Task<TResult> RunAsync<TGlobals, TResult>(string source, TGlobals globals)
    {
        using var engine = new ScriptEngine(Options);
        using var script = engine.CompileAsync<TGlobals, TResult>(source);
        return await script.RunAsync(globals);
    }

    /// <summary>Compiles and returns the diagnostics, expecting failure.</summary>
    protected static IReadOnlyList<Diagnostic> Errors<TGlobals, TResult>(string source, bool async = false)
    {
        using var engine = new ScriptEngine(Options);

        var diagnostics = async
            ? engine.TryCompileAsync<TGlobals, TResult>(source).Diagnostics
            : engine.TryCompile<TGlobals, TResult>(source).Diagnostics;

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.NotEmpty(errors);
        return diagnostics;
    }

    protected static void AssertError<TGlobals, TResult>(string source, ErrorCode expected, bool async = false)
    {
        var diagnostics = Errors<TGlobals, TResult>(source, async);
        Assert.True(
            diagnostics.Any(d => d.Id == expected),
            $"未产生 {expected.Code()}；实际诊断：{string.Join(" | ", diagnostics)}");
    }

    protected static void AssertErrorIn(string source, ErrorCode expected) =>
        AssertError<EmptyGlobals, object>(source, expected);
}
