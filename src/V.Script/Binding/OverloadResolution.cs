using System.Reflection;

namespace V.Script.Binding;

/// <summary>
/// An argument as seen by overload resolution. <paramref name="LambdaArity"/> is non-negative
/// when the argument is a lambda that has not been bound yet, in which case applicability is
/// decided by parameter count rather than by a conversion.
/// </summary>
public readonly record struct ArgumentInfo(Type Type, string? Name, int LambdaArity = -1)
{
    public bool IsUnboundLambda => LambdaArity >= 0;
}

/// <summary>
/// The chosen overload together with the mapping the binder needs to materialise the
/// final argument list (defaults filled in, params collected into an array).
/// </summary>
public sealed record ResolvedOverload
{
    public required MethodBase Method { get; init; }

    public required ParameterInfo[] Parameters { get; init; }

    public required bool Expanded { get; init; }

    /// <summary>True when this came from a generic definition via inference.</summary>
    public bool FromGenericDefinition { get; init; }

    /// <summary>For each parameter, the argument indices feeding it. Empty means "use the default".</summary>
    public required IReadOnlyList<int[]> ParameterArguments { get; init; }

    public Type? ParamsElementType { get; init; }
}

public enum OverloadOutcome
{
    Resolved,
    NoneApplicable,
    Ambiguous,
}

public readonly record struct OverloadResult(
    OverloadOutcome Outcome,
    ResolvedOverload? Best,
    IReadOnlyList<MethodBase> Considered,
    bool SkippedGenericCandidates = false);

/// <summary>
/// Implements the documented subset of ECMA-334 §12.6.4. Supported: positional and named
/// arguments, optional parameters, <c>params</c> expansion, and better-conversion tie-breaks.
/// Not supported: lambda return-type inference participating in betterness, generic method
/// type inference, <c>ref</c>/<c>out</c> parameters.
/// </summary>
public static class OverloadResolution
{
    public static OverloadResult Resolve(
        IReadOnlyList<MethodBase> candidates,
        IReadOnlyList<ArgumentInfo> arguments,
        GenericInference.LambdaReturnProbe? lambdaReturnProbe = null)
    {
        var applicable = new List<(ResolvedOverload Resolved, Type?[] ParameterTypes)>();
        var skippedGeneric = false;

        // Probing a lambda means binding its body, so the answer is memoised: betterness asks
        // for it once per candidate pair otherwise.
        var probed = new Dictionary<(int Index, string Signature), Type?>();

        GenericInference.LambdaReturnProbe? probe = lambdaReturnProbe is null ? null : (index, types) =>
        {
            var key = (index, string.Join(",", types.Select(t => t.AssemblyQualifiedName)));
            if (probed.TryGetValue(key, out var cached)) return cached;

            var value = lambdaReturnProbe(index, types);
            probed[key] = value;
            return value;
        };

        foreach (var original in candidates)
        {
            var candidate = original;
            var fromGeneric = candidate.IsGenericMethodDefinition;

            if (candidate.IsGenericMethodDefinition)
            {
                var constructed = TryConstruct((MethodInfo)candidate, arguments, probe);
                if (constructed is null)
                {
                    // Remember this so the caller can say inference failed rather than
                    // reporting a plain "no matching overload".
                    skippedGeneric = true;
                    continue;
                }

                candidate = constructed;
            }

            var parameters = candidate.GetParameters();
            if (parameters.Any(p => p.ParameterType.IsByRef)) continue;

            if (TryBuild(candidate, parameters, arguments, expanded: false, out var normal, out var normalTypes))
                applicable.Add((normal! with { FromGenericDefinition = fromGeneric }, normalTypes!));
            else if (TryBuild(candidate, parameters, arguments, expanded: true, out var expandedForm, out var expandedTypes))
                applicable.Add((expandedForm! with { FromGenericDefinition = fromGeneric }, expandedTypes!));
        }

        if (applicable.Count == 0)
            return new OverloadResult(OverloadOutcome.NoneApplicable, null, candidates, skippedGeneric);

        if (applicable.Count == 1)
            return new OverloadResult(OverloadOutcome.Resolved, applicable[0].Resolved, candidates);

        var best = 0;
        for (var i = 1; i < applicable.Count; i++)
        {
            var comparison = Compare(arguments, applicable[i], applicable[best], probe);
            if (comparison < 0) best = i;
        }

        // Verify the winner beats every other candidate outright, otherwise it is ambiguous.
        for (var i = 0; i < applicable.Count; i++)
        {
            if (i == best) continue;
            if (Compare(arguments, applicable[best], applicable[i], probe) >= 0)
                return new OverloadResult(OverloadOutcome.Ambiguous, null, candidates);
        }

        return new OverloadResult(OverloadOutcome.Resolved, applicable[best].Resolved, candidates);
    }

    /// <summary>
    /// Attempts to turn a generic method definition into a concrete method for this call.
    /// The argument-to-parameter map is computed first because inference needs to know which
    /// parameter each argument lines up with.
    /// </summary>
    private static MethodInfo? TryConstruct(
        MethodInfo definition,
        IReadOnlyList<ArgumentInfo> arguments,
        GenericInference.LambdaReturnProbe? probe)
    {
        var parameters = definition.GetParameters();
        if (parameters.Any(p => p.ParameterType.IsByRef)) return null;

        var map = MapArguments(parameters, arguments);
        return map is null ? null : GenericInference.TryInfer(definition, arguments, map, probe);
    }

    /// <summary>Which parameter each argument feeds, honouring names and <c>params</c>; null if impossible.</summary>
    private static int[]? MapArguments(ParameterInfo[] parameters, IReadOnlyList<ArgumentInfo> arguments)
    {
        var hasParams = parameters.Length > 0 &&
                        parameters[^1].GetCustomAttribute<ParamArrayAttribute>() is not null;

        var map = new int[arguments.Count];
        var positional = 0;

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Name is { } name)
            {
                var index = Array.FindIndex(parameters, p => p.Name == name);
                if (index < 0) return null;
                map[i] = index;
                continue;
            }

            if (positional < parameters.Length) map[i] = positional++;
            else if (hasParams) map[i] = parameters.Length - 1;
            else return null;
        }

        return map;
    }

    private static bool TryBuild(
        MethodBase method,
        ParameterInfo[] parameters,
        IReadOnlyList<ArgumentInfo> arguments,
        bool expanded,
        out ResolvedOverload? resolved,
        out Type?[]? parameterTypesPerArgument)
    {
        resolved = null;
        parameterTypesPerArgument = null;

        var hasParams = parameters.Length > 0 &&
                        parameters[^1].GetCustomAttribute<ParamArrayAttribute>() is not null &&
                        parameters[^1].ParameterType.IsArray;

        if (expanded && !hasParams) return false;

        var slots = new List<int>[parameters.Length];
        for (var i = 0; i < slots.Length; i++) slots[i] = [];

        var perArgument = new Type?[arguments.Count];
        var positional = 0;
        var fixedCount = expanded ? parameters.Length - 1 : parameters.Length;

        for (var a = 0; a < arguments.Count; a++)
        {
            var argument = arguments[a];
            int target;

            if (argument.Name is not null)
            {
                target = Array.FindIndex(parameters, p => p.Name == argument.Name);
                if (target < 0) return false;
                if (slots[target].Count > 0) return false;
            }
            else if (positional < fixedCount)
            {
                target = positional++;
            }
            else if (expanded)
            {
                target = parameters.Length - 1;
            }
            else
            {
                return false; // too many arguments
            }

            var parameterType = expanded && target == parameters.Length - 1
                ? parameters[target].ParameterType.GetElementType()!
                : parameters[target].ParameterType;

            if (argument.IsUnboundLambda)
            {
                if (Conversions.GetInvokeMethod(parameterType) is not { } invoke) return false;
                if (invoke.GetParameters().Length != argument.LambdaArity) return false;
                if (invoke.GetParameters().Any(p => p.ParameterType.IsByRef)) return false;
            }
            else if (!Conversions.Classify(argument.Type, parameterType).IsImplicit)
            {
                return false;
            }

            slots[target].Add(a);
            perArgument[a] = parameterType;
        }

        for (var p = 0; p < parameters.Length; p++)
        {
            if (slots[p].Count > 0) continue;
            if (expanded && p == parameters.Length - 1) continue; // empty params array is fine
            if (!parameters[p].HasDefaultValue) return false;
        }

        if (!expanded)
        {
            for (var p = 0; p < parameters.Length; p++)
                if (slots[p].Count > 1) return false;
        }

        resolved = new ResolvedOverload
        {
            Method = method,
            Parameters = parameters,
            Expanded = expanded,
            ParameterArguments = slots.Select(s => s.ToArray()).ToArray(),
            ParamsElementType = expanded ? parameters[^1].ParameterType.GetElementType() : null,
        };
        parameterTypesPerArgument = perArgument;
        return true;
    }

    /// <summary>Negative when <paramref name="left"/> is the better candidate.</summary>
    private static int Compare(
        IReadOnlyList<ArgumentInfo> arguments,
        (ResolvedOverload Resolved, Type?[] ParameterTypes) left,
        (ResolvedOverload Resolved, Type?[] ParameterTypes) right,
        GenericInference.LambdaReturnProbe? probe)
    {
        var leftBetter = 0;
        var rightBetter = 0;

        for (var i = 0; i < arguments.Count; i++)
        {
            var lp = left.ParameterTypes[i];
            var rp = right.ParameterTypes[i];
            if (lp is null || rp is null || lp == rp) continue;

            var comparison = arguments[i].IsUnboundLambda
                ? CompareDelegateTargets(i, lp, rp, probe)
                : Conversions.CompareConversionTargets(arguments[i].Type, lp, rp);

            if (comparison < 0) leftBetter++;
            else if (comparison > 0) rightBetter++;
        }

        if (leftBetter > 0 && rightBetter == 0) return -1;
        if (rightBetter > 0 && leftBetter == 0) return 1;
        if (leftBetter != rightBetter) return rightBetter - leftBetter;

        // Tie-breaks, in the order C# applies them: normal form beats expanded form, a
        // non-generic method beats an inferred one, then fewer declared parameters wins.
        if (left.Resolved.Expanded != right.Resolved.Expanded)
            return left.Resolved.Expanded ? 1 : -1;

        if (left.Resolved.FromGenericDefinition != right.Resolved.FromGenericDefinition)
            return left.Resolved.FromGenericDefinition ? 1 : -1;

        var parameterDelta = left.Resolved.Parameters.Length - right.Resolved.Parameters.Length;
        if (parameterDelta != 0) return parameterDelta;

        return 0;
    }

    /// <summary>
    /// Ranks two delegate parameter types for the same lambda argument. Where the parameter
    /// lists agree, the deciding factor is which return type the lambda's own result fits best —
    /// that is what separates <c>Sum(Func&lt;T,int&gt;)</c> from <c>Sum(Func&lt;T,double&gt;)</c>
    /// for a lambda that produces an <c>int</c>.
    /// </summary>
    private static int CompareDelegateTargets(
        int argumentIndex,
        Type left,
        Type right,
        GenericInference.LambdaReturnProbe? probe)
    {
        if (probe is null) return 0;

        var leftInvoke = Conversions.GetInvokeMethod(left);
        var rightInvoke = Conversions.GetInvokeMethod(right);
        if (leftInvoke is null || rightInvoke is null) return 0;

        var leftParameters = leftInvoke.GetParameters();
        var rightParameters = rightInvoke.GetParameters();
        if (leftParameters.Length != rightParameters.Length) return 0;

        for (var i = 0; i < leftParameters.Length; i++)
            if (leftParameters[i].ParameterType != rightParameters[i].ParameterType)
                return 0;

        var natural = probe(argumentIndex, leftParameters.Select(p => p.ParameterType).ToArray());
        if (natural is null) return 0;

        return Conversions.CompareConversionTargets(natural, leftInvoke.ReturnType, rightInvoke.ReturnType);
    }
}
