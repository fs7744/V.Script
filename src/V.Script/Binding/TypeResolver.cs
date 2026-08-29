using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
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
            ["ushort"] = typeof(ushort),
            ["object"] = typeof(object),
            ["string"] = typeof(string),
            ["void"] = typeof(void),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IReadOnlyList<Assembly> _references;
    private readonly IReadOnlyList<string> _imports;
    private readonly ConcurrentDictionary<string, Type?> _cache = new(StringComparer.Ordinal);

    public TypeResolver(IReadOnlyList<Assembly> references, IReadOnlyList<string> imports)
    {
        _references = references;
        _imports = imports;
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
            if (!type.IsValueType || Conversions.IsNullableValueType(type)) return null;
            type = typeof(Nullable<>).MakeGenericType(type);
        }

        for (var i = 0; i < syntax.ArrayRank; i++)
            type = type.MakeArrayType();

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
    /// Looks for extension methods named <paramref name="name"/> that could apply to
    /// <paramref name="receiverType"/>. Only used on the error path, to turn a vague
    /// "no such member" into an explicit "extension methods are not supported yet".
    /// </summary>
    public bool HasExtensionMethodCandidate(Type receiverType, string name)
    {
        foreach (var assembly in _references)
        {
            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileNotFoundException)
            {
                continue;
            }

            foreach (var type in types)
            {
                if (!type.IsSealed || !type.IsAbstract || type.IsGenericType) continue;
                if (!_imports.Contains(type.Namespace ?? string.Empty)) continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != name) continue;
                    if (!method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length == 0) continue;

                    if (CouldReceive(parameters[0].ParameterType, receiverType)) return true;
                }
            }
        }

        return false;
    }

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
