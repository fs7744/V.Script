using System.Collections.Immutable;
using System.Reflection;

namespace V.Script;

/// <summary>
/// Compilation settings: which assemblies a script may name types from, and which namespaces
/// are searched for unqualified names.
/// </summary>
public sealed record ScriptOptions
{
    private static readonly ImmutableArray<Assembly> CoreReferences =
    [
        typeof(object).Assembly,
        typeof(Enumerable).Assembly,
        typeof(List<>).Assembly,
        typeof(Console).Assembly,
    ];

    private static readonly ImmutableArray<string> CoreImports =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
    ];

    /// <summary>Core BCL references and the usual three imports.</summary>
    public static ScriptOptions Default { get; } = new()
    {
        References = CoreReferences.Distinct().ToImmutableArray(),
        Imports = CoreImports,
    };

    public ImmutableArray<Assembly> References { get; init; } = [];

    public ImmutableArray<string> Imports { get; init; } = [];

    public ScriptOptions AddReferences(params ReadOnlySpan<Assembly> assemblies)
    {
        var builder = References.ToBuilder();
        foreach (var assembly in assemblies)
            if (!builder.Contains(assembly))
                builder.Add(assembly);
        return this with { References = builder.ToImmutable() };
    }

    /// <summary>Adds the assemblies declaring the given types.</summary>
    public ScriptOptions AddReferencesFrom(params ReadOnlySpan<Type> types)
    {
        var assemblies = new List<Assembly>();
        foreach (var type in types) assemblies.Add(type.Assembly);
        return AddReferences(assemblies.ToArray());
    }

    public ScriptOptions AddImports(params ReadOnlySpan<string> namespaces)
    {
        var builder = Imports.ToBuilder();
        foreach (var ns in namespaces)
            if (!builder.Contains(ns))
                builder.Add(ns);
        return this with { Imports = builder.ToImmutable() };
    }
}
