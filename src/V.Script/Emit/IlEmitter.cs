using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

/// <summary>
/// Translates a <see cref="BoundScript"/> into IL. It targets a bare <see cref="ILGenerator"/>,
/// which is what lets the same emitter serve both carriers: a <c>DynamicMethod</c> for
/// synchronous scripts and a <c>MethodBuilder</c> marked <c>Async</c> for asynchronous ones.
/// Lambdas always get their own <c>DynamicMethod</c>, whichever carrier the script itself uses.
/// </summary>
/// <remarks>
/// By contract this class performs no type analysis. Every conversion, promotion, lifted
/// operation and capture decision was made explicit by the binder; the code below only chooses
/// opcodes.
/// </remarks>
internal sealed partial class IlEmitter
{
    private readonly ILGenerator _il;
    private readonly BoundScript _script;

    /// <summary>Set when emitting a lambda body rather than the script body.</summary>
    private readonly BoundLambda? _lambda;

    /// <summary>The scope that argument 0 refers to inside a lambda method; null in the script body.</summary>
    private readonly ClosureScope? _incomingClosure;

    private readonly Dictionary<LocalSymbol, LocalBuilder> _locals = [];
    private readonly Dictionary<ClosureScope, LocalBuilder> _closures = [];

    private LocalBuilder? _returnValue;
    private Label _returnLabel;

    private readonly Stack<(Label Break, Label Continue)> _loops = new();

    private IlEmitter(
        ILGenerator il,
        BoundScript script,
        BoundLambda? lambda,
        ClosureScope? incomingClosure)
    {
        _il = il;
        _script = script;
        _lambda = lambda;
        _incomingClosure = incomingClosure;
    }

    /// <summary>
    /// Emits the script body into <paramref name="il"/> and every lambda into a method of its
    /// own. A synchronous lambda goes into a <see cref="DynamicMethod"/>; an async one needs
    /// <see cref="MethodImplAttributes.Async"/>, which only a real method can carry, so it goes
    /// into <paramref name="asyncHost"/> — the same reason the script body has two carriers.
    /// </summary>
    /// <returns>
    /// The step that publishes the lambda table. It runs after the host type is created, because
    /// a <see cref="MethodBuilder"/> has no invokable <see cref="MethodInfo"/> before that.
    /// </returns>
    public static Action<Type?> EmitScript(
        ILGenerator il,
        BoundScript script,
        ScriptHost host,
        TypeBuilder? asyncHost = null)
    {
        var lambdas = script.Lambdas;
        var defined = new LambdaMethod[lambdas.Count];

        for (var i = 0; i < lambdas.Count; i++)
        {
            lambdas[i].Index = i;
            defined[i] = DefineLambdaMethod(lambdas[i], i, asyncHost);
        }

        new IlEmitter(il, script, null, null).EmitBody();

        for (var i = 0; i < lambdas.Count; i++)
        {
            var incoming = NearestMaterialized(lambdas[i].EnclosingClosure);
            new IlEmitter(defined[i].Generator, script, lambdas[i], incoming).EmitBody();
        }

        return createdType => PublishLambdas(script, host, defined, createdType);
    }

    private static void PublishLambdas(
        BoundScript script,
        ScriptHost host,
        LambdaMethod[] defined,
        Type? createdType)
    {
        var entries = new ScriptHost.LambdaEntry[defined.Length];

        for (var i = 0; i < defined.Length; i++)
        {
            var lambda = script.Lambdas[i];
            var method = defined[i].Resolve(createdType);
            var incoming = NearestMaterialized(lambda.EnclosingClosure);

            // A lambda with nothing to capture always receives the host's shared empty closure,
            // so its delegate can be built once here instead of on every evaluation. A capturing
            // one gets a factory that avoids CreateDelegate on the hot path.
            var shared = incoming is null
                ? method.CreateDelegate(lambda.Type, host.EmptyClosure)
                : null;

            var factory = incoming is null
                ? null
                : ClosureBinder.TryCreateFactory(method, lambda.Type);

            entries[i] = new ScriptHost.LambdaEntry(method, lambda.Type, shared, factory);
        }

        host.SetLambdas(entries);
    }

    /// <summary>One lambda's generated method, before the type that holds it exists.</summary>
    private readonly record struct LambdaMethod(DynamicMethod? Dynamic, MethodBuilder? Builder)
    {
        public ILGenerator Generator => Dynamic?.GetILGenerator() ?? Builder!.GetILGenerator();

        public MethodInfo Resolve(Type? createdType) =>
            Dynamic ?? createdType!.GetMethod(Builder!.Name, BindingFlags.Public | BindingFlags.Static)!;
    }

    private static LambdaMethod DefineLambdaMethod(BoundLambda lambda, int index, TypeBuilder? asyncHost)
    {
        var parameterTypes = new Type[lambda.Parameters.Count + 1];
        parameterTypes[0] = typeof(ScriptClosure);
        for (var i = 0; i < lambda.Parameters.Count; i++)
            parameterTypes[i + 1] = lambda.Parameters[i].Type;

        if (!lambda.IsAsync)
        {
            return new LambdaMethod(
                new DynamicMethod(
                    $"lambda{index}",
                    lambda.ReturnType,
                    parameterTypes,
                    typeof(IlEmitter).Module,
                    skipVisibility: true),
                null);
        }

        // Declared as returning the Task; the IL body returns the unwrapped value and the
        // runtime does the wrapping, exactly as for an async script body.
        var builder = asyncHost!.DefineMethod(
            $"lambda{index}",
            MethodAttributes.Public | MethodAttributes.Static,
            lambda.DeclaredReturnType!,
            parameterTypes);

        builder.SetImplementationFlags(
            MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.Async);

        return new LambdaMethod(null, builder);
    }

    internal static ClosureScope? NearestMaterialized(ClosureScope? scope)
    {
        if (scope is null) return null;
        return scope.IsMaterialized ? scope : scope.MaterializedParent;
    }

    private void EmitBody()
    {
        if (_lambda is not null)
        {
            EmitLambdaBody();
            return;
        }

        foreach (var local in _script.Locals)
            if (!local.IsCaptured)
                _locals[local] = _il.DeclareLocal(local.Type);

        EmitWithReturnEpilogue(_script.Body, _script.ReturnType);
    }

    private void EmitLambdaBody()
    {
        var lambda = _lambda!;

        foreach (var local in lambda.Locals)
            if (!local.IsCaptured && !local.IsLambdaParameter)
                _locals[local] = _il.DeclareLocal(local.Type);

        // Only needed when a nested lambda captured one of this lambda's own parameters.
        if (lambda.OwnScope.IsMaterialized)
        {
            EmitCreateClosure(lambda.OwnScope);

            foreach (var parameter in lambda.Parameters)
            {
                if (!parameter.IsCaptured) continue;

                var typed = EmitSlotStoreTarget(parameter);
                EmitLdarg(parameter.LambdaArgIndex);
                EmitSlotStore(parameter, typed);
            }
        }

        if (lambda.BodyStatement is not null)
        {
            EmitWithReturnEpilogue(lambda.BodyStatement, lambda.ReturnType);
            return;
        }

        // An Action's body is still an expression; its value has to be discarded before ret.
        if (lambda.ReturnType == typeof(void)) EmitAsStatement(lambda.Body!);
        else EmitExpression(lambda.Body!);

        _il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a statement body followed by the single exit point every <c>return</c> leaves to.
    /// A bare <c>ret</c> is invalid inside a protected region, so returns stash their value and
    /// branch here instead.
    /// </summary>
    private void EmitWithReturnEpilogue(BoundStatement body, Type returnType)
    {
        _returnLabel = _il.DefineLabel();
        if (returnType != typeof(void)) _returnValue = _il.DeclareLocal(returnType);

        EmitStatement(body);

        _il.MarkLabel(_returnLabel);
        if (_returnValue is not null) _il.Emit(OpCodes.Ldloc, _returnValue);
        _il.Emit(OpCodes.Ret);
    }

    /// <summary>Pushes the <see cref="ScriptHost"/>: argument 0 in the script body, via the closure in a lambda.</summary>
    private void EmitHost()
    {
        _il.Emit(OpCodes.Ldarg_0);
        if (_lambda is not null)
            _il.Emit(OpCodes.Callvirt, typeof(ScriptClosure).GetProperty(nameof(ScriptClosure.Host))!.GetMethod!);
    }

    // ============================================================ statements

    private void EmitStatement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlock block:
                if (block.Closure is not null) EmitCreateClosure(block.Closure);
                foreach (var child in block.Statements) EmitStatement(child);
                break;

            case BoundNop:
                break;

            case BoundExpressionStatement expression:
                EmitAsStatement(expression.Expression);
                break;

            case BoundLocalDeclaration declaration:
                EmitLocalDeclaration(declaration);
                break;

            case BoundIf conditional:
                EmitIf(conditional);
                break;

            case BoundWhile loop:
                EmitWhile(loop);
                break;

            case BoundDoWhile loop:
                EmitDoWhile(loop);
                break;

            case BoundFor loop:
                EmitFor(loop);
                break;

            case BoundReturn ret:
                EmitReturn(ret);
                break;

            case BoundBreakScope scope:
                EmitBreakScope(scope);
                break;

            case BoundLabel labelled:
                _il.MarkLabel(LabelFor(labelled.Label));
                break;

            case BoundGoto jump:
                // `leave` rather than `br` because the jump may be leaving a protected region,
                // and it is valid outside one too.
                _il.Emit(OpCodes.Leave, LabelFor(jump.Label));
                break;

            case BoundBreak:
                _il.Emit(OpCodes.Leave, _loops.Peek().Break);
                break;

            case BoundContinue:
                _il.Emit(OpCodes.Leave, _loops.Peek().Continue);
                break;

            case BoundThrow thrown:
                EmitExpression(thrown.Expression);
                _il.Emit(OpCodes.Throw);
                break;

            case BoundTry tri:
                EmitTry(tri);
                break;

            default:
                throw new InvalidOperationException($"未处理的语句节点 {statement.GetType().Name}。");
        }
    }

    private void EmitLocalDeclaration(BoundLocalDeclaration declaration)
    {
        var local = declaration.Local;

        if (local.IsCaptured)
        {
            var typed = EmitSlotStoreTarget(local);

            if (declaration.Initializer is null) EmitDefaultValue(local.Type);
            else EmitExpression(declaration.Initializer);

            EmitSlotStore(local, typed);
            return;
        }

        if (declaration.Initializer is null) EmitDefaultValue(local.Type);
        else EmitExpression(declaration.Initializer);

        _il.Emit(OpCodes.Stloc, _locals[local]);
    }

    private void EmitIf(BoundIf statement)
    {
        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        EmitExpression(statement.Condition);
        _il.Emit(OpCodes.Brfalse, elseLabel);

        EmitStatement(statement.Then);
        if (statement.Else is not null) _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(elseLabel);
        if (statement.Else is not null)
        {
            EmitStatement(statement.Else);
            _il.MarkLabel(endLabel);
        }
    }

    /// <summary>
    /// Gives <c>break</c> somewhere to go without introducing a loop. The continue label is
    /// inherited from the enclosing loop, so <c>continue</c> inside a switch still means the
    /// loop — when there is no enclosing loop the binder has already rejected it.
    /// </summary>
    private readonly Dictionary<LabelSymbol, Label> _namedLabels = [];

    private Label LabelFor(LabelSymbol symbol)
    {
        if (_namedLabels.TryGetValue(symbol, out var existing)) return existing;

        var label = _il.DefineLabel();
        _namedLabels[symbol] = label;
        return label;
    }

    private void EmitBreakScope(BoundBreakScope scope)
    {
        var exit = _il.DefineLabel();
        _loops.Push((exit, _loops.Count > 0 ? _loops.Peek().Continue : exit));

        EmitStatement(scope.Body);

        _il.MarkLabel(exit);
        _loops.Pop();
    }

    private void EmitWhile(BoundWhile loop)
    {
        var body = _il.DefineLabel();
        var check = _il.DefineLabel();
        var exit = _il.DefineLabel();

        _loops.Push((exit, check));

        _il.Emit(OpCodes.Br, check);

        _il.MarkLabel(body);
        EmitStatement(loop.Body);

        _il.MarkLabel(check);
        EmitExpression(loop.Condition);
        _il.Emit(OpCodes.Brtrue, body);

        _il.MarkLabel(exit);
        _loops.Pop();
    }

    private void EmitDoWhile(BoundDoWhile loop)
    {
        var body = _il.DefineLabel();
        var check = _il.DefineLabel();
        var exit = _il.DefineLabel();

        _loops.Push((exit, check));

        _il.MarkLabel(body);
        EmitStatement(loop.Body);

        _il.MarkLabel(check);
        EmitExpression(loop.Condition);
        _il.Emit(OpCodes.Brtrue, body);

        _il.MarkLabel(exit);
        _loops.Pop();
    }

    private void EmitFor(BoundFor loop)
    {
        if (loop.Closure is not null) EmitCreateClosure(loop.Closure);

        foreach (var initializer in loop.Initializers) EmitStatement(initializer);

        var body = _il.DefineLabel();
        var next = _il.DefineLabel();
        var check = _il.DefineLabel();
        var exit = _il.DefineLabel();

        _loops.Push((exit, next));

        _il.Emit(OpCodes.Br, check);

        _il.MarkLabel(body);
        EmitStatement(loop.Body);

        _il.MarkLabel(next);
        foreach (var incrementor in loop.Incrementors) EmitStatement(incrementor);

        _il.MarkLabel(check);
        if (loop.Condition is null)
        {
            _il.Emit(OpCodes.Br, body);
        }
        else
        {
            EmitExpression(loop.Condition);
            _il.Emit(OpCodes.Brtrue, body);
        }

        _il.MarkLabel(exit);
        _loops.Pop();
    }

    private void EmitReturn(BoundReturn statement)
    {
        if (statement.Expression is not null && _returnValue is not null)
        {
            EmitExpression(statement.Expression);
            _il.Emit(OpCodes.Stloc, _returnValue);
        }

        _il.Emit(OpCodes.Leave, _returnLabel);
    }

    private void EmitTry(BoundTry statement)
    {
        _il.BeginExceptionBlock();
        EmitStatement(statement.Body);

        foreach (var clause in statement.Catches)
        {
            _il.BeginCatchBlock(clause.ExceptionType);

            if (clause.Variable is null)
            {
                _il.Emit(OpCodes.Pop);
            }
            else if (clause.Variable.IsCaptured)
            {
                var pending = _il.DeclareLocal(clause.Variable.Type);
                _il.Emit(OpCodes.Stloc, pending);
                var typed = EmitSlotStoreTarget(clause.Variable);
                _il.Emit(OpCodes.Ldloc, pending);
                EmitSlotStore(clause.Variable, typed);
            }
            else
            {
                _il.Emit(OpCodes.Stloc, _locals[clause.Variable]);
            }

            EmitStatement(clause.Body);
        }

        if (statement.Finally is not null)
        {
            _il.BeginFinallyBlock();
            EmitStatement(statement.Finally);
        }

        _il.EndExceptionBlock();
    }

    /// <summary>Emits an expression for its side effects, discarding any value it produces.</summary>
    private void EmitAsStatement(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundAssignment assignment:
                EmitAssignment(assignment, leaveValue: false);
                return;

            case BoundSequence sequence:
                foreach (var effect in sequence.SideEffects) EmitAsStatement(effect);
                EmitAsStatement(sequence.Value);
                return;

            case BoundErrorExpression:
                return;

            default:
                EmitExpression(expression);
                if (expression.Type != typeof(void)) _il.Emit(OpCodes.Pop);
                return;
        }
    }
}
