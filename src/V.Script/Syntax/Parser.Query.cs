using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// Query expressions. They are pure syntactic sugar, so they are rewritten into the method calls
/// they stand for here and nothing downstream ever learns that queries exist.
/// </summary>
/// <remarks>
/// The interesting part is what C# calls the transparent identifier: after <c>let</c> or a second
/// <c>from</c>, a clause needs to see several range variables at once, but a lambda has one
/// parameter. Roslyn synthesises an anonymous type; this parser uses a tuple and gives each later
/// lambda a block body that unpacks it into ordinary locals. The clause's own expression is then
/// used verbatim — no substitution pass, and the names resolve because they really are locals.
/// </remarks>
public sealed partial class Parser
{
    /// <summary>The lambda parameter that carries the packed range variables.</summary>
    private const string TransparentName = "<>q";

    private bool StartsQuery()
    {
        if (!IsContextual("from")) return false;

        var start = _pos;
        Advance();

        // `from [type] name in` — the `in` is what makes it a query rather than a name.
        var looksLikeQuery = false;

        if (Current.Kind == SyntaxKind.Identifier)
        {
            if (Peek(1).Kind == SyntaxKind.InKeyword) looksLikeQuery = true;
            else if (TrySpeculateType(out _) && Current.Kind == SyntaxKind.Identifier &&
                     Peek(1).Kind == SyntaxKind.InKeyword)
            {
                looksLikeQuery = true;
            }
        }

        _pos = start;
        return looksLikeQuery;
    }

    private ExpressionSyntax ParseQuery()
    {
        var position = Current.Position;

        var (name, source) = ParseFromClause();
        return ParseQueryBody(position, source, [name]);
    }

    private (string Name, ExpressionSyntax Source) ParseFromClause()
    {
        Advance(); // 'from'
        return ParseRangeBinding();
    }

    /// <summary><c>[type] name in source</c>, shared by <c>from</c> and <c>join</c>.</summary>
    private (string Name, ExpressionSyntax Source) ParseRangeBinding()
    {
        // An explicit element type is a cast on the source, which is what C# does with it.
        TypeSyntax? elementType = null;
        if (Peek(1).Kind != SyntaxKind.InKeyword) TrySpeculateType(out elementType);

        var name = Expect(SyntaxKind.Identifier, "范围变量名").Text;
        Expect(SyntaxKind.InKeyword, "'in'");

        var source = ParseExpression();

        if (elementType is not null)
            source = Call(source, "Cast", [], [elementType]);

        return (name, source);
    }

    private ExpressionSyntax ParseQueryBody(
        SourcePosition position,
        ExpressionSyntax source,
        List<string> variables)
    {
        while (true)
        {
            if (IsContextual("where"))
            {
                Advance();
                source = Call(source, "Where", [Lambda(variables, ParseExpression())]);
                continue;
            }

            if (IsContextual("let"))
            {
                Advance();
                var name = Expect(SyntaxKind.Identifier, "标识符").Text;
                Expect(SyntaxKind.Equals, "'='");

                var value = ParseExpression();

                // The projection packs the old variables and the new one into one tuple.
                source = Call(source, "Select", [Lambda(variables, Pack(variables, value, position))]);
                variables = [.. variables, name];
                continue;
            }

            if (IsContextual("from"))
            {
                var (name, inner) = ParseFromClause();

                var collectionSelector = Lambda(variables, inner);
                var resultSelector = PairLambda(variables, name, position);

                source = Call(source, "SelectMany", [collectionSelector, resultSelector]);
                variables = [.. variables, name];
                continue;
            }

            if (IsContextual("join"))
            {
                source = ParseJoinClause(source, ref variables, position);
                continue;
            }

            if (IsContextual("orderby"))
            {
                Advance();
                source = ParseOrderings(source, variables);
                continue;
            }

            break;
        }

        if (IsContextual("group")) return ParseGroupClause(source, variables, position);

        if (!IsContextual("select"))
        {
            _diagnostics.Report(ErrorCode.ExpectedToken, Current.Position,
                $"查询表达式需要以 select 或 group 结束，但遇到 '{Describe(Current)}'。");

            return new ErrorExpressionSyntax(position);
        }

        Advance();
        var projection = ParseExpression();

        // `select x` over an untouched single range variable is the identity; skipping it keeps
        // the generated call chain the same shape the hand-written one would have.
        var result = IsRangeVariable(projection, variables)
            ? source
            : Call(source, "Select", [Lambda(variables, projection)]);

        return ParseContinuation(position, result);
    }

    private ExpressionSyntax ParseJoinClause(
        ExpressionSyntax source,
        ref List<string> variables,
        SourcePosition position)
    {
        Advance(); // 'join'

        var (name, inner) = ParseRangeBinding();

        if (!IsContextual("on"))
        {
            _diagnostics.Report(ErrorCode.ExpectedToken, Current.Position, "join 之后需要 'on'。");
            return source;
        }

        Advance();
        var outerKey = ParseExpression();

        if (!IsContextual("equals"))
        {
            _diagnostics.Report(ErrorCode.ExpectedToken, Current.Position, "join ... on 之后需要 'equals'。");
            return source;
        }

        Advance();
        var innerKey = ParseExpression();

        // `join ... into g` groups the matches instead of pairing them one by one.
        if (IsContextual("into"))
        {
            Advance();
            var group = Expect(SyntaxKind.Identifier, "标识符").Text;

            var grouped = Call(source, "GroupJoin",
            [
                inner,
                Lambda(variables, outerKey),
                Lambda([name], innerKey),
                PairLambda(variables, group, position),
            ]);

            variables = [.. variables, group];
            return grouped;
        }

        var joined = Call(source, "Join",
        [
            inner,
            Lambda(variables, outerKey),
            Lambda([name], innerKey),
            PairLambda(variables, name, position),
        ]);

        variables = [.. variables, name];
        return joined;
    }

    private ExpressionSyntax ParseOrderings(ExpressionSyntax source, List<string> variables)
    {
        var first = true;

        while (true)
        {
            var key = ParseExpression();

            var descending = IsContextual("descending");
            if (descending || IsContextual("ascending")) Advance();

            var method = first
                ? descending ? "OrderByDescending" : "OrderBy"
                : descending ? "ThenByDescending" : "ThenBy";

            source = Call(source, method, [Lambda(variables, key)]);
            first = false;

            if (!Match(SyntaxKind.Comma)) return source;
        }
    }

    private ExpressionSyntax ParseGroupClause(
        ExpressionSyntax source,
        List<string> variables,
        SourcePosition position)
    {
        Advance(); // 'group'
        var element = ParseExpression();

        if (!IsContextual("by"))
        {
            _diagnostics.Report(ErrorCode.ExpectedToken, Current.Position, "group 之后需要 'by'。");
            return source;
        }

        Advance();
        var key = ParseExpression();

        var grouped = IsRangeVariable(element, variables)
            ? Call(source, "GroupBy", [Lambda(variables, key)])
            : Call(source, "GroupBy", [Lambda(variables, key), Lambda(variables, element)]);

        return ParseContinuation(position, grouped);
    }

    /// <summary><c>into g ...</c> starts a fresh query whose single range variable is <c>g</c>.</summary>
    private ExpressionSyntax ParseContinuation(SourcePosition position, ExpressionSyntax source)
    {
        if (!IsContextual("into")) return source;

        Advance();
        var name = Expect(SyntaxKind.Identifier, "标识符").Text;

        return ParseQueryBody(position, source, [name]);
    }

    // ============================================================ building the calls

    private static ExpressionSyntax Call(
        ExpressionSyntax receiver,
        string method,
        IReadOnlyList<ExpressionSyntax> arguments,
        IReadOnlyList<TypeSyntax>? typeArguments = null)
    {
        var target = new MemberAccessExpressionSyntax(
            receiver.Position, receiver, method, IsNullConditional: false, typeArguments);

        var argumentList = arguments
            .Select(a => new ArgumentSyntax(a.Position, null, a))
            .ToArray();

        return new InvocationExpressionSyntax(receiver.Position, target, argumentList, typeArguments);
    }

    /// <summary>
    /// A lambda over the current range variables. With one variable it is simply
    /// <c>x =&gt; body</c>; with several the parameter is a tuple and the body unpacks it first,
    /// which is what makes the original expression usable unchanged.
    /// </summary>
    private static ExpressionSyntax Lambda(IReadOnlyList<string> variables, ExpressionSyntax body)
    {
        var position = body.Position;

        if (variables.Count == 1)
        {
            return new LambdaExpressionSyntax(
                position, [new LambdaParameterSyntax(position, null, variables[0])], body);
        }

        var statements = new List<StatementSyntax>(variables.Count + 1);

        for (var i = 0; i < variables.Count; i++)
        {
            statements.Add(new VariableDeclarationSyntax(
                position, null, variables[i], TupleItem(position, i)));
        }

        statements.Add(new ReturnStatementSyntax(position, body));

        return new LambdaExpressionSyntax(
            position,
            [new LambdaParameterSyntax(position, null, TransparentName)],
            new BlockStatementSyntax(position, statements));
    }

    /// <summary>Builds the tuple of the current variables plus one more value.</summary>
    private static ExpressionSyntax Pack(
        IReadOnlyList<string> variables,
        ExpressionSyntax extra,
        SourcePosition position)
    {
        var elements = new List<TupleElementSyntax>(variables.Count + 1);

        for (var i = 0; i < variables.Count; i++)
        {
            var value = variables.Count == 1
                ? new NameExpressionSyntax(position, variables[0])
                : TupleItem(position, i);

            elements.Add(new TupleElementSyntax(position, null, value));
        }

        elements.Add(new TupleElementSyntax(position, null, extra));
        return new TupleExpressionSyntax(position, elements);
    }

    /// <summary>
    /// The two-parameter result selector a <c>SelectMany</c> or <c>Join</c> needs: it receives
    /// the packed variables and the new element, and returns them packed together.
    /// </summary>
    private static ExpressionSyntax PairLambda(
        IReadOnlyList<string> variables,
        string added,
        SourcePosition position)
    {
        var outer = variables.Count == 1 ? variables[0] : TransparentName;

        var elements = new List<TupleElementSyntax>(variables.Count + 1);

        for (var i = 0; i < variables.Count; i++)
        {
            var value = variables.Count == 1
                ? (ExpressionSyntax)new NameExpressionSyntax(position, outer)
                : TupleItem(position, i);

            elements.Add(new TupleElementSyntax(position, null, value));
        }

        elements.Add(new TupleElementSyntax(position, null, new NameExpressionSyntax(position, added)));

        return new LambdaExpressionSyntax(
            position,
            [
                new LambdaParameterSyntax(position, null, outer),
                new LambdaParameterSyntax(position, null, added),
            ],
            new TupleExpressionSyntax(position, elements));
    }

    private static ExpressionSyntax TupleItem(SourcePosition position, int index) =>
        new MemberAccessExpressionSyntax(
            position,
            new NameExpressionSyntax(position, TransparentName),
            $"Item{index + 1}",
            IsNullConditional: false);

    private static bool IsRangeVariable(ExpressionSyntax expression, IReadOnlyList<string> variables) =>
        variables.Count == 1 &&
        expression is NameExpressionSyntax name &&
        name.Name == variables[0];
}
