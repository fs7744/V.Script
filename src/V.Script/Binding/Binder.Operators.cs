using System.Reflection;
using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

internal sealed partial class Binder
{
    // ============================================================ conversions

    /// <summary>
    /// Applies a conversion, reporting a diagnostic when none exists. Every implicit rule the
    /// language has is resolved here so the emitter sees only explicit <see cref="BoundConversion"/> nodes.
    /// </summary>
    private BoundExpression Convert(BoundExpression expression, Type target, SourcePosition position, bool explicitCast)
    {
        if (expression is BoundErrorExpression) return expression;

        // A lambda has no type of its own; converting one *is* binding it.
        if (expression is BoundUnboundLambda unbound)
        {
            if (Conversions.IsDelegateType(target)) return BindLambda(unbound.Syntax, target);

            _diagnostics.Report(ErrorCode.CannotConvert, position,
                $"lambda 只能转换为委托类型，{TypeResolver.Display(target)} 不是委托。");
            return new BoundErrorExpression(position);
        }

        // Both branches of `flag ? [1, 2] : []` are untyped, so the conditional itself is too.
        // Pushing the target into the branches is what gives it a type.
        if (expression is BoundConditional conditional && Conversions.AdoptsTargetType(conditional.Type))
        {
            return conditional with
            {
                Type = target,
                WhenTrue = Convert(conditional.WhenTrue, target, position, explicitCast),
                WhenFalse = Convert(conditional.WhenFalse, target, position, explicitCast),
            };
        }

        if (expression is BoundMethodGroup group) return ConvertMethodGroup(group, target, position);

        // A throw expression produces no value, so it simply takes the type asked of it.
        if (expression is BoundThrowExpression thrown) return thrown with { Type = target };

        if (expression is BoundDefaultLiteral) return new BoundDefault(position, target);

        if (expression is BoundUnboundCollection collection)
            return BindCollectionExpression(collection.Syntax, target);

        if (expression is BoundNullLiteral)
        {
            if (Conversions.IsNullableValueType(target)) return new BoundDefault(position, target);
            if (!target.IsValueType) return new BoundLiteral(position, target, null);

            _diagnostics.Report(ErrorCode.CannotConvert, position,
                $"无法将 null 转换为不可为 null 的类型 {TypeResolver.Display(target)}。");
            return new BoundDefault(position, target);
        }

        if (expression.Type == target) return expression;

        if (expression is BoundLiteral literal && TryNarrowConstant(literal, target, out var narrowed))
            return narrowed!;

        var conversion = Conversions.Classify(expression.Type, target);

        if (!conversion.Exists)
        {
            _diagnostics.Report(ErrorCode.CannotConvert, position,
                $"无法将 {TypeResolver.Display(expression.Type)} 转换为 {TypeResolver.Display(target)}。");
            return new BoundDefault(position, target);
        }

        if (!conversion.IsImplicit && !explicitCast)
        {
            _diagnostics.Report(ErrorCode.CannotConvertImplicitly, position,
                $"无法将 {TypeResolver.Display(expression.Type)} 隐式转换为 {TypeResolver.Display(target)}；" +
                $"需要显式转换 ({TypeResolver.Display(target)})。");
            return new BoundDefault(position, target);
        }

        return new BoundConversion(position, target, expression, conversion, _checked);
    }

    /// <summary>
    /// Applies the constant-expression conversion before promotion. Without it <c>u / 2</c>
    /// (with <c>u</c> a <c>uint</c>) would promote to <c>long</c>, because <c>int</c> has no
    /// implicit conversion to <c>uint</c> — C# instead reinterprets the literal as <c>uint</c>.
    /// </summary>
    private static void ApplyConstantNarrowing(ref BoundExpression left, ref BoundExpression right)
    {
        if (left is BoundLiteral leftLiteral && right is not BoundLiteral)
        {
            var target = Conversions.Unlift(right.Type);
            if (!Conversions.HasImplicit(leftLiteral.Type, target) &&
                TryNarrowConstant(leftLiteral, target, out var narrowedLeft))
                left = narrowedLeft!;
        }
        else if (right is BoundLiteral rightLiteral && left is not BoundLiteral)
        {
            var target = Conversions.Unlift(left.Type);
            if (!Conversions.HasImplicit(rightLiteral.Type, target) &&
                TryNarrowConstant(rightLiteral, target, out var narrowedRight))
                right = narrowedRight!;
        }
    }

    /// <summary>
    /// Implements the constant-expression conversion: an integer literal that fits the target
    /// type converts implicitly, which is what makes <c>byte b = 1;</c> legal.
    /// </summary>
    private static bool TryNarrowConstant(BoundLiteral literal, Type target, out BoundExpression? result)
    {
        result = null;
        if (literal.Value is null) return false;

        var targetBase = Conversions.Unlift(target);
        if (!Conversions.IsIntegral(targetBase) || !Conversions.IsIntegral(literal.Type)) return false;

        long value;
        try
        {
            value = System.Convert.ToInt64(literal.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return false;
        }

        var fits = Type.GetTypeCode(targetBase) switch
        {
            TypeCode.SByte => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            TypeCode.Byte => value is >= byte.MinValue and <= byte.MaxValue,
            TypeCode.Int16 => value is >= short.MinValue and <= short.MaxValue,
            TypeCode.UInt16 => value is >= ushort.MinValue and <= ushort.MaxValue,
            TypeCode.Int32 => value is >= int.MinValue and <= int.MaxValue,
            TypeCode.UInt32 => value is >= uint.MinValue and <= uint.MaxValue,
            TypeCode.Char => value is >= char.MinValue and <= char.MaxValue,
            TypeCode.Int64 => true,
            TypeCode.UInt64 => value >= 0,

            // nint / nuint have no TypeCode; a constant fits when it would fit the 32-bit form,
            // which is the range C# guarantees on every platform.
            _ => targetBase == typeof(nint) ? value is >= int.MinValue and <= int.MaxValue
                : targetBase == typeof(nuint) && value is >= 0 and <= uint.MaxValue,
        };

        if (!fits) return false;

        var converted = targetBase == typeof(nint) ? (nint)value
            : targetBase == typeof(nuint) ? (nuint)value
            : System.Convert.ChangeType(
                literal.Value, targetBase, System.Globalization.CultureInfo.InvariantCulture);

        var narrowed = new BoundLiteral(literal.Position, targetBase, converted);

        result = targetBase == target
            ? narrowed
            : new BoundConversion(literal.Position, target, narrowed, new Conversion(ConversionKind.ImplicitNullable));

        return true;
    }

    // ============================================================ unary

    private BoundExpression BindUnary(UnaryExpressionSyntax syntax)
    {
        if (syntax.Operator is SyntaxKind.PlusPlus or SyntaxKind.MinusMinus)
            return BindIncrementAsValue(syntax.Operand, syntax.Operator, syntax.Position, prefix: true);

        var operand = BindExpression(syntax.Operand);
        if (operand is BoundErrorExpression) return operand;

        var kind = syntax.Operator switch
        {
            SyntaxKind.Plus => BoundUnaryKind.Plus,
            SyntaxKind.Minus => BoundUnaryKind.Negate,
            SyntaxKind.Bang => BoundUnaryKind.LogicalNot,
            SyntaxKind.Tilde => BoundUnaryKind.BitwiseNot,
            _ => BoundUnaryKind.Plus,
        };

        return BindUnaryOperator(kind, operand, syntax.Position);
    }

    private BoundExpression BindUnaryOperator(BoundUnaryKind kind, BoundExpression operand, SourcePosition position)
    {
        var lifted = Conversions.IsNullableValueType(operand.Type);
        var operandBase = Conversions.Unlift(operand.Type);

        if (kind == BoundUnaryKind.LogicalNot)
        {
            if (operandBase != typeof(bool))
            {
                return Fail(position, ErrorCode.OperatorNotDefined,
                    $"运算符 '!' 不能用于 {TypeResolver.Display(operand.Type)}。");
            }
            var resultType = lifted ? typeof(bool?) : typeof(bool);
            return new BoundUnary(position, resultType, kind, operand, lifted, null);
        }

        if (kind == BoundUnaryKind.BitwiseNot && operandBase == typeof(bool))
        {
            var resultType = lifted ? typeof(bool?) : typeof(bool);
            return new BoundUnary(position, resultType, BoundUnaryKind.LogicalNot, operand, lifted, null);
        }

        var promoted = kind == BoundUnaryKind.BitwiseNot
            ? (Conversions.IsIntegral(operandBase) ? NumericPromotion.PromoteUnary(operandBase) : null)
            : NumericPromotion.PromoteUnary(operandBase);

        if (promoted is null)
        {
            var userDefined = FindUnaryOperator(kind, operandBase);
            if (userDefined is not null)
            {
                var parameterType = userDefined.GetParameters()[0].ParameterType;
                var argument = Convert(operand, lifted ? Conversions.Lift(parameterType) : parameterType,
                    position, explicitCast: false);
                var resultType = lifted ? Conversions.Lift(userDefined.ReturnType) : userDefined.ReturnType;
                return new BoundUnary(position, resultType, kind, argument, lifted, userDefined);
            }

            return Fail(position, ErrorCode.OperatorNotDefined,
                $"运算符 '{Describe(kind)}' 不能用于 {TypeResolver.Display(operand.Type)}。");
        }

        if (kind == BoundUnaryKind.Plus && promoted == operandBase && !lifted) return operand;

        var targetType = lifted ? Conversions.Lift(promoted) : promoted;
        var converted = Convert(operand, targetType, position, explicitCast: false);

        // decimal has no IL-level arithmetic; it goes through its operator methods.
        MethodInfo? method = null;
        if (promoted == typeof(decimal) && kind == BoundUnaryKind.Negate)
            method = typeof(decimal).GetMethod("op_UnaryNegation", [typeof(decimal)]);

        return new BoundUnary(position, targetType, kind, converted, lifted, method);
    }

    private static string Describe(BoundUnaryKind kind) => kind switch
    {
        BoundUnaryKind.Plus => "+",
        BoundUnaryKind.Negate => "-",
        BoundUnaryKind.LogicalNot => "!",
        BoundUnaryKind.BitwiseNot => "~",
        _ => "?",
    };

    private static MethodInfo? FindUnaryOperator(BoundUnaryKind kind, Type operand)
    {
        var name = kind switch
        {
            BoundUnaryKind.Negate => "op_UnaryNegation",
            BoundUnaryKind.Plus => "op_UnaryPlus",
            BoundUnaryKind.LogicalNot => "op_LogicalNot",
            BoundUnaryKind.BitwiseNot => "op_OnesComplement",
            _ => null,
        };
        if (name is null) return null;

        foreach (var method in operand.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != name) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && Conversions.HasImplicit(operand, parameters[0].ParameterType))
                return method;
        }

        return null;
    }

    // ============================================================ binary

    private BoundExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        switch (syntax.Operator)
        {
            case SyntaxKind.AmpAmp:
            case SyntaxKind.PipePipe:
            {
                var left = BindCondition(syntax.Left);
                var right = BindCondition(syntax.Right);
                return new BoundLogical(syntax.Position, left, right, syntax.Operator == SyntaxKind.AmpAmp);
            }

            case SyntaxKind.QuestionQuestion:
                return BindNullCoalesce(syntax);
        }

        var boundLeft = BindExpression(syntax.Left);
        var boundRight = BindExpression(syntax.Right);

        if (boundLeft is BoundErrorExpression) return boundLeft;
        if (boundRight is BoundErrorExpression) return boundRight;

        var kind = MapBinaryKind(syntax.Operator);
        if (kind is null)
        {
            return Fail(syntax.Position, ErrorCode.OperatorNotDefined,
                $"不支持的运算符 '{syntax.Operator}'。");
        }

        return BindBinaryOperator(kind.Value, boundLeft, boundRight, syntax.Position);
    }

    private static BoundBinaryKind? MapBinaryKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Plus or SyntaxKind.PlusEquals => BoundBinaryKind.Add,
        SyntaxKind.Minus or SyntaxKind.MinusEquals => BoundBinaryKind.Subtract,
        SyntaxKind.Star or SyntaxKind.StarEquals => BoundBinaryKind.Multiply,
        SyntaxKind.Slash or SyntaxKind.SlashEquals => BoundBinaryKind.Divide,
        SyntaxKind.Percent or SyntaxKind.PercentEquals => BoundBinaryKind.Modulo,
        SyntaxKind.Amp or SyntaxKind.AmpEquals => BoundBinaryKind.BitAnd,
        SyntaxKind.Pipe or SyntaxKind.PipeEquals => BoundBinaryKind.BitOr,
        SyntaxKind.Caret or SyntaxKind.CaretEquals => BoundBinaryKind.BitXor,
        SyntaxKind.LessLess or SyntaxKind.LessLessEquals => BoundBinaryKind.LeftShift,
        SyntaxKind.GreaterGreater or SyntaxKind.GreaterGreaterEquals => BoundBinaryKind.RightShift,
        SyntaxKind.EqualsEquals => BoundBinaryKind.Equal,
        SyntaxKind.BangEquals => BoundBinaryKind.NotEqual,
        SyntaxKind.Less => BoundBinaryKind.Less,
        SyntaxKind.LessEquals => BoundBinaryKind.LessEqual,
        SyntaxKind.Greater => BoundBinaryKind.Greater,
        SyntaxKind.GreaterEquals => BoundBinaryKind.GreaterEqual,
        _ => null,
    };

    private BoundExpression BindBinaryOperator(
        BoundBinaryKind kind,
        BoundExpression left,
        BoundExpression right,
        SourcePosition position)
    {
        if (kind is BoundBinaryKind.Equal or BoundBinaryKind.NotEqual)
            return BindEquality(kind, left, right, position);

        if (kind is BoundBinaryKind.LeftShift or BoundBinaryKind.RightShift)
            return BindShift(kind, left, right, position);

        if (kind == BoundBinaryKind.Add && (left.Type == typeof(string) || right.Type == typeof(string)))
            return BindStringConcat(left, right, position);

        ApplyConstantNarrowing(ref left, ref right);

        var lifted = Conversions.IsNullableValueType(left.Type) || Conversions.IsNullableValueType(right.Type);
        var leftBase = Conversions.Unlift(left.Type);
        var rightBase = Conversions.Unlift(right.Type);

        // bool & | ^ are non-short-circuiting logical operators
        if (kind is BoundBinaryKind.BitAnd or BoundBinaryKind.BitOr or BoundBinaryKind.BitXor &&
            leftBase == typeof(bool) && rightBase == typeof(bool))
        {
            var resultType = lifted ? typeof(bool?) : typeof(bool);
            return new BoundBinary(position, resultType, kind,
                Convert(left, resultType, position, explicitCast: false),
                Convert(right, resultType, position, explicitCast: false),
                lifted, null);
        }

        if (leftBase.IsEnum || rightBase.IsEnum)
        {
            var enumResult = BindEnumArithmetic(kind, left, right, leftBase, rightBase, lifted, position);
            if (enumResult is not null) return enumResult;
        }

        var promoted = NumericPromotion.Promote(leftBase, rightBase);
        if (promoted is not null)
        {
            var isComparison = kind is BoundBinaryKind.Less or BoundBinaryKind.LessEqual
                or BoundBinaryKind.Greater or BoundBinaryKind.GreaterEqual;

            var operandType = lifted ? Conversions.Lift(promoted) : promoted;
            var convertedLeft = Convert(left, operandType, position, explicitCast: false);
            var convertedRight = Convert(right, operandType, position, explicitCast: false);

            MethodInfo? method = null;
            if (promoted == typeof(decimal))
            {
                method = FindBinaryOperator(kind, typeof(decimal), typeof(decimal));
                if (method is null)
                {
                    return Fail(position, ErrorCode.OperatorNotDefined,
                        $"decimal 不支持运算符 '{Describe(kind)}'。");
                }
            }

            var resultType = isComparison
                ? typeof(bool)
                : operandType;

            return new BoundBinary(
                position, resultType, kind, convertedLeft, convertedRight, lifted, method, _checked);
        }

        var userDefined = FindBinaryOperator(kind, leftBase, rightBase);
        if (userDefined is not null)
        {
            var parameters = userDefined.GetParameters();
            var leftType = lifted ? Conversions.Lift(parameters[0].ParameterType) : parameters[0].ParameterType;
            var rightType = lifted ? Conversions.Lift(parameters[1].ParameterType) : parameters[1].ParameterType;
            var resultType = lifted ? Conversions.Lift(userDefined.ReturnType) : userDefined.ReturnType;

            return new BoundBinary(position, resultType, kind,
                Convert(left, leftType, position, explicitCast: false),
                Convert(right, rightType, position, explicitCast: false),
                lifted, userDefined);
        }

        return Fail(position, ErrorCode.OperatorNotDefined,
            $"运算符 '{Describe(kind)}' 不能用于 {TypeResolver.Display(left.Type)} 和 {TypeResolver.Display(right.Type)}。");
    }

    private BoundExpression? BindEnumArithmetic(
        BoundBinaryKind kind,
        BoundExpression left,
        BoundExpression right,
        Type leftBase,
        Type rightBase,
        bool lifted,
        SourcePosition position)
    {
        // enum ± integral -> enum,  enum - enum -> underlying
        if (kind is not (BoundBinaryKind.Add or BoundBinaryKind.Subtract
            or BoundBinaryKind.Less or BoundBinaryKind.LessEqual
            or BoundBinaryKind.Greater or BoundBinaryKind.GreaterEqual
            or BoundBinaryKind.BitAnd or BoundBinaryKind.BitOr or BoundBinaryKind.BitXor))
            return null;

        if (leftBase.IsEnum && rightBase.IsEnum)
        {
            if (leftBase != rightBase) return null;

            var underlying = Enum.GetUnderlyingType(leftBase);
            var operandType = lifted ? Conversions.Lift(underlying) : underlying;

            var resultType = kind switch
            {
                BoundBinaryKind.Subtract => operandType,
                BoundBinaryKind.BitAnd or BoundBinaryKind.BitOr or BoundBinaryKind.BitXor =>
                    lifted ? Conversions.Lift(leftBase) : leftBase,
                _ => typeof(bool),
            };

            var bound = new BoundBinary(position, resultType == leftBase ? operandType : resultType, kind,
                Convert(left, operandType, position, explicitCast: true),
                Convert(right, operandType, position, explicitCast: true),
                lifted, null, _checked);

            return resultType == operandType || resultType == typeof(bool)
                ? bound
                : new BoundConversion(position, resultType, bound, new Conversion(ConversionKind.ExplicitEnumeration));
        }

        if (kind is not (BoundBinaryKind.Add or BoundBinaryKind.Subtract)) return null;

        var enumType = leftBase.IsEnum ? leftBase : rightBase;
        var enumUnderlying = Enum.GetUnderlyingType(enumType);
        var otherBase = leftBase.IsEnum ? rightBase : leftBase;
        if (!Conversions.IsIntegral(otherBase)) return null;

        var operandNumeric = lifted ? Conversions.Lift(enumUnderlying) : enumUnderlying;
        var arithmetic = new BoundBinary(position, operandNumeric, kind,
            Convert(left, operandNumeric, position, explicitCast: true),
            Convert(right, operandNumeric, position, explicitCast: true),
            lifted, null, _checked);

        var enumResultType = lifted ? Conversions.Lift(enumType) : enumType;
        return new BoundConversion(position, enumResultType, arithmetic,
            new Conversion(ConversionKind.ExplicitEnumeration));
    }

    private BoundExpression BindShift(
        BoundBinaryKind kind,
        BoundExpression left,
        BoundExpression right,
        SourcePosition position)
    {
        var lifted = Conversions.IsNullableValueType(left.Type) || Conversions.IsNullableValueType(right.Type);
        var leftBase = Conversions.Unlift(left.Type);

        var promoted = Conversions.IsIntegral(leftBase) ? NumericPromotion.PromoteUnary(leftBase) : null;
        if (promoted is null)
        {
            return Fail(position, ErrorCode.OperatorNotDefined,
                $"移位运算符不能用于 {TypeResolver.Display(left.Type)}。");
        }

        var leftType = lifted ? Conversions.Lift(promoted) : promoted;
        var rightType = lifted ? typeof(int?) : typeof(int);

        return new BoundBinary(position, leftType, kind,
            Convert(left, leftType, position, explicitCast: false),
            Convert(right, rightType, position, explicitCast: false),
            lifted, null, _checked);
    }

    private BoundExpression BindStringConcat(BoundExpression left, BoundExpression right, SourcePosition position)
    {
        var bothStrings = left.Type == typeof(string) && right.Type == typeof(string);

        var method = bothStrings
            ? typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!
            : typeof(string).GetMethod(nameof(string.Concat), [typeof(object), typeof(object)])!;

        var parameterType = bothStrings ? typeof(string) : typeof(object);

        return new BoundCall(position, null, method,
        [
            ConvertForConcat(left, parameterType, position),
            ConvertForConcat(right, parameterType, position),
        ]);
    }

    private BoundExpression ConvertForConcat(BoundExpression expression, Type parameterType, SourcePosition position)
    {
        if (expression is BoundNullLiteral) return new BoundLiteral(position, parameterType, null);
        if (expression.Type == parameterType) return expression;
        return Convert(expression, parameterType, position, explicitCast: false);
    }

    private BoundExpression BindEquality(
        BoundBinaryKind kind,
        BoundExpression left,
        BoundExpression right,
        SourcePosition position)
    {
        // null comparisons
        if (left is BoundNullLiteral || right is BoundNullLiteral)
        {
            var value = left is BoundNullLiteral ? right : left;
            if (value is BoundNullLiteral)
                return new BoundLiteral(position, typeof(bool), kind == BoundBinaryKind.Equal);

            if (!Conversions.IsNullAssignable(value.Type))
            {
                _diagnostics.Warn(ErrorCode.OperatorNotDefined, position,
                    $"{TypeResolver.Display(value.Type)} 永远不为 null，该比较结果恒定。");
                return new BoundLiteral(position, typeof(bool), kind == BoundBinaryKind.NotEqual);
            }

            return MakeNullTest(value, kind == BoundBinaryKind.Equal, position);
        }

        ApplyConstantNarrowing(ref left, ref right);

        var lifted = Conversions.IsNullableValueType(left.Type) || Conversions.IsNullableValueType(right.Type);
        var leftBase = Conversions.Unlift(left.Type);
        var rightBase = Conversions.Unlift(right.Type);

        if (leftBase == typeof(string) && rightBase == typeof(string))
        {
            var op = typeof(string).GetMethod(
                kind == BoundBinaryKind.Equal ? "op_Equality" : "op_Inequality",
                [typeof(string), typeof(string)])!;
            return new BoundCall(position, null, op, [left, right]);
        }

        if (leftBase == typeof(bool) && rightBase == typeof(bool))
        {
            var operandType = lifted ? typeof(bool?) : typeof(bool);
            return new BoundBinary(position, typeof(bool), kind,
                Convert(left, operandType, position, explicitCast: false),
                Convert(right, operandType, position, explicitCast: false),
                lifted, null);
        }

        if (leftBase.IsEnum && rightBase.IsEnum && leftBase == rightBase)
        {
            var underlying = Enum.GetUnderlyingType(leftBase);
            var operandType = lifted ? Conversions.Lift(underlying) : underlying;
            return new BoundBinary(position, typeof(bool), kind,
                Convert(left, operandType, position, explicitCast: true),
                Convert(right, operandType, position, explicitCast: true),
                lifted, null);
        }

        var promoted = NumericPromotion.Promote(leftBase, rightBase);
        if (promoted is not null)
        {
            var operandType = lifted ? Conversions.Lift(promoted) : promoted;

            MethodInfo? method = null;
            if (promoted == typeof(decimal))
                method = FindBinaryOperator(kind, typeof(decimal), typeof(decimal));

            return new BoundBinary(position, typeof(bool), kind,
                Convert(left, operandType, position, explicitCast: false),
                Convert(right, operandType, position, explicitCast: false),
                lifted, method);
        }

        var userDefined = FindBinaryOperator(kind, leftBase, rightBase);
        if (userDefined is not null)
        {
            var parameters = userDefined.GetParameters();
            return new BoundBinary(position, typeof(bool), kind,
                Convert(left, lifted ? Conversions.Lift(parameters[0].ParameterType) : parameters[0].ParameterType,
                    position, explicitCast: false),
                Convert(right, lifted ? Conversions.Lift(parameters[1].ParameterType) : parameters[1].ParameterType,
                    position, explicitCast: false),
                lifted, userDefined);
        }

        // reference equality
        if (!left.Type.IsValueType && !right.Type.IsValueType &&
            (left.Type.IsAssignableFrom(right.Type) || right.Type.IsAssignableFrom(left.Type)))
        {
            return new BoundBinary(position, typeof(bool), kind,
                Convert(left, typeof(object), position, explicitCast: false),
                Convert(right, typeof(object), position, explicitCast: false),
                IsLifted: false, Method: null);
        }

        return Fail(position, ErrorCode.OperatorNotDefined,
            $"运算符 '{Describe(kind)}' 不能用于 {TypeResolver.Display(left.Type)} 和 {TypeResolver.Display(right.Type)}。");
    }

    private BoundExpression MakeNullTest(BoundExpression value, bool testingForNull, SourcePosition position)
    {
        if (Conversions.IsNullableValueType(value.Type))
        {
            var hasValue = value.Type.GetProperty("HasValue")!;
            BoundExpression test = new BoundPropertyAccess(position, value, hasValue);
            return testingForNull
                ? new BoundUnary(position, typeof(bool), BoundUnaryKind.LogicalNot, test, false, null)
                : test;
        }

        return new BoundBinary(position, typeof(bool),
            testingForNull ? BoundBinaryKind.Equal : BoundBinaryKind.NotEqual,
            Convert(value, typeof(object), position, explicitCast: false),
            new BoundLiteral(position, typeof(object), null),
            IsLifted: false, Method: null);
    }

    private static string Describe(BoundBinaryKind kind) => kind switch
    {
        BoundBinaryKind.Add => "+",
        BoundBinaryKind.Subtract => "-",
        BoundBinaryKind.Multiply => "*",
        BoundBinaryKind.Divide => "/",
        BoundBinaryKind.Modulo => "%",
        BoundBinaryKind.BitAnd => "&",
        BoundBinaryKind.BitOr => "|",
        BoundBinaryKind.BitXor => "^",
        BoundBinaryKind.LeftShift => "<<",
        BoundBinaryKind.RightShift => ">>",
        BoundBinaryKind.Equal => "==",
        BoundBinaryKind.NotEqual => "!=",
        BoundBinaryKind.Less => "<",
        BoundBinaryKind.LessEqual => "<=",
        BoundBinaryKind.Greater => ">",
        BoundBinaryKind.GreaterEqual => ">=",
        _ => "?",
    };

    private static MethodInfo? FindBinaryOperator(BoundBinaryKind kind, Type left, Type right)
    {
        var name = kind switch
        {
            BoundBinaryKind.Add => "op_Addition",
            BoundBinaryKind.Subtract => "op_Subtraction",
            BoundBinaryKind.Multiply => "op_Multiply",
            BoundBinaryKind.Divide => "op_Division",
            BoundBinaryKind.Modulo => "op_Modulus",
            BoundBinaryKind.BitAnd => "op_BitwiseAnd",
            BoundBinaryKind.BitOr => "op_BitwiseOr",
            BoundBinaryKind.BitXor => "op_ExclusiveOr",
            BoundBinaryKind.Equal => "op_Equality",
            BoundBinaryKind.NotEqual => "op_Inequality",
            BoundBinaryKind.Less => "op_LessThan",
            BoundBinaryKind.LessEqual => "op_LessThanOrEqual",
            BoundBinaryKind.Greater => "op_GreaterThan",
            BoundBinaryKind.GreaterEqual => "op_GreaterThanOrEqual",
            _ => null,
        };
        if (name is null) return null;

        foreach (var declaring in left == right ? [left] : new[] { left, right })
        {
            foreach (var method in declaring.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != name) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 2) continue;
                if (!Conversions.HasImplicit(left, parameters[0].ParameterType)) continue;
                if (!Conversions.HasImplicit(right, parameters[1].ParameterType)) continue;
                return method;
            }
        }

        return null;
    }

    // ============================================================ ?? and assignment

    private BoundExpression BindNullCoalesce(BinaryExpressionSyntax syntax)
    {
        var left = BindExpression(syntax.Left);
        var right = BindExpression(syntax.Right);
        var position = syntax.Position;

        if (left is BoundErrorExpression || right is BoundErrorExpression)
            return new BoundErrorExpression(position);

        if (!Conversions.IsNullAssignable(left.Type))
        {
            _diagnostics.Warn(ErrorCode.OperatorNotDefined, position,
                $"'??' 的左操作数 {TypeResolver.Display(left.Type)} 永远不为 null。");
            return left;
        }

        var leftValueType = Conversions.IsNullableValueType(left.Type)
            ? Conversions.Unlift(left.Type)
            : left.Type;

        var resultType = BestCommonType(leftValueType, right.Type == Conversions.NullLiteralType
            ? leftValueType
            : right.Type);

        if (resultType is null)
        {
            return Fail(position, ErrorCode.CannotConvert,
                $"'??' 的两侧类型不兼容：{TypeResolver.Display(left.Type)} 与 {TypeResolver.Display(right.Type)}。");
        }

        var temp = MakeTemp(left.Type);
        var store = new BoundAssignment(position, new BoundLocalAccess(position, temp), left);

        var condition = MakeNullTest(new BoundLocalAccess(position, temp), testingForNull: false, position);

        BoundExpression whenNotNull = new BoundLocalAccess(position, temp);
        if (Conversions.IsNullableValueType(left.Type))
        {
            var getValue = left.Type.GetMethod("GetValueOrDefault", Type.EmptyTypes)!;
            whenNotNull = new BoundCall(position, whenNotNull, getValue, []);
        }

        var conditional = new BoundConditional(position, resultType, condition,
            Convert(whenNotNull, resultType, position, explicitCast: false),
            Convert(right, resultType, position, explicitCast: false));

        return new BoundSequence(position, resultType, [store], conditional);
    }

    private BoundExpression BindAssignment(AssignmentExpressionSyntax syntax)
    {
        if (syntax.Target is TupleExpressionSyntax tuple)
        {
            if (syntax.Operator != SyntaxKind.Equals)
            {
                return Fail(syntax.Position, ErrorCode.OperatorNotDefined,
                    "解构赋值只支持 '='。");
            }

            return BindTupleAssignment(tuple, syntax.Value);
        }

        var target = BindExpression(syntax.Target);
        if (target is BoundErrorExpression) return target;

        if (!IsAssignable(target))
        {
            return Fail(syntax.Position, ErrorCode.NotAssignable, "赋值目标不是变量、属性或索引器。");
        }

        if (syntax.Operator == SyntaxKind.Equals)
        {
            var value = BindExpression(syntax.Value);
            return new BoundAssignment(syntax.Position,
                target, Convert(value, target.Type, syntax.Position, explicitCast: false));
        }

        var (readable, sideEffects) = PrepareTarget(target, syntax.Position);

        if (syntax.Operator == SyntaxKind.QuestionQuestionEquals)
        {
            var value = BindExpression(syntax.Value);
            if (!Conversions.IsNullAssignable(readable.Type))
            {
                return Fail(syntax.Position, ErrorCode.OperatorNotDefined,
                    $"'??=' 的目标 {TypeResolver.Display(readable.Type)} 永远不为 null。");
            }

            var condition = MakeNullTest(readable, testingForNull: true, syntax.Position);
            var assign = new BoundAssignment(syntax.Position, readable,
                Convert(value, readable.Type, syntax.Position, explicitCast: false));

            var conditional = new BoundConditional(syntax.Position, readable.Type, condition, assign, readable);
            return sideEffects.Count == 0
                ? conditional
                : new BoundSequence(syntax.Position, readable.Type, sideEffects, conditional);
        }

        var kind = MapBinaryKind(syntax.Operator);
        if (kind is null)
        {
            return Fail(syntax.Position, ErrorCode.OperatorNotDefined,
                $"不支持的复合赋值运算符 '{syntax.Operator}'。");
        }

        var operand = BindExpression(syntax.Value);
        var combined = BindBinaryOperator(kind.Value, readable, operand, syntax.Position);
        if (combined is BoundErrorExpression) return combined;

        // Compound assignment implies a cast back to the target type.
        var result = new BoundAssignment(syntax.Position, readable,
            Convert(combined, readable.Type, syntax.Position, explicitCast: true));

        return sideEffects.Count == 0
            ? result
            : new BoundSequence(syntax.Position, readable.Type, sideEffects, result);
    }

    private static bool IsAssignable(BoundExpression expression) => expression switch
    {
        BoundLocalAccess => true,
        BoundArrayAccess => true,
        BoundFieldAccess field => !field.Field.IsInitOnly && !field.Field.IsLiteral,
        BoundPropertyAccess property => property.Property.CanWrite,
        BoundIndexerAccess indexer => indexer.Indexer.CanWrite,
        _ => false,
    };

    /// <summary>
    /// Returns a form of the target that can be read and written more than once without
    /// re-evaluating its receiver, plus the side effects needed to set that up.
    /// </summary>
    private (BoundExpression Target, List<BoundExpression> SideEffects) PrepareTarget(
        BoundExpression target,
        SourcePosition position)
    {
        var sideEffects = new List<BoundExpression>();

        BoundExpression Capture(BoundExpression expression)
        {
            if (IsRepeatable(expression)) return expression;

            var temp = MakeTemp(expression.Type);
            sideEffects.Add(new BoundAssignment(position, new BoundLocalAccess(position, temp), expression));
            return new BoundLocalAccess(position, temp);
        }

        return target switch
        {
            BoundFieldAccess { Receiver: not null } field =>
                (field with { Receiver = Capture(field.Receiver) }, sideEffects),

            BoundPropertyAccess { Receiver: not null } property =>
                (property with { Receiver = Capture(property.Receiver) }, sideEffects),

            BoundArrayAccess array =>
                (array with
                {
                    Array = Capture(array.Array),
                    Indices = array.Indices.Select(Capture).ToArray(),
                }, sideEffects),

            BoundIndexerAccess indexer =>
                (indexer with
                {
                    Receiver = Capture(indexer.Receiver),
                    Arguments = indexer.Arguments.Select(Capture).ToArray(),
                }, sideEffects),

            _ => (target, sideEffects),
        };
    }

    private static bool IsRepeatable(BoundExpression expression) => expression switch
    {
        BoundLocalAccess => true,
        BoundParameterAccess => true,
        BoundLiteral => true,
        BoundDefault => true,
        BoundFieldAccess field => field.Receiver is null || IsRepeatable(field.Receiver),
        _ => false,
    };

    // ============================================================ ++ / --

    private BoundExpression BindIncrementAsStatement(
        ExpressionSyntax targetSyntax,
        SyntaxKind op,
        SourcePosition position)
    {
        var target = BindExpression(targetSyntax);
        if (target is BoundErrorExpression) return target;

        if (!IsAssignable(target))
            return Fail(position, ErrorCode.NotAssignable, "'++' / '--' 的目标不是变量、属性或索引器。");

        var (readable, sideEffects) = PrepareTarget(target, position);
        var kind = op == SyntaxKind.PlusPlus ? BoundBinaryKind.Add : BoundBinaryKind.Subtract;

        var one = MakeOne(readable.Type, position);
        if (one is null)
        {
            return Fail(position, ErrorCode.OperatorNotDefined,
                $"'{(op == SyntaxKind.PlusPlus ? "++" : "--")}' 不能用于 {TypeResolver.Display(readable.Type)}。");
        }

        var combined = BindBinaryOperator(kind, readable, one, position);
        if (combined is BoundErrorExpression) return combined;

        var assign = new BoundAssignment(position, readable,
            Convert(combined, readable.Type, position, explicitCast: true));

        return sideEffects.Count == 0
            ? assign
            : new BoundSequence(position, readable.Type, sideEffects, assign);
    }

    private BoundExpression BindIncrementAsValue(
        ExpressionSyntax targetSyntax,
        SyntaxKind op,
        SourcePosition position,
        bool prefix)
    {
        var target = BindExpression(targetSyntax);
        if (target is BoundErrorExpression) return target;

        if (!IsAssignable(target))
            return Fail(position, ErrorCode.NotAssignable, "'++' / '--' 的目标不是变量、属性或索引器。");

        var (readable, sideEffects) = PrepareTarget(target, position);
        var kind = op == SyntaxKind.PlusPlus ? BoundBinaryKind.Add : BoundBinaryKind.Subtract;

        var one = MakeOne(readable.Type, position);
        if (one is null)
        {
            return Fail(position, ErrorCode.OperatorNotDefined,
                $"'{(op == SyntaxKind.PlusPlus ? "++" : "--")}' 不能用于 {TypeResolver.Display(readable.Type)}。");
        }

        var combined = BindBinaryOperator(kind, readable, one, position);
        if (combined is BoundErrorExpression) return combined;

        var assign = new BoundAssignment(position, readable,
            Convert(combined, readable.Type, position, explicitCast: true));

        if (prefix)
        {
            sideEffects.Add(assign);
            return new BoundSequence(position, readable.Type, sideEffects, readable);
        }

        // postfix: capture the old value before writing the new one
        var old = MakeTemp(readable.Type);
        sideEffects.Add(new BoundAssignment(position, new BoundLocalAccess(position, old), readable));
        sideEffects.Add(assign);
        return new BoundSequence(position, readable.Type, sideEffects, new BoundLocalAccess(position, old));
    }

    private static BoundExpression? MakeOne(Type type, SourcePosition position)
    {
        var baseType = Conversions.Unlift(type);

        if (Conversions.IsNumeric(baseType))
        {
            var one = System.Convert.ChangeType(1, baseType, System.Globalization.CultureInfo.InvariantCulture);
            return new BoundLiteral(position, baseType, one);
        }

        if (baseType.IsEnum) return new BoundLiteral(position, typeof(int), 1);

        return null;
    }
}
