using FenBrowser.Js.Ast;
using FenBrowser.Js.Diagnostics;
using FenBrowser.Js.Parser;

namespace FenBrowser.Js.AstValidation;

/// <summary>
/// Validates AST for ECMA-262 early errors (Static Semantics checks).
/// Each check corresponds to an Early Error rule in the spec.
/// Throws JsParserException for violations so parse-negative tests pass.
/// </summary>
public sealed class AstValidator
{
    public void Validate(ProgramNode program, DiagnosticBag diagnostics)
    {
        foreach (var statement in program.Body)
        {
            WalkStatement(statement, inFieldInit: false);
        }
    }

    /// <summary>
    /// Walk a statement subtree looking for class-field early errors.
    /// Returns true if a violation was found (caller should stop).
    /// </summary>
    private static bool WalkStatement(StatementNode statement, bool inFieldInit)
    {
        switch (statement)
        {
            case ClassDeclarationNode classDecl:
                return WalkClassMembers(classDecl.Members);

            case ExpressionStatementNode expr:
                return WalkExpression(expr.Expression, inFieldInit);

            case VariableDeclarationStatementNode varDecl:
                foreach (var d in varDecl.Declarators)
                {
                    if (d.Initializer is not null && WalkExpression(d.Initializer, inFieldInit))
                        return true;
                }
                return false;

            case IfStatementNode ifStmt:
                return WalkExpression(ifStmt.Test, inFieldInit) ||
                       WalkStatement(ifStmt.Consequent, inFieldInit) ||
                       (ifStmt.Alternate is not null && WalkStatement(ifStmt.Alternate, inFieldInit));

            case ForStatementNode forStmt:
                if (forStmt.Initializer is not null && WalkStatement(forStmt.Initializer, inFieldInit))
                    return true;
                if (forStmt.Test is not null && WalkExpression(forStmt.Test, inFieldInit))
                    return true;
                if (forStmt.Update is not null && WalkExpression(forStmt.Update, inFieldInit))
                    return true;
                return WalkStatement(forStmt.Body, inFieldInit);

            case ForOfStatementNode forOf:
                return WalkStatement(forOf.Initializer, inFieldInit) ||
                       WalkExpression(forOf.Iterable, inFieldInit) ||
                       WalkStatement(forOf.Body, inFieldInit);

            case ForInStatementNode forIn:
                return WalkStatement(forIn.Initializer, inFieldInit) ||
                       WalkExpression(forIn.Iterable, inFieldInit) ||
                       WalkStatement(forIn.Body, inFieldInit);

            case WhileStatementNode whileStmt:
                return WalkExpression(whileStmt.Test, inFieldInit) ||
                       WalkStatement(whileStmt.Body, inFieldInit);

            case DoWhileStatementNode doWhile:
                return WalkStatement(doWhile.Body, inFieldInit) ||
                       WalkExpression(doWhile.Test, inFieldInit);

            case WithStatementNode withStmt:
                return WalkExpression(withStmt.Object, inFieldInit) ||
                       WalkStatement(withStmt.Body, inFieldInit);

            case ReturnStatementNode ret:
                return ret.Argument is not null && WalkExpression(ret.Argument, inFieldInit);

            case ThrowStatementNode thr:
                return WalkExpression(thr.Argument, inFieldInit);

            case TryCatchStatementNode tryCatch:
                if (WalkBlock(tryCatch.TryBlock.Statements, inFieldInit)) return true;
                return WalkBlock(tryCatch.CatchBlock.Statements, inFieldInit);

            case TryFinallyStatementNode tryFin:
                if (WalkBlock(tryFin.TryBlock.Statements, inFieldInit)) return true;
                return WalkBlock(tryFin.FinallyBlock.Statements, inFieldInit);

            case TryCatchFinallyStatementNode tryCF:
                if (WalkBlock(tryCF.TryBlock.Statements, inFieldInit)) return true;
                if (WalkBlock(tryCF.CatchBlock.Statements, inFieldInit)) return true;
                return WalkBlock(tryCF.FinallyBlock.Statements, inFieldInit);

            case BlockStatementNode block:
                return WalkBlock(block.Statements, inFieldInit);

            case SwitchStatementNode sw:
                foreach (var c in sw.Cases)
                {
                    if (c.Test is not null && WalkExpression(c.Test, inFieldInit))
                        return true;
                    if (WalkBlock(c.Consequent, inFieldInit))
                        return true;
                }
                return false;

            case LabeledStatementNode labeled:
                return WalkStatement(labeled.Body, inFieldInit);
        }
        return false;
    }

    private static bool WalkBlock(IReadOnlyList<StatementNode> statements, bool inFieldInit)
    {
        foreach (var s in statements)
        {
            if (WalkStatement(s, inFieldInit))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check class members for early errors (ECMA-262 15.7.10).
    /// </summary>
    private static bool WalkClassMembers(IReadOnlyList<ClassMemberNode> members)
    {
        foreach (var member in members)
        {
            bool isField = member.Kind == ClassMemberKind.Field;

            if (member.Function is FunctionExpressionNode func)
            {
                // Walk the body with the field-init context flag
                if (WalkBlock(func.Body.Statements,
                        inFieldInit: isField))
                    return true;
            }
            else if (member.Function is ArrowFunctionExpressionNode arrow)
            {
                // Arrow function bodies inherit the outer context
                if (WalkArrowBody(arrow, inFieldInit: isField))
                    return true;
            }
            // Other member function types: constructor, method, getter, setter
            // These are always FunctionExpressionNode, not ArrowFunctionExpressionNode
        }
        return false;
    }

    private static bool WalkArrowBody(ArrowFunctionExpressionNode arrow, bool inFieldInit)
    {
        if (arrow.BlockBody is not null)
            return WalkBlock(arrow.BlockBody.Statements, inFieldInit);
        if (arrow.ExpressionBody is not null)
            return WalkExpression(arrow.ExpressionBody, inFieldInit);
        return false;
    }

    /// <summary>
    /// Walk an expression subtree. Returns true if a class-field early error
    /// violation is found.
    /// </summary>
    private static bool WalkExpression(ExpressionNode expression, bool inFieldInit)
    {
        switch (expression)
        {
            // ECMA-262 15.7.10: super cannot appear in class field initializers.
            case SuperExpressionNode:
                if (inFieldInit)
                    throw new JsParserException(
                        "super cannot be used in class field initializers.");
                return inFieldInit;

            // arguments cannot appear in class field initializers.
            case IdentifierExpressionNode id when id.Name == "arguments":
                if (inFieldInit)
                    throw new JsParserException(
                        "arguments cannot be used in class field initializers.");
                return inFieldInit;

            // new.target cannot appear in class field initializers.
            case NewTargetExpressionNode:
                if (inFieldInit)
                    throw new JsParserException(
                        "new.target cannot be used in class field initializers.");
                return inFieldInit;

            // Class expressions: walk their members for field errors
            case ClassExpressionNode classExpr:
                return WalkClassMembers(classExpr.Members);

            // Function boundaries reset the field-init context for non-arrow functions.
            case FunctionExpressionNode func:
                // Regular functions, generators, async functions, methods
                // have their own scope — super/arguments inside them don't
                // inherit the enclosing field-init context.
                if (!func.IsMethod && func.Name is not null)
                {
                    // Named function expression: new scope
                    return false;
                }
                // For methods and anonymous functions inside class elements,
                // the context is already determined by WalkClassMembers
                return WalkBlock(func.Body.Statements, inFieldInit: false);

            // Arrow functions inherit the enclosing context
            case ArrowFunctionExpressionNode arrow:
                return WalkArrowBody(arrow, inFieldInit);

            // Walk sub-expressions
            case BinaryExpressionNode bin:
                return WalkExpression(bin.Left, inFieldInit) ||
                       WalkExpression(bin.Right, inFieldInit);

            case UnaryExpressionNode unary:
                return WalkExpression(unary.Operand, inFieldInit);

            case AssignmentExpressionNode assign:
                return WalkExpression(assign.Left, inFieldInit) ||
                       WalkExpression(assign.Right, inFieldInit);

            case CallExpressionNode call:
                if (WalkExpression(call.Callee, inFieldInit)) return true;
                foreach (var arg in call.Arguments)
                    if (WalkExpression(arg, inFieldInit)) return true;
                return false;

            case NewExpressionNode newExpr:
                if (WalkExpression(newExpr.Callee, inFieldInit)) return true;
                foreach (var arg in newExpr.Arguments)
                    if (WalkExpression(arg, inFieldInit)) return true;
                return false;

            case MemberExpressionNode member:
                return WalkExpression(member.Object, inFieldInit);

            case OptionalMemberExpressionNode optMember:
                return WalkExpression(optMember.Object, inFieldInit);

            case OptionalCallExpressionNode optCall:
                if (WalkExpression(optCall.Callee, inFieldInit)) return true;
                foreach (var arg in optCall.Arguments)
                    if (WalkExpression(arg, inFieldInit)) return true;
                return false;

            case ConditionalExpressionNode cond:
                return WalkExpression(cond.Test, inFieldInit) ||
                       WalkExpression(cond.Consequent, inFieldInit) ||
                       WalkExpression(cond.Alternate, inFieldInit);

            case ObjectLiteralExpressionNode obj:
                foreach (var prop in obj.Properties)
                    if (WalkExpression(prop.Value, inFieldInit)) return true;
                return false;

            case ArrayLiteralExpressionNode arr:
                foreach (var elem in arr.Elements)
                {
                    if (elem is not ElisionExpressionNode &&
                        WalkExpression(elem, inFieldInit)) return true;
                }
                return false;

            case ParenthesizedExpressionNode paren:
                return WalkExpression(paren.Expression, inFieldInit);

            case SpreadElementExpressionNode spread:
                return WalkExpression(spread.Argument, inFieldInit);

            case TemplateLiteralExpressionNode template:
                foreach (var sub in template.Expressions)
                    if (WalkExpression(sub, inFieldInit)) return true;
                return false;

            case TaggedTemplateExpressionNode tagged:
                return WalkExpression(tagged.Tag, inFieldInit) ||
                       WalkExpression(tagged.Template, inFieldInit);

            case ImportCallExpressionNode import:
                return WalkExpression(import.Specifier, inFieldInit);
        }
        return false;
    }
}
