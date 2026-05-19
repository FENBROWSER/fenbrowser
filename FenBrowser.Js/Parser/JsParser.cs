using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Source;
using System.Globalization;

namespace FenBrowser.Js.Parser;

public sealed class JsParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _index;
    private int _syntheticBindingCounter;

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
        if (Current().Kind == TokenKind.Identifier && PeekIsPunctuator(1, ":"))
        {
            var labelToken = Advance();
            ExpectPunctuator(":");
            var body = ParseStatement();
            return new LabeledStatementNode(labelToken.Text, body, MergeSpan(labelToken.Span, body.Span));
        }

        if (IsPunctuator(";"))
        {
            var semi = Advance();
            return new EmptyStatementNode(semi.Span);
        }

        if (IsPunctuator("{"))
        {
            return ParseBlockStatement();
        }

        if (Current().Kind == TokenKind.Keyword)
        {
            switch (Current().Text)
            {
                case "import":
                case "export":
                    throw new UnsupportedFeatureException(Current().Text, FeatureSupportLevel.ParserOnly, Current().Span);
                case "class":
                    return ParseClassDeclaration();
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
                case "switch":
                    return ParseSwitchStatement();
                case "async":
                    if (PeekKeyword(1, "function"))
                    {
                        return ParseFunctionDeclaration();
                    }

                    break;
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
            var id = ParseBindingIdentifierOrPattern();
            ExpressionNode? initializer = null;
            if (IsPunctuator("="))
            {
                Advance();
                initializer = ParseExpression(2);
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

    private Token ParseBindingIdentifierOrPattern()
    {
        if (IsIdentifierLike(Current()))
        {
            return Advance();
        }

        if (IsPunctuator("[") || IsPunctuator("{"))
        {
            var start = Current().Span;
            ConsumeBindingPatternTarget();
            var name = $"__pattern{_syntheticBindingCounter++}";
            return new Token(TokenKind.Identifier, name, start);
        }

        throw new JsParserException($"Expected identifier, found '{Current().Text}'.");
    }

    private void ConsumeBindingPatternTarget()
    {
        var open = Current();
        if (!(open.Kind == TokenKind.Punctuator && (open.Text == "[" || open.Text == "{")))
        {
            throw new JsParserException($"Expected binding pattern, found '{open.Text}'.");
        }

        var openText = open.Text;
        var closeText = openText == "[" ? "]" : "}";
        _ = Advance();
        var depth = 1;

        while (!Is(TokenKind.EndOfFile) && depth > 0)
        {
            var token = Advance();
            if (token.Kind != TokenKind.Punctuator)
            {
                continue;
            }

            if (token.Text == openText)
            {
                depth++;
            }
            else if (token.Text == closeText)
            {
                depth--;
            }
        }

        if (depth != 0)
        {
            throw new JsParserException("Unterminated binding pattern target.");
        }
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

    private StatementNode ParseForStatement()
    {
        var start = Advance(); // for
        if (Current().Kind == TokenKind.Keyword && Current().Text == "await")
        {
            Advance();
        }

        ExpectPunctuator("(");

        StatementNode? initializer = null;
        var requireInitializerSemicolon = false;
        var initializerIsDeclaration = false;
        if (!IsPunctuator(";"))
        {
            if (Current().Kind == TokenKind.Keyword && (Current().Text == "let" || Current().Text == "const" || Current().Text == "var"))
            {
                initializer = ParseVariableDeclarationStatement();
                initializerIsDeclaration = true;
            }
            else
            {
                var initExpr = ParseExpression(0);
                initializer = new ExpressionStatementNode(initExpr, initExpr.Span);
                requireInitializerSemicolon = true;
            }
        }
        else
        {
            ExpectPunctuator(";");
        }

        if (Current().Kind == TokenKind.Keyword && Current().Text == "in")
        {
            if (initializer is null)
            {
                throw new JsParserException("for-in requires an initializer target.");
            }

            Advance(); // in
            var iterable = ParseExpression(0);
            ExpectPunctuator(")");
            var forInBody = ParseStatement();
            return new ForInStatementNode(initializer, iterable, forInBody, MergeSpan(start.Span, forInBody.Span));
        }

        if (Current().Kind == TokenKind.Keyword && Current().Text == "of")
        {
            if (initializer is null)
            {
                throw new JsParserException("for-of requires an initializer target.");
            }

            Advance(); // of
            var iterable = ParseExpression(0);
            ExpectPunctuator(")");
            var forOfBody = ParseStatement();
            return new ForOfStatementNode(initializer, iterable, forOfBody, MergeSpan(start.Span, forOfBody.Span));
        }

        if (requireInitializerSemicolon)
        {
            if (IsPunctuator(";"))
            {
                Advance();
            }
            else if (IsPunctuator(")"))
            {
                Advance();
                var bodyWithImplicitlyEmptyRemainder = ParseStatement();
                return new ForStatementNode(initializer, null, null, bodyWithImplicitlyEmptyRemainder, MergeSpan(start.Span, bodyWithImplicitlyEmptyRemainder.Span));
            }
            else
            {
                ExpectPunctuator(";");
            }
        }
        else if (initializer is not null && !initializerIsDeclaration && IsPunctuator(";"))
        {
            Advance();
        }

        if (IsPunctuator(")"))
        {
            Advance();
            var bodyWithImplicitlyEmptyRemainder = ParseStatement();
            return new ForStatementNode(initializer, null, null, bodyWithImplicitlyEmptyRemainder, MergeSpan(start.Span, bodyWithImplicitlyEmptyRemainder.Span));
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
        Token start;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "async")
        {
            start = Advance(); // async
            if (!(Current().Kind == TokenKind.Keyword && Current().Text == "function"))
            {
                throw new JsParserException($"Expected 'function', found '{Current().Text}'.");
            }

            _ = Advance(); // function
        }
        else
        {
            start = Advance(); // function
        }

        if (IsPunctuator("*"))
        {
            Advance();
        }

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

    private StatementNode ParseTryCatchStatement()
    {
        var start = Advance(); // try
        var tryBlock = ParseBlockStatement();
        var hasCatch = Current().Kind == TokenKind.Keyword && Current().Text == "catch";
        var hasFinally = Current().Kind == TokenKind.Keyword && Current().Text == "finally";

        if (!hasCatch && !hasFinally)
        {
            throw new JsParserException("Expected 'catch' or 'finally' after try block.");
        }

        if (hasCatch)
        {
            Advance(); // catch
            ExpectPunctuator("(");
            string catchIdentifier;
            if (Current().Kind == TokenKind.Identifier)
            {
                catchIdentifier = Advance().Text;
            }
            else
            {
                _ = ParseExpression(0);
                catchIdentifier = "<pattern>";
            }

            ExpectPunctuator(")");
            var catchBlock = ParseBlockStatement();
            if (Current().Kind == TokenKind.Keyword && Current().Text == "finally")
            {
                Advance(); // finally
                var finallyBlock = ParseBlockStatement();
                return new TryCatchFinallyStatementNode(tryBlock, catchIdentifier, catchBlock, finallyBlock, MergeSpan(start.Span, finallyBlock.Span));
            }

            return new TryCatchStatementNode(tryBlock, catchIdentifier, catchBlock, MergeSpan(start.Span, catchBlock.Span));
        }

        Advance(); // finally
        var finallyOnlyBlock = ParseBlockStatement();
        return new TryFinallyStatementNode(tryBlock, finallyOnlyBlock, MergeSpan(start.Span, finallyOnlyBlock.Span));
    }

    private ClassDeclarationNode ParseClassDeclaration()
    {
        var start = Advance(); // class
        var name = ExpectIdentifier();

        ExpressionNode? baseClass = null;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "extends")
        {
            Advance();
            baseClass = ParseExpression(0);
        }

        ExpectPunctuator("{");
        var depth = 1;
        Token close = Previous();
        while (!Is(TokenKind.EndOfFile) && depth > 0)
        {
            var token = Advance();
            if (token.Kind != TokenKind.Punctuator)
            {
                continue;
            }

            if (token.Text == "{")
            {
                depth++;
            }
            else if (token.Text == "}")
            {
                depth--;
                close = token;
            }
        }

        if (depth != 0)
        {
            throw new JsParserException("Unterminated class body.");
        }

        return new ClassDeclarationNode(name.Text, baseClass, MergeSpan(start.Span, close.Span));
    }

    private ClassExpressionNode ParseClassExpression()
    {
        var start = Advance(); // class
        string? name = null;
        if (Current().Kind == TokenKind.Identifier)
        {
            name = Advance().Text;
        }

        ExpressionNode? baseClass = null;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "extends")
        {
            Advance();
            baseClass = ParseExpression(0);
        }

        ExpectPunctuator("{");
        var depth = 1;
        Token close = Previous();
        while (!Is(TokenKind.EndOfFile) && depth > 0)
        {
            var token = Advance();
            if (token.Kind != TokenKind.Punctuator)
            {
                continue;
            }

            if (token.Text == "{")
            {
                depth++;
            }
            else if (token.Text == "}")
            {
                depth--;
                close = token;
            }
        }

        if (depth != 0)
        {
            throw new JsParserException("Unterminated class body.");
        }

        return new ClassExpressionNode(name, baseClass, MergeSpan(start.Span, close.Span));
    }

    private SwitchStatementNode ParseSwitchStatement()
    {
        var start = Advance(); // switch
        ExpectPunctuator("(");
        var discriminant = ParseExpression(0);
        ExpectPunctuator(")");
        ExpectPunctuator("{");

        var cases = new List<SwitchCaseNode>();
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("}"))
        {
            ExpressionNode? test = null;
            var caseStart = Current().Span;
            if (Current().Kind == TokenKind.Keyword && Current().Text == "case")
            {
                Advance();
                test = ParseExpression(0);
                ExpectPunctuator(":");
            }
            else if (Current().Kind == TokenKind.Keyword && Current().Text == "default")
            {
                Advance();
                ExpectPunctuator(":");
            }
            else
            {
                throw new JsParserException($"Expected 'case' or 'default', found '{Current().Text}'.");
            }

            var consequent = new List<StatementNode>();
            while (!Is(TokenKind.EndOfFile) &&
                   !IsPunctuator("}") &&
                   !(Current().Kind == TokenKind.Keyword && (Current().Text == "case" || Current().Text == "default")))
            {
                consequent.Add(ParseStatement());
            }

            var caseEnd = consequent.Count > 0 ? consequent[^1].Span : caseStart;
            cases.Add(new SwitchCaseNode(test, consequent, MergeSpan(caseStart, caseEnd)));
        }

        ExpectPunctuator("}");
        var close = Previous();
        return new SwitchStatementNode(discriminant, cases, MergeSpan(start.Span, close.Span));
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
            var rest = IsPunctuator("...");
            if (rest)
            {
                Advance();
            }

            var identifier = ExpectIdentifier().Text;
            parameters.Add(identifier);

            if (IsPunctuator("="))
            {
                Advance();
                _ = ParseExpression(2);
            }

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
                var property = ExpectPropertyNameAfterDot();
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

            if ((IsPunctuator("++") || IsPunctuator("--")) && minBindingPower <= 34)
            {
                if (!IsUpdateTarget(left))
                {
                    throw new JsParserException("Invalid update expression target.");
                }

                var updateToken = Advance();
                left = BuildUpdateAssignment(left, updateToken.Text, updateToken.Span, isPostfix: true);
                continue;
            }

            if (IsAssignmentOperator(Current()) && minBindingPower <= 9)
            {
                var op = Advance().Text;
                var assignmentRight = ParseExpression(10);
                var rhs = BuildAssignmentRight(left, op, assignmentRight);
                left = new AssignmentExpressionNode(left, rhs, MergeSpan(left.Span, assignmentRight.Span));
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
        if (token.Kind == TokenKind.Punctuator && (token.Text == "++" || token.Text == "--"))
        {
            var op = Advance();
            var target = ParseExpression(40);
            if (!IsUpdateTarget(target))
            {
                throw new JsParserException("Invalid update expression target.");
            }

            return BuildUpdateAssignment(target, op.Text, op.Span, isPostfix: false);
        }

        if (token.Kind == TokenKind.Punctuator && (token.Text == "!" || token.Text == "-" || token.Text == "+"))
        {
            var op = Advance();
            var operand = ParseExpression(40);
            return new UnaryExpressionNode(op.Text, operand, MergeSpan(op.Span, operand.Span));
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "typeof")
        {
            var op = Advance();
            var operand = ParseExpression(40);
            return new UnaryExpressionNode(op.Text, operand, MergeSpan(op.Span, operand.Span));
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "yield")
        {
            var op = Advance();
            var delegated = false;
            if (IsPunctuator("*"))
            {
                delegated = true;
                Advance();
            }

            if (IsPunctuator(";") || IsPunctuator("}") || Is(TokenKind.EndOfFile))
            {
                return new UnaryExpressionNode(op.Text, new IdentifierExpressionNode("undefined", op.Span), op.Span);
            }

            var operand = ParseExpression(40);
            var opText = delegated ? "yield*" : op.Text;
            return new UnaryExpressionNode(opText, operand, MergeSpan(op.Span, operand.Span));
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "function")
        {
            return ParseFunctionExpression();
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "async" && PeekKeyword(1, "function"))
        {
            return ParseFunctionExpression();
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "new")
        {
            return ParseNewExpression();
        }

        if (token.Kind == TokenKind.Keyword && (token.Text == "async" || token.Text == "await"))
        {
            Advance();
            return new IdentifierExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "class")
        {
            return ParseClassExpression();
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "this")
        {
            Advance();
            return new ThisExpressionNode(token.Span);
        }

        if (token.Kind == TokenKind.Identifier)
        {
            Advance();
            return new IdentifierExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Number)
        {
            Advance();
            if (!TryParseNumberLiteral(token.Text, out var value))
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

        if (token.Kind == TokenKind.RegularExpression)
        {
            Advance();
            return new RegexLiteralExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Template)
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

    private static bool TryParseNumberLiteral(string text, out double value)
    {
        if (text.EndsWith('n'))
        {
            text = text[..^1];
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length > 2 && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                value = hex;
                return true;
            }

            value = 0;
            return false;
        }

        if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length > 2 && TryParseRadix(text[2..], 8, out var oct))
            {
                value = oct;
                return true;
            }

            value = 0;
            return false;
        }

        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length > 2 && TryParseRadix(text[2..], 2, out var bin))
            {
                value = bin;
                return true;
            }

            value = 0;
            return false;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseRadix(string text, int radix, out long value)
    {
        value = 0;
        foreach (var ch in text)
        {
            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => 10 + (ch - 'a'),
                >= 'A' and <= 'F' => 10 + (ch - 'A'),
                _ => -1
            };

            if (digit < 0 || digit >= radix)
            {
                return false;
            }

            checked
            {
                value = (value * radix) + digit;
            }
        }

        return true;
    }

    private FunctionExpressionNode ParseFunctionExpression()
    {
        Token start;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "async")
        {
            start = Advance(); // async
            if (!(Current().Kind == TokenKind.Keyword && Current().Text == "function"))
            {
                throw new JsParserException($"Expected 'function', found '{Current().Text}'.");
            }

            _ = Advance(); // function
        }
        else
        {
            start = Advance(); // function
        }

        if (IsPunctuator("*"))
        {
            Advance();
        }

        string? name = null;
        if (Current().Kind == TokenKind.Identifier)
        {
            name = Advance().Text;
        }

        var parameters = ParseParameterList();
        var body = ParseBlockStatement();
        return new FunctionExpressionNode(name, parameters, body, MergeSpan(start.Span, body.Span));
    }

    private NewExpressionNode ParseNewExpression()
    {
        var start = Advance(); // new
        var callee = ParsePrefix();
        callee = ParsePostfix(callee, minBindingPower: 35, allowCall: false);

        IReadOnlyList<ExpressionNode> args = Array.Empty<ExpressionNode>();
        if (IsPunctuator("("))
        {
            args = ParseCallArguments();
        }

        var end = args.Count > 0 ? Previous().Span : callee.Span;
        return new NewExpressionNode(callee, args, MergeSpan(start.Span, end));
    }

    private ObjectLiteralExpressionNode ParseObjectLiteral()
    {
        var open = Advance(); // {
        var properties = new List<ObjectPropertyNode>();
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("}"))
        {
            var keyToken = Current();

            if (IsAccessorPropertyStart())
            {
                var accessorKind = Advance();
                string? accessorKey = null;
                ExpressionNode? accessorComputedKey = null;
                var accessorIsComputed = false;
                var accessorKeyToken = Current();
                if (IsPunctuator("["))
                {
                    Advance(); // [
                    accessorComputedKey = ParseExpression(0);
                    ExpectPunctuator("]");
                    accessorIsComputed = true;
                }
                else if (accessorKeyToken.Kind == TokenKind.Identifier || accessorKeyToken.Kind == TokenKind.Keyword)
                {
                    accessorKey = Advance().Text;
                }
                else if (accessorKeyToken.Kind == TokenKind.String)
                {
                    var raw = Advance().Text;
                    accessorKey = raw.Length >= 2 ? raw[1..^1] : string.Empty;
                }
                else
                {
                    throw new JsParserException($"Expected object property key, found '{accessorKeyToken.Text}'.");
                }

                var parameters = ParseParameterList();
                var body = ParseBlockStatement();
                var accessorFnName = accessorKey ?? accessorKind.Text;
                var accessorFn = new FunctionExpressionNode(accessorFnName, parameters, body, MergeSpan(accessorKind.Span, body.Span));
                properties.Add(new ObjectPropertyNode(accessorKey, accessorComputedKey, accessorIsComputed, accessorFn, accessorFn.Span));
                if (IsPunctuator(","))
                {
                    Advance();
                    continue;
                }

                break;
            }

            if ((keyToken.Kind == TokenKind.Identifier || keyToken.Kind == TokenKind.Keyword) &&
                keyToken.Text == "async" &&
                IsAsyncMethodPropertyStart())
            {
                var asyncStart = Advance(); // async
                string? methodKey = null;
                ExpressionNode? methodComputedKey = null;
                var methodIsComputed = false;
                var methodKeyToken = Current();
                if (IsPunctuator("["))
                {
                    Advance(); // [
                    methodComputedKey = ParseExpression(0);
                    ExpectPunctuator("]");
                    methodIsComputed = true;
                }
                else if (methodKeyToken.Kind == TokenKind.Identifier || methodKeyToken.Kind == TokenKind.Keyword)
                {
                    methodKey = Advance().Text;
                }
                else if (methodKeyToken.Kind == TokenKind.String)
                {
                    var raw = Advance().Text;
                    methodKey = raw.Length >= 2 ? raw[1..^1] : string.Empty;
                }
                else if (methodKeyToken.Kind == TokenKind.Number)
                {
                    methodKey = Advance().Text;
                }
                else
                {
                    throw new JsParserException($"Expected object property key, found '{methodKeyToken.Text}'.");
                }

                var parameters = ParseParameterList();
                var body = ParseBlockStatement();
                var methodFnName = methodKey ?? "async";
                var asyncMethodFn = new FunctionExpressionNode(methodFnName, parameters, body, MergeSpan(asyncStart.Span, body.Span));
                properties.Add(new ObjectPropertyNode(methodKey, methodComputedKey, methodIsComputed, asyncMethodFn, asyncMethodFn.Span));
                if (IsPunctuator(","))
                {
                    Advance();
                    continue;
                }

                break;
            }

            string? key = null;
            ExpressionNode? computedKey = null;
            var isComputed = false;
            if (IsPunctuator("["))
            {
                Advance(); // [
                computedKey = ParseExpression(0);
                ExpectPunctuator("]");
                isComputed = true;
            }
            else if (keyToken.Kind == TokenKind.Identifier || keyToken.Kind == TokenKind.Keyword)
            {
                key = Advance().Text;
            }
            else if (keyToken.Kind == TokenKind.String)
            {
                var raw = Advance().Text;
                key = raw.Length >= 2 ? raw[1..^1] : string.Empty;
            }
            else if (keyToken.Kind == TokenKind.Number)
            {
                key = Advance().Text;
            }
            else
            {
                throw new JsParserException($"Expected object property key, found '{keyToken.Text}'.");
            }

            ExpressionNode value;
            if (IsPunctuator("("))
            {
                var parameters = ParseParameterList();
                var body = ParseBlockStatement();
                value = new FunctionExpressionNode(key, parameters, body, MergeSpan(keyToken.Span, body.Span));
            }
            else if (IsPunctuator(":"))
            {
                Advance();
                value = ParseExpression(2);
            }
            else if (IsPunctuator("="))
            {
                Advance();
                value = ParseExpression(2);
            }
            else if (!isComputed && key is not null)
            {
                value = new IdentifierExpressionNode(key, keyToken.Span);
            }
            else
            {
                throw new JsParserException($"Expected ':' or '=' after object property key, found '{Current().Text}'.");
            }
            properties.Add(new ObjectPropertyNode(key, computedKey, isComputed, value, value.Span));
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
            if (IsPunctuator("..."))
            {
                var spread = Advance();
                var argument = ParseExpression(2);
                elements.Add(new SpreadElementExpressionNode(argument, MergeSpan(spread.Span, argument.Span)));
            }
            else
            {
                elements.Add(ParseExpression(2));
            }

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

        if (Current().Kind == TokenKind.Keyword && Current().Text == "async")
        {
            Advance(); // async

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
                var asyncParameters = new List<string>();
                var asyncValid = true;
                if (!IsPunctuator(")"))
                {
                    while (true)
                    {
                        if (!IsIdentifierLike(Current()))
                        {
                            asyncValid = false;
                            break;
                        }

                        asyncParameters.Add(Advance().Text);
                        if (IsPunctuator("="))
                        {
                            Advance();
                            _ = ParseExpression(2);
                        }

                        if (IsPunctuator(","))
                        {
                            Advance();
                            continue;
                        }

                        break;
                    }
                }

                if (!asyncValid || !IsPunctuator(")"))
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
                expression = ParseArrowFunctionBody(asyncParameters, _tokens[saved].Span);
                return true;
            }

            _index = saved;
        }

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
                    if (!IsIdentifierLike(Current()))
                    {
                        valid = false;
                        break;
                    }

                    parameters.Add(Advance().Text);
                    if (IsPunctuator("="))
                    {
                        Advance();
                        _ = ParseExpression(2);
                    }

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
            args.Add(ParseExpression(2));
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

    private ExpressionNode ParsePostfix(ExpressionNode left, int minBindingPower, bool allowCall = true)
    {
        while (true)
        {
            if (IsPunctuator("."))
            {
                Advance();
                var property = ExpectPropertyNameAfterDot();
                left = new MemberExpressionNode(left, property.Text, Computed: false, PropertyExpression: null, MergeSpan(left.Span, property.Span));
                continue;
            }

            if (IsPunctuator("["))
            {
                Advance();
                var propExpr = ParseExpression(0);
                ExpectPunctuator("]");
                var close = Previous();
                left = new MemberExpressionNode(left, string.Empty, Computed: true, PropertyExpression: propExpr, MergeSpan(left.Span, close.Span));
                continue;
            }

            if (allowCall && IsPunctuator("("))
            {
                var args = ParseCallArguments();
                var end = Previous();
                left = new CallExpressionNode(left, args, MergeSpan(left.Span, end.Span));
                continue;
            }

            if (!TryGetInfixBindingPower(Current(), out _, out var leftBp, out _) || leftBp < minBindingPower)
            {
                break;
            }

            break;
        }

        return left;
    }

    private bool TryGetInfixBindingPower(Token token, out string op, out int leftBindingPower, out int rightBindingPower)
    {
        op = token.Text;
        leftBindingPower = 0;
        rightBindingPower = 0;

        if (token.Kind == TokenKind.Keyword && (token.Text == "in" || token.Text == "instanceof"))
        {
            leftBindingPower = 15;
            rightBindingPower = 16;
            return true;
        }

        if (token.Kind != TokenKind.Punctuator)
        {
            return false;
        }

        switch (token.Text)
        {
            case ",":
                leftBindingPower = 1;
                rightBindingPower = 2;
                return true;
            case "||":
                leftBindingPower = 5;
                rightBindingPower = 6;
                return true;
            case "??":
                leftBindingPower = 6;
                rightBindingPower = 7;
                return true;
            case "&&":
                leftBindingPower = 7;
                rightBindingPower = 8;
                return true;
            case "==":
            case "!=":
            case "===":
            case "!==":
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
            case "%":
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

    private bool PeekKeyword(int offset, string text)
    {
        var idx = Math.Min(_index + offset, _tokens.Count - 1);
        var token = _tokens[idx];
        return token.Kind == TokenKind.Keyword && token.Text == text;
    }

    private Token ExpectIdentifier()
    {
        if (!IsIdentifierLike(Current()))
        {
            throw new JsParserException($"Expected identifier, found '{Current().Text}'.");
        }

        return Advance();
    }

    private Token ExpectPropertyNameAfterDot()
    {
        if (Current().Kind != TokenKind.Identifier && Current().Kind != TokenKind.Keyword)
        {
            throw new JsParserException($"Expected identifier, found '{Current().Text}'.");
        }

        return Advance();
    }

    private static bool IsIdentifierLike(Token token) =>
        token.Kind == TokenKind.Identifier ||
        (token.Kind == TokenKind.Keyword && (token.Text == "async" || token.Text == "await"));

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

    private static bool IsAssignmentOperator(Token token)
    {
        return token.Kind == TokenKind.Punctuator && token.Text is "=" or "+=" or "-=" or "*=" or "/=" or "&&=" or "||=" or "??=";
    }

    private static ExpressionNode BuildAssignmentRight(ExpressionNode left, string op, ExpressionNode right)
    {
        return op switch
        {
            "=" => right,
            "+=" => new BinaryExpressionNode("+", left, right, MergeSpan(left.Span, right.Span)),
            "-=" => new BinaryExpressionNode("-", left, right, MergeSpan(left.Span, right.Span)),
            "*=" => new BinaryExpressionNode("*", left, right, MergeSpan(left.Span, right.Span)),
            "/=" => new BinaryExpressionNode("/", left, right, MergeSpan(left.Span, right.Span)),
            "&&=" => new BinaryExpressionNode("&&", left, right, MergeSpan(left.Span, right.Span)),
            "||=" => new BinaryExpressionNode("||", left, right, MergeSpan(left.Span, right.Span)),
            "??=" => new BinaryExpressionNode("??", left, right, MergeSpan(left.Span, right.Span)),
            _ => throw new JsParserException($"Unsupported assignment operator '{op}'.")
        };
    }

    private static bool IsUpdateTarget(ExpressionNode node) =>
        node is IdentifierExpressionNode or MemberExpressionNode or CallExpressionNode;

    private static AssignmentExpressionNode BuildUpdateAssignment(ExpressionNode target, string updateOp, SourceSpan opSpan, bool isPostfix)
    {
        var numeric = new NumericLiteralExpressionNode(1, "1", opSpan);
        var binaryOp = updateOp == "++" ? "+" : "-";
        var right = new BinaryExpressionNode(binaryOp, target, numeric, MergeSpan(target.Span, numeric.Span));
        var span = isPostfix ? MergeSpan(target.Span, opSpan) : MergeSpan(opSpan, target.Span);
        return new AssignmentExpressionNode(target, right, span);
    }

    private bool IsAccessorPropertyStart()
    {
        var current = Current();
        if (!((current.Kind == TokenKind.Identifier || current.Kind == TokenKind.Keyword) &&
              (current.Text == "get" || current.Text == "set")))
        {
            return false;
        }

        var nextIndex = Math.Min(_index + 1, _tokens.Count - 1);
        var next = _tokens[nextIndex];
        if (next.Kind == TokenKind.Punctuator && next.Text == "[")
        {
            var depth = 1;
            var scan = nextIndex + 1;
            while (scan < _tokens.Count)
            {
                var token = _tokens[scan];
                if (token.Kind == TokenKind.Punctuator)
                {
                    if (token.Text == "[")
                    {
                        depth++;
                    }
                    else if (token.Text == "]")
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                scan++;
            }

            if (depth != 0)
            {
                return false;
            }

            var afterBracket = Math.Min(scan + 1, _tokens.Count - 1);
            return _tokens[afterBracket].Kind == TokenKind.Punctuator && _tokens[afterBracket].Text == "(";
        }

        var isSimpleName = next.Kind == TokenKind.Identifier || next.Kind == TokenKind.Keyword || next.Kind == TokenKind.String;
        if (!isSimpleName)
        {
            return false;
        }

        var afterName = Math.Min(nextIndex + 1, _tokens.Count - 1);
        return _tokens[afterName].Kind == TokenKind.Punctuator && _tokens[afterName].Text == "(";
    }

    private bool IsAsyncMethodPropertyStart()
    {
        var current = Current();
        if (!(current.Kind == TokenKind.Identifier || current.Kind == TokenKind.Keyword) || current.Text != "async")
        {
            return false;
        }

        var nextIndex = Math.Min(_index + 1, _tokens.Count - 1);
        var next = _tokens[nextIndex];
        if (next.Kind == TokenKind.Punctuator && next.Text == "[")
        {
            var depth = 1;
            var scan = nextIndex + 1;
            while (scan < _tokens.Count)
            {
                var token = _tokens[scan];
                if (token.Kind == TokenKind.Punctuator)
                {
                    if (token.Text == "[")
                    {
                        depth++;
                    }
                    else if (token.Text == "]")
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                scan++;
            }

            if (depth != 0)
            {
                return false;
            }

            var afterBracket = Math.Min(scan + 1, _tokens.Count - 1);
            return _tokens[afterBracket].Kind == TokenKind.Punctuator && _tokens[afterBracket].Text == "(";
        }

        var isSimpleName = next.Kind == TokenKind.Identifier || next.Kind == TokenKind.Keyword || next.Kind == TokenKind.String || next.Kind == TokenKind.Number;
        if (!isSimpleName)
        {
            return false;
        }

        var afterName = Math.Min(nextIndex + 1, _tokens.Count - 1);
        return _tokens[afterName].Kind == TokenKind.Punctuator && _tokens[afterName].Text == "(";
    }
}
