using V.Script.Diagnostics;

namespace V.Script;

/// <summary>Base type for every failure raised by the scripting engine.</summary>
public abstract class ScriptException(string message) : Exception(message);

/// <summary>Thrown by the <c>Compile*</c> methods when binding produced errors.</summary>
public sealed class ScriptCompilationException : ScriptException
{
    public ScriptCompilationException(IReadOnlyList<Diagnostic> diagnostics)
        : base(Format(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private static string Format(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        var header = $"脚本编译失败，{errors.Length} 个错误：";
        return header + Environment.NewLine +
               string.Join(Environment.NewLine, errors.Select(d => "  " + d));
    }
}
