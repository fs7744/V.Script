namespace V.Script.Binding;

/// <summary>
/// A lexical scope. Name lookup walks the parent chain at compile time so that a resolved
/// identifier costs nothing at run time — it becomes an IL local slot or an argument index.
/// </summary>
internal sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, LocalSymbol> _locals = new(StringComparer.Ordinal);

    public Scope? Parent { get; } = parent;

    public bool TryDeclare(LocalSymbol symbol)
    {
        if (_locals.ContainsKey(symbol.Name)) return false;
        _locals[symbol.Name] = symbol;
        return true;
    }

    public bool TryLookup(string name, out LocalSymbol symbol)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
            if (scope._locals.TryGetValue(name, out symbol!))
                return true;

        symbol = null!;
        return false;
    }

    /// <summary>True when the name is declared in this scope specifically, ignoring parents.</summary>
    public bool DeclaresLocally(string name) => _locals.ContainsKey(name);
}
