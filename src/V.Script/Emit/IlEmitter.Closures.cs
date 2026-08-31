using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

/// <summary>
/// Closure and lambda emission. Captured variables move out of IL locals into a
/// <see cref="ScriptClosure"/> so that the enclosing method and the lambda read and write the
/// same storage — capture by reference, as in C#.
/// </summary>
internal sealed partial class IlEmitter
{
    private static readonly ConstructorInfo ClosureConstructor =
        typeof(ScriptClosure).GetConstructor([typeof(ScriptHost), typeof(ScriptClosure), typeof(int)])!;

    private static readonly MethodInfo ClosureParentGetter =
        typeof(ScriptClosure).GetProperty(nameof(ScriptClosure.Parent))!.GetMethod!;

    private static readonly MethodInfo ClosureValuesGetter =
        typeof(ScriptClosure).GetProperty(nameof(ScriptClosure.Values))!.GetMethod!;

    private static readonly MethodInfo GetLambdaMethod =
        typeof(ScriptHost).GetMethod(nameof(ScriptHost.GetLambda))!;

    private static readonly MethodInfo BindLambdaMethod =
        typeof(ScriptHost).GetMethod(nameof(ScriptHost.BindLambda))!;

    /// <summary>
    /// Instantiates the closure for a scope on entry. Inside a loop body this runs once per
    /// iteration, which is what makes a captured iteration variable independent per pass.
    /// </summary>
    private void EmitCreateClosure(ClosureScope scope)
    {
        var local = _il.DeclareLocal(typeof(ScriptClosure));

        EmitHost();
        EmitParentClosure(scope);
        EmitLdcI4(scope.Slots.Count);

        _il.Emit(OpCodes.Newobj, ClosureConstructor);
        _il.Emit(OpCodes.Stloc, local);

        _closures[scope] = local;
    }

    private void EmitParentClosure(ClosureScope scope)
    {
        var parent = scope.MaterializedParent;

        if (parent is not null && _closures.TryGetValue(parent, out var known))
        {
            _il.Emit(OpCodes.Ldloc, known);
            return;
        }

        // The parent lives in the enclosing method, which handed it to us as argument 0.
        if (_lambda is not null && _incomingClosure is not null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            return;
        }

        _il.Emit(OpCodes.Ldnull);
    }

    /// <summary>Pushes the live <see cref="ScriptClosure"/> instance for <paramref name="target"/>.</summary>
    private void EmitClosureInstance(ClosureScope target)
    {
        if (_closures.TryGetValue(target, out var local))
        {
            _il.Emit(OpCodes.Ldloc, local);
            return;
        }

        var hops = _incomingClosure?.HopsTo(target) ?? -1;
        if (hops < 0)
        {
            throw new InvalidOperationException(
                "闭包作用域不可达；绑定器与发射器对捕获层级的判断不一致。");
        }

        _il.Emit(OpCodes.Ldarg_0);
        for (var i = 0; i < hops; i++)
            _il.Emit(OpCodes.Callvirt, ClosureParentGetter);
    }

    /// <summary>Pushes the values array and the slot index, ready for <c>ldelem</c> or <c>stelem</c>.</summary>
    private void EmitCapturedSlotAddress(LocalSymbol local)
    {
        EmitClosureInstance(local.Closure!);
        _il.Emit(OpCodes.Callvirt, ClosureValuesGetter);
        EmitLdcI4(local.ClosureSlot);
    }

    private void EmitCapturedLoad(LocalSymbol local)
    {
        EmitCapturedSlotAddress(local);
        _il.Emit(OpCodes.Ldelem_Ref);

        // unbox.any covers both value types and reference types (where it acts as castclass).
        _il.Emit(OpCodes.Unbox_Any, local.Type);
    }

    private void EmitCapturedStore(LocalSymbol local, BoundExpression value, bool leaveValue)
    {
        EmitCapturedSlotAddress(local);
        EmitExpression(value);

        LocalBuilder? stash = null;
        if (leaveValue)
        {
            stash = _il.DeclareLocal(value.Type);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Stloc, stash);
        }

        if (local.Type.IsValueType) _il.Emit(OpCodes.Box, local.Type);
        _il.Emit(OpCodes.Stelem_Ref);

        if (stash is not null) _il.Emit(OpCodes.Ldloc, stash);
    }

    /// <summary>
    /// Materialises a lambda. Generated code cannot take the address of a
    /// <see cref="DynamicMethod"/>, so the delegate is built by the host: either fetched from a
    /// one-off cache when nothing is captured, or bound to the live closure when something is.
    /// </summary>
    private void EmitLambda(BoundLambda lambda)
    {
        var incoming = NearestMaterialized(lambda.EnclosingClosure);

        EmitHost();
        EmitLdcI4(lambda.Index);

        if (incoming is null)
        {
            _il.Emit(OpCodes.Callvirt, GetLambdaMethod);
        }
        else
        {
            EmitClosureInstance(incoming);
            _il.Emit(OpCodes.Callvirt, BindLambdaMethod);
        }

        _il.Emit(OpCodes.Castclass, lambda.Type);
    }

    private void EmitDelegateInvoke(BoundDelegateInvoke invocation)
    {
        EmitExpression(invocation.Target);
        foreach (var argument in invocation.Arguments) EmitExpression(argument);
        _il.Emit(OpCodes.Callvirt, invocation.Invoke);
    }
}
