using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>Describes one argument of the generated method as the binder sees it.</summary>
internal sealed record ScriptParameter(string Name, Type Type, int IlIndex, bool IsGlobals);

/// <summary>
/// Turns the syntax tree into a <see cref="BoundScript"/>. Every implicit language rule —
/// conversions, numeric promotion, nullable lifting, overload resolution, foreach and
/// compound-assignment lowering — is resolved here, so the emitter only translates.
/// </summary>
internal sealed partial class Binder
{
    private DiagnosticBag _diagnostics;
    private readonly TypeResolver _resolver;
    private readonly IReadOnlyList<ScriptParameter> _parameters;
    private readonly ScriptParameter? _globals;
    private readonly Type _returnType;
    private readonly bool _isAsync;
    private readonly ScriptLimits _limits;

    private readonly List<BoundLambda> _lambdas = [];
    private readonly ClosureScope _rootScope = new(null);
    private readonly Dictionary<ScriptParameter, LocalSymbol> _parameterLocals = [];

    private List<LocalSymbol> _locals = [];
    private Scope _scope;
    private ClosureScope _closureScope;
    private LocalSymbol? _globalsLocal;
    private int _functionDepth;
    private int _loopDepth;
    private int _handlerDepth;
    private int _tempCounter;
    private bool _sawReturn;

    public Binder(
        DiagnosticBag diagnostics,
        TypeResolver resolver,
        IReadOnlyList<ScriptParameter> parameters,
        Type returnType,
        bool isAsync,
        ScriptLimits limits)
    {
        _diagnostics = diagnostics;
        _resolver = resolver;
        _parameters = parameters;
        _globals = parameters.FirstOrDefault(p => p.IsGlobals);
        _returnType = returnType;
        _isAsync = isAsync;
        _limits = limits;
        _scope = new Scope(null);
        _closureScope = _rootScope;
    }

    public BoundScript BindScript(CompilationUnitSyntax unit)
    {
        var statements = new List<BoundStatement>();

        // Copy the method's arguments into locals up front. An argument index means nothing
        // inside a lambda's own method, so making parameters ordinary locals lets a lambda
        // capture the globals object exactly the way it captures anything else.
        foreach (var parameter in _parameters)
        {
            var local = new LocalSymbol(parameter.Name, parameter.Type, parameter.IsGlobals);
            Register(local);
            _scope.TryDeclare(local);
            _parameterLocals[parameter] = local;

            statements.Add(new BoundLocalDeclaration(unit.Position, local,
                new BoundParameterAccess(unit.Position, parameter.Type, parameter.IlIndex)));
        }

        if (_globals is not null) _globalsLocal = _parameterLocals[_globals];

        for (var i = 0; i < unit.Statements.Count; i++)
        {
            var isLast = i == unit.Statements.Count - 1;
            statements.Add(BindTopLevelStatement(unit.Statements[i], isLast));
        }

        var body = new BoundBlock(unit.Position, statements,
            _rootScope.IsMaterialized ? _rootScope : null);

        if (_returnType != typeof(void) && !AlwaysReturns(body))
        {
            _diagnostics.Report(ErrorCode.NotAllCodePathsReturn, unit.Position,
                $"脚本必须返回 {TypeResolver.Display(_returnType)}，但存在没有 return 的执行路径。");
        }

        AssignSlots(_locals);
        foreach (var lambda in _lambdas) AssignSlots(lambda.Locals);

        return new BoundScript(
            body,
            _returnType,
            _locals,
            _isAsync,
            _limits.NeedsCheckpoints,
            _rootScope,
            _lambdas);
    }

    /// <summary>Captured variables live in a closure, so only the rest need an IL local slot.</summary>
    private static void AssignSlots(IReadOnlyList<LocalSymbol> locals)
    {
        var slot = 0;
        foreach (var local in locals)
            if (!local.IsCaptured)
                local.Slot = slot++;
    }

    /// <summary>Records a new variable against the function and scope currently being bound.</summary>
    private void Register(LocalSymbol local)
    {
        local.FunctionDepth = _functionDepth;
        local.DeclaringScope = _closureScope;
        _locals.Add(local);
    }

    /// <summary>
    /// A trailing bare expression acts as the script's result, so <c>"a + b"</c> works without
    /// an explicit <c>return</c>. Only applies when nothing has returned yet.
    /// </summary>
    private BoundStatement BindTopLevelStatement(StatementSyntax syntax, bool isLast)
    {
        if (isLast && !_sawReturn && _returnType != typeof(void) &&
            syntax is ExpressionStatementSyntax expressionStatement)
        {
            var value = BindExpression(expressionStatement.Expression);
            if (value.Type != typeof(void))
            {
                var converted = Convert(value, _returnType, expressionStatement.Position, explicitCast: false);
                return new BoundReturn(syntax.Position, converted);
            }
        }

        return BindStatement(syntax);
    }

    // ============================================================ statements

    private BoundStatement BindStatement(StatementSyntax syntax) => syntax switch
    {
        BlockStatementSyntax block => BindBlock(block),
        ExpressionStatementSyntax expression => BindExpressionStatement(expression),
        VariableDeclarationSyntax declaration => BindVariableDeclaration(declaration),
        IfStatementSyntax conditional => BindIf(conditional),
        WhileStatementSyntax loop => BindWhile(loop),
        DoWhileStatementSyntax loop => BindDoWhile(loop),
        ForStatementSyntax loop => BindFor(loop),
        ForEachStatementSyntax loop => BindForEach(loop),
        ReturnStatementSyntax ret => BindReturn(ret),
        BreakStatementSyntax brk => BindBreak(brk),
        ContinueStatementSyntax cont => BindContinue(cont),
        ThrowStatementSyntax thr => BindThrow(thr),
        TryStatementSyntax tri => BindTry(tri),
        ErrorStatementSyntax => new BoundNop(syntax.Position),
        _ => new BoundNop(syntax.Position),
    };

    private readonly record struct ScopeState(Scope Scope, ClosureScope Closure);

    /// <summary>
    /// Enters a nested lexical scope. Every scope gets a closure object, but only the ones that
    /// actually capture something are instantiated at run time.
    /// </summary>
    private ScopeState PushScope()
    {
        var saved = new ScopeState(_scope, _closureScope);
        _scope = new Scope(saved.Scope);
        _closureScope = new ClosureScope(saved.Closure);
        return saved;
    }

    private void PopScope(ScopeState saved)
    {
        _scope = saved.Scope;
        _closureScope = saved.Closure;
    }

    private BoundStatement BindBlock(BlockStatementSyntax syntax)
    {
        var saved = PushScope();
        var closure = _closureScope;

        var statements = syntax.Statements.Select(BindStatement).ToArray();

        PopScope(saved);
        return new BoundBlock(syntax.Position, statements, closure.IsMaterialized ? closure : null);
    }

    private BoundStatement BindExpressionStatement(ExpressionStatementSyntax syntax)
    {
        // ++/-- in statement position lowers to a plain compound assignment, which avoids
        // the temp needed to produce the old value.
        var expression = syntax.Expression switch
        {
            PostfixExpressionSyntax postfix =>
                BindIncrementAsStatement(postfix.Operand, postfix.Operator, syntax.Position),
            UnaryExpressionSyntax { Operator: SyntaxKind.PlusPlus or SyntaxKind.MinusMinus } prefix =>
                BindIncrementAsStatement(prefix.Operand, prefix.Operator, syntax.Position),
            _ => BindExpression(syntax.Expression),
        };

        return new BoundExpressionStatement(syntax.Position, expression);
    }

    private BoundStatement BindVariableDeclaration(VariableDeclarationSyntax syntax)
    {
        Type? declaredType = null;
        if (syntax.Type is not null)
        {
            declaredType = ResolveType(syntax.Type);
            if (declaredType is null) return new BoundNop(syntax.Position);
        }

        BoundExpression? initializer = null;
        if (syntax.Initializer is not null)
        {
            initializer = BindExpression(syntax.Initializer);

            if (declaredType is null)
            {
                if (initializer.Type == Conversions.LambdaType)
                {
                    _diagnostics.Report(ErrorCode.CannotInferType, syntax.Position,
                        $"无法从 lambda 推断 'var {syntax.Name}' 的类型。请写出委托类型，" +
                        $"例如 Func<int, int> {syntax.Name} = ...。");
                    declaredType = typeof(object);
                }
                else if (initializer.Type == Conversions.NullLiteralType || initializer.Type == typeof(void))
                {
                    _diagnostics.Report(ErrorCode.CannotInferType, syntax.Position,
                        $"无法从初始值推断 'var {syntax.Name}' 的类型。");
                    declaredType = typeof(object);
                }
                else
                {
                    declaredType = initializer.Type;
                }
            }

            initializer = Convert(initializer, declaredType, syntax.Position, explicitCast: false);
        }
        else if (declaredType is null)
        {
            _diagnostics.Report(ErrorCode.CannotInferType, syntax.Position,
                $"'var {syntax.Name}' 必须有初始值。");
            declaredType = typeof(object);
        }

        var local = new LocalSymbol(syntax.Name, declaredType);
        if (!DeclareLocal(local, syntax.Position)) return new BoundNop(syntax.Position);

        return new BoundLocalDeclaration(syntax.Position, local, initializer);
    }

    private bool DeclareLocal(LocalSymbol local, SourcePosition position)
    {
        if (_parameters.Any(p => p.Name == local.Name && !p.IsGlobals))
        {
            _diagnostics.Report(ErrorCode.VariableAlreadyDefined, position,
                $"'{local.Name}' 与脚本参数同名。");
            return false;
        }

        if (!_scope.TryDeclare(local))
        {
            _diagnostics.Report(ErrorCode.VariableAlreadyDefined, position,
                $"当前作用域中已存在名为 '{local.Name}' 的变量。");
            return false;
        }

        Register(local);
        return true;
    }

    private BoundStatement BindIf(IfStatementSyntax syntax)
    {
        var condition = BindCondition(syntax.Condition);
        var then = BindStatement(syntax.Then);
        var otherwise = syntax.Else is null ? null : BindStatement(syntax.Else);
        return new BoundIf(syntax.Position, condition, then, otherwise);
    }

    private BoundStatement BindWhile(WhileStatementSyntax syntax)
    {
        var condition = BindCondition(syntax.Condition);
        _loopDepth++;
        var body = BindStatement(syntax.Body);
        _loopDepth--;
        return new BoundWhile(syntax.Position, condition, body);
    }

    private BoundStatement BindDoWhile(DoWhileStatementSyntax syntax)
    {
        _loopDepth++;
        var body = BindStatement(syntax.Body);
        _loopDepth--;
        var condition = BindCondition(syntax.Condition);
        return new BoundDoWhile(syntax.Position, body, condition);
    }

    private BoundStatement BindFor(ForStatementSyntax syntax)
    {
        var saved = PushScope();
        var closure = _closureScope;

        var initializers = syntax.Initializers.Select(BindStatement).ToArray();
        var condition = syntax.Condition is null ? null : BindCondition(syntax.Condition);

        var incrementors = syntax.Incrementors
            .Select(e => (BoundStatement)new BoundExpressionStatement(e.Position, e switch
            {
                PostfixExpressionSyntax postfix =>
                    BindIncrementAsStatement(postfix.Operand, postfix.Operator, e.Position),
                UnaryExpressionSyntax { Operator: SyntaxKind.PlusPlus or SyntaxKind.MinusMinus } prefix =>
                    BindIncrementAsStatement(prefix.Operand, prefix.Operator, e.Position),
                _ => BindExpression(e),
            }))
            .ToArray();

        _loopDepth++;
        var body = BindStatement(syntax.Body);
        _loopDepth--;

        PopScope(saved);
        return new BoundFor(syntax.Position, initializers, condition, incrementors, body,
            closure.IsMaterialized ? closure : null);
    }

    private BoundStatement BindReturn(ReturnStatementSyntax syntax)
    {
        _sawReturn = true;

        if (_returnType == typeof(void))
        {
            if (syntax.Expression is not null)
            {
                var ignored = BindExpression(syntax.Expression);
                _diagnostics.Report(ErrorCode.ReturnTypeMismatch, syntax.Position,
                    "脚本没有返回值，'return' 不能带表达式。");
                _ = ignored;
            }
            return new BoundReturn(syntax.Position, null);
        }

        if (syntax.Expression is null)
        {
            _diagnostics.Report(ErrorCode.ReturnTypeMismatch, syntax.Position,
                $"脚本必须返回 {TypeResolver.Display(_returnType)}。");
            return new BoundReturn(syntax.Position, new BoundDefault(syntax.Position, _returnType));
        }

        var value = BindExpression(syntax.Expression);
        return new BoundReturn(syntax.Position, Convert(value, _returnType, syntax.Position, explicitCast: false));
    }

    private BoundStatement BindBreak(BreakStatementSyntax syntax)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(ErrorCode.BreakOutsideLoop, syntax.Position, "'break' 只能出现在循环中。");
            return new BoundNop(syntax.Position);
        }
        return new BoundBreak(syntax.Position);
    }

    private BoundStatement BindContinue(ContinueStatementSyntax syntax)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(ErrorCode.ContinueOutsideLoop, syntax.Position, "'continue' 只能出现在循环中。");
            return new BoundNop(syntax.Position);
        }
        return new BoundContinue(syntax.Position);
    }

    private BoundStatement BindThrow(ThrowStatementSyntax syntax)
    {
        var value = BindExpression(syntax.Expression);
        if (value.Type != typeof(object) && !typeof(Exception).IsAssignableFrom(value.Type))
        {
            _diagnostics.Report(ErrorCode.CannotConvert, syntax.Position,
                $"只能抛出 Exception，实际为 {TypeResolver.Display(value.Type)}。");
        }
        else if (value.Type != typeof(Exception))
        {
            value = Convert(value, typeof(Exception), syntax.Position, explicitCast: true);
        }

        return new BoundThrow(syntax.Position, value);
    }

    private BoundStatement BindTry(TryStatementSyntax syntax)
    {
        var body = BindStatement(syntax.Body);

        var catches = new List<BoundCatchClause>();
        foreach (var clause in syntax.Catches)
        {
            var exceptionType = clause.ExceptionType is null
                ? typeof(Exception)
                : ResolveType(clause.ExceptionType) ?? typeof(Exception);

            if (!typeof(Exception).IsAssignableFrom(exceptionType))
            {
                _diagnostics.Report(ErrorCode.CannotConvert, clause.Position,
                    $"catch 的类型必须派生自 Exception，实际为 {TypeResolver.Display(exceptionType)}。");
                exceptionType = typeof(Exception);
            }

            var saved = PushScope();
            var clauseClosure = _closureScope;

            LocalSymbol? variable = null;
            if (clause.VariableName is not null)
            {
                variable = new LocalSymbol(clause.VariableName, exceptionType);
                DeclareLocal(variable, clause.Position);
            }

            _handlerDepth++;
            var clauseBody = BindStatement(clause.Body);
            _handlerDepth--;

            PopScope(saved);

            if (clauseClosure.IsMaterialized)
                clauseBody = new BoundBlock(clause.Position, [clauseBody], clauseClosure);

            catches.Add(new BoundCatchClause(clause.Position, exceptionType, variable, clauseBody));
        }

        BoundStatement? finallyBlock = null;
        if (syntax.Finally is not null)
        {
            _handlerDepth++;
            finallyBlock = BindStatement(syntax.Finally);
            _handlerDepth--;
        }

        return new BoundTry(syntax.Position, body, catches, finallyBlock);
    }

    // ============================================================ foreach lowering

    /// <summary>
    /// Arrays lower to an indexed <c>for</c> loop; everything else to the enumerator pattern
    /// wrapped in try/finally. Producing existing bound nodes keeps the emitter free of
    /// foreach-specific knowledge.
    /// </summary>
    private BoundStatement BindForEach(ForEachStatementSyntax syntax)
    {
        var collection = BindExpression(syntax.Collection);

        if (collection.Type == typeof(void) || collection is BoundErrorExpression)
            return new BoundNop(syntax.Position);

        if (collection.Type.IsArray && collection.Type.GetArrayRank() == 1)
            return BindForEachOverArray(syntax, collection);

        return BindForEachOverEnumerable(syntax, collection);
    }

    private BoundStatement BindForEachOverArray(ForEachStatementSyntax syntax, BoundExpression collection)
    {
        var elementType = collection.Type.GetElementType()!;
        var declaredType = ResolveElementType(syntax, elementType);

        var saved = PushScope();
        var loopClosure = _closureScope;

        var arrayLocal = MakeTemp(collection.Type);
        var indexLocal = MakeTemp(typeof(int));

        var pos = syntax.Position;

        BoundStatement[] initializers =
        [
            new BoundLocalDeclaration(pos, arrayLocal, collection),
            new BoundLocalDeclaration(pos, indexLocal, new BoundLiteral(pos, typeof(int), 0)),
        ];

        var lengthProperty = typeof(Array).GetProperty(nameof(Array.Length))!;
        var condition = new BoundBinary(
            pos, typeof(bool), BoundBinaryKind.Less,
            new BoundLocalAccess(pos, indexLocal),
            new BoundPropertyAccess(pos, new BoundLocalAccess(pos, arrayLocal), lengthProperty),
            IsLifted: false, Method: null);

        BoundStatement[] incrementors =
        [
            new BoundExpressionStatement(pos, new BoundAssignment(
                pos,
                new BoundLocalAccess(pos, indexLocal),
                new BoundBinary(pos, typeof(int), BoundBinaryKind.Add,
                    new BoundLocalAccess(pos, indexLocal),
                    new BoundLiteral(pos, typeof(int), 1),
                    IsLifted: false, Method: null))),
        ];

        var element = new BoundArrayAccess(pos, elementType,
            new BoundLocalAccess(pos, arrayLocal),
            new BoundLocalAccess(pos, indexLocal));

        // The iteration variable belongs to a scope entered once per iteration, so capturing it
        // in a lambda gives a fresh value each time round, as C# has done since version 5.
        var bodySaved = PushScope();
        var bodyClosure = _closureScope;

        var item = new LocalSymbol(syntax.Name, declaredType);
        DeclareLocal(item, syntax.Position);

        _loopDepth++;
        var innerBody = BindStatement(syntax.Body);
        _loopDepth--;

        PopScope(bodySaved);

        var body = new BoundBlock(pos,
        [
            new BoundLocalDeclaration(pos, item, Convert(element, declaredType, pos, explicitCast: true)),
            innerBody,
        ], bodyClosure.IsMaterialized ? bodyClosure : null);

        PopScope(saved);
        return new BoundFor(pos, initializers, condition, incrementors, body,
            loopClosure.IsMaterialized ? loopClosure : null);
    }

    private BoundStatement BindForEachOverEnumerable(ForEachStatementSyntax syntax, BoundExpression collection)
    {
        var pos = syntax.Position;

        var getEnumerator = FindGetEnumerator(collection.Type);
        if (getEnumerator is null)
        {
            _diagnostics.Report(ErrorCode.NotEnumerable, pos,
                $"{TypeResolver.Display(collection.Type)} 不是可枚举类型：找不到 GetEnumerator()。");
            return new BoundNop(pos);
        }

        var enumeratorType = getEnumerator.ReturnType;
        var currentProperty = FindCurrentProperty(enumeratorType);
        var moveNext = FindMoveNext(enumeratorType);

        if (currentProperty is null || moveNext is null)
        {
            _diagnostics.Report(ErrorCode.NotEnumerable, pos,
                $"{TypeResolver.Display(enumeratorType)} 缺少 Current 或 MoveNext()。");
            return new BoundNop(pos);
        }

        var elementType = currentProperty.PropertyType;
        var declaredType = ResolveElementType(syntax, elementType);

        var saved = PushScope();
        var loopClosure = _closureScope;

        var enumeratorLocal = MakeTemp(enumeratorType);
        var current = new BoundPropertyAccess(pos, new BoundLocalAccess(pos, enumeratorLocal), currentProperty);

        // Fresh scope per iteration, so a captured iteration variable is not shared.
        var bodySaved = PushScope();
        var bodyClosure = _closureScope;

        var item = new LocalSymbol(syntax.Name, declaredType);
        DeclareLocal(item, pos);

        _loopDepth++;
        var innerBody = BindStatement(syntax.Body);
        _loopDepth--;

        PopScope(bodySaved);

        var loopBody = new BoundBlock(pos,
        [
            new BoundLocalDeclaration(pos, item, Convert(current, declaredType, pos, explicitCast: true)),
            innerBody,
        ], bodyClosure.IsMaterialized ? bodyClosure : null);

        var condition = new BoundCall(pos, new BoundLocalAccess(pos, enumeratorLocal), moveNext, []);
        var loop = new BoundWhile(pos, condition, loopBody);

        var disposal = BuildEnumeratorDisposal(pos, enumeratorLocal);

        PopScope(saved);

        BoundStatement inner = disposal is null
            ? loop
            : new BoundTry(pos, loop, [], disposal);

        return new BoundBlock(pos,
        [
            new BoundLocalDeclaration(pos, enumeratorLocal,
                new BoundCall(pos, collection, getEnumerator, [])),
            inner,
        ], loopClosure.IsMaterialized ? loopClosure : null);
    }

    private BoundStatement? BuildEnumeratorDisposal(SourcePosition pos, LocalSymbol enumerator)
    {
        var disposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;
        var enumeratorType = enumerator.Type;

        if (typeof(IDisposable).IsAssignableFrom(enumeratorType))
        {
            var receiver = enumeratorType.IsValueType
                ? (BoundExpression)new BoundLocalAccess(pos, enumerator)
                : new BoundLocalAccess(pos, enumerator);

            var target = enumeratorType.IsValueType
                ? FindInterfaceImplementation(enumeratorType, disposeMethod) ?? disposeMethod
                : disposeMethod;

            return new BoundExpressionStatement(pos, new BoundCall(pos, receiver, target, []));
        }

        if (enumeratorType.IsValueType || enumeratorType.IsSealed)
            return null; // statically known not to be disposable

        // Reference enumerator whose static type is not IDisposable: test at run time.
        var disposable = MakeTemp(typeof(IDisposable));
        return new BoundBlock(pos,
        [
            new BoundLocalDeclaration(pos, disposable,
                new BoundAsType(pos, typeof(IDisposable), new BoundLocalAccess(pos, enumerator))),
            new BoundIf(pos,
                new BoundBinary(pos, typeof(bool), BoundBinaryKind.NotEqual,
                    new BoundLocalAccess(pos, disposable),
                    new BoundLiteral(pos, typeof(IDisposable), null),
                    IsLifted: false, Method: null),
                new BoundExpressionStatement(pos,
                    new BoundCall(pos, new BoundLocalAccess(pos, disposable), disposeMethod, [])),
                null),
        ]);
    }

    private Type ResolveElementType(ForEachStatementSyntax syntax, Type inferred)
    {
        if (syntax.ElementType is null) return inferred;

        var declared = ResolveType(syntax.ElementType);
        if (declared is null) return inferred;

        if (!Conversions.Classify(inferred, declared).Exists)
        {
            _diagnostics.Report(ErrorCode.CannotConvert, syntax.Position,
                $"无法将元素类型 {TypeResolver.Display(inferred)} 转换为 {TypeResolver.Display(declared)}。");
            return inferred;
        }

        return declared;
    }

    private static MethodInfo? FindGetEnumerator(Type type)
    {
        var duck = type.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (duck is not null && duck.ReturnType != typeof(void)) return duck;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetMethod("GetEnumerator");
        }

        if (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetMethod("GetEnumerator");

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            return typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator");

        return null;
    }

    private static PropertyInfo? FindCurrentProperty(Type enumeratorType) =>
        enumeratorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance)
        ?? enumeratorType.GetInterfaces()
            .Select(i => i.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance))
            .FirstOrDefault(p => p is not null);

    private static MethodInfo? FindMoveNext(Type enumeratorType)
    {
        var direct = enumeratorType.GetMethod("MoveNext",
            BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (direct is not null && direct.ReturnType == typeof(bool)) return direct;

        return typeof(System.Collections.IEnumerator).GetMethod("MoveNext");
    }

    private static MethodInfo? FindInterfaceImplementation(Type type, MethodInfo interfaceMethod)
    {
        if (!interfaceMethod.DeclaringType!.IsAssignableFrom(type)) return null;

        var map = type.GetInterfaceMap(interfaceMethod.DeclaringType);
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
            if (map.InterfaceMethods[i] == interfaceMethod)
                return map.TargetMethods[i];

        return null;
    }

    // ============================================================ helpers

    private LocalSymbol MakeTemp(Type type)
    {
        var local = new LocalSymbol($"<temp{_tempCounter++}>", type, isCompilerGenerated: true);
        _locals.Add(local);
        return local;
    }

    private Type? ResolveType(TypeSyntax syntax)
    {
        var type = _resolver.Resolve(syntax);
        if (type is null)
        {
            _diagnostics.Report(ErrorCode.UnknownType, syntax.Position,
                $"找不到类型 '{syntax.DisplayName}'。请检查 AddReferences / AddImports 配置。");
        }
        return type;
    }

    /// <summary>Conservative definite-return analysis; only shapes that clearly always return count.</summary>
    private static bool AlwaysReturns(BoundStatement statement) => statement switch
    {
        BoundReturn => true,
        BoundThrow => true,
        BoundBlock block => block.Statements.Any(AlwaysReturns),
        BoundIf { Else: not null } conditional =>
            AlwaysReturns(conditional.Then) && AlwaysReturns(conditional.Else),
        BoundTry tri =>
            (AlwaysReturns(tri.Body) && tri.Catches.All(c => AlwaysReturns(c.Body)))
            || (tri.Finally is not null && AlwaysReturns(tri.Finally)),
        BoundWhile { Condition: BoundLiteral { Value: true } } loop => !ContainsBreak(loop.Body),
        BoundFor { Condition: null } loop => !ContainsBreak(loop.Body),
        _ => false,
    };

    private static bool ContainsBreak(BoundStatement statement) => statement switch
    {
        BoundBreak => true,
        BoundBlock block => block.Statements.Any(ContainsBreak),
        BoundIf conditional => ContainsBreak(conditional.Then) ||
                               (conditional.Else is not null && ContainsBreak(conditional.Else)),
        BoundTry tri => ContainsBreak(tri.Body) || tri.Catches.Any(c => ContainsBreak(c.Body)) ||
                        (tri.Finally is not null && ContainsBreak(tri.Finally)),
        _ => false, // nested loops capture their own break
    };
}
