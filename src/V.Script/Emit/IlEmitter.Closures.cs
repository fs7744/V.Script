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
    private static readonly ConstructorInfo ArrayClosureConstructor =
        typeof(ArrayClosure).GetConstructor([typeof(ScriptHost), typeof(ScriptClosure), typeof(int)])!;

    private static readonly MethodInfo ClosureParentGetter =
        typeof(ScriptClosure).GetProperty(nameof(ScriptClosure.Parent))!.GetMethod!;

    private static readonly MethodInfo ClosureValuesGetter =
        typeof(ArrayClosure).GetProperty(nameof(ArrayClosure.Values))!.GetMethod!;

    /// <summary>The typed layouts, indexed by how many slots they hold.</summary>
    private static readonly Type?[] TypedClosureDefinitions =
    [
        null,
        typeof(ScriptClosure<>),
        typeof(ScriptClosure<,>),
        typeof(ScriptClosure<,,>),
        typeof(ScriptClosure<,,,>),
    ];

    /// <summary>
    /// The concrete closure class for a scope: a typed layout when the slot types allow one,
    /// otherwise the boxing fallback.
    /// </summary>
    /// <remarks>
    /// Cached on the scope because emission asks for it once per captured read and write, and
    /// <see cref="Type.MakeGenericType"/> is not free.
    /// </remarks>
    private static Type ClosureTypeFor(ClosureScope scope) => scope.RuntimeType ??= BuildClosureType(scope);

    private static Type BuildClosureType(ClosureScope scope)
    {
        var slots = scope.Slots;
        if (slots.Count is 0 or > ScriptClosure.MaxTypedSlots) return typeof(ArrayClosure);

        var arguments = new Type[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            // Anything that cannot be a generic argument cannot be boxed either, so the fallback
            // would not work for it either. Nothing in the supported language subset reaches
            // here; the check is so that a future addition fails the old way rather than a new one.
            if (!CanBeGenericArgument(slots[i].Type)) return typeof(ArrayClosure);
            arguments[i] = slots[i].Type;
        }

        return TypedClosureDefinitions[slots.Count]!.MakeGenericType(arguments);
    }

    private static bool CanBeGenericArgument(Type type) =>
        !type.IsByRef && !type.IsByRefLike && !type.IsPointer && type != typeof(void);

    private static FieldInfo SlotField(Type closureType, int slot) =>
        closureType.GetField($"Slot{slot}")!;

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
        var type = ClosureTypeFor(scope);

        // The local is declared with the concrete type, so every access from this method reaches
        // its slots without a cast. Only a lambda, which receives the base type as argument 0,
        // has to prove what it is holding.
        var local = _il.DeclareLocal(type);

        EmitHost();
        EmitParentClosure(scope);

        if (type == typeof(ArrayClosure))
        {
            EmitLdcI4(scope.Slots.Count);
            _il.Emit(OpCodes.Newobj, ArrayClosureConstructor);
        }
        else
        {
            _il.Emit(OpCodes.Newobj,
                type.GetConstructor([typeof(ScriptHost), typeof(ScriptClosure)])!);
        }

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

    /// <summary>
    /// Pushes the closure holding <paramref name="scope"/>'s slots, statically typed as its
    /// concrete class so that a field access can follow.
    /// </summary>
    private void EmitTypedClosureInstance(ClosureScope scope, Type closureType)
    {
        if (_closures.TryGetValue(scope, out var local))
        {
            // Declared with the concrete type when it was created, so nothing to prove.
            _il.Emit(OpCodes.Ldloc, local);
            return;
        }

        EmitClosureInstance(scope);
        _il.Emit(OpCodes.Castclass, closureType);
    }

    /// <summary>
    /// Pushes whatever a store into <paramref name="local"/>'s slot needs underneath the value:
    /// the values array and an index for the boxing fallback, or just the closure for a typed
    /// layout. Returns whether the layout is typed, which <see cref="EmitSlotStore"/> needs.
    /// </summary>
    /// <remarks>
    /// Split in two because the sites that store into a slot — a declaration, a lambda parameter,
    /// a catch variable, an assignment — each produce the value differently in between.
    /// </remarks>
    private bool EmitSlotStoreTarget(LocalSymbol local)
    {
        var scope = local.Closure!;
        var closureType = ClosureTypeFor(scope);

        if (closureType == typeof(ArrayClosure))
        {
            EmitCapturedSlotAddress(local);
            return false;
        }

        EmitTypedClosureInstance(scope, closureType);
        return true;
    }

    /// <summary>Consumes the value on the stack, completing a store begun with <see cref="EmitSlotStoreTarget"/>.</summary>
    private void EmitSlotStore(LocalSymbol local, bool typed)
    {
        if (typed)
        {
            _il.Emit(OpCodes.Stfld, SlotField(ClosureTypeFor(local.Closure!), local.ClosureSlot));
            return;
        }

        if (local.Type.IsValueType) _il.Emit(OpCodes.Box, local.Type);
        _il.Emit(OpCodes.Stelem_Ref);
    }

    /// <summary>Pushes the values array and the slot index, ready for <c>ldelem</c> or <c>stelem</c>.</summary>
    private void EmitCapturedSlotAddress(LocalSymbol local)
    {
        EmitClosureInstance(local.Closure!);
        _il.Emit(OpCodes.Castclass, typeof(ArrayClosure));
        _il.Emit(OpCodes.Callvirt, ClosureValuesGetter);
        EmitLdcI4(local.ClosureSlot);
    }

    private void EmitCapturedLoad(LocalSymbol local)
    {
        var scope = local.Closure!;
        var closureType = ClosureTypeFor(scope);

        if (closureType == typeof(ArrayClosure))
        {
            EmitCapturedSlotAddress(local);
            _il.Emit(OpCodes.Ldelem_Ref);

            // unbox.any covers both value types and reference types (where it acts as castclass).
            _il.Emit(OpCodes.Unbox_Any, local.Type);
            return;
        }

        EmitTypedClosureInstance(scope, closureType);
        _il.Emit(OpCodes.Ldfld, SlotField(closureType, local.ClosureSlot));
    }

    private void EmitCapturedStore(LocalSymbol local, BoundExpression value, bool leaveValue)
    {
        var typed = EmitSlotStoreTarget(local);
        EmitExpression(value);

        LocalBuilder? stash = null;
        if (leaveValue)
        {
            stash = _il.DeclareLocal(value.Type);
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Stloc, stash);
        }

        EmitSlotStore(local, typed);

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
