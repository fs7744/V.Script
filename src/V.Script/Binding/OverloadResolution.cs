using System.Reflection;

namespace V.Script.Binding;

/// <summary>An argument as seen by overload resolution.</summary>
public readonly record struct ArgumentInfo(Type Type, string? Name);

/// <summary>
/// The chosen overload together with the mapping the binder needs to materialise the
/// final argument list (defaults filled in, params collected into an array).
/// </summary>
public sealed class ResolvedOverload
{
    public required MethodBase Method { get; init; }

    public required ParameterInfo[] Parameters { get; init; }

    public required bool Expanded { get; init; }

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
    public static OverloadResult Resolve(IReadOnlyList<MethodBase> candidates, IReadOnlyList<ArgumentInfo> arguments)
    {
        var applicable = new List<(ResolvedOverload Resolved, Type?[] ParameterTypes)>();
        var skippedGeneric = false;

        foreach (var candidate in candidates)
        {
            if (candidate.IsGenericMethodDefinition)
            {
                // Type inference is not implemented; remember this so the caller can report
                // that specifically instead of a generic "no matching overload".
                skippedGeneric = true;
                continue;
            }

            var parameters = candidate.GetParameters();
            if (parameters.Any(p => p.ParameterType.IsByRef)) continue;

            if (TryBuild(candidate, parameters, arguments, expanded: false, out var normal, out var normalTypes))
                applicable.Add((normal!, normalTypes!));
            else if (TryBuild(candidate, parameters, arguments, expanded: true, out var expandedForm, out var expandedTypes))
                applicable.Add((expandedForm!, expandedTypes!));
        }

        if (applicable.Count == 0)
            return new OverloadResult(OverloadOutcome.NoneApplicable, null, candidates, skippedGeneric);

        if (applicable.Count == 1)
            return new OverloadResult(OverloadOutcome.Resolved, applicable[0].Resolved, candidates);

        var best = 0;
        for (var i = 1; i < applicable.Count; i++)
        {
            var comparison = Compare(arguments, applicable[i], applicable[best]);
            if (comparison < 0) best = i;
        }

        // Verify the winner beats every other candidate outright, otherwise it is ambiguous.
        for (var i = 0; i < applicable.Count; i++)
        {
            if (i == best) continue;
            if (Compare(arguments, applicable[best], applicable[i]) >= 0)
                return new OverloadResult(OverloadOutcome.Ambiguous, null, candidates);
        }

        return new OverloadResult(OverloadOutcome.Resolved, applicable[best].Resolved, candidates);
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

            if (!Conversions.Classify(argument.Type, parameterType).IsImplicit)
                return false;

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
        (ResolvedOverload Resolved, Type?[] ParameterTypes) right)
    {
        var leftBetter = 0;
        var rightBetter = 0;

        for (var i = 0; i < arguments.Count; i++)
        {
            var lp = left.ParameterTypes[i];
            var rp = right.ParameterTypes[i];
            if (lp is null || rp is null || lp == rp) continue;

            var comparison = Conversions.CompareConversionTargets(arguments[i].Type, lp, rp);
            if (comparison < 0) leftBetter++;
            else if (comparison > 0) rightBetter++;
        }

        if (leftBetter > 0 && rightBetter == 0) return -1;
        if (rightBetter > 0 && leftBetter == 0) return 1;
        if (leftBetter != rightBetter) return rightBetter - leftBetter;

        // Tie-breaks: normal form beats expanded form; then fewer declared parameters wins.
        if (left.Resolved.Expanded != right.Resolved.Expanded)
            return left.Resolved.Expanded ? 1 : -1;

        var parameterDelta = left.Resolved.Parameters.Length - right.Resolved.Parameters.Length;
        if (parameterDelta != 0) return parameterDelta;

        return 0;
    }
}
