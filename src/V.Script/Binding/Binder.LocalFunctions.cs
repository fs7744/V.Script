using V.Script.Diagnostics;
using V.Script.Syntax;

namespace V.Script.Binding;

/// <summary>
/// Local functions. Each one becomes a local holding a delegate, and its body is bound as a
/// lambda against that delegate — so the whole feature reuses the closure machinery rather than
/// adding a second kind of nested function.
/// </summary>
/// <remarks>
/// The delegates are assigned at the top of the block that declares them, before any other
/// statement runs. That is what makes both recursion and calling a function written further
/// down work: by the time any of the block's own code executes, every local function in it
/// already holds its delegate.
/// </remarks>
internal sealed partial class Binder
{
    /// <summary>
    /// Binds a statement list, hoisting the local functions in it to the front. Every block —
    /// including the script's own top level — goes through here.
    /// </summary>
    /// <remarks>
    /// Binding order and execution order differ on purpose. A body binds where it is written, so
    /// it sees exactly the variables C# would let it capture; the assignment it produces is
    /// hoisted, so a call written above the function still finds a delegate there.
    /// </remarks>
    private List<BoundStatement> BindStatementList(
        IReadOnlyList<StatementSyntax> statements,
        Func<StatementSyntax, int, BoundStatement> bindOne)
    {
        var symbols = DeclareLocalFunctions(statements);

        if (symbols.Count == 0)
        {
            var plain = new List<BoundStatement>(statements.Count);
            for (var i = 0; i < statements.Count; i++) plain.Add(bindOne(statements[i], i));
            return plain;
        }

        var prologue = new List<BoundStatement>(symbols.Count);
        var body = new List<BoundStatement>(statements.Count);

        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i] is LocalFunctionStatementSyntax function)
            {
                if (symbols.TryGetValue(function, out var symbol))
                    prologue.Add(BindLocalFunctionBody(function, symbol));

                continue;
            }

            body.Add(bindOne(statements[i], i));
        }

        return [.. prologue, .. body];
    }

    /// <summary>
    /// Declares every local function's name up front so that they can call each other and
    /// themselves regardless of the order they are written in.
    /// </summary>
    private Dictionary<LocalFunctionStatementSyntax, LocalSymbol> DeclareLocalFunctions(
        IReadOnlyList<StatementSyntax> statements)
    {
        var symbols = new Dictionary<LocalFunctionStatementSyntax, LocalSymbol>();

        foreach (var statement in statements)
        {
            if (statement is not LocalFunctionStatementSyntax function) continue;

            var delegateType = LocalFunctionDelegateType(function);
            if (delegateType is null) continue;

            var symbol = new LocalSymbol(function.Name, delegateType);
            if (!DeclareLocal(symbol, function.Position)) continue;

            symbols.Add(function, symbol);
        }

        return symbols;
    }

    private Type? LocalFunctionDelegateType(LocalFunctionStatementSyntax syntax)
    {
        var parameterTypes = new Type[syntax.Parameters.Count];
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            var resolved = ResolveType(syntax.Parameters[i].Type!);
            if (resolved is null) return null;
            parameterTypes[i] = resolved;
        }

        var returnType = syntax.ReturnType is null ? typeof(void) : ResolveType(syntax.ReturnType);
        if (returnType is null) return null;

        var delegateType = MakeDelegateType(parameterTypes, returnType);
        if (delegateType is null)
        {
            _diagnostics.Report(ErrorCode.ConstructNotSupported, syntax.Position,
                $"局部函数 '{syntax.Name}' 的签名无法表示为 Func/Action（参数过多，或返回 void 的参数超过 16 个）。");
        }

        return delegateType;
    }

    private BoundStatement BindLocalFunctionBody(LocalFunctionStatementSyntax syntax, LocalSymbol symbol)
    {
        var lambda = new LambdaExpressionSyntax(syntax.Position, syntax.Parameters, syntax.Body);
        var bound = BindLambda(lambda, symbol.Type);

        return new BoundLocalDeclaration(syntax.Position, symbol, bound);
    }
}
