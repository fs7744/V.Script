using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

internal sealed partial class Binder
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.Static;

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
        AsExpressionSyntax asExpression => BindAs(asExpression),
        TypeofExpressionSyntax typeofExpression => BindTypeof(typeofExpression),
        ObjectCreationExpressionSyntax creation => BindObjectCreation(creation),
        LambdaExpressionSyntax lambda => ReportLambda(lambda),
        ErrorExpressionSyntax => new BoundErrorExpression(syntax.Position),
        _ => Fail(syntax.Position, ErrorCode.ConstructNotSupported, "不支持的表达式。"),
    };

    private BoundExpression ReportLambda(LambdaExpressionSyntax syntax) =>
        Fail(syntax.Position, ErrorCode.LambdaNotSupported,
            "当前版本尚不支持 lambda 表达式与闭包。请在宿主侧预先计算后通过 globals 传入。");

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
            return new BoundLocalAccess(syntax.Position, local);

        foreach (var parameter in _parameters)
        {
            if (parameter.IsGlobals || parameter.Name != syntax.Name) continue;
            return new BoundParameterAccess(syntax.Position, parameter.Type, parameter.IlIndex);
        }

        if (TryBindGlobalsMember(syntax.Position, syntax.Name, out var globalsMember))
            return globalsMember!;

        var type = _resolver.ResolveName(syntax.Name);
        if (type is not null) return new BoundTypeReference(syntax.Position, type);

        return Fail(syntax.Position, ErrorCode.UndefinedName, $"找不到名称 '{syntax.Name}'。");
    }

    private bool TryBindGlobalsMember(SourcePosition position, string name, out BoundExpression? result)
    {
        result = null;
        if (_globals is null) return false;

        var receiver = new BoundParameterAccess(position, _globals.Type, _globals.IlIndex);

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
        var receiver = BindExpression(syntax.Target);
        if (receiver is BoundErrorExpression) return receiver;

        if (receiver is BoundTypeReference typeReference)
            return BindStaticMember(syntax.Position, typeReference.ReferencedType, syntax.MemberName);

        return BindInstanceMember(syntax.Position, receiver, syntax.MemberName);
    }

    private BoundExpression BindInstanceMember(SourcePosition position, BoundExpression receiver, string name)
    {
        var type = receiver.Type;

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

            case NameExpressionSyntax name when _globals is not null:
            {
                receiver = new BoundParameterAccess(syntax.Position, _globals.Type, _globals.IlIndex);
                lookupType = _globals.Type;
                methodName = name.Name;
                break;
            }

            default:
                return Fail(syntax.Position, ErrorCode.NotInvocable, "该表达式不可调用。");
        }

        var methods = GatherMethods(lookupType, methodName, staticOnly);
        if (methods.Count == 0)
        {
            if (!staticOnly && _resolver.HasExtensionMethodCandidate(lookupType, methodName))
            {
                return Fail(syntax.Position, ErrorCode.ExtensionMethodNotSupported,
                    $"'{methodName}' 只能作为扩展方法调用，当前版本尚不支持扩展方法（LINQ 亦在此列）。" +
                    "请在宿主侧计算后通过 globals 传入结果。");
            }

            return Fail(syntax.Position, ErrorCode.UndefinedMember,
                $"{TypeResolver.Display(lookupType)} 不包含名为 '{methodName}' 的方法。{Suggest(lookupType, methodName)}");
        }

        var arguments = syntax.Arguments
            .Select(a => (Argument: a, Bound: BindExpression(a.Value)))
            .ToArray();

        if (arguments.Any(a => a.Bound is BoundErrorExpression))
            return new BoundErrorExpression(syntax.Position);

        var infos = arguments
            .Select(a => new ArgumentInfo(a.Bound.Type, a.Argument.Name))
            .ToArray();

        var resolution = OverloadResolution.Resolve(methods, infos);

        switch (resolution.Outcome)
        {
            case OverloadOutcome.Resolved:
                break;

            case OverloadOutcome.Ambiguous:
                return Fail(syntax.Position, ErrorCode.AmbiguousOverload,
                    $"对 '{methodName}' 的调用不明确，存在多个同样匹配的重载。");

            default:
                if (resolution.SkippedGenericCandidates)
                {
                    return Fail(syntax.Position, ErrorCode.GenericMethodInferenceNotSupported,
                        $"'{methodName}' 是泛型方法，需要类型参数推断，当前版本尚不支持。" +
                        "请在宿主侧调用后通过 globals 传入结果。");
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

        var finalArguments = BuildArguments(best, arguments.Select(a => a.Bound).ToArray(), syntax.Position);
        return new BoundCall(syntax.Position, method.IsStatic ? null : receiver, method, finalArguments);
    }

    private static string DescribeCandidates(IReadOnlyList<MethodBase> methods) =>
        string.Join(", ", methods.Take(4).Select(m =>
            $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => TypeResolver.Display(p.ParameterType)))})"));

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

        return result;
    }

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

            result.Add(Convert(bound[indices[0]], parameter.ParameterType, position, explicitCast: false));
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
        if (receiver is BoundErrorExpression) return receiver;

        var arguments = syntax.Arguments.Select(BindExpression).ToArray();

        if (receiver.Type.IsArray)
        {
            if (receiver.Type.GetArrayRank() != 1 || arguments.Length != 1)
            {
                return Fail(syntax.Position, ErrorCode.NotIndexable,
                    "只支持一维数组索引。");
            }

            var index = Convert(arguments[0], typeof(int), syntax.Position, explicitCast: false);
            return new BoundArrayAccess(syntax.Position, receiver.Type.GetElementType()!, receiver, index);
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

    private BoundExpression BindIs(IsExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        var target = ResolveType(syntax.Type);
        if (target is null || operand is BoundErrorExpression) return new BoundErrorExpression(syntax.Position);

        if (operand.Type.IsValueType && !Conversions.IsNullableValueType(operand.Type))
            operand = Convert(operand, typeof(object), syntax.Position, explicitCast: false);

        return new BoundIsType(syntax.Position, operand, target);
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

    private BoundExpression BindObjectCreation(ObjectCreationExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);
        if (type is null) return new BoundErrorExpression(syntax.Position);

        var arguments = syntax.Arguments
            .Select(a => (Argument: a, Bound: BindExpression(a.Value)))
            .ToArray();

        if (arguments.Any(a => a.Bound is BoundErrorExpression))
            return new BoundErrorExpression(syntax.Position);

        if (type.IsValueType && arguments.Length == 0)
            return new BoundDefault(syntax.Position, type);

        var constructors = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MethodBase>()
            .ToArray();

        var infos = arguments.Select(a => new ArgumentInfo(a.Bound.Type, a.Argument.Name)).ToArray();
        var resolution = OverloadResolution.Resolve(constructors, infos);

        if (resolution.Outcome != OverloadOutcome.Resolved)
        {
            return Fail(syntax.Position, ErrorCode.NoMatchingOverload,
                $"{TypeResolver.Display(type)} 没有匹配 " +
                $"({string.Join(", ", infos.Select(i => TypeResolver.Display(i.Type)))}) 的构造函数。");
        }

        var finalArguments = BuildArguments(resolution.Best!, arguments.Select(a => a.Bound).ToArray(), syntax.Position);
        return new BoundObjectCreation(syntax.Position, (ConstructorInfo)resolution.Best!.Method, finalArguments);
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

        return new BoundConditional(
            syntax.Position, type, condition,
            Convert(whenTrue, type, syntax.Position, explicitCast: false),
            Convert(whenFalse, type, syntax.Position, explicitCast: false));
    }

    private static Type? BestCommonType(Type left, Type right)
    {
        if (left == right) return left;
        if (left == Conversions.NullLiteralType) return Conversions.IsNullAssignable(right) ? right : Conversions.Lift(right);
        if (right == Conversions.NullLiteralType) return Conversions.IsNullAssignable(left) ? left : Conversions.Lift(left);

        var leftToRight = Conversions.HasImplicit(left, right);
        var rightToLeft = Conversions.HasImplicit(right, left);

        if (leftToRight && !rightToLeft) return right;
        if (rightToLeft && !leftToRight) return left;
        if (leftToRight && rightToLeft) return left;

        if (Conversions.IsNumeric(left) && Conversions.IsNumeric(right))
            return NumericPromotion.Promote(left, right);

        return null;
    }

    // ============================================================ await

    private BoundExpression BindAwait(AwaitExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return operand;

        if (!_isAsync)
        {
            return Fail(syntax.Position, ErrorCode.AwaitInSynchronousScript,
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

        // Route the await through Task.WaitAsync so that a configured timeout or the caller's
        // cancellation token can interrupt a suspended script. Without this, a pending await
        // never reaches a checkpoint and the limits would not apply.
        if (_limits.NeedsCheckpoints)
        {
            var normalized = AwaitHelpers.NormalizeForCancellation(operand, kind, resultType, syntax.Position);
            if (normalized is not null)
            {
                operand = AddWaitAsync(normalized.Value.Expression, normalized.Value.Kind, resultType, syntax.Position);
                kind = normalized.Value.Kind;
            }
        }

        var helper = AwaitHelpers.GetAwaitMethod(kind, resultType);
        return new BoundAwait(syntax.Position, resultType, operand, kind, helper);
    }

    private BoundExpression AddWaitAsync(BoundExpression task, AwaitKind kind, Type resultType, SourcePosition position)
    {
        var waitAsync = AwaitHelpers.GetWaitAsync(kind, resultType);
        if (waitAsync is null) return task;

        var token = new BoundIntrinsic(position, typeof(CancellationToken), IntrinsicKind.ScriptStateToken);
        return new BoundCall(position, task, waitAsync, [token]);
    }
}
