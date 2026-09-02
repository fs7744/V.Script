using System.Collections.Frozen;
using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// Recursive-descent parser with precedence climbing for expressions.
/// Ambiguous constructs (declaration vs. expression, cast vs. parenthesis, lambda vs. group)
/// are resolved by speculative parsing with backtracking.
/// </summary>
public sealed partial class Parser
{
    /// <summary>Names that are unambiguously types, used to disambiguate casts like <c>(int)-1</c>.</summary>
    private static readonly FrozenSet<string> BuiltInTypeNames = new[]
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float",
        "int", "uint", "long", "ulong", "short", "ushort", "object", "string",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly List<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;

    public Parser(List<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    internal Token Current => Peek(0);

    private Token Peek(int offset)
    {
        var index = _pos + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[^1];
    }

    private Token Advance()
    {
        var token = Current;
        if (_pos < _tokens.Count - 1) _pos++;
        return token;
    }

    private bool Match(SyntaxKind kind)
    {
        if (Current.Kind != kind) return false;
        Advance();
        return true;
    }

    private Token Expect(SyntaxKind kind, string what)
    {
        if (Current.Kind == kind) return Advance();

        _diagnostics.Report(ErrorCode.ExpectedToken, Current.Position,
            $"应为 {what}，但遇到 '{Describe(Current)}'。");
        return new Token(kind, string.Empty, Current.Position);
    }

    private static string Describe(Token token) =>
        token.Kind == SyntaxKind.EndOfFile ? "<脚本结束>" : token.Text;

    /// <summary>
    /// A trailing ';' is optional at the very end of a script, so a one-line expression such as
    /// <c>"a + b"</c> parses as a statement without ceremony.
    /// </summary>
    private void ExpectStatementEnd()
    {
        if (Current.Kind == SyntaxKind.EndOfFile) return;
        Expect(SyntaxKind.Semicolon, "';'");
    }

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var start = Current.Position;
        var statements = new List<StatementSyntax>();

        while (Current.Kind != SyntaxKind.EndOfFile)
        {
            var before = _pos;
            statements.Add(ParseStatement());

            // Guarantee forward progress even when a statement fails to parse.
            if (_pos == before) Advance();
        }

        return new CompilationUnitSyntax(start, statements);
    }

    // ============================================================ statements

    private StatementSyntax ParseStatement()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.OpenBrace: return ParseBlock();
            case SyntaxKind.IfKeyword: return ParseIf();
            case SyntaxKind.WhileKeyword: return ParseWhile();
            case SyntaxKind.DoKeyword: return ParseDoWhile();
            case SyntaxKind.ForKeyword: return ParseFor();
            case SyntaxKind.ForeachKeyword: return ParseForEach();
            case SyntaxKind.ReturnKeyword: return ParseReturn();
            case SyntaxKind.TryKeyword: return ParseTry();
            case SyntaxKind.ThrowKeyword: return ParseThrow();
            case SyntaxKind.SwitchKeyword: return ParseSwitchStatement();
            case SyntaxKind.UsingKeyword: return ParseUsing();
            case SyntaxKind.LockKeyword: return ParseLock();
            case SyntaxKind.GotoKeyword: return ParseGoto();

            case SyntaxKind.Identifier when Peek(1).Kind == SyntaxKind.Colon:
            {
                var name = Advance();
                Advance(); // ':'
                return new LabeledStatementSyntax(name.Position, name.Text, ParseStatement());
            }

            case SyntaxKind.CheckedKeyword when Peek(1).Kind == SyntaxKind.OpenBrace:
            case SyntaxKind.UncheckedKeyword when Peek(1).Kind == SyntaxKind.OpenBrace:
            {
                var isChecked = Advance().Kind == SyntaxKind.CheckedKeyword;
                return new CheckedStatementSyntax(Current.Position, isChecked, ParseBlock());
            }
            case SyntaxKind.BreakKeyword:
            {
                var pos = Advance().Position;
                Expect(SyntaxKind.Semicolon, "';'");
                return new BreakStatementSyntax(pos);
            }
            case SyntaxKind.ContinueKeyword:
            {
                var pos = Advance().Position;
                Expect(SyntaxKind.Semicolon, "';'");
                return new ContinueStatementSyntax(pos);
            }
            case SyntaxKind.Semicolon:
            {
                var pos = Advance().Position;
                return new BlockStatementSyntax(pos, []);
            }
            default:
                return ParseDeclarationOrExpressionStatement();
        }
    }

    private BlockStatementSyntax ParseBlock()
    {
        var pos = Expect(SyntaxKind.OpenBrace, "'{'").Position;
        var statements = new List<StatementSyntax>();

        while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
        {
            var before = _pos;
            statements.Add(ParseStatement());
            if (_pos == before) Advance();
        }

        Expect(SyntaxKind.CloseBrace, "'}'");
        return new BlockStatementSyntax(pos, statements);
    }

    private StatementSyntax ParseIf()
    {
        var pos = Advance().Position;
        Expect(SyntaxKind.OpenParen, "'('");
        var condition = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");
        var then = ParseStatement();
        StatementSyntax? otherwise = null;
        if (Match(SyntaxKind.ElseKeyword)) otherwise = ParseStatement();
        return new IfStatementSyntax(pos, condition, then, otherwise);
    }

    private StatementSyntax ParseWhile()
    {
        var pos = Advance().Position;
        Expect(SyntaxKind.OpenParen, "'('");
        var condition = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");
        return new WhileStatementSyntax(pos, condition, ParseStatement());
    }

    private StatementSyntax ParseDoWhile()
    {
        var pos = Advance().Position;
        var body = ParseStatement();
        Expect(SyntaxKind.WhileKeyword, "'while'");
        Expect(SyntaxKind.OpenParen, "'('");
        var condition = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");
        Expect(SyntaxKind.Semicolon, "';'");
        return new DoWhileStatementSyntax(pos, body, condition);
    }

    private StatementSyntax ParseFor()
    {
        var pos = Advance().Position;
        Expect(SyntaxKind.OpenParen, "'('");

        var initializers = new List<StatementSyntax>();
        if (Current.Kind != SyntaxKind.Semicolon)
        {
            if (TryParseVariableDeclaration(requireSemicolon: false, out var declaration))
            {
                initializers.Add(declaration!);
                while (Match(SyntaxKind.Comma))
                {
                    // subsequent declarators reuse the first declaration's type
                    var namePos = Current.Position;
                    var name = Expect(SyntaxKind.Identifier, "标识符").Text;
                    ExpressionSyntax? init = Match(SyntaxKind.Equals) ? ParseExpression() : null;
                    initializers.Add(new VariableDeclarationSyntax(
                        namePos, ((VariableDeclarationSyntax)declaration!).Type, name, init));
                }
            }
            else
            {
                do
                {
                    var expr = ParseExpression();
                    initializers.Add(new ExpressionStatementSyntax(expr.Position, expr));
                } while (Match(SyntaxKind.Comma));
            }
        }
        Expect(SyntaxKind.Semicolon, "';'");

        ExpressionSyntax? condition = Current.Kind == SyntaxKind.Semicolon ? null : ParseExpression();
        Expect(SyntaxKind.Semicolon, "';'");

        var incrementors = new List<ExpressionSyntax>();
        if (Current.Kind != SyntaxKind.CloseParen)
        {
            do { incrementors.Add(ParseExpression()); } while (Match(SyntaxKind.Comma));
        }
        Expect(SyntaxKind.CloseParen, "')'");

        return new ForStatementSyntax(pos, initializers, condition, incrementors, ParseStatement());
    }

    private StatementSyntax ParseForEach()
    {
        var pos = Advance().Position;
        Expect(SyntaxKind.OpenParen, "'('");

        TypeSyntax? elementType = null;
        if (!Match(SyntaxKind.VarKeyword))
            elementType = ParseType();

        var name = Expect(SyntaxKind.Identifier, "标识符").Text;
        Expect(SyntaxKind.InKeyword, "'in'");
        var collection = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");

        return new ForEachStatementSyntax(pos, elementType, name, collection, ParseStatement());
    }

    private StatementSyntax ParseReturn()
    {
        var pos = Advance().Position;
        ExpressionSyntax? value = Current.Kind == SyntaxKind.Semicolon ? null : ParseExpression();
        Expect(SyntaxKind.Semicolon, "';'");
        return new ReturnStatementSyntax(pos, value);
    }

    private StatementSyntax ParseThrow()
    {
        var pos = Advance().Position;
        var value = ParseExpression();
        Expect(SyntaxKind.Semicolon, "';'");
        return new ThrowStatementSyntax(pos, value);
    }

    private StatementSyntax ParseTry()
    {
        var pos = Advance().Position;
        var body = ParseBlock();

        var catches = new List<CatchClauseSyntax>();
        while (Current.Kind == SyntaxKind.CatchKeyword)
        {
            var catchPos = Advance().Position;
            TypeSyntax? exceptionType = null;
            string? variable = null;

            if (Match(SyntaxKind.OpenParen))
            {
                exceptionType = ParseType();
                if (Current.Kind == SyntaxKind.Identifier) variable = Advance().Text;
                Expect(SyntaxKind.CloseParen, "')'");
            }

            catches.Add(new CatchClauseSyntax(catchPos, exceptionType, variable, ParseBlock()));
        }

        BlockStatementSyntax? finallyBlock = null;
        if (Match(SyntaxKind.FinallyKeyword)) finallyBlock = ParseBlock();

        if (catches.Count == 0 && finallyBlock is null)
        {
            _diagnostics.Report(ErrorCode.ExpectedToken, pos,
                "'try' 至少需要一个 'catch' 或 'finally' 块。");
        }

        return new TryStatementSyntax(pos, body, catches, finallyBlock);
    }

    /// <summary>
    /// <c>using (var x = e) body</c>, <c>using (e) body</c>, or the declaration form
    /// <c>using var x = e;</c>.
    /// </summary>
    private StatementSyntax ParseUsing()
    {
        var pos = Advance().Position;

        if (!Match(SyntaxKind.OpenParen))
        {
            // `using var x = e;` — no parentheses, no body of its own.
            if (!TryParseVariableDeclaration(requireSemicolon: true, out var declared))
            {
                _diagnostics.Report(ErrorCode.ExpectedToken, Current.Position,
                    "using 后面需要 '(' 或者一个变量声明。");
                return new ErrorStatementSyntax(pos);
            }

            return new UsingStatementSyntax(pos, (VariableDeclarationSyntax)declared!, null, null);
        }

        VariableDeclarationSyntax? declaration = null;
        ExpressionSyntax? resource = null;

        if (TryParseVariableDeclaration(requireSemicolon: false, out var inner))
            declaration = (VariableDeclarationSyntax)inner!;
        else
            resource = ParseExpression();

        Expect(SyntaxKind.CloseParen, "')'");
        return new UsingStatementSyntax(pos, declaration, resource, ParseStatement());
    }

    private StatementSyntax ParseLock()
    {
        var pos = Advance().Position;

        Expect(SyntaxKind.OpenParen, "'('");
        var target = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");

        return new LockStatementSyntax(pos, target, ParseStatement());
    }

    private StatementSyntax ParseGoto()
    {
        var pos = Advance().Position;

        if (Match(SyntaxKind.CaseKeyword))
        {
            var value = ParseExpression();
            ExpectStatementEnd();
            return new GotoStatementSyntax(pos, null, value);
        }

        if (Match(SyntaxKind.DefaultKeyword))
        {
            ExpectStatementEnd();
            return new GotoStatementSyntax(pos, null, null, IsDefault: true);
        }

        var label = Expect(SyntaxKind.Identifier, "标签名");
        ExpectStatementEnd();

        return new GotoStatementSyntax(pos, label.Text, null);
    }

    private StatementSyntax ParseDeclarationOrExpressionStatement()
    {
        if (TryParseDeconstruction(out var deconstruction)) return deconstruction!;
        if (TryParseLocalFunction(out var function)) return function!;

        if (TryParseVariableDeclaration(requireSemicolon: true, out var declaration))
            return declaration!;

        var expression = ParseExpression();
        ExpectStatementEnd();
        return new ExpressionStatementSyntax(expression.Position, expression);
    }

    /// <summary>
    /// Speculatively parses <c>Type name [= init]</c>. Restores the position and returns
    /// false when the tokens turn out to be an expression instead.
    /// </summary>
    private bool TryParseVariableDeclaration(bool requireSemicolon, out StatementSyntax? result)
    {
        var start = _pos;
        var pos = Current.Position;

        var isConst = Match(SyntaxKind.ConstKeyword);

        TypeSyntax? type = null;
        if (Current.Kind == SyntaxKind.VarKeyword)
        {
            Advance();
        }
        else
        {
            // A tuple type starts with '(' rather than a name.
            if (Current.Kind is not (SyntaxKind.Identifier or SyntaxKind.OpenParen) ||
                !TrySpeculateType(out type))
            {
                _pos = start;
                result = null;
                return false;
            }
        }

        if (Current.Kind != SyntaxKind.Identifier)
        {
            _pos = start;
            result = null;
            return false;
        }

        var nameToken = Peek(0);
        var following = Peek(1).Kind;
        if (following is not (SyntaxKind.Equals or SyntaxKind.Semicolon or SyntaxKind.Comma))
        {
            _pos = start;
            result = null;
            return false;
        }

        Advance(); // name
        ExpressionSyntax? initializer = Match(SyntaxKind.Equals) ? ParseExpression() : null;
        if (requireSemicolon) ExpectStatementEnd();

        result = new VariableDeclarationSyntax(pos, type, nameToken.Text, initializer, isConst);
        return true;
    }

    /// <summary>
    /// Speculatively parses <c>var (a, b) = e;</c> or <c>(int a, string b) = e;</c>. Assigning
    /// to variables that already exist is left to the ordinary assignment path.
    /// </summary>
    private bool TryParseDeconstruction(out StatementSyntax? result)
    {
        var start = _pos;
        var pos = Current.Position;

        result = null;

        // `var (a, b)` puts one var out front; the per-element forms are handled below.
        var allVar = Current.Kind == SyntaxKind.VarKeyword && Peek(1).Kind == SyntaxKind.OpenParen;
        if (allVar) Advance();

        if (!Match(SyntaxKind.OpenParen)) { _pos = start; return false; }

        var targets = new List<DeconstructionTargetSyntax>();
        var anyDeclares = allVar;

        while (Current.Kind is not (SyntaxKind.CloseParen or SyntaxKind.EndOfFile))
        {
            var targetPosition = Current.Position;
            TypeSyntax? type = null;
            var isVar = allVar;

            if (!allVar)
            {
                if (Match(SyntaxKind.VarKeyword)) isVar = true;
                else if (Peek(1).Kind is not (SyntaxKind.Comma or SyntaxKind.CloseParen) &&
                         !TrySpeculateType(out type))
                {
                    _pos = start;
                    return false;
                }
            }

            if (Current.Kind != SyntaxKind.Identifier) { _pos = start; return false; }

            anyDeclares |= isVar || type is not null;
            targets.Add(new DeconstructionTargetSyntax(targetPosition, type, isVar, Advance().Text));

            if (!Match(SyntaxKind.Comma)) break;
        }

        if (targets.Count < 2 ||
            !anyDeclares ||
            !Match(SyntaxKind.CloseParen) ||
            !Match(SyntaxKind.Equals))
        {
            _pos = start;
            return false;
        }

        var value = ParseExpression();
        ExpectStatementEnd();

        result = new DeconstructionStatementSyntax(pos, targets, value);
        return true;
    }

    private bool TrySpeculateType(out TypeSyntax? type)
    {
        var start = _pos;
        var errors = _diagnostics.Count;
        type = ParseType(speculative: true);

        if (type is null || _diagnostics.Count != errors)
        {
            _pos = start;
            type = null;
            return false;
        }
        return true;
    }

    // ============================================================ types

    private TypeSyntax ParseType() => ParseType(speculative: false) ?? new TypeSyntax(
        Current.Position, ["?"], [], false, 0);

    private TypeSyntax? ParseType(bool speculative)
    {
        var pos = Current.Position;

        if (Current.Kind == SyntaxKind.OpenParen) return ParseTupleType(speculative);

        if (Current.Kind != SyntaxKind.Identifier)
        {
            if (!speculative)
                _diagnostics.Report(ErrorCode.ExpectedIdentifier, pos, $"应为类型名，但遇到 '{Describe(Current)}'。");
            return null;
        }

        var parts = new List<string> { Advance().Text };
        while (Current.Kind == SyntaxKind.Dot && Peek(1).Kind == SyntaxKind.Identifier)
        {
            Advance();
            parts.Add(Advance().Text);
        }

        var typeArguments = new List<TypeSyntax>();
        if (Current.Kind == SyntaxKind.Less)
        {
            var save = _pos;
            Advance();
            var ok = true;
            while (true)
            {
                var argument = ParseType(speculative: true);
                if (argument is null) { ok = false; break; }
                typeArguments.Add(argument);
                if (Match(SyntaxKind.Comma)) continue;
                break;
            }

            if (!ok || !Match(SyntaxKind.Greater))
            {
                _pos = save;
                typeArguments.Clear();
            }
        }

        var nullable = Match(SyntaxKind.Question);

        var dimensions = ParseArraySuffixes();

        return new TypeSyntax(pos, parts, typeArguments, nullable, dimensions.Count, null, dimensions);
    }

    /// <summary>
    /// Reads the <c>[]</c> / <c>[,]</c> groups after a type name and returns each group's
    /// dimension count. Stops at anything that is not an empty bracket group, so <c>a[0]</c>
    /// is never mistaken for a type.
    /// </summary>
    private List<int> ParseArraySuffixes()
    {
        var dimensions = new List<int>();

        while (Current.Kind == SyntaxKind.OpenBracket)
        {
            var save = _pos;
            Advance();

            var rank = 1;
            while (Match(SyntaxKind.Comma)) rank++;

            if (!Match(SyntaxKind.CloseBracket))
            {
                _pos = save;
                break;
            }

            dimensions.Add(rank);
        }

        return dimensions;
    }

    /// <summary>
    /// <c>(int a, string b)</c>. It becomes a <c>ValueTuple&lt;...&gt;</c> whose element names
    /// are carried alongside, so the rest of the pipeline sees an ordinary generic type.
    /// </summary>
    private TypeSyntax? ParseTupleType(bool speculative)
    {
        var pos = Current.Position;
        var start = _pos;

        Advance(); // '('

        var elements = new List<TypeSyntax>();
        var names = new List<string?>();

        while (Current.Kind is not (SyntaxKind.CloseParen or SyntaxKind.EndOfFile))
        {
            var element = ParseType(speculative: true);
            if (element is null) { _pos = start; return Failed(pos, speculative); }

            elements.Add(element);
            names.Add(Current.Kind == SyntaxKind.Identifier ? Advance().Text : null);

            if (!Match(SyntaxKind.Comma)) break;
        }

        // A one-element tuple does not exist in C#, and `(x)` is far more likely a grouping.
        if (elements.Count < 2 || !Match(SyntaxKind.CloseParen))
        {
            _pos = start;
            return Failed(pos, speculative);
        }

        var nullable = Match(SyntaxKind.Question);

        var rank = 0;
        while (Current.Kind == SyntaxKind.OpenBracket && Peek(1).Kind == SyntaxKind.CloseBracket)
        {
            Advance();
            Advance();
            rank++;
        }

        return new TypeSyntax(pos, ["System", "ValueTuple"], elements, nullable, rank, names);
    }

    private TypeSyntax? Failed(SourcePosition position, bool speculative)
    {
        if (!speculative)
            _diagnostics.Report(ErrorCode.ExpectedIdentifier, position, "元组类型至少需要两个元素。");

        return null;
    }

    // ============================================================ expressions

    public ExpressionSyntax ParseExpression() => ParseAssignment();

    private ExpressionSyntax ParseAssignment()
    {
        var left = ParseConditional();

        if (IsAssignmentOperator(Current.Kind))
        {
            var op = Advance();
            var right = ParseAssignment(); // right associative
            return new AssignmentExpressionSyntax(left.Position, left, op.Kind, right);
        }

        return left;
    }

    private static bool IsAssignmentOperator(SyntaxKind kind) => kind is
        SyntaxKind.Equals or SyntaxKind.PlusEquals or SyntaxKind.MinusEquals or
        SyntaxKind.StarEquals or SyntaxKind.SlashEquals or SyntaxKind.PercentEquals or
        SyntaxKind.AmpEquals or SyntaxKind.PipeEquals or SyntaxKind.CaretEquals or
        SyntaxKind.LessLessEquals or SyntaxKind.GreaterGreaterEquals or
        SyntaxKind.QuestionQuestionEquals;

    private ExpressionSyntax ParseConditional()
    {
        var condition = ParseBinary(0);

        if (Current.Kind != SyntaxKind.Question) return condition;

        Advance();
        var whenTrue = ParseAssignment();
        Expect(SyntaxKind.Colon, "':'");
        var whenFalse = ParseAssignment();
        return new ConditionalExpressionSyntax(condition.Position, condition, whenTrue, whenFalse);
    }

    /// <summary>Binding power for binary operators; higher binds tighter.</summary>
    /// <summary>
    /// Whether an expression can begin here. Only the open-ended forms of a range need to ask,
    /// which is why a conservative "not a closer or an operator" answer is enough.
    /// </summary>
    private bool StartsExpression() => Current.Kind is not (
        SyntaxKind.CloseParen or SyntaxKind.CloseBracket or SyntaxKind.CloseBrace or
        SyntaxKind.Comma or SyntaxKind.Semicolon or SyntaxKind.Colon or SyntaxKind.EndOfFile);

    private static int Precedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.QuestionQuestion => 1,
        SyntaxKind.PipePipe => 2,
        SyntaxKind.AmpAmp => 3,
        SyntaxKind.Pipe => 4,
        SyntaxKind.Caret => 5,
        SyntaxKind.Amp => 6,
        SyntaxKind.EqualsEquals or SyntaxKind.BangEquals => 7,
        SyntaxKind.Less or SyntaxKind.LessEquals or SyntaxKind.Greater or SyntaxKind.GreaterEquals
            or SyntaxKind.IsKeyword or SyntaxKind.AsKeyword => 8,
        SyntaxKind.DotDot => 9,
        SyntaxKind.LessLess or SyntaxKind.GreaterGreater => 10,
        SyntaxKind.Plus or SyntaxKind.Minus => 11,
        SyntaxKind.Star or SyntaxKind.Slash or SyntaxKind.Percent => 12,
        _ => 0,
    };

    private ExpressionSyntax ParseBinary(int minPrecedence)
    {
        var left = ParseUnary();

        while (true)
        {
            var kind = Current.Kind;
            var consumed = 1;

            // The lexer never emits '>>' so that List<List<int>> closes correctly;
            // recombine two adjacent '>' tokens here instead.
            if (kind == SyntaxKind.Greater && Peek(1).Kind == SyntaxKind.Greater && IsAdjacent(Peek(0), Peek(1)))
            {
                kind = SyntaxKind.GreaterGreater;
                consumed = 2;
            }

            var precedence = Precedence(kind);
            if (precedence == 0 || precedence <= minPrecedence) break;

            if (kind == SyntaxKind.DotDot)
            {
                Advance();

                // Either end may be absent: `a..`, `..b`, and a bare `..` are all ranges.
                var end = StartsExpression() ? ParseBinary(precedence) : null;
                left = new RangeExpressionSyntax(left.Position, left, end);
                continue;
            }

            if (kind is SyntaxKind.IsKeyword or SyntaxKind.AsKeyword)
            {
                Advance();
                left = kind == SyntaxKind.IsKeyword
                    ? new IsExpressionSyntax(left.Position, left, ParsePattern())
                    : new AsExpressionSyntax(left.Position, left, ParseType());
                continue;
            }

            for (var i = 0; i < consumed; i++) Advance();

            // '??' is right associative; the rest are left associative.
            var right = kind == SyntaxKind.QuestionQuestion
                ? ParseBinary(precedence - 1)
                : ParseBinary(precedence);

            left = new BinaryExpressionSyntax(left.Position, left, kind, right);
        }

        return left;
    }

    private static bool IsAdjacent(Token a, Token b) =>
        a.Position.Line == b.Position.Line && b.Position.Column == a.Position.Column + a.Text.Length;

    private ExpressionSyntax ParseUnary()
    {
        var pos = Current.Position;

        switch (Current.Kind)
        {
            case SyntaxKind.Plus:
            case SyntaxKind.Minus:
            case SyntaxKind.Bang:
            case SyntaxKind.Tilde:
            case SyntaxKind.PlusPlus:
            case SyntaxKind.MinusMinus:
            {
                var op = Advance().Kind;
                return new UnaryExpressionSyntax(pos, op, ParseUnary());
            }
            case SyntaxKind.AwaitKeyword:
            {
                Advance();
                return new AwaitExpressionSyntax(pos, ParseUnary());
            }
            default:
                return ParsePostfix(ParsePrimary());
        }
    }

    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case SyntaxKind.Dot:
                {
                    Advance();
                    var name = Expect(SyntaxKind.Identifier, "成员名");
                    var typeArguments = TryParseCallTypeArguments();
                    expression = new MemberAccessExpressionSyntax(
                        expression.Position, expression, name.Text, IsNullConditional: false, typeArguments);
                    break;
                }
                case SyntaxKind.QuestionDot when Current.Text == "?":
                {
                    // '?[' — null-conditional element access
                    Advance();
                    Expect(SyntaxKind.OpenBracket, "'['");
                    var args = ParseIndexArguments();
                    expression = new IndexExpressionSyntax(
                        expression.Position, expression, args, IsNullConditional: true);
                    break;
                }
                case SyntaxKind.QuestionDot:
                {
                    Advance();
                    var name = Expect(SyntaxKind.Identifier, "成员名");
                    expression = new MemberAccessExpressionSyntax(
                        expression.Position, expression, name.Text, IsNullConditional: true);
                    break;
                }
                case SyntaxKind.OpenParen:
                {
                    Advance();
                    var args = ParseArguments();
                    var typeArguments = (expression as MemberAccessExpressionSyntax)?.TypeArguments;
                    expression = new InvocationExpressionSyntax(
                        expression.Position, expression, args, typeArguments);
                    break;
                }
                case SyntaxKind.OpenBracket:
                {
                    Advance();
                    var args = ParseIndexArguments();
                    expression = new IndexExpressionSyntax(
                        expression.Position, expression, args, IsNullConditional: false);
                    break;
                }
                case SyntaxKind.PlusPlus:
                case SyntaxKind.MinusMinus:
                {
                    var op = Advance().Kind;
                    expression = new PostfixExpressionSyntax(expression.Position, expression, op);
                    break;
                }
                case SyntaxKind.WithKeyword when Peek(1).Kind == SyntaxKind.OpenBrace:
                {
                    Advance();
                    var initializer = ParseInitializer();

                    if (initializer is not ObjectInitializerSyntax members)
                    {
                        _diagnostics.Report(ErrorCode.ExpectedToken, initializer.Position,
                            "with 只接受 { 成员 = 值 } 形式。");
                        return new ErrorExpressionSyntax(expression.Position);
                    }

                    expression = new WithExpressionSyntax(expression.Position, expression, members);
                    break;
                }

                case SyntaxKind.SwitchKeyword:
                {
                    expression = ParseSwitchExpression(expression);
                    break;
                }
                default:
                    return expression;
            }
        }
    }

    private List<ArgumentSyntax> ParseArguments()
    {
        var arguments = new List<ArgumentSyntax>();

        if (Current.Kind != SyntaxKind.CloseParen)
        {
            do
            {
                arguments.Add(ParseArgument());
            } while (Match(SyntaxKind.Comma));
        }

        Expect(SyntaxKind.CloseParen, "')'");
        return arguments;
    }

    /// <summary>
    /// <c>[name:] [ref|out] [var|Type] expr</c>. Only <c>out</c> may introduce a variable, which
    /// is the one place an argument list can declare something.
    /// </summary>
    private ArgumentSyntax ParseArgument()
    {
        var pos = Current.Position;

        string? name = null;
        if (Current.Kind == SyntaxKind.Identifier && Peek(1).Kind == SyntaxKind.Colon)
        {
            name = Advance().Text;
            Advance();
        }

        var refKind = ArgumentRefKind.None;
        if (Match(SyntaxKind.RefKeyword)) refKind = ArgumentRefKind.Ref;
        else if (Match(SyntaxKind.OutKeyword)) refKind = ArgumentRefKind.Out;

        if (refKind == ArgumentRefKind.Out && TryParseOutDeclaration(pos, name, out var declaration))
            return declaration!;

        return new ArgumentSyntax(pos, name, ParseExpression(), refKind);
    }

    private bool TryParseOutDeclaration(SourcePosition pos, string? name, out ArgumentSyntax? result)
    {
        var start = _pos;
        result = null;

        TypeSyntax? declaredType = null;
        if (Match(SyntaxKind.VarKeyword))
        {
            // `out var x`
        }
        else if (!TrySpeculateType(out declaredType))
        {
            return false;
        }

        if (Current.Kind != SyntaxKind.Identifier ||
            Peek(1).Kind is not (SyntaxKind.Comma or SyntaxKind.CloseParen))
        {
            _pos = start;
            return false;
        }

        var variable = Advance();

        result = new ArgumentSyntax(
            pos, name, new NameExpressionSyntax(variable.Position, variable.Text),
            ArgumentRefKind.Out, DeclaresVariable: true, declaredType);

        return true;
    }

    private List<ExpressionSyntax> ParseIndexArguments()
    {
        var arguments = new List<ExpressionSyntax>();
        if (Current.Kind != SyntaxKind.CloseBracket)
        {
            do { arguments.Add(ParseExpression()); } while (Match(SyntaxKind.Comma));
        }
        Expect(SyntaxKind.CloseBracket, "']'");
        return arguments;
    }

    // ============================================================ patterns

    /// <summary>
    /// <c>and</c>, <c>or</c>, <c>not</c> and <c>when</c> are contextual: they are ordinary
    /// identifiers everywhere else, so a script may still declare a variable called <c>not</c>.
    /// </summary>
    private bool IsContextual(string text) =>
        Current.Kind == SyntaxKind.Identifier && Current.Text == text;

    /// <summary>A designation cannot be one of the contextual pattern keywords.</summary>
    private bool AtDesignation() =>
        Current.Kind == SyntaxKind.Identifier &&
        !IsContextual("and") && !IsContextual("or") && !IsContextual("when");

    private PatternSyntax ParsePattern() => ParseOrPattern();

    private PatternSyntax ParseOrPattern()
    {
        var left = ParseAndPattern();

        while (IsContextual("or"))
        {
            Advance();
            left = new BinaryPatternSyntax(left.Position, left, IsAnd: false, ParseAndPattern());
        }

        return left;
    }

    private PatternSyntax ParseAndPattern()
    {
        var left = ParseUnaryPattern();

        while (IsContextual("and"))
        {
            Advance();
            left = new BinaryPatternSyntax(left.Position, left, IsAnd: true, ParseUnaryPattern());
        }

        return left;
    }

    private PatternSyntax ParseUnaryPattern()
    {
        if (!IsContextual("not")) return ParsePrimaryPattern();

        var pos = Advance().Position;
        return new NotPatternSyntax(pos, ParseUnaryPattern());
    }

    private PatternSyntax ParsePrimaryPattern()
    {
        var pos = Current.Position;

        switch (Current.Kind)
        {
            case SyntaxKind.OpenParen:
            {
                // `(int, int) (a, b)` starts with a tuple type, not with a pattern list. Only a
                // following '(' or '{' tells the two apart.
                if (TryParsePatternType(out var tupleType))
                {
                    if (Current.Kind == SyntaxKind.OpenParen &&
                        TryParsePositionalPattern(pos, tupleType, out var typedPositional))
                    {
                        return typedPositional!;
                    }

                    return ParsePropertyPattern(pos, tupleType);
                }

                if (TryParsePositionalPattern(pos, null, out var positional)) return positional!;

                Advance();
                var inner = ParsePattern();
                Expect(SyntaxKind.CloseParen, "')'");
                return new ParenthesizedPatternSyntax(pos, inner);
            }

            case SyntaxKind.OpenBracket:
                return ParseListPattern(pos);

            case SyntaxKind.OpenBrace:
                return ParsePropertyPattern(pos, null);

            case SyntaxKind.Less:
            case SyntaxKind.LessEquals:
            case SyntaxKind.Greater:
            case SyntaxKind.GreaterEquals:
            case SyntaxKind.EqualsEquals:
            case SyntaxKind.BangEquals:
            {
                var op = Advance().Kind;
                return new RelationalPatternSyntax(pos, op, ParseUnary());
            }

            case SyntaxKind.VarKeyword:
            {
                Advance();
                var name = Expect(SyntaxKind.Identifier, "标识符").Text;
                return new VarPatternSyntax(pos, name);
            }

            case SyntaxKind.NullKeyword:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.IntLiteral:
            case SyntaxKind.LongLiteral:
            case SyntaxKind.UIntLiteral:
            case SyntaxKind.ULongLiteral:
            case SyntaxKind.DoubleLiteral:
            case SyntaxKind.FloatLiteral:
            case SyntaxKind.DecimalLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.CharLiteral:
            case SyntaxKind.Minus:
                return new ConstantPatternSyntax(pos, ParseUnary());

            case SyntaxKind.Identifier when Current.Text == "_" &&
                                            Peek(1).Kind is not (SyntaxKind.Dot or SyntaxKind.Less
                                                or SyntaxKind.OpenBrace or SyntaxKind.Identifier):
                Advance();
                return new DiscardPatternSyntax(pos);

            case SyntaxKind.Identifier:
            {
                var type = ParseType();

                if (Current.Kind == SyntaxKind.OpenParen &&
                    TryParsePositionalPattern(pos, type, out var typed))
                {
                    return typed!;
                }

                if (Current.Kind == SyntaxKind.OpenBrace) return ParsePropertyPattern(pos, type);

                string? designation = null;
                if (AtDesignation()) designation = Advance().Text;

                // A name that carries type syntax cannot also be a constant.
                var mayBeConstant = designation is null &&
                                    !type.IsNullable &&
                                    type.ArrayRank == 0 &&
                                    type.TypeArguments.Count == 0;

                return new TypePatternSyntax(pos, type, designation, mayBeConstant);
            }

            default:
                _diagnostics.Report(ErrorCode.ExpectedPattern, pos,
                    $"应为模式，但遇到 '{Describe(Current)}'。");
                return new ErrorPatternSyntax(pos);
        }
    }

    /// <summary>
    /// Reads a parenthesised type when it is followed by something only a type can precede in
    /// pattern position. Restores the position and returns false otherwise.
    /// </summary>
    private bool TryParsePatternType(out TypeSyntax? type)
    {
        var start = _pos;

        if (!TrySpeculateType(out type) ||
            type is not { IsTuple: true } ||
            Current.Kind is not (SyntaxKind.OpenParen or SyntaxKind.OpenBrace))
        {
            _pos = start;
            type = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>(p1, p2)</c>. A single parenthesised pattern is a grouping rather than a one-element
    /// positional pattern, unless the element is named — which only a pattern list can be.
    /// </summary>
    private bool TryParsePositionalPattern(SourcePosition pos, TypeSyntax? type, out PatternSyntax? result)
    {
        var start = _pos;
        result = null;

        Advance(); // '('

        var subpatterns = new List<PositionalSubpatternSyntax>();

        while (Current.Kind is not (SyntaxKind.CloseParen or SyntaxKind.EndOfFile))
        {
            var subPosition = Current.Position;

            string? name = null;
            if (Current.Kind == SyntaxKind.Identifier && Peek(1).Kind == SyntaxKind.Colon)
            {
                name = Advance().Text;
                Advance();
            }

            subpatterns.Add(new PositionalSubpatternSyntax(subPosition, name, ParsePattern()));
            if (!Match(SyntaxKind.Comma)) break;
        }

        if (!Match(SyntaxKind.CloseParen)) { _pos = start; return false; }

        if (subpatterns.Count < 2 && subpatterns.All(s => s.Name is null) && type is null)
        {
            _pos = start;
            return false;
        }

        var properties = Current.Kind == SyntaxKind.OpenBrace
            ? ParsePropertySubpatterns()
            : [];

        string? designation = null;
        if (AtDesignation()) designation = Advance().Text;

        result = new PositionalPatternSyntax(pos, type, subpatterns, properties, designation);
        return true;
    }

    private PatternSyntax ParseListPattern(SourcePosition pos)
    {
        Expect(SyntaxKind.OpenBracket, "'['");

        var before = new List<PatternSyntax>();
        var after = new List<PatternSyntax>();
        var hasSlice = false;
        string? sliceDesignation = null;

        while (Current.Kind is not (SyntaxKind.CloseBracket or SyntaxKind.EndOfFile))
        {
            if (Current.Kind == SyntaxKind.DotDot)
            {
                if (hasSlice)
                {
                    _diagnostics.Report(ErrorCode.ExpectedPattern, Current.Position,
                        "列表模式最多只能有一个 '..'。");
                }

                Advance();
                hasSlice = true;

                if (Match(SyntaxKind.VarKeyword))
                    sliceDesignation = Expect(SyntaxKind.Identifier, "标识符").Text;
            }
            else if (hasSlice)
            {
                after.Add(ParsePattern());
            }
            else
            {
                before.Add(ParsePattern());
            }

            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBracket, "']'");

        string? designation = null;
        if (AtDesignation()) designation = Advance().Text;

        return new ListPatternSyntax(pos, before, hasSlice, sliceDesignation, after, designation);
    }

    private List<PropertySubpatternSyntax> ParsePropertySubpatterns()
    {
        Expect(SyntaxKind.OpenBrace, "'{'");

        var subpatterns = new List<PropertySubpatternSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
        {
            var subPosition = Current.Position;
            var name = Expect(SyntaxKind.Identifier, "属性名").Text;
            Expect(SyntaxKind.Colon, "':'");

            subpatterns.Add(new PropertySubpatternSyntax(subPosition, name, ParsePattern()));

            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBrace, "'}'");
        return subpatterns;
    }

    private PatternSyntax ParsePropertyPattern(SourcePosition pos, TypeSyntax? type)
    {
        Expect(SyntaxKind.OpenBrace, "'{'");

        var subpatterns = new List<PropertySubpatternSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
        {
            var subPosition = Current.Position;
            var name = Expect(SyntaxKind.Identifier, "属性名").Text;
            Expect(SyntaxKind.Colon, "':'");

            subpatterns.Add(new PropertySubpatternSyntax(subPosition, name, ParsePattern()));

            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBrace, "'}'");

        string? designation = null;
        if (AtDesignation()) designation = Advance().Text;

        return new PropertyPatternSyntax(pos, type, subpatterns, designation);
    }

    private ExpressionSyntax ParseCollectionExpression(SourcePosition pos)
    {
        Expect(SyntaxKind.OpenBracket, "'['");

        var elements = new List<CollectionElementSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBracket or SyntaxKind.EndOfFile))
        {
            var elementPos = Current.Position;
            var spread = Match(SyntaxKind.DotDot);

            elements.Add(new CollectionElementSyntax(elementPos, spread, ParseExpression()));
            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBracket, "']'");
        return new CollectionExpressionSyntax(pos, elements);
    }

    /// <summary>
    /// <c>switch (x) { case P when G: ... default: ... }</c>. A section runs until the next
    /// label or the closing brace; whether it may fall out of the bottom is the binder's call.
    /// </summary>
    private StatementSyntax ParseSwitchStatement()
    {
        var pos = Advance().Position; // 'switch'

        Expect(SyntaxKind.OpenParen, "'('");
        var governing = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");
        Expect(SyntaxKind.OpenBrace, "'{'");

        var sections = new List<SwitchSectionSyntax>();

        while (Current.Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
        {
            var sectionPosition = Current.Position;
            var labels = new List<SwitchLabelSyntax>();

            while (Current.Kind is SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword)
            {
                var labelPosition = Current.Position;

                if (Match(SyntaxKind.DefaultKeyword))
                {
                    Expect(SyntaxKind.Colon, "':'");
                    labels.Add(new SwitchLabelSyntax(labelPosition, null, null));
                    continue;
                }

                Advance(); // 'case'
                var pattern = ParsePattern();

                ExpressionSyntax? guard = null;
                if (IsContextual("when"))
                {
                    Advance();
                    guard = ParseExpression();
                }

                Expect(SyntaxKind.Colon, "':'");
                labels.Add(new SwitchLabelSyntax(labelPosition, pattern, guard));
            }

            var statements = new List<StatementSyntax>();
            while (Current.Kind is not (SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword
                                        or SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
            {
                var before = _pos;
                statements.Add(ParseStatement());
                if (_pos == before) Advance();
            }

            sections.Add(new SwitchSectionSyntax(sectionPosition, labels, statements));
        }

        Expect(SyntaxKind.CloseBrace, "'}'");
        return new SwitchStatementSyntax(pos, governing, sections);
    }

    private ExpressionSyntax ParseSwitchExpression(ExpressionSyntax governing)
    {
        var pos = Advance().Position; // 'switch'
        Expect(SyntaxKind.OpenBrace, "'{'");

        var arms = new List<SwitchArmSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
        {
            var armPosition = Current.Position;
            var pattern = ParsePattern();

            ExpressionSyntax? guard = null;
            if (IsContextual("when"))
            {
                Advance();
                guard = ParseExpression();
            }

            Expect(SyntaxKind.Arrow, "'=>'");
            arms.Add(new SwitchArmSyntax(armPosition, pattern, guard, ParseExpression()));

            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBrace, "'}'");

        if (arms.Count == 0)
        {
            _diagnostics.Report(ErrorCode.ExpectedPattern, pos,
                "switch 表达式至少需要一个分支。");
        }

        return new SwitchExpressionSyntax(pos, governing, arms);
    }

    // ============================================================ primaries

    private ExpressionSyntax ParsePrimary()
    {
        var pos = Current.Position;

        switch (Current.Kind)
        {
            case SyntaxKind.IntLiteral:
            case SyntaxKind.LongLiteral:
            case SyntaxKind.UIntLiteral:
            case SyntaxKind.ULongLiteral:
            case SyntaxKind.DoubleLiteral:
            case SyntaxKind.FloatLiteral:
            case SyntaxKind.DecimalLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.CharLiteral:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
                return new LiteralExpressionSyntax(pos, Advance());

            case SyntaxKind.Identifier when StartsQuery():
                return ParseQuery();

            case SyntaxKind.Identifier when Current.Text == "async" && StartsLambdaAfterAsync():
            {
                Advance();
                var lambda = ParseLambdaAfterAsync();
                return lambda;
            }

            case SyntaxKind.Identifier:
            {
                // single-parameter lambda: x => ...
                if (Peek(1).Kind == SyntaxKind.Arrow)
                {
                    var parameterToken = Advance();
                    return ParseLambda(
                        [new LambdaParameterSyntax(parameterToken.Position, null, parameterToken.Text)],
                        skipParen: false);
                }

                // Unlike C#, `nameof` in call position always means the operator — a global
                // or local of that name cannot be invoked. Reading it as a value still works.
                if (Current.Text == "nameof" && Peek(1).Kind == SyntaxKind.OpenParen)
                {
                    Advance();
                    Advance();
                    var operand = ParseExpression();
                    Expect(SyntaxKind.CloseParen, "')'");
                    return new NameOfExpressionSyntax(pos, operand);
                }

                return new NameExpressionSyntax(pos, Advance().Text);
            }

            case SyntaxKind.NewKeyword:
                return ParseObjectOrArrayCreation(pos);

            case SyntaxKind.InterpolatedStringLiteral:
                return ParseInterpolatedString(Advance());

            case SyntaxKind.OpenBracket:
                return ParseCollectionExpression(pos);

            case SyntaxKind.Caret:
            {
                Advance();
                return new FromEndExpressionSyntax(pos, ParseUnary());
            }

            case SyntaxKind.DotDot:
            {
                Advance();
                var end = StartsExpression() ? ParseBinary(Precedence(SyntaxKind.DotDot)) : null;
                return new RangeExpressionSyntax(pos, null, end);
            }

            case SyntaxKind.CheckedKeyword:
            case SyntaxKind.UncheckedKeyword:
            {
                var isChecked = Advance().Kind == SyntaxKind.CheckedKeyword;
                Expect(SyntaxKind.OpenParen, "'('");
                var operand = ParseExpression();
                Expect(SyntaxKind.CloseParen, "')'");
                return new CheckedExpressionSyntax(pos, isChecked, operand);
            }

            case SyntaxKind.ThrowKeyword:
                Advance();
                return new ThrowExpressionSyntax(pos, ParseExpression());

            case SyntaxKind.DefaultKeyword:
            {
                Advance();
                if (Current.Kind != SyntaxKind.OpenParen) return new DefaultExpressionSyntax(pos, null);

                Advance();
                var type = ParseType();
                Expect(SyntaxKind.CloseParen, "')'");
                return new DefaultExpressionSyntax(pos, type);
            }


            case SyntaxKind.TypeofKeyword:
            {
                Advance();
                Expect(SyntaxKind.OpenParen, "'('");
                var type = ParseType();
                Expect(SyntaxKind.CloseParen, "')'");
                return new TypeofExpressionSyntax(pos, type);
            }

            case SyntaxKind.OpenParen:
                return ParseParenthesized(pos);

            default:
                _diagnostics.Report(ErrorCode.ExpectedExpression, pos,
                    $"应为表达式，但遇到 '{Describe(Current)}'。");
                return new ErrorExpressionSyntax(pos);
        }
    }

    /// <summary>
    /// Distinguishes <c>new T(...)</c>, <c>new T { ... }</c>, <c>new T[n]</c>,
    /// <c>new T[] { ... }</c> and <c>new[] { ... }</c>.
    /// </summary>
    private ExpressionSyntax ParseObjectOrArrayCreation(SourcePosition pos)
    {
        Advance(); // 'new'

        // new[] { ... } — element type comes from the elements.
        if (Current.Kind == SyntaxKind.OpenBracket)
        {
            Advance();
            Expect(SyntaxKind.CloseBracket, "']'");
            return new ArrayCreationExpressionSyntax(pos, null, null, ParseArrayElements());
        }

        var type = ParseType();

        // new T[length] — ParseType only consumes empty bracket groups, so a sized rank is here.
        if (Current.Kind == SyntaxKind.OpenBracket)
        {
            Advance();

            var lengths = new List<ExpressionSyntax>();
            while (Current.Kind is not (SyntaxKind.CloseBracket or SyntaxKind.EndOfFile))
            {
                lengths.Add(ParseExpression());
                if (!Match(SyntaxKind.Comma)) break;
            }

            Expect(SyntaxKind.CloseBracket, "']'");

            // `new int[2][]` sizes the outer array; the empty groups after it are the element
            // type's own ranks.
            var extra = ParseArraySuffixes();
            if (extra.Count > 0)
            {
                type = type with
                {
                    ArrayRank = type.ArrayRank + extra.Count,
                    ArrayDimensions = [.. Enumerable.Range(0, type.ArrayRank).Select(type.DimensionsAt), .. extra],
                };
            }

            return new ArrayCreationExpressionSyntax(pos, type, lengths, null);
        }

        // new T[] { ... }
        if (type.ArrayRank > 0 && Current.Kind == SyntaxKind.OpenBrace)
        {
            var elementType = type with { ArrayRank = type.ArrayRank - 1 };
            return new ArrayCreationExpressionSyntax(pos, elementType, null, ParseArrayElements());
        }

        var arguments = Match(SyntaxKind.OpenParen) ? ParseArguments() : [];
        var initializer = Current.Kind == SyntaxKind.OpenBrace ? ParseInitializer() : null;

        return new ObjectCreationExpressionSyntax(pos, type, arguments, initializer);
    }

    private List<ExpressionSyntax> ParseArrayElements()
    {
        Expect(SyntaxKind.OpenBrace, "'{'");

        var elements = new List<ExpressionSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
        {
            elements.Add(ParseExpression());
            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBrace, "'}'");
        return elements;
    }

    /// <summary>
    /// An initializer is a member list when it starts with <c>Name =</c>, and an element list
    /// otherwise. An empty <c>{ }</c> is treated as a member list, which behaves identically.
    /// </summary>
    private InitializerSyntax ParseInitializer()
    {
        var pos = Expect(SyntaxKind.OpenBrace, "'{'").Position;

        var isMemberList = Current.Kind == SyntaxKind.CloseBrace ||
                           (Current.Kind == SyntaxKind.Identifier && Peek(1).Kind == SyntaxKind.Equals) ||
                           Current.Kind == SyntaxKind.OpenBracket;

        if (isMemberList)
        {
            var members = new List<MemberInitializerSyntax>();

            while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
            {
                members.Add(ParseMemberInitializer());
                if (!Match(SyntaxKind.Comma)) break;
            }

            Expect(SyntaxKind.CloseBrace, "'}'");
            return new ObjectInitializerSyntax(pos, members);
        }

        var elements = new List<ExpressionSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBrace or SyntaxKind.EndOfFile))
        {
            elements.Add(ParseExpression());
            if (!Match(SyntaxKind.Comma)) break;
        }

        Expect(SyntaxKind.CloseBrace, "'}'");
        return new CollectionInitializerSyntax(pos, elements);
    }

    /// <summary>
    /// <c>Name = v</c>, <c>[k] = v</c>, and the nested forms where the right-hand side is
    /// another initializer applied to whatever the member already holds.
    /// </summary>
    private MemberInitializerSyntax ParseMemberInitializer()
    {
        var pos = Current.Position;

        string? name = null;
        List<ExpressionSyntax>? index = null;

        if (Match(SyntaxKind.OpenBracket))
        {
            index = [];
            while (Current.Kind is not (SyntaxKind.CloseBracket or SyntaxKind.EndOfFile))
            {
                index.Add(ParseExpression());
                if (!Match(SyntaxKind.Comma)) break;
            }

            Expect(SyntaxKind.CloseBracket, "']'");
        }
        else
        {
            name = Expect(SyntaxKind.Identifier, "成员名").Text;
        }

        Expect(SyntaxKind.Equals, "'='");

        if (Current.Kind == SyntaxKind.OpenBrace)
            return new MemberInitializerSyntax(pos, name, index, null, ParseInitializer());

        return new MemberInitializerSyntax(pos, name, index, ParseExpression(), null);
    }

    /// <summary>
    /// Speculatively reads <c>&lt;T, U&gt;</c> when it is immediately followed by <c>(</c>,
    /// which is what separates a type-argument list from a chain of comparisons.
    /// </summary>
    private List<TypeSyntax>? TryParseCallTypeArguments()
    {
        if (Current.Kind != SyntaxKind.Less) return null;

        var start = _pos;
        var errors = _diagnostics.Count;
        Advance();

        var arguments = new List<TypeSyntax>();
        while (true)
        {
            var argument = ParseType(speculative: true);
            if (argument is null) { _pos = start; return null; }

            arguments.Add(argument);
            if (Match(SyntaxKind.Comma)) continue;
            break;
        }

        if (_diagnostics.Count != errors ||
            !Match(SyntaxKind.Greater) ||
            Current.Kind != SyntaxKind.OpenParen)
        {
            _pos = start;
            return null;
        }

        return arguments;
    }

    /// <summary>
    /// Turns the lexer's raw parts into an expression tree. Each hole is parsed with its own
    /// lexer seeded at the hole's position, so errors inside it report real coordinates.
    /// </summary>
    private ExpressionSyntax ParseInterpolatedString(Token token)
    {
        var raw = (IReadOnlyList<RawInterpolationPart>)token.Value!;
        var parts = new List<InterpolationPartSyntax>(raw.Count);

        foreach (var part in raw)
        {
            if (!part.IsHole)
            {
                parts.Add(new InterpolationPartSyntax(part.Position, part.Text, null, null, null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.Text))
            {
                _diagnostics.Report(ErrorCode.ExpectedExpression, part.Position, "插值项为空。");
                continue;
            }

            var tokens = new Lexer(part.Text, _diagnostics, part.Position).Tokenize();
            var inner = new Parser(tokens, _diagnostics);
            var value = inner.ParseExpression();

            if (inner.Current.Kind != SyntaxKind.EndOfFile)
            {
                _diagnostics.Report(ErrorCode.UnexpectedToken, inner.Current.Position,
                    $"插值项中有多余内容 '{Describe(inner.Current)}'。");
            }

            parts.Add(new InterpolationPartSyntax(
                part.Position, null, value, part.Alignment?.Trim(), part.Format));
        }

        return new InterpolatedStringExpressionSyntax(token.Position, parts);
    }

    private ExpressionSyntax ParseParenthesized(SourcePosition pos)
    {
        if (TryParseParenthesizedLambda(pos, out var lambda)) return lambda!;
        if (TryParseCast(pos, out var cast)) return cast!;

        Advance(); // '('

        var first = ParseTupleElement();

        // A parenthesized expression cannot contain a top-level comma, so one means a tuple.
        if (Current.Kind != SyntaxKind.Comma)
        {
            Expect(SyntaxKind.CloseParen, "')'");

            return first.Name is null
                ? new ParenthesizedExpressionSyntax(pos, first.Value)
                : new TupleExpressionSyntax(pos, [first]);
        }

        var elements = new List<TupleElementSyntax> { first };
        while (Match(SyntaxKind.Comma))
        {
            if (Current.Kind == SyntaxKind.CloseParen) break;
            elements.Add(ParseTupleElement());
        }

        Expect(SyntaxKind.CloseParen, "')'");
        return new TupleExpressionSyntax(pos, elements);
    }

    private TupleElementSyntax ParseTupleElement()
    {
        var position = Current.Position;

        // `a: 1` names the element. A conditional always has '?' before its ':', so an
        // identifier immediately followed by ':' is unambiguous here.
        string? name = null;
        if (Current.Kind == SyntaxKind.Identifier && Peek(1).Kind == SyntaxKind.Colon)
        {
            name = Advance().Text;
            Advance();
        }

        return new TupleElementSyntax(position, name, ParseExpression());
    }

    private bool TryParseParenthesizedLambda(SourcePosition pos, out ExpressionSyntax? result, bool isAsync = false)
    {
        var start = _pos;
        Advance(); // '('

        var parameters = new List<LambdaParameterSyntax>();
        if (Current.Kind != SyntaxKind.CloseParen)
        {
            while (true)
            {
                if (!TryParseLambdaParameter(out var parameter)) { _pos = start; result = null; return false; }

                parameters.Add(parameter!);
                if (Match(SyntaxKind.Comma)) continue;
                break;
            }
        }

        if (!Match(SyntaxKind.CloseParen) || Current.Kind != SyntaxKind.Arrow)
        {
            _pos = start;
            result = null;
            return false;
        }

        result = ParseLambda(parameters, skipParen: true, isAsync);
        _ = pos;
        return true;
    }

    /// <summary>
    /// A lambda parameter is <c>name</c> or <c>Type name</c>. The type is tried first, and only
    /// kept when a name actually follows it — otherwise <c>(a, b)</c> would read <c>a</c> as a type.
    /// </summary>
    private bool TryParseLambdaParameter(out LambdaParameterSyntax? parameter)
    {
        var position = Current.Position;
        var start = _pos;

        if (Current.Kind == SyntaxKind.Identifier && TrySpeculateType(out var type) &&
            Current.Kind == SyntaxKind.Identifier)
        {
            parameter = new LambdaParameterSyntax(position, type, Advance().Text);
            return true;
        }

        _pos = start;

        if (Current.Kind != SyntaxKind.Identifier)
        {
            parameter = null;
            return false;
        }

        parameter = new LambdaParameterSyntax(position, null, Advance().Text);
        return true;
    }

    /// <summary>
    /// Speculatively parses <c>ReturnType Name(Type p, ...) body</c>. Local functions are the
    /// only statement that starts with a type and reaches an open paren.
    /// </summary>
    private bool TryParseLocalFunction(out StatementSyntax? result)
    {
        var start = _pos;
        var pos = Current.Position;

        result = null;

        // `static` may lead, and only a local function can start with it here.
        var isStatic = Match(SyntaxKind.StaticKeyword);

        if (Current.Kind is not (SyntaxKind.Identifier or SyntaxKind.OpenParen))
        {
            _pos = start;
            return false;
        }

        var isAsync = Current.Kind == SyntaxKind.Identifier && Current.Text == "async" &&
                      Peek(1).Kind is SyntaxKind.Identifier or SyntaxKind.OpenParen;

        if (isAsync) Advance();

        if (isStatic && Current.Kind is not (SyntaxKind.Identifier or SyntaxKind.OpenParen))
        {
            _pos = start;
            return false;
        }

        var isVoid = Current.Kind == SyntaxKind.Identifier &&
                     Current.Text == "void" && Peek(1).Kind == SyntaxKind.Identifier;

        TypeSyntax? returnType = null;
        if (isVoid) Advance();
        else if (!TrySpeculateType(out returnType)) { _pos = start; return false; }

        if (Current.Kind != SyntaxKind.Identifier || Peek(1).Kind != SyntaxKind.OpenParen)
        {
            _pos = start;
            return false;
        }

        var name = Advance().Text;
        Advance(); // '('

        var parameters = new List<LambdaParameterSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseParen or SyntaxKind.EndOfFile))
        {
            var parameterPosition = Current.Position;

            if (!TrySpeculateType(out var parameterType) || Current.Kind != SyntaxKind.Identifier)
            {
                _pos = start;
                return false;
            }

            parameters.Add(new LambdaParameterSyntax(parameterPosition, parameterType, Advance().Text));
            if (!Match(SyntaxKind.Comma)) break;
        }

        if (!Match(SyntaxKind.CloseParen)) { _pos = start; return false; }

        SyntaxNode body;
        if (Current.Kind == SyntaxKind.OpenBrace)
        {
            body = ParseBlock();
        }
        else if (Match(SyntaxKind.Arrow))
        {
            body = ParseExpression();
            ExpectStatementEnd();
        }
        else
        {
            _pos = start;
            return false;
        }

        result = new LocalFunctionStatementSyntax(pos, returnType, name, parameters, body, isAsync, isStatic);
        return true;
    }

    /// <summary>
    /// Is <c>async</c> here the modifier of a lambda rather than an ordinary name? Only the
    /// shape that follows it can say.
    /// </summary>
    private bool StartsLambdaAfterAsync()
    {
        var next = Peek(1);

        if (next.Kind == SyntaxKind.Identifier && Peek(2).Kind == SyntaxKind.Arrow) return true;
        if (next.Kind != SyntaxKind.OpenParen) return false;

        var start = _pos;
        Advance();

        var isLambda = TryParseParenthesizedLambda(Current.Position, out _);
        _pos = start;

        return isLambda;
    }

    private ExpressionSyntax ParseLambdaAfterAsync()
    {
        if (Current.Kind == SyntaxKind.Identifier)
        {
            var parameter = Advance();
            return ParseLambda(
                [new LambdaParameterSyntax(parameter.Position, null, parameter.Text)],
                skipParen: false, isAsync: true);
        }

        var position = Current.Position;
        TryParseParenthesizedLambda(position, out var lambda, isAsync: true);

        return lambda!;
    }

    private ExpressionSyntax ParseLambda(
        IReadOnlyList<LambdaParameterSyntax> parameters,
        bool skipParen,
        bool isAsync = false)
    {
        _ = skipParen;
        var pos = Current.Position;
        Expect(SyntaxKind.Arrow, "'=>'");

        SyntaxNode body = Current.Kind == SyntaxKind.OpenBrace
            ? ParseBlock()
            : ParseExpression();

        return new LambdaExpressionSyntax(pos, parameters, body, isAsync);
    }

    private bool TryParseCast(SourcePosition pos, out ExpressionSyntax? result)
    {
        var start = _pos;
        var errors = _diagnostics.Count;

        Advance(); // '('
        var type = ParseType(speculative: true);

        if (type is null || _diagnostics.Count != errors || !Match(SyntaxKind.CloseParen))
        {
            _pos = start;
            result = null;
            return false;
        }

        if (!CanFollowCast(type))
        {
            _pos = start;
            result = null;
            return false;
        }

        result = new CastExpressionSyntax(pos, type, ParseUnary());
        return true;
    }

    /// <summary>
    /// Decides whether <c>(X)</c> introduces a cast. For a built-in type name it always does,
    /// which is what separates <c>(int)-1</c> from <c>(a)-1</c>.
    /// </summary>
    private bool CanFollowCast(TypeSyntax type)
    {
        var unambiguousType = type.ArrayRank > 0
                              || type.IsNullable
                              || type.TypeArguments.Count > 0
                              || (type.NameParts.Count == 1 && BuiltInTypeNames.Contains(type.NameParts[0]));

        return Current.Kind switch
        {
            SyntaxKind.Identifier or SyntaxKind.OpenParen or SyntaxKind.Bang or SyntaxKind.Tilde
                or SyntaxKind.NewKeyword or SyntaxKind.AwaitKeyword or SyntaxKind.TypeofKeyword
                or SyntaxKind.IntLiteral or SyntaxKind.LongLiteral or SyntaxKind.UIntLiteral
                or SyntaxKind.ULongLiteral or SyntaxKind.DoubleLiteral or SyntaxKind.FloatLiteral
                or SyntaxKind.DecimalLiteral or SyntaxKind.StringLiteral or SyntaxKind.CharLiteral
                or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword => true,

            SyntaxKind.Minus or SyntaxKind.Plus => unambiguousType,
            _ => false,
        };
    }
}
