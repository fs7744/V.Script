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
    bool IsNullConditional) : ExpressionSyntax(Position);

public sealed record InvocationExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    IReadOnlyList<ArgumentSyntax> Arguments) : ExpressionSyntax(Position);

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

public sealed record IsExpressionSyntax(SourcePosition Position, ExpressionSyntax Operand, TypeSyntax Type)
    : ExpressionSyntax(Position);

public sealed record AsExpressionSyntax(SourcePosition Position, ExpressionSyntax Operand, TypeSyntax Type)
    : ExpressionSyntax(Position);

public sealed record TypeofExpressionSyntax(SourcePosition Position, TypeSyntax Type)
    : ExpressionSyntax(Position);

public sealed record ObjectCreationExpressionSyntax(
    SourcePosition Position,
    TypeSyntax Type,
    IReadOnlyList<ArgumentSyntax> Arguments) : ExpressionSyntax(Position);

/// <summary>
/// Parsed but not yet bindable. Recognising the shape lets the binder emit a precise
/// "not supported" diagnostic instead of a confusing syntax error.
/// </summary>
public sealed record LambdaExpressionSyntax(
    SourcePosition Position,
    IReadOnlyList<string> Parameters,
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
