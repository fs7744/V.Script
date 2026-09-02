namespace V.Script.Binding;

/// <summary>
/// Binary numeric promotion per ECMA-334 §12.4.7. Both operands of an arithmetic,
/// relational or integer-logical operator are promoted to a single common type before the
/// operation is bound; the result is what the emitter actually converts to.
/// </summary>
public static class NumericPromotion
{
    /// <summary>
    /// Computes the common type for <paramref name="left"/> and <paramref name="right"/>.
    /// Returns null when no promotion exists (for example <c>decimal</c> with <c>double</c>).
    /// </summary>
    public static Type? Promote(Type left, Type right)
    {
        if (!Conversions.IsNumeric(left) || !Conversions.IsNumeric(right)) return null;

        if (left == typeof(decimal) || right == typeof(decimal))
        {
            if (IsBinaryFloat(left) || IsBinaryFloat(right)) return null;
            return typeof(decimal);
        }

        if (left == typeof(double) || right == typeof(double)) return typeof(double);
        if (left == typeof(float) || right == typeof(float)) return typeof(float);

        if (left == typeof(ulong) || right == typeof(ulong))
        {
            var other = left == typeof(ulong) ? right : left;
            if (IsSigned(other)) return null; // sbyte/short/int/long with ulong has no promotion
            return typeof(ulong);
        }

        if (left == typeof(long) || right == typeof(long)) return typeof(long);

        // nint / nuint promote against the smaller integers, and mixing the two has no result —
        // the same shape as ulong against a signed type.
        if (left == typeof(nuint) || right == typeof(nuint))
        {
            var other = left == typeof(nuint) ? right : left;
            if (other == typeof(nuint)) return typeof(nuint);
            return IsSigned(other) || other == typeof(nint) ? null : typeof(nuint);
        }

        if (left == typeof(nint) || right == typeof(nint))
        {
            var other = left == typeof(nint) ? right : left;
            return other == typeof(uint) ? null : typeof(nint);
        }

        if (left == typeof(uint) || right == typeof(uint))
        {
            var other = left == typeof(uint) ? right : left;
            if (other == typeof(sbyte) || other == typeof(short) || other == typeof(int)) return typeof(long);
            return typeof(uint);
        }

        return typeof(int);
    }

    private static bool IsBinaryFloat(Type type) => type == typeof(float) || type == typeof(double);

    private static bool IsSigned(Type type) =>
        type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long);

    /// <summary>
    /// Unary numeric promotion: <c>+x</c>, <c>-x</c>, <c>~x</c> widen small integral types to
    /// <see cref="int"/>. Returns null when the operand is not numeric.
    /// </summary>
    public static Type? PromoteUnary(Type operand)
    {
        if (!Conversions.IsNumeric(operand)) return null;

        if (operand == typeof(nint) || operand == typeof(nuint)) return operand;

        if (operand == typeof(sbyte) || operand == typeof(byte) ||
            operand == typeof(short) || operand == typeof(ushort) ||
            operand == typeof(char))
            return typeof(int);

        return operand;
    }
}
