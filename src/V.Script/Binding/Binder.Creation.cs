using System.Reflection;
using System.Text;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// Object creation, array creation and interpolated strings. All three lower into shapes the
/// emitter already knows — a sequence of assignments, a <c>newarr</c>, or a call to
/// <c>string.Concat</c> / <c>string.Format</c> — so none of them reaches the emitter as a
/// special case.
/// </summary>
internal sealed partial class Binder
{
    private BoundExpression BindObjectCreation(ObjectCreationExpressionSyntax syntax)
    {
        var type = ResolveType(syntax.Type);
        if (type is null) return new BoundErrorExpression(syntax.Position);

        if (type.IsArray)
        {
            return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                "数组请写成 new T[n] 或 new T[] { ... }。");
        }

        if (Conversions.IsDelegateType(type))
        {
            if (syntax.Arguments.Count != 1 || syntax.Initializer is not null)
            {
                return Fail(syntax.Position, ErrorCode.WrongArgumentCount,
                    $"{TypeResolver.Display(type)} 是委托类型，new 只接受一个可转换为它的实参。");
            }

            var value = BindExpression(syntax.Arguments[0].Value);
            return value is BoundErrorExpression
                ? value
                : Convert(value, type, syntax.Position, explicitCast: false);
        }

        var creation = BindConstructorCall(syntax, type);
        if (creation is BoundErrorExpression || syntax.Initializer is null) return creation;

        return BindInitializer(creation, syntax.Initializer);
    }

    private BoundExpression BindConstructorCall(ObjectCreationExpressionSyntax syntax, Type type)
    {
        var arguments = syntax.Arguments
            .Select(a => (Argument: a, Bound: BindExpression(a.Value)))
            .ToArray();

        if (arguments.Any(a => a.Bound is BoundErrorExpression))
            return new BoundErrorExpression(syntax.Position);

        // A struct has no visible parameterless constructor to find, so `new S()` is `default(S)`.
        if (type.IsValueType && arguments.Length == 0)
            return new BoundDefault(syntax.Position, type);

        var constructors = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MethodBase>()
            .ToArray();

        var infos = arguments.Select(a => Describe(a.Bound, a.Argument.Name)).ToArray();
        var resolution = OverloadResolution.Resolve(constructors, infos);

        if (resolution.Outcome != OverloadOutcome.Resolved)
        {
            return Fail(syntax.Position, ErrorCode.NoMatchingOverload,
                $"{TypeResolver.Display(type)} 没有匹配 " +
                $"({string.Join(", ", infos.Select(i => TypeResolver.Display(i.Type)))}) 的构造函数。");
        }

        var finalArguments = BuildArguments(
            resolution.Best!, arguments.Select(a => a.Bound).ToArray(), syntax.Position);

        return new BoundObjectCreation(syntax.Position, (ConstructorInfo)resolution.Best!.Method, finalArguments);
    }

    /// <summary>
    /// Lowers <c>new T { ... }</c> into <c>tmp = new T(); tmp.X = ...; tmp</c>. The temp is what
    /// makes the initializer see one instance, and it is also what an <c>Add</c> call needs as a
    /// receiver.
    /// </summary>
    private BoundExpression BindInitializer(BoundExpression creation, InitializerSyntax syntax)
    {
        var position = syntax.Position;
        var temp = MakeTemp(creation.Type);
        var target = new BoundLocalAccess(position, temp);

        var effects = new List<BoundExpression> { new BoundAssignment(position, target, creation) };

        if (!ApplyInitializer(target, syntax, effects))
            return new BoundErrorExpression(position);

        return new BoundSequence(position, creation.Type, effects, target);
    }

    /// <summary>
    /// Appends the writes an initializer performs on an existing value. Nested initializers use
    /// the same entry point against the member they belong to, which is what makes
    /// <c>new A { Inner = { X = 1 } }</c> write into the object <c>Inner</c> already holds.
    /// </summary>
    private bool ApplyInitializer(BoundExpression target, InitializerSyntax syntax, List<BoundExpression> effects)
    {
        switch (syntax)
        {
            case ObjectInitializerSyntax members:
                foreach (var member in members.Members)
                    if (!BindMemberInitializer(target, member, effects))
                        return false;

                return true;

            case CollectionInitializerSyntax elements:
                foreach (var element in elements.Elements)
                {
                    var argument = BindExpression(element);
                    if (argument is BoundErrorExpression) return false;

                    var add = BindAddCall(target, argument, element.Position);
                    if (add is BoundErrorExpression) return false;

                    effects.Add(add);
                }

                return true;

            default:
                return true;
        }
    }

    private bool BindMemberInitializer(
        BoundExpression target,
        MemberInitializerSyntax syntax,
        List<BoundExpression> effects)
    {
        var access = syntax.Name is not null
            ? BindInstanceMember(syntax.Position, target, syntax.Name)
            : BindIndexAccess(target, syntax.Index!, syntax.Position);

        if (access is BoundErrorExpression) return false;

        // A nested initializer reads the member instead of assigning it.
        if (syntax.Nested is not null)
        {
            var inner = access;

            if (!IsRepeatable(inner))
            {
                var temp = MakeTemp(inner.Type);
                var slot = new BoundLocalAccess(syntax.Position, temp);
                effects.Add(new BoundAssignment(syntax.Position, slot, inner));
                inner = slot;
            }

            return ApplyInitializer(inner, syntax.Nested, effects);
        }

        var described = syntax.Name ?? "索引";

        if (access is BoundPropertyAccess { Property.SetMethod: null })
        {
            Fail(syntax.Position, ErrorCode.PropertyHasNoSetter, $"属性 '{described}' 没有 set 访问器。");
            return false;
        }

        if (!IsAssignable(access))
        {
            Fail(syntax.Position, ErrorCode.NotAssignable, $"'{described}' 是只读的，不能在对象初始化器中赋值。");
            return false;
        }

        var value = BindExpression(syntax.Value!);
        if (value is BoundErrorExpression) return false;

        effects.Add(new BoundAssignment(syntax.Position, access,
            Convert(value, access.Type, syntax.Position, explicitCast: false)));

        return true;
    }

    /// <summary>
    /// A collection initializer element is an <c>Add</c> call on the instance being built.
    /// Extension <c>Add</c> methods are deliberately not considered.
    /// </summary>
    private BoundExpression BindAddCall(BoundExpression target, BoundExpression argument, SourcePosition position)
    {
        var methods = GatherMethods(target.Type, "Add", staticOnly: false);
        if (methods.Count == 0)
        {
            return Fail(position, ErrorCode.UndefinedMember,
                $"{TypeResolver.Display(target.Type)} 没有 Add 方法，不能使用集合初始化器。");
        }

        BoundExpression[] bound = [argument];
        var infos = new[] { Describe(argument, (string?)null) };
        var resolution = OverloadResolution.Resolve(methods, infos, (i, types) => ProbeLambdaReturn(bound, i, types));

        if (resolution.Outcome != OverloadOutcome.Resolved || resolution.Best!.Method.IsStatic)
        {
            return Fail(position, ErrorCode.NoMatchingOverload,
                $"{TypeResolver.Display(target.Type)}.Add 没有匹配 " +
                $"{TypeResolver.Display(argument.Type)} 的重载。");
        }

        var arguments = BuildArguments(resolution.Best!, bound, position);
        return new BoundCall(position, target, (MethodInfo)resolution.Best!.Method, arguments);
    }

    // ============================================================ collection expressions

    /// <summary>
    /// Binds <c>[a, b, c]</c> now that a target type is known. An array target — and every
    /// interface an array satisfies — becomes an array; anything else is built the way a
    /// collection initializer builds it.
    /// </summary>
    private BoundExpression BindCollectionExpression(CollectionExpressionSyntax syntax, Type target)
    {
        var elementType = Conversions.CollectionElementType(target);
        if (elementType is null)
        {
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                $"无法把集合表达式转换为 {TypeResolver.Display(target)}；" +
                "目标需要是数组、数组能满足的接口，或者可以无参构造并 Add 的类型。");
        }

        if (syntax.Elements.Any(e => e.IsSpread))
            return BindSpreadCollection(syntax, target, elementType);

        var elements = syntax.Elements.Select(e => BindExpression(e.Value)).ToArray();
        if (elements.Any(e => e is BoundErrorExpression)) return new BoundErrorExpression(syntax.Position);

        var array = MakeArray(syntax.Position, elementType, elements);

        if (target.IsArray) return array;
        if (target.IsInterface) return Convert(array, target, syntax.Position, explicitCast: false);

        var constructor = target.GetConstructor(Type.EmptyTypes)!;
        var creation = new BoundObjectCreation(syntax.Position, constructor, []);

        var temp = MakeTemp(target);
        var receiver = new BoundLocalAccess(syntax.Position, temp);

        var effects = new List<BoundExpression>
        {
            new BoundAssignment(syntax.Position, receiver, creation),
        };

        foreach (var element in elements)
        {
            var add = BindAddCall(receiver, element, element.Position);
            if (add is BoundErrorExpression error) return error;
            effects.Add(add);
        }

        return new BoundSequence(syntax.Position, target, effects, receiver);
    }

    /// <summary>
    /// With a spread the final length is only known at run time, so the elements are collected
    /// into a <c>List&lt;T&gt;</c> and handed to the target from there. That costs one extra copy
    /// against C#'s length-counting lowering, and is what buys arbitrary spreads.
    /// </summary>
    private BoundExpression BindSpreadCollection(
        CollectionExpressionSyntax syntax,
        Type target,
        Type elementType)
    {
        var position = syntax.Position;
        var listType = typeof(List<>).MakeGenericType(elementType);

        var temp = MakeTemp(listType);
        var builder = new BoundLocalAccess(position, temp);

        var effects = new List<BoundExpression>
        {
            new BoundAssignment(position, builder,
                new BoundObjectCreation(position, listType.GetConstructor(Type.EmptyTypes)!, [])),
        };

        var addRange = listType.GetMethod("AddRange", [typeof(IEnumerable<>).MakeGenericType(elementType)])!;

        foreach (var element in syntax.Elements)
        {
            var value = BindExpression(element.Value);
            if (value is BoundErrorExpression) return value;

            if (!element.IsSpread)
            {
                var add = BindAddCall(builder, value, element.Position);
                if (add is BoundErrorExpression) return add;

                effects.Add(add);
                continue;
            }

            var sequence = typeof(IEnumerable<>).MakeGenericType(elementType);
            if (!sequence.IsAssignableFrom(value.Type))
            {
                return Fail(element.Position, ErrorCode.CannotConvert,
                    $"'..' 的操作数必须是 {TypeResolver.Display(sequence)}，" +
                    $"实际为 {TypeResolver.Display(value.Type)}。");
            }

            effects.Add(new BoundCall(element.Position, builder, addRange,
                [Convert(value, sequence, element.Position, explicitCast: false)]));
        }

        if (listType == target || target.IsAssignableFrom(listType))
            return new BoundSequence(position, target, effects, Convert(builder, target, position, explicitCast: false));

        if (target.IsArray)
        {
            var toArray = listType.GetMethod("ToArray", Type.EmptyTypes)!;
            return new BoundSequence(position, target, effects,
                new BoundCall(position, builder, toArray, []));
        }

        // Some other collection type: build it from the list through its Add method.
        var result = MakeTemp(target);
        var receiver = new BoundLocalAccess(position, result);

        effects.Add(new BoundAssignment(position, receiver,
            new BoundObjectCreation(position, target.GetConstructor(Type.EmptyTypes)!, [])));

        var toArrayMethod = listType.GetMethod("ToArray", Type.EmptyTypes)!;
        var addRangeOnTarget = target.GetMethod("AddRange", [typeof(IEnumerable<>).MakeGenericType(elementType)]);

        if (addRangeOnTarget is not null)
        {
            effects.Add(new BoundCall(position, receiver, addRangeOnTarget,
                [Convert(builder, typeof(IEnumerable<>).MakeGenericType(elementType), position, explicitCast: false)]));

            return new BoundSequence(position, target, effects, receiver);
        }

        _ = toArrayMethod;

        return Fail(position, ErrorCode.ConstructNotSupported,
            $"{TypeResolver.Display(target)} 没有 AddRange，不能用带 '..' 的集合表达式构造。");
    }

    // ============================================================ default, throw, nameof

    private BoundExpression BindDefault(DefaultExpressionSyntax syntax)
    {
        if (syntax.Type is null) return new BoundDefaultLiteral(syntax.Position);

        var type = ResolveType(syntax.Type);
        return type is null
            ? new BoundErrorExpression(syntax.Position)
            : new BoundDefault(syntax.Position, type);
    }

    private BoundExpression BindThrowExpression(ThrowExpressionSyntax syntax)
    {
        var exception = BindExpression(syntax.Exception);
        if (exception is BoundErrorExpression) return exception;

        if (!typeof(Exception).IsAssignableFrom(exception.Type))
        {
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                $"throw 的操作数必须是 Exception，实际为 {TypeResolver.Display(exception.Type)}。");
        }

        // The type stays a sentinel until a conversion gives it the one the context wants.
        return new BoundThrowExpression(syntax.Position, Conversions.ThrowType, exception);
    }

    /// <summary>
    /// <c>nameof(x)</c> is the last identifier of its operand. A bare name is checked against
    /// what is in scope; a member access is not, because resolving <c>T.Member</c> without
    /// evaluating it needs a lookup mode the binder does not have.
    /// </summary>
    private BoundExpression BindNameOf(NameOfExpressionSyntax syntax)
    {
        switch (syntax.Operand)
        {
            case NameExpressionSyntax simple:
            {
                if (!NameExists(simple.Name))
                {
                    return Fail(syntax.Position, ErrorCode.UndefinedName,
                        $"nameof 的操作数 '{simple.Name}' 不是已知的名称。");
                }

                return new BoundLiteral(syntax.Position, typeof(string), simple.Name);
            }

            case MemberAccessExpressionSyntax member:
                return new BoundLiteral(syntax.Position, typeof(string), member.MemberName);

            default:
                return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                    "nameof 的操作数必须是名称或成员访问。");
        }
    }

    private bool NameExists(string name)
    {
        if (_scope.TryLookup(name, out _)) return true;
        if (_resolver.Resolve(new TypeSyntax(default, [name], [], false, 0)) is not null) return true;
        if (_globals is null) return false;

        return MemberCache.Property(_globals.Type, InstanceFlags, name) is not null ||
               MemberCache.Field(_globals.Type, InstanceFlags, name) is not null ||
               MemberCache.MethodsNamed(_globals.Type, InstanceFlags, name).Length > 0;
    }

    // ============================================================ arrays

    private BoundExpression BindArrayCreation(ArrayCreationExpressionSyntax syntax)
    {
        // new T[a, b]
        if (syntax.Lengths is not null)
        {
            var elementType = ResolveType(syntax.ElementType!);
            if (elementType is null) return new BoundErrorExpression(syntax.Position);

            var lengths = new BoundExpression[syntax.Lengths.Count];
            for (var i = 0; i < lengths.Length; i++)
            {
                var length = BindExpression(syntax.Lengths[i]);
                if (length is BoundErrorExpression) return length;

                lengths[i] = Convert(length, typeof(int), syntax.Position, explicitCast: false);
            }

            return new BoundNewArray(syntax.Position, elementType, lengths);
        }

        var elements = syntax.Elements!.Select(BindExpression).ToArray();
        if (elements.Any(e => e is BoundErrorExpression)) return new BoundErrorExpression(syntax.Position);

        // new T[] { ... }
        if (syntax.ElementType is not null)
        {
            var declared = ResolveType(syntax.ElementType);
            return declared is null
                ? new BoundErrorExpression(syntax.Position)
                : MakeArray(syntax.Position, declared, elements);
        }

        // new[] { ... } — the element type is the best common type of the elements, ignoring the
        // ones that have no type of their own.
        Type? common = null;
        foreach (var element in elements)
        {
            if (element is BoundNullLiteral or BoundUnboundLambda) continue;

            common = common is null ? element.Type : BestCommonType(common, element.Type);
            if (common is null) break;
        }

        if (common is null)
        {
            return Fail(syntax.Position, ErrorCode.CannotInferType,
                "无法从元素推断 new[] 的元素类型，请写成 new T[] { ... }。");
        }

        return MakeArray(syntax.Position, common, elements);
    }

    private BoundExpression MakeArray(
        SourcePosition position,
        Type elementType,
        IReadOnlyList<BoundExpression> elements)
    {
        var converted = new BoundExpression[elements.Count];
        for (var i = 0; i < elements.Count; i++)
            converted[i] = Convert(elements[i], elementType, elements[i].Position, explicitCast: false);

        return new BoundArrayCreation(position, elementType, converted);
    }

    // ============================================================ interpolated strings

    /// <summary>One piece of an interpolated string after binding.</summary>
    private readonly record struct InterpolationPart(
        string? Text,
        BoundExpression? Value,
        string? Alignment,
        string? Format)
    {
        public bool IsHole => Value is not null;
    }

    /// <summary>
    /// Lowers <c>$"a{x}b"</c>. Without alignment or format specifiers it becomes a
    /// <c>string.Concat</c> call, which is what the C# compiler emits too; with them it becomes
    /// a <c>string.Format</c> against a composite format string built here.
    /// </summary>
    private BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax)
    {
        var parts = new List<InterpolationPart>(syntax.Parts.Count);

        foreach (var part in syntax.Parts)
        {
            if (!part.IsHole)
            {
                parts.Add(new InterpolationPart(part.Text, null, null, null));
                continue;
            }

            var value = BindExpression(part.Value!);
            if (value is BoundErrorExpression) return value;

            if (value is BoundUnboundLambda)
                return Fail(part.Position, ErrorCode.CannotConvert, "lambda 不能直接出现在插值项中。");

            parts.Add(new InterpolationPart(null, value, part.Alignment, part.Format));
        }

        var needsFormat = parts.Any(p => p.IsHole && (p.Alignment is not null || p.Format is not null));

        return needsFormat
            ? BindInterpolationAsFormat(syntax.Position, parts)
            : BindInterpolationAsConcat(syntax.Position, parts);
    }

    private BoundExpression BindInterpolationAsConcat(SourcePosition position, List<InterpolationPart> parts)
    {
        var operands = new List<BoundExpression>(parts.Count);

        foreach (var part in parts)
        {
            if (part.IsHole)
            {
                operands.Add(part.Value!);
                continue;
            }

            if (part.Text!.Length == 0) continue;

            // Adjacent literal runs fold now, so `$"a{x}"` needs one Concat rather than two.
            if (operands.Count > 0 && operands[^1] is BoundLiteral { Value: string previous } last &&
                last.Type == typeof(string))
            {
                operands[^1] = last with { Value = previous + part.Text };
                continue;
            }

            operands.Add(new BoundLiteral(position, typeof(string), part.Text));
        }

        if (operands.Count == 0) return new BoundLiteral(position, typeof(string), string.Empty);

        if (operands.Count == 1 && operands[0].Type == typeof(string)) return operands[0];

        var parameterType = operands.All(o => o.Type == typeof(string)) ? typeof(string) : typeof(object);
        var arguments = operands.Select(o => ConvertForConcat(o, parameterType, position)).ToArray();

        var method = FindConcat(parameterType, arguments.Length);
        if (method is not null) return new BoundCall(position, null, method, arguments);

        // More operands than any fixed overload takes: pass them as one array.
        var array = new BoundArrayCreation(position, parameterType, arguments);
        var arrayMethod = typeof(string).GetMethod(nameof(string.Concat), [parameterType.MakeArrayType()])!;

        return new BoundCall(position, null, arrayMethod, [array]);
    }

    private static MethodInfo? FindConcat(Type parameterType, int count)
    {
        if (count == 1)
        {
            return parameterType == typeof(object)
                ? typeof(string).GetMethod(nameof(string.Concat), [typeof(object)])
                : null;
        }

        if (count > 4 || (count == 4 && parameterType != typeof(string))) return null;

        return typeof(string).GetMethod(
            nameof(string.Concat), [.. Enumerable.Repeat(parameterType, count)]);
    }

    private BoundExpression BindInterpolationAsFormat(SourcePosition position, List<InterpolationPart> parts)
    {
        var format = new StringBuilder();
        var holes = new List<BoundExpression>();

        foreach (var part in parts)
        {
            if (!part.IsHole)
            {
                // Literal braces have to survive as themselves through string.Format.
                foreach (var c in part.Text!)
                {
                    if (c is '{' or '}') format.Append(c);
                    format.Append(c);
                }
                continue;
            }

            format.Append('{').Append(holes.Count);
            if (part.Alignment is { Length: > 0 }) format.Append(',').Append(part.Alignment);
            if (part.Format is not null) format.Append(':').Append(part.Format);
            format.Append('}');

            holes.Add(Convert(part.Value!, typeof(object), position, explicitCast: false));
        }

        var formatLiteral = new BoundLiteral(position, typeof(string), format.ToString());

        var fixedOverload = holes.Count is >= 1 and <= 3
            ? typeof(string).GetMethod(
                nameof(string.Format), [typeof(string), .. Enumerable.Repeat(typeof(object), holes.Count)])
            : null;

        if (fixedOverload is not null)
            return new BoundCall(position, null, fixedOverload, [formatLiteral, .. holes]);

        var array = new BoundArrayCreation(position, typeof(object), holes);
        var method = typeof(string).GetMethod(nameof(string.Format), [typeof(string), typeof(object[])])!;

        return new BoundCall(position, null, method, [formatLiteral, array]);
    }
}
