using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// Positional and list patterns. Both take the subject apart and then match the pieces with
/// ordinary patterns, so almost all of the work is producing the pieces — which is the same job
/// deconstruction and indexing already do.
/// </summary>
internal sealed partial class Binder
{
    private BoundExpression BindPositionalPattern(
        BoundExpression subject,
        PositionalPatternSyntax syntax,
        out BoundExpression narrowed)
    {
        var position = syntax.Position;
        var prelude = new List<BoundExpression>();
        BoundExpression test = Literal(position, true);
        var effective = subject;

        narrowed = subject;

        if (syntax.Type is not null)
        {
            var type = ResolveType(syntax.Type);
            if (type is null) return new BoundErrorExpression(position);

            test = BindTypeTest(subject, type, designation: null, position, out effective);
        }
        else if (!IsRepeatable(subject))
        {
            var temp = MakeTemp(subject.Type);
            prelude.Add(new BoundAssignment(position, new BoundLocalAccess(position, temp), subject));
            effective = new BoundLocalAccess(position, temp);
        }

        var parts = DeconstructInto(effective, syntax.Subpatterns.Count, position, out var statements);
        if (parts is null) return new BoundErrorExpression(position);

        // Deconstruct calls have to run before the first element is looked at.
        foreach (var statement in statements)
            prelude.Add(((BoundExpressionStatement)statement).Expression);

        for (var i = 0; i < syntax.Subpatterns.Count; i++)
        {
            var subpattern = syntax.Subpatterns[i];

            if (subpattern.Name is not null && TupleNamesOf(effective) is { } names)
            {
                var index = -1;
                for (var n = 0; n < names.Count; n++)
                    if (names[n] == subpattern.Name)
                        index = n;

                if (index < 0)
                {
                    return Fail(subpattern.Position, ErrorCode.UndefinedMember,
                        $"元组没有名为 '{subpattern.Name}' 的元素。");
                }

                if (index != i)
                {
                    return Fail(subpattern.Position, ErrorCode.ConstructNotSupported,
                        $"位置模式中的元素名 '{subpattern.Name}' 必须与它所在的位置一致。");
                }
            }

            var elementTest = BindPatternAgainst(parts[i], subpattern.Pattern, subpattern.Position);
            test = new BoundLogical(position, test, elementTest, IsAnd: true);
        }

        foreach (var property in syntax.Properties)
        {
            var member = BindInstanceMember(position, effective, property.Name);
            if (member is BoundErrorExpression) return member;

            test = new BoundLogical(position, test,
                BindPatternAgainst(member, property.Pattern, property.Position), IsAnd: true);
        }

        test = AppendDesignation(test, effective, syntax.Designation, position);

        narrowed = effective;
        return prelude.Count == 0
            ? test
            : new BoundSequence(position, typeof(bool), prelude, test);
    }

    // ============================================================ list patterns

    /// <summary>
    /// <c>[a, b]</c> tests the length and then each element. With a slice the length becomes a
    /// minimum, and the patterns after the slice count back from the end.
    /// </summary>
    private BoundExpression BindListPattern(
        BoundExpression subject,
        ListPatternSyntax syntax,
        out BoundExpression narrowed)
    {
        var position = syntax.Position;
        narrowed = subject;

        if (LengthMemberOf(subject.Type) is not { } lengthMember)
        {
            return Fail(position, ErrorCode.NotIndexable,
                $"{TypeResolver.Display(subject.Type)} 没有 Length / Count，不能用列表模式匹配。");
        }

        // An array indexes without a property; anything else needs an int indexer.
        var indexer = IndexerOf(subject.Type);
        if (!subject.Type.IsArray && indexer is null)
        {
            return Fail(position, ErrorCode.NotIndexable,
                $"{TypeResolver.Display(subject.Type)} 不能按 int 索引，不能用列表模式匹配。");
        }

        var prelude = new List<BoundExpression>();
        var effective = subject;

        if (!IsRepeatable(subject))
        {
            var temp = MakeTemp(subject.Type);
            prelude.Add(new BoundAssignment(position, new BoundLocalAccess(position, temp), subject));
            effective = new BoundLocalAccess(position, temp);
        }

        BoundExpression test = Conversions.IsNullAssignable(effective.Type)
            ? MakeNullTest(effective, testingForNull: false, position)
            : Literal(position, true);

        // The length is read once and kept, because the tail patterns index relative to it.
        var lengthLocal = MakeTemp(typeof(int));
        var length = new BoundLocalAccess(position, lengthLocal);

        prelude.Add(new BoundAssignment(position, length, ReadLength(effective, lengthMember, position)));

        var required = syntax.Before.Count + syntax.After.Count;
        var lengthTest = new BoundBinary(position, typeof(bool),
            syntax.HasSlice ? BoundBinaryKind.GreaterEqual : BoundBinaryKind.Equal,
            length, new BoundLiteral(position, typeof(int), required), IsLifted: false, Method: null);

        test = new BoundLogical(position, test, lengthTest, IsAnd: true);

        for (var i = 0; i < syntax.Before.Count; i++)
        {
            var element = MakeElement(effective, indexer, new BoundLiteral(position, typeof(int), i), position);
            test = new BoundLogical(position, test,
                BindPatternAgainst(element, syntax.Before[i], syntax.Before[i].Position), IsAnd: true);
        }

        for (var i = 0; i < syntax.After.Count; i++)
        {
            // Counting back from the end: length - (After.Count - i).
            var offset = new BoundBinary(position, typeof(int), BoundBinaryKind.Subtract,
                length, new BoundLiteral(position, typeof(int), syntax.After.Count - i),
                IsLifted: false, Method: null);

            var element = MakeElement(effective, indexer, offset, position);
            test = new BoundLogical(position, test,
                BindPatternAgainst(element, syntax.After[i], syntax.After[i].Position), IsAnd: true);
        }

        if (syntax.SliceDesignation is not null)
        {
            var slice = BindSlice(effective, length, syntax, position);
            if (slice is BoundErrorExpression) return slice;

            test = new BoundLogical(position, test, slice, IsAnd: true);
        }

        test = AppendDesignation(test, effective, syntax.Designation, position);

        return prelude.Count == 0
            ? test
            : new BoundSequence(position, typeof(bool), prelude, test);
    }

    /// <summary>Binds <c>..var rest</c> by calling the subject's own slicing method.</summary>
    private BoundExpression BindSlice(
        BoundExpression subject,
        BoundExpression length,
        ListPatternSyntax syntax,
        SourcePosition position)
    {
        var start = new BoundLiteral(position, typeof(int), syntax.Before.Count);
        var count = new BoundBinary(position, typeof(int), BoundBinaryKind.Subtract,
            new BoundBinary(position, typeof(int), BoundBinaryKind.Subtract,
                length, start, IsLifted: false, Method: null),
            new BoundLiteral(position, typeof(int), syntax.After.Count),
            IsLifted: false, Method: null);

        BoundExpression slice;

        if (subject.Type.IsArray)
        {
            var method = typeof(ArraySegment<>)
                .MakeGenericType(subject.Type.GetElementType()!)
                .GetConstructor([subject.Type, typeof(int), typeof(int)])!;

            slice = new BoundObjectCreation(position, method, [subject, start, count]);
        }
        else
        {
            var method = subject.Type.GetMethod("Slice", [typeof(int), typeof(int)])
                ?? subject.Type.GetMethod("GetRange", [typeof(int), typeof(int)]);

            if (method is null)
            {
                return Fail(position, ErrorCode.ConstructNotSupported,
                    $"{TypeResolver.Display(subject.Type)} 没有 Slice / GetRange，'..var' 只能用于数组或可切片的类型。");
            }

            slice = new BoundCall(position, subject, method, [start, count]);
        }

        var local = new LocalSymbol(syntax.SliceDesignation!, slice.Type);
        if (!DeclareLocal(local, position)) return new BoundErrorExpression(position);

        var store = new BoundAssignment(position, new BoundLocalAccess(position, local), slice);
        return new BoundSequence(position, typeof(bool), [store], Literal(position, true));
    }

    // ============================================================ shared helpers

    private static MemberInfo? LengthMemberOf(Type type)
    {
        if (type.IsArray) return type.GetProperty("Length");

        return (MemberInfo?)type.GetProperty("Length", InstanceFlags)
            ?? type.GetProperty("Count", InstanceFlags);
    }

    private static PropertyInfo? IndexerOf(Type type)
    {
        if (type.IsArray) return null; // arrays index without a property

        foreach (var property in type.GetProperties(InstanceFlags))
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int)) return property;
        }

        return null;
    }

    private static BoundExpression ReadLength(BoundExpression subject, MemberInfo member, SourcePosition position) =>
        new BoundPropertyAccess(position, subject, (PropertyInfo)member);

    private static BoundExpression MakeElement(
        BoundExpression subject,
        PropertyInfo? indexer,
        BoundExpression index,
        SourcePosition position) =>
        subject.Type.IsArray
            ? new BoundArrayAccess(position, subject.Type.GetElementType()!, subject, index)
            : new BoundIndexerAccess(position, subject, indexer!, [index]);

    /// <summary>Adds the <c>x</c> of <c>[1, 2] x</c>, which names the whole matched value.</summary>
    private BoundExpression AppendDesignation(
        BoundExpression test,
        BoundExpression value,
        string? designation,
        SourcePosition position)
    {
        if (designation is null) return test;

        var local = new LocalSymbol(designation, value.Type);
        if (!DeclareLocal(local, position)) return test;

        var store = new BoundAssignment(position, new BoundLocalAccess(position, local), value);

        return new BoundLogical(position, test,
            new BoundSequence(position, typeof(bool), [store], Literal(position, true)), IsAnd: true);
    }
}
