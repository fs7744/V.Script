using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

/// <summary>
/// Translates a <see cref="BoundScript"/> into IL. It targets a bare <see cref="ILGenerator"/>,
/// which is what lets the same emitter serve both carriers: a <c>DynamicMethod</c> for
/// synchronous scripts and a <c>MethodBuilder</c> marked <c>Async</c> for asynchronous ones.
/// </summary>
/// <remarks>
/// By contract this class performs no type analysis. Every conversion, promotion and lifted
/// operation was made explicit by the binder; the code below only chooses opcodes.
/// </remarks>
internal sealed partial class IlEmitter
{
    private readonly ILGenerator _il;
    private readonly BoundScript _script;
    private readonly bool _hasCancellationToken;

    private readonly Dictionary<LocalSymbol, LocalBuilder> _locals = [];
    private LocalBuilder? _state;
    private LocalBuilder? _returnValue;
    private Label _returnLabel;

    private readonly Stack<(Label Break, Label Continue)> _loops = new();

    public IlEmitter(ILGenerator il, BoundScript script, bool hasCancellationToken)
    {
        _il = il;
        _script = script;
        _hasCancellationToken = hasCancellationToken;
    }

    public void Emit()
    {
        foreach (var local in _script.Locals)
            _locals[local] = _il.DeclareLocal(local.Type);

        if (_script.UsesCheckpoints) EmitStateInitialization();

        _returnLabel = _il.DefineLabel();
        if (_script.ReturnType != typeof(void))
            _returnValue = _il.DeclareLocal(_script.ReturnType);

        EmitStatement(_script.Body);

        _il.MarkLabel(_returnLabel);
        if (_returnValue is not null) _il.Emit(OpCodes.Ldloc, _returnValue);
        _il.Emit(OpCodes.Ret);
    }

    private void EmitStateInitialization()
    {
        _state = _il.DeclareLocal(typeof(ScriptState));

        _il.Emit(OpCodes.Ldarg_0); // ScriptHost

        MethodInfo create;
        if (_hasCancellationToken)
        {
            _il.Emit(OpCodes.Ldarg_1);
            create = typeof(ScriptState).GetMethod(
                nameof(ScriptState.Create), [typeof(ScriptHost), typeof(CancellationToken)])!;
        }
        else
        {
            create = typeof(ScriptState).GetMethod(nameof(ScriptState.Create), [typeof(ScriptHost)])!;
        }

        _il.Emit(OpCodes.Call, create);
        _il.Emit(OpCodes.Stloc, _state);
    }

    private void EmitCheckpoint()
    {
        if (_state is null) return;
        _il.Emit(OpCodes.Ldloca, _state);
        _il.Emit(OpCodes.Call, typeof(ScriptState).GetMethod(nameof(ScriptState.Checkpoint))!);
    }

    // ============================================================ statements

    private void EmitStatement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlock block:
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
        var local = _locals[declaration.Local];

        if (declaration.Initializer is null)
        {
            EmitDefaultValue(declaration.Local.Type);
            _il.Emit(OpCodes.Stloc, local);
            return;
        }

        EmitExpression(declaration.Initializer);
        _il.Emit(OpCodes.Stloc, local);
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

    private void EmitWhile(BoundWhile loop)
    {
        var body = _il.DefineLabel();
        var check = _il.DefineLabel();
        var exit = _il.DefineLabel();

        _loops.Push((exit, check));

        _il.Emit(OpCodes.Br, check);

        _il.MarkLabel(body);
        EmitCheckpoint();
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
        EmitCheckpoint();
        EmitStatement(loop.Body);

        _il.MarkLabel(check);
        EmitExpression(loop.Condition);
        _il.Emit(OpCodes.Brtrue, body);

        _il.MarkLabel(exit);
        _loops.Pop();
    }

    private void EmitFor(BoundFor loop)
    {
        foreach (var initializer in loop.Initializers) EmitStatement(initializer);

        var body = _il.DefineLabel();
        var next = _il.DefineLabel();
        var check = _il.DefineLabel();
        var exit = _il.DefineLabel();

        _loops.Push((exit, next));

        _il.Emit(OpCodes.Br, check);

        _il.MarkLabel(body);
        EmitCheckpoint();
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

    /// <summary>
    /// Returns always route through a single exit point. A bare <c>ret</c> is invalid inside a
    /// protected region, so the value is stashed and the method leaves to the common epilogue.
    /// </summary>
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

            if (clause.Variable is not null)
                _il.Emit(OpCodes.Stloc, _locals[clause.Variable]);
            else
                _il.Emit(OpCodes.Pop);

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
