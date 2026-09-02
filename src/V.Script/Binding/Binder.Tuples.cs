using System.Reflection;
using System.Runtime.CompilerServices;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// Tuples and deconstruction. A tuple is a <c>ValueTuple&lt;...&gt;</c> and nothing more; the
/// element names exist only at compile time, exactly as in C#, which is why they are tracked
/// beside the type rather than in it.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>
    /// How many elements one <c>ValueTuple</c> holds before it has to nest. The eighth type
    /// argument is <c>TRest</c>, another tuple, which is how longer tuples are built.
    /// </summary>
    private const int TupleChunk = 7;

    /// <summary>Where a longer tuple stops being worth writing rather than being impossible.</summary>
    private const int MaxTupleArity = 64;

    // ============================================================ tuple types and names

    internal static bool IsTupleType(Type type) =>
        type.IsGenericType &&
        type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true;

    private static Type? MakeTupleType(IReadOnlyList<Type> elementTypes) =>
        elementTypes.Count is < 2 or > MaxTupleArity ? null : MakeTupleTypeCore(elementTypes);

    /// <summary>
    /// The same construction without the "at least two" rule: a nested rest may hold a single
    /// element, which is what an eight-element tuple ends in.
    /// </summary>
    private static Type? MakeTupleTypeCore(IReadOnlyList<Type> elementTypes)
    {
        if (elementTypes.Count <= TupleChunk)
        {
            var definition = Type.GetType($"System.ValueTuple`{elementTypes.Count}");
            return definition?.MakeGenericType([.. elementTypes]);
        }

        var rest = MakeTupleTypeCore([.. elementTypes.Skip(TupleChunk)]);
        if (rest is null) return null;

        return typeof(ValueTuple<,,,,,,,>).MakeGenericType([.. elementTypes.Take(TupleChunk), rest]);
    }

    /// <summary>The number of elements a tuple type holds, counting through its nested rest.</summary>
    private static int TupleArity(Type type)
    {
        var arguments = type.GetGenericArguments();
        if (arguments.Length < 8) return arguments.Length;

        return TupleChunk + TupleArity(arguments[7]);
    }


    /// <summary>
    /// Reads element <paramref name="index"/> (zero-based). Past the seventh it lives in the
    /// nested <c>Rest</c>, so the access walks down one tuple at a time.
    /// </summary>
    private static BoundExpression TupleElementAccess(BoundExpression tuple, int index, SourcePosition position)
    {
        while (index >= TupleChunk)
        {
            tuple = new BoundFieldAccess(position, tuple, tuple.Type.GetField("Rest")!);
            index -= TupleChunk;
        }

        return new BoundFieldAccess(position, tuple, tuple.Type.GetField($"Item{index + 1}")!);
    }

    /// <summary>
    /// The element names an expression carries, or null when it has none. A name comes from the
    /// literal that produced the tuple, from the declared type of the local holding it, or from
    /// the <c>TupleElementNamesAttribute</c> the compiler put on a member.
    /// </summary>
    private static IReadOnlyList<string?>? TupleNamesOf(BoundExpression expression) => expression switch
    {
        BoundTupleLiteral literal => literal.Names,
        BoundLocalAccess local => local.Local.TupleNames,
        BoundParameterAccess => null,
        BoundConversion conversion => TupleNamesOf(conversion.Operand),
        BoundSequence sequence => TupleNamesOf(sequence.Value),
        BoundFieldAccess field => NamesFromAttribute(field.Field),
        BoundPropertyAccess property => NamesFromAttribute(property.Property),
        BoundCall call => NamesFromAttribute(call.Method.ReturnParameter),
        _ => null,
    };

    private static IReadOnlyList<string?>? NamesFromAttribute(ICustomAttributeProvider member)
    {
        var attribute = member
            .GetCustomAttributes(typeof(TupleElementNamesAttribute), inherit: false)
            .OfType<TupleElementNamesAttribute>()
            .FirstOrDefault();

        return attribute?.TransformNames as IReadOnlyList<string?>;
    }

    /// <summary>
    /// Resolves <c>t.name</c> against the tuple's element names, or returns null when the
    /// receiver is not a tuple or the name is not one of its elements.
    /// </summary>
    private static BoundExpression? TupleElementFor(BoundExpression receiver, string name, SourcePosition position)
    {
        if (!IsTupleType(receiver.Type)) return null;

        var arity = TupleArity(receiver.Type);

        // `ItemN` past the seventh is not a real field, but C# still accepts it and walks Rest.
        if (name.StartsWith("Item", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(4), out var ordinal) &&
            ordinal > TupleChunk && ordinal <= arity)
        {
            return TupleElementAccess(receiver, ordinal - 1, position);
        }

        var names = TupleNamesOf(receiver);
        if (names is null) return null;

        for (var i = 0; i < names.Count && i < arity; i++)
            if (names[i] == name)
                return TupleElementAccess(receiver, i, position);

        return null;
    }

    // ============================================================ tuple literals

    private BoundExpression BindTupleExpression(TupleExpressionSyntax syntax)
    {
        if (syntax.Elements.Count is < 2 or > MaxTupleArity)
        {
            return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                $"元组需要 2 到 {MaxTupleArity} 个元素，实际 {syntax.Elements.Count} 个。");
        }

        var elements = new BoundExpression[syntax.Elements.Count];
        var names = new string?[syntax.Elements.Count];

        for (var i = 0; i < elements.Length; i++)
        {
            var element = syntax.Elements[i];
            var value = BindExpression(element.Value);
            if (value is BoundErrorExpression) return value;

            if (value.Type == typeof(void) || Conversions.IsUntyped(value.Type))
            {
                return Fail(element.Position, ErrorCode.CannotInferType,
                    "元组元素必须有确定的类型；请写出显式类型或强制转换。");
            }

            elements[i] = value;
            names[i] = element.Name ?? InferredElementName(element.Value);
        }

        var type = MakeTupleType([.. elements.Select(e => e.Type)])!;
        return MakeTupleLiteral(syntax.Position, type, elements, names);
    }

    /// <summary>
    /// Builds the literal, nesting the elements past the seventh into a tuple of their own —
    /// which is exactly the shape <c>ValueTuple</c>'s eighth type argument expects.
    /// </summary>
    private static BoundTupleLiteral MakeTupleLiteral(
        SourcePosition position,
        Type type,
        IReadOnlyList<BoundExpression> elements,
        IReadOnlyList<string?> names)
    {
        if (elements.Count <= TupleChunk)
            return new BoundTupleLiteral(position, type, type.GetConstructors()[0], elements, names);

        var restType = type.GetGenericArguments()[7];
        var rest = MakeTupleLiteral(position, restType,
            [.. elements.Skip(TupleChunk)], [.. names.Skip(TupleChunk)]);

        IReadOnlyList<BoundExpression> outer = [.. elements.Take(TupleChunk), rest];

        return new BoundTupleLiteral(position, type, type.GetConstructors()[0], outer, names);
    }

    /// <summary>
    /// C# takes the element name from the expression when it is a plain name or member access,
    /// so <c>(x, p.Name)</c> has elements called <c>x</c> and <c>Name</c>.
    /// </summary>
    private static string? InferredElementName(ExpressionSyntax syntax) => syntax switch
    {
        NameExpressionSyntax name => name.Name,
        MemberAccessExpressionSyntax member => member.MemberName,
        _ => null,
    };

    // ============================================================ deconstruction

    private BoundStatement BindDeconstruction(DeconstructionStatementSyntax syntax)
    {
        var value = BindExpression(syntax.Value);
        if (value is BoundErrorExpression) return new BoundNop(syntax.Position);

        var parts = DeconstructInto(value, syntax.Targets.Count, syntax.Position, out var prologue);
        if (parts is null) return new BoundNop(syntax.Position);

        var statements = new List<BoundStatement>(prologue);

        for (var i = 0; i < syntax.Targets.Count; i++)
        {
            var target = syntax.Targets[i];

            // A bare name assigns to a variable that already exists.
            if (!target.Declares)
            {
                var existing = BindExpression(new NameExpressionSyntax(target.Position, target.Name));
                if (existing is BoundErrorExpression) continue;

                if (!IsAssignable(existing))
                {
                    _diagnostics.Report(ErrorCode.NotAssignable, target.Position,
                        $"'{target.Name}' 不是可以赋值的目标。");
                    continue;
                }

                statements.Add(new BoundExpressionStatement(target.Position,
                    new BoundAssignment(target.Position, existing,
                        Convert(parts[i], existing.Type, target.Position, explicitCast: false))));

                continue;
            }

            var declaredType = target.Type is null ? parts[i].Type : ResolveType(target.Type);
            if (declaredType is null) continue;

            var local = new LocalSymbol(target.Name, declaredType)
            {
                TupleNames = target.Type?.TupleNames ?? TupleNamesOf(parts[i]),
            };

            if (!DeclareLocal(local, target.Position)) continue;

            statements.Add(new BoundLocalDeclaration(target.Position, local,
                Convert(parts[i], declaredType, target.Position, explicitCast: false)));
        }

        return new BoundBlock(syntax.Position, statements, null);
    }

    /// <summary>
    /// Assigning to a tuple of existing variables: <c>(a, b) = (b, a)</c>. The right side is
    /// read into temporaries first, which is what makes a swap work.
    /// </summary>
    private BoundExpression BindTupleAssignment(TupleExpressionSyntax targets, ExpressionSyntax valueSyntax)
    {
        var value = BindExpression(valueSyntax);
        if (value is BoundErrorExpression) return value;

        var parts = DeconstructInto(value, targets.Elements.Count, targets.Position, out var prologue);
        if (parts is null) return new BoundErrorExpression(targets.Position);

        var effects = new List<BoundExpression>();
        foreach (var statement in prologue)
            effects.Add(((BoundExpressionStatement)statement).Expression);

        // Every element is read into its own temp before anything is written back.
        var staged = new List<BoundExpression>(parts.Count);
        foreach (var part in parts)
        {
            var temp = MakeTemp(part.Type);
            var access = new BoundLocalAccess(targets.Position, temp);
            effects.Add(new BoundAssignment(targets.Position, access, part));
            staged.Add(access);
        }

        for (var i = 0; i < targets.Elements.Count; i++)
        {
            var element = targets.Elements[i];
            if (element.Name is not null)
            {
                return Fail(element.Position, ErrorCode.NotAssignable,
                    "赋值目标不能带元素名。");
            }

            var target = BindExpression(element.Value);
            if (target is BoundErrorExpression) return target;

            if (!IsAssignable(target))
                return Fail(element.Position, ErrorCode.NotAssignable, "解构赋值的目标不是变量、属性或索引器。");

            effects.Add(new BoundAssignment(element.Position, target,
                Convert(staged[i], target.Type, element.Position, explicitCast: false)));
        }

        var last = effects[^1];
        effects.RemoveAt(effects.Count - 1);

        return new BoundSequence(targets.Position, last.Type, effects, last);
    }

    /// <summary>
    /// Produces one expression per element. A tuple is read field by field; anything else needs
    /// a matching <c>Deconstruct</c> method, whose <c>out</c> arguments become temporaries that
    /// <paramref name="prologue"/> fills in.
    /// </summary>
    private List<BoundExpression>? DeconstructInto(
        BoundExpression value,
        int count,
        SourcePosition position,
        out List<BoundStatement> prologue)
    {
        prologue = [];

        if (IsTupleType(value.Type))
        {
            var arity = TupleArity(value.Type);
            if (arity != count)
            {
                Fail(position, ErrorCode.WrongArgumentCount,
                    $"元组有 {arity} 个元素，但解构写了 {count} 个。");
                return null;
            }

            // Read the tuple once; the fields are then plain reads off a local.
            var source = value;
            if (!IsRepeatable(value))
            {
                var temp = MakeTemp(value.Type);
                var access = new BoundLocalAccess(position, temp);
                prologue.Add(new BoundExpressionStatement(position,
                    new BoundAssignment(position, access, value)));

                temp.TupleNames = TupleNamesOf(value);
                source = access;
            }

            var fields = new List<BoundExpression>(count);
            for (var i = 0; i < count; i++) fields.Add(TupleElementAccess(source, i, position));

            return fields;
        }

        return DeconstructViaMethod(value, count, position, prologue);
    }

    private List<BoundExpression>? DeconstructViaMethod(
        BoundExpression value,
        int count,
        SourcePosition position,
        List<BoundStatement> prologue)
    {
        var method = value.Type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Deconstruct" &&
                                 m.ReturnType == typeof(void) &&
                                 m.GetParameters().Length == count &&
                                 m.GetParameters().All(p => p.IsOut));

        if (method is null)
        {
            Fail(position, ErrorCode.UndefinedMember,
                $"{TypeResolver.Display(value.Type)} 不是元组，也没有接受 {count} 个 out 参数的 Deconstruct 方法。");
            return null;
        }

        var results = new List<BoundExpression>(count);
        var arguments = new List<BoundExpression>(count);

        foreach (var parameter in method.GetParameters())
        {
            var temp = MakeTemp(parameter.ParameterType.GetElementType()!);
            arguments.Add(new BoundLocalAddress(position, temp));
            results.Add(new BoundLocalAccess(position, temp));
        }

        prologue.Add(new BoundExpressionStatement(position,
            new BoundCall(position, value, method, arguments)));

        return results;
    }
}
