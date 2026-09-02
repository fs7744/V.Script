using System.Collections.Immutable;
using V.Script.Diagnostics;

namespace V.Script.Binding;

/// <summary>
/// Reports reading a variable on a path where nothing has assigned it — most usefully a pattern
/// variable outside the branch that matched, which would otherwise silently read <c>default</c>.
/// </summary>
/// <remarks>
/// The analysis runs over the bound tree, after every construct has been lowered, so it only has
/// to understand a dozen node kinds rather than the whole language. Where it cannot see the flow
/// it errs towards <em>assigned</em>: a label may be jumped to from anywhere, so reaching one
/// clears the state rather than producing errors nobody can act on. Rejecting a valid script
/// would be a worse failure than missing a questionable one.
/// </remarks>
internal sealed class DefiniteAssignment(DiagnosticBag diagnostics)
{
    private readonly DiagnosticBag _diagnostics = diagnostics;

    /// <summary>Locals already reported, so one bad variable produces one message.</summary>
    private readonly HashSet<LocalSymbol> _reported = [];

    /// <summary>A branch's outcome: the state after it, split by the value of a condition.</summary>
    private readonly record struct Split(State WhenTrue, State WhenFalse)
    {
        public State Merged => WhenTrue.Meet(WhenFalse);

        public static Split Both(State state) => new(state, state);
    }

    /// <summary>
    /// Which locals are assigned, and whether this point is reachable at all. Unreachable code
    /// assigns nothing and complains about nothing.
    /// </summary>
    private sealed class State
    {
        /// <summary>
        /// A persistent set, not a copied one: every assignment forks the state, and structural
        /// sharing is what keeps that from being quadratic in the number of assignments.
        /// </summary>
        private readonly ImmutableHashSet<LocalSymbol> _assigned;

        private State(ImmutableHashSet<LocalSymbol> assigned, bool reachable)
        {
            _assigned = assigned;
            Reachable = reachable;
        }

        public bool Reachable { get; }

        public static State Entry() => new(ImmutableHashSet<LocalSymbol>.Empty, reachable: true);

        public static State Unreachable() => new(ImmutableHashSet<LocalSymbol>.Empty, reachable: false);

        public bool IsAssigned(LocalSymbol local) => !Reachable || _assigned.Contains(local);

        public State With(LocalSymbol local) => new(_assigned.Add(local), Reachable);

        /// <summary>Everything is assigned from here on; used where the flow is not tracked.</summary>
        public State Opaque() => new(_assigned, reachable: true);

        /// <summary>What both paths agree on — the intersection, unless one cannot be reached.</summary>
        public State Meet(State other)
        {
            if (!Reachable) return other;
            if (!other.Reachable) return this;
            if (ReferenceEquals(_assigned, other._assigned)) return this;

            return new State(_assigned.Intersect(other._assigned), reachable: true);
        }
    }

    /// <summary>True once a label has been seen, after which flow is no longer tracked.</summary>
    private bool _sawLabel;

    public static void Analyze(BoundScript script, DiagnosticBag diagnostics)
    {
        var analysis = new DefiniteAssignment(diagnostics);
        analysis.Statement(script.Body, State.Entry());

        foreach (var lambda in script.Lambdas)
        {
            analysis._sawLabel = false;

            if (lambda.Body is not null) analysis.Expression(lambda.Body, State.Entry());
            else if (lambda.BodyStatement is not null) analysis.Statement(lambda.BodyStatement, State.Entry());
        }
    }

    // ============================================================ statements

    private State Statement(BoundStatement statement, State state)
    {
        switch (statement)
        {
            case BoundBlock block:
                foreach (var inner in block.Statements) state = Statement(inner, state);
                return state;

            case BoundLocalDeclaration declaration:
                if (declaration.Initializer is not null) state = Expression(declaration.Initializer, state).Merged;
                return declaration.Initializer is null ? state : state.With(declaration.Local);

            case BoundExpressionStatement expression:
                return Expression(expression.Expression, state).Merged;

            case BoundIf conditional:
            {
                var split = Expression(conditional.Condition, state);
                var then = Statement(conditional.Then, split.WhenTrue);
                var otherwise = conditional.Else is null
                    ? split.WhenFalse
                    : Statement(conditional.Else, split.WhenFalse);

                return then.Meet(otherwise);
            }

            case BoundWhile loop:
            {
                var split = Expression(loop.Condition, state);
                Statement(loop.Body, split.WhenTrue);
                return split.WhenFalse;
            }

            case BoundDoWhile loop:
            {
                // The body always runs at least once, so what it assigns survives.
                var after = Statement(loop.Body, state);
                return Expression(loop.Condition, after).WhenFalse;
            }

            case BoundFor loop:
            {
                foreach (var initializer in loop.Initializers) state = Statement(initializer, state);

                var split = loop.Condition is null ? Split.Both(state) : Expression(loop.Condition, state);
                var body = Statement(loop.Body, split.WhenTrue);
                foreach (var incrementor in loop.Incrementors) Statement(incrementor, body);

                return split.WhenFalse;
            }

            case BoundReturn ret:
                if (ret.Expression is not null) Expression(ret.Expression, state);
                return State.Unreachable();

            case BoundThrow thrown:
                Expression(thrown.Expression, state);
                return State.Unreachable();

            case BoundBreak or BoundContinue or BoundGoto:
                return State.Unreachable();

            case BoundLabel:
                // Anything could jump here, so nothing can be said about what is assigned.
                _sawLabel = true;
                return state.Opaque();

            case BoundBreakScope scope:
                Statement(scope.Body, state);
                return state;

            case BoundTry tri:
            {
                // A catch runs after an arbitrary prefix of the body, so only what was already
                // assigned on entry can be relied on there.
                var body = Statement(tri.Body, state);
                var result = body;

                foreach (var clause in tri.Catches)
                {
                    // The runtime hands the exception to the variable; nothing assigns it.
                    var entry = clause.Variable is null ? state : state.With(clause.Variable);
                    result = result.Meet(Statement(clause.Body, entry));
                }

                if (tri.Finally is not null) result = Statement(tri.Finally, result);

                return result;
            }

            default:
                return state;
        }
    }

    // ============================================================ expressions

    private Split Expression(BoundExpression expression, State state)
    {
        switch (expression)
        {
            case BoundLocalAccess access:
                Check(access.Local, state, access.Position);
                return Split.Both(state);

            case BoundLocalAddress address:
                // Passing a variable by reference counts as assigning it.
                return Split.Both(state.With(address.Local));

            case BoundAssignment { Target: BoundLocalAccess target } assignment:
            {
                var after = Expression(assignment.Value, state).Merged;
                return Split.Both(after.With(target.Local));
            }

            case BoundAssignment assignment:
            {
                var after = Expression(assignment.Target, state).Merged;
                return Split.Both(Expression(assignment.Value, after).Merged);
            }

            case BoundLogical logical:
            {
                var left = Expression(logical.Left, state);
                var from = logical.IsAnd ? left.WhenTrue : left.WhenFalse;
                var right = Expression(logical.Right, from);

                return logical.IsAnd
                    ? new Split(right.WhenTrue, left.WhenFalse.Meet(right.WhenFalse))
                    : new Split(left.WhenTrue.Meet(right.WhenTrue), right.WhenFalse);
            }

            case BoundUnary { Kind: BoundUnaryKind.LogicalNot } negation:
            {
                var inner = Expression(negation.Operand, state);
                return new Split(inner.WhenFalse, inner.WhenTrue);
            }

            case BoundConditional conditional:
            {
                // Both branches are kept split rather than merged. A pattern lowers to
                // `test ? (assign; true) : false`, and only that keeps the assignment visible
                // on the true path — which is the whole point of `if (x is int n)`.
                var split = Expression(conditional.Condition, state);
                var then = Expression(conditional.WhenTrue, split.WhenTrue);
                var otherwise = Expression(conditional.WhenFalse, split.WhenFalse);

                return new Split(
                    then.WhenTrue.Meet(otherwise.WhenTrue),
                    then.WhenFalse.Meet(otherwise.WhenFalse));
            }

            // A constant condition makes the other side unreachable, which is what lets the
            // `: false` arm of a lowered pattern contribute nothing to the true path.
            case BoundLiteral { Value: bool constant }:
                return constant
                    ? new Split(state, State.Unreachable())
                    : new Split(State.Unreachable(), state);

            case BoundSequence sequence:
            {
                foreach (var effect in sequence.SideEffects) state = Expression(effect, state).Merged;
                return Expression(sequence.Value, state);
            }

            default:
                foreach (var child in Children(expression)) state = Expression(child, state).Merged;
                return Split.Both(state);
        }
    }

    private void Check(LocalSymbol local, State state, SourcePosition position)
    {
        // A captured variable lives in a closure that another function may have written to, so
        // this pass cannot see all of its assignments and does not judge it.
        if (_sawLabel || local.IsCompilerGenerated || local.IsLambdaParameter || local.IsCaptured) return;
        if (state.IsAssigned(local) || !_reported.Add(local)) return;

        _diagnostics.Report(ErrorCode.UseOfUnassignedVariable, position,
            $"变量 '{local.Name}' 在这里可能尚未赋值。");
    }

    /// <summary>Sub-expressions in evaluation order, for the nodes with no flow of their own.</summary>
    private static IEnumerable<BoundExpression> Children(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundBinary binary: yield return binary.Left; yield return binary.Right; break;
            case BoundUnary unary: yield return unary.Operand; break;
            case BoundConversion conversion: yield return conversion.Operand; break;
            case BoundFieldAccess { Receiver: not null } field: yield return field.Receiver; break;
            case BoundPropertyAccess { Receiver: not null } property: yield return property.Receiver; break;

            case BoundCall call:
                if (call.Receiver is not null) yield return call.Receiver;
                foreach (var argument in call.Arguments) yield return argument;
                break;

            case BoundDelegateInvoke invoke:
                yield return invoke.Target;
                foreach (var argument in invoke.Arguments) yield return argument;
                break;

            case BoundObjectCreation creation:
                foreach (var argument in creation.Arguments) yield return argument;
                break;

            case BoundTupleLiteral tuple:
                foreach (var element in tuple.Elements) yield return element;
                break;

            case BoundArrayCreation array:
                foreach (var element in array.Elements) yield return element;
                break;

            case BoundNewArray array:
                foreach (var length in array.Lengths) yield return length;
                break;

            case BoundArrayAccess access:
                yield return access.Array;
                foreach (var index in access.Indices) yield return index;
                break;

            case BoundIndexerAccess indexer:
                yield return indexer.Receiver;
                foreach (var argument in indexer.Arguments) yield return argument;
                break;

            case BoundIsType isType: yield return isType.Operand; break;
            case BoundAsType asType: yield return asType.Operand; break;
            case BoundAwait await: yield return await.Operand; break;
            case BoundThrowExpression thrown: yield return thrown.Exception; break;

            case BoundConditionalAccess access:
                yield return access.Receiver;
                break;
        }
    }
}
