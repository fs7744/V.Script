using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// Pattern matching. Patterns are lowered to ordinary bound expressions — type tests, null
/// tests, comparisons and short-circuiting logic — so the emitter needs no pattern-specific
/// knowledge at all.
/// </summary>
internal sealed partial class Binder
{
    private static readonly MethodInfo NoMatchingSwitchArm =
        typeof(ScriptOperations).GetMethod(nameof(ScriptOperations.NoMatchingSwitchArm))!;

    private BoundExpression BindIs(IsExpressionSyntax syntax)
    {
        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return operand;

        if (operand.Type == typeof(void))
        {
            return Fail(syntax.Position, ErrorCode.CannotConvert,
                "'is' 的操作数没有值。");
        }

        return BindPatternAgainst(operand, syntax.Pattern, syntax.Position);
    }

    /// <summary>
    /// Tests <paramref name="subject"/> against a pattern, capturing it into a temp first when
    /// it is not safe to read more than once.
    /// </summary>
    private BoundExpression BindPatternAgainst(BoundExpression subject, PatternSyntax pattern, SourcePosition position)
    {
        if (IsRepeatable(subject)) return BindPattern(subject, pattern, out _);

        var temp = MakeTemp(subject.Type);
        var store = new BoundAssignment(position, new BoundLocalAccess(position, temp), subject);
        var test = BindPattern(new BoundLocalAccess(position, temp), pattern, out _);

        return new BoundSequence(position, typeof(bool), [store], test);
    }

    /// <summary>
    /// Binds one pattern. <paramref name="narrowed"/> reports the value as the pattern leaves
    /// it: a type pattern narrows to its target, which is what lets the right-hand side of
    /// <c>is int n and &gt; 1</c> compare an <c>int</c> rather than the original <c>object</c>.
    /// </summary>
    private BoundExpression BindPattern(BoundExpression subject, PatternSyntax pattern, out BoundExpression narrowed)
    {
        narrowed = subject;

        switch (pattern)
        {
            case ParenthesizedPatternSyntax parenthesized:
                return BindPattern(subject, parenthesized.Pattern, out narrowed);

            case DiscardPatternSyntax:
                return Literal(pattern.Position, true);

            case VarPatternSyntax variable:
                return BindVarPattern(subject, variable);

            case NotPatternSyntax negated:
            {
                // A negated pattern tells us nothing about the value, so nothing narrows.
                var inner = BindPattern(subject, negated.Pattern, out _);
                return new BoundUnary(pattern.Position, typeof(bool), BoundUnaryKind.LogicalNot,
                    inner, IsLifted: false, Method: null);
            }

            case BinaryPatternSyntax { IsAnd: true } conjunction:
            {
                var left = BindPattern(subject, conjunction.Left, out var afterLeft);
                var right = BindPattern(afterLeft, conjunction.Right, out narrowed);
                return new BoundLogical(pattern.Position, left, right, IsAnd: true);
            }

            case BinaryPatternSyntax disjunction:
            {
                // Alternatives may narrow differently, so the union narrows to nothing.
                var left = BindPattern(subject, disjunction.Left, out _);
                var right = BindPattern(subject, disjunction.Right, out _);
                return new BoundLogical(pattern.Position, left, right, IsAnd: false);
            }

            case ConstantPatternSyntax constant:
                return BindConstantPattern(subject, constant.Value, pattern.Position);

            case RelationalPatternSyntax relational:
                return BindRelationalPattern(subject, relational);

            case TypePatternSyntax typePattern:
                return BindTypePattern(subject, typePattern, out narrowed);

            case PropertyPatternSyntax propertyPattern:
                return BindPropertyPattern(subject, propertyPattern, out narrowed);

            default:
                return new BoundErrorExpression(pattern.Position);
        }
    }

    private static BoundLiteral Literal(SourcePosition position, bool value) =>
        new(position, typeof(bool), value);

    private BoundExpression BindVarPattern(BoundExpression subject, VarPatternSyntax syntax)
    {
        var local = new LocalSymbol(syntax.Designation, subject.Type);
        if (!DeclareLocal(local, syntax.Position)) return new BoundErrorExpression(syntax.Position);

        var store = new BoundAssignment(syntax.Position, new BoundLocalAccess(syntax.Position, local), subject);
        return new BoundSequence(syntax.Position, typeof(bool), [store], Literal(syntax.Position, true));
    }

    private BoundExpression BindConstantPattern(
        BoundExpression subject,
        ExpressionSyntax valueSyntax,
        SourcePosition position)
    {
        var value = BindExpression(valueSyntax);
        if (value is BoundErrorExpression) return value;

        if (value is BoundNullLiteral)
        {
            if (!Conversions.IsNullAssignable(subject.Type))
            {
                _diagnostics.Warn(ErrorCode.PatternNeverMatches, position,
                    $"{TypeResolver.Display(subject.Type)} 永远不为 null，该模式恒不匹配。");
                return Literal(position, false);
            }

            return MakeNullTest(subject, testingForNull: true, position);
        }

        return BindBinaryOperator(BoundBinaryKind.Equal, subject, value, position);
    }

    private BoundExpression BindRelationalPattern(BoundExpression subject, RelationalPatternSyntax syntax)
    {
        var value = BindExpression(syntax.Value);
        if (value is BoundErrorExpression) return value;

        var kind = MapBinaryKind(syntax.Operator);
        if (kind is null)
        {
            return Fail(syntax.Position, ErrorCode.OperatorNotDefined,
                $"模式中不支持运算符 '{syntax.Operator}'。");
        }

        return BindBinaryOperator(kind.Value, subject, value, syntax.Position);
    }

    /// <summary>
    /// A bare name is a type pattern when it names a type, and a constant pattern otherwise —
    /// which is how <c>is Order o</c> and <c>is Status.Active</c> both work.
    /// </summary>
    private BoundExpression BindTypePattern(
        BoundExpression subject,
        TypePatternSyntax syntax,
        out BoundExpression narrowed)
    {
        narrowed = subject;
        var type = _resolver.Resolve(syntax.Type);

        if (type is null)
        {
            if (syntax.MayBeConstant)
                return BindConstantPattern(subject, NameToExpression(syntax.Type), syntax.Position);

            _diagnostics.Report(ErrorCode.UnknownType, syntax.Position,
                $"找不到类型 '{syntax.Type.DisplayName}'。");
            return new BoundErrorExpression(syntax.Position);
        }

        return BindTypeTest(subject, type, syntax.Designation, syntax.Position, out narrowed);
    }

    /// <summary>Rebuilds a dotted name as a member-access expression, for the constant case.</summary>
    private static ExpressionSyntax NameToExpression(TypeSyntax syntax)
    {
        ExpressionSyntax expression = new NameExpressionSyntax(syntax.Position, syntax.NameParts[0]);

        for (var i = 1; i < syntax.NameParts.Count; i++)
            expression = new MemberAccessExpressionSyntax(
                syntax.Position, expression, syntax.NameParts[i], IsNullConditional: false);

        return expression;
    }

    /// <summary>
    /// Emits the run-time type test and binds the narrowed value to a local, which
    /// <paramref name="narrowed"/> reads back. A reference target uses <c>as</c> plus a null
    /// check; a value target boxes and uses <c>isinst</c>, which is also what makes
    /// <c>int? n is int v</c> behave correctly.
    /// </summary>
    private BoundExpression BindTypeTest(
        BoundExpression subject,
        Type type,
        string? designation,
        SourcePosition position,
        out BoundExpression narrowed)
    {
        LocalSymbol target;
        if (designation is null)
        {
            target = MakeTemp(type);
        }
        else
        {
            target = new LocalSymbol(designation, type);
            if (!DeclareLocal(target, position))
            {
                narrowed = new BoundErrorExpression(position);
                return new BoundErrorExpression(position);
            }
        }

        var access = new BoundLocalAccess(position, target);
        narrowed = access;

        if (!type.IsValueType)
        {
            var source = subject.Type.IsValueType && !Conversions.IsNullableValueType(subject.Type)
                ? Convert(subject, typeof(object), position, explicitCast: false)
                : subject;

            var store = new BoundAssignment(position, access, new BoundAsType(position, type, source));
            var test = MakeNullTest(access, testingForNull: false, position);

            return new BoundSequence(position, typeof(bool), [store], test);
        }

        // Value target: box once, test the box, then unbox into the pattern variable. The
        // variable keeps default(T) when the test fails; the engine does not model C#'s
        // definite-assignment rules, so reading it on the non-matching path is not an error.
        var boxed = MakeTemp(typeof(object));
        var boxedAccess = new BoundLocalAccess(position, boxed);
        var storeBoxed = new BoundAssignment(position, boxedAccess,
            Convert(subject, typeof(object), position, explicitCast: true));

        var isTest = new BoundIsType(position, boxedAccess, type);

        var assignNarrowed = new BoundAssignment(position, access,
            Convert(boxedAccess, type, position, explicitCast: true));

        var whenMatched = new BoundSequence(position, typeof(bool), [assignNarrowed], Literal(position, true));

        var conditional = new BoundConditional(position, typeof(bool), isTest,
            whenMatched, Literal(position, false));

        return new BoundSequence(position, typeof(bool), [storeBoxed], conditional);
    }

    private BoundExpression BindPropertyPattern(
        BoundExpression subject,
        PropertyPatternSyntax syntax,
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

            test = BindTypeTest(subject, type, syntax.Designation, position, out effective);
        }
        else
        {
            if (!IsRepeatable(subject))
            {
                var temp = MakeTemp(subject.Type);
                prelude.Add(new BoundAssignment(position, new BoundLocalAccess(position, temp), subject));
                effective = new BoundLocalAccess(position, temp);
            }

            // A property pattern never matches null.
            if (Conversions.IsNullAssignable(effective.Type))
                test = MakeNullTest(effective, testingForNull: false, position);

            if (syntax.Designation is not null)
            {
                var named = new LocalSymbol(syntax.Designation, effective.Type);
                if (DeclareLocal(named, position))
                {
                    var store = new BoundAssignment(position, new BoundLocalAccess(position, named), effective);
                    test = new BoundLogical(position, test,
                        new BoundSequence(position, typeof(bool), [store], Literal(position, true)),
                        IsAnd: true);
                }
            }
        }

        foreach (var subpattern in syntax.Subpatterns)
        {
            var member = BindInstanceMember(position, effective, subpattern.Name);
            if (member is BoundErrorExpression)
            {
                narrowed = effective;
                return member;
            }

            var memberTest = BindPatternAgainst(member, subpattern.Pattern, subpattern.Position);
            test = new BoundLogical(position, test, memberTest, IsAnd: true);
        }

        narrowed = effective;

        return prelude.Count == 0
            ? test
            : new BoundSequence(position, typeof(bool), prelude, test);
    }

    // ============================================================ switch expression

    private BoundExpression BindSwitchExpression(SwitchExpressionSyntax syntax)
    {
        var governing = BindExpression(syntax.Governing);
        if (governing is BoundErrorExpression) return governing;

        if (governing.Type == typeof(void))
            return Fail(syntax.Position, ErrorCode.CannotConvert, "switch 表达式的操作数没有值。");

        if (syntax.Arms.Count == 0) return new BoundErrorExpression(syntax.Position);

        var temp = MakeTemp(governing.Type);
        var store = new BoundAssignment(syntax.Position, new BoundLocalAccess(syntax.Position, temp), governing);
        var subject = new BoundLocalAccess(syntax.Position, temp);

        var arms = new List<(BoundExpression Test, BoundExpression Result)>(syntax.Arms.Count);

        foreach (var arm in syntax.Arms)
        {
            // Each arm gets its own name scope so that two arms may both call their variable
            // `o`. The closure scope is deliberately shared with the enclosing statement, since
            // a switch expression has no run-time scope of its own to instantiate one against.
            var savedScope = _scope;
            _scope = new Scope(savedScope);

            var test = BindPattern(subject, arm.Pattern, out _);

            if (arm.Guard is not null)
                test = new BoundLogical(arm.Position, test, BindCondition(arm.Guard), IsAnd: true);

            var result = BindExpression(arm.Result);

            _scope = savedScope;

            arms.Add((test, result));
        }

        if (arms.Any(a => a.Result is BoundErrorExpression || a.Test is BoundErrorExpression))
            return new BoundErrorExpression(syntax.Position);

        var resultType = arms[0].Result.Type;
        for (var i = 1; i < arms.Count; i++)
        {
            var next = BestCommonType(resultType, arms[i].Result.Type);
            if (next is null)
            {
                return Fail(syntax.Position, ErrorCode.SwitchArmTypeMismatch,
                    $"switch 表达式的分支类型不兼容：{TypeResolver.Display(resultType)} 与 " +
                    $"{TypeResolver.Display(arms[i].Result.Type)}。");
            }
            resultType = next;
        }

        if (Conversions.IsUntyped(resultType))
        {
            return Fail(syntax.Position, ErrorCode.CannotInferType,
                "无法推断 switch 表达式的结果类型。");
        }

        // Unmatched input throws, matching C#, rather than silently yielding a default.
        BoundExpression chain = new BoundCall(
            syntax.Position, null, NoMatchingSwitchArm.MakeGenericMethod(resultType),
            [Convert(subject, typeof(object), syntax.Position, explicitCast: true)]);

        for (var i = arms.Count - 1; i >= 0; i--)
        {
            var result = Convert(arms[i].Result, resultType, syntax.Position, explicitCast: false);
            chain = new BoundConditional(syntax.Position, resultType, arms[i].Test, result, chain);
        }

        return new BoundSequence(syntax.Position, resultType, [store], chain);
    }
}
