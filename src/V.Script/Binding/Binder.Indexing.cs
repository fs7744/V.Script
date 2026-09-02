using System.Reflection;
using System.Runtime.CompilerServices;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// <c>^e</c>, <c>a..b</c>, and what indexing with them means. Also <c>with</c>, which shares the
/// initializer machinery.
/// </summary>
/// <remarks>
/// Rather than special-casing every collection shape, an <c>Index</c> or <c>Range</c> used as a
/// subscript is turned into ordinary integers at run time — <c>Index.GetOffset(length)</c> and
/// <c>Range.GetOffsetAndLength(length)</c> — and the existing indexing path takes it from there.
/// A type that declares its own <c>Index</c>/<c>Range</c> indexer is used directly instead.
/// </remarks>
internal sealed partial class Binder
{
    private static readonly ConstructorInfo IndexConstructor =
        typeof(Index).GetConstructor([typeof(int), typeof(bool)])!;

    private static readonly MethodInfo IndexGetOffset =
        typeof(Index).GetMethod(nameof(Index.GetOffset), [typeof(int)])!;

    private static readonly ConstructorInfo RangeConstructor =
        typeof(Range).GetConstructor([typeof(Index), typeof(Index)])!;

    private static readonly MethodInfo RangeGetOffsetAndLength =
        typeof(Range).GetMethod(nameof(Range.GetOffsetAndLength), [typeof(int)])!;

    private static readonly MethodInfo GetSubArray =
        typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetSubArray))!;

    // ============================================================ ^e and a..b

    private BoundExpression BindFromEnd(FromEndExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return operand;

        return new BoundObjectCreation(syntax.Position, IndexConstructor,
        [
            Convert(operand, typeof(int), syntax.Position, explicitCast: false),
            new BoundLiteral(syntax.Position, typeof(bool), true),
        ]);
    }

    private BoundExpression BindRange(RangeExpressionSyntax syntax)
    {
        var start = BindRangeEnd(syntax.Start, syntax.Position, fromEnd: false);
        var end = BindRangeEnd(syntax.End, syntax.Position, fromEnd: true);

        if (start is BoundErrorExpression) return start;
        if (end is BoundErrorExpression) return end;

        return new BoundObjectCreation(syntax.Position, RangeConstructor, [start, end]);
    }

    /// <summary>An absent end is index 0 from that side, which is what <c>Range.All</c> is made of.</summary>
    private BoundExpression BindRangeEnd(ExpressionSyntax? syntax, SourcePosition position, bool fromEnd)
    {
        if (syntax is null)
        {
            return new BoundObjectCreation(position, IndexConstructor,
            [
                new BoundLiteral(position, typeof(int), 0),
                new BoundLiteral(position, typeof(bool), fromEnd),
            ]);
        }

        var value = BindExpression(syntax);
        if (value is BoundErrorExpression) return value;

        if (value.Type == typeof(Index)) return value;

        return new BoundObjectCreation(position, IndexConstructor,
        [
            Convert(value, typeof(int), position, explicitCast: false),
            new BoundLiteral(position, typeof(bool), false),
        ]);
    }

    // ============================================================ using them as a subscript

    /// <summary>
    /// Handles <c>a[^1]</c> and <c>a[1..2]</c>, or returns null when the subscript is an
    /// ordinary one and the normal path should deal with it.
    /// </summary>
    private BoundExpression? TryBindIndexOrRangeAccess(
        BoundExpression receiver,
        IReadOnlyList<BoundExpression> arguments,
        SourcePosition position)
    {
        if (arguments.Count != 1) return null;

        var argument = arguments[0];
        if (argument.Type != typeof(Index) && argument.Type != typeof(Range)) return null;

        // A type that indexes by Index/Range itself knows best.
        if (!receiver.Type.IsArray && FindIndexerTaking(receiver.Type, argument.Type) is { } declared)
            return new BoundIndexerAccess(position, receiver, declared, [argument]);

        if (LengthMemberOf(receiver.Type) is not PropertyInfo length)
        {
            return Fail(position, ErrorCode.NotIndexable,
                $"{TypeResolver.Display(receiver.Type)} 没有 Length / Count，不能用 ^ 或 .. 索引。");
        }

        // The receiver is read twice — once for its length — so it has to be repeatable.
        var effects = new List<BoundExpression>();
        var subject = receiver;

        if (!IsRepeatable(receiver))
        {
            var temp = MakeTemp(receiver.Type);
            var slot = new BoundLocalAccess(position, temp);
            effects.Add(new BoundAssignment(position, slot, receiver));
            subject = slot;
        }

        var size = new BoundPropertyAccess(position, subject, length);

        var result = argument.Type == typeof(Index)
            ? BindIndexAccessAt(subject, argument, size, position)
            : BindRangeSlice(subject, argument, size, position);

        if (result is BoundErrorExpression || effects.Count == 0) return result;

        return new BoundSequence(position, result.Type, effects, result);
    }

    private BoundExpression BindIndexAccessAt(
        BoundExpression subject,
        BoundExpression index,
        BoundExpression length,
        SourcePosition position)
    {
        var offset = new BoundCall(position, index, IndexGetOffset, [length]);

        return subject.Type.IsArray
            ? new BoundArrayAccess(position, subject.Type.GetElementType()!, subject, offset)
            : BindIndexAccessCore(subject, [offset], position);
    }

    private BoundExpression BindRangeSlice(
        BoundExpression subject,
        BoundExpression range,
        BoundExpression length,
        SourcePosition position)
    {
        // An array slices by copying, which is what the C# compiler emits too.
        if (subject.Type.IsArray)
        {
            var element = subject.Type.GetElementType()!;
            return new BoundCall(position, null, GetSubArray.MakeGenericMethod(element), [subject, range]);
        }

        var slice = subject.Type.GetMethod("Slice", [typeof(int), typeof(int)])
            ?? subject.Type.GetMethod("Substring", [typeof(int), typeof(int)]);

        if (slice is null)
        {
            return Fail(position, ErrorCode.NotIndexable,
                $"{TypeResolver.Display(subject.Type)} 没有 Slice / Substring，不能用 .. 切片。");
        }

        // GetOffsetAndLength returns both numbers at once, so the range is only decoded once.
        var pair = MakeTemp(typeof(ValueTuple<int, int>));
        var pairAccess = new BoundLocalAccess(position, pair);

        var store = new BoundAssignment(position, pairAccess,
            new BoundCall(position, range, RangeGetOffsetAndLength, [length]));

        var call = new BoundCall(position, subject, slice,
        [
            new BoundFieldAccess(position, pairAccess, pair.Type.GetField("Item1")!),
            new BoundFieldAccess(position, pairAccess, pair.Type.GetField("Item2")!),
        ]);

        return new BoundSequence(position, call.Type, [store], call);
    }

    private static PropertyInfo? FindIndexerTaking(Type type, Type argumentType)
    {
        foreach (var property in FindIndexers(type))
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == argumentType) return property;
        }

        return null;
    }

    // ============================================================ with

    /// <summary>
    /// <c>r with { X = 1 }</c> clones and then assigns. A record's clone is the compiler-made
    /// <c>&lt;Clone&gt;$</c>; a struct copies by assignment, which is the same thing for it.
    /// </summary>
    private BoundExpression BindWith(WithExpressionSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        if (target is BoundErrorExpression) return target;

        BoundExpression clone;

        if (target.Type.IsValueType)
        {
            clone = target;
        }
        else
        {
            var cloneMethod = target.Type.GetMethod(
                "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (cloneMethod is null)
            {
                return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                    $"{TypeResolver.Display(target.Type)} 不是 record，也不是值类型，不能用 with。");
            }

            clone = new BoundCall(syntax.Position, target, cloneMethod, []);
        }

        var temp = MakeTemp(target.Type);
        var copy = new BoundLocalAccess(syntax.Position, temp);

        var effects = new List<BoundExpression>
        {
            new BoundAssignment(syntax.Position, copy, clone),
        };

        if (!ApplyInitializer(copy, syntax.Initializer, effects))
            return new BoundErrorExpression(syntax.Position);

        return new BoundSequence(syntax.Position, target.Type, effects, copy);
    }
}
