namespace V.Script;

/// <summary>
/// Argument and state checks whose framework helpers are not available on every target.
/// </summary>
/// <remarks>
/// These helpers are static methods on framework types, so they cannot be added from outside.
/// The choice is a conditional at every call site or one helper with the conditional inside;
/// this is the helper. Each is a throw-only path, so the JIT inlines the test and never has to
/// inline the throw.
/// </remarks>
internal static class Guard
{
    public static void NotDisposed(bool disposed, object instance)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(disposed, instance);
#else
        if (disposed) throw new ObjectDisposedException(instance.GetType().FullName);
#endif
    }

    public static void NotNull(object? argument, string parameterName)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(argument, parameterName);
#else
        if (argument is null) throw new ArgumentNullException(parameterName);
#endif
    }
}
