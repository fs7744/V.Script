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

/// <summary>
/// <c>(a, b)</c> or <c>Point (var x, var y)</c>: the value is deconstructed and each part is
/// matched in turn. An element may be named, as in <c>(x: 1, y: 2)</c>.
/// </summary>
public sealed record PositionalPatternSyntax(
    SourcePosition Position,
    TypeSyntax? Type,
    IReadOnlyList<PositionalSubpatternSyntax> Subpatterns,
    IReadOnlyList<PropertySubpatternSyntax> Properties,
    string? Designation) : PatternSyntax(Position);

public sealed record PositionalSubpatternSyntax(
    SourcePosition Position,
    string? Name,
    PatternSyntax Pattern) : SyntaxNode(Position);

/// <summary>
/// <c>[1, 2]</c>, <c>[1, .., 3]</c>, <c>[first, ..var rest]</c>. At most one slice is allowed,
/// and it is what makes the pattern match a range of lengths rather than exactly one.
/// </summary>
public sealed record ListPatternSyntax(
    SourcePosition Position,
    IReadOnlyList<PatternSyntax> Before,
    bool HasSlice,
    string? SliceDesignation,
    IReadOnlyList<PatternSyntax> After,
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
