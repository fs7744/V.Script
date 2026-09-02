using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

internal sealed partial class Binder
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.Static;

    /// <summary>Non-null while probing a block lambda; return statements append their type here.</summary>
    private List<Type>? _returnTypeProbe;

    private ExpressionSyntax? _substituteFor;
    private BoundExpression? _substitute;
    private HashSet<ExpressionSyntax>? _chainNodes;

    // ============================================================ entry points

    private BoundExpression BindExpression(ExpressionSyntax syntax)
    {
        if (_substituteFor is not null && ReferenceEquals(syntax, _substituteFor))
            return _substitute!;

        if (_chainNodes is not null && _chainNodes.Contains(syntax))
            return BindExpressionCore(syntax);

        var chain = FindConditionalChain(syntax);
        return chain is null ? BindExpressionCore(syntax) : BindConditionalChain(syntax, chain);
    }

    private BoundExpression BindCondition(ExpressionSyntax syntax)
    {
        var value = BindExpression(syntax);
        if (value.Type == typeof(bool)) return value;

        if (Conversions.Classify(value.Type, typeof(bool)).IsImplicit)
            return Convert(value, typeof(bool), syntax.Position, explicitCast: false);

        if (value is not BoundErrorExpression)
        {
            _diagnostics.Report(ErrorCode.ConditionMustBeBool, syntax.Position,
                $"条件表达式必须是 bool，实际为 {TypeResolver.Display(value.Type)}。");
        }
        return new BoundLiteral(syntax.Position, typeof(bool), false);
    }

    private BoundExpression BindExpressionCore(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax literal => BindLiteral(literal),
        NameExpressionSyntax name => BindName(name),
        ParenthesizedExpressionSyntax paren => BindExpression(paren.Inner),
        UnaryExpressionSyntax unary => BindUnary(unary),
        PostfixExpressionSyntax postfix => BindIncrementAsValue(postfix.Operand, postfix.Operator, postfix.Position, prefix: false),
        BinaryExpressionSyntax binary => BindBinary(binary),
        AssignmentExpressionSyntax assignment => BindAssignment(assignment),
        ConditionalExpressionSyntax conditional => BindConditionalExpression(conditional),
        MemberAccessExpressionSyntax member => BindMemberAccess(member),
        InvocationExpressionSyntax invocation => BindInvocation(invocation),
        IndexExpressionSyntax index => BindIndex(index),
        CastExpressionSyntax cast => BindCast(cast),
        AwaitExpressionSyntax await => BindAwait(await),
        IsExpressionSyntax isExpression => BindIs(isExpression),
        SwitchExpressionSyntax switchExpression => BindSwitchExpression(switchExpression),
        AsExpressionSyntax asExpression => BindAs(asExpression),
        TypeofExpressionSyntax typeofExpression => BindTypeof(typeofExpression),
        ObjectCreationExpressionSyntax creation => BindObjectCreation(creation),
        TupleExpressionSyntax tuple => BindTupleExpression(tuple),
        CheckedExpressionSyntax region => BindCheckedExpression(region),
        FromEndExpressionSyntax fromEnd => BindFromEnd(fromEnd),
        RangeExpressionSyntax range => BindRange(range),
        WithExpressionSyntax with => BindWith(with),
        ArrayCreationExpressionSyntax array => BindArrayCreation(array),
        CollectionExpressionSyntax collection => new BoundUnboundCollection(collection.Position, collection),
        DefaultExpressionSyntax defaultValue => BindDefault(defaultValue),
        ThrowExpressionSyntax thrown => BindThrowExpression(thrown),
        NameOfExpressionSyntax nameOf => BindNameOf(nameOf),
        InterpolatedStringExpressionSyntax interpolated => BindInterpolatedString(interpolated),
        LambdaExpressionSyntax lambda => new BoundUnboundLambda(lambda.Position, lambda),
        ErrorExpressionSyntax => new BoundErrorExpression(syntax.Position),
        _ => Fail(syntax.Position, ErrorCode.ConstructNotSupported, "不支持的表达式。"),
    };

    /// <summary>
    /// Binds a lambda now that a target delegate type is known. The body is bound in its own
    /// function frame, so any reference it makes to an enclosing variable captures that variable.
    /// </summary>
    internal BoundExpression BindLambda(LambdaExpressionSyntax syntax, Type delegateType)
    {
        var invoke = Conversions.GetInvokeMethod(delegateType);
        if (invoke is null)
        {
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                $"lambda 只能转换为委托类型，{TypeResolver.Display(delegateType)} 不是委托。");
        }

        var parameters = invoke.GetParameters();

        if (parameters.Length != syntax.Parameters.Count)
        {
            return Fail(syntax.Position, ErrorCode.WrongArgumentCount,
                $"lambda 有 {syntax.Parameters.Count} 个参数，但 " +
                $"{TypeResolver.Display(delegateType)} 需要 {parameters.Length} 个。");
        }

        if (parameters.Any(p => p.ParameterType.IsByRef))
        {
            return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                "不支持带 ref / out 参数的委托。");
        }

        var savedScope = _scope;
        var savedClosure = _closureScope;
        var savedLocals = _locals;
        var savedLoopDepth = _loopDepth;
        var savedSwitchDepth = _switchDepth;
        var savedHandlerDepth = _handlerDepth;
        var savedProtectedDepth = _protectedDepth;
        var savedLabels = _labels;
        var savedGotos = _pendingGotos;
        var savedAsyncContext = _isAsyncContext;
        var savedStaticBoundary = _staticBoundary;
        var savedReturnType = _returnType;
        var savedSawReturn = _sawReturn;
        var savedProbe = _returnTypeProbe;

        _functionDepth++;
        _scope = new Scope(savedScope);
        _closureScope = new ClosureScope(savedClosure);
        _locals = [];
        _loopDepth = 0;
        _switchDepth = 0;
        _handlerDepth = 0;
        _protectedDepth = 0;
        _labels = [];
        _pendingGotos = [];
        _isAsyncContext = syntax.IsAsync;

        // An async lambda's body produces the awaited value; the Task around it is the runtime's
        // doing, exactly as for an async script body.
        var declaredReturnType = invoke.ReturnType;
        var bodyReturnType = syntax.IsAsync
            ? AwaitHelpers.UnwrapTaskType(declaredReturnType)
            : declaredReturnType;

        if (bodyReturnType is null)
        {
            _isAsyncContext = savedAsyncContext;
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                $"async lambda 必须返回 Task 或 Task<T>，{TypeResolver.Display(delegateType)} 返回 " +
                $"{TypeResolver.Display(declaredReturnType)}。");
        }

        _returnType = bodyReturnType;
        _sawReturn = false;
        _returnTypeProbe = null;

        var ownScope = _closureScope;
        var lambdaParameters = new List<LocalSymbol>(parameters.Length);

        for (var i = 0; i < parameters.Length; i++)
        {
            var written = syntax.Parameters[i];

            // A written type has to be the delegate's type exactly, as in C# — it is a check,
            // not a conversion.
            if (written.Type is not null)
            {
                var declared = ResolveType(written.Type);
                if (declared is not null && declared != parameters[i].ParameterType)
                {
                    _diagnostics.Report(ErrorCode.CannotConvert, written.Position,
                        $"lambda 参数 '{written.Name}' 声明为 {TypeResolver.Display(declared)}，但 " +
                        $"{TypeResolver.Display(delegateType)} 要求 {TypeResolver.Display(parameters[i].ParameterType)}。");
                }
            }

            // Argument 0 of the generated lambda method is always its closure.
            var parameter = new LocalSymbol(written.Name, parameters[i].ParameterType)
            {
                LambdaArgIndex = i + 1,
            };

            Register(parameter);

            if (!_scope.TryDeclare(parameter))
            {
                _diagnostics.Report(ErrorCode.VariableAlreadyDefined, syntax.Position,
                    $"lambda 参数 '{parameter.Name}' 重复。");
            }

            lambdaParameters.Add(parameter);
        }

        var returnType = bodyReturnType;

        BoundExpression? body = null;
        BoundStatement? bodyStatement = null;

        if (syntax.Body is ExpressionSyntax expressionBody)
        {
            body = BindExpression(expressionBody);
            if (returnType != typeof(void))
                body = Convert(body, returnType, syntax.Position, explicitCast: false);
        }
        else
        {
            bodyStatement = BindStatement((StatementSyntax)syntax.Body);

            if (returnType != typeof(void) && !AlwaysReturns(bodyStatement))
            {
                _diagnostics.Report(ErrorCode.NotAllCodePathsReturn, syntax.Position,
                    $"lambda 必须返回 {TypeResolver.Display(returnType)}，但存在没有 return 的执行路径。");
            }
        }

        ValidateGotos();

        var lambdaLocals = _locals;

        _functionDepth--;
        _scope = savedScope;
        _closureScope = savedClosure;
        _locals = savedLocals;
        _loopDepth = savedLoopDepth;
        _switchDepth = savedSwitchDepth;
        _handlerDepth = savedHandlerDepth;
        _protectedDepth = savedProtectedDepth;
        _labels = savedLabels;
        _pendingGotos = savedGotos;
        _isAsyncContext = savedAsyncContext;
        _staticBoundary = savedStaticBoundary;
        _returnType = savedReturnType;
        _sawReturn = savedSawReturn;
        _returnTypeProbe = savedProbe;

        var lambda = new BoundLambda(
            syntax.Position, delegateType, lambdaParameters, lambdaLocals,
            body, returnType, ownScope, savedClosure, bodyStatement,
            syntax.IsAsync, declaredReturnType);

        _lambdas.Add(lambda);
        return lambda;
    }

    private BoundExpression Fail(SourcePosition position, ErrorCode code, string message)
    {
        _diagnostics.Report(code, position, message);
        return new BoundErrorExpression(position);
    }

    // ============================================================ null-conditional chains

    private static ExpressionSyntax? ReceiverOf(ExpressionSyntax syntax) => syntax switch
    {
        MemberAccessExpressionSyntax member => member.Target,
        IndexExpressionSyntax index => index.Target,
        InvocationExpressionSyntax invocation => invocation.Target,
        _ => null,
    };

    private static bool IsConditionalNode(ExpressionSyntax syntax) => syntax is
        MemberAccessExpressionSyntax { IsNullConditional: true } or
        IndexExpressionSyntax { IsNullConditional: true };

    /// <summary>
    /// Finds the outermost <c>?.</c> in an access chain, together with the nodes above it.
    /// C# short-circuits everything to the right of a <c>?.</c>, so the chain is bound as one
    /// unit from that point outwards; the receiver below it goes back through
    /// <see cref="BindExpression"/>, which handles any further <c>?.</c> the same way. Picking
    /// the outermost rather than the deepest is what makes <c>a?.b?.c</c> short-circuit twice.
    /// </summary>
    private ExpressionSyntax? FindConditionalChain(ExpressionSyntax syntax)
    {
        var path = new List<ExpressionSyntax>();

        for (var node = syntax; node is not null; node = ReceiverOf(node))
        {
            path.Add(node);
            if (!IsConditionalNode(node)) continue;

            _pendingChainPath = path;
            return node;
        }

        return null;
    }

    private List<ExpressionSyntax>? _pendingChainPath;

    private BoundExpression BindConditionalChain(ExpressionSyntax outer, ExpressionSyntax conditionalNode)
    {
        var path = _pendingChainPath!;
        _pendingChainPath = null;

        var receiverSyntax = ReceiverOf(conditionalNode)!;
        var receiver = BindExpression(receiverSyntax);
        var position = conditionalNode.Position;

        if (!Conversions.IsNullAssignable(receiver.Type) && receiver is not BoundErrorExpression)
        {
            _diagnostics.Report(ErrorCode.CannotConvert, position,
                $"'?.' 的操作数不可能为 null（类型 {TypeResolver.Display(receiver.Type)}）。");
            return BindExpressionCore(outer);
        }

        var temp = MakeTemp(receiver.Type);

        // Members of a nullable value type are reached through its underlying value.
        BoundExpression substitute = new BoundLocalAccess(position, temp);
        if (Conversions.IsNullableValueType(receiver.Type))
        {
            var getValue = receiver.Type.GetMethod("GetValueOrDefault", Type.EmptyTypes)!;
            substitute = new BoundCall(position, substitute, getValue, []);
        }

        var savedFor = _substituteFor;
        var savedSubstitute = _substitute;
        var savedChain = _chainNodes;

        _substituteFor = receiverSyntax;
        _substitute = substitute;
        _chainNodes = [.. path];

        var whenNotNull = BindExpressionCore(outer);

        _substituteFor = savedFor;
        _substitute = savedSubstitute;
        _chainNodes = savedChain;

        var resultType = whenNotNull.Type == typeof(void)
            ? typeof(void)
            : Conversions.Lift(whenNotNull.Type);

        if (resultType != whenNotNull.Type && resultType != typeof(void))
            whenNotNull = Convert(whenNotNull, resultType, position, explicitCast: false);

        return new BoundConditionalAccess(position, resultType, receiver, whenNotNull, temp);
    }

    // ============================================================ primaries

    private BoundExpression BindLiteral(LiteralExpressionSyntax syntax)
    {
        var token = syntax.Token;
        if (token.Kind == SyntaxKind.NullKeyword) return new BoundNullLiteral(syntax.Position);

        var type = token.Kind switch
        {
            SyntaxKind.IntLiteral => typeof(int),
            SyntaxKind.UIntLiteral => typeof(uint),
            SyntaxKind.LongLiteral => typeof(long),
            SyntaxKind.ULongLiteral => typeof(ulong),
            SyntaxKind.DoubleLiteral => typeof(double),
            SyntaxKind.FloatLiteral => typeof(float),
            SyntaxKind.DecimalLiteral => typeof(decimal),
            SyntaxKind.StringLiteral => typeof(string),
            SyntaxKind.CharLiteral => typeof(char),
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => typeof(bool),
            _ => typeof(object),
        };

        return new BoundLiteral(syntax.Position, type, token.Value);
    }

    private BoundExpression BindName(NameExpressionSyntax syntax)
    {
        if (_scope.TryLookup(syntax.Name, out var local))
        {
            return local.ConstantValue is { } constant
                ? constant with { Position = syntax.Position }
                : MakeLocalAccess(syntax.Position, local);
        }

        if (TryBindGlobalsMember(syntax.Position, syntax.Name, out var globalsMember))
            return globalsMember!;

        var type = _resolver.ResolveName(syntax.Name);
        if (type is not null) return new BoundTypeReference(syntax.Position, type);

        if (TryBindMethodGroup(syntax.Position, syntax.Name) is { } methodGroup) return methodGroup;

        return Fail(syntax.Position, ErrorCode.UndefinedName, $"找不到名称 '{syntax.Name}'。");
    }

    /// <summary>
    /// Reads a variable. Reaching one that belongs to an enclosing function is exactly what
    /// capture means, so the variable is moved into its declaring scope's closure.
    /// </summary>
    private BoundExpression MakeLocalAccess(SourcePosition position, LocalSymbol local)
    {
        if (local.FunctionDepth < _functionDepth)
        {
            if (_staticBoundary >= 0 && local.FunctionDepth < _staticBoundary)
            {
                return Fail(position, ErrorCode.ConstructNotSupported,
                    $"static 局部函数不能引用外部变量 '{local.Name}'。去掉 static，或把它作为参数传进来。");
            }

            local.DeclaringScope!.Capture(local);
        }

        return new BoundLocalAccess(position, local);
    }

    private bool TryBindGlobalsMember(SourcePosition position, string name, out BoundExpression? result)
    {
        result = null;
        if (_globals is null || _globalsLocal is null) return false;

        var receiver = MakeLocalAccess(position, _globalsLocal);

        var property = _globals.Type.GetProperty(name, InstanceFlags);
        if (property is not null && property.CanRead)
        {
            result = new BoundPropertyAccess(position, receiver, property);
            return true;
        }

        var field = _globals.Type.GetField(name, InstanceFlags);
        if (field is not null)
        {
            result = MakeFieldAccess(position, receiver, field);
            return true;
        }

        return false;
    }

    private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax)
    {
        // A dotted chain may name a type through its namespace, as in System.Math.Max. There is
        // no namespace value to bind, so the whole chain is offered to the type resolver first —
        // but only when its head is not already a variable or a globals member, which keeps the
        // C# order of "locals, then members, then types".
        if (!syntax.IsNullConditional &&
            !HeadResolvesAsValue(syntax) &&
            TryGetDottedName(syntax, out var dotted) &&
            _resolver.ResolveName(dotted) is { } qualified)
        {
            return new BoundTypeReference(syntax.Position, qualified);
        }

        var receiver = BindExpression(syntax.Target);
        if (receiver is BoundErrorExpression) return receiver;

        if (receiver is BoundTypeReference typeReference)
            return BindStaticMember(syntax.Position, typeReference.ReferencedType, syntax.MemberName);

        return BindInstanceMember(syntax.Position, receiver, syntax.MemberName);
    }

    /// <summary>Renders a chain of plain member accesses as a dotted name, or fails.</summary>
    private static bool TryGetDottedName(ExpressionSyntax syntax, out string dotted)
    {
        var parts = new Stack<string>();

        for (var node = syntax; ; )
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax { IsNullConditional: false } member:
                    parts.Push(member.MemberName);
                    node = member.Target;
                    continue;

                case NameExpressionSyntax name:
                    parts.Push(name.Name);
                    dotted = string.Join('.', parts);
                    return true;

                default:
                    dotted = string.Empty;
                    return false;
            }
        }
    }

    /// <summary>True when the leftmost identifier of a chain is a variable or a globals member.</summary>
    private bool HeadResolvesAsValue(ExpressionSyntax syntax)
    {
        var node = syntax;
        while (node is MemberAccessExpressionSyntax member) node = member.Target;

        if (node is not NameExpressionSyntax name) return false;
        if (_scope.TryLookup(name.Name, out _)) return true;
        if (_globals is null) return false;

        return _globals.Type.GetProperty(name.Name, InstanceFlags) is not null ||
               _globals.Type.GetField(name.Name, InstanceFlags) is not null;
    }

    private BoundExpression BindInstanceMember(SourcePosition position, BoundExpression receiver, string name)
    {
        var type = receiver.Type;

        // A tuple element name is not a real member; it stands for a position.
        if (TupleElementFor(receiver, name, position) is { } element) return element;

        var property = type.GetProperty(name, InstanceFlags);
        if (property is not null)
        {
            if (!property.CanRead)
                return Fail(position, ErrorCode.PropertyHasNoGetter, $"属性 '{name}' 没有 get 访问器。");
            return new BoundPropertyAccess(position, receiver, property);
        }

        var field = type.GetField(name, InstanceFlags);
        if (field is not null) return MakeFieldAccess(position, receiver, field);

        // Interfaces do not inherit members through GetProperty/GetField.
        if (type.IsInterface)
        {
            foreach (var iface in type.GetInterfaces())
            {
                var inherited = iface.GetProperty(name, InstanceFlags);
                if (inherited is not null) return new BoundPropertyAccess(position, receiver, inherited);
            }
        }

        if (TryBindMethodGroup(position, receiver, type, name) is { } methodGroup) return methodGroup;

        return Fail(position, ErrorCode.UndefinedMember,
            $"{TypeResolver.Display(type)} 不包含名为 '{name}' 的成员。{Suggest(type, name)}");
    }

    private BoundExpression BindStaticMember(SourcePosition position, Type type, string name)
    {
        var property = type.GetProperty(name, StaticFlags);
        if (property is not null && property.CanRead)
            return new BoundPropertyAccess(position, null, property);

        var field = type.GetField(name, StaticFlags);
        if (field is not null) return MakeFieldAccess(position, null, field);

        var nested = type.GetNestedType(name, BindingFlags.Public);
        if (nested is not null) return new BoundTypeReference(position, nested);

        if (TryBindMethodGroup(position, null, type, name) is { } methodGroup) return methodGroup;

        return Fail(position, ErrorCode.UndefinedMember,
            $"{TypeResolver.Display(type)} 不包含名为 '{name}' 的静态成员。{Suggest(type, name)}");
    }

    /// <summary>
    /// A <c>const</c> field — every enum member is one — has no storage, so <c>ldsfld</c> would
    /// fail. Its value is inlined as a literal instead, exactly as the C# compiler does.
    /// </summary>
    private static BoundExpression MakeFieldAccess(SourcePosition position, BoundExpression? receiver, FieldInfo field)
    {
        if (field.IsLiteral)
            return new BoundLiteral(position, field.FieldType, field.GetRawConstantValue());

        return new BoundFieldAccess(position, field.IsStatic ? null : receiver, field);
    }

    /// <summary>Offers a "did you mean" hint when a member name is a near miss.</summary>
    private static string Suggest(Type type, string name)
    {
        var candidates = type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .Where(n => !n.Contains('_'))
            .Distinct()
            .Where(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) ||
                        (Math.Abs(n.Length - name.Length) <= 2 && Distance(n, name) <= 2))
            .Take(3)
            .ToArray();

        return candidates.Length == 0 ? string.Empty : $"是否想用 '{string.Join("' / '", candidates)}'?";
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // ============================================================ invocation

    private BoundExpression BindInvocation(InvocationExpressionSyntax syntax)
    {
        BoundExpression? receiver = null;
        Type lookupType;
        string methodName;
        var staticOnly = false;

        switch (syntax.Target)
        {
            case NameExpressionSyntax delegateName
                when _scope.TryLookup(delegateName.Name, out var delegateLocal) &&
                     Conversions.IsDelegateType(delegateLocal.Type):
                return BindDelegateInvocation(syntax, MakeLocalAccess(syntax.Position, delegateLocal));

            case MemberAccessExpressionSyntax member:
            {
                var target = BindExpression(member.Target);
                if (target is BoundErrorExpression) return target;

                if (target is BoundTypeReference typeReference)
                {
                    lookupType = typeReference.ReferencedType;
                    staticOnly = true;
                }
                else
                {
                    receiver = target;
                    lookupType = target.Type;
                }
                methodName = member.MemberName;
                break;
            }

            case NameExpressionSyntax name when _globalsLocal is not null:
            {
                receiver = MakeLocalAccess(syntax.Position, _globalsLocal);
                lookupType = _globals!.Type;
                methodName = name.Name;
                break;
            }

            default:
            {
                // Anything that evaluates to a delegate can be invoked: a chained call such as
                // f(1)(2), an indexer result, a property, and so on.
                var value = BindExpression(syntax.Target);
                if (value is BoundErrorExpression) return value;

                if (Conversions.IsDelegateType(value.Type))
                    return BindDelegateInvocation(syntax, value);

                return Fail(syntax.Position, ErrorCode.NotInvocable,
                    $"{TypeResolver.Display(value.Type)} 不可调用。");
            }
        }

        var methods = GatherMethods(lookupType, methodName, staticOnly);

        var extensions = staticOnly || receiver is null
            ? []
            : _resolver.GetExtensionMethods(methodName);

        if (syntax.TypeArguments is { Count: > 0 } typeArgumentSyntax)
        {
            var typeArguments = new Type[typeArgumentSyntax.Count];
            for (var i = 0; i < typeArguments.Length; i++)
            {
                var resolved = ResolveType(typeArgumentSyntax[i]);
                if (resolved is null) return new BoundErrorExpression(syntax.Position);
                typeArguments[i] = resolved;
            }

            methods = Substitute(methods, typeArguments).Cast<MethodBase>().ToList();
            extensions = Substitute(extensions, typeArguments).ToList();

            if (methods.Count == 0 && extensions.Count == 0)
            {
                return Fail(syntax.Position, ErrorCode.NoMatchingOverload,
                    $"'{methodName}' 没有接受 {typeArguments.Length} 个类型参数、" +
                    "且满足其约束的泛型重载。");
            }
        }

        if (methods.Count == 0 && extensions.Count == 0)
        {
            // A member holding a delegate is invoked rather than called.
            if (TryFindDelegateMember(lookupType, methodName, receiver, syntax.Position) is { } delegateMember)
                return BindDelegateInvocation(syntax, delegateMember);

            return Fail(syntax.Position, ErrorCode.UndefinedMember,
                $"{TypeResolver.Display(lookupType)} 不包含名为 '{methodName}' 的方法。{Suggest(lookupType, methodName)}");
        }

        var arguments = syntax.Arguments
            .Select(a => (Argument: a, Bound: BindArgument(a)))
            .ToArray();

        if (arguments.Any(a => a.Bound is BoundErrorExpression))
            return new BoundErrorExpression(syntax.Position);

        var bound = arguments.Select(a => a.Bound).ToArray();
        var infos = arguments.Select(a => Describe(a.Bound, a.Argument)).ToArray();

        var resolution = methods.Count > 0
            ? OverloadResolution.Resolve(methods, infos, (i, types) => ProbeLambdaReturn(bound, i, types))
            : new OverloadResult(OverloadOutcome.NoneApplicable, null, methods);

        // An extension method is only considered when no ordinary member fits, which is the
        // order C# uses too.
        if (resolution.Outcome != OverloadOutcome.Resolved && extensions.Count > 0 && receiver is not null)
        {
            var extended = BindAsExtensionCall(syntax, methodName, receiver, bound, infos, extensions);
            if (extended is not null) return extended;
        }

        switch (resolution.Outcome)
        {
            case OverloadOutcome.Resolved:
                break;

            case OverloadOutcome.Ambiguous:
                return Fail(syntax.Position, ErrorCode.AmbiguousOverload,
                    $"对 '{methodName}' 的调用不明确，存在多个同样匹配的重载。");

            default:
                if (resolution.SkippedGenericCandidates || extensions.Count > 0)
                {
                    return Fail(syntax.Position, ErrorCode.GenericMethodInferenceNotSupported,
                        $"无法确定 '{methodName}' 的类型参数。请检查实参类型，" +
                        "或写出显式类型（引擎不支持从方法组推断）。");
                }

                return Fail(syntax.Position, ErrorCode.NoMatchingOverload,
                    $"方法 '{TypeResolver.Display(lookupType)}.{methodName}' 没有匹配 " +
                    $"({string.Join(", ", infos.Select(i => TypeResolver.Display(i.Type)))}) 的重载；" +
                    $"候选: {DescribeCandidates(methods)}");
        }

        var best = resolution.Best!;
        var method = (MethodInfo)best.Method;

        if (method.IsStatic && receiver is not null && !staticOnly)
        {
            return Fail(syntax.Position, ErrorCode.MemberIsStatic,
                $"'{methodName}' 是静态方法，不能通过实例调用。");
        }

        if (!method.IsStatic && receiver is null)
        {
            return Fail(syntax.Position, ErrorCode.MemberIsNotStatic,
                $"'{methodName}' 是实例方法，需要一个实例。");
        }

        var finalArguments = BuildArguments(best, MaterialiseOutVariables(best, bound), syntax.Position);
        return new BoundCall(syntax.Position, method.IsStatic ? null : receiver, method, finalArguments);
    }

    /// <summary>
    /// Presents an argument to overload resolution. A lambda has no type yet, so it is described
    /// by its parameter count and matched against delegate-typed parameters.
    /// </summary>
    private static ArgumentInfo Describe(BoundExpression argument, string? name) =>
        argument is BoundUnboundLambda unbound
            ? new ArgumentInfo(Conversions.LambdaType, name, unbound.Syntax.Parameters.Count)
            : new ArgumentInfo(argument.Type, name);

    private BoundExpression? TryFindDelegateMember(
        Type lookupType,
        string name,
        BoundExpression? receiver,
        SourcePosition position)
    {
        var property = lookupType.GetProperty(name, receiver is null ? StaticFlags : InstanceFlags);
        if (property is not null && property.CanRead && Conversions.IsDelegateType(property.PropertyType))
            return new BoundPropertyAccess(position, receiver, property);

        var field = lookupType.GetField(name, receiver is null ? StaticFlags : InstanceFlags);
        if (field is not null && Conversions.IsDelegateType(field.FieldType))
            return new BoundFieldAccess(position, field.IsStatic ? null : receiver, field);

        return null;
    }

    private BoundExpression BindDelegateInvocation(InvocationExpressionSyntax syntax, BoundExpression target)
    {
        var invoke = Conversions.GetInvokeMethod(target.Type)!;
        var parameters = invoke.GetParameters();

        if (syntax.Arguments.Count != parameters.Length)
        {
            return Fail(syntax.Position, ErrorCode.WrongArgumentCount,
                $"{TypeResolver.Display(target.Type)} 需要 {parameters.Length} 个参数，" +
                $"实际提供了 {syntax.Arguments.Count} 个。");
        }

        var arguments = new BoundExpression[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var bound = BindExpression(syntax.Arguments[i].Value);
            if (bound is BoundErrorExpression) return bound;
            arguments[i] = Convert(bound, parameters[i].ParameterType, syntax.Position, explicitCast: false);
        }

        return new BoundDelegateInvoke(syntax.Position, invoke.ReturnType, target, invoke, arguments);
    }

    /// <summary>
    /// Re-runs resolution with the receiver moved into first position, which is exactly what an
    /// extension method call is. Returns null when no extension applies either.
    /// </summary>
    private BoundExpression? BindAsExtensionCall(
        InvocationExpressionSyntax syntax,
        string methodName,
        BoundExpression receiver,
        IReadOnlyList<BoundExpression> bound,
        IReadOnlyList<ArgumentInfo> infos,
        IReadOnlyList<MethodInfo> extensions)
    {
        var extendedInfos = new ArgumentInfo[infos.Count + 1];
        extendedInfos[0] = new ArgumentInfo(receiver.Type, null);
        for (var i = 0; i < infos.Count; i++) extendedInfos[i + 1] = infos[i];

        var extendedBound = new List<BoundExpression>(bound.Count + 1) { receiver };
        extendedBound.AddRange(bound);

        var candidates = extensions.Cast<MethodBase>().ToArray();

        // Argument 0 is the receiver, so a lambda probe index has to shift back by one.
        var resolution = OverloadResolution.Resolve(
            candidates, extendedInfos, (i, types) => ProbeLambdaReturn(bound, i - 1, types));

        if (resolution.Outcome != OverloadOutcome.Resolved) return null;

        var method = (MethodInfo)resolution.Best!.Method;
        var finalArguments = BuildArguments(resolution.Best!, extendedBound, syntax.Position);

        _ = methodName;
        return new BoundCall(syntax.Position, null, method, finalArguments);
    }

    /// <summary>
    /// The delegate type a lambda has on its own, which exists only when every parameter type is
    /// written out. The return type is probed from the body; a body with no value is an
    /// <c>Action</c>, which is also what a body that fails to bind falls back to — the real
    /// diagnostics then come from binding it for real.
    /// </summary>
    private Type? NaturalDelegateType(LambdaExpressionSyntax syntax)
    {
        if (syntax.Parameters.Any(p => p.Type is null)) return null;

        var parameterTypes = new Type[syntax.Parameters.Count];
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            var resolved = ResolveType(syntax.Parameters[i].Type!);
            if (resolved is null) return null;
            parameterTypes[i] = resolved;
        }

        var returnType = ProbeLambdaReturn(syntax, parameterTypes) ?? typeof(void);

        if (syntax.IsAsync)
        {
            returnType = returnType == typeof(void)
                ? typeof(Task)
                : typeof(Task<>).MakeGenericType(returnType);
        }

        return MakeDelegateType(parameterTypes, returnType);
    }

    /// <summary>The <c>Func</c> or <c>Action</c> for a signature, or null when it does not fit one.</summary>
    private static Type? MakeDelegateType(Type[] parameterTypes, Type returnType)
    {
        if (parameterTypes.Any(t => t == typeof(void))) return null;

        var isAction = returnType == typeof(void);
        if (isAction && parameterTypes.Length == 0) return typeof(Action);

        var arity = parameterTypes.Length + (isAction ? 0 : 1);
        if (arity > 16) return null;

        var definition = Type.GetType($"System.{(isAction ? "Action" : "Func")}`{arity}");
        if (definition is null) return null;

        return definition.MakeGenericType(isAction ? parameterTypes : [.. parameterTypes, returnType]);
    }

    /// <summary>
    /// Binds a lambda argument's body speculatively so that generic inference can learn its
    /// return type. Diagnostics and any lambdas it creates are discarded; only the type escapes.
    /// </summary>
    private Type? ProbeLambdaReturn(IReadOnlyList<BoundExpression> bound, int index, Type[] parameterTypes)
    {
        if (index < 0 || index >= bound.Count) return null;
        if (bound[index] is BoundMethodGroup group) return ProbeMethodGroupReturn(group, parameterTypes);
        if (bound[index] is not BoundUnboundLambda unbound) return null;

        return ProbeLambdaReturn(unbound.Syntax, parameterTypes);
    }

    private Type? ProbeLambdaReturn(LambdaExpressionSyntax syntax, Type[] parameterTypes)
    {
        if (syntax.Parameters.Count != parameterTypes.Length) return null;

        var savedDiagnostics = _diagnostics;
        var savedScope = _scope;
        var savedClosure = _closureScope;
        var savedLocals = _locals;
        var savedFunctionDepth = _functionDepth;
        var savedProbe = _returnTypeProbe;
        var savedLoopDepth = _loopDepth;
        var savedSwitchDepth = _switchDepth;
        var savedHandlerDepth = _handlerDepth;
        var savedProtectedDepth = _protectedDepth;
        var savedLabels = _labels;
        var savedGotos = _pendingGotos;
        var savedAsyncContext = _isAsyncContext;
        var lambdaCount = _lambdas.Count;

        _diagnostics = new DiagnosticBag();
        _functionDepth++;
        _scope = new Scope(savedScope);
        _closureScope = new ClosureScope(savedClosure);
        _locals = [];
        _loopDepth = 0;
        _switchDepth = 0;
        _handlerDepth = 0;
        _protectedDepth = 0;
        _labels = [];
        _pendingGotos = [];
        _isAsyncContext = syntax.IsAsync;

        try
        {
            for (var i = 0; i < parameterTypes.Length; i++)
            {
                var parameter = new LocalSymbol(syntax.Parameters[i].Name, parameterTypes[i])
                {
                    LambdaArgIndex = i + 1,
                };

                Register(parameter);
                _scope.TryDeclare(parameter);
            }

            if (syntax.Body is ExpressionSyntax expressionBody)
            {
                _returnTypeProbe = null;
                var result = BindExpression(expressionBody);

                if (_diagnostics.HasErrors || result is BoundErrorExpression) return null;
                return Usable(result.Type);
            }

            // A block body contributes the types of its return statements; they are collected
            // rather than converted, because the target type is what is being inferred.
            var collected = new List<Type>();
            _returnTypeProbe = collected;

            BindStatement((StatementSyntax)syntax.Body);

            if (_diagnostics.HasErrors || collected.Count == 0) return null;

            var common = collected[0];
            for (var i = 1; i < collected.Count; i++)
            {
                var next = BestCommonType(common, collected[i]);
                if (next is null) return null;
                common = next;
            }

            return Usable(common);
        }
        finally
        {
            _diagnostics = savedDiagnostics;
            _scope = savedScope;
            _closureScope = savedClosure;
            _locals = savedLocals;
            _functionDepth = savedFunctionDepth;
            _returnTypeProbe = savedProbe;
            _loopDepth = savedLoopDepth;
            _switchDepth = savedSwitchDepth;
            _handlerDepth = savedHandlerDepth;
            _protectedDepth = savedProtectedDepth;
            _labels = savedLabels;
            _pendingGotos = savedGotos;
            _isAsyncContext = savedAsyncContext;
            _lambdas.RemoveRange(lambdaCount, _lambdas.Count - lambdaCount);
        }

        static Type? Usable(Type type) =>
            type == typeof(void) || Conversions.IsUntyped(type) ? null : type;
    }

    private static string DescribeCandidates(IReadOnlyList<MethodBase> methods) =>
        string.Join(", ", methods.Take(4).Select(m =>
            $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => TypeResolver.Display(p.ParameterType)))})"));

    /// <summary>
    /// Applies explicit type arguments, keeping only the generic definitions of the right arity
    /// whose constraints the arguments satisfy. Everything downstream then sees ordinary
    /// constructed methods, so overload resolution needs no notion of explicit type arguments.
    /// </summary>
    private static IEnumerable<MethodInfo> Substitute(IEnumerable<MethodBase> candidates, Type[] typeArguments)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is not MethodInfo method || !method.IsGenericMethodDefinition) continue;
            if (method.GetGenericArguments().Length != typeArguments.Length) continue;

            MethodInfo constructed;
            try
            {
                constructed = method.MakeGenericMethod(typeArguments);
            }
            catch (ArgumentException)
            {
                continue; // constraints not satisfied
            }

            yield return constructed;
        }
    }

    private static List<MethodBase> GatherMethods(Type type, string name, bool staticOnly)
    {
        var flags = staticOnly ? StaticFlags : InstanceFlags | StaticFlags;
        var result = new List<MethodBase>();

        foreach (var method in type.GetMethods(flags))
            if (method.Name == name && !method.IsSpecialName)
                result.Add(method);

        if (type.IsInterface)
        {
            foreach (var iface in type.GetInterfaces())
                foreach (var method in iface.GetMethods(InstanceFlags))
                    if (method.Name == name && !method.IsSpecialName)
                        result.Add(method);
        }

        if (result.Count == 0 && !staticOnly)
        {
            foreach (var method in typeof(object).GetMethods(InstanceFlags))
                if (method.Name == name)
                    result.Add(method);
        }

        return RemoveHidden(result);
    }

    /// <summary>
    /// Drops base-class methods that a derived class hides with the same signature. Reflection
    /// reports both, and to overload resolution they look equally good — <c>Task&lt;T&gt;</c>
    /// hiding <c>Task.GetAwaiter</c> is the case that makes this matter.
    /// </summary>
    private static List<MethodBase> RemoveHidden(List<MethodBase> methods)
    {
        if (methods.Count < 2) return methods;

        var kept = new List<MethodBase>(methods.Count);

        foreach (var method in methods)
        {
            var hidden = methods.Any(other =>
                !ReferenceEquals(other, method) &&
                SameSignature(other, method) &&
                IsMoreDerived(other.DeclaringType, method.DeclaringType));

            if (!hidden) kept.Add(method);
        }

        return kept.Count == 0 ? methods : kept;
    }

    private static bool SameSignature(MethodBase left, MethodBase right)
    {
        var a = left.GetParameters();
        var b = right.GetParameters();

        return a.Length == b.Length &&
               left.IsStatic == right.IsStatic &&
               !a.Where((p, i) => p.ParameterType != b[i].ParameterType).Any();
    }

    private static bool IsMoreDerived(Type? derived, Type? bas) =>
        derived is not null && bas is not null && derived != bas && bas.IsAssignableFrom(derived);

    /// <summary>Materialises the final argument list: conversions applied, defaults filled, params packed.</summary>
    private IReadOnlyList<BoundExpression> BuildArguments(
        ResolvedOverload overload,
        IReadOnlyList<BoundExpression> bound,
        SourcePosition position)
    {
        var result = new List<BoundExpression>(overload.Parameters.Length);

        for (var p = 0; p < overload.Parameters.Length; p++)
        {
            var parameter = overload.Parameters[p];
            var indices = overload.ParameterArguments[p];
            var isParamsSlot = overload.Expanded && p == overload.Parameters.Length - 1;

            if (isParamsSlot)
            {
                var elementType = overload.ParamsElementType!;
                var elements = indices
                    .Select(i => Convert(bound[i], elementType, position, explicitCast: false))
                    .ToArray();
                result.Add(new BoundArrayCreation(position, elementType, elements));
                continue;
            }

            if (indices.Length == 0)
            {
                result.Add(MakeDefaultArgument(parameter, position));
                continue;
            }

            result.Add(parameter.ParameterType.IsByRef
                ? bound[indices[0]]
                : Convert(bound[indices[0]], parameter.ParameterType, position, explicitCast: false));
        }

        return result;
    }

    private static BoundExpression MakeDefaultArgument(ParameterInfo parameter, SourcePosition position)
    {
        var type = parameter.ParameterType;
        var value = parameter.HasDefaultValue ? parameter.DefaultValue : null;

        if (value is null) return new BoundDefault(position, type);
        return new BoundLiteral(position, type, value);
    }

    // ============================================================ indexing

    private BoundExpression BindIndex(IndexExpressionSyntax syntax)
    {
        var receiver = BindExpression(syntax.Target);
        return receiver is BoundErrorExpression
            ? receiver
            : BindIndexAccess(receiver, syntax.Arguments, syntax.Position);
    }

    /// <summary>Indexing an already-bound receiver, which an initializer also needs.</summary>
    private BoundExpression BindIndexAccess(
        BoundExpression receiver,
        IReadOnlyList<ExpressionSyntax> argumentSyntax,
        SourcePosition position)
    {
        var arguments = argumentSyntax.Select(BindExpression).ToArray();
        if (arguments.Any(a => a is BoundErrorExpression)) return new BoundErrorExpression(position);

        if (TryBindIndexOrRangeAccess(receiver, arguments, position) is { } special) return special;

        return BindIndexAccessCore(receiver, arguments, position);
    }

    private BoundExpression BindIndexAccessCore(
        BoundExpression receiver,
        IReadOnlyList<BoundExpression> arguments,
        SourcePosition position)
    {
        var syntax = new { Position = position };

        if (receiver.Type.IsArray)
        {
            var rank = receiver.Type.GetArrayRank();
            if (rank != arguments.Count)
            {
                return Fail(syntax.Position, ErrorCode.NotIndexable,
                    $"数组是 {rank} 维的，但给了 {arguments.Count} 个下标。");
            }

            var indices = arguments
                .Select(a => Convert(a, typeof(int), syntax.Position, explicitCast: false))
                .ToArray();

            return new BoundArrayAccess(syntax.Position, receiver.Type.GetElementType()!, receiver, indices);
        }

        var indexers = FindIndexers(receiver.Type);
        if (indexers.Count == 0)
        {
            return Fail(syntax.Position, ErrorCode.NotIndexable,
                $"{TypeResolver.Display(receiver.Type)} 没有索引器。");
        }

        var infos = arguments.Select(a => new ArgumentInfo(a.Type, null)).ToArray();
        var getters = indexers.Select(i => (MethodBase)i.GetMethod!).Where(m => m is not null).ToArray();
        var resolution = OverloadResolution.Resolve(getters, infos);

        if (resolution.Outcome != OverloadOutcome.Resolved)
        {
            return Fail(syntax.Position, ErrorCode.NotIndexable,
                $"{TypeResolver.Display(receiver.Type)} 的索引器没有匹配 " +
                $"({string.Join(", ", infos.Select(i => TypeResolver.Display(i.Type)))}) 的重载。");
        }

        var getter = (MethodInfo)resolution.Best!.Method;
        var indexer = indexers.First(i => i.GetMethod == getter);
        var finalArguments = BuildArguments(resolution.Best!, arguments, syntax.Position);

        return new BoundIndexerAccess(syntax.Position, receiver, indexer, finalArguments);
    }

    private static List<PropertyInfo> FindIndexers(Type type)
    {
        var result = new List<PropertyInfo>();

        foreach (var property in type.GetProperties(InstanceFlags))
            if (property.GetIndexParameters().Length > 0)
                result.Add(property);

        if (type.IsInterface)
        {
            foreach (var iface in type.GetInterfaces())
                foreach (var property in iface.GetProperties(InstanceFlags))
                    if (property.GetIndexParameters().Length > 0)
                        result.Add(property);
        }

        return result;
    }

    // ============================================================ misc expressions

    private BoundExpression BindCast(CastExpressionSyntax syntax)
    {
        var target = ResolveType(syntax.Type);
        var operand = BindExpression(syntax.Operand);
        if (target is null || operand is BoundErrorExpression) return new BoundErrorExpression(syntax.Position);
        return Convert(operand, target, syntax.Position, explicitCast: true);
    }

    private BoundExpression BindAs(AsExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        var target = ResolveType(syntax.Type);
        if (target is null || operand is BoundErrorExpression) return new BoundErrorExpression(syntax.Position);

        if (!Conversions.IsNullAssignable(target))
        {
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                $"'as' 的目标类型必须可为 null，{TypeResolver.Display(target)} 不满足。");
        }

        if (operand.Type.IsValueType && !Conversions.IsNullableValueType(operand.Type))
            operand = Convert(operand, typeof(object), syntax.Position, explicitCast: false);

        return new BoundAsType(syntax.Position, target, operand);
    }

    private BoundExpression BindTypeof(TypeofExpressionSyntax syntax)
    {
        var target = ResolveType(syntax.Type);
        return target is null
            ? new BoundErrorExpression(syntax.Position)
            : new BoundTypeofExpression(syntax.Position, target);
    }

    private BoundExpression BindCheckedExpression(CheckedExpressionSyntax syntax)
    {
        var saved = _checked;
        _checked = syntax.IsChecked;

        var operand = BindExpression(syntax.Operand);

        _checked = saved;
        return operand;
    }

    private BoundExpression BindConditionalExpression(ConditionalExpressionSyntax syntax)
    {
        var condition = BindCondition(syntax.Condition);
        var whenTrue = BindExpression(syntax.WhenTrue);
        var whenFalse = BindExpression(syntax.WhenFalse);

        var type = BestCommonType(whenTrue.Type, whenFalse.Type);
        if (type is null)
        {
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                $"条件表达式的两个分支类型不兼容：{TypeResolver.Display(whenTrue.Type)} 与 " +
                $"{TypeResolver.Display(whenFalse.Type)}。");
        }

        // Both branches untyped: leave them alone so that whatever consumes the conditional can
        // push its own target type down into them.
        if (Conversions.AdoptsTargetType(type))
            return new BoundConditional(syntax.Position, type, condition, whenTrue, whenFalse);

        return new BoundConditional(
            syntax.Position, type, condition,
            Convert(whenTrue, type, syntax.Position, explicitCast: false),
            Convert(whenFalse, type, syntax.Position, explicitCast: false));
    }

    private static Type? BestCommonType(Type left, Type right) => Conversions.BestCommonType(left, right);

    // ============================================================ await

    private BoundExpression BindAwait(AwaitExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return operand;

        if (!_isAsyncContext)
        {
            return _functionDepth > 0
                ? Fail(syntax.Position, ErrorCode.AwaitInLambda,
                    "lambda 或局部函数内使用 'await' 需要把它标记为 async，" +
                    "例如 async x => await ...。")
                : Fail(syntax.Position, ErrorCode.AwaitInSynchronousScript,
                    "同步脚本中不能使用 'await'。请改用 CompileAsync 编译。");
        }

        if (_handlerDepth > 0)
        {
            return Fail(syntax.Position, ErrorCode.AwaitInExceptionHandler,
                "'await' 不能出现在 catch 或 finally 块中。运行时对处理器块内的挂起点不提供保护，" +
                "该组合会导致进程崩溃。请将异步调用移出处理器。");
        }

        var awaited = AwaitHelpers.Describe(operand.Type);
        if (awaited is null)
        {
            return Fail(syntax.Position, ErrorCode.NotAWaitable,
                $"无法 await 类型 {TypeResolver.Display(operand.Type)}；仅支持 Task、Task<T>、ValueTask、ValueTask<T>。");
        }

        var (kind, resultType) = awaited.Value;
        var helper = AwaitHelpers.GetAwaitMethod(kind, resultType);

        return new BoundAwait(syntax.Position, resultType, operand, kind, helper);
    }
}
