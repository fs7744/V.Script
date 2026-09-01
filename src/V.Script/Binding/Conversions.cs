using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace V.Script.Binding;

public enum ConversionKind
{
    None = 0,
    Identity,
    ImplicitNumeric,
    ImplicitNullable,
    ImplicitNullLiteral,
    ImplicitReference,
    Boxing,
    ImplicitEnumeration,
    ImplicitUserDefined,
    ExplicitNumeric,
    ExplicitNullable,
    ExplicitReference,
    Unboxing,
    ExplicitEnumeration,
    ExplicitUserDefined,
}

public readonly record struct Conversion(ConversionKind Kind, MethodInfo? Method = null)
{
    public static readonly Conversion None = new(ConversionKind.None);
    public static readonly Conversion Identity = new(ConversionKind.Identity);

    public bool Exists => Kind != ConversionKind.None;

    public bool IsImplicit => Kind is
        ConversionKind.Identity or ConversionKind.ImplicitNumeric or ConversionKind.ImplicitNullable or
        ConversionKind.ImplicitNullLiteral or ConversionKind.ImplicitReference or ConversionKind.Boxing or
        ConversionKind.ImplicitEnumeration or ConversionKind.ImplicitUserDefined;

    public bool IsUserDefined => Kind is
        ConversionKind.ImplicitUserDefined or ConversionKind.ExplicitUserDefined;
}

/// <summary>
/// Classifies conversions between CLR types following the subset of ECMA-334 §10 that the
/// engine implements. Everything the binder needs to know about assignability funnels through here.
/// </summary>
public static class Conversions
{
    // Index order used by the implicit/explicit numeric matrices.
    private static readonly Type[] NumericTypes =
    [
        typeof(sbyte), typeof(byte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(char), typeof(float), typeof(double), typeof(decimal),
    ];

    private const int SByteIdx = 0, ByteIdx = 1, ShortIdx = 2, UShortIdx = 3;
    private const int IntIdx = 4, UIntIdx = 5, LongIdx = 6, ULongIdx = 7;
    private const int CharIdx = 8, FloatIdx = 9, DoubleIdx = 10, DecimalIdx = 11;

    private static readonly bool[,] ImplicitNumericMatrix = BuildImplicitNumericMatrix();

    private static bool[,] BuildImplicitNumericMatrix()
    {
        var m = new bool[NumericTypes.Length, NumericTypes.Length];

        void Allow(int from, params int[] to)
        {
            foreach (var t in to) m[from, t] = true;
        }

        Allow(SByteIdx, ShortIdx, IntIdx, LongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(ByteIdx, ShortIdx, UShortIdx, IntIdx, UIntIdx, LongIdx, ULongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(ShortIdx, IntIdx, LongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(UShortIdx, IntIdx, UIntIdx, LongIdx, ULongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(IntIdx, LongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(UIntIdx, LongIdx, ULongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(LongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(ULongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(CharIdx, UShortIdx, IntIdx, UIntIdx, LongIdx, ULongIdx, FloatIdx, DoubleIdx, DecimalIdx);
        Allow(FloatIdx, DoubleIdx);

        return m;
    }

    internal static int NumericIndex(Type type)
    {
        if (type == typeof(int)) return IntIdx;
        if (type == typeof(long)) return LongIdx;
        if (type == typeof(double)) return DoubleIdx;
        if (type == typeof(decimal)) return DecimalIdx;
        if (type == typeof(float)) return FloatIdx;
        if (type == typeof(uint)) return UIntIdx;
        if (type == typeof(ulong)) return ULongIdx;
        if (type == typeof(short)) return ShortIdx;
        if (type == typeof(ushort)) return UShortIdx;
        if (type == typeof(byte)) return ByteIdx;
        if (type == typeof(sbyte)) return SByteIdx;
        if (type == typeof(char)) return CharIdx;
        return -1;
    }

    public static bool IsNumeric(Type type) => NumericIndex(type) >= 0;

    public static bool IsIntegral(Type type) =>
        NumericIndex(type) is >= SByteIdx and <= ULongIdx or CharIdx;

    public static bool IsNullableValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

    public static Type Unlift(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    public static Type Lift(Type type) =>
        type.IsValueType && !IsNullableValueType(type) ? typeof(Nullable<>).MakeGenericType(type) : type;

    /// <summary>True when a value of this type may be null.</summary>
    public static bool IsNullAssignable(Type type) => !type.IsValueType || IsNullableValueType(type);

    /// <summary>
    /// Classifies the conversion from <paramref name="from"/> to <paramref name="to"/>.
    /// Pass <see cref="NullLiteralType"/> as <paramref name="from"/> for the untyped null literal.
    /// </summary>
    public static Conversion Classify(Type from, Type to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from == to) return Conversion.Identity;

        if (from == NullLiteralType)
            return IsNullAssignable(to) ? new Conversion(ConversionKind.ImplicitNullLiteral) : Conversion.None;

        // Lambdas are converted by the binder once a target delegate type is known, never here.
        if (IsUntyped(from) || IsUntyped(to)) return Conversion.None;

        if (to == typeof(void) || from == typeof(void)) return Conversion.None;

        // --- nullable value types -------------------------------------------------
        var fromNullable = IsNullableValueType(from);
        var toNullable = IsNullableValueType(to);
        var fromBase = Unlift(from);
        var toBase = Unlift(to);

        if (fromNullable || toNullable)
        {
            if (fromBase == toBase)
            {
                // T -> T?  is implicit;  T? -> T  is explicit
                return toNullable
                    ? new Conversion(ConversionKind.ImplicitNullable)
                    : new Conversion(ConversionKind.ExplicitNullable);
            }

            if (IsNumeric(fromBase) && IsNumeric(toBase))
            {
                var lifted = ClassifyNumeric(fromBase, toBase);
                if (lifted.Kind == ConversionKind.ImplicitNumeric && toNullable)
                    return new Conversion(ConversionKind.ImplicitNullable);
                if (lifted.Exists)
                    return new Conversion(ConversionKind.ExplicitNullable);
            }

            if (!fromNullable && toNullable && from.IsEnum && toBase == from)
                return new Conversion(ConversionKind.ImplicitNullable);

            if (fromNullable && !toNullable && to == typeof(object))
                return new Conversion(ConversionKind.Boxing);

            return Conversion.None;
        }

        // --- enums ----------------------------------------------------------------
        if (from.IsEnum || to.IsEnum)
        {
            var fromUnderlying = from.IsEnum ? Enum.GetUnderlyingType(from) : from;
            var toUnderlying = to.IsEnum ? Enum.GetUnderlyingType(to) : to;

            if (to == typeof(object) || to == typeof(Enum) || to == typeof(ValueType))
                return new Conversion(ConversionKind.Boxing);

            if (IsNumeric(fromUnderlying) && IsNumeric(toUnderlying))
                return new Conversion(ConversionKind.ExplicitEnumeration);

            return Conversion.None;
        }

        // --- numeric --------------------------------------------------------------
        if (IsNumeric(from) && IsNumeric(to))
            return ClassifyNumeric(from, to);

        // --- reference / boxing ---------------------------------------------------
        if (to.IsAssignableFrom(from))
            return from.IsValueType && !to.IsValueType
                ? new Conversion(ConversionKind.Boxing)
                : new Conversion(ConversionKind.ImplicitReference);

        if (from.IsAssignableFrom(to))
            return to.IsValueType && !from.IsValueType
                ? new Conversion(ConversionKind.Unboxing)
                : new Conversion(ConversionKind.ExplicitReference);

        // Interfaces can always be attempted downwards at runtime.
        if (from.IsInterface && !to.IsSealed) return new Conversion(ConversionKind.ExplicitReference);
        if (to.IsInterface && !from.IsSealed) return new Conversion(ConversionKind.ExplicitReference);

        // --- user defined ---------------------------------------------------------
        return ClassifyUserDefined(from, to);
    }

    private static Conversion ClassifyNumeric(Type from, Type to)
    {
        var f = NumericIndex(from);
        var t = NumericIndex(to);
        if (f < 0 || t < 0) return Conversion.None;
        if (f == t) return Conversion.Identity;

        return ImplicitNumericMatrix[f, t]
            ? new Conversion(ConversionKind.ImplicitNumeric)
            : new Conversion(ConversionKind.ExplicitNumeric);
    }

    private static Conversion ClassifyUserDefined(Type from, Type to)
    {
        foreach (var declaring in new[] { from, to })
        {
            foreach (var method in declaring.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name is not ("op_Implicit" or "op_Explicit")) continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                if (parameters[0].ParameterType != from || method.ReturnType != to) continue;

                return method.Name == "op_Implicit"
                    ? new Conversion(ConversionKind.ImplicitUserDefined, method)
                    : new Conversion(ConversionKind.ExplicitUserDefined, method);
            }
        }

        return Conversion.None;
    }

    /// <summary>Sentinel standing in for the type of the <c>null</c> literal.</summary>
    public static readonly Type NullLiteralType = typeof(NullLiteralSentinel);

    public sealed class NullLiteralSentinel
    {
        private NullLiteralSentinel() { }
    }

    /// <summary>
    /// Sentinel standing in for a lambda that has not been given a target type yet. A lambda is
    /// convertible only to a delegate, and only overload resolution can say which one, so it
    /// carries this until a parameter type is chosen.
    /// </summary>
    public static readonly Type LambdaType = typeof(LambdaSentinel);

    public sealed class LambdaSentinel
    {
        private LambdaSentinel() { }
    }

    /// <summary>Sentinel for <c>[a, b, c]</c> before a target type is known.</summary>
    public static readonly Type CollectionType = typeof(CollectionSentinel);

    public sealed class CollectionSentinel
    {
        private CollectionSentinel() { }
    }

    /// <summary>Sentinel for the bare <c>default</c> literal.</summary>
    public static readonly Type DefaultLiteralType = typeof(DefaultSentinel);

    public sealed class DefaultSentinel
    {
        private DefaultSentinel() { }
    }

    /// <summary>Sentinel for a throw expression, which never produces a value at all.</summary>
    public static readonly Type ThrowType = typeof(ThrowSentinel);

    public sealed class ThrowSentinel
    {
        private ThrowSentinel() { }
    }

    /// <summary>
    /// True for the expressions that have no type of their own until something gives them one:
    /// <c>null</c>, a lambda, a collection expression, <c>default</c>, and a throw expression.
    /// They can never be a <c>var</c> initializer or an inferred common type.
    /// </summary>
    public static bool IsUntyped(Type type) =>
        type == NullLiteralType || type == LambdaType || type == CollectionType ||
        type == DefaultLiteralType || type == ThrowType;

    /// <summary>
    /// True for the untyped forms that simply adopt whatever type the context wants, which is
    /// what lets them sit opposite a typed branch in <c>?:</c> or <c>??</c>.
    /// </summary>
    public static bool AdoptsTargetType(Type type) =>
        type == CollectionType || type == DefaultLiteralType || type == ThrowType;

    /// <summary>
    /// The interfaces a collection expression satisfies by building an array, in the order C#
    /// lists them. An array implements all of them, so no wrapper type is needed.
    /// </summary>
    private static readonly Type[] ArrayBackedInterfaces =
    [
        typeof(IEnumerable<>), typeof(ICollection<>), typeof(IList<>),
        typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>),
    ];

    /// <summary>
    /// Can <c>[a, b, c]</c> become a <paramref name="target"/>? Either it is an array or one of
    /// the interfaces an array implements, or it is a type that can be built up with
    /// <c>Add</c> — the same shape a collection initializer needs.
    /// </summary>
    public static bool CouldBeCollection(Type target) => CollectionElementType(target) is not null;

    /// <summary>The element type <paramref name="target"/> wants, or null when it is not a collection.</summary>
    public static Type? CollectionElementType(Type target)
    {
        if (target.IsArray && target.GetArrayRank() == 1) return target.GetElementType();

        if (target.IsGenericType && ArrayBackedInterfaces.Contains(target.GetGenericTypeDefinition()))
            return target.GetGenericArguments()[0];

        if (target.IsAbstract || target.IsInterface) return null;
        if (target.GetConstructor(Type.EmptyTypes) is null) return null;
        if (target.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance) is null) return null;

        return ElementTypeOf(target) ?? typeof(object);
    }

    /// <summary>The T of the first <c>IEnumerable&lt;T&gt;</c> a type implements.</summary>
    private static Type? ElementTypeOf(Type type)
    {
        foreach (var candidate in type.GetInterfaces())
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0];

        return null;
    }

    public static bool IsDelegateType(Type type) =>
        typeof(Delegate).IsAssignableFrom(type) &&
        type != typeof(Delegate) &&
        type != typeof(MulticastDelegate);

    public static MethodInfo? GetInvokeMethod(Type delegateType) =>
        IsDelegateType(delegateType) ? delegateType.GetMethod("Invoke") : null;

    public static bool HasImplicit(Type from, Type to) => Classify(from, to).IsImplicit;

    /// <summary>
    /// Ranks two candidate target types for the same source type, implementing the
    /// "better conversion target" tie-break used by overload resolution.
    /// Returns a negative number when <paramref name="left"/> wins.
    /// </summary>
    public static int CompareConversionTargets(Type source, Type left, Type right)
    {
        if (left == right) return 0;
        if (source == left) return -1;
        if (source == right) return 1;

        var leftFromRight = HasImplicit(right, left);
        var rightFromLeft = HasImplicit(left, right);

        if (rightFromLeft && !leftFromRight) return -1;
        if (leftFromRight && !rightFromLeft) return 1;

        // Neither target subsumes the other, so fall back to how the argument reaches each one.
        // A reference conversion is a better fit than a user-defined one, which is what keeps
        // an array binding to IEnumerable<T> rather than to ReadOnlySpan<T>.
        var leftRank = Rank(Classify(source, left).Kind);
        var rightRank = Rank(Classify(source, right).Kind);

        return leftRank.CompareTo(rightRank);
    }

    /// <summary>Lower is a closer fit. Used only to break ties that betterness leaves open.</summary>
    private static int Rank(ConversionKind kind) => kind switch
    {
        ConversionKind.Identity => 0,
        ConversionKind.ImplicitReference => 1,
        ConversionKind.Boxing => 2,
        ConversionKind.ImplicitNumeric => 3,
        ConversionKind.ImplicitNullable => 4,
        ConversionKind.ImplicitEnumeration => 5,
        ConversionKind.ImplicitNullLiteral => 6,
        ConversionKind.ImplicitUserDefined => 7,
        _ => 8,
    };

    public static bool TryGetElementType(Type collection, [NotNullWhen(true)] out Type? elementType)
    {
        if (collection.IsArray)
        {
            elementType = collection.GetElementType()!;
            return true;
        }

        foreach (var iface in Enumerable.Concat(
                     collection.IsInterface ? [collection] : Array.Empty<Type>(),
                     collection.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(collection))
        {
            elementType = typeof(object);
            return true;
        }

        elementType = null;
        return false;
    }
}
