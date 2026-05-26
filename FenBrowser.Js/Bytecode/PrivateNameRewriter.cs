using FenBrowser.Js.Ast;

namespace FenBrowser.Js.Bytecode;

// H.5 - rewrites private member accesses (`this.#x`, `obj.#m()`) inside a
// class member body to use a per-class mangled name (e.g. `#x@C7`) so that
// ordinary property-access opcodes can store and retrieve them while keeping
// the private name lexically scoped to the defining class.
// Brand validation (TypeError on access against a non-instance) is deferred.
internal static class PrivateNameRewriter
{
    public static IReadOnlyList<StatementNode> RewriteStatements(
        IReadOnlyList<StatementNode> statements,
        IReadOnlyDictionary<string, string> mangleMap)
    {
        if (mangleMap.Count == 0) return statements;
        var result = new StatementNode[statements.Count];
        for (int i = 0; i < statements.Count; i++)
        {
            result[i] = RewriteStatement(statements[i], mangleMap);
        }
        return result;
    }

    public static ExpressionNode Rewrite(ExpressionNode expr, IReadOnlyDictionary<string, string> mangleMap)
    {
        if (mangleMap.Count == 0) return expr;
        return RewriteExpression(expr, mangleMap);
    }

    private static StatementNode RewriteStatement(StatementNode stmt, IReadOnlyDictionary<string, string> m)
    {
        switch (stmt)
        {
            case ExpressionStatementNode es:
                return new ExpressionStatementNode(RewriteExpression(es.Expression, m), es.Span);
            case BlockStatementNode b:
                return new BlockStatementNode(RewriteStatements(b.Statements, m), b.Span);
            case ReturnStatementNode r:
                return new ReturnStatementNode(
                    r.Argument is null ? null : RewriteExpression(r.Argument, m), r.Span);
            case IfStatementNode iff:
                return new IfStatementNode(
                    RewriteExpression(iff.Test, m),
                    RewriteStatement(iff.Consequent, m),
                    iff.Alternate is null ? null : RewriteStatement(iff.Alternate, m),
                    iff.Span);
            case WhileStatementNode w:
                return new WhileStatementNode(
                    RewriteExpression(w.Test, m), RewriteStatement(w.Body, m), w.Span);
            case DoWhileStatementNode dw:
                return new DoWhileStatementNode(
                    (BlockStatementNode)RewriteStatement(dw.Body, m),
                    RewriteExpression(dw.Test, m),
                    dw.Span);
            case ForStatementNode f:
                return new ForStatementNode(
                    f.Initializer is null ? null : RewriteStatement(f.Initializer, m),
                    f.Test is null ? null : RewriteExpression(f.Test, m),
                    f.Update is null ? null : RewriteExpression(f.Update, m),
                    RewriteStatement(f.Body, m),
                    f.Span);
            case ForOfStatementNode fo:
                return new ForOfStatementNode(
                    RewriteStatement(fo.Initializer, m),
                    RewriteExpression(fo.Iterable, m),
                    RewriteStatement(fo.Body, m),
                    fo.Span);
            case ForInStatementNode fi:
                return new ForInStatementNode(
                    RewriteStatement(fi.Initializer, m),
                    RewriteExpression(fi.Iterable, m),
                    RewriteStatement(fi.Body, m),
                    fi.Span);
            case VariableDeclarationStatementNode vd:
                return RewriteVarDecl(vd, m);
            case ThrowStatementNode th:
                return new ThrowStatementNode(RewriteExpression(th.Argument, m), th.Span);
            case TryCatchStatementNode tc:
                return new TryCatchStatementNode(
                    (BlockStatementNode)RewriteStatement(tc.TryBlock, m),
                    tc.CatchIdentifier,
                    (BlockStatementNode)RewriteStatement(tc.CatchBlock, m),
                    tc.Span);
            case TryFinallyStatementNode tf:
                return new TryFinallyStatementNode(
                    (BlockStatementNode)RewriteStatement(tf.TryBlock, m),
                    (BlockStatementNode)RewriteStatement(tf.FinallyBlock, m),
                    tf.Span);
            case TryCatchFinallyStatementNode tcf:
                return new TryCatchFinallyStatementNode(
                    (BlockStatementNode)RewriteStatement(tcf.TryBlock, m),
                    tcf.CatchIdentifier,
                    (BlockStatementNode)RewriteStatement(tcf.CatchBlock, m),
                    (BlockStatementNode)RewriteStatement(tcf.FinallyBlock, m),
                    tcf.Span);
            case LabeledStatementNode ls:
                return new LabeledStatementNode(ls.Label, RewriteStatement(ls.Body, m), ls.Span);
            case SwitchStatementNode sw:
                var cases = new SwitchCaseNode[sw.Cases.Count];
                for (int i = 0; i < sw.Cases.Count; i++)
                {
                    var c = sw.Cases[i];
                    cases[i] = new SwitchCaseNode(
                        c.Test is null ? null : RewriteExpression(c.Test, m),
                        RewriteStatements(c.Consequent, m),
                        c.Span);
                }
                return new SwitchStatementNode(RewriteExpression(sw.Discriminant, m), cases, sw.Span);
            case FunctionDeclarationNode fd:
                return new FunctionDeclarationNode(
                    fd.Name,
                    fd.Parameters,
                    new BlockStatementNode(RewriteStatements(fd.Body.Statements, m), fd.Body.Span),
                    fd.Span,
                    IsAsync: fd.IsAsync,
                    IsGenerator: fd.IsGenerator,
                    RestParameterIndex: fd.RestParameterIndex);
            default:
                return stmt;
        }
    }

    private static VariableDeclarationStatementNode RewriteVarDecl(VariableDeclarationStatementNode vd, IReadOnlyDictionary<string, string> m)
    {
        var newDecls = new VariableDeclaratorNode[vd.Declarators.Count];
        for (int i = 0; i < vd.Declarators.Count; i++)
        {
            var d = vd.Declarators[i];
            newDecls[i] = new VariableDeclaratorNode(
                d.Identifier,
                d.Initializer is null ? null : RewriteExpression(d.Initializer, m),
                d.Span);
        }
        return new VariableDeclarationStatementNode(vd.Kind, newDecls, vd.Span);
    }

    private static ExpressionNode RewriteExpression(ExpressionNode expr, IReadOnlyDictionary<string, string> m)
    {
        switch (expr)
        {
            case MemberExpressionNode me:
                var obj = RewriteExpression(me.Object, m);
                if (!me.Computed && me.Property.StartsWith('#') && m.TryGetValue(me.Property, out var mangled))
                {
                    return new MemberExpressionNode(obj, mangled, false, null, me.Span);
                }
                var propExpr = me.PropertyExpression is null ? null : RewriteExpression(me.PropertyExpression, m);
                return new MemberExpressionNode(obj, me.Property, me.Computed, propExpr, me.Span);

            case AssignmentExpressionNode ae:
                return new AssignmentExpressionNode(
                    RewriteExpression(ae.Left, m),
                    RewriteExpression(ae.Right, m),
                    ae.Span);

            case BinaryExpressionNode be:
                return new BinaryExpressionNode(
                    be.Operator,
                    RewriteExpression(be.Left, m),
                    RewriteExpression(be.Right, m),
                    be.Span);

            case UnaryExpressionNode ue:
                return new UnaryExpressionNode(ue.Operator, RewriteExpression(ue.Operand, m), ue.Span);

            case ConditionalExpressionNode ce:
                return new ConditionalExpressionNode(
                    RewriteExpression(ce.Test, m),
                    RewriteExpression(ce.Consequent, m),
                    RewriteExpression(ce.Alternate, m),
                    ce.Span);

            case CallExpressionNode call:
                var callee = RewriteExpression(call.Callee, m);
                var args = new ExpressionNode[call.Arguments.Count];
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    args[i] = RewriteExpression(call.Arguments[i], m);
                }
                return new CallExpressionNode(callee, args, call.Span);

            case NewExpressionNode ne:
                var neCallee = RewriteExpression(ne.Callee, m);
                var neArgs = new ExpressionNode[ne.Arguments.Count];
                for (int i = 0; i < ne.Arguments.Count; i++)
                {
                    neArgs[i] = RewriteExpression(ne.Arguments[i], m);
                }
                return new NewExpressionNode(neCallee, neArgs, ne.Span);

            case ParenthesizedExpressionNode pe:
                return new ParenthesizedExpressionNode(RewriteExpression(pe.Expression, m), pe.Span);

            case ArrayLiteralExpressionNode al:
                var elems = new ExpressionNode[al.Elements.Count];
                for (int i = 0; i < al.Elements.Count; i++)
                {
                    elems[i] = RewriteExpression(al.Elements[i], m);
                }
                return new ArrayLiteralExpressionNode(elems, al.Span);

            case SpreadElementExpressionNode sp:
                return new SpreadElementExpressionNode(RewriteExpression(sp.Argument, m), sp.Span);

            case ObjectLiteralExpressionNode ol:
                var props = new ObjectPropertyNode[ol.Properties.Count];
                for (int i = 0; i < ol.Properties.Count; i++)
                {
                    var p = ol.Properties[i];
                    props[i] = new ObjectPropertyNode(
                        p.Key,
                        p.ComputedKey is null ? null : RewriteExpression(p.ComputedKey, m),
                        p.IsComputed,
                        RewriteExpression(p.Value, m),
                        p.Span);
                }
                return new ObjectLiteralExpressionNode(props, ol.Span);

            case ArrowFunctionExpressionNode af:
                BlockStatementNode? newBlock = af.BlockBody is null
                    ? null
                    : new BlockStatementNode(RewriteStatements(af.BlockBody.Statements, m), af.BlockBody.Span);
                ExpressionNode? newExprBody = af.ExpressionBody is null
                    ? null
                    : RewriteExpression(af.ExpressionBody, m);
                return new ArrowFunctionExpressionNode(
                    af.Parameters,
                    newBlock,
                    newExprBody,
                    af.Span,
                    IsAsync: af.IsAsync);

            case FunctionExpressionNode fn:
                var newFnBody = new BlockStatementNode(RewriteStatements(fn.Body.Statements, m), fn.Body.Span);
                return new FunctionExpressionNode(
                    fn.Name,
                    fn.Parameters,
                    newFnBody,
                    fn.Span,
                    IsAsync: fn.IsAsync,
                    IsGenerator: fn.IsGenerator);

            case TemplateLiteralExpressionNode tl:
                var tlExprs = new ExpressionNode[tl.Expressions.Count];
                for (int i = 0; i < tl.Expressions.Count; i++)
                {
                    tlExprs[i] = RewriteExpression(tl.Expressions[i], m);
                }
                return new TemplateLiteralExpressionNode(tl.Quasis, tlExprs, tl.Span);

            default:
                return expr;
        }
    }
}
