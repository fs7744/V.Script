namespace V.Script;

/// <summary>Turns on the parts of the language that need runtime-async.</summary>
public static class ScriptOptionsAsyncExtensions
{
    /// <summary>
    /// Installs async support, which is what lets a <em>synchronous</em> script contain an
    /// <c>async</c> lambda.
    /// </summary>
    /// <param name="options">The options to extend.</param>
    /// <param name="scriptsPerGeneratedAssembly">
    /// How many scripts share one generated assembly. The default, 1, gives every script its own,
    /// so disposing a script reclaims its code immediately. A larger value amortises assembly
    /// creation — nearly the whole cost of an asynchronous compile — but an assembly is then only
    /// reclaimed once every script sharing it has been disposed, so one long-lived script keeps
    /// its whole batch resident. Raise it when scripts are compiled and retired in batches.
    /// </param>
    /// <remarks>
    /// <see cref="ScriptEngineAsyncExtensions.CompileAsync{TGlobals, TResult}"/> does not require
    /// this: an asynchronous script is compiled by this library, so it can supply its own support.
    /// An <c>async</c> lambda inside a synchronous script is different — the base library decides
    /// at bind time whether it can compile one, and without this call it reports an error rather
    /// than guessing.
    /// </remarks>
    public static ScriptOptions WithAsync(this ScriptOptions options, int scriptsPerGeneratedAssembly = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(scriptsPerGeneratedAssembly, 1);

        return options with
        {
            AsyncSupport = new Async.AsyncSupport(
                new Async.GeneratedAssemblyPool(scriptsPerGeneratedAssembly)),
        };
    }
}
