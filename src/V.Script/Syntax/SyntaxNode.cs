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
    int ArrayRank,
    IReadOnlyList<string?>? TupleNames = null,
    IReadOnlyList<int>? ArrayDimensions = null) : SyntaxNode(Position)
{
    /// <summary>
    /// How many dimensions each <c>[]</c> group has, outermost first. Null means every group is
    /// one-dimensional, which is all but a rare case — <c>int[,]</c> is what needs this.
    /// </summary>
    public int DimensionsAt(int group) => ArrayDimensions is null ? 1 : ArrayDimensions[group];

    /// <summary>
    /// A tuple type is written <c>(int a, string b)</c> but resolved as
    /// <c>ValueTuple&lt;int, string&gt;</c>; the element names survive only here.
    /// </summary>
    public bool IsTuple => TupleNames is not null;

    public string DisplayName
    {
        get
        {
            if (TupleNames is not null)
            {
                var elements = TypeArguments.Select((a, i) =>
                    TupleNames[i] is { } elementName ? $"{a.DisplayName} {elementName}" : a.DisplayName);
                return '(' + string.Join(", ", elements) + ')';
            }

            var name = string.Join('.', NameParts);
            if (TypeArguments.Count > 0)
                name += '<' + string.Join(", ", TypeArguments.Select(a => a.DisplayName)) + '>';
            if (IsNullable) name += '?';
            for (var i = 0; i < ArrayRank; i++)
                name += '[' + new string(',', DimensionsAt(i) - 1) + ']';
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

/// <summary>How an argument is passed. <c>out</c> also allows declaring the variable inline.</summary>
public enum ArgumentRefKind
{
    None,
    Ref,
    Out,
}

/// <summary>
/// One argument. <paramref name="DeclaredType"/> is set for <c>out int x</c> and is null for
/// <c>out var x</c>; both are recognised by <paramref name="DeclaresVariable"/>.
/// </summary>
public sealed record ArgumentSyntax(
    SourcePosition Position,
    string? Name,
    ExpressionSyntax Value,
    ArgumentRefKind RefKind = ArgumentRefKind.None,
    bool DeclaresVariable = false,
    TypeSyntax? DeclaredType = null) : SyntaxNode(Position);

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

/// <summary>
/// One entry of an object initializer: <c>Name = v</c>, <c>[k] = v</c>, or either of those with
/// a nested initializer instead of a value.
/// </summary>
public sealed record MemberInitializerSyntax(
    SourcePosition Position,
    string? Name,
    IReadOnlyList<ExpressionSyntax>? Index,
    ExpressionSyntax? Value,
    InitializerSyntax? Nested) : SyntaxNode(Position);

/// <summary><c>{ 1, 2, 3 }</c> — each element becomes an <c>Add</c> call.</summary>
public sealed record CollectionInitializerSyntax(
    SourcePosition Position,
    IReadOnlyList<ExpressionSyntax> Elements) : InitializerSyntax(Position);

/// <summary>
/// <c>new int[3]</c>, <c>new int[] { 1, 2 }</c> and <c>new[] { 1, 2 }</c>. A null
/// <paramref name="ElementType"/> means the element type comes from the elements.
/// </summary>
/// <summary>
/// <c>new T[a, b]</c> (Lengths set), or <c>new T[] { ... }</c> / <c>new[] { ... }</c> (Elements set).
/// </summary>
public sealed record ArrayCreationExpressionSyntax(
    SourcePosition Position,
    TypeSyntax? ElementType,
    IReadOnlyList<ExpressionSyntax>? Lengths,
    IReadOnlyList<ExpressionSyntax>? Elements) : ExpressionSyntax(Position);

/// <summary>A <c>$"..."</c> string, already split into literal text and interpolation holes.</summary>
/// <summary>
/// <c>[a, b, c]</c>. Like a lambda it has no type of its own — the target type decides whether
/// it becomes an array, a <c>List&lt;T&gt;</c>, or something else with an <c>Add</c> method.
/// </summary>
public sealed record CollectionExpressionSyntax(
    SourcePosition Position,
    IReadOnlyList<CollectionElementSyntax> Elements) : ExpressionSyntax(Position);

/// <summary>One element of <c>[a, ..b, c]</c>; a spread contributes a whole sequence.</summary>
public sealed record CollectionElementSyntax(
    SourcePosition Position,
    bool IsSpread,
    ExpressionSyntax Value) : SyntaxNode(Position);

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
    SyntaxNode Body,
    bool IsAsync = false) : ExpressionSyntax(Position);

/// <summary><c>^e</c> — an index counted from the end.</summary>
public sealed record FromEndExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Operand) : ExpressionSyntax(Position);

/// <summary><c>a..b</c>, with either side optional.</summary>
public sealed record RangeExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax? Start,
    ExpressionSyntax? End) : ExpressionSyntax(Position);

/// <summary><c>r with { X = 1 }</c>.</summary>
public sealed record WithExpressionSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    ObjectInitializerSyntax Initializer) : ExpressionSyntax(Position);

/// <summary><c>checked(e)</c> / <c>unchecked(e)</c>.</summary>
public sealed record CheckedExpressionSyntax(
    SourcePosition Position,
    bool IsChecked,
    ExpressionSyntax Operand) : ExpressionSyntax(Position);

/// <summary><c>(1, 2)</c> or <c>(a: 1, b: 2)</c>.</summary>
public sealed record TupleExpressionSyntax(
    SourcePosition Position,
    IReadOnlyList<TupleElementSyntax> Elements) : ExpressionSyntax(Position);

public sealed record TupleElementSyntax(
    SourcePosition Position,
    string? Name,
    ExpressionSyntax Value) : SyntaxNode(Position);

/// <summary>Placeholder produced after a parse error so parsing can continue.</summary>
public sealed record ErrorExpressionSyntax(SourcePosition Position) : ExpressionSyntax(Position);

// ---------------------------------------------------------------- statements

public abstract record StatementSyntax(SourcePosition Position) : SyntaxNode(Position);

public sealed record BlockStatementSyntax(SourcePosition Position, IReadOnlyList<StatementSyntax> Statements)
    : StatementSyntax(Position);

public sealed record ExpressionStatementSyntax(SourcePosition Position, ExpressionSyntax Expression)
    : StatementSyntax(Position);

/// <summary>
/// <c>[const] Type name [= init]</c>. A <c>const</c> declaration folds into its uses, so it has
/// no storage at all.
/// </summary>
public sealed record VariableDeclarationSyntax(
    SourcePosition Position,
    TypeSyntax? Type,
    string Name,
    ExpressionSyntax? Initializer,
    bool IsConst = false) : StatementSyntax(Position);

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
    SyntaxNode Body,
    bool IsAsync = false,
    bool IsStatic = false) : StatementSyntax(Position);

/// <summary>
/// <c>var (a, b) = t;</c> or <c>(int a, string b) = t;</c>. Only declarations come through
/// here — assigning to variables that already exist is an ordinary assignment whose target
/// happens to be a <see cref="TupleExpressionSyntax"/>.
/// </summary>
public sealed record DeconstructionStatementSyntax(
    SourcePosition Position,
    IReadOnlyList<DeconstructionTargetSyntax> Targets,
    ExpressionSyntax Value) : StatementSyntax(Position);

/// <summary>
/// One element of a deconstruction. A written type or <c>var</c> declares a new variable;
/// a bare name assigns to one that already exists, which is what lets the two forms mix.
/// </summary>
public sealed record DeconstructionTargetSyntax(
    SourcePosition Position,
    TypeSyntax? Type,
    bool IsVar,
    string Name) : SyntaxNode(Position)
{
    public bool Declares => IsVar || Type is not null;
}

/// <summary>
/// <c>using (var x = e) body</c> or <c>using var x = e;</c>. The second form has no body of its
/// own — it runs to the end of the enclosing block, which the binder arranges.
/// </summary>
public sealed record UsingStatementSyntax(
    SourcePosition Position,
    VariableDeclarationSyntax? Declaration,
    ExpressionSyntax? Resource,
    StatementSyntax? Body) : StatementSyntax(Position);

public sealed record LockStatementSyntax(
    SourcePosition Position,
    ExpressionSyntax Target,
    StatementSyntax Body) : StatementSyntax(Position);

/// <summary><c>name: statement</c>.</summary>
public sealed record LabeledStatementSyntax(
    SourcePosition Position,
    string Name,
    StatementSyntax Statement) : StatementSyntax(Position);

/// <summary>
/// <c>goto name;</c>, <c>goto case v;</c> or <c>goto default;</c>. The last two carry no label
/// name and are resolved against the switch that encloses them.
/// </summary>
public sealed record GotoStatementSyntax(
    SourcePosition Position,
    string? Label,
    ExpressionSyntax? CaseValue,
    bool IsDefault = false) : StatementSyntax(Position);

/// <summary><c>checked { ... }</c> / <c>unchecked { ... }</c>.</summary>
public sealed record CheckedStatementSyntax(
    SourcePosition Position,
    bool IsChecked,
    BlockStatementSyntax Body) : StatementSyntax(Position);

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
