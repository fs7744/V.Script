using System.Reflection.Emit;
#if NETSTANDARD2_0
using System.Collections.Concurrent;
#endif

namespace V.Script;

/// <summary>
/// The few reflection members whose shape differs across targets.
/// </summary>
internal static class Compat
{
    /// <summary>
    /// Whether a type is a <c>ref struct</c>. netstandard2.0 predates the concept, and nothing
    /// that reaches these call sites can be one there, so false is the correct answer.
    /// </summary>
    public static bool IsByRefLike(this Type type)
    {
#if NETSTANDARD2_0
        return false;
#else
        return type.IsByRefLike;
#endif
    }

#if NETSTANDARD2_0

    /// <summary>
    /// The factory-with-argument overload of <c>GetOrAdd</c>, which netstandard2.0 predates.
    /// </summary>
    /// <remarks>
    /// Being an extension it is only found where the instance method is missing, so the callers
    /// need no conditional of their own. Like the real one, the factory may run more than once
    /// under contention and only one result is kept.
    /// </remarks>
    public static TValue GetOrAdd<TKey, TArg, TValue>(
        this ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key,
        Func<TKey, TArg, TValue> factory,
        TArg argument)
        where TKey : notnull =>
        dictionary.TryGetValue(key, out var existing)
            ? existing
            : dictionary.GetOrAdd(key, factory(key, argument));

#endif

    /// <summary>
    /// Finishes a generated type. The netstandard2.0 Reflection.Emit contract exposes only
    /// <c>CreateTypeInfo</c>; <c>TypeInfo</c> is a <see cref="Type"/>, so callers see no difference.
    /// </summary>
    public static Type CreateGeneratedType(this TypeBuilder builder)
    {
#if NETSTANDARD2_0
        return builder.CreateTypeInfo()!.AsType();
#else
        return builder.CreateType()!;
#endif
    }
}
