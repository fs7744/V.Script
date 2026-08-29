using System.Reflection;
using V.Script.Diagnostics;

namespace V.Script.Binding;

/// <summary>A local slot allocated by the binder; the emitter declares one IL local per slot.</summary>
public sealed class LocalSymbol(string name, Type type, bool isCompilerGenerated = false)
{
    public string Name { get; } = name;

    public Type Type { get; } = type;

    public bool IsCompilerGenerated { get; } = isCompilerGenerated;

    /// <summary>Assigned by the binder once the whole method is bound.</summary>
    public int Slot { get; internal set; } = -1;

    public override string ToString() => $"{TypeResolver.Display(Type)} {Name} (slot {Slot})";
}

public enum BoundBinaryKind
{
    Add, Subtract, Multiply, Divide, Modulo,
    BitAnd, BitOr, BitXor, LeftShift, RightShift,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
}

public enum BoundUnaryKind
{
    Plus, Negate, LogicalNot, BitwiseNot,
}

public enum AwaitKind
{
    Task,
    TaskOfT,
    ValueTask,
    ValueTaskOfT,
}

// ================================================================= expressions

public abstract record BoundExpression(SourcePosition Position, Type Type);

public sealed record BoundLiteral(SourcePosition Position, Type Type, object? Value)
    : BoundExpression(Position, Type);

/// <summary>The untyped <c>null</c> literal before it is converted to a target type.</summary>
public sealed record BoundNullLiteral(SourcePosition Position)
    : BoundExpression(Position, Conversions.NullLiteralType);

public sealed record BoundDefault(SourcePosition Position, Type Type)
    : BoundExpression(Position, Type);

public sealed record BoundLocalAccess(SourcePosition Position, LocalSymbol Local)
    : BoundExpression(Position, Local.Type);

/// <summary>Reads one of the generated method's parameters (globals object or a script argument).</summary>
public sealed record BoundParameterAccess(SourcePosition Position, Type Type, int Index)
    : BoundExpression(Position, Type);

public sealed record BoundConversion(
    SourcePosition Position,
    Type Type,
    BoundExpression Operand,
    Conversion Conversion) : BoundExpression(Position, Type);

public sealed record BoundBinary(
    SourcePosition Position,
    Type Type,
    BoundBinaryKind Kind,
    BoundExpression Left,
    BoundExpression Right,
    bool IsLifted,
    MethodInfo? Method) : BoundExpression(Position, Type);

public sealed record BoundUnary(
    SourcePosition Position,
    Type Type,
    BoundUnaryKind Kind,
    BoundExpression Operand,
    bool IsLifted,
    MethodInfo? Method) : BoundExpression(Position, Type);

/// <summary>Short-circuiting <c>&amp;&amp;</c> / <c>||</c>.</summary>
public sealed record BoundLogical(
    SourcePosition Position,
    BoundExpression Left,
    BoundExpression Right,
    bool IsAnd) : BoundExpression(Position, typeof(bool));

public sealed record BoundConditional(
    SourcePosition Position,
    Type Type,
    BoundExpression Condition,
    BoundExpression WhenTrue,
    BoundExpression WhenFalse) : BoundExpression(Position, Type);

/// <summary>
/// <c>receiver?.rest</c>. The receiver is stored into <paramref name="Temp"/>, and
/// <paramref name="WhenNotNull"/> reads it back through a <see cref="BoundLocalAccess"/>.
/// </summary>
public sealed record BoundConditionalAccess(
    SourcePosition Position,
    Type Type,
    BoundExpression Receiver,
    BoundExpression WhenNotNull,
    LocalSymbol Temp) : BoundExpression(Position, Type);

public sealed record BoundFieldAccess(
    SourcePosition Position,
    BoundExpression? Receiver,
    FieldInfo Field) : BoundExpression(Position, Field.FieldType);

public sealed record BoundPropertyAccess(
    SourcePosition Position,
    BoundExpression? Receiver,
    PropertyInfo Property) : BoundExpression(Position, Property.PropertyType);

public sealed record BoundCall(
    SourcePosition Position,
    BoundExpression? Receiver,
    MethodInfo Method,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Position, Method.ReturnType);

public sealed record BoundIndexerAccess(
    SourcePosition Position,
    BoundExpression Receiver,
    PropertyInfo Indexer,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Position, Indexer.PropertyType);

public sealed record BoundArrayAccess(
    SourcePosition Position,
    Type Type,
    BoundExpression Array,
    BoundExpression Index) : BoundExpression(Position, Type);

public sealed record BoundObjectCreation(
    SourcePosition Position,
    ConstructorInfo Constructor,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Position, Constructor.DeclaringType!);

public sealed record BoundArrayCreation(
    SourcePosition Position,
    Type ElementType,
    IReadOnlyList<BoundExpression> Elements) : BoundExpression(Position, ElementType.MakeArrayType());

public sealed record BoundAwait(
    SourcePosition Position,
    Type Type,
    BoundExpression Operand,
    AwaitKind Kind,
    MethodInfo AwaitHelper) : BoundExpression(Position, Type);

public sealed record BoundIsType(
    SourcePosition Position,
    BoundExpression Operand,
    Type TargetType) : BoundExpression(Position, typeof(bool));

public sealed record BoundAsType(
    SourcePosition Position,
    Type Type,
    BoundExpression Operand) : BoundExpression(Position, Type);

public sealed record BoundTypeofExpression(SourcePosition Position, Type TargetType)
    : BoundExpression(Position, typeof(Type));

public sealed record BoundAssignment(
    SourcePosition Position,
    BoundExpression Target,
    BoundExpression Value) : BoundExpression(Position, Target.Type);

public sealed record BoundErrorExpression(SourcePosition Position)
    : BoundExpression(Position, typeof(object));

/// <summary>
/// Evaluates <paramref name="SideEffects"/> in order (discarding their values), then yields
/// <paramref name="Value"/>. Compound assignment, <c>++</c>/<c>--</c> and receiver capture all
/// lower into this shape so the emitter never has to reason about evaluation order itself.
/// </summary>
public sealed record BoundSequence(
    SourcePosition Position,
    Type Type,
    IReadOnlyList<BoundExpression> SideEffects,
    BoundExpression Value) : BoundExpression(Position, Type);

public enum IntrinsicKind
{
    /// <summary>The <see cref="ScriptState"/> local's cancellation token.</summary>
    ScriptStateToken,
}

/// <summary>A value produced by the generated method's own frame rather than by script code.</summary>
public sealed record BoundIntrinsic(SourcePosition Position, Type Type, IntrinsicKind Kind)
    : BoundExpression(Position, Type);

/// <summary>
/// Intermediate node standing for a type name used as the receiver of a static member access.
/// It never survives to emission; the binder reports an error if one is used as a value.
/// </summary>
public sealed record BoundTypeReference(SourcePosition Position, Type ReferencedType)
    : BoundExpression(Position, typeof(Type));

// ================================================================= statements

public abstract record BoundStatement(SourcePosition Position);

public sealed record BoundBlock(SourcePosition Position, IReadOnlyList<BoundStatement> Statements)
    : BoundStatement(Position);

public sealed record BoundNop(SourcePosition Position) : BoundStatement(Position);

/// <summary>Evaluates an expression for its side effects; the emitter pops any result.</summary>
public sealed record BoundExpressionStatement(SourcePosition Position, BoundExpression Expression)
    : BoundStatement(Position);

public sealed record BoundLocalDeclaration(
    SourcePosition Position,
    LocalSymbol Local,
    BoundExpression? Initializer) : BoundStatement(Position);

public sealed record BoundIf(
    SourcePosition Position,
    BoundExpression Condition,
    BoundStatement Then,
    BoundStatement? Else) : BoundStatement(Position);

public sealed record BoundWhile(
    SourcePosition Position,
    BoundExpression Condition,
    BoundStatement Body) : BoundStatement(Position);

public sealed record BoundDoWhile(
    SourcePosition Position,
    BoundStatement Body,
    BoundExpression Condition) : BoundStatement(Position);

public sealed record BoundFor(
    SourcePosition Position,
    IReadOnlyList<BoundStatement> Initializers,
    BoundExpression? Condition,
    IReadOnlyList<BoundStatement> Incrementors,
    BoundStatement Body) : BoundStatement(Position);

public sealed record BoundReturn(SourcePosition Position, BoundExpression? Expression)
    : BoundStatement(Position);

public sealed record BoundBreak(SourcePosition Position) : BoundStatement(Position);

public sealed record BoundContinue(SourcePosition Position) : BoundStatement(Position);

public sealed record BoundThrow(SourcePosition Position, BoundExpression Expression)
    : BoundStatement(Position);

public sealed record BoundCatchClause(
    SourcePosition Position,
    Type ExceptionType,
    LocalSymbol? Variable,
    BoundStatement Body);

public sealed record BoundTry(
    SourcePosition Position,
    BoundStatement Body,
    IReadOnlyList<BoundCatchClause> Catches,
    BoundStatement? Finally) : BoundStatement(Position);

// ================================================================= root

/// <summary>A fully bound script body ready for emission.</summary>
public sealed record BoundScript(
    BoundStatement Body,
    Type ReturnType,
    IReadOnlyList<LocalSymbol> Locals,
    bool IsAsync,
    bool UsesCheckpoints);
