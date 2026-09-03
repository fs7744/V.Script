using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// Resolves source-level type names against the referenced assemblies and imported
/// namespaces. Lookups are cached because the binder hits this on every declaration and cast.
/// </summary>
public sealed class TypeResolver
{
    private static readonly FrozenDictionary<string, Type> Aliases =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["bool"] = typeof(bool),
            ["byte"] = typeof(byte),
            ["sbyte"] = typeof(sbyte),
            ["char"] = typeof(char),
            ["decimal"] = typeof(decimal),
            ["double"] = typeof(double),
            ["float"] = typeof(float),
            ["int"] = typeof(int),
            ["uint"] = typeof(uint),
            ["long"] = typeof(long),
            ["ulong"] = typeof(ulong),
            ["short"] = typeof(short),
            ["nint"] = typeof(nint),
            ["nuint"] = typeof(nuint),
            ["ushort"] = typeof(ushort),
            ["object"] = typeof(object),
            ["string"] = typeof(string),
            ["void"] = typeof(void),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IReadOnlyList<Assembly> _references;
    private readonly IReadOnlyList<string> _imports;
    private readonly ConcurrentDictionary<string, Type?> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<FrozenDictionary<string, MethodInfo[]>> _extensionMethods;

    public TypeResolver(IReadOnlyList<Assembly> references, IReadOnlyList<string> imports)
    {
        _references = references;
        _imports = imports;
        _extensionMethods = new Lazy<FrozenDictionary<string, MethodInfo[]>>(
            BuildExtensionIndex, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Resolves a parsed type reference, or returns null when it cannot be found.</summary>
    public Type? Resolve(TypeSyntax syntax)
    {
        var arity = syntax.TypeArguments.Count;
        var name = string.Join('.', syntax.NameParts);

        Type? type;
        if (arity == 0)
        {
            type = ResolveName(name);
        }
        else
        {
            var open = ResolveName($"{name}`{arity}");
            if (open is null) return null;

            var arguments = new Type[arity];
            for (var i = 0; i < arity; i++)
            {
                var argument = Resolve(syntax.TypeArguments[i]);
                if (argument is null) return null;
                arguments[i] = argument;
            }

            type = open.MakeGenericType(arguments);
        }

        if (type is null) return null;

        if (syntax.IsNullable)
        {
            // On a value type `T?` is Nullable<T>. On a reference type it is only an annotation
            // for a nullability analysis the engine does not perform, so it is accepted and
            // dropped rather than rejected — writing `string?` should not fail to compile.
            if (type.IsValueType && !Conversions.IsNullableValueType(type))
                type = typeof(Nullable<>).MakeGenericType(type);
        }

        // Bracket groups apply outermost-first, and each may be multi-dimensional.
        for (var i = syntax.ArrayRank - 1; i >= 0; i--)
        {
            var dimensions = syntax.DimensionsAt(i);
            type = dimensions == 1 ? type.MakeArrayType() : type.MakeArrayType(dimensions);
        }

        return type;
    }

    /// <summary>Resolves a dotted name, trying aliases, imports and assembly-qualified lookup.</summary>
    public Type? ResolveName(string name) => _cache.GetOrAdd(name, ResolveNameCore);

    private Type? ResolveNameCore(string name)
    {
        if (Aliases.TryGetValue(name, out var alias)) return alias;

        var direct = FindInReferences(name);
        if (direct is not null) return direct;

        foreach (var import in _imports)
        {
            var candidate = FindInReferences($"{import}.{name}");
            if (candidate is not null) return candidate;
        }

        // Nested types written with a dot, e.g. Outer.Inner
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var outer = ResolveName(name[..lastDot]);
            var inner = name[(lastDot + 1)..];
            if (outer is not null)
            {
                var nested = outer.GetNestedType(inner, BindingFlags.Public);
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    private Type? FindInReferences(string fullName)
    {
        foreach (var assembly in _references)
        {
            var type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type is not null && type.IsPublic || type is { IsNestedPublic: true })
                return type;
        }
        return null;
    }

    /// <summary>
    /// Extension methods visible through the imported namespaces, indexed by name. Built once
    /// per engine and only on first use, because scanning the exported types of every reference
    /// is not something an ordinary member lookup should pay for.
    /// </summary>
    private FrozenDictionary<string, MethodInfo[]> BuildExtensionIndex()
    {
        var imports = _imports.ToHashSet(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal);

        foreach (var assembly in _references)
        {
            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException or TypeLoadException)
            {
                continue;
            }

            foreach (var type in types)
            {
                // A static class in C# is abstract and sealed, and carries [Extension].
                if (!type.IsAbstract || !type.IsSealed || type.IsGenericTypeDefinition) continue;
                if (!imports.Contains(type.Namespace ?? string.Empty)) continue;
                if (!type.IsDefined(typeof(ExtensionAttribute), inherit: false)) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!method.IsDefined(typeof(ExtensionAttribute), inherit: false)) continue;
                    if (method.GetParameters().Length == 0) continue;

                    if (!byName.TryGetValue(method.Name, out var list))
                        byName[method.Name] = list = [];

                    list.Add(method);
                }
            }
        }

        return byName.ToFrozenDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Extension methods with this name, in declaration order. Empty when there are none.</summary>
    public IReadOnlyList<MethodInfo> GetExtensionMethods(string name) =>
        _extensionMethods.Value.TryGetValue(name, out var methods) ? methods : [];

    /// <summary>Whether any extension method by that name could plausibly take this receiver.</summary>
    public bool HasExtensionMethodCandidate(Type receiverType, string name) =>
        GetExtensionMethods(name).Any(m => CouldReceive(MemberCache.ParametersOf(m)[0].ParameterType, receiverType));

    private static bool CouldReceive(Type parameterType, Type receiverType)
    {
        if (parameterType.IsAssignableFrom(receiverType)) return true;

        // An open generic such as IEnumerable<T> only has to match by shape here.
        if (!parameterType.IsGenericType) return parameterType.IsGenericParameter;

        var definition = parameterType.GetGenericTypeDefinition();

        if (receiverType.IsArray && definition == typeof(IEnumerable<>)) return true;

        foreach (var iface in receiverType.GetInterfaces())
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == definition)
                return true;

        return receiverType.IsGenericType && receiverType.GetGenericTypeDefinition() == definition;
    }

    /// <summary>Renders a type the way a script author wrote it, for diagnostics.</summary>
    public static string Display(Type type)
    {
        if (type == Conversions.NullLiteralType) return "null";

        foreach (var (alias, aliased) in Aliases)
            if (aliased == type) return alias;

        if (Conversions.IsNullableValueType(type))
            return Display(Nullable.GetUnderlyingType(type)!) + "?";

        if (type.IsArray)
            return Display(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>";
        }

        return type.Name;
    }
}
