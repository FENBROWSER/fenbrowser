using FenBrowser.Js.Ast;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Parser;

public sealed class JsParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _index;

    private JsParser(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens;
    }

    public static ProgramNode ParseScript(SourceText source)
    {
        var tokens = new JsLexer(source).LexAll();
        var parser = new JsParser(tokens);
        return parser.ParseProgram(ProgramKind.Script);
    }

    public static ProgramNode ParseModule(SourceText source)
    {
        var tokens = new JsLexer(source).LexAll();
        var parser = new JsParser(tokens);
        return parser.ParseProgram(ProgramKind.Module);
    }

    private ProgramNode ParseProgram(ProgramKind kind)
    {
        var statements = new List<StatementNode>();
        var start = Current().Span;

        while (!Is(TokenKind.EndOfFile))
        {
            statements.Add(ParseStatement());
        }

        var end = Current().Span;
        var span = new SourceSpan(start.Start, Math.Max(0, end.Start - start.Start), start.Line, start.Column);
        return new ProgramNode(kind, statements, span);
    }

    private StatementNode ParseStatement()
    {
        var expression = ParseExpression(0);
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new ExpressionStatementNode(expression, expression.Span);
    }

    private ExpressionNode ParseExpression(int minBindingPower)
    {
        var left = ParsePrefix();

        while (true)
        {
            if (!TryGetInfixBindingPower(Current(), out var op, out var leftBp, out var rightBp) || leftBp < minBindingPower)
            {
                break;
            }

            var opToken = Advance();
            var right = ParseExpression(rightBp);
            var span = MergeSpan(left.Span, right.Span);
            left = new BinaryExpressionNode(opToken.Text, left, right, span);
        }

        return left;
    }

    private ExpressionNode ParsePrefix()
    {
        var token = Current();
        if (token.Kind == TokenKind.Identifier)
        {
            Advance();
            return new IdentifierExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Number)
        {
            Advance();
            if (!double.TryParse(token.Text, out var value))
            {
                throw new JsParserException($"Invalid numeric literal '{token.Text}'.");
            }

            return new NumericLiteralExpressionNode(value, token.Text, token.Span);
        }

        if (token.Kind == TokenKind.String)
        {
            Advance();
            var value = token.Text.Length >= 2 ? token.Text[1..^1] : string.Empty;
            return new StringLiteralExpressionNode(value, token.Text, token.Span);
        }

        if (IsPunctuator("("))
        {
            var open = Advance();
            var expression = ParseExpression(0);
            ExpectPunctuator(")");
            var span = MergeSpan(open.Span, expression.Span);
            return new ParenthesizedExpressionNode(expression, span);
        }

        throw new JsParserException($"Unexpected token '{token.Text}' ({token.Kind}).");
    }

    private bool TryGetInfixBindingPower(Token token, out string op, out int leftBindingPower, out int rightBindingPower)
    {
        op = token.Text;
        leftBindingPower = 0;
        rightBindingPower = 0;

        if (token.Kind != TokenKind.Punctuator)
        {
            return false;
        }

        switch (token.Text)
        {
            case "*":
            case "/":
                leftBindingPower = 30;
                rightBindingPower = 31;
                return true;
            case "+":
            case "-":
                leftBindingPower = 20;
                rightBindingPower = 21;
                return true;
            default:
                return false;
        }
    }

    private Token Current() => _tokens[Math.Min(_index, _tokens.Count - 1)];

    private Token Advance()
    {
        var token = Current();
        _index = Math.Min(_index + 1, _tokens.Count - 1);
        return token;
    }

    private bool Is(TokenKind kind) => Current().Kind == kind;

    private bool IsPunctuator(string text) => Current().Kind == TokenKind.Punctuator && Current().Text == text;

    private void ExpectPunctuator(string text)
    {
        if (!IsPunctuator(text))
        {
            throw new JsParserException($"Expected '{text}', found '{Current().Text}'.");
        }

        Advance();
    }

    private static SourceSpan MergeSpan(SourceSpan start, SourceSpan end)
    {
        var length = Math.Max(0, (end.Start + end.Length) - start.Start);
        return new SourceSpan(start.Start, length, start.Line, start.Column);
    }
}
