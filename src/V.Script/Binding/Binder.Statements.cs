using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// <c>using</c>, <c>lock</c>, and labelled <c>goto</c>. The first two lower into a
/// <c>try</c>/<c>finally</c> the emitter already knows how to build; only <c>goto</c> needs the
/// emitter to learn anything new.
/// </summary>
internal sealed partial class Binder
{
    private static readonly MethodInfo DisposeMethod =
        typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;

    private static readonly MethodInfo MonitorEnter =
        typeof(System.Threading.Monitor).GetMethod(
            nameof(System.Threading.Monitor.Enter), [typeof(object), typeof(bool).MakeByRefType()])!;

    private static readonly MethodInfo MonitorExit =
        typeof(System.Threading.Monitor).GetMethod(
            nameof(System.Threading.Monitor.Exit), [typeof(object)])!;

    // ============================================================ using

    /// <summary>
    /// <c>using (r) body</c> becomes <c>r; try { body } finally { r?.Dispose(); }</c>. The
    /// declaration form has no body of its own, so the binder gives it the rest of the block.
    /// </summary>
    private BoundStatement BindUsing(UsingStatementSyntax syntax, Func<List<BoundStatement>> bindRest)
    {
        var pos = syntax.Position;
        var saved = PushScope();
        var closure = _closureScope;

        var prologue = new List<BoundStatement>();
        BoundExpression resource;

        if (syntax.Declaration is not null)
        {
            var declaration = BindVariableDeclaration(syntax.Declaration);
            if (declaration is not BoundLocalDeclaration declared)
            {
                PopScope(saved);
                return new BoundNop(pos);
            }

            prologue.Add(declared);
            resource = new BoundLocalAccess(pos, declared.Local);
        }
        else
        {
            var value = BindExpression(syntax.Resource!);
            if (value is BoundErrorExpression)
            {
                PopScope(saved);
                return new BoundNop(pos);
            }

            var temp = MakeTemp(value.Type);
            prologue.Add(new BoundExpressionStatement(pos,
                new BoundAssignment(pos, new BoundLocalAccess(pos, temp), value)));

            resource = new BoundLocalAccess(pos, temp);
        }

        if (!typeof(IDisposable).IsAssignableFrom(resource.Type) && !resource.Type.IsInterface)
        {
            _diagnostics.Report(ErrorCode.CannotConvert, pos,
                $"using 的资源 {TypeResolver.Display(resource.Type)} 不实现 IDisposable。");
        }

        // The declaration form's body is everything that follows it in the block, and that has
        // to be bound here — inside the scope the resource was just declared in.
        var body = syntax.Body is not null
            ? BindStatement(syntax.Body)
            : new BoundBlock(pos, bindRest(), null);

        var cleanup = BuildDisposal(pos, resource);
        var protectedBody = new BoundTry(pos, body, [], cleanup);

        PopScope(saved);

        return new BoundBlock(pos, [.. prologue, protectedBody], closure.IsMaterialized ? closure : null);
    }

    /// <summary>A reference-typed resource is null-checked first, exactly as C# does.</summary>
    private BoundStatement BuildDisposal(SourcePosition pos, BoundExpression resource)
    {
        var target = resource.Type.IsValueType
            ? FindInterfaceImplementation(resource.Type, DisposeMethod) ?? DisposeMethod
            : DisposeMethod;

        var call = new BoundExpressionStatement(pos, new BoundCall(pos, resource, target, []));
        if (resource.Type.IsValueType) return call;

        var notNull = MakeNullTest(resource, testingForNull: false, pos);
        return new BoundIf(pos, notNull, call, null);
    }

    // ============================================================ lock

    /// <summary>
    /// The <c>Monitor.Enter(o, ref taken)</c> shape, which is what the C# compiler emits: the
    /// flag is what makes the release safe if the acquire is interrupted.
    /// </summary>
    private BoundStatement BindLock(LockStatementSyntax syntax)
    {
        var pos = syntax.Position;

        var target = BindExpression(syntax.Target);
        if (target is BoundErrorExpression) return new BoundNop(pos);

        if (target.Type.IsValueType)
        {
            _diagnostics.Report(ErrorCode.CannotConvert, pos,
                $"lock 的对象必须是引用类型，{TypeResolver.Display(target.Type)} 不是。");
            return new BoundNop(pos);
        }

        var subject = MakeTemp(typeof(object));
        var taken = MakeTemp(typeof(bool));

        var storeSubject = new BoundExpressionStatement(pos,
            new BoundAssignment(pos, new BoundLocalAccess(pos, subject),
                Convert(target, typeof(object), pos, explicitCast: false)));

        var storeFlag = new BoundExpressionStatement(pos,
            new BoundAssignment(pos, new BoundLocalAccess(pos, taken),
                new BoundLiteral(pos, typeof(bool), false)));

        var enter = new BoundExpressionStatement(pos,
            new BoundCall(pos, null, MonitorEnter,
                [new BoundLocalAccess(pos, subject), new BoundLocalAddress(pos, taken)]));

        var exit = new BoundIf(pos, new BoundLocalAccess(pos, taken),
            new BoundExpressionStatement(pos,
                new BoundCall(pos, null, MonitorExit, [new BoundLocalAccess(pos, subject)])),
            null);

        var body = new BoundTry(pos, BindStatement(syntax.Body), [], exit);

        return new BoundBlock(pos, [storeSubject, storeFlag, enter, body], null);
    }

    // ============================================================ goto

    private BoundStatement BindLabeled(LabeledStatementSyntax syntax)
    {
        var label = LookupLabel(syntax.Name);

        if (label.IsDefined)
        {
            _diagnostics.Report(ErrorCode.VariableAlreadyDefined, syntax.Position,
                $"标签 '{syntax.Name}' 重复定义。");
        }

        label.IsDefined = true;
        label.HandlerDepth = _protectedDepth;

        return new BoundBlock(syntax.Position,
            [new BoundLabel(syntax.Position, label), BindStatement(syntax.Statement)], null);
    }

    private BoundStatement BindGoto(GotoStatementSyntax syntax)
    {
        if (syntax.Label is null)
        {
            // `goto case` / `goto default` target the enclosing switch's sections.
            return BindGotoCase(syntax);
        }

        var label = LookupLabel(syntax.Label);
        _pendingGotos.Add((label, syntax.Position, _protectedDepth));

        return new BoundGoto(syntax.Position, label);
    }

    private LabelSymbol LookupLabel(string name)
    {
        if (_labels.TryGetValue(name, out var existing)) return existing;

        var label = new LabelSymbol(name);
        _labels[name] = label;
        return label;
    }

    /// <summary>
    /// Every <c>goto</c> is checked once the whole function is bound, because a label may be
    /// written after the jump to it.
    /// </summary>
    private void ValidateGotos()
    {
        foreach (var (label, position, handlerDepth) in _pendingGotos)
        {
            if (!label.IsDefined)
            {
                _diagnostics.Report(ErrorCode.UndefinedName, position,
                    $"找不到标签 '{label.Name}'。");
                continue;
            }

            if (label.HandlerDepth > handlerDepth)
            {
                _diagnostics.Report(ErrorCode.ConstructNotSupported, position,
                    $"不能跳进 try / catch / finally 块中的标签 '{label.Name}'。");
            }
        }

        _pendingGotos.Clear();
    }
}
