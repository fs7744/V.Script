using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using V.Script.Binding;

namespace V.Script.Emit;

internal sealed partial class IlEmitter
{
    private void EmitExpression(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLiteral literal: EmitLiteral(literal); break;
            case BoundNullLiteral: _il.Emit(OpCodes.Ldnull); break;
            case BoundDefault defaultValue: EmitDefaultValue(defaultValue.Type); break;
            case BoundLocalAccess local: EmitLocalAccess(local.Local); break;
            case BoundParameterAccess parameter: EmitLdarg(parameter.Index); break;
            case BoundConversion conversion: EmitConversion(conversion); break;
            case BoundBinary binary: EmitBinary(binary); break;
            case BoundUnary unary: EmitUnary(unary); break;
            case BoundLogical logical: EmitLogical(logical); break;
            case BoundConditional conditional: EmitConditional(conditional); break;
            case BoundConditionalAccess access: EmitConditionalAccess(access); break;
            case BoundSequence sequence: EmitSequence(sequence); break;
            case BoundFieldAccess field: EmitFieldAccess(field); break;
            case BoundPropertyAccess property: EmitPropertyAccess(property); break;
            case BoundCall call: EmitCall(call); break;
            case BoundIndexerAccess indexer: EmitIndexerAccess(indexer); break;
            case BoundArrayAccess array: EmitArrayAccess(array); break;
            case BoundObjectCreation creation: EmitObjectCreation(creation); break;
            case BoundArrayCreation creation: EmitArrayCreation(creation); break;
            case BoundNewArray creation: EmitNewArray(creation); break;
            case BoundThrowExpression thrown: EmitThrowExpression(thrown); break;
            case BoundAwait await: EmitAwait(await); break;
            case BoundIsType isType: EmitIsType(isType); break;
            case BoundAsType asType: EmitAsType(asType); break;
            case BoundTypeofExpression typeofExpression: EmitTypeof(typeofExpression); break;
            case BoundAssignment assignment: EmitAssignment(assignment, leaveValue: true); break;
            case BoundLambda lambda: EmitLambda(lambda); break;
            case BoundDelegateInvoke invocation: EmitDelegateInvoke(invocation); break;

            case BoundErrorExpression:
            case BoundTypeReference:
            case BoundUnboundLambda:
            case BoundUnboundCollection:
            case BoundDefaultLiteral:
                throw new InvalidOperationException("绑定失败的表达式不应到达发射阶段。");

            default:
                throw new InvalidOperationException($"未处理的表达式节点 {expression.GetType().Name}。");
        }
    }

    /// <summary>
    /// Reads a variable. Where it lives was decided by the binder: an IL local, an argument of
    /// the lambda method, or a slot in a closure.
    /// </summary>
    private void EmitLocalAccess(LocalSymbol local)
    {
        if (local.IsCaptured)
        {
            EmitCapturedLoad(local);
            return;
        }

        if (local.IsLambdaParameter)
        {
            EmitLdarg(local.LambdaArgIndex);
            return;
        }

        _il.Emit(OpCodes.Ldloc, _locals[local]);
    }

    // ============================================================ constants

    private void EmitLiteral(BoundLiteral literal)
    {
        var type = literal.Type;
        var value = literal.Value;

        if (value is null)
        {
            EmitDefaultValue(type);
            return;
        }

        var baseType = Conversions.Unlift(type);

        switch (value)
        {
            case bool b: EmitLdcI4(b ? 1 : 0); break;
            case char c: EmitLdcI4(c); break;
            case sbyte v: EmitLdcI4(v); break;
            case byte v: EmitLdcI4(v); break;
            case short v: EmitLdcI4(v); break;
            case ushort v: EmitLdcI4(v); break;
            case int v: EmitLdcI4(v); break;
            case uint v: EmitLdcI4(unchecked((int)v)); break;
            case long v: _il.Emit(OpCodes.Ldc_I8, v); break;
            case ulong v: _il.Emit(OpCodes.Ldc_I8, unchecked((long)v)); break;
            case float v: _il.Emit(OpCodes.Ldc_R4, v); break;
            case double v: _il.Emit(OpCodes.Ldc_R8, v); break;
            case string s: _il.Emit(OpCodes.Ldstr, s); break;
            case decimal d: EmitDecimal(d); break;
            case Enum e: EmitEnumConstant(e, baseType); break;
            default:
                throw new InvalidOperationException($"无法发射常量 {value.GetType().Name}。");
        }

        if (Conversions.IsNullableValueType(type)) WrapInNullable(type);
    }

    private void EmitEnumConstant(Enum value, Type enumType)
    {
        var underlying = Enum.GetUnderlyingType(enumType);
        var raw = System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);

        if (underlying == typeof(long) || underlying == typeof(ulong))
            _il.Emit(OpCodes.Ldc_I8, System.Convert.ToInt64(raw, CultureInfo.InvariantCulture));
        else
            EmitLdcI4(System.Convert.ToInt32(raw, CultureInfo.InvariantCulture));
    }

    /// <summary>decimal has no IL literal form; reconstruct it from its bit pattern.</summary>
    private void EmitDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (byte)((bits[3] >> 16) & 0x7F);
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;

        EmitLdcI4(bits[0]);
        EmitLdcI4(bits[1]);
        EmitLdcI4(bits[2]);
        EmitLdcI4(negative ? 1 : 0);
        EmitLdcI4(scale);

        var constructor = typeof(decimal).GetConstructor(
            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(byte)])!;
        _il.Emit(OpCodes.Newobj, constructor);
    }

    private void EmitDefaultValue(Type type)
    {
        if (!type.IsValueType)
        {
            _il.Emit(OpCodes.Ldnull);
            return;
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
            case TypeCode.Char:
            case TypeCode.SByte:
            case TypeCode.Byte:
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Int32:
            case TypeCode.UInt32:
                EmitLdcI4(0);
                return;
            case TypeCode.Int64:
            case TypeCode.UInt64:
                _il.Emit(OpCodes.Ldc_I8, 0L);
                return;
            case TypeCode.Single:
                _il.Emit(OpCodes.Ldc_R4, 0f);
                return;
            case TypeCode.Double:
                _il.Emit(OpCodes.Ldc_R8, 0d);
                return;
        }

        if (type.IsEnum)
        {
            EmitDefaultValue(Enum.GetUnderlyingType(type));
            return;
        }

        var temp = _il.DeclareLocal(type);
        _il.Emit(OpCodes.Ldloca, temp);
        _il.Emit(OpCodes.Initobj, type);
        _il.Emit(OpCodes.Ldloc, temp);
    }

    // ============================================================ conversions

    private void EmitConversion(BoundConversion conversion)
    {
        var from = conversion.Operand.Type;
        var to = conversion.Type;

        if (conversion.Conversion.Method is { } userDefined)
        {
            EmitExpression(conversion.Operand);
            _il.Emit(OpCodes.Call, userDefined);
            return;
        }

        if (Conversions.IsNullableValueType(from) || Conversions.IsNullableValueType(to))
        {
            EmitNullableConversion(conversion.Operand, from, to);
            return;
        }

        EmitExpression(conversion.Operand);
        EmitValueConversion(from, to);
    }

    /// <summary>Conversions between plain (non-nullable) types once the value is on the stack.</summary>
    private void EmitValueConversion(Type from, Type to)
    {
        if (from == to) return;

        if (to == typeof(object) || to.IsInterface || !to.IsValueType)
        {
            if (from.IsValueType) _il.Emit(OpCodes.Box, from);
            else if (!to.IsAssignableFrom(from)) _il.Emit(OpCodes.Castclass, to);
            return;
        }

        if (!from.IsValueType)
        {
            _il.Emit(OpCodes.Unbox_Any, to);
            return;
        }

        var fromBase = from.IsEnum ? Enum.GetUnderlyingType(from) : from;
        var toBase = to.IsEnum ? Enum.GetUnderlyingType(to) : to;
        if (fromBase == toBase) return;

        if (toBase == typeof(decimal))
        {
            _il.Emit(OpCodes.Call, FindDecimalConversion(fromBase, typeof(decimal)));
            return;
        }

        if (fromBase == typeof(decimal))
        {
            _il.Emit(OpCodes.Call, FindDecimalConversion(typeof(decimal), toBase));
            return;
        }

        EmitNumericConversion(fromBase, toBase);
    }

    private static MethodInfo FindDecimalConversion(Type from, Type to)
    {
        foreach (var method in typeof(decimal).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name is not ("op_Implicit" or "op_Explicit")) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == from && method.ReturnType == to)
                return method;
        }

        throw new InvalidOperationException(
            $"decimal 与 {to.Name} 之间没有转换运算符。");
    }

    private void EmitNumericConversion(Type from, Type to)
    {
        var fromUnsigned = IsUnsigned(from);

        switch (Type.GetTypeCode(to))
        {
            case TypeCode.SByte: _il.Emit(OpCodes.Conv_I1); break;
            case TypeCode.Byte: _il.Emit(OpCodes.Conv_U1); break;
            case TypeCode.Int16: _il.Emit(OpCodes.Conv_I2); break;
            case TypeCode.UInt16:
            case TypeCode.Char: _il.Emit(OpCodes.Conv_U2); break;
            case TypeCode.Int32: _il.Emit(OpCodes.Conv_I4); break;
            case TypeCode.UInt32: _il.Emit(OpCodes.Conv_U4); break;
            case TypeCode.Int64:
                _il.Emit(fromUnsigned ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
                break;
            case TypeCode.UInt64:
                _il.Emit(fromUnsigned ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
                break;
            case TypeCode.Single:
                if (fromUnsigned) _il.Emit(OpCodes.Conv_R_Un);
                _il.Emit(OpCodes.Conv_R4);
                break;
            case TypeCode.Double:
                if (fromUnsigned) _il.Emit(OpCodes.Conv_R_Un);
                _il.Emit(OpCodes.Conv_R8);
                break;
            default:
                throw new InvalidOperationException($"无法从 {from.Name} 转换为 {to.Name}。");
        }
    }

    private static bool IsUnsigned(Type type) =>
        type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) ||
        type == typeof(ulong) || type == typeof(char);

    /// <summary>
    /// Nullable conversions. Widening wraps the value; narrowing unwraps through
    /// <c>Value</c>; a nullable-to-nullable change of base type lifts the inner conversion.
    /// </summary>
    private void EmitNullableConversion(BoundExpression operand, Type from, Type to)
    {
        var fromNullable = Conversions.IsNullableValueType(from);
        var toNullable = Conversions.IsNullableValueType(to);
        var fromBase = Conversions.Unlift(from);
        var toBase = Conversions.Unlift(to);

        // T -> T?  (possibly with a numeric change first)
        if (!fromNullable && toNullable)
        {
            EmitExpression(operand);
            if (fromBase != toBase) EmitValueConversion(fromBase, toBase);
            WrapInNullable(to);
            return;
        }

        // T? -> object
        if (fromNullable && !toNullable && !to.IsValueType)
        {
            EmitExpression(operand);
            _il.Emit(OpCodes.Box, from);
            if (to != typeof(object) && !to.IsAssignableFrom(from)) _il.Emit(OpCodes.Castclass, to);
            return;
        }

        // T? -> T  (throws when empty, matching C# semantics for an explicit cast)
        if (fromNullable && !toNullable)
        {
            EmitAddressOf(operand);
            _il.Emit(OpCodes.Call, from.GetProperty("Value")!.GetMethod!);
            if (fromBase != toBase) EmitValueConversion(fromBase, toBase);
            return;
        }

        // S? -> T? : lifted conversion, empty in gives empty out
        var source = _il.DeclareLocal(from);
        var result = _il.DeclareLocal(to);
        var nullCase = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitExpression(operand);
        _il.Emit(OpCodes.Stloc, source);

        _il.Emit(OpCodes.Ldloca, source);
        _il.Emit(OpCodes.Call, from.GetProperty("HasValue")!.GetMethod!);
        _il.Emit(OpCodes.Brfalse, nullCase);

        _il.Emit(OpCodes.Ldloca, source);
        _il.Emit(OpCodes.Call, from.GetMethod("GetValueOrDefault", Type.EmptyTypes)!);
        if (fromBase != toBase) EmitValueConversion(fromBase, toBase);
        WrapInNullable(to);
        _il.Emit(OpCodes.Stloc, result);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(nullCase);
        _il.Emit(OpCodes.Ldloca, result);
        _il.Emit(OpCodes.Initobj, to);

        _il.MarkLabel(end);
        _il.Emit(OpCodes.Ldloc, result);
    }

    private void WrapInNullable(Type nullableType)
    {
        var constructor = nullableType.GetConstructor([Conversions.Unlift(nullableType)])!;
        _il.Emit(OpCodes.Newobj, constructor);
    }

    // ============================================================ operators

    private void EmitBinary(BoundBinary binary)
    {
        if (binary.IsLifted)
        {
            EmitLiftedBinary(binary);
            return;
        }

        EmitExpression(binary.Left);
        EmitExpression(binary.Right);
        EmitBinaryCore(binary.Kind, binary.Left.Type, binary.Method);
    }

    /// <summary>Emits the operation itself, with both operands already on the stack.</summary>
    private void EmitBinaryCore(BoundBinaryKind kind, Type operandType, MethodInfo? method)
    {
        if (method is not null)
        {
            _il.Emit(OpCodes.Call, method);
            return;
        }

        var unsigned = IsUnsigned(operandType.IsEnum ? Enum.GetUnderlyingType(operandType) : operandType);
        var floating = operandType == typeof(float) || operandType == typeof(double);

        switch (kind)
        {
            case BoundBinaryKind.Add: _il.Emit(OpCodes.Add); break;
            case BoundBinaryKind.Subtract: _il.Emit(OpCodes.Sub); break;
            case BoundBinaryKind.Multiply: _il.Emit(OpCodes.Mul); break;
            case BoundBinaryKind.Divide: _il.Emit(unsigned ? OpCodes.Div_Un : OpCodes.Div); break;
            case BoundBinaryKind.Modulo: _il.Emit(unsigned ? OpCodes.Rem_Un : OpCodes.Rem); break;
            case BoundBinaryKind.BitAnd: _il.Emit(OpCodes.And); break;
            case BoundBinaryKind.BitOr: _il.Emit(OpCodes.Or); break;
            case BoundBinaryKind.BitXor: _il.Emit(OpCodes.Xor); break;
            case BoundBinaryKind.LeftShift: _il.Emit(OpCodes.Shl); break;
            case BoundBinaryKind.RightShift: _il.Emit(unsigned ? OpCodes.Shr_Un : OpCodes.Shr); break;

            case BoundBinaryKind.Equal:
                _il.Emit(OpCodes.Ceq);
                break;

            case BoundBinaryKind.NotEqual:
                _il.Emit(OpCodes.Ceq);
                EmitLogicalNegate();
                break;

            case BoundBinaryKind.Less:
                _il.Emit(unsigned ? OpCodes.Clt_Un : OpCodes.Clt);
                break;

            case BoundBinaryKind.Greater:
                _il.Emit(unsigned ? OpCodes.Cgt_Un : OpCodes.Cgt);
                break;

            case BoundBinaryKind.LessEqual:
                // NaN must compare false, hence the unordered form for floating point.
                _il.Emit(unsigned || floating ? OpCodes.Cgt_Un : OpCodes.Cgt);
                EmitLogicalNegate();
                break;

            case BoundBinaryKind.GreaterEqual:
                _il.Emit(unsigned || floating ? OpCodes.Clt_Un : OpCodes.Clt);
                EmitLogicalNegate();
                break;

            default:
                throw new InvalidOperationException($"未处理的二元运算 {kind}。");
        }
    }

    private void EmitLogicalNegate()
    {
        EmitLdcI4(0);
        _il.Emit(OpCodes.Ceq);
    }

    private static bool IsComparison(BoundBinaryKind kind) => kind is
        BoundBinaryKind.Less or BoundBinaryKind.LessEqual or
        BoundBinaryKind.Greater or BoundBinaryKind.GreaterEqual;

    private static bool IsEquality(BoundBinaryKind kind) => kind is
        BoundBinaryKind.Equal or BoundBinaryKind.NotEqual;

    /// <summary>
    /// Lifted operators. Arithmetic yields an empty result when either operand is empty;
    /// relational operators yield false; equality treats two empties as equal.
    /// </summary>
    private void EmitLiftedBinary(BoundBinary binary)
    {
        var nullableType = binary.Left.Type;
        var baseType = Conversions.Unlift(nullableType);

        var left = _il.DeclareLocal(binary.Left.Type);
        var right = _il.DeclareLocal(binary.Right.Type);

        EmitExpression(binary.Left);
        _il.Emit(OpCodes.Stloc, left);
        EmitExpression(binary.Right);
        _il.Emit(OpCodes.Stloc, right);

        if (IsEquality(binary.Kind))
        {
            EmitLiftedEquality(binary, left, right, baseType);
            return;
        }

        var fallback = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitHasValue(left);
        EmitHasValue(right);
        _il.Emit(OpCodes.And);
        _il.Emit(OpCodes.Brfalse, fallback);

        EmitGetValueOrDefault(left);
        EmitGetValueOrDefault(right);
        EmitBinaryCore(binary.Kind, Conversions.Unlift(binary.Right.Type), binary.Method);

        if (IsComparison(binary.Kind))
        {
            _il.Emit(OpCodes.Br, end);
            _il.MarkLabel(fallback);
            EmitLdcI4(0);
            _il.MarkLabel(end);
            return;
        }

        var result = _il.DeclareLocal(binary.Type);
        WrapInNullable(binary.Type);
        _il.Emit(OpCodes.Stloc, result);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(fallback);
        _il.Emit(OpCodes.Ldloca, result);
        _il.Emit(OpCodes.Initobj, binary.Type);

        _il.MarkLabel(end);
        _il.Emit(OpCodes.Ldloc, result);
        _ = baseType;
    }

    private void EmitLiftedEquality(BoundBinary binary, LocalBuilder left, LocalBuilder right, Type baseType)
    {
        var bothSame = _il.DefineLabel();
        var bothNull = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitHasValue(left);
        EmitHasValue(right);
        _il.Emit(OpCodes.Beq, bothSame);

        EmitLdcI4(0); // exactly one side empty: never equal
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(bothSame);
        EmitHasValue(left);
        _il.Emit(OpCodes.Brfalse, bothNull);

        EmitGetValueOrDefault(left);
        EmitGetValueOrDefault(right);
        EmitBinaryCore(BoundBinaryKind.Equal, baseType, binary.Method);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(bothNull);
        EmitLdcI4(1); // both empty: equal

        _il.MarkLabel(end);
        if (binary.Kind == BoundBinaryKind.NotEqual) EmitLogicalNegate();
    }

    private void EmitHasValue(LocalBuilder nullable)
    {
        _il.Emit(OpCodes.Ldloca, nullable);
        _il.Emit(OpCodes.Call, nullable.LocalType.GetProperty("HasValue")!.GetMethod!);
    }

    private void EmitGetValueOrDefault(LocalBuilder nullable)
    {
        _il.Emit(OpCodes.Ldloca, nullable);
        _il.Emit(OpCodes.Call, nullable.LocalType.GetMethod("GetValueOrDefault", Type.EmptyTypes)!);
    }

    private void EmitUnary(BoundUnary unary)
    {
        if (unary.IsLifted)
        {
            EmitLiftedUnary(unary);
            return;
        }

        EmitExpression(unary.Operand);
        EmitUnaryCore(unary.Kind, unary.Method);
    }

    private void EmitUnaryCore(BoundUnaryKind kind, MethodInfo? method)
    {
        if (method is not null)
        {
            _il.Emit(OpCodes.Call, method);
            return;
        }

        switch (kind)
        {
            case BoundUnaryKind.Plus: break;
            case BoundUnaryKind.Negate: _il.Emit(OpCodes.Neg); break;
            case BoundUnaryKind.BitwiseNot: _il.Emit(OpCodes.Not); break;
            case BoundUnaryKind.LogicalNot: EmitLogicalNegate(); break;
            default: throw new InvalidOperationException($"未处理的一元运算 {kind}。");
        }
    }

    private void EmitLiftedUnary(BoundUnary unary)
    {
        var operand = _il.DeclareLocal(unary.Operand.Type);
        var result = _il.DeclareLocal(unary.Type);
        var nullCase = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitExpression(unary.Operand);
        _il.Emit(OpCodes.Stloc, operand);

        EmitHasValue(operand);
        _il.Emit(OpCodes.Brfalse, nullCase);

        EmitGetValueOrDefault(operand);
        EmitUnaryCore(unary.Kind, unary.Method);
        WrapInNullable(unary.Type);
        _il.Emit(OpCodes.Stloc, result);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(nullCase);
        _il.Emit(OpCodes.Ldloca, result);
        _il.Emit(OpCodes.Initobj, unary.Type);

        _il.MarkLabel(end);
        _il.Emit(OpCodes.Ldloc, result);
    }

    private void EmitLogical(BoundLogical logical)
    {
        var shortCircuit = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitExpression(logical.Left);
        _il.Emit(logical.IsAnd ? OpCodes.Brfalse : OpCodes.Brtrue, shortCircuit);

        EmitExpression(logical.Right);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(shortCircuit);
        EmitLdcI4(logical.IsAnd ? 0 : 1);

        _il.MarkLabel(end);
    }

    private void EmitConditional(BoundConditional conditional)
    {
        var falseCase = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitExpression(conditional.Condition);
        _il.Emit(OpCodes.Brfalse, falseCase);

        EmitExpression(conditional.WhenTrue);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(falseCase);
        EmitExpression(conditional.WhenFalse);

        _il.MarkLabel(end);
    }

    private void EmitConditionalAccess(BoundConditionalAccess access)
    {
        var temp = _locals[access.Temp];
        var nullCase = _il.DefineLabel();
        var end = _il.DefineLabel();

        EmitExpression(access.Receiver);
        _il.Emit(OpCodes.Stloc, temp);

        if (Conversions.IsNullableValueType(access.Temp.Type))
            EmitHasValue(temp);
        else
            _il.Emit(OpCodes.Ldloc, temp);

        _il.Emit(OpCodes.Brfalse, nullCase);

        EmitExpression(access.WhenNotNull);

        if (access.Type == typeof(void))
        {
            _il.Emit(OpCodes.Br, end);
            _il.MarkLabel(nullCase);
            _il.MarkLabel(end);
            return;
        }

        var result = _il.DeclareLocal(access.Type);
        _il.Emit(OpCodes.Stloc, result);
        _il.Emit(OpCodes.Br, end);

        _il.MarkLabel(nullCase);
        if (access.Type.IsValueType)
        {
            _il.Emit(OpCodes.Ldloca, result);
            _il.Emit(OpCodes.Initobj, access.Type);
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, result);
        }

        _il.MarkLabel(end);
        _il.Emit(OpCodes.Ldloc, result);
    }

    private void EmitSequence(BoundSequence sequence)
    {
        foreach (var effect in sequence.SideEffects) EmitAsStatement(effect);
        EmitExpression(sequence.Value);
    }

    // ============================================================ members

    private void EmitFieldAccess(BoundFieldAccess field)
    {
        if (field.Field.IsStatic)
        {
            _il.Emit(OpCodes.Ldsfld, field.Field);
            return;
        }

        EmitInstanceReceiver(field.Receiver!, field.Field.DeclaringType!);
        _il.Emit(OpCodes.Ldfld, field.Field);
    }

    private void EmitPropertyAccess(BoundPropertyAccess property)
    {
        var getter = property.Property.GetMethod
            ?? throw new InvalidOperationException($"属性 {property.Property.Name} 没有 get 访问器。");

        EmitMethodCall(getter, property.Receiver, []);
    }

    private void EmitCall(BoundCall call) => EmitMethodCall(call.Method, call.Receiver, call.Arguments);

    private void EmitMethodCall(MethodInfo method, BoundExpression? receiver, IReadOnlyList<BoundExpression> arguments)
    {
        var constrained = false;

        if (!method.IsStatic)
        {
            constrained = EmitInstanceReceiver(receiver!, method.DeclaringType!);
        }

        foreach (var argument in arguments) EmitExpression(argument);

        if (constrained)
        {
            _il.Emit(OpCodes.Constrained, receiver!.Type);
            _il.Emit(OpCodes.Callvirt, method);
        }
        else if (method.IsStatic || receiver!.Type.IsValueType || !method.IsVirtual || method.IsFinal)
        {
            _il.Emit(OpCodes.Call, method);
        }
        else
        {
            _il.Emit(OpCodes.Callvirt, method);
        }
    }

    /// <summary>
    /// Pushes the receiver. Value types need a managed pointer, and calling an interface
    /// method on one additionally needs a <c>constrained.</c> prefix, which this reports.
    /// </summary>
    private bool EmitInstanceReceiver(BoundExpression receiver, Type declaringType)
    {
        if (!receiver.Type.IsValueType)
        {
            EmitExpression(receiver);
            return false;
        }

        EmitAddressOf(receiver);
        return declaringType.IsInterface || declaringType == typeof(object) ||
               declaringType == typeof(ValueType) || declaringType == typeof(Enum);
    }

    private void EmitAddressOf(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLocalAccess { Local.IsCaptured: false, Local.IsLambdaParameter: false } local:
                _il.Emit(OpCodes.Ldloca, _locals[local.Local]);
                return;

            case BoundLocalAccess { Local.IsLambdaParameter: true } local:
                _il.Emit(OpCodes.Ldarga, local.Local.LambdaArgIndex);
                return;

            case BoundParameterAccess parameter:
                _il.Emit(OpCodes.Ldarga, parameter.Index);
                return;

            case BoundFieldAccess { Field.IsStatic: false } field:
                EmitInstanceReceiver(field.Receiver!, field.Field.DeclaringType!);
                _il.Emit(OpCodes.Ldflda, field.Field);
                return;

            case BoundFieldAccess { Field.IsStatic: true } field:
                _il.Emit(OpCodes.Ldsflda, field.Field);
                return;

            default:
            {
                var temp = _il.DeclareLocal(expression.Type);
                EmitExpression(expression);
                _il.Emit(OpCodes.Stloc, temp);
                _il.Emit(OpCodes.Ldloca, temp);
                return;
            }
        }
    }

    private void EmitIndexerAccess(BoundIndexerAccess indexer)
    {
        var getter = indexer.Indexer.GetMethod
            ?? throw new InvalidOperationException("索引器没有 get 访问器。");

        EmitMethodCall(getter, indexer.Receiver, indexer.Arguments);
    }

    private void EmitArrayAccess(BoundArrayAccess array)
    {
        EmitExpression(array.Array);
        EmitExpression(array.Index);
        EmitLdelem(array.Type);
    }

    private void EmitLdelem(Type elementType)
    {
        if (!elementType.IsValueType)
        {
            _il.Emit(OpCodes.Ldelem_Ref);
            return;
        }

        switch (Type.GetTypeCode(elementType))
        {
            case TypeCode.Boolean:
            case TypeCode.SByte: _il.Emit(OpCodes.Ldelem_I1); return;
            case TypeCode.Byte: _il.Emit(OpCodes.Ldelem_U1); return;
            case TypeCode.Int16: _il.Emit(OpCodes.Ldelem_I2); return;
            case TypeCode.UInt16:
            case TypeCode.Char: _il.Emit(OpCodes.Ldelem_U2); return;
            case TypeCode.Int32: _il.Emit(OpCodes.Ldelem_I4); return;
            case TypeCode.UInt32: _il.Emit(OpCodes.Ldelem_U4); return;
            case TypeCode.Int64:
            case TypeCode.UInt64: _il.Emit(OpCodes.Ldelem_I8); return;
            case TypeCode.Single: _il.Emit(OpCodes.Ldelem_R4); return;
            case TypeCode.Double: _il.Emit(OpCodes.Ldelem_R8); return;
            default: _il.Emit(OpCodes.Ldelem, elementType); return;
        }
    }

    private void EmitStelem(Type elementType)
    {
        if (!elementType.IsValueType)
        {
            _il.Emit(OpCodes.Stelem_Ref);
            return;
        }

        switch (Type.GetTypeCode(elementType))
        {
            case TypeCode.Boolean:
            case TypeCode.SByte:
            case TypeCode.Byte: _il.Emit(OpCodes.Stelem_I1); return;
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Char: _il.Emit(OpCodes.Stelem_I2); return;
            case TypeCode.Int32:
            case TypeCode.UInt32: _il.Emit(OpCodes.Stelem_I4); return;
            case TypeCode.Int64:
            case TypeCode.UInt64: _il.Emit(OpCodes.Stelem_I8); return;
            case TypeCode.Single: _il.Emit(OpCodes.Stelem_R4); return;
            case TypeCode.Double: _il.Emit(OpCodes.Stelem_R8); return;
            default: _il.Emit(OpCodes.Stelem, elementType); return;
        }
    }

    private void EmitObjectCreation(BoundObjectCreation creation)
    {
        foreach (var argument in creation.Arguments) EmitExpression(argument);
        _il.Emit(OpCodes.Newobj, creation.Constructor);
    }

    /// <summary>
    /// A throw expression leaves nothing behind. `throw` empties the evaluation stack, so the
    /// branch it sits on simply never reaches the merge point the other branch does.
    /// </summary>
    private void EmitThrowExpression(BoundThrowExpression thrown)
    {
        EmitExpression(thrown.Exception);
        _il.Emit(OpCodes.Throw);
    }

    private void EmitNewArray(BoundNewArray creation)
    {
        EmitExpression(creation.Length);
        _il.Emit(OpCodes.Newarr, creation.ElementType);
    }

    private void EmitArrayCreation(BoundArrayCreation creation)
    {
        EmitLdcI4(creation.Elements.Count);
        _il.Emit(OpCodes.Newarr, creation.ElementType);

        for (var i = 0; i < creation.Elements.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            EmitLdcI4(i);
            EmitExpression(creation.Elements[i]);
            EmitStelem(creation.ElementType);
        }
    }

    // ============================================================ async & type tests

    /// <summary>
    /// A suspension point. The JIT turns this call into the state machine, so the whole of
    /// <c>await</c> support on the emit side is one operand plus one call.
    /// </summary>
    private void EmitAwait(BoundAwait await)
    {
        EmitExpression(await.Operand);
        _il.Emit(OpCodes.Call, await.AwaitHelper);
    }

    private void EmitIsType(BoundIsType isType)
    {
        EmitExpression(isType.Operand);
        _il.Emit(OpCodes.Isinst, isType.TargetType);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Cgt_Un);
    }

    private void EmitAsType(BoundAsType asType)
    {
        EmitExpression(asType.Operand);
        _il.Emit(OpCodes.Isinst, asType.Type);
    }

    private void EmitTypeof(BoundTypeofExpression typeofExpression)
    {
        _il.Emit(OpCodes.Ldtoken, typeofExpression.TargetType);
        _il.Emit(OpCodes.Call, typeof(Type).GetMethod(
            nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!);
    }

    // ============================================================ assignment

    private void EmitAssignment(BoundAssignment assignment, bool leaveValue)
    {
        switch (assignment.Target)
        {
            case BoundLocalAccess local:
            {
                if (local.Local.IsCaptured)
                {
                    EmitCapturedStore(local.Local, assignment.Value, leaveValue);
                    return;
                }

                if (local.Local.IsLambdaParameter)
                {
                    EmitExpression(assignment.Value);
                    if (leaveValue) _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Starg_S, (byte)local.Local.LambdaArgIndex);
                    return;
                }

                EmitExpression(assignment.Value);
                if (leaveValue) _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Stloc, _locals[local.Local]);
                return;
            }

            case BoundFieldAccess field:
            {
                LocalBuilder? stash = null;
                if (!field.Field.IsStatic) EmitInstanceReceiver(field.Receiver!, field.Field.DeclaringType!);

                EmitExpression(assignment.Value);
                if (leaveValue)
                {
                    stash = _il.DeclareLocal(assignment.Value.Type);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Stloc, stash);
                }

                _il.Emit(field.Field.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, field.Field);
                if (stash is not null) _il.Emit(OpCodes.Ldloc, stash);
                return;
            }

            case BoundPropertyAccess property:
            {
                var setter = property.Property.SetMethod
                    ?? throw new InvalidOperationException($"属性 {property.Property.Name} 没有 set 访问器。");

                var constrained = false;
                if (!setter.IsStatic) constrained = EmitInstanceReceiver(property.Receiver!, setter.DeclaringType!);

                EmitExpression(assignment.Value);

                LocalBuilder? stash = null;
                if (leaveValue)
                {
                    stash = _il.DeclareLocal(assignment.Value.Type);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Stloc, stash);
                }

                if (constrained)
                {
                    _il.Emit(OpCodes.Constrained, property.Receiver!.Type);
                    _il.Emit(OpCodes.Callvirt, setter);
                }
                else if (setter.IsStatic || property.Receiver!.Type.IsValueType || !setter.IsVirtual)
                {
                    _il.Emit(OpCodes.Call, setter);
                }
                else
                {
                    _il.Emit(OpCodes.Callvirt, setter);
                }

                if (stash is not null) _il.Emit(OpCodes.Ldloc, stash);
                return;
            }

            case BoundArrayAccess array:
            {
                EmitExpression(array.Array);
                EmitExpression(array.Index);
                EmitExpression(assignment.Value);

                LocalBuilder? stash = null;
                if (leaveValue)
                {
                    stash = _il.DeclareLocal(assignment.Value.Type);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Stloc, stash);
                }

                EmitStelem(array.Type);
                if (stash is not null) _il.Emit(OpCodes.Ldloc, stash);
                return;
            }

            case BoundIndexerAccess indexer:
            {
                var setter = indexer.Indexer.SetMethod
                    ?? throw new InvalidOperationException("索引器没有 set 访问器。");

                var constrained = EmitInstanceReceiver(indexer.Receiver, setter.DeclaringType!);
                foreach (var argument in indexer.Arguments) EmitExpression(argument);
                EmitExpression(assignment.Value);

                LocalBuilder? stash = null;
                if (leaveValue)
                {
                    stash = _il.DeclareLocal(assignment.Value.Type);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Stloc, stash);
                }

                if (constrained)
                {
                    _il.Emit(OpCodes.Constrained, indexer.Receiver.Type);
                    _il.Emit(OpCodes.Callvirt, setter);
                }
                else
                {
                    _il.Emit(setter.IsVirtual && !indexer.Receiver.Type.IsValueType
                        ? OpCodes.Callvirt
                        : OpCodes.Call, setter);
                }

                if (stash is not null) _il.Emit(OpCodes.Ldloc, stash);
                return;
            }

            default:
                throw new InvalidOperationException(
                    $"无法赋值到 {assignment.Target.GetType().Name}。");
        }
    }

    // ============================================================ primitives

    private void EmitLdarg(int index)
    {
        switch (index)
        {
            case 0: _il.Emit(OpCodes.Ldarg_0); return;
            case 1: _il.Emit(OpCodes.Ldarg_1); return;
            case 2: _il.Emit(OpCodes.Ldarg_2); return;
            case 3: _il.Emit(OpCodes.Ldarg_3); return;
            default:
                if (index <= byte.MaxValue) _il.Emit(OpCodes.Ldarg_S, (byte)index);
                else _il.Emit(OpCodes.Ldarg, (short)index);
                return;
        }
    }

    private void EmitLdcI4(int value)
    {
        switch (value)
        {
            case -1: _il.Emit(OpCodes.Ldc_I4_M1); return;
            case 0: _il.Emit(OpCodes.Ldc_I4_0); return;
            case 1: _il.Emit(OpCodes.Ldc_I4_1); return;
            case 2: _il.Emit(OpCodes.Ldc_I4_2); return;
            case 3: _il.Emit(OpCodes.Ldc_I4_3); return;
            case 4: _il.Emit(OpCodes.Ldc_I4_4); return;
            case 5: _il.Emit(OpCodes.Ldc_I4_5); return;
            case 6: _il.Emit(OpCodes.Ldc_I4_6); return;
            case 7: _il.Emit(OpCodes.Ldc_I4_7); return;
            case 8: _il.Emit(OpCodes.Ldc_I4_8); return;
            default:
                if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
                    _il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
                else
                    _il.Emit(OpCodes.Ldc_I4, value);
                return;
        }
    }
}
