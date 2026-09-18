// Types the compiler or the code needs on older targets. Every one of them is compiled out on
// frameworks that ship it, so the modern builds are byte-for-byte unaffected.
//
// The pattern — declaring these in your own assembly as internal — is the standard way to use a
// newer language feature against an older framework. The compiler looks the type up by name, not
// by assembly, so its own definition satisfies it.

#if !NET7_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field |
                    AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute;

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;

        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute;
}

#endif

#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    /// <summary>Required by every <c>init</c> accessor and every positional record.</summary>
    internal static class IsExternalInit;
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute
    {
        public bool ReturnValue { get; } = returnValue;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property,
                    Inherited = false)]
    internal sealed class MaybeNullWhenAttribute(bool returnValue) : Attribute
    {
        public bool ReturnValue { get; } = returnValue;
    }
}

namespace System
{
    /// <summary>
    /// What <c>^1</c> compiles to. Only the engine's own code produces one — scripts cannot use
    /// <c>^</c> or <c>..</c> on this target, precisely so that this internal type never escapes
    /// into generated IL.
    /// </summary>
    internal readonly struct Index(int value, bool fromEnd = false)
    {
        private readonly int _value = fromEnd ? ~value : value;

        public int Value => _value < 0 ? ~_value : _value;

        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length - ~_value : _value;

        public static implicit operator Index(int value) => new(value);
    }

    /// <summary>What <c>a..b</c> compiles to; on a string it becomes a <c>Substring</c> call.</summary>
    internal readonly struct Range(Index start, Index end)
    {
        public Index Start { get; } = start;

        public Index End { get; } = end;

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);

            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length), "范围超出集合边界。");

            return (start, end - start);
        }
    }

    /// <summary>Enough of <see cref="HashCode"/> for combining a handful of values.</summary>
    internal struct HashCode
    {
        private int _hash;
        private bool _started;

        public void Add<T>(T value)
        {
            var next = value?.GetHashCode() ?? 0;
            _hash = _started ? unchecked((_hash * 31) + next) : next;
            _started = true;
        }

        public readonly int ToHashCode() => _hash;
    }
}

#endif
