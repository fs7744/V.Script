namespace V.Script.Tests;

/// <summary>
/// Differential testing: the same expression is evaluated by the real C# compiler and by the
/// engine, then the two results are compared. Hand-written expectations cannot be trusted for
/// numeric promotion and lifted operators — this is what actually pins the semantics.
/// </summary>
public sealed class DifferentialTests : ScriptTest
{
    private static void Same<T>(string source, Func<NumberGlobals, T> oracle, NumberGlobals globals)
    {
        var expected = oracle(globals);
        var actual = Run<NumberGlobals, T>(source, globals);
        Assert.Equal(expected, actual);
    }

    private static IEnumerable<NumberGlobals> IntegerCorpus()
    {
        int[] values = [0, 1, -1, 2, 7, -7, 255, 256, -128, 127, int.MaxValue, int.MinValue];

        foreach (var a in values)
            foreach (var b in values)
                yield return new NumberGlobals { A = a, B = b };
    }

    [Fact]
    public void Integer_arithmetic_matches_the_compiler_across_a_corpus()
    {
        foreach (var globals in IntegerCorpus())
        {
            Same("A + B", g => unchecked(g.A + g.B), globals);
            Same("A - B", g => unchecked(g.A - g.B), globals);
            Same("A * B", g => unchecked(g.A * g.B), globals);
            Same("A & B", g => g.A & g.B, globals);
            Same("A | B", g => g.A | g.B, globals);
            Same("A ^ B", g => g.A ^ g.B, globals);
        }
    }

    [Fact]
    public void Integer_division_matches_the_compiler_where_defined()
    {
        foreach (var globals in IntegerCorpus())
        {
            if (globals.B == 0) continue;
            if (globals is { A: int.MinValue, B: -1 }) continue; // overflows in both

            Same("A / B", g => g.A / g.B, globals);
            Same("A % B", g => g.A % g.B, globals);
        }
    }

    [Fact]
    public void Integer_comparisons_match_the_compiler()
    {
        foreach (var globals in IntegerCorpus())
        {
            Same("A < B", g => g.A < g.B, globals);
            Same("A <= B", g => g.A <= g.B, globals);
            Same("A > B", g => g.A > g.B, globals);
            Same("A >= B", g => g.A >= g.B, globals);
            Same("A == B", g => g.A == g.B, globals);
            Same("A != B", g => g.A != g.B, globals);
        }
    }

    [Fact]
    public void Shifts_match_the_compiler_including_the_count_mask()
    {
        foreach (var shift in Enumerable.Range(0, 40))
        {
            var globals = new NumberGlobals { A = -12345, B = shift };
            Same("A << B", g => g.A << g.B, globals);
            Same("A >> B", g => g.A >> g.B, globals);
        }
    }

    [Fact]
    public void Mixed_width_promotion_matches_the_compiler()
    {
        long[] longs = [0, 1, -1, int.MaxValue, long.MaxValue / 2];
        int[] ints = [0, 1, -1, 7, int.MaxValue];

        foreach (var l in longs)
            foreach (var i in ints)
            {
                var globals = new NumberGlobals { BigA = l, A = i };
                Same("BigA + A", g => unchecked(g.BigA + g.A), globals);
                Same("A + BigA", g => unchecked(g.A + g.BigA), globals);
                Same("BigA < A", g => g.BigA < g.A, globals);
            }
    }

    [Fact]
    public void Unsigned_arithmetic_matches_the_compiler()
    {
        uint[] values = [0, 1, 7, 255, uint.MaxValue, uint.MaxValue / 2];

        foreach (var u in values)
        {
            var globals = new NumberGlobals { U = u, A = 3 };
            Same("U / 2", g => g.U / 2, globals);
            Same("U >> 1", g => g.U >> 1, globals);
            Same("U > 100", g => g.U > 100, globals);
            Same("U + A", g => g.U + g.A, globals);
        }
    }

    [Fact]
    public void Byte_promotion_matches_the_compiler()
    {
        foreach (var value in new byte[] { 0, 1, 127, 200, 255 })
        {
            var globals = new NumberGlobals { Small = value };
            Same("Small + Small", g => g.Small + g.Small, globals);
            Same("Small * 2", g => g.Small * 2, globals);
            Same("-Small", g => -g.Small, globals);
            Same("~Small", g => ~g.Small, globals);
        }
    }

    [Fact]
    public void Floating_point_matches_the_compiler_including_nan_and_infinity()
    {
        double[] values = [0.0, 1.0, -1.0, 0.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

        foreach (var d in values)
        {
            var globals = new NumberGlobals { D = d, A = 2 };
            Same("D + A", g => g.D + g.A, globals);
            Same("D * A", g => g.D * g.A, globals);
            Same("D / A", g => g.D / g.A, globals);
            Same("D < 1.0", g => g.D < 1.0, globals);
            Same("D <= 1.0", g => g.D <= 1.0, globals);
            Same("D > 1.0", g => g.D > 1.0, globals);
            Same("D >= 1.0", g => g.D >= 1.0, globals);
            Same("D == D", g => g.D == g.D, globals);
        }
    }

    [Fact]
    public void Decimal_arithmetic_matches_the_compiler()
    {
        decimal[] values = [0m, 1m, -1m, 0.1m, 2.5m, 1234.5678m];

        foreach (var m in values)
        {
            var globals = new NumberGlobals { M = m, A = 3 };
            Same("M + A", g => g.M + g.A, globals);
            Same("M * A", g => g.M * g.A, globals);
            Same("M - 1m", g => g.M - 1m, globals);
            Same("-M", g => -g.M, globals);
            Same("M > 1m", g => g.M > 1m, globals);
            Same("M == 1m", g => g.M == 1m, globals);
        }
    }

    [Fact]
    public void Lifted_arithmetic_matches_the_compiler()
    {
        int?[] values = [null, 0, 1, -1, 7, int.MaxValue];

        foreach (var a in values)
            foreach (var b in values)
            {
                var globals = new NumberGlobals { MaybeA = a, MaybeB = b };
                Same("MaybeA + MaybeB", g => unchecked(g.MaybeA + g.MaybeB), globals);
                Same("MaybeA - MaybeB", g => unchecked(g.MaybeA - g.MaybeB), globals);
                Same("MaybeA * MaybeB", g => unchecked(g.MaybeA * g.MaybeB), globals);
                Same("-MaybeA", g => -g.MaybeA, globals);
            }
    }

    [Fact]
    public void Lifted_comparisons_match_the_compiler()
    {
        int?[] values = [null, 0, 1, -1, 7];

        foreach (var a in values)
            foreach (var b in values)
            {
                var globals = new NumberGlobals { MaybeA = a, MaybeB = b };
                Same("MaybeA < MaybeB", g => g.MaybeA < g.MaybeB, globals);
                Same("MaybeA <= MaybeB", g => g.MaybeA <= g.MaybeB, globals);
                Same("MaybeA > MaybeB", g => g.MaybeA > g.MaybeB, globals);
                Same("MaybeA >= MaybeB", g => g.MaybeA >= g.MaybeB, globals);
                Same("MaybeA == MaybeB", g => g.MaybeA == g.MaybeB, globals);
                Same("MaybeA != MaybeB", g => g.MaybeA != g.MaybeB, globals);
                Same("MaybeA ?? -99", g => g.MaybeA ?? -99, globals);
            }
    }

    [Fact]
    public void Lifted_mixed_operands_match_the_compiler()
    {
        int?[] values = [null, 0, 5];

        foreach (var a in values)
        {
            var globals = new NumberGlobals { MaybeA = a, A = 3 };
            Same("MaybeA + A", g => g.MaybeA + g.A, globals);
            Same("A + MaybeA", g => g.A + g.MaybeA, globals);
            Same("MaybeA > A", g => g.MaybeA > g.A, globals);
            Same("MaybeA == A", g => g.MaybeA == g.A, globals);
        }
    }

    [Fact]
    public void Nullable_conversions_match_the_compiler()
    {
        int?[] values = [null, 0, 42, -7];

        foreach (var a in values)
        {
            var globals = new NumberGlobals { MaybeA = a };
            Same("MaybeA", g => (long?)g.MaybeA, globals);
            Same("MaybeA", g => (double?)g.MaybeA, globals);
            Same("MaybeA", g => (decimal?)g.MaybeA, globals);
        }
    }

    [Fact]
    public void Explicit_narrowing_conversions_match_the_compiler()
    {
        int[] values = [0, 1, -1, 127, 128, 255, 256, 65535, 65536, int.MaxValue, int.MinValue];

        foreach (var value in values)
        {
            var globals = new NumberGlobals { A = value };
            Same("(byte)A", g => unchecked((byte)g.A), globals);
            Same("(sbyte)A", g => unchecked((sbyte)g.A), globals);
            Same("(short)A", g => unchecked((short)g.A), globals);
            Same("(ushort)A", g => unchecked((ushort)g.A), globals);
            Same("(uint)A", g => unchecked((uint)g.A), globals);
            Same("(long)A", g => (long)g.A, globals);
            Same("(double)A", g => (double)g.A, globals);
            Same("(char)A", g => unchecked((char)g.A), globals);
        }
    }

    [Fact]
    public void Double_to_integer_conversions_match_the_compiler()
    {
        double[] values = [0.0, 1.9, -1.9, 127.5, 1e10];

        foreach (var d in values)
        {
            var globals = new NumberGlobals { D = d };
            Same("(int)D", g => unchecked((int)g.D), globals);
            Same("(long)D", g => unchecked((long)g.D), globals);
            Same("(float)D", g => (float)g.D, globals);
        }
    }

    [Fact]
    public void Boolean_expressions_match_the_compiler()
    {
        foreach (var flag in new[] { true, false })
            foreach (var a in new[] { 0, 1, -1 })
            {
                var globals = new NumberGlobals { Flag = flag, A = a };
                Same("Flag && A > 0", g => g.Flag && g.A > 0, globals);
                Same("Flag || A > 0", g => g.Flag || g.A > 0, globals);
                Same("!Flag", g => !g.Flag, globals);
                Same("Flag ^ (A > 0)", g => g.Flag ^ (g.A > 0), globals);
                Same("Flag ? A : -A", g => g.Flag ? g.A : -g.A, globals);
            }
    }

    [Fact]
    public void String_operations_match_the_compiler()
    {
        string?[] values = [null, "", "a", "abc"];

        foreach (var text in values)
        {
            var globals = new NumberGlobals { Text = text, A = 5 };
            Same("Text + \"!\"", g => g.Text + "!", globals);
            Same("Text + A", g => g.Text + g.A, globals);
            Same("Text == null", g => g.Text == null, globals);
            Same("Text ?? \"fallback\"", g => g.Text ?? "fallback", globals);
        }
    }

    [Fact]
    public void Char_arithmetic_matches_the_compiler()
    {
        foreach (var c in new[] { '\0', 'a', 'Z', '9', char.MaxValue })
        {
            var globals = new NumberGlobals { Ch = c, A = 1 };
            Same("Ch + A", g => g.Ch + g.A, globals);
            Same("Ch > 'a'", g => g.Ch > 'a', globals);
            Same("(int)Ch", g => (int)g.Ch, globals);
        }
    }

    [Fact]
    public void Compound_assignment_matches_the_compiler()
    {
        foreach (var start in new[] { 0, 1, -5, 100 })
        {
            var globals = new NumberGlobals { A = start, B = 3 };

            Same("var x = A; x += B; return x;", g => { var x = g.A; x += g.B; return x; }, globals);
            Same("var x = A; x -= B; return x;", g => { var x = g.A; x -= g.B; return x; }, globals);
            Same("var x = A; x *= B; return x;", g => { var x = g.A; x *= g.B; return x; }, globals);
            Same("var x = A; x /= B; return x;", g => { var x = g.A; x /= g.B; return x; }, globals);
            Same("var x = A; x %= B; return x;", g => { var x = g.A; x %= g.B; return x; }, globals);
            Same("var x = A; x <<= 2; return x;", g => { var x = g.A; x <<= 2; return x; }, globals);
        }
    }

    [Fact]
    public void Narrowing_compound_assignment_matches_the_compiler()
    {
        // 'b += 1' on a byte carries an implicit cast back to byte, so it wraps rather than failing.
        foreach (var start in new byte[] { 0, 128, 255 })
        {
            var globals = new NumberGlobals { Small = start };
            Same("byte b = Small; b += 1; return b;",
                g => { var b = g.Small; b += 1; return b; }, globals);
        }
    }

    [Fact]
    public void Increment_semantics_match_the_compiler()
    {
        foreach (var start in new[] { 0, 5, -1 })
        {
            var globals = new NumberGlobals { A = start };
            Same("var i = A; var j = i++; return j;", g => { var i = g.A; var j = i++; return j; }, globals);
            Same("var i = A; var j = i++; return i;", g => { var i = g.A; var j = i++; return i; }, globals);
            Same("var i = A; var j = ++i; return j;", g => { var i = g.A; var j = ++i; return j; }, globals);
            Same("var i = A; var j = --i; return j;", g => { var i = g.A; var j = --i; return j; }, globals);
            Same("var i = A; var j = i--; return j;", g => { var i = g.A; var j = i--; return j; }, globals);
        }
    }
}
