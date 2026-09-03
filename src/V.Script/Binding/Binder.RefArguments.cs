using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// <c>ref</c> / <c>out</c> arguments, and converting a method group to a delegate. Both are
/// argument-position features that overload resolution has to know about before the argument
/// itself has a final form.
/// </summary>
internal sealed partial class Binder
{
    /// <summary>
    /// Binds one argument. A <c>ref</c>/<c>out</c> argument must name a plain local: its address
    /// is what gets passed, and only an IL local has one. <c>out var x</c> becomes a placeholder
    /// because its type is whatever the chosen overload says it is.
    /// </summary>
    private BoundExpression BindArgument(ArgumentSyntax syntax)
    {
        if (syntax.RefKind == ArgumentRefKind.None) return BindExpression(syntax.Value);

        if (syntax.DeclaresVariable)
            return new BoundOutVariable(syntax.Position, ((NameExpressionSyntax)syntax.Value).Name, syntax.DeclaredType);

        if (syntax.Value is not NameExpressionSyntax name || !_scope.TryLookup(name.Name, out var local))
        {
            return Fail(syntax.Position, ErrorCode.NotAssignable,
                "ref / out 实参必须是一个局部变量。请先把值读进变量再传。");
        }

        if (local.IsCaptured || local.FunctionDepth < _functionDepth)
        {
            return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                $"'{local.Name}' 被 lambda 捕获，存在闭包里没有地址，不能作为 ref / out 实参。" +
                "请改用一个未被捕获的临时变量。");
        }

        if (local.IsLambdaParameter)
        {
            return Fail(syntax.Position, ErrorCode.ConstructNotSupported,
                $"lambda 参数 '{local.Name}' 不能作为 ref / out 实参。");
        }

        return new BoundLocalAddress(syntax.Position, local);
    }

    /// <summary>
    /// Turns every <see cref="BoundOutVariable"/> into a real local now that the overload — and
    /// therefore the parameter type — is known.
    /// </summary>
    private BoundExpression[] MaterialiseOutVariables(ResolvedOverload overload, IReadOnlyList<BoundExpression> bound)
    {
        var result = bound.ToArray();

        for (var p = 0; p < overload.Parameters.Length; p++)
        {
            var parameterType = overload.Parameters[p].ParameterType;
            if (!parameterType.IsByRef) continue;

            foreach (var index in overload.ParameterArguments[p])
            {
                if (result[index] is not BoundOutVariable declaration) continue;

                var type = declaration.DeclaredType is null
                    ? parameterType.GetElementType()!
                    : ResolveType(declaration.DeclaredType);

                if (type is null)
                {
                    result[index] = new BoundErrorExpression(declaration.Position);
                    continue;
                }

                var local = new LocalSymbol(declaration.Name, type);
                result[index] = DeclareLocal(local, declaration.Position)
                    ? new BoundLocalAddress(declaration.Position, local)
                    : new BoundErrorExpression(declaration.Position);
            }
        }

        return result;
    }

    /// <summary>
    /// Presents an argument to overload resolution. A ref/out argument is described by the type
    /// it refers to rather than by the byref type itself, which is what the parameter's element
    /// type is compared against.
    /// </summary>
    private static ArgumentInfo Describe(BoundExpression argument, ArgumentSyntax syntax) => argument switch
    {
        BoundUnboundLambda unbound =>
            new ArgumentInfo(Conversions.LambdaType, syntax.Name, unbound.Syntax.Parameters.Count),

        BoundLocalAddress address =>
            new ArgumentInfo(address.Local.Type, syntax.Name, -1, syntax.RefKind),

        _ => new ArgumentInfo(argument.Type, syntax.Name, -1, syntax.RefKind),
    };

    // ============================================================ method groups

    /// <summary>
    /// A name that resolves to methods but is not being called is a method group. It has no type
    /// until something converts it to a delegate, exactly like a lambda.
    /// </summary>
    private BoundExpression? TryBindMethodGroup(SourcePosition position, string name)
    {
        if (_globals is null || _globalsLocal is null) return null;

        var methods = _globals.Type
            .GetMethods(InstanceFlags)
            .Where(m => m.Name == name && !m.IsSpecialName)
            .ToArray();

        if (methods.Length == 0) return null;

        return new BoundMethodGroup(position, name, MakeLocalAccess(position, _globalsLocal), methods);
    }

    private BoundExpression? TryBindMethodGroup(SourcePosition position, BoundExpression? receiver, Type type, string name)
    {
        var flags = receiver is null ? StaticFlags : InstanceFlags;

        var methods = type
            .GetMethods(flags)
            .Where(m => m.Name == name && !m.IsSpecialName)
            .ToArray();

        return methods.Length == 0 ? null : new BoundMethodGroup(position, name, receiver, methods);
    }

    /// <summary>
    /// Picks the overload whose signature matches <paramref name="delegateType"/> exactly, which
    /// is the rule C# uses for a method group conversion.
    /// </summary>
    private BoundExpression ConvertMethodGroup(BoundMethodGroup group, Type delegateType, SourcePosition position)
    {
        if (Conversions.GetInvokeMethod(delegateType) is not { } invoke)
        {
            return Fail(position, ErrorCode.CannotConvert,
                $"方法组只能转换为委托类型，{TypeResolver.Display(delegateType)} 不是委托。");
        }

        var wanted = MemberCache.ParametersOf(invoke).Select(p => p.ParameterType).ToArray();
        var match = FindMatchingOverload(group.Methods, wanted, invoke.ReturnType);

        if (match is null)
        {
            return Fail(position, ErrorCode.NoMatchingOverload,
                $"方法组 '{group.Name}' 中没有与 {TypeResolver.Display(delegateType)} 签名一致的重载。");
        }

        if (match.IsStatic != (group.Receiver is null) && !match.IsStatic && group.Receiver is null)
        {
            return Fail(position, ErrorCode.MemberIsNotStatic,
                $"'{group.Name}' 是实例方法，需要一个实例。");
        }

        return new BoundMethodGroupConversion(
            position, delegateType, match.IsStatic ? null : group.Receiver, match);
    }

    private static MethodInfo? FindMatchingOverload(
        IReadOnlyList<MethodInfo> methods,
        Type[] parameterTypes,
        Type? returnType)
    {
        MethodInfo? inferred = null;

        foreach (var method in methods)
        {
            var candidate = method;

            if (candidate.IsGenericMethodDefinition)
            {
                candidate = TryInferFromSignature(candidate, parameterTypes);
                if (candidate is null) continue;
            }

            var parameters = MemberCache.ParametersOf(candidate);
            if (parameters.Length != parameterTypes.Length) continue;
            if (parameters.Where((p, i) => p.ParameterType != parameterTypes[i]).Any()) continue;

            if (returnType is not null && candidate.ReturnType != returnType)
            {
                // An exact return type is preferred, but an implicit one still converts.
                if (!Conversions.HasImplicit(candidate.ReturnType, returnType)) continue;
                inferred ??= candidate;
                continue;
            }

            return candidate;
        }

        return inferred;
    }

    /// <summary>Infers a generic method's type arguments from the delegate's parameter types.</summary>
    private static MethodInfo? TryInferFromSignature(MethodInfo definition, Type[] parameterTypes)
    {
        if (MemberCache.ParametersOf(definition).Length != parameterTypes.Length) return null;

        var infos = parameterTypes.Select(t => new ArgumentInfo(t, null)).ToArray();
        var map = Enumerable.Range(0, parameterTypes.Length).ToArray();

        return GenericInference.TryInfer(definition, infos, map, probe: null);
    }

    /// <summary>
    /// The return type a method group would have with these parameter types. Generic inference
    /// asks for it the same way it asks a lambda, which is what makes <c>xs.Select(Foo)</c> work.
    /// </summary>
    private static Type? ProbeMethodGroupReturn(BoundMethodGroup group, Type[] parameterTypes)
    {
        var match = FindMatchingOverload(group.Methods, parameterTypes, returnType: null);
        return match?.ReturnType == typeof(void) ? null : match?.ReturnType;
    }
}
