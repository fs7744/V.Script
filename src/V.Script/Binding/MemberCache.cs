using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace V.Script.Binding;

/// <summary>
/// Memoises the reflection lookups the binder repeats on every call site and every operator.
/// </summary>
/// <remarks>
/// <see cref="Type.GetMethods(BindingFlags)"/> and <see cref="MethodBase.GetParameters"/> hand
/// back a fresh array on each call, so binding <c>Price * Quantity</c> scans and allocates the
/// whole of <see cref="decimal"/>'s static method table twice. The <see cref="MethodInfo"/>
/// objects themselves are already cached by the runtime; only the arrays are not.
/// <para>
/// Keys are held weakly, so caching a globals type does not pin the assembly it came from —
/// a host that loads rules into a collectible assembly can still unload it.
/// </para>
/// <para>
/// Every array returned from here is shared. Callers must treat them as read-only.
/// </para>
/// </remarks>
internal static class MemberCache
{
    private static readonly ConditionalWeakTable<Type, TypeEntry> Types = new();
    private static readonly ConditionalWeakTable<MethodBase, ParameterInfo[]> Parameters = new();

    /// <summary>The equivalent of <c>type.GetMethods(flags)</c>, shared and read-only.</summary>
    public static MethodInfo[] Methods(Type type, BindingFlags flags) =>
        Types.GetValue(type, static t => new TypeEntry(t)).Methods(flags);

    /// <summary>The methods of <paramref name="type"/> called <paramref name="name"/>.</summary>
    /// <remarks>
    /// Named lookup is what the binder actually wants — a call site, an operator, a conversion.
    /// Caching the filtered result keeps the common case down to one dictionary probe.
    /// </remarks>
    public static MethodInfo[] MethodsNamed(Type type, BindingFlags flags, string name) =>
        Types.GetValue(type, static t => new TypeEntry(t)).MethodsNamed(flags, name);

    /// <summary>The equivalent of <c>type.GetProperty(name, flags)</c>.</summary>
    /// <remarks>
    /// Every bare identifier in a script is a lookup against the globals type, and a miss is as
    /// common as a hit — resolving <c>Total</c> tries a property, then a field. Both answers,
    /// including <see langword="null"/>, are worth remembering.
    /// </remarks>
    public static PropertyInfo? Property(Type type, BindingFlags flags, string name) =>
        Types.GetValue(type, static t => new TypeEntry(t)).Property(flags, name);

    /// <summary>The equivalent of <c>type.GetField(name, flags)</c>.</summary>
    public static FieldInfo? Field(Type type, BindingFlags flags, string name) =>
        Types.GetValue(type, static t => new TypeEntry(t)).Field(flags, name);

    /// <summary>The equivalent of <c>type.GetProperties(flags)</c>, shared and read-only.</summary>
    public static PropertyInfo[] Properties(Type type, BindingFlags flags) =>
        Types.GetValue(type, static t => new TypeEntry(t)).Properties(flags);

    /// <summary>The equivalent of <c>type.GetInterfaces()</c>, shared and read-only.</summary>
    public static Type[] Interfaces(Type type) =>
        Types.GetValue(type, static t => new TypeEntry(t)).Interfaces();

    /// <summary>The equivalent of <c>method.GetParameters()</c>, shared and read-only.</summary>
    /// <remarks>
    /// Overload resolution asks for a candidate's parameters several times over — once to test
    /// applicability, again for each betterness comparison — and every one of those was a copy.
    /// </remarks>
    public static ParameterInfo[] ParametersOf(MethodBase method) =>
        Parameters.GetValue(method, static m => m.GetParameters());

    private sealed class TypeEntry(Type type)
    {
        private readonly ConcurrentDictionary<BindingFlags, MethodInfo[]> _methods = new();
        private readonly ConcurrentDictionary<(BindingFlags Flags, string Name), MethodInfo[]> _named = new();
        private readonly ConcurrentDictionary<(BindingFlags Flags, string Name), PropertyInfo?> _property = new();
        private readonly ConcurrentDictionary<(BindingFlags Flags, string Name), FieldInfo?> _field = new();
        private readonly ConcurrentDictionary<BindingFlags, PropertyInfo[]> _properties = new();
        private Type[]? _interfaces;

        public MethodInfo[] Methods(BindingFlags flags) =>
            _methods.GetOrAdd(flags, static (f, t) => t.GetMethods(f), type);

        public MethodInfo[] MethodsNamed(BindingFlags flags, string name) =>
            _named.GetOrAdd((flags, name), static (key, self) =>
            {
                var all = self.Methods(key.Flags);
                var matches = new List<MethodInfo>();

                foreach (var method in all)
                    if (string.Equals(method.Name, key.Name, StringComparison.Ordinal))
                        matches.Add(method);

                return matches.Count == 0 ? [] : [.. matches];
            }, this);

        public PropertyInfo? Property(BindingFlags flags, string name) =>
            _property.GetOrAdd((flags, name), static (key, t) => t.GetProperty(key.Name, key.Flags), type);

        public FieldInfo? Field(BindingFlags flags, string name) =>
            _field.GetOrAdd((flags, name), static (key, t) => t.GetField(key.Name, key.Flags), type);

        public PropertyInfo[] Properties(BindingFlags flags) =>
            _properties.GetOrAdd(flags, static (f, t) => t.GetProperties(f), type);

        public Type[] Interfaces() => _interfaces ??= type.GetInterfaces();
    }
}
