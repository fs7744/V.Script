using System.ComponentModel;

namespace V.Script;

/// <summary>
/// The object bound as the first argument of every compiled delegate. It gives generated IL
/// access to the limits configured for the script without needing a static field.
/// </summary>
/// <remarks>
/// Part of the generated-code contract rather than the user-facing API.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScriptHost
{
    internal ScriptHost(ScriptLimits limits, string sourceName)
    {
        Limits = limits;
        SourceName = sourceName;
    }

    public ScriptLimits Limits { get; }

    public string SourceName { get; }
}
