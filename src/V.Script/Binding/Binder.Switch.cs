using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// The <c>switch</c> statement. It lowers to an if/else chain over a temp holding the governing
/// value, wrapped in a <see cref="BoundBreakScope"/> so that <c>break</c> has somewhere to go —
/// the emitter needs no notion of a switch at all.
/// </summary>
internal sealed partial class Binder
{
    private BoundStatement BindSwitchStatement(SwitchStatementSyntax syntax)
    {
        var position = syntax.Position;

        var governing = BindExpression(syntax.Governing);
        if (governing is BoundErrorExpression) return new BoundNop(position);

        if (governing.Type == typeof(void) || Conversions.IsUntyped(governing.Type))
        {
            _diagnostics.Report(ErrorCode.CannotInferType, position,
                "switch 的对象必须是有类型的值。");
            return new BoundNop(position);
        }

        var temp = MakeTemp(governing.Type);
        var subject = new BoundLocalAccess(position, temp);

        var store = new BoundExpressionStatement(position,
            new BoundAssignment(position, subject, governing));

        _switchDepth++;

        var cases = new List<(BoundExpression Test, BoundStatement Body)>();
        BoundStatement? defaultBody = null;
        SourcePosition? defaultAt = null;

        foreach (var section in syntax.Sections)
        {
            // Labels bind before the body so a pattern variable is visible inside it. They land
            // in the enclosing scope, exactly as `if (x is T t)` does, which means two sections
            // cannot reuse a designation name.
            BoundExpression? test = null;
            var isDefault = false;

            foreach (var label in section.Labels)
            {
                if (label.Pattern is null)
                {
                    if (defaultAt is not null)
                    {
                        _diagnostics.Report(ErrorCode.UnexpectedToken, label.Position,
                            "一个 switch 语句只能有一个 default 标签。");
                    }

                    defaultAt = label.Position;
                    isDefault = true;
                    continue;
                }

                var labelTest = BindPattern(subject, label.Pattern, out _);

                if (label.Guard is not null)
                    labelTest = new BoundLogical(label.Position, labelTest, BindCondition(label.Guard), IsAnd: true);

                test = test is null ? labelTest : new BoundLogical(label.Position, test, labelTest, IsAnd: false);
            }

            var body = BindBlock(new BlockStatementSyntax(section.Position, section.Statements));

            if (!AlwaysExits(body))
            {
                _diagnostics.Report(ErrorCode.SwitchSectionFallsThrough, section.Position,
                    "switch 分支不能落到下一个分支，请以 break、return、continue 或 throw 结束。");
            }

            if (isDefault)
            {
                // `default:` may share a section with case labels; the case tests still apply,
                // and the default arm is what runs when none of them do.
                defaultBody = body;
                if (test is null) continue;
            }

            if (test is not null) cases.Add((test, body));
        }

        _switchDepth--;

        BoundStatement chain = defaultBody ?? new BoundNop(position);
        for (var i = cases.Count - 1; i >= 0; i--)
            chain = new BoundIf(position, cases[i].Test, cases[i].Body, chain);

        return new BoundBlock(position, [store, new BoundBreakScope(position, chain)], null);
    }

    /// <summary>
    /// Does control always leave <paramref name="statement"/> rather than run off its end?
    /// This is what tells a legal switch section from one that would fall through.
    /// </summary>
    private static bool AlwaysExits(BoundStatement statement) => statement switch
    {
        BoundReturn or BoundThrow or BoundBreak or BoundContinue => true,
        BoundBlock block => block.Statements.Any(AlwaysExits),

        // A break inside a nested switch leaves that switch, so only a return or throw gets out.
        BoundBreakScope scope => AlwaysReturns(scope.Body),

        BoundIf { Else: not null } conditional =>
            AlwaysExits(conditional.Then) && AlwaysExits(conditional.Else),

        BoundTry tri =>
            (AlwaysExits(tri.Body) && tri.Catches.All(c => AlwaysExits(c.Body)))
            || (tri.Finally is not null && AlwaysExits(tri.Finally)),

        BoundWhile { Condition: BoundLiteral { Value: true } } loop => !ContainsBreak(loop.Body),
        BoundFor { Condition: null } loop => !ContainsBreak(loop.Body),

        _ => false,
    };
}
