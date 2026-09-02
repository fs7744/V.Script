using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// Converts script source into a token stream. Scans over the source span without
/// allocating per character; only literal text and identifiers materialize strings.
/// </summary>
public sealed class Lexer
{
    private static readonly FrozenDictionary<string, SyntaxKind> Keywords =
        new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
        {
            ["true"] = SyntaxKind.TrueKeyword,
            ["false"] = SyntaxKind.FalseKeyword,
            ["null"] = SyntaxKind.NullKeyword,
            ["var"] = SyntaxKind.VarKeyword,
            ["if"] = SyntaxKind.IfKeyword,
            ["else"] = SyntaxKind.ElseKeyword,
            ["while"] = SyntaxKind.WhileKeyword,
            ["do"] = SyntaxKind.DoKeyword,
            ["for"] = SyntaxKind.ForKeyword,
            ["foreach"] = SyntaxKind.ForeachKeyword,
            ["in"] = SyntaxKind.InKeyword,
            ["return"] = SyntaxKind.ReturnKeyword,
            ["break"] = SyntaxKind.BreakKeyword,
            ["continue"] = SyntaxKind.ContinueKeyword,
            ["try"] = SyntaxKind.TryKeyword,
            ["catch"] = SyntaxKind.CatchKeyword,
            ["finally"] = SyntaxKind.FinallyKeyword,
            ["throw"] = SyntaxKind.ThrowKeyword,
            ["new"] = SyntaxKind.NewKeyword,
            ["await"] = SyntaxKind.AwaitKeyword,
            ["is"] = SyntaxKind.IsKeyword,
            ["as"] = SyntaxKind.AsKeyword,
            ["typeof"] = SyntaxKind.TypeofKeyword,
            ["switch"] = SyntaxKind.SwitchKeyword,
            ["case"] = SyntaxKind.CaseKeyword,
            ["default"] = SyntaxKind.DefaultKeyword,
            ["ref"] = SyntaxKind.RefKeyword,
            ["out"] = SyntaxKind.OutKeyword,
            ["checked"] = SyntaxKind.CheckedKeyword,
            ["unchecked"] = SyntaxKind.UncheckedKeyword,
            ["using"] = SyntaxKind.UsingKeyword,
            ["lock"] = SyntaxKind.LockKeyword,
            ["goto"] = SyntaxKind.GotoKeyword,
            ["const"] = SyntaxKind.ConstKeyword,
            ["static"] = SyntaxKind.StaticKeyword,
            ["with"] = SyntaxKind.WithKeyword,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly SearchValues<char> DigitChars =
        SearchValues.Create("0123456789");

    private readonly string _text;
    private readonly DiagnosticBag _diagnostics;
    private readonly SourcePosition _origin;
    private int _pos;
    private int _line = 1;
    private int _lineStart;

    public Lexer(string text, DiagnosticBag diagnostics)
        : this(text, diagnostics, new SourcePosition(1, 1))
    {
    }

    /// <summary>
    /// Scans <paramref name="text"/> as if it began at <paramref name="origin"/>. Interpolation
    /// holes are lexed this way so their diagnostics carry positions in the original script.
    /// </summary>
    public Lexer(string text, DiagnosticBag diagnostics, SourcePosition origin)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _diagnostics = diagnostics;
        _origin = origin;
    }

    private char Current => _pos < _text.Length ? _text[_pos] : '\0';

    private char Peek(int offset = 1) =>
        _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private SourcePosition Here
    {
        get
        {
            var column = _pos - _lineStart + 1;
            return _line == 1
                ? new SourcePosition(_origin.Line, _origin.Column + column - 1)
                : new SourcePosition(_origin.Line + _line - 1, column);
        }
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Kind == SyntaxKind.EndOfFile)
                return tokens;
        }
    }

    private Token Next()
    {
        SkipTrivia();

        var start = Here;
        if (_pos >= _text.Length)
            return new Token(SyntaxKind.EndOfFile, string.Empty, start);

        var c = Current;

        // The @"..." forms have to be recognised before @identifier.
        if (c == '@' && Peek() == '"') return ReadVerbatimString(start);
        if (c == '@' && Peek() == '$' && Peek(2) == '"') return ReadInterpolatedString(start, verbatim: true);
        if (c == '$' && Peek() == '@' && Peek(2) == '"') return ReadInterpolatedString(start, verbatim: true);

        if (char.IsLetter(c) || c == '_' || c == '@')
            return ReadIdentifierOrKeyword(start);

        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek())))
            return ReadNumber(start);

        if (c == '$' && StartsRawInterpolation()) return ReadRawInterpolatedString(start);
        if (c == '"' && Peek() == '"' && Peek(2) == '"') return ReadRawString(start);
        if (c == '$' && Peek() == '"') return ReadInterpolatedString(start);

        return c switch
        {
            '"' => ReadString(start),
            '\'' => ReadChar(start),
            _ => ReadPunctuation(start),
        };
    }

    private void SkipTrivia()
    {
        while (_pos < _text.Length)
        {
            var c = Current;
            if (c == '\n')
            {
                _pos++;
                _line++;
                _lineStart = _pos;
            }
            else if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && Peek() == '/')
            {
                while (_pos < _text.Length && Current != '\n') _pos++;
            }
            else if (c == '/' && Peek() == '*')
            {
                var start = Here;
                _pos += 2;
                while (true)
                {
                    if (_pos >= _text.Length)
                    {
                        _diagnostics.Report(ErrorCode.UnterminatedComment, start, "块注释未闭合，缺少 '*/'。");
                        return;
                    }
                    if (Current == '*' && Peek() == '/') { _pos += 2; break; }
                    if (Current == '\n') { _line++; _lineStart = _pos + 1; }
                    _pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private Token ReadIdentifierOrKeyword(SourcePosition start)
    {
        var verbatim = Current == '@';
        if (verbatim) _pos++;

        var begin = _pos;
        while (_pos < _text.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
            _pos++;

        var text = _text[begin.._pos];
        if (text.Length == 0)
        {
            _diagnostics.Report(ErrorCode.ExpectedIdentifier, start, "'@' 之后需要标识符。");
            return new Token(SyntaxKind.BadToken, "@", start);
        }

        if (!verbatim && Keywords.TryGetValue(text, out var keyword))
        {
            object? value = keyword switch
            {
                SyntaxKind.TrueKeyword => true,
                SyntaxKind.FalseKeyword => false,
                _ => null,
            };
            return new Token(keyword, text, start, value);
        }

        return new Token(SyntaxKind.Identifier, text, start);
    }

    private Token ReadNumber(SourcePosition start)
    {
        var begin = _pos;

        if (Current == '0' && (Peek() is 'x' or 'X'))
            return ReadHex(start);
        if (Current == '0' && (Peek() is 'b' or 'B'))
            return ReadBinary(start);

        ScanDigits();

        var isReal = false;
        if (Current == '.' && char.IsAsciiDigit(Peek()))
        {
            isReal = true;
            _pos++;
            ScanDigits();
        }

        if (Current is 'e' or 'E')
        {
            var save = _pos;
            _pos++;
            if (Current is '+' or '-') _pos++;
            if (char.IsAsciiDigit(Current)) { isReal = true; ScanDigits(); }
            else _pos = save;
        }

        var digits = _text[begin.._pos];
        var suffix = ReadNumericSuffix();
        var raw = _text[begin.._pos];

        return DecodeNumber(digits, suffix, isReal, raw, start);
    }

    private void ScanDigits()
    {
        while (_pos < _text.Length &&
               (DigitChars.Contains(Current) || (Current == '_' && DigitChars.Contains(Peek()))))
            _pos++;
    }

    private string ReadNumericSuffix()
    {
        var begin = _pos;
        while (_pos < _text.Length && "uUlLfFdDmM".Contains(Current))
            _pos++;
        return _text[begin.._pos];
    }

    private Token DecodeNumber(string digits, string suffix, bool isReal, string raw, SourcePosition start)
    {
        digits = digits.Replace("_", string.Empty);
        var s = suffix.ToUpperInvariant();

        try
        {
            switch (s)
            {
                case "M":
                    return new Token(SyntaxKind.DecimalLiteral, raw, start,
                        decimal.Parse(digits, CultureInfo.InvariantCulture));
                case "F":
                    return new Token(SyntaxKind.FloatLiteral, raw, start,
                        float.Parse(digits, CultureInfo.InvariantCulture));
                case "D":
                    return new Token(SyntaxKind.DoubleLiteral, raw, start,
                        double.Parse(digits, CultureInfo.InvariantCulture));
                case "U":
                    return MakeUnsigned(digits, raw, start, forceLong: false);
                case "L":
                    return new Token(SyntaxKind.LongLiteral, raw, start,
                        long.Parse(digits, CultureInfo.InvariantCulture));
                case "UL":
                case "LU":
                    return new Token(SyntaxKind.ULongLiteral, raw, start,
                        ulong.Parse(digits, CultureInfo.InvariantCulture));
                case "":
                    if (isReal)
                        return new Token(SyntaxKind.DoubleLiteral, raw, start,
                            double.Parse(digits, CultureInfo.InvariantCulture));
                    return MakeSigned(digits, raw, start);
                default:
                    _diagnostics.Report(ErrorCode.InvalidNumericLiteral, start,
                        $"数字字面量后缀 '{suffix}' 无效。");
                    return new Token(SyntaxKind.IntLiteral, raw, start, 0);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            _diagnostics.Report(ErrorCode.InvalidNumericLiteral, start,
                $"数字字面量 '{raw}' 无法表示。");
            return new Token(SyntaxKind.IntLiteral, raw, start, 0);
        }
    }

    private static Token MakeSigned(string digits, string raw, SourcePosition start)
    {
        if (int.TryParse(digits, CultureInfo.InvariantCulture, out var i))
            return new Token(SyntaxKind.IntLiteral, raw, start, i);
        if (long.TryParse(digits, CultureInfo.InvariantCulture, out var l))
            return new Token(SyntaxKind.LongLiteral, raw, start, l);
        return new Token(SyntaxKind.ULongLiteral, raw, start,
            ulong.Parse(digits, CultureInfo.InvariantCulture));
    }

    private static Token MakeUnsigned(string digits, string raw, SourcePosition start, bool forceLong)
    {
        if (!forceLong && uint.TryParse(digits, CultureInfo.InvariantCulture, out var u))
            return new Token(SyntaxKind.UIntLiteral, raw, start, u);
        return new Token(SyntaxKind.ULongLiteral, raw, start,
            ulong.Parse(digits, CultureInfo.InvariantCulture));
    }

    private Token ReadHex(SourcePosition start)
    {
        var begin = _pos;
        _pos += 2;
        var digitsStart = _pos;
        while (_pos < _text.Length && (Uri.IsHexDigit(Current) || Current == '_')) _pos++;
        var digits = _text[digitsStart.._pos].Replace("_", string.Empty);
        var suffix = ReadNumericSuffix();
        var raw = _text[begin.._pos];

        if (digits.Length == 0)
        {
            _diagnostics.Report(ErrorCode.InvalidNumericLiteral, start, "十六进制字面量缺少数字。");
            return new Token(SyntaxKind.IntLiteral, raw, start, 0);
        }

        return DecodeIntegral(Convert.ToUInt64(digits, 16), suffix, raw, start);
    }

    private Token ReadBinary(SourcePosition start)
    {
        var begin = _pos;
        _pos += 2;
        var digitsStart = _pos;
        while (_pos < _text.Length && (Current is '0' or '1' or '_')) _pos++;
        var digits = _text[digitsStart.._pos].Replace("_", string.Empty);
        var suffix = ReadNumericSuffix();
        var raw = _text[begin.._pos];

        if (digits.Length == 0)
        {
            _diagnostics.Report(ErrorCode.InvalidNumericLiteral, start, "二进制字面量缺少数字。");
            return new Token(SyntaxKind.IntLiteral, raw, start, 0);
        }

        return DecodeIntegral(Convert.ToUInt64(digits, 2), suffix, raw, start);
    }

    private static Token DecodeIntegral(ulong value, string suffix, string raw, SourcePosition start)
    {
        switch (suffix.ToUpperInvariant())
        {
            case "U": return new Token(SyntaxKind.UIntLiteral, raw, start, (uint)value);
            case "L": return new Token(SyntaxKind.LongLiteral, raw, start, (long)value);
            case "UL":
            case "LU": return new Token(SyntaxKind.ULongLiteral, raw, start, value);
            default:
                if (value <= int.MaxValue) return new Token(SyntaxKind.IntLiteral, raw, start, (int)value);
                if (value <= uint.MaxValue) return new Token(SyntaxKind.UIntLiteral, raw, start, (uint)value);
                if (value <= long.MaxValue) return new Token(SyntaxKind.LongLiteral, raw, start, (long)value);
                return new Token(SyntaxKind.ULongLiteral, raw, start, value);
        }
    }

    /// <summary>
    /// <c>@"..."</c>: no escapes, newlines allowed, and <c>""</c> is a single quote.
    /// </summary>
    private Token ReadVerbatimString(SourcePosition start)
    {
        var begin = _pos;
        _pos += 2; // @"

        var sb = new StringBuilder();

        while (true)
        {
            if (_pos >= _text.Length)
            {
                _diagnostics.Report(ErrorCode.UnterminatedString, start, "逐字字符串未闭合。");
                break;
            }

            if (Current == '"')
            {
                if (Peek() != '"') { _pos++; break; }

                sb.Append('"');
                _pos += 2;
                continue;
            }

            if (Current == '\n') { _line++; _lineStart = _pos + 1; }

            sb.Append(Current);
            _pos++;
        }

        return new Token(SyntaxKind.StringLiteral, _text[begin.._pos], start, sb.ToString());
    }

    /// <summary>A run of <c>$</c> followed by at least three quotes.</summary>
    private bool StartsRawInterpolation()
    {
        var at = _pos;
        while (at < _text.Length && _text[at] == '$') at++;

        return at + 2 < _text.Length &&
               _text[at] == '"' && _text[at + 1] == '"' && _text[at + 2] == '"';
    }

    /// <summary>
    /// <c>$$"""...{{hole}}..."""</c>. The number of <c>$</c> is the number of braces a hole
    /// needs, which is what lets the text itself contain single braces untouched.
    /// </summary>
    private Token ReadRawInterpolatedString(SourcePosition start)
    {
        var begin = _pos;

        var dollars = 0;
        while (_pos < _text.Length && Current == '$') { dollars++; _pos++; }

        var raw = ReadRawStringBody(start, out var ok);
        if (!ok) return new Token(SyntaxKind.StringLiteral, _text[begin.._pos], start, string.Empty);

        var normalised = NormaliseRaw(raw, start);
        var parts = SplitRawInterpolation(normalised, start, dollars);

        return new Token(SyntaxKind.InterpolatedStringLiteral, _text[begin.._pos], start, parts);
    }

    /// <summary>
    /// Splits already-normalised raw text on runs of <paramref name="braces"/> braces. Positions
    /// inside are approximate — a raw literal's content has been reindented, so there is no exact
    /// mapping back to the file.
    /// </summary>
    private List<RawInterpolationPart> SplitRawInterpolation(string text, SourcePosition start, int braces)
    {
        var parts = new List<RawInterpolationPart>();
        var literal = new StringBuilder();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] != '{' || !HasRun(text, i, '{', braces))
            {
                literal.Append(text[i]);
                i++;
                continue;
            }

            if (literal.Length > 0)
            {
                parts.Add(new RawInterpolationPart(IsHole: false, literal.ToString(), start));
                literal.Clear();
            }

            i += braces;
            var holeStart = i;

            while (i < text.Length && !HasRun(text, i, '}', braces)) i++;

            if (i >= text.Length)
            {
                _diagnostics.Report(ErrorCode.UnterminatedString, start, "插值项缺少结束的 '}'。");
                break;
            }

            parts.Add(new RawInterpolationPart(IsHole: true, text[holeStart..i], start));
            i += braces;
        }

        if (literal.Length > 0)
            parts.Add(new RawInterpolationPart(IsHole: false, literal.ToString(), start));

        return parts;
    }

    private static bool HasRun(string text, int at, char c, int count)
    {
        if (at + count > text.Length) return false;

        for (var i = 0; i < count; i++)
            if (text[at + i] != c)
                return false;

        return true;
    }

    /// <summary>
    /// A raw string literal. The fence may be longer than three quotes; a multi-line one drops
    /// the first and last newline and strips the indentation the closing fence sits at, which is
    /// what lets an indented literal read as unindented text.
    /// </summary>
    private Token ReadRawString(SourcePosition start)
    {
        var begin = _pos;
        var raw = ReadRawStringBody(start, out var ok);

        return ok
            ? new Token(SyntaxKind.StringLiteral, _text[begin.._pos], start, NormaliseRaw(raw, start))
            : new Token(SyntaxKind.StringLiteral, _text[begin.._pos], start, string.Empty);
    }

    /// <summary>Consumes the fence, the content and the closing fence; returns the content.</summary>
    private string ReadRawStringBody(SourcePosition start, out bool ok)
    {
        var fence = 0;
        while (_pos < _text.Length && Current == '"') { fence++; _pos++; }

        var contentStart = _pos;
        var contentEnd = -1;

        while (_pos < _text.Length)
        {
            if (Current != '"')
            {
                if (Current == '\n') { _line++; _lineStart = _pos + 1; }
                _pos++;
                continue;
            }

            var at = _pos;
            var run = 0;
            while (at < _text.Length && _text[at] == '"') { run++; at++; }

            if (run >= fence)
            {
                // A longer run closes at its last `fence` quotes; the extra ones are content.
                contentEnd = at - fence;
                _pos = at;
                break;
            }

            _pos = at;
        }

        if (contentEnd < 0)
        {
            _diagnostics.Report(ErrorCode.UnterminatedString, start, "原始字符串未闭合。");
            _pos = _text.Length;
            ok = false;
            return string.Empty;
        }

        ok = true;
        return _text[contentStart..contentEnd];
    }

    /// <summary>Applies the multi-line rules of a raw string literal to the text it captured.</summary>
    private string NormaliseRaw(string raw, SourcePosition start)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');

        // A single-line raw string is taken exactly as written.
        if (lines.Length == 1) return raw;

        if (lines[0].Trim().Length != 0)
        {
            _diagnostics.Report(ErrorCode.UnterminatedString, start,
                "多行原始字符串的内容必须从开始分隔符的下一行开始。");
            return raw;
        }

        var closing = lines[^1];
        if (closing.Trim().Length != 0)
        {
            _diagnostics.Report(ErrorCode.UnterminatedString, start,
                "多行原始字符串的结束分隔符必须独占一行。");
            return raw;
        }

        var indent = closing.Length;
        var body = lines[1..^1];

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i].Trim().Length == 0) { body[i] = string.Empty; continue; }

            if (body[i].Length < indent || body[i][..indent].Trim().Length != 0)
            {
                _diagnostics.Report(ErrorCode.UnterminatedString, start,
                    "多行原始字符串的每一行缩进都不能少于结束分隔符。");
                return raw;
            }

            body[i] = body[i][indent..];
        }

        return string.Join('\n', body);
    }

    private Token ReadString(SourcePosition start)
    {
        var begin = _pos;
        _pos++; // opening quote
        var sb = new StringBuilder();

        while (true)
        {
            if (_pos >= _text.Length || Current == '\n')
            {
                _diagnostics.Report(ErrorCode.UnterminatedString, start, "字符串字面量未闭合。");
                return new Token(SyntaxKind.StringLiteral, _text[begin..Math.Min(_pos, _text.Length)], start, sb.ToString());
            }

            if (Current == '"') { _pos++; break; }

            if (Current == '\\')
            {
                _pos++;
                if (!TryReadEscape(out var decoded))
                    continue;
                sb.Append(decoded);
            }
            else
            {
                sb.Append(Current);
                _pos++;
            }
        }

        return new Token(SyntaxKind.StringLiteral, _text[begin.._pos], start, sb.ToString());
    }

    /// <summary>
    /// Scans <c>$"..."</c> into literal runs and holes. The hole's source is captured verbatim
    /// and parsed later; brace, bracket, paren and string nesting are tracked so that a hole
    /// containing its own braces or strings closes at the right place.
    /// </summary>
    /// <summary>
    /// Scans <c>$"..."</c>. In <paramref name="verbatim"/> mode (<c>$@"..."</c>) there are no
    /// backslash escapes, newlines are allowed, and <c>""</c> stands for one quote.
    /// </summary>
    private Token ReadInterpolatedString(SourcePosition start, bool verbatim = false)
    {
        var begin = _pos;

        // Skip the $ / @ prefix, however it was spelled, and the opening quote.
        while (Current != '"') _pos++;
        _pos++;

        var parts = new List<RawInterpolationPart>();
        var text = new StringBuilder();

        void FlushText(SourcePosition position)
        {
            if (text.Length == 0) return;
            parts.Add(new RawInterpolationPart(IsHole: false, text.ToString(), position));
            text.Clear();
        }

        var runStart = Here;

        while (true)
        {
            if (_pos >= _text.Length || (!verbatim && Current == '\n'))
            {
                _diagnostics.Report(ErrorCode.UnterminatedString, start, "插值字符串未闭合。");
                break;
            }

            if (Current == '"')
            {
                if (!verbatim || Peek() != '"') { _pos++; break; }

                text.Append('"');
                _pos += 2;
                continue;
            }

            if (verbatim && Current == '\n') { _line++; _lineStart = _pos + 1; }

            if (Current == '{' && Peek() == '{') { text.Append('{'); _pos += 2; continue; }
            if (Current == '}' && Peek() == '}') { text.Append('}'); _pos += 2; continue; }

            if (Current == '}')
            {
                _diagnostics.Report(ErrorCode.UnexpectedCharacter, Here,
                    "插值字符串中的 '}' 需要写成 '}}'。");
                _pos++;
                continue;
            }

            if (Current == '{')
            {
                FlushText(runStart);
                ReadInterpolationHole(parts);
                runStart = Here;
                continue;
            }

            if (!verbatim && Current == '\\')
            {
                _pos++;
                if (TryReadEscape(out var decoded)) text.Append(decoded);
                continue;
            }

            text.Append(Current);
            _pos++;
        }

        FlushText(runStart);
        return new Token(SyntaxKind.InterpolatedStringLiteral, _text[begin.._pos], start, parts);
    }

    private void ReadInterpolationHole(List<RawInterpolationPart> parts)
    {
        _pos++; // '{'
        var holeStart = Here;
        var begin = _pos;

        var depth = 0;
        var alignmentAt = -1;
        var formatAt = -1;

        while (_pos < _text.Length)
        {
            var c = Current;

            if (c == '"' || c == '\'')
            {
                SkipNestedLiteral(c);
                continue;
            }

            if (c is '(' or '[' or '{') { depth++; _pos++; continue; }

            if (c == ')' || c == ']') { depth--; _pos++; continue; }

            if (c == '}')
            {
                if (depth == 0) break;
                depth--;
                _pos++;
                continue;
            }

            // At the top level of a hole, ',' starts the alignment and ':' the format specifier.
            // A conditional expression therefore has to be parenthesised, exactly as in C#.
            if (depth == 0 && c == ',' && alignmentAt < 0 && formatAt < 0) alignmentAt = _pos;
            if (depth == 0 && c == ':' && formatAt < 0) formatAt = _pos;

            _pos++;
        }

        var end = _pos;
        if (_pos < _text.Length && Current == '}') _pos++;
        else _diagnostics.Report(ErrorCode.UnterminatedString, holeStart, "插值项缺少 '}'。");

        var expressionEnd = alignmentAt >= 0 ? alignmentAt : formatAt >= 0 ? formatAt : end;

        var expression = _text[begin..expressionEnd];
        var alignment = alignmentAt >= 0
            ? _text[(alignmentAt + 1)..(formatAt >= 0 ? formatAt : end)]
            : null;
        var format = formatAt >= 0 ? _text[(formatAt + 1)..end] : null;

        parts.Add(new RawInterpolationPart(IsHole: true, expression, holeStart, alignment, format));
    }

    /// <summary>Skips a string or char literal inside an interpolation hole.</summary>
    private void SkipNestedLiteral(char quote)
    {
        _pos++;
        while (_pos < _text.Length && Current != quote)
        {
            if (Current == '\\') _pos++;
            _pos++;
        }
        if (_pos < _text.Length) _pos++;
    }

    private Token ReadChar(SourcePosition start)
    {
        var begin = _pos;
        _pos++; // opening quote
        char value;

        if (_pos >= _text.Length)
        {
            _diagnostics.Report(ErrorCode.UnterminatedString, start, "字符字面量未闭合。");
            return new Token(SyntaxKind.CharLiteral, "'", start, '\0');
        }

        if (Current == '\\')
        {
            _pos++;
            value = TryReadEscape(out var decoded) ? decoded : '\0';
        }
        else
        {
            value = Current;
            _pos++;
        }

        if (Current == '\'') _pos++;
        else _diagnostics.Report(ErrorCode.UnterminatedString, start, "字符字面量未闭合。");

        return new Token(SyntaxKind.CharLiteral, _text[begin.._pos], start, value);
    }

    private bool TryReadEscape(out char value)
    {
        var escapeStart = Here;
        var c = Current;
        _pos++;

        switch (c)
        {
            case 'n': value = '\n'; return true;
            case 't': value = '\t'; return true;
            case 'r': value = '\r'; return true;
            case '0': value = '\0'; return true;
            case 'a': value = '\a'; return true;
            case 'b': value = '\b'; return true;
            case 'f': value = '\f'; return true;
            case 'v': value = '\v'; return true;
            case '\\': value = '\\'; return true;
            case '\'': value = '\''; return true;
            case '"': value = '"'; return true;
            case 'u':
            {
                var digits = 0;
                var code = 0;
                while (digits < 4 && Uri.IsHexDigit(Current))
                {
                    code = code * 16 + Convert.ToInt32(Current.ToString(), 16);
                    _pos++;
                    digits++;
                }
                if (digits != 4)
                {
                    _diagnostics.Report(ErrorCode.InvalidEscapeSequence, escapeStart,
                        "'\\u' 转义需要 4 位十六进制数字。");
                    value = '\0';
                    return false;
                }
                value = (char)code;
                return true;
            }
            default:
                _diagnostics.Report(ErrorCode.InvalidEscapeSequence, escapeStart,
                    $"无法识别的转义序列 '\\{c}'。");
                value = c;
                return true;
        }
    }

    private Token ReadPunctuation(SourcePosition start)
    {
        var c = Current;
        var n = Peek();
        var n2 = Peek(2);

        SyntaxKind kind;
        int length;

        switch (c)
        {
            case '(': kind = SyntaxKind.OpenParen; length = 1; break;
            case ')': kind = SyntaxKind.CloseParen; length = 1; break;
            case '{': kind = SyntaxKind.OpenBrace; length = 1; break;
            case '}': kind = SyntaxKind.CloseBrace; length = 1; break;
            case '[': kind = SyntaxKind.OpenBracket; length = 1; break;
            case ']': kind = SyntaxKind.CloseBracket; length = 1; break;
            case ',': kind = SyntaxKind.Comma; length = 1; break;
            case ';': kind = SyntaxKind.Semicolon; length = 1; break;
            case ':': kind = SyntaxKind.Colon; length = 1; break;
            case '~': kind = SyntaxKind.Tilde; length = 1; break;
            case '.':
                if (Peek() == '.') { kind = SyntaxKind.DotDot; length = 2; }
                else { kind = SyntaxKind.Dot; length = 1; }
                break;

            case '?':
                if (n == '?' && n2 == '=') { kind = SyntaxKind.QuestionQuestionEquals; length = 3; }
                else if (n == '?') { kind = SyntaxKind.QuestionQuestion; length = 2; }
                else if (n == '.') { kind = SyntaxKind.QuestionDot; length = 2; }
                else if (n == '[') { kind = SyntaxKind.QuestionDot; length = 1; } // ?[  -> handled by parser
                else { kind = SyntaxKind.Question; length = 1; }
                break;

            case '+':
                if (n == '+') { kind = SyntaxKind.PlusPlus; length = 2; }
                else if (n == '=') { kind = SyntaxKind.PlusEquals; length = 2; }
                else { kind = SyntaxKind.Plus; length = 1; }
                break;

            case '-':
                if (n == '-') { kind = SyntaxKind.MinusMinus; length = 2; }
                else if (n == '=') { kind = SyntaxKind.MinusEquals; length = 2; }
                else { kind = SyntaxKind.Minus; length = 1; }
                break;

            case '*': (kind, length) = n == '=' ? (SyntaxKind.StarEquals, 2) : (SyntaxKind.Star, 1); break;
            case '/': (kind, length) = n == '=' ? (SyntaxKind.SlashEquals, 2) : (SyntaxKind.Slash, 1); break;
            case '%': (kind, length) = n == '=' ? (SyntaxKind.PercentEquals, 2) : (SyntaxKind.Percent, 1); break;
            case '^': (kind, length) = n == '=' ? (SyntaxKind.CaretEquals, 2) : (SyntaxKind.Caret, 1); break;
            case '!': (kind, length) = n == '=' ? (SyntaxKind.BangEquals, 2) : (SyntaxKind.Bang, 1); break;

            case '&':
                if (n == '&') { kind = SyntaxKind.AmpAmp; length = 2; }
                else if (n == '=') { kind = SyntaxKind.AmpEquals; length = 2; }
                else { kind = SyntaxKind.Amp; length = 1; }
                break;

            case '|':
                if (n == '|') { kind = SyntaxKind.PipePipe; length = 2; }
                else if (n == '=') { kind = SyntaxKind.PipeEquals; length = 2; }
                else { kind = SyntaxKind.Pipe; length = 1; }
                break;

            case '=':
                if (n == '=') { kind = SyntaxKind.EqualsEquals; length = 2; }
                else if (n == '>') { kind = SyntaxKind.Arrow; length = 2; }
                else { kind = SyntaxKind.Equals; length = 1; }
                break;

            case '<':
                if (n == '<' && n2 == '=') { kind = SyntaxKind.LessLessEquals; length = 3; }
                else if (n == '<') { kind = SyntaxKind.LessLess; length = 2; }
                else if (n == '=') { kind = SyntaxKind.LessEquals; length = 2; }
                else { kind = SyntaxKind.Less; length = 1; }
                break;

            case '>':
                // Plain '>>' is produced by the parser from two '>' tokens so that generic type
                // arguments such as List<List<int>> still close correctly. '>>=' is safe to lex
                // as one token because no generic argument list can be followed directly by '='.
                if (n == '>' && n2 == '=') { kind = SyntaxKind.GreaterGreaterEquals; length = 3; }
                else if (n == '=') { kind = SyntaxKind.GreaterEquals; length = 2; }
                else { kind = SyntaxKind.Greater; length = 1; }
                break;

            default:
                _diagnostics.Report(ErrorCode.UnexpectedCharacter, start,
                    $"无法识别的字符 '{c}'。");
                _pos++;
                return new Token(SyntaxKind.BadToken, c.ToString(), start);
        }

        var text = _text.Substring(_pos, length);
        _pos += length;
        return new Token(kind, text, start);
    }
}
