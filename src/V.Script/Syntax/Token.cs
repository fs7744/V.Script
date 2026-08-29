using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// A lexical token. <see cref="Value"/> carries the decoded literal for literal tokens
/// (already boxed into its final CLR type) and is null otherwise.
/// </summary>
public readonly record struct Token(
    SyntaxKind Kind,
    string Text,
    SourcePosition Position,
    object? Value = null)
{
    public override string ToString() => $"{Kind} '{Text}' {Position}";
}
