using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Tests;

public sealed class LexerTests
{
    private static List<Token> Lex(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        return new Lexer(text, diagnostics).Tokenize();
    }

    private static Token First(string text)
    {
        var tokens = Lex(text, out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join(" | ", diagnostics));
        return tokens[0];
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("1_000_000", 1_000_000)]
    [InlineData("0xFF", 255)]
    [InlineData("0b1010", 10)]
    public void Integer_literals(string text, int expected)
    {
        var token = First(text);
        Assert.Equal(SyntaxKind.IntLiteral, token.Kind);
        Assert.Equal(expected, token.Value);
    }

    [Fact]
    public void Literal_suffixes_select_the_type()
    {
        Assert.Equal(SyntaxKind.LongLiteral, First("1L").Kind);
        Assert.Equal(SyntaxKind.ULongLiteral, First("1UL").Kind);
        Assert.Equal(SyntaxKind.UIntLiteral, First("1u").Kind);
        Assert.Equal(SyntaxKind.FloatLiteral, First("1.5f").Kind);
        Assert.Equal(SyntaxKind.DoubleLiteral, First("1.5d").Kind);
        Assert.Equal(SyntaxKind.DecimalLiteral, First("1.5m").Kind);
    }

    [Fact]
    public void Large_literals_widen_automatically()
    {
        Assert.Equal(SyntaxKind.LongLiteral, First("3000000000").Kind);
        Assert.Equal(3_000_000_000L, First("3000000000").Value);
    }

    [Fact]
    public void Real_literals_and_exponents()
    {
        Assert.Equal(1.5, First("1.5").Value);
        Assert.Equal(1000.0, First("1e3").Value);
        Assert.Equal(0.001, First("1e-3").Value);
    }

    [Fact]
    public void String_escapes_are_decoded()
    {
        Assert.Equal("a\tb", First("\"a\\tb\"").Value);
        Assert.Equal("\"", First("\"\\\"\"").Value);
        Assert.Equal("\\", First("\"\\\\\"").Value);
        Assert.Equal("A", First("\"\\u0041\"").Value);
    }

    [Fact]
    public void Char_literals()
    {
        Assert.Equal('x', First("'x'").Value);
        Assert.Equal('\n', First("'\\n'").Value);
    }

    [Fact]
    public void Keywords_are_recognised()
    {
        Assert.Equal(SyntaxKind.IfKeyword, First("if").Kind);
        Assert.Equal(SyntaxKind.AwaitKeyword, First("await").Kind);
        Assert.Equal(SyntaxKind.TrueKeyword, First("true").Kind);
    }

    [Fact]
    public void Verbatim_identifier_escapes_a_keyword()
    {
        var token = First("@if");
        Assert.Equal(SyntaxKind.Identifier, token.Kind);
        Assert.Equal("if", token.Text);
    }

    [Fact]
    public void Comments_are_skipped()
    {
        var tokens = Lex("1 // trailing\n+ /* inner */ 2", out var diagnostics);
        Assert.False(diagnostics.HasErrors);
        Assert.Equal(
            [SyntaxKind.IntLiteral, SyntaxKind.Plus, SyntaxKind.IntLiteral, SyntaxKind.EndOfFile],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Positions_track_lines_and_columns()
    {
        var tokens = Lex("1\n  + 2", out _);
        Assert.Equal(new SourcePosition(1, 1), tokens[0].Position);
        Assert.Equal(new SourcePosition(2, 3), tokens[1].Position);
        Assert.Equal(new SourcePosition(2, 5), tokens[2].Position);
    }

    [Fact]
    public void Multi_character_operators()
    {
        Assert.Equal(SyntaxKind.EqualsEquals, First("==").Kind);
        Assert.Equal(SyntaxKind.QuestionQuestion, First("??").Kind);
        Assert.Equal(SyntaxKind.QuestionQuestionEquals, First("??=").Kind);
        Assert.Equal(SyntaxKind.QuestionDot, First("?.").Kind);
        Assert.Equal(SyntaxKind.Arrow, First("=>").Kind);
        Assert.Equal(SyntaxKind.PlusPlus, First("++").Kind);
        Assert.Equal(SyntaxKind.LessLess, First("<<").Kind);
    }

    [Fact]
    public void Greater_greater_is_left_to_the_parser()
    {
        // Emitting two '>' keeps List<List<int>> closable; the parser recombines them.
        var tokens = Lex(">>", out _);
        Assert.Equal(SyntaxKind.Greater, tokens[0].Kind);
        Assert.Equal(SyntaxKind.Greater, tokens[1].Kind);
    }

    [Fact]
    public void Unterminated_string_reports_an_error()
    {
        Lex("\"abc", out var diagnostics);
        Assert.Contains(diagnostics, d => d.Id == ErrorCode.UnterminatedString);
    }

    [Fact]
    public void Unterminated_block_comment_reports_an_error()
    {
        Lex("/* abc", out var diagnostics);
        Assert.Contains(diagnostics, d => d.Id == ErrorCode.UnterminatedComment);
    }

    [Fact]
    public void Unknown_character_reports_an_error()
    {
        Lex("#", out var diagnostics);
        Assert.Contains(diagnostics, d => d.Id == ErrorCode.UnexpectedCharacter);
    }
}

public sealed class ParserTests
{
    private static CompilationUnitSyntax Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, diagnostics).Tokenize();
        return new Parser(tokens, diagnostics).ParseCompilationUnit();
    }

    private static CompilationUnitSyntax ParseOk(string text)
    {
        var unit = Parse(text, out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join(" | ", diagnostics));
        return unit;
    }

    [Fact]
    public void Binary_operators_are_left_associative()
    {
        var unit = ParseOk("1 - 2 - 3");
        var expression = Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression;
        var outer = Assert.IsType<BinaryExpressionSyntax>(expression);

        Assert.IsType<BinaryExpressionSyntax>(outer.Left);
        Assert.IsType<LiteralExpressionSyntax>(outer.Right);
    }

    [Fact]
    public void Coalesce_is_right_associative()
    {
        var unit = ParseOk("a ?? b ?? c");
        var expression = Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression;
        var outer = Assert.IsType<BinaryExpressionSyntax>(expression);

        Assert.IsType<NameExpressionSyntax>(outer.Left);
        Assert.IsType<BinaryExpressionSyntax>(outer.Right);
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var unit = ParseOk("1 + 2 * 3");
        var outer = Assert.IsType<BinaryExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression);

        Assert.Equal(SyntaxKind.Plus, outer.Operator);
        Assert.IsType<BinaryExpressionSyntax>(outer.Right);
    }

    [Fact]
    public void Declaration_is_distinguished_from_a_comparison()
    {
        // 'a < b' must parse as an expression, not as a declaration of a generic type.
        var unit = ParseOk("a < b;");
        Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]);
    }

    [Fact]
    public void Generic_type_declaration_parses_as_a_declaration()
    {
        var unit = ParseOk("List<int> xs = null;");
        var declaration = Assert.IsType<VariableDeclarationSyntax>(unit.Statements[0]);
        Assert.Equal("List<int>", declaration.Type!.DisplayName);
    }

    [Fact]
    public void Nested_generics_close_correctly()
    {
        var unit = ParseOk("List<List<int>> xs = null;");
        var declaration = Assert.IsType<VariableDeclarationSyntax>(unit.Statements[0]);
        Assert.Equal("List<List<int>>", declaration.Type!.DisplayName);
    }

    [Fact]
    public void Nullable_and_array_type_suffixes()
    {
        Assert.Equal("int?", Assert.IsType<VariableDeclarationSyntax>(
            ParseOk("int? a = null;").Statements[0]).Type!.DisplayName);

        Assert.Equal("int[]", Assert.IsType<VariableDeclarationSyntax>(
            ParseOk("int[] a = null;").Statements[0]).Type!.DisplayName);
    }

    [Fact]
    public void Cast_of_a_builtin_type_wins_over_subtraction()
    {
        var unit = ParseOk("(int)-1;");
        var expression = Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression;
        Assert.IsType<CastExpressionSyntax>(expression);
    }

    [Fact]
    public void Parenthesised_name_minus_value_stays_a_subtraction()
    {
        var unit = ParseOk("(a)-1;");
        var expression = Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression;
        Assert.IsType<BinaryExpressionSyntax>(expression);
    }

    [Fact]
    public void Lambdas_are_parsed_even_though_binding_rejects_them()
    {
        var single = Assert.IsType<ExpressionStatementSyntax>(ParseOk("x => x;").Statements[0]).Expression;
        Assert.IsType<LambdaExpressionSyntax>(single);

        var multiple = Assert.IsType<ExpressionStatementSyntax>(ParseOk("(a, b) => a;").Statements[0]).Expression;
        Assert.Equal(2, Assert.IsType<LambdaExpressionSyntax>(multiple).Parameters.Count);
    }

    [Fact]
    public void Shift_operator_is_recombined_from_two_tokens()
    {
        var unit = ParseOk("1 >> 2;");
        var outer = Assert.IsType<BinaryExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression);

        Assert.Equal(SyntaxKind.GreaterGreater, outer.Operator);
    }

    [Fact]
    public void Named_arguments_are_captured()
    {
        var unit = ParseOk("f(a: 1, 2);");
        var invocation = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(unit.Statements[0]).Expression);

        Assert.Equal("a", invocation.Arguments[0].Name);
        Assert.Null(invocation.Arguments[1].Name);
    }

    [Fact]
    public void Trailing_semicolon_is_optional_at_the_end_of_a_script()
    {
        var unit = ParseOk("1 + 2");
        Assert.Single(unit.Statements);
    }

    [Fact]
    public void Missing_semicolon_in_the_middle_is_reported()
    {
        Parse("var a = 1 var b = 2;", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Parser_recovers_and_keeps_reporting()
    {
        Parse("var a = ; var b = ; var c = ;", out var diagnostics);
        Assert.True(diagnostics.Count >= 2);
    }

    [Fact]
    public void Try_without_catch_or_finally_is_reported()
    {
        Parse("try { }", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
    }
}
