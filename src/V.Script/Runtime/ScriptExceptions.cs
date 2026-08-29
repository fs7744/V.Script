using V.Script.Diagnostics;

namespace V.Script;

/// <summary>Base type for every failure raised by the scripting engine.</summary>
public abstract class ScriptException : Exception
{
    protected ScriptException(string message) : base(message) { }

    protected ScriptException(string message, Exception inner) : base(message, inner) { }
}

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

/// <summary>The script exceeded its iteration budget.</summary>
public sealed class ScriptBudgetExceededException(long budget)
    : ScriptException($"脚本超出执行预算（{budget} 步）。可能存在死循环。")
{
    public long Budget { get; } = budget;
}

/// <summary>The script exceeded its wall-clock limit.</summary>
public sealed class ScriptTimeoutException(TimeSpan timeout)
    : ScriptException($"脚本执行超时（{timeout.TotalMilliseconds:F0} ms）。")
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>An exception escaped the script body.</summary>
public sealed class ScriptRuntimeException(string message, Exception inner)
    : ScriptException(message, inner);
