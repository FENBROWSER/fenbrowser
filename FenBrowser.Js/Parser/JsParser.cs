using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
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
        if (IsPunctuator("{"))
        {
            return ParseBlockStatement();
        }

        if (Current().Kind == TokenKind.Keyword)
        {
            switch (Current().Text)
            {
                case "class":
                case "import":
                case "export":
                    throw new UnsupportedFeatureException(Current().Text, FeatureSupportLevel.ParserOnly, Current().Span);
                case "let":
                case "const":
                case "var":
                    return ParseVariableDeclarationStatement();
                case "if":
                    return ParseIfStatement();
                case "while":
                    return ParseWhileStatement();
                case "for":
                    return ParseForStatement();
                case "function":
                    return ParseFunctionDeclaration();
                case "return":
                    return ParseReturnStatement();
                case "throw":
                    return ParseThrowStatement();
                case "try":
                    return ParseTryCatchStatement();
                case "break":
                    return ParseBreakStatement();
                case "continue":
                    return ParseContinueStatement();
            }
        }

        var expression = ParseExpression(0);
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new ExpressionStatementNode(expression, expression.Span);
    }

    private BlockStatementNode ParseBlockStatement()
    {
        var open = Advance();
        var statements = new List<StatementNode>();
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("}"))
        {
            statements.Add(ParseStatement());
        }

        ExpectPunctuator("}");
        var close = Previous();
        return new BlockStatementNode(statements, MergeSpan(open.Span, close.Span));
    }

    private VariableDeclarationStatementNode ParseVariableDeclarationStatement()
    {
        var start = Advance(); // let|const|var
        var declarators = new List<VariableDeclaratorNode>();

        while (true)
        {
            var id = ExpectIdentifier();
            ExpressionNode? initializer = null;
            if (IsPunctuator("="))
            {
                Advance();
                initializer = ParseExpression(0);
            }

            var declaratorSpan = initializer is null ? id.Span : MergeSpan(id.Span, initializer.Span);
            declarators.Add(new VariableDeclaratorNode(id.Text, initializer, declaratorSpan));

            if (!IsPunctuator(","))
            {
                break;
            }

            Advance();
        }

        if (IsPunctuator(";"))
        {
            Advance();
        }

        var end = Previous();
        return new VariableDeclarationStatementNode(start.Text, declarators, MergeSpan(start.Span, end.Span));
    }

    private IfStatementNode ParseIfStatement()
    {
        var start = Advance(); // if
        ExpectPunctuator("(");
        var test = ParseExpression(0);
        ExpectPunctuator(")");
        var consequent = ParseStatement();
        StatementNode? alternate = null;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "else")
        {
            Advance();
            alternate = ParseStatement();
        }

        var endSpan = alternate?.Span ?? consequent.Span;
        return new IfStatementNode(test, consequent, alternate, MergeSpan(start.Span, endSpan));
    }

    private WhileStatementNode ParseWhileStatement()
    {
        var start = Advance(); // while
        ExpectPunctuator("(");
        var test = ParseExpression(0);
        ExpectPunctuator(")");
        var body = ParseStatement();
        return new WhileStatementNode(test, body, MergeSpan(start.Span, body.Span));
    }

    private ForStatementNode ParseForStatement()
    {
        var start = Advance(); // for
        ExpectPunctuator("(");

        StatementNode? initializer = null;
        if (!IsPunctuator(";"))
        {
            if (Current().Kind == TokenKind.Keyword && (Current().Text == "let" || Current().Text == "const" || Current().Text == "var"))
            {
                initializer = ParseVariableDeclarationStatement();
            }
            else
            {
                var initExpr = ParseExpression(0);
                initializer = new ExpressionStatementNode(initExpr, initExpr.Span);
                ExpectPunctuator(";");
            }
        }
        else
        {
            ExpectPunctuator(";");
        }

        ExpressionNode? test = null;
        if (!IsPunctuator(";"))
        {
            test = ParseExpression(0);
        }

        ExpectPunctuator(";");

        ExpressionNode? update = null;
        if (!IsPunctuator(")"))
        {
            update = ParseExpression(0);
        }

        ExpectPunctuator(")");
        var body = ParseStatement();
        return new ForStatementNode(initializer, test, update, body, MergeSpan(start.Span, body.Span));
    }

    private ReturnStatementNode ParseReturnStatement()
    {
        var start = Advance(); // return
        ExpressionNode? argument = null;
        if (!IsPunctuator(";") && !IsPunctuator("}") && !Is(TokenKind.EndOfFile))
        {
            argument = ParseExpression(0);
        }

        if (IsPunctuator(";"))
        {
            Advance();
        }

        var endSpan = argument?.Span ?? start.Span;
        return new ReturnStatementNode(argument, MergeSpan(start.Span, endSpan));
    }

    private FunctionDeclarationNode ParseFunctionDeclaration()
    {
        var start = Advance(); // function
        var name = ExpectIdentifier();
        var parameters = ParseParameterList();
        var body = ParseBlockStatement();
        return new FunctionDeclarationNode(name.Text, parameters, body, MergeSpan(start.Span, body.Span));
    }

    private ThrowStatementNode ParseThrowStatement()
    {
        var start = Advance(); // throw
        var argument = ParseExpression(0);
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new ThrowStatementNode(argument, MergeSpan(start.Span, argument.Span));
    }

    private TryCatchStatementNode ParseTryCatchStatement()
    {
        var start = Advance(); // try
        var tryBlock = ParseBlockStatement();
        if (!(Current().Kind == TokenKind.Keyword && Current().Text == "catch"))
        {
            throw new JsParserException("Expected 'catch' after try block.");
        }

        Advance(); // catch
        ExpectPunctuator("(");
        var catchId = ExpectIdentifier();
        ExpectPunctuator(")");
        var catchBlock = ParseBlockStatement();
        return new TryCatchStatementNode(tryBlock, catchId.Text, catchBlock, MergeSpan(start.Span, catchBlock.Span));
    }

    private BreakStatementNode ParseBreakStatement()
    {
        var token = Advance(); // break
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new BreakStatementNode(token.Span);
    }

    private ContinueStatementNode ParseContinueStatement()
    {
        var token = Advance(); // continue
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new ContinueStatementNode(token.Span);
    }

    private IReadOnlyList<string> ParseParameterList()
    {
        var parameters = new List<string>();
        ExpectPunctuator("(");
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator(")"))
        {
            parameters.Add(ExpectIdentifier().Text);
            if (IsPunctuator(","))
            {
                Advance();
                continue;
            }

            break;
        }

        ExpectPunctuator(")");
        return parameters;
    }

    private ExpressionNode ParseExpression(int minBindingPower)
    {
        if (TryParseArrowFunction(out var arrow))
        {
            return arrow;
        }

        var left = ParsePrefix();

        while (true)
        {
            if (IsPunctuator("?") && minBindingPower <= 4)
            {
                Advance();
                var consequent = ParseExpression(0);
                ExpectPunctuator(":");
                var alternate = ParseExpression(4);
                left = new ConditionalExpressionNode(left, consequent, alternate, MergeSpan(left.Span, alternate.Span));
                continue;
            }

            if (IsPunctuator("."))
            {
                Advance();
                var property = ExpectIdentifier();
                left = new MemberExpressionNode(left, property.Text, Computed: false, PropertyExpression: null, MergeSpan(left.Span, property.Span));
                continue;
            }

            if (IsPunctuator("["))
            {
                var open = Advance();
                var propExpr = ParseExpression(0);
                ExpectPunctuator("]");
                var close = Previous();
                left = new MemberExpressionNode(left, string.Empty, Computed: true, PropertyExpression: propExpr, MergeSpan(left.Span, close.Span));
                continue;
            }

            if (IsPunctuator("("))
            {
                var args = ParseCallArguments();
                var end = Previous();
                left = new CallExpressionNode(left, args, MergeSpan(left.Span, end.Span));
                continue;
            }

            if (IsPunctuator("=") && minBindingPower <= 9)
            {
                Advance();
                var assignmentRight = ParseExpression(10);
                left = new AssignmentExpressionNode(left, assignmentRight, MergeSpan(left.Span, assignmentRight.Span));
                continue;
            }

            if (!TryGetInfixBindingPower(Current(), out _, out var leftBp, out var rightBp) || leftBp < minBindingPower)
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
        if (token.Kind == TokenKind.Punctuator && (token.Text == "!" || token.Text == "-"))
        {
            var op = Advance();
            var operand = ParseExpression(40);
            return new UnaryExpressionNode(op.Text, operand, MergeSpan(op.Span, operand.Span));
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "function")
        {
            return ParseFunctionExpression();
        }

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

        if (IsPunctuator("{"))
        {
            return ParseObjectLiteral();
        }

        if (IsPunctuator("["))
        {
            return ParseArrayLiteral();
        }

        throw new JsParserException($"Unexpected token '{token.Text}' ({token.Kind}).");
    }

    private FunctionExpressionNode ParseFunctionExpression()
    {
        var start = Advance(); // function
        string? name = null;
        if (Current().Kind == TokenKind.Identifier)
        {
            name = Advance().Text;
        }

        var parameters = ParseParameterList();
        var body = ParseBlockStatement();
        return new FunctionExpressionNode(name, parameters, body, MergeSpan(start.Span, body.Span));
    }

    private ObjectLiteralExpressionNode ParseObjectLiteral()
    {
        var open = Advance(); // {
        var properties = new List<ObjectPropertyNode>();
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("}"))
        {
            var keyToken = Current();
            string key;
            if (keyToken.Kind == TokenKind.Identifier)
            {
                key = Advance().Text;
            }
            else if (keyToken.Kind == TokenKind.String)
            {
                var raw = Advance().Text;
                key = raw.Length >= 2 ? raw[1..^1] : string.Empty;
            }
            else
            {
                throw new JsParserException($"Expected object property key, found '{keyToken.Text}'.");
            }

            ExpectPunctuator(":");
            var value = ParseExpression(0);
            properties.Add(new ObjectPropertyNode(key, value, value.Span));
            if (IsPunctuator(","))
            {
                Advance();
                continue;
            }

            break;
        }

        ExpectPunctuator("}");
        var close = Previous();
        return new ObjectLiteralExpressionNode(properties, MergeSpan(open.Span, close.Span));
    }

    private ArrayLiteralExpressionNode ParseArrayLiteral()
    {
        var open = Advance(); // [
        var elements = new List<ExpressionNode>();
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("]"))
        {
            elements.Add(ParseExpression(0));
            if (IsPunctuator(","))
            {
                Advance();
                continue;
            }

            break;
        }

        ExpectPunctuator("]");
        var close = Previous();
        return new ArrayLiteralExpressionNode(elements, MergeSpan(open.Span, close.Span));
    }

    private bool TryParseArrowFunction(out ExpressionNode expression)
    {
        expression = null!;
        var saved = _index;

        if (Current().Kind == TokenKind.Identifier && PeekIsPunctuator(1, "=") && PeekIsPunctuator(2, ">"))
        {
            var parameter = Advance().Text;
            Advance(); // =
            Advance(); // >
            expression = ParseArrowFunctionBody(new[] { parameter }, _tokens[saved].Span);
            return true;
        }

        if (IsPunctuator("("))
        {
            Advance();
            var parameters = new List<string>();
            var valid = true;
            if (!IsPunctuator(")"))
            {
                while (true)
                {
                    if (Current().Kind != TokenKind.Identifier)
                    {
                        valid = false;
                        break;
                    }

                    parameters.Add(Advance().Text);
                    if (IsPunctuator(","))
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (!valid || !IsPunctuator(")"))
            {
                _index = saved;
                return false;
            }

            Advance(); // )
            if (!(IsPunctuator("=") && PeekIsPunctuator(1, ">")))
            {
                _index = saved;
                return false;
            }

            Advance(); // =
            Advance(); // >
            expression = ParseArrowFunctionBody(parameters, _tokens[saved].Span);
            return true;
        }

        return false;
    }

    private ArrowFunctionExpressionNode ParseArrowFunctionBody(IReadOnlyList<string> parameters, SourceSpan start)
    {
        if (IsPunctuator("{"))
        {
            var block = ParseBlockStatement();
            return new ArrowFunctionExpressionNode(parameters, block, null, MergeSpan(start, block.Span));
        }

        var bodyExpression = ParseExpression(0);
        return new ArrowFunctionExpressionNode(parameters, null, bodyExpression, MergeSpan(start, bodyExpression.Span));
    }

    private IReadOnlyList<ExpressionNode> ParseCallArguments()
    {
        var args = new List<ExpressionNode>();
        ExpectPunctuator("(");
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator(")"))
        {
            args.Add(ParseExpression(0));
            if (IsPunctuator(","))
            {
                Advance();
                continue;
            }

            break;
        }

        ExpectPunctuator(")");
        return args;
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
            case "||":
                leftBindingPower = 5;
                rightBindingPower = 6;
                return true;
            case "&&":
                leftBindingPower = 7;
                rightBindingPower = 8;
                return true;
            case "==":
            case "!=":
                leftBindingPower = 10;
                rightBindingPower = 11;
                return true;
            case "<":
            case ">":
            case "<=":
            case ">=":
                leftBindingPower = 15;
                rightBindingPower = 16;
                return true;
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

    private Token Previous() => _tokens[Math.Max(0, _index - 1)];

    private Token Advance()
    {
        var token = Current();
        _index = Math.Min(_index + 1, _tokens.Count - 1);
        return token;
    }

    private bool Is(TokenKind kind) => Current().Kind == kind;

    private bool IsPunctuator(string text) => Current().Kind == TokenKind.Punctuator && Current().Text == text;

    private bool PeekIsPunctuator(int offset, string text)
    {
        var idx = Math.Min(_index + offset, _tokens.Count - 1);
        var token = _tokens[idx];
        return token.Kind == TokenKind.Punctuator && token.Text == text;
    }

    private Token ExpectIdentifier()
    {
        if (Current().Kind != TokenKind.Identifier)
        {
            throw new JsParserException($"Expected identifier, found '{Current().Text}'.");
        }

        return Advance();
    }

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
