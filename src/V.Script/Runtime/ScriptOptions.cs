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

        // Needed to write Task<T> in a delegate type, which an async lambda's target requires.
        "System.Threading.Tasks",
    ];

    /// <summary>Core BCL references and the usual imports.</summary>
    public static ScriptOptions Default { get; } = new()
    {
        References = CoreReferences.Distinct().ToImmutableArray(),
        Imports = CoreImports,
    };

    public ImmutableArray<Assembly> References { get; init; } = [];

    public ImmutableArray<string> Imports { get; init; } = [];

    /// <summary>Symbols that <c>#if</c> sees as defined.</summary>
    public ImmutableArray<string> PreprocessorSymbols { get; init; } = [];

    /// <summary>
    /// How many scripts share one generated assembly. The default, 1, gives every script its own.
    /// </summary>
    /// <remarks>
    /// Only asynchronous scripts and synchronous scripts containing an <c>async</c> lambda need a
    /// generated assembly at all; everything else is a <c>DynamicMethod</c> and is unaffected.
    /// <para>
    /// Creating a collectible assembly is nearly all of what an asynchronous compile costs, and
    /// it is charged per assembly rather than per script, so raising this amortises it — at the
    /// price of unloading granularity. With the default, disposing a script reclaims its code
    /// immediately. With a larger value, an assembly is only reclaimed once every script sharing
    /// it has been disposed, so one long-lived script keeps its whole batch resident. Raise it
    /// when scripts are compiled and retired in batches; leave it alone when their lifetimes are
    /// independent.
    /// </para>
    /// </remarks>
    public int ScriptsPerGeneratedAssembly
    {
        get;
        init => field = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "每个程序集至少要放一个脚本。");
    } = 1;

    public ScriptOptions AddPreprocessorSymbols(params ReadOnlySpan<string> symbols)
    {
        var builder = PreprocessorSymbols.ToBuilder();
        foreach (var symbol in symbols)
            if (!builder.Contains(symbol))
                builder.Add(symbol);

        return this with { PreprocessorSymbols = builder.ToImmutable() };
    }

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
