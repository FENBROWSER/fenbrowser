using FenBrowser.Js.Ast;
using FenBrowser.Js.Diagnostics;

namespace FenBrowser.Js.AstValidation;

public sealed class AstValidator
{
    public void Validate(ProgramNode program, DiagnosticBag diagnostics)
    {
        foreach (var statement in program.Body)
        {
            ValidateStatement(statement, diagnostics);
        }
    }

    private static void ValidateStatement(StatementNode statement, DiagnosticBag diagnostics)
    {
        if (statement is ExpressionStatementNode expr)
        {
            ValidateExpression(expr.Expression, diagnostics);
        }
    }

    private static void ValidateExpression(ExpressionNode expression, DiagnosticBag diagnostics)
    {
        switch (expression)
        {
            case BinaryExpressionNode bin:
                ValidateExpression(bin.Left, diagnostics);
                ValidateExpression(bin.Right, diagnostics);
                break;
            case ParenthesizedExpressionNode paren:
                ValidateExpression(paren.Expression, diagnostics);
                break;
            default:
                break;
        }
    }
}
