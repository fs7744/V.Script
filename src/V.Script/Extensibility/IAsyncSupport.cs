using System.Reflection;
using System.Reflection.Emit;

namespace V.Script;

/// <summary>
/// Everything the compiler needs in order to compile <c>await</c> and <c>async</c> lambdas.
/// </summary>
/// <remarks>
/// The base library parses both — they are part of the grammar — but cannot compile either,
/// because doing so needs .NET 11's runtime-async support and a real type to hold methods
/// marked with the Async implementation flag. <c>V.Script.Async</c> supplies an implementation
/// and installs it on <see cref="ScriptOptions"/>; without one, a script using <c>await</c> or
/// an <c>async</c> lambda gets a compile error rather than silently different behaviour.
/// <para>
/// Internal on purpose: this is a seam between the two libraries, not a public extension point.
/// </para>
/// </remarks>
internal interface IAsyncSupport
{
    /// <summary>
    /// The value an async body with this declared return type actually produces — <c>void</c>
    /// for <c>Task</c>, <c>T</c> for <c>Task&lt;T&gt;</c> — or null when it is not a task at all.
    /// </summary>
    Type? UnwrapAsyncReturnType(Type declaredReturnType);

    /// <summary>
    /// The result of awaiting <paramref name="awaitable"/> together with the static method that
    /// suspends on it, or null when the type cannot be awaited.
    /// </summary>
    /// <remarks>
    /// The method takes the awaitable and returns the result, so <c>await x</c> lowers to an
    /// ordinary call. That is the whole of runtime-async on the emitter's side — it has no
    /// await-specific code at all.
    /// </remarks>
    (Type ResultType, MethodInfo Suspend)? DescribeAwaitable(Type awaitable);

    /// <summary>
    /// Reserves a type to emit methods that must carry the Async implementation flag into.
    /// </summary>
    /// <remarks>
    /// A <see cref="DynamicMethod"/> has no <c>SetImplementationFlags</c>, so it can never be
    /// marked Async. Anything that suspends therefore needs a real type in a real assembly.
    /// </remarks>
    IGeneratedType ReserveType(string name);
}

/// <summary>
/// A type reserved for generated code. Holding it keeps that code loaded; disposing it is what
/// lets the assembly behind it unload.
/// </summary>
internal interface IGeneratedType : IDisposable
{
    TypeBuilder Builder { get; }

    /// <summary>Pins the finished type once <c>CreateType</c> has run.</summary>
    void Publish(Type created);
}
