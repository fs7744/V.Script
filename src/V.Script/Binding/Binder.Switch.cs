using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// The <c>switch</c> statement. It lowers to a dispatch of tests that jump to labelled sections,
/// wrapped in a <see cref="BoundBreakScope"/> so that <c>break</c> has somewhere to go — the
/// emitter needs no notion of a switch at all.
/// </summary>
/// <remarks>
/// The sections could just as well have been nested if/else bodies, and were at first. Giving
/// each one a label is what makes <c>goto case</c> expressible: it is an ordinary jump to the
/// section's label. Laying the sections out in sequence is safe because a section that could
/// fall out of its own bottom is rejected before it gets here.
/// </remarks>
internal sealed partial class Binder
{
    /// <summary>Where <c>goto case</c> / <c>goto default</c> inside the current switch land.</summary>
    private readonly Stack<SwitchLabels> _switchLabels = new();

    private sealed class SwitchLabels
    {
        public readonly List<(object? Value, LabelSymbol Label)> Cases = [];

        public LabelSymbol? Default { get; set; }
    }

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
        var labels = new SwitchLabels();
        _switchLabels.Push(labels);

        var dispatch = new List<BoundStatement>();
        var bodies = new List<BoundStatement>();
        var sectionBodies = new List<BoundStatement>();
        SourcePosition? defaultAt = null;

        // Every section's label is created up front so that `goto case` can name one that is
        // written further down.
        var sectionLabels = syntax.Sections
            .Select((_, i) => new LabelSymbol($"<case{i}>") { IsDefined = true })
            .ToArray();

        for (var s = 0; s < syntax.Sections.Count; s++)
        {
            foreach (var label in syntax.Sections[s].Labels)
            {
                if (label.Pattern is null)
                {
                    if (defaultAt is not null)
                    {
                        _diagnostics.Report(ErrorCode.UnexpectedToken, label.Position,
                            "一个 switch 语句只能有一个 default 标签。");
                    }

                    defaultAt = label.Position;
                    labels.Default = sectionLabels[s];
                }
                else if (label.Guard is null && ConstantOf(label.Pattern) is { } constant)
                {
                    labels.Cases.Add((constant, sectionLabels[s]));
                }
            }
        }

        for (var s = 0; s < syntax.Sections.Count; s++)
        {
            var section = syntax.Sections[s];
            var sectionLabel = sectionLabels[s];
            BoundExpression? test = null;

            foreach (var label in section.Labels)
            {
                if (label.Pattern is null) continue;

                // Labels bind before the body so a pattern variable is visible inside it. They
                // land in the enclosing scope, exactly as `if (x is T t)` does, which means two
                // sections cannot reuse a designation name.
                var labelTest = BindPattern(subject, label.Pattern, out _);

                if (label.Guard is not null)
                    labelTest = new BoundLogical(label.Position, labelTest, BindCondition(label.Guard), IsAnd: true);

                test = test is null ? labelTest : new BoundLogical(label.Position, test, labelTest, IsAnd: false);
            }

            if (test is not null)
            {
                dispatch.Add(new BoundIf(section.Position, test,
                    new BoundGoto(section.Position, sectionLabel), null));
            }

            var body = BindBlock(new BlockStatementSyntax(section.Position, section.Statements));

            if (!AlwaysExits(body))
            {
                _diagnostics.Report(ErrorCode.SwitchSectionFallsThrough, section.Position,
                    "switch 分支不能落到下一个分支，请以 break、return、continue、goto 或 throw 结束。");
            }

            sectionBodies.Add(body);

            bodies.Add(new BoundLabel(section.Position, sectionLabel));
            bodies.Add(body);
        }

        _switchLabels.Pop();
        _switchDepth--;

        var exit = new LabelSymbol("<switchEnd>") { IsDefined = true };

        // Nothing matched: jump past every section, or into the default one.
        dispatch.Add(new BoundGoto(position, labels.Default ?? exit));

        var inside = new BoundBlock(position, [.. dispatch, .. bodies, new BoundLabel(position, exit)], null);

        // Only an exhaustive switch whose every section returns can be said to always return.
        var exhaustive = labels.Default is not null &&
                         syntax.Sections.Count > 0 &&
                         EverySectionReturns(sectionBodies, sectionLabels);

        return new BoundBlock(position, [store, new BoundBreakScope(position, inside, exhaustive)], null);
    }

    /// <summary>
    /// Whether control can only leave the switch by returning. A section that ends in
    /// <c>goto case</c> returns exactly when the section it jumps to does, so this is a small
    /// fixpoint rather than a single pass.
    /// </summary>
    private static bool EverySectionReturns(
        IReadOnlyList<BoundStatement> bodies,
        IReadOnlyList<LabelSymbol> labels)
    {
        var returns = bodies.Select(AlwaysReturns).ToArray();
        var index = labels.Select((label, i) => (label, i)).ToDictionary(p => p.label, p => p.i);

        bool progress;
        do
        {
            progress = false;

            for (var i = 0; i < bodies.Count; i++)
            {
                if (returns[i] || !AlwaysExits(bodies[i])) continue;
                if (ContainsBreak(bodies[i])) continue;

                var targets = new List<LabelSymbol>();
                CollectGotos(bodies[i], targets);

                // A jump out of the switch entirely says nothing about returning.
                if (targets.Count == 0 || targets.Any(t => !index.ContainsKey(t))) continue;
                if (!targets.All(t => returns[index[t]])) continue;

                returns[i] = true;
                progress = true;
            }
        }
        while (progress);

        return returns.All(r => r);
    }

    private static void CollectGotos(BoundStatement statement, List<LabelSymbol> into)
    {
        switch (statement)
        {
            case BoundGoto jump: into.Add(jump.Label); break;
            case BoundBlock block: foreach (var s in block.Statements) CollectGotos(s, into); break;
            case BoundIf conditional:
                CollectGotos(conditional.Then, into);
                if (conditional.Else is not null) CollectGotos(conditional.Else, into);
                break;
            case BoundWhile loop: CollectGotos(loop.Body, into); break;
            case BoundDoWhile loop: CollectGotos(loop.Body, into); break;
            case BoundFor loop: CollectGotos(loop.Body, into); break;
            case BoundTry tri:
                CollectGotos(tri.Body, into);
                foreach (var clause in tri.Catches) CollectGotos(clause.Body, into);
                if (tri.Finally is not null) CollectGotos(tri.Finally, into);
                break;
        }
    }

    /// <summary>The value of a constant pattern, which is what <c>goto case</c> matches against.</summary>
    private static object? ConstantOf(PatternSyntax pattern) =>
        pattern is ConstantPatternSyntax { Value: LiteralExpressionSyntax literal }
            ? literal.Token.Value
            : null;

    private BoundStatement BindGotoCase(GotoStatementSyntax syntax)
    {
        if (_switchLabels.Count == 0)
        {
            _diagnostics.Report(ErrorCode.ConstructNotSupported, syntax.Position,
                "'goto case' 与 'goto default' 只能出现在 switch 语句中。");
            return new BoundNop(syntax.Position);
        }

        var labels = _switchLabels.Peek();

        if (syntax.IsDefault)
        {
            if (labels.Default is null)
            {
                _diagnostics.Report(ErrorCode.UndefinedName, syntax.Position,
                    "当前 switch 没有 default 分支。");
                return new BoundNop(syntax.Position);
            }

            return new BoundGoto(syntax.Position, labels.Default);
        }

        var value = BindExpression(syntax.CaseValue!);
        if (value is not BoundLiteral literal)
        {
            _diagnostics.Report(ErrorCode.ConstructNotSupported, syntax.Position,
                "'goto case' 的目标必须是常量。");
            return new BoundNop(syntax.Position);
        }

        foreach (var (constant, label) in labels.Cases)
            if (Equals(constant, literal.Value))
                return new BoundGoto(syntax.Position, label);

        _diagnostics.Report(ErrorCode.UndefinedName, syntax.Position,
            $"当前 switch 没有 case {literal.Value} 分支。");

        return new BoundNop(syntax.Position);
    }

    /// <summary>
    /// Does control always leave <paramref name="statement"/> rather than run off its end?
    /// This is what tells a legal switch section from one that would fall through.
    /// </summary>
    private static bool AlwaysExits(BoundStatement statement) => statement switch
    {
        BoundReturn or BoundThrow or BoundBreak or BoundContinue or BoundGoto => true,
        BoundBlock block => block.Statements.Any(AlwaysExits),

        // A break inside a nested switch leaves that switch, so only a return or throw gets out.
        BoundBreakScope scope => scope.AllPathsReturn,

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
