using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace V.Script;

/// <summary>
/// Per-invocation execution state. Lives as an IL local inside the generated method, so a
/// script call allocates nothing for it.
/// </summary>
/// <remarks>
/// This type is part of the generated-code contract, not the user-facing API. It is public
/// only because emitted IL must be able to call it.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ScriptState
{
    private const long SlowCheckMask = 0x3FF; // consult the clock every 1024 checkpoints

    private long _budget;
    private long _deadlineTicks;
    private long _configuredBudget;
    private TimeSpan _timeout;
    private CancellationToken _cancellationToken;

    /// <summary>Called by generated IL at method entry when no cancellation token is available.</summary>
    public static ScriptState Create(ScriptHost host) => Create(host, CancellationToken.None);

    /// <summary>Called by generated IL at method entry.</summary>
    public static ScriptState Create(ScriptHost host, CancellationToken cancellationToken)
    {
        var limits = host.Limits;
        var budget = limits.MaxSteps ?? long.MaxValue;

        return new ScriptState
        {
            _budget = budget,
            _configuredBudget = budget,
            _timeout = limits.Timeout ?? TimeSpan.Zero,
            _deadlineTicks = limits.Timeout is { } t ? Environment.TickCount64 + (long)t.TotalMilliseconds : 0,
            _cancellationToken = cancellationToken,
        };
    }

    /// <summary>
    /// Injected at every loop back-edge. The fast path is a decrement and a branch; the clock
    /// and the cancellation token are only consulted once every 1024 iterations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Checkpoint()
    {
        var remaining = --_budget;
        if (remaining < 0) ThrowBudget();
        if ((remaining & SlowCheckMask) == 0) SlowCheck();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SlowCheck()
    {
        if (_deadlineTicks != 0 && Environment.TickCount64 >= _deadlineTicks)
            throw new ScriptTimeoutException(_timeout);

        _cancellationToken.ThrowIfCancellationRequested();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ThrowBudget() => throw new ScriptBudgetExceededException(_configuredBudget);

    /// <summary>The token generated IL threads into <c>Task.WaitAsync</c> at every await point.</summary>
    public readonly CancellationToken Token => _cancellationToken;
}
