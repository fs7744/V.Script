using V.Script.Diagnostics;

namespace V.Script.Syntax;

public abstract record PatternSyntax(SourcePosition Position) : SyntaxNode(Position);

/// <summary>
/// A bare name in pattern position: <c>x is Order o</c> (a type) or <c>x is Status.Active</c>
/// (a constant). The parser cannot tell them apart, so it records the name and lets the binder
/// try type resolution first. A designation or a property list settles it as a type.
/// </summary>
public sealed record TypePatternSyntax(
    SourcePosition Position,
    TypeSyntax Type,
    string? Designation,
    bool MayBeConstant) : PatternSyntax(Position);

public sealed record ConstantPatternSyntax(SourcePosition Position, ExpressionSyntax Value)
    : PatternSyntax(Position);

/// <summary>A bare relational test such as <c>is &gt; 10</c>.</summary>
public sealed record RelationalPatternSyntax(
    SourcePosition Position,
    SyntaxKind Operator,
    ExpressionSyntax Value) : PatternSyntax(Position);

public sealed record NotPatternSyntax(SourcePosition Position, PatternSyntax Pattern)
    : PatternSyntax(Position);

public sealed record BinaryPatternSyntax(
    SourcePosition Position,
    PatternSyntax Left,
    bool IsAnd,
    PatternSyntax Right) : PatternSyntax(Position);

public sealed record ParenthesizedPatternSyntax(SourcePosition Position, PatternSyntax Pattern)
    : PatternSyntax(Position);

/// <summary><c>var x</c> — always matches and names the value.</summary>
public sealed record VarPatternSyntax(SourcePosition Position, string Designation)
    : PatternSyntax(Position);

/// <summary><c>_</c> — always matches and names nothing.</summary>
public sealed record DiscardPatternSyntax(SourcePosition Position) : PatternSyntax(Position);

public sealed record PropertySubpatternSyntax(
    SourcePosition Position,
    string Name,
    PatternSyntax Pattern) : SyntaxNode(Position);

/// <summary><c>{ Quantity: &gt; 2, Sku: "abc" }</c>, optionally typed and named.</summary>
public sealed record PropertyPatternSyntax(
    SourcePosition Position,
    TypeSyntax? Type,
    IReadOnlyList<PropertySubpatternSyntax> Subpatterns,
    string? Designation) : PatternSyntax(Position);

/// <summary>Placeholder produced after a parse error so parsing can continue.</summary>
public sealed record ErrorPatternSyntax(SourcePosition Position) : PatternSyntax(Position);

// ---------------------------------------------------------------- switch expression

public sealed record SwitchArmSyntax(
    SourcePosition Position,
    PatternSyntax Pattern,
    ExpressionSyntax? Guard,
    ExpressionSyntax Result) : SyntaxNode(Position);

public sealed record SwitchExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Governing,
    IReadOnlyList<SwitchArmSyntax> Arms) : ExpressionSyntax(Position);
