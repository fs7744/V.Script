using System.Collections;

namespace V.Script.Diagnostics;

/// <summary>
/// Collects diagnostics during a compilation. The binder keeps going after an error
/// (producing error nodes) so that one compile reports every problem rather than the first.
/// </summary>
public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    public int Count => _items.Count;

    public bool HasErrors { get; private set; }

    public void Report(ErrorCode id, SourcePosition position, string message)
    {
        _items.Add(new Diagnostic(id, DiagnosticSeverity.Error, position, message));
        HasErrors = true;
    }

    public void Warn(ErrorCode id, SourcePosition position, string message) =>
        _items.Add(new Diagnostic(id, DiagnosticSeverity.Warning, position, message));

    public IReadOnlyList<Diagnostic> ToImmutable() =>
        _items.OrderBy(d => d.Position.Line).ThenBy(d => d.Position.Column).ToArray();

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
