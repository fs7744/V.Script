using System.Collections.Frozen;
using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// Recursive-descent parser with precedence climbing for expressions.
/// Ambiguous constructs (declaration vs. expression, cast vs. parenthesis, lambda vs. group)
/// are resolved by speculative parsing with backtracking.
/// </summary>
public sealed class Parser
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

    private Token Current => Peek(0);

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

    private StatementSyntax ParseDeclarationOrExpressionStatement()
    {
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

        TypeSyntax? type = null;
        if (Current.Kind == SyntaxKind.VarKeyword)
        {
            Advance();
        }
        else
        {
            if (Current.Kind != SyntaxKind.Identifier || !TrySpeculateType(out type))
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

        result = new VariableDeclarationSyntax(pos, type, nameToken.Text, initializer);
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

        var rank = 0;
        while (Current.Kind == SyntaxKind.OpenBracket && Peek(1).Kind == SyntaxKind.CloseBracket)
        {
            Advance();
            Advance();
            rank++;
        }

        return new TypeSyntax(pos, parts, typeArguments, nullable, rank);
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
        SyntaxKind.LessLess or SyntaxKind.GreaterGreater => 9,
        SyntaxKind.Plus or SyntaxKind.Minus => 10,
        SyntaxKind.Star or SyntaxKind.Slash or SyntaxKind.Percent => 11,
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
                    expression = new MemberAccessExpressionSyntax(
                        expression.Position, expression, name.Text, IsNullConditional: false);
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
                    expression = new InvocationExpressionSyntax(expression.Position, expression, args);
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
                var pos = Current.Position;
                string? name = null;
                if (Current.Kind == SyntaxKind.Identifier && Peek(1).Kind == SyntaxKind.Colon)
                {
                    name = Advance().Text;
                    Advance();
                }
                arguments.Add(new ArgumentSyntax(pos, name, ParseExpression()));
            } while (Match(SyntaxKind.Comma));
        }

        Expect(SyntaxKind.CloseParen, "')'");
        return arguments;
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
                Advance();
                var inner = ParsePattern();
                Expect(SyntaxKind.CloseParen, "')'");
                return new ParenthesizedPatternSyntax(pos, inner);
            }

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

            case SyntaxKind.Identifier:
            {
                // single-parameter lambda: x => ...
                if (Peek(1).Kind == SyntaxKind.Arrow)
                    return ParseLambda([Advance().Text], skipParen: false);

                return new NameExpressionSyntax(pos, Advance().Text);
            }

            case SyntaxKind.NewKeyword:
            {
                Advance();
                var type = ParseType();
                var args = Match(SyntaxKind.OpenParen) ? ParseArguments() : [];
                return new ObjectCreationExpressionSyntax(pos, type, args);
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

    private ExpressionSyntax ParseParenthesized(SourcePosition pos)
    {
        if (TryParseParenthesizedLambda(pos, out var lambda)) return lambda!;
        if (TryParseCast(pos, out var cast)) return cast!;

        Advance(); // '('
        var inner = ParseExpression();
        Expect(SyntaxKind.CloseParen, "')'");
        return new ParenthesizedExpressionSyntax(pos, inner);
    }

    private bool TryParseParenthesizedLambda(SourcePosition pos, out ExpressionSyntax? result)
    {
        var start = _pos;
        Advance(); // '('

        var parameters = new List<string>();
        if (Current.Kind != SyntaxKind.CloseParen)
        {
            while (true)
            {
                if (Current.Kind != SyntaxKind.Identifier) { _pos = start; result = null; return false; }
                parameters.Add(Advance().Text);
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

        result = ParseLambda(parameters, skipParen: true);
        _ = pos;
        return true;
    }

    private ExpressionSyntax ParseLambda(IReadOnlyList<string> parameters, bool skipParen)
    {
        _ = skipParen;
        var pos = Current.Position;
        Expect(SyntaxKind.Arrow, "'=>'");

        SyntaxNode body = Current.Kind == SyntaxKind.OpenBrace
            ? ParseBlock()
            : ParseExpression();

        return new LambdaExpressionSyntax(pos, parameters, body);
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
