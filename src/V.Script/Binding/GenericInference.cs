using System.Reflection;

namespace V.Script.Binding;

/// <summary>
/// Infers the type arguments of a generic method from the call's arguments, implementing the
/// shape of ECMA-334 §12.6.3 that ordinary calls need.
/// </summary>
/// <remarks>
/// Two rounds, because lambdas contribute in both directions. The first round fixes what the
/// ordinary arguments determine — <c>TSource</c> in <c>Where&lt;TSource&gt;</c> comes from the
/// sequence. The second round then knows each lambda's parameter types, binds its body through
/// the supplied probe, and lets the body's type fix what remains — which is how
/// <c>Select&lt;TSource, TResult&gt;</c> learns <c>TResult</c>.
/// <para>
/// Not implemented: inference from method groups, variance-aware "best common type" when one
/// type parameter is inferred from several arguments (the first binding wins), and constraint
/// re-inference. Failures simply mean the candidate is not applicable.
/// </para>
/// </remarks>
public static class GenericInference
{
    /// <summary>
    /// Binds a lambda argument's body given its parameter types and reports its natural return
    /// type, or null when that cannot be determined.
    /// </summary>
    public delegate Type? LambdaReturnProbe(int argumentIndex, Type[] parameterTypes);

    public static MethodInfo? TryInfer(
        MethodInfo definition,
        IReadOnlyList<ArgumentInfo> arguments,
        IReadOnlyList<int> argumentToParameter,
        LambdaReturnProbe? probe)
    {
        var typeParameters = definition.GetGenericArguments();
        var parameters = definition.GetParameters();
        var bound = new Dictionary<Type, Type>();

        // Round 1: everything that is not a lambda.
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameterIndex = argumentToParameter[i];
            if (parameterIndex < 0 || parameterIndex >= parameters.Length) return null;
            if (arguments[i].IsUnboundLambda) continue;

            Unify(parameters[parameterIndex].ParameterType, arguments[i].Type, bound);
        }

        // Round 2: lambdas, repeated while progress is being made.
        if (probe is not null)
        {
            var progress = true;
            while (progress && !AllBound(typeParameters, bound))
            {
                progress = false;

                for (var i = 0; i < arguments.Count; i++)
                {
                    if (!arguments[i].IsUnboundLambda) continue;

                    var parameterIndex = argumentToParameter[i];
                    if (parameterIndex < 0 || parameterIndex >= parameters.Length) continue;

                    var invoke = Conversions.GetInvokeMethod(parameters[parameterIndex].ParameterType);
                    if (invoke is null) continue;

                    var lambdaParameters = invoke.GetParameters();
                    if (lambdaParameters.Length != arguments[i].LambdaArity) continue;

                    var substituted = new Type[lambdaParameters.Length];
                    var closed = true;

                    for (var p = 0; p < lambdaParameters.Length; p++)
                    {
                        var type = Substitute(lambdaParameters[p].ParameterType, bound);
                        if (type is null) { closed = false; break; }
                        substituted[p] = type;
                    }

                    if (!closed) continue;

                    var returnType = probe(i, substituted);
                    if (returnType is null) continue;

                    var before = bound.Count;
                    Unify(invoke.ReturnType, returnType, bound);
                    if (bound.Count != before) progress = true;
                }
            }
        }

        if (!AllBound(typeParameters, bound)) return null;

        var typeArguments = new Type[typeParameters.Length];
        for (var i = 0; i < typeParameters.Length; i++) typeArguments[i] = bound[typeParameters[i]];

        try
        {
            return definition.MakeGenericMethod(typeArguments);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Constraints not satisfied — the candidate simply does not apply.
            return null;
        }
    }

    private static bool AllBound(Type[] typeParameters, Dictionary<Type, Type> bound) =>
        typeParameters.All(bound.ContainsKey);

    /// <summary>
    /// Matches an argument type against a parameter type pattern, recording what each type
    /// parameter must be. Unknowns simply stay unrecorded; there is no failure state.
    /// </summary>
    private static void Unify(Type parameter, Type argument, Dictionary<Type, Type> bound)
    {
        if (argument == Conversions.NullLiteralType || argument == Conversions.LambdaType) return;

        if (parameter.IsGenericParameter)
        {
            bound.TryAdd(parameter, argument);
            return;
        }

        if (parameter.IsArray)
        {
            if (argument.IsArray)
                Unify(parameter.GetElementType()!, argument.GetElementType()!, bound);
            return;
        }

        if (!parameter.IsGenericType) return;

        var definition = parameter.GetGenericTypeDefinition();
        var parameterArguments = parameter.GetGenericArguments();

        foreach (var candidate in Candidates(argument))
        {
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != definition) continue;

            var candidateArguments = candidate.GetGenericArguments();
            for (var i = 0; i < parameterArguments.Length && i < candidateArguments.Length; i++)
                Unify(parameterArguments[i], candidateArguments[i], bound);

            return;
        }
    }

    /// <summary>The argument type itself, its interfaces and its base types — anything the pattern might match.</summary>
    private static IEnumerable<Type> Candidates(Type argument)
    {
        yield return argument;

        // An array satisfies IEnumerable<T> for its element type.
        if (argument.IsArray && argument.GetArrayRank() == 1)
            yield return typeof(IEnumerable<>).MakeGenericType(argument.GetElementType()!);

        foreach (var iface in argument.GetInterfaces()) yield return iface;

        for (var baseType = argument.BaseType; baseType is not null; baseType = baseType.BaseType)
            yield return baseType;
    }

    /// <summary>Replaces type parameters with what has been inferred, or returns null if any is still open.</summary>
    private static Type? Substitute(Type type, Dictionary<Type, Type> bound)
    {
        if (type.IsGenericParameter)
            return bound.TryGetValue(type, out var value) ? value : null;

        if (type.IsArray)
        {
            var element = Substitute(type.GetElementType()!, bound);
            return element?.MakeArrayType();
        }

        if (!type.IsGenericType) return type;

        var arguments = type.GetGenericArguments();
        var substituted = new Type[arguments.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            var value = Substitute(arguments[i], bound);
            if (value is null) return null;
            substituted[i] = value;
        }

        return type.GetGenericTypeDefinition().MakeGenericType(substituted);
    }
}
