using V.Script.Diagnostics;

namespace V.Script.Syntax;

public abstract record SyntaxNode(SourcePosition Position);

// ---------------------------------------------------------------- types

/// <summary>
/// A type reference as written in source, e.g. <c>int</c>, <c>List&lt;string&gt;</c>,
/// <c>decimal?</c>, <c>int[]</c>. Resolution to a CLR <see cref="Type"/> happens in the binder.
/// </summary>
public sealed record TypeSyntax(
    SourcePosition Position,
    IReadOnlyList<string> NameParts,
    IReadOnlyList<TypeSyntax> TypeArguments,
    bool IsNullable,
    int ArrayRank) : SyntaxNode(Position)
{
    public string DisplayName
    {
        get
        {
            var name = string.Join('.', NameParts);
            if (TypeArguments.Count > 0)
                name += '<' + string.Join(", ", TypeArguments.Select(a => a.DisplayName)) + '>';
            if (IsNullable) name += '?';
            for (var i = 0; i < ArrayRank; i++) name += "[]";
            return name;
        }
    }
}

// ---------------------------------------------------------------- expressions

public abstract record ExpressionSyntax(SourcePosition Position) : SyntaxNode(Position);

public sealed record LiteralExpressionSyntax(SourcePosition Position, Token Token)
    : ExpressionSyntax(Position);

public sealed record NameExpressionSyntax(SourcePosition Position, string Name)
    : ExpressionSyntax(Position);

public sealed record ParenthesizedExpressionSyntax(SourcePosition Position, ExpressionSyntax Inner)
    : ExpressionSyntax(Position);

public sealed record UnaryExpressionSyntax(SourcePosition Position, SyntaxKind Operator, ExpressionSyntax Operand)
    : ExpressionSyntax(Position);

public sealed record PostfixExpressionSyntax(SourcePosition Position, ExpressionSyntax Operand, SyntaxKind Operator)
    : ExpressionSyntax(Position);

public sealed record BinaryExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Left,
    SyntaxKind Operator,
    ExpressionSyntax Right) : ExpressionSyntax(Position);

public sealed record AssignmentExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    SyntaxKind Operator,
    ExpressionSyntax Value) : ExpressionSyntax(Position);

public sealed record ConditionalExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Condition,
    ExpressionSyntax WhenTrue,
    ExpressionSyntax WhenFalse) : ExpressionSyntax(Position);

public sealed record MemberAccessExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    string MemberName,
    bool IsNullConditional,
    IReadOnlyList<TypeSyntax>? TypeArguments = null) : ExpressionSyntax(Position);

public sealed record InvocationExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    IReadOnlyList<ArgumentSyntax> Arguments,
    IReadOnlyList<TypeSyntax>? TypeArguments = null) : ExpressionSyntax(Position);

public sealed record ArgumentSyntax(SourcePosition Position, string? Name, ExpressionSyntax Value)
    : SyntaxNode(Position);

public sealed record IndexExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    IReadOnlyList<ExpressionSyntax> Arguments,
    bool IsNullConditional) : ExpressionSyntax(Position);

public sealed record CastExpressionSyntax(SourcePosition Position, TypeSyntax Type, ExpressionSyntax Operand)
    : ExpressionSyntax(Position);

public sealed record AwaitExpressionSyntax(SourcePosition Position, ExpressionSyntax Operand)
    : ExpressionSyntax(Position);

public sealed record IsExpressionSyntax(SourcePosition Position, ExpressionSyntax Operand, PatternSyntax Pattern)
    : ExpressionSyntax(Position);

public sealed record AsExpressionSyntax(SourcePosition Position, ExpressionSyntax Operand, TypeSyntax Type)
    : ExpressionSyntax(Position);

public sealed record TypeofExpressionSyntax(SourcePosition Position, TypeSyntax Type)
    : ExpressionSyntax(Position);

public sealed record ObjectCreationExpressionSyntax(
    SourcePosition Position,
    TypeSyntax Type,
    IReadOnlyList<ArgumentSyntax> Arguments,
    InitializerSyntax? Initializer = null) : ExpressionSyntax(Position);

public abstract record InitializerSyntax(SourcePosition Position) : SyntaxNode(Position);

/// <summary><c>{ Name = value, Other = value }</c></summary>
public sealed record ObjectInitializerSyntax(
    SourcePosition Position,
    IReadOnlyList<MemberInitializerSyntax> Members) : InitializerSyntax(Position);

public sealed record MemberInitializerSyntax(
    SourcePosition Position,
    string Name,
    ExpressionSyntax Value) : SyntaxNode(Position);

/// <summary><c>{ 1, 2, 3 }</c> — each element becomes an <c>Add</c> call.</summary>
public sealed record CollectionInitializerSyntax(
    SourcePosition Position,
    IReadOnlyList<ExpressionSyntax> Elements) : InitializerSyntax(Position);

/// <summary>
/// <c>new int[3]</c>, <c>new int[] { 1, 2 }</c> and <c>new[] { 1, 2 }</c>. A null
/// <paramref name="ElementType"/> means the element type comes from the elements.
/// </summary>
public sealed record ArrayCreationExpressionSyntax(
    SourcePosition Position,
    TypeSyntax? ElementType,
    ExpressionSyntax? Length,
    IReadOnlyList<ExpressionSyntax>? Elements) : ExpressionSyntax(Position);

/// <summary>A <c>$"..."</c> string, already split into literal text and interpolation holes.</summary>
/// <summary>
/// <c>[a, b, c]</c>. Like a lambda it has no type of its own — the target type decides whether
/// it becomes an array, a <c>List&lt;T&gt;</c>, or something else with an <c>Add</c> method.
/// </summary>
public sealed record CollectionExpressionSyntax(
    SourcePosition Position,
    IReadOnlyList<ExpressionSyntax> Elements) : ExpressionSyntax(Position);

/// <summary><c>default</c> (Type null) or <c>default(T)</c>.</summary>
public sealed record DefaultExpressionSyntax(
    SourcePosition Position,
    TypeSyntax? Type) : ExpressionSyntax(Position);

/// <summary><c>x ?? throw new E()</c>. Produces no value, so it fits any target type.</summary>
public sealed record ThrowExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Exception) : ExpressionSyntax(Position);

/// <summary><c>nameof(x)</c>. The operand is never evaluated, only spelled.</summary>
public sealed record NameOfExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Operand) : ExpressionSyntax(Position);

public sealed record InterpolatedStringExpressionSyntax(
    SourcePosition Position,
    IReadOnlyList<InterpolationPartSyntax> Parts) : ExpressionSyntax(Position);

public sealed record InterpolationPartSyntax(
    SourcePosition Position,
    string? Text,
    ExpressionSyntax? Value,
    string? Alignment,
    string? Format) : SyntaxNode(Position)
{
    public bool IsHole => Value is not null;
}

/// <summary>
/// Parsed but not yet bindable. Recognising the shape lets the binder emit a precise
/// "not supported" diagnostic instead of a confusing syntax error.
/// </summary>
/// <summary>
/// One lambda or local-function parameter. <paramref name="Type"/> is null for a lambda
/// parameter written bare, where the target delegate supplies the type.
/// </summary>
public sealed record LambdaParameterSyntax(
    SourcePosition Position,
    TypeSyntax? Type,
    string Name) : SyntaxNode(Position);

public sealed record LambdaExpressionSyntax(
    SourcePosition Position,
    IReadOnlyList<LambdaParameterSyntax> Parameters,
    SyntaxNode Body) : ExpressionSyntax(Position);

/// <summary>Placeholder produced after a parse error so parsing can continue.</summary>
public sealed record ErrorExpressionSyntax(SourcePosition Position) : ExpressionSyntax(Position);

// ---------------------------------------------------------------- statements

public abstract record StatementSyntax(SourcePosition Position) : SyntaxNode(Position);

public sealed record BlockStatementSyntax(SourcePosition Position, IReadOnlyList<StatementSyntax> Statements)
    : StatementSyntax(Position);

public sealed record ExpressionStatementSyntax(SourcePosition Position, ExpressionSyntax Expression)
    : StatementSyntax(Position);

public sealed record VariableDeclarationSyntax(
    SourcePosition Position,
    TypeSyntax? Type,
    string Name,
    ExpressionSyntax? Initializer) : StatementSyntax(Position);

public sealed record IfStatementSyntax(
    SourcePosition Position,
    ExpressionSyntax Condition,
    StatementSyntax Then,
    StatementSyntax? Else) : StatementSyntax(Position);

public sealed record WhileStatementSyntax(SourcePosition Position, ExpressionSyntax Condition, StatementSyntax Body)
    : StatementSyntax(Position);

public sealed record DoWhileStatementSyntax(SourcePosition Position, StatementSyntax Body, ExpressionSyntax Condition)
    : StatementSyntax(Position);

public sealed record ForStatementSyntax(
    SourcePosition Position,
    IReadOnlyList<StatementSyntax> Initializers,
    ExpressionSyntax? Condition,
    IReadOnlyList<ExpressionSyntax> Incrementors,
    StatementSyntax Body) : StatementSyntax(Position);

public sealed record ForEachStatementSyntax(
    SourcePosition Position,
    TypeSyntax? ElementType,
    string Name,
    ExpressionSyntax Collection,
    StatementSyntax Body) : StatementSyntax(Position);

public sealed record ReturnStatementSyntax(SourcePosition Position, ExpressionSyntax? Expression)
    : StatementSyntax(Position);

public sealed record BreakStatementSyntax(SourcePosition Position) : StatementSyntax(Position);

public sealed record ContinueStatementSyntax(SourcePosition Position) : StatementSyntax(Position);

public sealed record ThrowStatementSyntax(SourcePosition Position, ExpressionSyntax Expression)
    : StatementSyntax(Position);

/// <summary>
/// <c>int F(int x) { ... }</c>. Every parameter and the return type are written out — there is
/// nothing for the binder to infer. A null <paramref name="ReturnType"/> means <c>void</c>.
/// </summary>
public sealed record LocalFunctionStatementSyntax(
    SourcePosition Position,
    TypeSyntax? ReturnType,
    string Name,
    IReadOnlyList<LambdaParameterSyntax> Parameters,
    SyntaxNode Body) : StatementSyntax(Position);

public sealed record SwitchStatementSyntax(
    SourcePosition Position,
    ExpressionSyntax Governing,
    IReadOnlyList<SwitchSectionSyntax> Sections) : StatementSyntax(Position);

/// <summary>One or more labels sharing a body. Falling out of the body is not allowed.</summary>
public sealed record SwitchSectionSyntax(
    SourcePosition Position,
    IReadOnlyList<SwitchLabelSyntax> Labels,
    IReadOnlyList<StatementSyntax> Statements) : SyntaxNode(Position);

/// <summary>A <c>case</c> label, or <c>default:</c> when <paramref name="Pattern"/> is null.</summary>
public sealed record SwitchLabelSyntax(
    SourcePosition Position,
    PatternSyntax? Pattern,
    ExpressionSyntax? Guard) : SyntaxNode(Position);

public sealed record CatchClauseSyntax(
    SourcePosition Position,
    TypeSyntax? ExceptionType,
    string? VariableName,
    BlockStatementSyntax Body) : SyntaxNode(Position);

public sealed record TryStatementSyntax(
    SourcePosition Position,
    BlockStatementSyntax Body,
    IReadOnlyList<CatchClauseSyntax> Catches,
    BlockStatementSyntax? Finally) : StatementSyntax(Position);

/// <summary>Placeholder produced after a parse error so parsing can continue.</summary>
public sealed record ErrorStatementSyntax(SourcePosition Position) : StatementSyntax(Position);

// ---------------------------------------------------------------- root

public sealed record CompilationUnitSyntax(SourcePosition Position, IReadOnlyList<StatementSyntax> Statements)
    : SyntaxNode(Position);
