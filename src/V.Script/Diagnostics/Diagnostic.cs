namespace V.Script.Diagnostics;

/// <summary>Severity of a <see cref="Diagnostic"/>.</summary>
public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>A position in script source. Both values are 1-based.</summary>
public readonly record struct SourcePosition(int Line, int Column)
{
    public static SourcePosition None => new(0, 0);

    public override string ToString() => Line == 0 ? "(?,?)" : $"({Line},{Column})";
}

/// <summary>A single compile-time message produced by the lexer, parser or binder.</summary>
public sealed record Diagnostic(
    ErrorCode Id,
    DiagnosticSeverity Severity,
    SourcePosition Position,
    string Message)
{
    public int Line => Position.Line;

    public int Column => Position.Column;

    public override string ToString() =>
        $"{Severity} {Id.Code()} {Position}: {Message}";
}
