using System.Reflection;
using V.Script.Diagnostics;

namespace V.Script.Binding;

/// <summary>
/// A branch target. The emitter creates one IL label per symbol, which is what lets a jump be
/// emitted before the label it targets has been seen.
/// </summary>
public sealed class LabelSymbol(string name)
{
    public string Name { get; } = name;

    /// <summary>False until the labelled statement itself is bound.</summary>
    public bool IsDefined { get; internal set; }

    /// <summary>How deep in try/catch/finally the label sits; jumping inwards is illegal.</summary>
    public int HandlerDepth { get; internal set; }

    public override string ToString() => Name + ":";
}

/// <summary>A local slot allocated by the binder; the emitter declares one IL local per slot.</summary>
public sealed class LocalSymbol(string name, Type type, bool isCompilerGenerated = false)
{
    public string Name { get; } = name;

    public Type Type { get; } = type;

    public bool IsCompilerGenerated { get; } = isCompilerGenerated;

    /// <summary>Assigned by the binder once the whole method is bound.</summary>
    public int Slot { get; internal set; } = -1;

    /// <summary>
    /// For a lambda parameter, its index among the lambda method's arguments (argument 0 is
    /// always the closure). -1 for ordinary locals.
    /// </summary>
    public int LambdaArgIndex { get; internal set; } = -1;

    public bool IsLambdaParameter => LambdaArgIndex >= 0;

    /// <summary>
    /// Which function declared this variable: 0 for the script body, 1+ inside a lambda.
    /// A reference from a deeper function is what makes the variable captured.
    /// </summary>
    internal int FunctionDepth { get; set; }

    /// <summary>The scope this variable was declared in; it is the scope that would capture it.</summary>
    internal ClosureScope? DeclaringScope { get; set; }

    /// <summary>
    /// The value of a <c>const</c>, which is folded into every use rather than stored. Null for
    /// an ordinary variable, including one that merely happens to be initialised by a literal.
    /// </summary>
    public BoundLiteral? ConstantValue { get; internal set; }

    /// <summary>
    /// The tuple element names this variable was declared with, if any. They exist only at
    /// compile time — <c>ValueTuple</c> itself knows nothing about them.
    /// </summary>
    public IReadOnlyList<string?>? TupleNames { get; internal set; }

    /// <summary>Set when some lambda reads this variable; it then lives in a closure, not an IL local.</summary>
    public ClosureScope? Closure { get; internal set; }

    public int ClosureSlot { get; internal set; } = -1;

    public bool IsCaptured => Closure is not null;

    public override string ToString() =>
        $"{TypeResolver.Display(Type)} {Name}" + (IsCaptured ? $" (closure slot {ClosureSlot})" : $" (slot {Slot})");
}

/// <summary>
/// The set of captured variables belonging to one lexical scope. A scope only becomes a real
/// <see cref="ScriptClosure"/> at run time if something was actually captured from it, and it is
/// instantiated on every entry — so a loop body allocates one per iteration, which is what makes
/// a captured <c>foreach</c> variable behave per-iteration as it does in C#.
/// </summary>
public sealed class ClosureScope(ClosureScope? parent)
{
    private readonly List<LocalSymbol> _slots = [];

    public ClosureScope? Parent { get; } = parent;

    public IReadOnlyList<LocalSymbol> Slots => _slots;

    /// <summary>False when nothing was captured here, in which case no instance is created.</summary>
    public bool IsMaterialized => _slots.Count > 0;

    /// <summary>
    /// The concrete <see cref="ScriptClosure"/> subclass this scope instantiates, memoised by the
    /// emitter once the slot list is final.
    /// </summary>
    internal Type? RuntimeType { get; set; }

    /// <summary>The nearest enclosing scope that does get an instance, or null.</summary>
    public ClosureScope? MaterializedParent
    {
        get
        {
            for (var scope = Parent; scope is not null; scope = scope.Parent)
                if (scope.IsMaterialized)
                    return scope;
            return null;
        }
    }

    public void Capture(LocalSymbol local)
    {
        if (local.Closure is not null) return;

        local.Closure = this;
        local.ClosureSlot = _slots.Count;
        _slots.Add(local);
    }

    /// <summary>Number of <c>Parent</c> hops from this scope to <paramref name="target"/>, or -1.</summary>
    public int HopsTo(ClosureScope target)
    {
        var hops = 0;
        for (var scope = this; scope is not null; scope = scope.MaterializedParent)
        {
            if (ReferenceEquals(scope, target)) return hops;
            hops++;
        }
        return -1;
    }
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
    Conversion Conversion,
    bool IsChecked = false) : BoundExpression(Position, Type);

public sealed record BoundBinary(
    SourcePosition Position,
    Type Type,
    BoundBinaryKind Kind,
    BoundExpression Left,
    BoundExpression Right,
    bool IsLifted,
    MethodInfo? Method,
    bool IsChecked = false) : BoundExpression(Position, Type);

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

/// <summary>
/// <c>a[i]</c>, or <c>a[i, j]</c> for a multi-dimensional array — where the CLR has no
/// <c>ldelem</c> and the access goes through the array type's own <c>Get</c> / <c>Set</c>.
/// </summary>
public sealed record BoundArrayAccess(
    SourcePosition Position,
    Type Type,
    BoundExpression Array,
    IReadOnlyList<BoundExpression> Indices) : BoundExpression(Position, Type)
{
    public BoundArrayAccess(SourcePosition position, Type type, BoundExpression array, BoundExpression index)
        : this(position, type, array, (IReadOnlyList<BoundExpression>)[index])
    {
    }

    public bool IsMultiDimensional => Indices.Count > 1;
}

public sealed record BoundObjectCreation(
    SourcePosition Position,
    ConstructorInfo Constructor,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Position, Constructor.DeclaringType!);

/// <summary>
/// <c>new T[n]</c>. The elements are the type's default, which is what <c>newarr</c> gives.
/// </summary>
public sealed record BoundNewArray(
    SourcePosition Position,
    Type ElementType,
    IReadOnlyList<BoundExpression> Lengths) : BoundExpression(
        Position,
        Lengths.Count == 1 ? ElementType.MakeArrayType() : ElementType.MakeArrayType(Lengths.Count));

/// <summary>
/// <c>(1, "x")</c>. <paramref name="Names"/> is compile-time only and never reaches the IL.
/// </summary>
public sealed record BoundTupleLiteral(
    SourcePosition Position,
    Type Type,
    ConstructorInfo Constructor,
    IReadOnlyList<BoundExpression> Elements,
    IReadOnlyList<string?> Names) : BoundExpression(Position, Type);

/// <summary>
/// <c>out var x</c> before resolution has said what type <c>x</c> is. It is replaced by a
/// <see cref="BoundLocalAddress"/> once the overload is chosen.
/// </summary>
public sealed record BoundOutVariable(
    SourcePosition Position,
    string Name,
    Syntax.TypeSyntax? DeclaredType) : BoundExpression(Position, Conversions.OutVariableType);

/// <summary>
/// A named group of methods that has not been given a delegate type yet. Like a lambda, it only
/// becomes a value once something says which delegate it converts to.
/// </summary>
public sealed record BoundMethodGroup(
    SourcePosition Position,
    string Name,
    BoundExpression? Receiver,
    IReadOnlyList<MethodInfo> Methods) : BoundExpression(Position, Conversions.MethodGroupType);

/// <summary>
/// A delegate built from a method group: <c>ldnull</c>/receiver, <c>ldftn</c>, <c>newobj</c>.
/// </summary>
public sealed record BoundMethodGroupConversion(
    SourcePosition Position,
    Type Type,
    BoundExpression? Receiver,
    MethodInfo Method) : BoundExpression(Position, Type);

/// <summary>
/// The address of a local, for passing it to an <c>out</c> parameter. Only ever produced for
/// compiler-generated temporaries, which are never captured, so <c>ldloca</c> always applies.
/// </summary>
public sealed record BoundLocalAddress(SourcePosition Position, LocalSymbol Local)
    : BoundExpression(Position, Local.Type.MakeByRefType());

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

/// <summary>
/// A collection expression before a target type is known. Which collection it becomes — an
/// array, a <c>List&lt;T&gt;</c>, an interface — is decided entirely by the conversion.
/// </summary>
public sealed record BoundUnboundCollection(
    SourcePosition Position,
    Syntax.CollectionExpressionSyntax Syntax) : BoundExpression(Position, Conversions.CollectionType);

/// <summary>The bare <c>default</c> literal; <c>default(T)</c> binds straight to BoundDefault.</summary>
public sealed record BoundDefaultLiteral(SourcePosition Position)
    : BoundExpression(Position, Conversions.DefaultLiteralType);

/// <summary>
/// <c>throw e</c> in expression position. It yields no value, so <see cref="BoundExpression.Type"/>
/// is whatever the context asks for — the emitter never leaves anything on the stack for it.
/// </summary>
public sealed record BoundThrowExpression(
    SourcePosition Position,
    Type Type,
    BoundExpression Exception) : BoundExpression(Position, Type);

/// <summary>
/// A lambda before a target type is known. C# anonymous functions have no type of their own, so
/// this survives until overload resolution picks the parameter it converts to.
/// </summary>
public sealed record BoundUnboundLambda(
    SourcePosition Position,
    Syntax.LambdaExpressionSyntax Syntax) : BoundExpression(Position, Conversions.LambdaType);

/// <summary>
/// A lambda bound against a concrete delegate type; the emitter gives it its own method.
/// Exactly one of <paramref name="Body"/> and <paramref name="BodyStatement"/> is set,
/// depending on whether the source wrote an expression or a block.
/// </summary>
/// <summary>
/// A bound lambda. <paramref name="ReturnType"/> is what the IL body returns; for an async
/// lambda that is the unwrapped value, while <paramref name="DeclaredReturnType"/> is the
/// <c>Task</c> the method is declared to return and the runtime produces.
/// </summary>
public sealed record BoundLambda(
    SourcePosition Position,
    Type Type,
    IReadOnlyList<LocalSymbol> Parameters,
    IReadOnlyList<LocalSymbol> Locals,
    BoundExpression? Body,
    Type ReturnType,
    ClosureScope OwnScope,
    ClosureScope? EnclosingClosure,
    BoundStatement? BodyStatement = null,
    bool IsAsync = false,
    Type? DeclaredReturnType = null) : BoundExpression(Position, Type)
{
    /// <summary>Index into the host's lambda table; assigned by the emitter.</summary>
    public int Index { get; internal set; } = -1;
}

/// <summary>Invoking a delegate-typed value, as opposed to calling a named method.</summary>
public sealed record BoundDelegateInvoke(
    SourcePosition Position,
    Type Type,
    BoundExpression Target,
    System.Reflection.MethodInfo Invoke,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Position, Type);

/// <summary>
/// Intermediate node standing for a type name used as the receiver of a static member access.
/// It never survives to emission; the binder reports an error if one is used as a value.
/// </summary>
public sealed record BoundTypeReference(SourcePosition Position, Type ReferencedType)
    : BoundExpression(Position, typeof(Type));

// ================================================================= statements

public abstract record BoundStatement(SourcePosition Position);

public sealed record BoundBlock(
    SourcePosition Position,
    IReadOnlyList<BoundStatement> Statements,
    ClosureScope? Closure = null) : BoundStatement(Position);

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
    BoundStatement Body,
    ClosureScope? Closure = null) : BoundStatement(Position);

public sealed record BoundReturn(SourcePosition Position, BoundExpression? Expression)
    : BoundStatement(Position);

public sealed record BoundBreak(SourcePosition Position) : BoundStatement(Position);

public sealed record BoundLabel(SourcePosition Position, LabelSymbol Label) : BoundStatement(Position);

public sealed record BoundGoto(SourcePosition Position, LabelSymbol Label) : BoundStatement(Position);

/// <summary>
/// A region that <c>break</c> leaves. A <c>switch</c> lowers to one of these wrapped around an
/// if/else chain; <c>continue</c> inside it still belongs to the enclosing loop.
/// </summary>
/// <remarks>
/// <paramref name="AllPathsReturn"/> is computed by whoever built the scope. The body is a flat
/// list of labelled sections, so no structural analysis of it could tell whether control can
/// reach the end — only the builder knows.
/// </remarks>
public sealed record BoundBreakScope(
    SourcePosition Position,
    BoundStatement Body,
    bool AllPathsReturn = false) : BoundStatement(Position);

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
    ClosureScope RootScope,
    IReadOnlyList<BoundLambda> Lambdas);
