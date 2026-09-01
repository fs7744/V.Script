using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// One piece of an interpolated string as the lexer found it: either literal text, or the raw
/// source of an interpolation hole together with its optional alignment and format specifier.
/// The hole's source is parsed later, seeded with <see cref="Position"/> so that diagnostics
/// inside it point at the right place.
/// </summary>
public readonly record struct RawInterpolationPart(
    bool IsHole,
    string Text,
    SourcePosition Position,
    string? Alignment = null,
    string? Format = null);

/// <summary>
/// A lexical token. <see cref="Value"/> carries the decoded literal for literal tokens
/// (already boxed into its final CLR type) and is null otherwise. For an interpolated string it
/// carries the <see cref="RawInterpolationPart"/> list.
/// </summary>
public readonly record struct Token(
    SyntaxKind Kind,
    string Text,
    SourcePosition Position,
    object? Value = null)
{
    public override string ToString() => $"{Kind} '{Text}' {Position}";
}
