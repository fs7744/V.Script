namespace V.Script;

/// <summary>
/// Execution limits enforced by checkpoints the compiler injects at loop back-edges.
/// Generated IL cannot be interrupted from outside, so every limit here is cooperative.
/// </summary>
public sealed record ScriptLimits
{
    /// <summary>No budget, no timeout. Suitable only for fully trusted scripts.</summary>
    public static readonly ScriptLimits Unlimited = new();

    /// <summary>A conservative default: 10 million loop iterations and a two second wall clock.</summary>
    public static readonly ScriptLimits Default = new()
    {
        MaxSteps = 10_000_000,
        Timeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// Maximum number of loop iterations across the whole invocation. Null disables the budget.
    /// </summary>
    public long? MaxSteps { get; init; }

    /// <summary>
    /// Wall-clock limit for one invocation. For synchronous scripts this is checked at loop
    /// back-edges; for asynchronous scripts it additionally cancels pending awaits.
    /// Null disables the timeout.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Reserved. Scripts cannot currently declare functions or lambdas, so no script-level
    /// recursion is reachable and this value is not enforced.
    /// </summary>
    public int? MaxStackDepth { get; init; }

    internal bool NeedsCheckpoints => MaxSteps is not null || Timeout is not null;
}
