using V.Script.Diagnostics;

namespace V.Script.Syntax;

/// <summary>
/// Applies <c>#if</c> / <c>#elif</c> / <c>#else</c> / <c>#endif</c> before lexing, by blanking
/// the lines that are switched off.
/// </summary>
/// <remarks>
/// Excluded text is replaced with empty lines rather than removed, so every position the lexer
/// and the diagnostics report still lines up with the original source. That is the whole reason
/// this is a text pass instead of a token filter.
/// </remarks>
internal static class Preprocessor
{
    public static string Apply(string source, IReadOnlyCollection<string> symbols, DiagnosticBag diagnostics)
    {
        if (!source.Contains('#')) return source;

        var lines = source.Split('\n');
        var states = new Stack<BranchState>();
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith('#'))
            {
                if (states.Count > 0 && !states.Peek().Taking)
                {
                    lines[i] = Blank(lines[i]);
                    changed = true;
                }

                continue;
            }

            var position = new SourcePosition(i + 1, lines[i].Length - trimmed.Length + 1);
            var (directive, rest) = Split(trimmed[1..]);

            switch (directive)
            {
                case "if":
                    states.Push(new BranchState(Evaluate(rest, symbols, position, diagnostics)));
                    break;

                case "elif":
                    if (!Pop(states, position, diagnostics, out var elif)) break;

                    // Once a branch has been taken, no later one can be.
                    states.Push(elif.AnyTaken
                        ? elif.WithTaking(false)
                        : elif.WithTaking(Evaluate(rest, symbols, position, diagnostics)));
                    break;

                case "else":
                    if (!Pop(states, position, diagnostics, out var branch)) break;
                    states.Push(branch.WithTaking(!branch.AnyTaken));
                    break;

                case "endif":
                    if (states.Count == 0)
                    {
                        diagnostics.Report(ErrorCode.UnexpectedToken, position, "多余的 #endif。");
                        break;
                    }

                    states.Pop();
                    break;

                default:
                    diagnostics.Report(ErrorCode.UnexpectedToken, position,
                        $"不支持的预处理指令 '#{directive}'。仅支持 #if / #elif / #else / #endif。");
                    break;
            }

            // The directive line itself is never part of the program.
            lines[i] = Blank(lines[i]);
            changed = true;
        }

        if (states.Count > 0)
        {
            diagnostics.Report(ErrorCode.UnexpectedToken, new SourcePosition(lines.Length, 1),
                "缺少 #endif。");
        }

        return changed ? string.Join('\n', lines) : source;
    }

    private static bool Pop(
        Stack<BranchState> states,
        SourcePosition position,
        DiagnosticBag diagnostics,
        out BranchState state)
    {
        if (states.Count > 0)
        {
            state = states.Pop();
            return true;
        }

        diagnostics.Report(ErrorCode.UnexpectedToken, position, "#elif / #else 之前没有 #if。");
        state = default;
        return false;
    }

    /// <summary>A carriage return has to survive, or CRLF sources gain a stray character.</summary>
    private static string Blank(string line) => line.EndsWith('\r') ? "\r" : string.Empty;

    private static (string Directive, string Argument) Split(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end])) end++;

        return (text[..end], text[end..]);
    }

    private readonly record struct BranchState(bool Taking, bool AnyTaken)
    {
        public BranchState(bool taking) : this(taking, taking) { }

        public BranchState WithTaking(bool taking) => new(taking, AnyTaken || taking);
    }

    // ============================================================ the condition grammar

    /// <summary>
    /// Evaluates a directive condition: symbols, <c>true</c>/<c>false</c>, <c>!</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>==</c>, <c>!=</c> and parentheses.
    /// </summary>
    private static bool Evaluate(
        string text,
        IReadOnlyCollection<string> symbols,
        SourcePosition position,
        DiagnosticBag diagnostics)
    {
        var reader = new ConditionReader(text, symbols);
        var value = reader.ParseOr();

        if (!reader.AtEnd)
        {
            diagnostics.Report(ErrorCode.UnexpectedToken, position,
                $"预处理条件中有多余内容：'{reader.Remaining}'。");
        }

        return value;
    }

    private ref struct ConditionReader(string text, IReadOnlyCollection<string> symbols)
    {
        private readonly string _text = text;
        private readonly IReadOnlyCollection<string> _symbols = symbols;
        private int _pos;

        public bool AtEnd
        {
            get
            {
                SkipSpace();
                return _pos >= _text.Length;
            }
        }

        public string Remaining => _text[Math.Min(_pos, _text.Length)..];

        public bool ParseOr()
        {
            var left = ParseAnd();
            while (Match("||")) left = ParseAnd() || left;
            return left;
        }

        private bool ParseAnd()
        {
            var left = ParseEquality();
            while (Match("&&")) left = ParseEquality() && left;
            return left;
        }

        private bool ParseEquality()
        {
            var left = ParseUnary();

            while (true)
            {
                if (Match("==")) left = left == ParseUnary();
                else if (Match("!=")) left = left != ParseUnary();
                else return left;
            }
        }

        private bool ParseUnary()
        {
            if (Match("!")) return !ParseUnary();

            SkipSpace();

            if (Match("("))
            {
                var inner = ParseOr();
                Match(")");
                return inner;
            }

            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;

            var name = _text[start.._pos];

            return name switch
            {
                "true" => true,
                "false" => false,
                "" => false,
                _ => _symbols.Contains(name),
            };
        }

        private bool Match(string token)
        {
            SkipSpace();

            if (!_text.AsSpan(_pos).StartsWith(token)) return false;

            // `!` must not swallow the `!` of `!=`.
            if (token == "!" && _text.AsSpan(_pos).StartsWith("!=")) return false;

            _pos += token.Length;
            return true;
        }

        private void SkipSpace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }
    }
}
