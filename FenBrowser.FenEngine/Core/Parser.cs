using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Core
{
    public enum Precedence
    {
        Lowest,
        Comma,       // ,
        Assignment,  // =
        Ternary,     // ? :  (conditional)
        LogicalOr,   // ||
        LogicalAnd,  // &&
        Equals,      // ==
        LessGreater, // > or <
        Sum,         // +
        Product,     // *
        Prefix,      // -X or !X
        Call,        // myFunction(X)
        Index        // array[index]
    }

    public class Parser
    {
        private readonly Lexer _lexer;
        private Token _curToken;
        private Token _peekToken;
        private readonly List<string> _errors = new List<string>();

        private readonly Dictionary<TokenType, Func<Expression>> _prefixParseFns;
        private readonly Dictionary<TokenType, Func<Expression, Expression>> _infixParseFns;

        private readonly Dictionary<TokenType, Precedence> _precedences = new Dictionary<TokenType, Precedence>
        {
            { TokenType.Comma, Precedence.Comma },
            { TokenType.Assign, Precedence.Assignment },
            { TokenType.PlusAssign, Precedence.Assignment },
            { TokenType.MinusAssign, Precedence.Assignment },
            { TokenType.MulAssign, Precedence.Assignment },
            { TokenType.DivAssign, Precedence.Assignment },
            { TokenType.Or, Precedence.LogicalOr },
            { TokenType.And, Precedence.LogicalAnd },
            { TokenType.Eq, Precedence.Equals },
            { TokenType.NotEq, Precedence.Equals },
            { TokenType.StrictEq, Precedence.Equals },
            { TokenType.StrictNotEq, Precedence.Equals },
            { TokenType.Lt, Precedence.LessGreater },
            { TokenType.Gt, Precedence.LessGreater },
            { TokenType.LtEq, Precedence.LessGreater },
            { TokenType.GtEq, Precedence.LessGreater },
            { TokenType.Instanceof, Precedence.LessGreater },
            { TokenType.In, Precedence.LessGreater },
            { TokenType.Plus, Precedence.Sum },
            { TokenType.Minus, Precedence.Sum },
            { TokenType.Slash, Precedence.Product },
            { TokenType.Asterisk, Precedence.Product },
            { TokenType.Percent, Precedence.Product },
            { TokenType.LParen, Precedence.Call },
            { TokenType.Dot, Precedence.Call },  // Member access has same precedence as function calls
            { TokenType.TemplateString, Precedence.Call },  // Tagged template literals tag`...`
            { TokenType.LBracket, Precedence.Index },
            { TokenType.Question, Precedence.Ternary },  // Ternary operator
            { TokenType.Arrow, Precedence.Assignment },  // Arrow functions
            { TokenType.Increment, Precedence.Prefix },
            { TokenType.Decrement, Precedence.Prefix },
        };

        public Parser(Lexer lexer)
        {
            _lexer = lexer;
            _prefixParseFns = new Dictionary<TokenType, Func<Expression>>();
            _infixParseFns = new Dictionary<TokenType, Func<Expression, Expression>>();

            // Register prefix parsers
            RegisterPrefix(TokenType.Identifier, ParseIdentifier);
            RegisterPrefix(TokenType.Number, ParseIntegerLiteral);
            RegisterPrefix(TokenType.String, ParseStringLiteral);
            RegisterPrefix(TokenType.TemplateString, ParseTemplateLiteral);  // Template literal `...`
            RegisterPrefix(TokenType.LBracket, ParseArrayLiteral); // NEW: Array
            RegisterPrefix(TokenType.LBrace, ParseObjectLiteral);  // NEW: Object
            RegisterPrefix(TokenType.Bang, ParsePrefixExpression);
            RegisterPrefix(TokenType.Minus, ParsePrefixExpression);
            RegisterPrefix(TokenType.True, ParseBoolean);
            RegisterPrefix(TokenType.False, ParseBoolean);
            RegisterPrefix(TokenType.LParen, ParseGroupedExpression);
            RegisterPrefix(TokenType.If, ParseIfExpression);
            RegisterPrefix(TokenType.Function, ParseFunctionLiteral);
            RegisterPrefix(TokenType.New, ParseNewExpression);
            RegisterPrefix(TokenType.Null, ParseNull);
            RegisterPrefix(TokenType.Undefined, ParseUndefined);
            RegisterPrefix(TokenType.This, ParseIdentifier); // Handle 'this' as identifier
            RegisterPrefix(TokenType.Typeof, ParsePrefixExpression);  // typeof x
            RegisterPrefix(TokenType.Void, ParsePrefixExpression);    // void x
            RegisterPrefix(TokenType.Delete, ParsePrefixExpression);  // delete x
            RegisterPrefix(TokenType.Increment, ParsePrefixIncrement); // ++x
            RegisterPrefix(TokenType.Decrement, ParsePrefixDecrement); // --x
            RegisterPrefix(TokenType.Slash, ParseRegexLiteral);        // /pattern/flags
            RegisterPrefix(TokenType.Regex, ParseRegexToken);          // Already lexed regex
            RegisterPrefix(TokenType.Semicolon, ParseEmptyExpression); // Empty expression (;)
            RegisterPrefix(TokenType.Colon, ParseEmptyExpression);     // Recovery for labeled statements
            RegisterPrefix(TokenType.Comma, ParseEmptyExpression);     // Recovery for trailing commas
            RegisterPrefix(TokenType.RBrace, ParseEmptyExpression);    // Recovery for empty blocks
            RegisterPrefix(TokenType.Yield, ParseYieldExpression);     // yield and yield*
            RegisterPrefix(TokenType.Await, ParseAwaitExpression);     // await ...
            RegisterPrefix(TokenType.Async, ParseAsyncPrefix);         // async ...
            RegisterPrefix(TokenType.RParen, ParseEmptyExpression);    // Recovery for empty parens
            RegisterPrefix(TokenType.RBracket, ParseEmptyExpression);  // Recovery for empty brackets
            RegisterPrefix(TokenType.PrivateIdentifier, ParsePrivateIdentifier); // #field (private class fields)
            RegisterPrefix(TokenType.Else, ParseEmptyExpression);      // Recovery for else without if
            RegisterPrefix(TokenType.Dot, ParseEmptyExpression);       // Recovery for leading dots
            RegisterPrefix(TokenType.Lt, ParseEmptyExpression);        // Recovery for bare < (like in HTML)
            RegisterPrefix(TokenType.Plus, ParsePrefixExpression);     // +x (unary plus)
            RegisterPrefix(TokenType.Asterisk, ParseEmptyExpression);  // Recovery for bare *

            // Register infix parsers
            RegisterInfix(TokenType.Plus, ParseInfixExpression);
            RegisterInfix(TokenType.Minus, ParseInfixExpression);
            RegisterInfix(TokenType.Slash, ParseInfixExpression);
            RegisterInfix(TokenType.Asterisk, ParseInfixExpression);
            RegisterInfix(TokenType.Eq, ParseInfixExpression);
            RegisterInfix(TokenType.NotEq, ParseInfixExpression);
            RegisterInfix(TokenType.Lt, ParseInfixExpression);
            RegisterInfix(TokenType.Gt, ParseInfixExpression);
            RegisterInfix(TokenType.LtEq, ParseInfixExpression);
            RegisterInfix(TokenType.GtEq, ParseInfixExpression);
            RegisterInfix(TokenType.LParen, ParseCallExpression);
            RegisterInfix(TokenType.Dot, ParseMemberExpression);  // NEW: dot operator
            RegisterInfix(TokenType.Assign, ParseAssignmentExpression);  // NEW: assignment
            RegisterInfix(TokenType.And, ParseInfixExpression);
            RegisterInfix(TokenType.Or, ParseInfixExpression);
            RegisterInfix(TokenType.LBracket, ParseIndexExpression);
            RegisterInfix(TokenType.Comma, ParseInfixExpression);
            RegisterInfix(TokenType.Question, ParseConditionalExpression);  // Ternary
            RegisterInfix(TokenType.Arrow, ParseArrowFunctionFromParams);   // Arrow functions
            RegisterInfix(TokenType.StrictEq, ParseInfixExpression);    // ===
            RegisterInfix(TokenType.StrictNotEq, ParseInfixExpression); // !==
            RegisterInfix(TokenType.Percent, ParseInfixExpression);     // %
            RegisterInfix(TokenType.PlusAssign, ParseCompoundAssignment);  // +=
            RegisterInfix(TokenType.MinusAssign, ParseCompoundAssignment); // -=
            RegisterInfix(TokenType.MulAssign, ParseCompoundAssignment);   // *=
            RegisterInfix(TokenType.DivAssign, ParseCompoundAssignment);   // /=
            RegisterInfix(TokenType.Instanceof, ParseInfixExpression);  // instanceof
            RegisterInfix(TokenType.In, ParseInfixExpression);          // in
            RegisterInfix(TokenType.Increment, ParsePostfixIncrement);  // x++
            RegisterInfix(TokenType.Decrement, ParsePostfixDecrement);  // x--
            RegisterInfix(TokenType.TemplateString, ParseTaggedTemplate);  // tag`template`

            // Read two tokens, so curToken and peekToken are both set
            NextToken();
            NextToken();
        }

        public List<string> Errors => _errors;

        private void NextToken()
        {
            _curToken = _peekToken;
            _peekToken = _lexer.NextToken();
        }

        private void RegisterPrefix(TokenType type, Func<Expression> fn)
        {
            _prefixParseFns[type] = fn;
        }

        private void RegisterInfix(TokenType type, Func<Expression, Expression> fn)
        {
            _infixParseFns[type] = fn;
        }

        public Program ParseProgram()
        {
            var program = new Program();

            while (_curToken.Type != TokenType.Eof)
            {
                var stmt = ParseStatement();
                if (stmt != null)
                {
                    program.Statements.Add(stmt);
                }
                NextToken();
            }

            return program;
        }

        private Statement ParseStatement()
        {
            switch (_curToken.Type)
            {
                case TokenType.Let:
                case TokenType.Var:
                case TokenType.Const:
                    return ParseLetStatement();
                case TokenType.Function:
                    // Handle function declaration
                    var funcExp = ParseFunctionLiteral() as FunctionLiteral;
                    if (funcExp != null && funcExp.Name != null)
                    {
                        // Treat function declaration as let name = function...
                        return new LetStatement 
                        { 
                            Token = funcExp.Token, 
                            Name = new Identifier(funcExp.Token, funcExp.Name), 
                            Value = funcExp 
                        };
                    }
                    return new ExpressionStatement { Expression = funcExp };
                case TokenType.Class:
                    return ParseClassStatement();
                case TokenType.Import:
                    return ParseImportDeclaration();
                case TokenType.Export:
                    return ParseExportDeclaration();
                case TokenType.Async:
                    return ParseAsyncFunctionDeclaration();
                case TokenType.Return:
                    return ParseReturnStatement();
                case TokenType.Try:
                    return ParseTryStatement();
                case TokenType.Throw:
                    return ParseThrowStatement();
                case TokenType.While:
                    return ParseWhileStatement();
                case TokenType.For:
                    return ParseForStatement();
                case TokenType.Switch:
                    return ParseSwitchStatement();
                case TokenType.Break:
                    return ParseBreakStatement();
                case TokenType.Continue:
                    return ParseContinueStatement();
                case TokenType.Do:
                    return ParseDoWhileStatement();
                case TokenType.Semicolon:
                    return null; // Ignore empty statements
                default:
                    return ParseExpressionStatement();
            }
        }

        private LetStatement ParseLetStatement()
        {
            var stmt = new LetStatement { Token = _curToken };
            
            // Determine the declaration kind
            if (_curToken.Type == TokenType.Const)
                stmt.Kind = DeclarationKind.Const;
            else if (_curToken.Type == TokenType.Let)
                stmt.Kind = DeclarationKind.Let;
            else
                stmt.Kind = DeclarationKind.Var;

            NextToken(); // Move past var/let/const

            // Check for destructuring: var { a, b } = obj  or  var [x, y] = arr
            if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
            {
                // Skip destructuring pattern - just consume until we hit = or ;
                var depth = 1;
                var isObject = CurTokenIs(TokenType.LBrace);
                var closeToken = isObject ? TokenType.RBrace : TokenType.RBracket;
                
                // Parse the destructuring pattern
                stmt.DestructuringPattern = ParseExpression(Precedence.Lowest);
                stmt.Name = new Identifier(_curToken, "_destructured"); // Dummy name
                
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // Move to =
                    NextToken(); // Move past =
                    stmt.Value = ParseExpression(Precedence.Lowest);
                }
                
                if (PeekTokenIs(TokenType.Semicolon))
                {
                    NextToken();
                }
                
                return stmt;
            }

            // Normal variable: var x = value
            if (!CurTokenIs(TokenType.Identifier))
            {
                return null;
            }

            stmt.Name = new Identifier(_curToken, _curToken.Literal);

            // Handle variable without initializer: var x;
            if (PeekTokenIs(TokenType.Semicolon) || PeekTokenIs(TokenType.Comma))
            {
                if (PeekTokenIs(TokenType.Semicolon)) NextToken();
                return stmt;
            }

            if (!ExpectPeek(TokenType.Assign))
            {
                return null;
            }

            NextToken();

            stmt.Value = ParseExpression(Precedence.Lowest);

            if (PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
            }

            return stmt;
        }

        private ReturnStatement ParseReturnStatement()
        {
            var stmt = new ReturnStatement { Token = _curToken };

            NextToken();

            stmt.ReturnValue = ParseExpression(Precedence.Lowest);

            if (PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
            }

            return stmt;
        }

        private ExpressionStatement ParseExpressionStatement()
        {
            var stmt = new ExpressionStatement { Token = _curToken };

            stmt.Expression = ParseExpression(Precedence.Lowest);

            if (PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
            }

            return stmt;
        }

        private Expression ParseExpression(Precedence precedence)
        {
            if (!_prefixParseFns.TryGetValue(_curToken.Type, out var prefix))
            {
                NoPrefixParseFnError(_curToken.Type);
                return null;
            }

            var leftExp = prefix();

            while (!PeekTokenIs(TokenType.Semicolon) && precedence < PeekPrecedence())
            {
                if (!_infixParseFns.TryGetValue(_peekToken.Type, out var infix))
                {
                    return leftExp;
                }

                NextToken();

                leftExp = infix(leftExp);
            }
            
            // _errors.Add($"[Debug] ParseExpression finished. Cur: {_curToken.Type}, Peek: {_peekToken.Type}");

            return leftExp;
        }

        private Expression ParseIdentifier()
        {
            return new Identifier(_curToken, _curToken.Literal);
        }

        private Expression ParsePrivateIdentifier()
        {
            // Token literal is "#fieldName", extract just the name
            string name = _curToken.Literal.Substring(1); // Remove '#'
            return new PrivateIdentifier(_curToken, name);
        }

        private Expression ParseIntegerLiteral()
        {
            var lit = new IntegerLiteral { Token = _curToken };

            if (!long.TryParse(_curToken.Literal, out var value))
            {
                _errors.Add($"could not parse {_curToken.Literal} as integer");
                return null;
            }

            lit.Value = value;
            return lit;
        }
        
        private Expression ParseStringLiteral()
        {
            return new StringLiteral { Token = _curToken, Value = _curToken.Literal };
        }

        // Parse a regular template literal: `Hello ${name}!`
        private Expression ParseTemplateLiteral()
        {
            // The lexer already combined the template literal content into a single token
            // Just return it as a StringLiteral for now (the interpreter will handle ${} interpolation)
            return new StringLiteral { Token = _curToken, Value = _curToken.Literal };
        }

        // Parse a tagged template literal: tag`Hello ${name}!`
        private Expression ParseTaggedTemplate(Expression left)
        {
            var tagged = new TaggedTemplateExpression
            {
                Token = _curToken,
                Tag = left
            };

            // The template content is in _curToken.Literal
            // Parse the template literal content to extract strings and expressions
            string content = _curToken.Literal;
            
            // Split by ${...} patterns
            var strings = new List<string>();
            var expressions = new List<Expression>();
            
            int pos = 0;
            while (pos < content.Length)
            {
                int dollarPos = content.IndexOf("${", pos);
                if (dollarPos == -1)
                {
                    // No more expressions, add the rest as a string
                    strings.Add(content.Substring(pos));
                    break;
                }
                
                // Add the string part before ${}
                strings.Add(content.Substring(pos, dollarPos - pos));
                
                // Find the matching closing brace
                int bracePos = dollarPos + 2;
                int depth = 1;
                while (bracePos < content.Length && depth > 0)
                {
                    if (content[bracePos] == '{') depth++;
                    else if (content[bracePos] == '}') depth--;
                    bracePos++;
                }
                
                // Extract and parse the expression
                string exprStr = content.Substring(dollarPos + 2, bracePos - dollarPos - 3);
                if (!string.IsNullOrWhiteSpace(exprStr))
                {
                    var exprLexer = new Lexer(exprStr);
                    var exprParser = new Parser(exprLexer);
                    var exprProgram = exprParser.ParseProgram();
                    if (exprProgram.Statements.Count > 0 && exprProgram.Statements[0] is ExpressionStatement exprStmt)
                    {
                        expressions.Add(exprStmt.Expression);
                    }
                }
                
                pos = bracePos;
            }
            
            // Ensure we have at least one string part
            if (strings.Count == 0)
            {
                strings.Add("");
            }
            
            tagged.Strings = strings;
            tagged.Expressions = expressions;
            
            return tagged;
        }

        private Expression ParseBoolean()
        {
            return new BooleanLiteral { Token = _curToken, Value = _curToken.Type == TokenType.True };
        }

        private Expression ParsePrefixExpression()
        {
            var expression = new PrefixExpression
            {
                Token = _curToken,
                Operator = _curToken.Literal
            };

            NextToken();

            expression.Right = ParseExpression(Precedence.Prefix);

            return expression;
        }

        private Expression ParseInfixExpression(Expression left)
        {
            var expression = new InfixExpression
            {
                Token = _curToken,
                Operator = _curToken.Literal,
                Left = left
            };

            var precedence = CurPrecedence();
            NextToken();
            expression.Right = ParseExpression(precedence);

            return expression;
        }

        private Expression ParseGroupedExpression()
        {
            NextToken();

            // Handle empty parens: () => expr  (arrow function with no params)
            if (CurTokenIs(TokenType.RParen))
            {
                // Check if followed by arrow
                if (PeekTokenIs(TokenType.Arrow))
                {
                    NextToken(); // Move to =>
                    // Parse as arrow function with empty params
                    var arrow = new ArrowFunctionExpression { Token = _curToken };
                    arrow.Parameters = new List<Identifier>();
                    NextToken(); // Move past =>
                    
                    // Parse body: either block statement or expression
                    if (CurTokenIs(TokenType.LBrace))
                    {
                        arrow.Body = ParseBlockStatement();
                    }
                    else
                    {
                        arrow.Body = ParseExpression(Precedence.Lowest);
                    }
                    return arrow;
                }
                // Empty parens not followed by arrow - this is unusual but return null
                return null;
            }

            var exp = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
            {
                return null;
            }

            return exp;
        }

        private Expression ParseIfExpression()
        {
            var expression = new IfExpression { Token = _curToken };

            if (!ExpectPeek(TokenType.LParen))
            {
                return null;
            }

            NextToken();
            expression.Condition = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
            {
                return null;
            }

            // Handle body - with or without braces
            expression.Consequence = ParseBodyAsBlock();
            if (expression.Consequence == null) return null;

            if (PeekTokenIs(TokenType.Else))
            {
                NextToken();

                // Check for else if
                if (PeekTokenIs(TokenType.If))
                {
                    NextToken();
                    var elseIfExpr = ParseIfExpression();
                    if (elseIfExpr is IfExpression)
                    {
                        expression.Alternative = new BlockStatement 
                        { 
                            Token = _curToken,
                            Statements = new System.Collections.Generic.List<Statement> 
                            { 
                                new ExpressionStatement { Expression = elseIfExpr }
                            }
                        };
                    }
                }
                else
                {
                    // Regular else - with or without braces
                    expression.Alternative = ParseBodyAsBlock();
                }
            }

            return expression;
        }

        // Helper: Parse a body that may or may not have braces
        private BlockStatement ParseBodyAsBlock()
        {
            if (PeekTokenIs(TokenType.LBrace))
            {
                NextToken(); // Move to '{'
                return ParseBlockStatement();
            }
            else
            {
                // Single statement without braces
                NextToken();
                var stmt = ParseStatement();
                if (stmt == null) return null;
                
                var block = new BlockStatement { Token = _curToken };
                block.Statements.Add(stmt);
                return block;
            }
        }

        private BlockStatement ParseBlockStatement()
        {
            var block = new BlockStatement { Token = _curToken };
            NextToken();

            while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
            {
                var stmt = ParseStatement();
                if (stmt != null)
                {
                    block.Statements.Add(stmt);
                }
                NextToken();
            }

            return block;
        }

        private Expression ParseFunctionLiteral()
        {
            var lit = new FunctionLiteral { Token = _curToken };
            
            // Check for generator function: function*
            if (PeekTokenIs(TokenType.Asterisk))
            {
                NextToken(); // consume *
                lit.IsGenerator = true;
            }

            if (PeekTokenIs(TokenType.Identifier))
            {
                NextToken();
                lit.Name = _curToken.Literal;
            }

            if (!ExpectPeek(TokenType.LParen))
            {
                return null;
            }

            lit.Parameters = ParseFunctionParameters();

            if (!ExpectPeek(TokenType.LBrace))
            {
                return null;
            }

            lit.Body = ParseBlockStatement();

            return lit;
        }
        
        private Expression ParseYieldExpression()
        {
            var yield = new YieldExpression { Token = _curToken };
            
            // Check for yield*
            if (PeekTokenIs(TokenType.Asterisk))
            {
                NextToken(); // consume *
                yield.Delegate = true;
            }
            
            // Check if there's a value after yield
            if (!PeekTokenIs(TokenType.Semicolon) && !PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Eof))
            {
                NextToken();
                yield.Value = ParseExpression(Precedence.Lowest);
            }
            
            return yield;
        }

        private List<Identifier> ParseFunctionParameters()
        {
            var identifiers = new List<Identifier>();

            if (PeekTokenIs(TokenType.RParen))
            {
                NextToken();
                return identifiers;
            }

            NextToken();

            // TODO: Handle destructuring parameters
            if (CurTokenIs(TokenType.Identifier))
            {
                var ident = new Identifier(_curToken, _curToken.Literal);
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Lowest);
                }
                identifiers.Add(ident);
            }
            else if (CurTokenIs(TokenType.Ellipsis))
            {
                NextToken();
                if (!CurTokenIs(TokenType.Identifier)) return null;
                var ident = new Identifier(_curToken, _curToken.Literal) { IsRest = true };
                identifiers.Add(ident);
            }

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken();
                NextToken();
                
                if (CurTokenIs(TokenType.Identifier))
                {
                    var ident = new Identifier(_curToken, _curToken.Literal);
                    if (PeekTokenIs(TokenType.Assign))
                    {
                        NextToken(); // =
                        NextToken(); // value
                        ident.DefaultValue = ParseExpression(Precedence.Lowest);
                    }
                    identifiers.Add(ident);
                }
                else if (CurTokenIs(TokenType.Ellipsis))
                {
                    NextToken();
                    if (!CurTokenIs(TokenType.Identifier)) return null;
                    var ident = new Identifier(_curToken, _curToken.Literal) { IsRest = true };
                    identifiers.Add(ident);
                }
            }

            if (!ExpectPeek(TokenType.RParen))
            {
                return null;
            }

            return identifiers;
        }

        private Expression ParseCallExpression(Expression function)
        {
            var exp = new CallExpression { Token = _curToken, Function = function };
            exp.Arguments = ParseCallArguments();
            return exp;
        }

        private List<Expression> ParseCallArguments()
        {
            var args = new List<Expression>();

            if (PeekTokenIs(TokenType.RParen))
            {
                NextToken();
                return args;
            }

            NextToken();
            if (CurTokenIs(TokenType.Ellipsis))
            {
                NextToken();
                args.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Lowest) });
            }
            else
            {
                args.Add(ParseExpression(Precedence.Comma));
            }

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken();
                NextToken();
                
                if (CurTokenIs(TokenType.Ellipsis))
                {
                    NextToken();
                    args.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Lowest) });
                }
                else
                {
                    args.Add(ParseExpression(Precedence.Comma));
                }
            }

            if (!ExpectPeek(TokenType.RParen))
            {
                return null;
            }

            return args;
        }

        private Expression ParseNewExpression()
        {
            var exp = new NewExpression { Token = _curToken };
            NextToken(); // consume 'new'

            // Parse constructor - use Call precedence to stop before parsing arguments
            exp.Constructor = ParseExpression(Precedence.Call);

            // Parse arguments: new Date()
            if (PeekTokenIs(TokenType.LParen))
            {
                NextToken();
                exp.Arguments = ParseCallArguments();
            }

            return exp;
        }

        private Expression ParseArrayLiteral()
        {
            var array = new ArrayLiteral { Token = _curToken };

            array.Elements = ParseExpressionList(TokenType.RBracket);

            return array;
        }

        private Expression ParseObjectLiteral()
        {
            var obj = new ObjectLiteral { Token = _curToken };

            while (!PeekTokenIs(TokenType.RBrace))
            {
                NextToken();
                var key = "";
                
                // Key can be Identifier or String or Keyword
                if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String) || 
                   (_curToken.Type >= TokenType.Function && _curToken.Type <= TokenType.As))
                {
                    key = _curToken.Literal;
                }
                else
                {
                    // Try to recover from unexpected tokens
                    if (CurTokenIs(TokenType.RBrace)) break;
                    // Skip unknown tokens and try again
                     _errors.Add($"[Debug] ParseObjectLiteral skipped unexpected token as key: {_curToken.Type}");
                    continue;
                }

                // Check for method shorthand: { foo() { ... } }
                if (PeekTokenIs(TokenType.LParen))
                {
                    // This MIGHT be method shorthand - parse it as such
                    NextToken(); // Move to '('
                    var methodParams = ParseFunctionParameters();
                    
                    // If next is LBrace, it's definitely a method
                    if (PeekTokenIs(TokenType.LBrace))
                    {
                        NextToken(); // Move to '{'
                        var methodBody = ParseBlockStatement();
                        
                        var methodFunc = new FunctionLiteral 
                        { 
                            Token = _curToken, 
                            Parameters = methodParams, 
                            Body = methodBody 
                        };
                        
                        obj.Pairs[key] = methodFunc;
                        
                        if (!PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Comma))
                        {
                            if (PeekTokenIs(TokenType.Eof)) break;
                        }
                        if (PeekTokenIs(TokenType.Comma)) NextToken();
                        continue;
                    }
                    // Not a method - might be a computed property or something else
                    // For now, skip to recovery
                    return null;
                }

                // Check for regular key: value, destructuring key = value, or shorthand key
                if (PeekTokenIs(TokenType.Colon))
                {
                    NextToken(); // Consume :
                    NextToken(); // Move to value
                    var value = ParseExpression(Precedence.Comma);
                    obj.Pairs[key] = value;
                }
                else if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // Consume =
                    NextToken(); // Move to value
                    var value = ParseExpression(Precedence.Comma);
                    obj.Pairs[key] = value;
                }
                else if (PeekTokenIs(TokenType.Comma) || PeekTokenIs(TokenType.RBrace))
                {
                    // Shorthand property: { key } === { key: key }
                    obj.Pairs[key] = new Identifier(_curToken, key);
                }
                else
                {
                    _errors.Add($"[Debug] ParseObjectLiteral failed for key: {key}, Next token: {_peekToken.Type}");
                    return null;
                }

                if (!PeekTokenIs(TokenType.RBrace) && !ExpectPeek(TokenType.Comma))
                {
                    return null;
                }
            }

            if (!ExpectPeek(TokenType.RBrace))
            {
                return null;
            }

            return obj;
        }

        private Expression ParseMemberExpression(Expression obj)
        {
            var exp = new MemberExpression { Token = _curToken, Object = obj };

            if (!ExpectPeek(TokenType.Identifier))
            {
                return null;
            }

            exp.Property = _curToken.Literal;
            return exp;
        }

        private Expression ParseIndexExpression(Expression left)
        {
            var exp = new IndexExpression { Token = _curToken, Left = left };

            NextToken();
            exp.Index = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RBracket))
            {
                return null;
            }

            return exp;
        }

        private Expression ParseAssignmentExpression(Expression left)
        {
            var exp = new AssignmentExpression { Token = _curToken, Left = left };
            
            NextToken(); // Move past '='
            
            // Parse the right side with lowest precedence to capture the full expression
            exp.Right = ParseExpression(Precedence.Lowest);
            
            return exp;
        }

        private bool CurTokenIs(TokenType type)
        {
            return _curToken.Type == type;
        }

        private bool PeekTokenIs(TokenType type)
        {
            return _peekToken.Type == type;
        }

        private bool ExpectPeek(TokenType type)
        {
            if (PeekTokenIs(type))
            {
                NextToken();
                return true;
            }
            PeekError(type);
            return false;
        }

        private void PeekError(TokenType type)
        {
            _errors.Add($"expected next token to be {type}, got {_peekToken.Type} instead");
        }

        private void NoPrefixParseFnError(TokenType type)
        {
            _errors.Add($"no prefix parse function for {type} found");
        }

        private Precedence PeekPrecedence()
        {
            if (_precedences.TryGetValue(_peekToken.Type, out var p))
            {
                return p;
            }
            return Precedence.Lowest;
        }

        private Precedence CurPrecedence()
        {
            if (_precedences.TryGetValue(_curToken.Type, out var p))
            {
                return p;
            }
            return Precedence.Lowest;
        }
        private Statement ParseTryStatement()
        {
            var stmt = new TryStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.LBrace))
            {
                return null;
            }

            stmt.Block = ParseBlockStatement();

            if (PeekTokenIs(TokenType.Catch))
            {
                NextToken();
                
                // Optional catch parameter: catch(e)
                if (PeekTokenIs(TokenType.LParen))
                {
                    NextToken();
                    if (!ExpectPeek(TokenType.Identifier)) return null;
                    stmt.CatchParameter = new Identifier(_curToken, _curToken.Literal);
                    if (!ExpectPeek(TokenType.RParen)) return null;
                }

                if (!ExpectPeek(TokenType.LBrace)) return null;
                stmt.CatchBlock = ParseBlockStatement();
            }

            if (PeekTokenIs(TokenType.Finally))
            {
                NextToken();
                if (!ExpectPeek(TokenType.LBrace)) return null;
                stmt.FinallyBlock = ParseBlockStatement();
            }

            return stmt;
        }

        private List<Expression> ParseExpressionList(TokenType end)
        {
            var list = new List<Expression>();

            if (PeekTokenIs(end))
            {
                NextToken();
                try { System.IO.File.AppendAllText("parse_debug.txt", $"[ParseExpressionList] Empty list, end token found\r\n"); } catch { }
                return list;
            }

            NextToken();
            if (CurTokenIs(TokenType.Ellipsis))
            {
                NextToken();
                list.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Assignment) });
            }
            else
            {
                // Use Assignment precedence to avoid consuming commas as operators
                var expr = ParseExpression(Precedence.Assignment);
                list.Add(expr);
                try { System.IO.File.AppendAllText("parse_debug.txt", $"[ParseExpressionList] First element: {expr?.GetType().Name}, Peek: {_peekToken.Type}\r\n"); } catch { }
            }

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken();
                NextToken();
                try { System.IO.File.AppendAllText("parse_debug.txt", $"[ParseExpressionList] Next element after comma, Cur: {_curToken.Type} '{_curToken.Literal}'\r\n"); } catch { }
                
                if (CurTokenIs(TokenType.Ellipsis))
                {
                    NextToken();
                    list.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Assignment) });
                }
                else
                {
                    // Use Assignment precedence to avoid consuming commas as operators
                    var expr = ParseExpression(Precedence.Assignment);
                    list.Add(expr);
                    try { System.IO.File.AppendAllText("parse_debug.txt", $"[ParseExpressionList] Element added: {expr?.GetType().Name}\r\n"); } catch { }
                }
            }

            try { System.IO.File.AppendAllText("parse_debug.txt", $"[ParseExpressionList] Total elements: {list.Count}, Peek: {_peekToken.Type}\r\n"); } catch { }
            if (!ExpectPeek(end))
            {
                try { System.IO.File.AppendAllText("parse_debug.txt", $"[ParseExpressionList] ERROR: Expected {end}, got {_peekToken.Type}\r\n"); } catch { }
                return null;
            }

            return list;
        }


        private Statement ParseThrowStatement()
        {
            var stmt = new ThrowStatement { Token = _curToken };

            NextToken();

            stmt.Value = ParseExpression(Precedence.Lowest);

            if (PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
            }

            return stmt;
        }



        private Statement ParseWhileStatement()
        {
            var stmt = new WhileStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.LParen)) return null;

            NextToken();
            stmt.Condition = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen)) return null;

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();

            return stmt;
        }

        private Statement ParseForStatement()
        {
            var forToken = _curToken;

            if (!ExpectPeek(TokenType.LParen)) return null;
            NextToken();

            // Check for for-in: for (var x in obj) or for (x in obj)
            // Check for for-of: for (var x of iterable) or for (x of iterable)
            Token varKeyword = null;
            if (CurTokenIs(TokenType.Var) || CurTokenIs(TokenType.Let) || CurTokenIs(TokenType.Const))
            {
                varKeyword = _curToken;
                NextToken();
            }

            // If current is identifier and peek is 'in' or 'of', this is for-in/for-of
            if (CurTokenIs(TokenType.Identifier))
            {
                if (PeekTokenIs(TokenType.In))
                {
                    return ParseForInStatement(forToken, _curToken);
                }
                else if (PeekTokenIs(TokenType.Of))
                {
                    return ParseForOfStatement(forToken, _curToken);
                }
            }

            // Regular for loop - reset and continue
            var stmt = new ForStatement { Token = forToken };

            // We need to handle the init part we've already partially parsed
            if (varKeyword != null)
            {
                // We have 'var x' so far, parse the rest of let statement
                var varStmt = new LetStatement { Token = varKeyword };
                varStmt.Name = new Identifier(_curToken, _curToken.Literal);
                
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken();
                    NextToken();
                    varStmt.Value = ParseExpression(Precedence.Lowest);
                }
                stmt.Init = varStmt;
            }
            else if (!CurTokenIs(TokenType.Semicolon))
            {
                // Parse as expression statement
                var exp = ParseExpression(Precedence.Lowest);
                stmt.Init = new ExpressionStatement { Expression = exp };
            }
            
            if (!ExpectPeek(TokenType.Semicolon)) 
            {
                return null;
            }

            // Condition
            if (!PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
                stmt.Condition = ParseExpression(Precedence.Lowest);
            }
            if (!ExpectPeek(TokenType.Semicolon)) return null;

            // Update
            if (!PeekTokenIs(TokenType.RParen))
            {
                NextToken();
                var exp = ParseExpression(Precedence.Lowest);
                stmt.Update = new ExpressionStatement { Expression = exp };
            }

            if (!ExpectPeek(TokenType.RParen)) return null;

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();

            return stmt;
        }

        // Parse for-of: for (x of iterable) { ... }
        private Statement ParseForOfStatement(Token forToken, Token varToken)
        {
            var stmt = new ForOfStatement { Token = forToken };
            stmt.Variable = new Identifier(varToken, varToken.Literal);

            NextToken(); // Move past 'of'
            NextToken(); // Move to iterable expression
            
            stmt.Iterable = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen)) return null;

            stmt.Body = ParseBodyAsBlock();
            return stmt;
        }

        private Statement ParseClassStatement()
        {
            var stmt = new ClassStatement { Token = _curToken };

            if (!PeekTokenIs(TokenType.Identifier))
            {
                return null;
            }

            NextToken();
            stmt.Name = new Identifier(_curToken, _curToken.Literal);

            if (PeekTokenIs(TokenType.Extends))
            {
                NextToken();
                NextToken();
                stmt.SuperClass = new Identifier(_curToken, _curToken.Literal);
            }

            if (!ExpectPeek(TokenType.LBrace))
            {
                return null;
            }

            // Parse class body
            while (!PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Eof))
            {
                var member = ParseClassMember();
                if (member is MethodDefinition method)
                {
                    stmt.Methods.Add(method);
                }
                else if (member is ClassProperty prop)
                {
                    stmt.Properties.Add(prop);
                }
                NextToken();
            }

            if (!ExpectPeek(TokenType.RBrace))
            {
                return null;
            }

            return stmt;
        }

        private Statement ParseClassMember()
        {
            bool isStatic = false;
            bool isPrivate = false;
            
            if (PeekTokenIs(TokenType.Static))
            {
                NextToken();
                isStatic = true;
            }

            NextToken();
            
            // Check for private identifier (#field)
            if (CurTokenIs(TokenType.PrivateIdentifier))
            {
                isPrivate = true;
                // Token literal is "#fieldName", extract just the name
                string privateName = _curToken.Literal.Substring(1); // Remove '#'
                var key = new Identifier(_curToken, privateName);
                
                // Is it a method or a property?
                if (PeekTokenIs(TokenType.LParen))
                {
                    // It's a private method
                    var method = new MethodDefinition
                    {
                        Key = key,
                        Kind = "method",
                        Static = isStatic,
                        IsPrivate = true
                    };
                    
                    var funcLit = new FunctionLiteral { Token = _curToken };
                    if (!ExpectPeek(TokenType.LParen)) return null;
                    funcLit.Parameters = ParseFunctionParameters();
                    if (!ExpectPeek(TokenType.LBrace)) return null;
                    funcLit.Body = ParseBodyAsBlock();
                    method.Value = funcLit;
                    return method;
                }
                else
                {
                    // It's a private property
                    var prop = new ClassProperty
                    {
                        Key = key,
                        Static = isStatic,
                        IsPrivate = true
                    };
                    
                    if (PeekTokenIs(TokenType.Assign))
                    {
                        NextToken(); // consume =
                        NextToken(); // move to value
                        prop.Value = ParseExpression(Precedence.Lowest);
                    }
                    
                    // Skip optional semicolon
                    if (PeekTokenIs(TokenType.Semicolon))
                    {
                        NextToken();
                    }
                    return prop;
                }
            }
            
            // Handle regular member (constructor, method, getter, setter, property)
            if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String))
            {
                var key = new Identifier(_curToken, _curToken.Literal);
                
                // Check for constructor
                if (key.Value == "constructor")
                {
                    var method = new MethodDefinition
                    {
                        Key = key,
                        Kind = "constructor",
                        Static = false, // constructor can't be static
                        IsPrivate = false
                    };
                    
                    var funcLit = new FunctionLiteral { Token = _curToken };
                    if (!ExpectPeek(TokenType.LParen)) return null;
                    funcLit.Parameters = ParseFunctionParameters();
                    if (!ExpectPeek(TokenType.LBrace)) return null;
                    funcLit.Body = ParseBodyAsBlock();
                    method.Value = funcLit;
                    return method;
                }
                
                // Check for getter
                if (key.Value == "get" && (PeekTokenIs(TokenType.Identifier) || PeekTokenIs(TokenType.PrivateIdentifier)))
                {
                    NextToken();
                    bool getterIsPrivate = CurTokenIs(TokenType.PrivateIdentifier);
                    string getterName = getterIsPrivate ? _curToken.Literal.Substring(1) : _curToken.Literal;
                    
                    var method = new MethodDefinition
                    {
                        Key = new Identifier(_curToken, getterName),
                        Kind = "get",
                        Static = isStatic,
                        IsPrivate = getterIsPrivate
                    };
                    
                    var funcLit = new FunctionLiteral { Token = _curToken };
                    if (!ExpectPeek(TokenType.LParen)) return null;
                    funcLit.Parameters = ParseFunctionParameters();
                    if (!ExpectPeek(TokenType.LBrace)) return null;
                    funcLit.Body = ParseBodyAsBlock();
                    method.Value = funcLit;
                    return method;
                }
                
                // Check for setter
                if (key.Value == "set" && (PeekTokenIs(TokenType.Identifier) || PeekTokenIs(TokenType.PrivateIdentifier)))
                {
                    NextToken();
                    bool setterIsPrivate = CurTokenIs(TokenType.PrivateIdentifier);
                    string setterName = setterIsPrivate ? _curToken.Literal.Substring(1) : _curToken.Literal;
                    
                    var method = new MethodDefinition
                    {
                        Key = new Identifier(_curToken, setterName),
                        Kind = "set",
                        Static = isStatic,
                        IsPrivate = setterIsPrivate
                    };
                    
                    var funcLit = new FunctionLiteral { Token = _curToken };
                    if (!ExpectPeek(TokenType.LParen)) return null;
                    funcLit.Parameters = ParseFunctionParameters();
                    if (!ExpectPeek(TokenType.LBrace)) return null;
                    funcLit.Body = ParseBodyAsBlock();
                    method.Value = funcLit;
                    return method;
                }
                
                // Is it a method or a property?
                if (PeekTokenIs(TokenType.LParen))
                {
                    // It's a method
                    var method = new MethodDefinition
                    {
                        Key = key,
                        Kind = "method",
                        Static = isStatic,
                        IsPrivate = false
                    };
                    
                    var funcLit = new FunctionLiteral { Token = _curToken };
                    if (!ExpectPeek(TokenType.LParen)) return null;
                    funcLit.Parameters = ParseFunctionParameters();
                    if (!ExpectPeek(TokenType.LBrace)) return null;
                    funcLit.Body = ParseBodyAsBlock();
                    method.Value = funcLit;
                    return method;
                }
                else
                {
                    // It's a public property
                    var prop = new ClassProperty
                    {
                        Key = key,
                        Static = isStatic,
                        IsPrivate = false
                    };
                    
                    if (PeekTokenIs(TokenType.Assign))
                    {
                        NextToken(); // consume =
                        NextToken(); // move to value
                        prop.Value = ParseExpression(Precedence.Lowest);
                    }
                    
                    // Skip optional semicolon
                    if (PeekTokenIs(TokenType.Semicolon))
                    {
                        NextToken();
                    }
                    return prop;
                }
            }
            
            return null;
        }

        private MethodDefinition ParseMethodDefinition()
        {
            var method = new MethodDefinition();
            
            if (PeekTokenIs(TokenType.Static))
            {
                NextToken();
                method.Static = true;
            }

            NextToken();
            
            // Handle constructor or method name
            if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String))
            {
                method.Key = new Identifier(_curToken, _curToken.Literal);
                
                if (method.Key.Value == "constructor")
                {
                    method.Kind = "constructor";
                }
                else if (method.Key.Value == "get")
                {
                    method.Kind = "get";
                    // Handle getter name
                    if (PeekTokenIs(TokenType.Identifier))
                    {
                        NextToken();
                        method.Key = new Identifier(_curToken, _curToken.Literal);
                    }
                }
                else if (method.Key.Value == "set")
                {
                    method.Kind = "set";
                    // Handle setter name
                    if (PeekTokenIs(TokenType.Identifier))
                    {
                        NextToken();
                        method.Key = new Identifier(_curToken, _curToken.Literal);
                    }
                }
                else
                {
                    method.Kind = "method";
                }
            }
            else
            {
                return null;
            }

            // Parse method body as function literal
            if (PeekTokenIs(TokenType.LParen))
            {
                // We need to manually parse function literal parts because ParseFunctionLiteral expects 'function' token
                // But here we are at method name.
                
                // Let's reuse ParseFunctionParameters and ParseBodyAsBlock
                
                var funcLit = new FunctionLiteral { Token = _curToken }; // Token is method name
                
                if (!ExpectPeek(TokenType.LParen)) return null;
                
                funcLit.Parameters = ParseFunctionParameters();
                
                if (!ExpectPeek(TokenType.LBrace)) return null;
                
                funcLit.Body = ParseBodyAsBlock();
                
                method.Value = funcLit;
                return method;
            }

            return null;
        }

        private Statement ParseImportDeclaration()
        {
            var stmt = new ImportDeclaration { Token = _curToken };

            NextToken(); // Move past 'import'

            // import "module"
            if (CurTokenIs(TokenType.String))
            {
                stmt.Source = _curToken.Literal;
                if (PeekTokenIs(TokenType.Semicolon)) NextToken();
                return stmt;
            }

            // import { x, y } from "module"
            if (CurTokenIs(TokenType.LBrace))
            {
                stmt.Specifiers = ParseImportSpecifiers();
                
                if (!ExpectPeek(TokenType.From)) return null;
                
                if (!ExpectPeek(TokenType.String)) return null;
                
                stmt.Source = _curToken.Literal;
            }
            // import x from "module" (default import)
            else if (CurTokenIs(TokenType.Identifier))
            {
                var specifier = new ImportSpecifier
                {
                    Local = new Identifier(_curToken, _curToken.Literal),
                    Imported = new Identifier(_curToken, "default")
                };
                stmt.Specifiers.Add(specifier);

                if (PeekTokenIs(TokenType.Comma))
                {
                    NextToken(); // Skip comma
                    if (PeekTokenIs(TokenType.LBrace))
                    {
                        NextToken(); // Move to {
                        stmt.Specifiers.AddRange(ParseImportSpecifiers());
                    }
                }

                if (!ExpectPeek(TokenType.From)) return null;
                
                if (!ExpectPeek(TokenType.String)) return null;
                
                stmt.Source = _curToken.Literal;
            }

            if (PeekTokenIs(TokenType.Semicolon)) NextToken();
            return stmt;
        }

        private List<ImportSpecifier> ParseImportSpecifiers()
        {
            var specifiers = new List<ImportSpecifier>();
            
            if (!ExpectPeek(TokenType.LBrace)) return specifiers; // Should be at {
            
            NextToken(); // Move past {

            while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
            {
                var specifier = new ImportSpecifier();
                
                if (!CurTokenIs(TokenType.Identifier)) return specifiers;
                
                var ident = new Identifier(_curToken, _curToken.Literal);
                specifier.Imported = ident;
                specifier.Local = ident; // Default to same name

                if (PeekTokenIs(TokenType.As))
                {
                    NextToken(); // Move to 'as'
                    if (!ExpectPeek(TokenType.Identifier)) return specifiers;
                    specifier.Local = new Identifier(_curToken, _curToken.Literal);
                }

                specifiers.Add(specifier);

                if (PeekTokenIs(TokenType.Comma))
                {
                    NextToken();
                    NextToken();
                }
                else
                {
                    NextToken();
                }
            }

            return specifiers;
        }

        private Statement ParseExportDeclaration()
        {
            var stmt = new ExportDeclaration { Token = _curToken };
            NextToken(); // Move past 'export'

            if (CurTokenIs(TokenType.Default))
            {
                NextToken(); // Move past 'default'
                stmt.DefaultExpression = ParseExpression(Precedence.Lowest);
            }
            else if (CurTokenIs(TokenType.Var) || CurTokenIs(TokenType.Let) || CurTokenIs(TokenType.Const) || 
                     CurTokenIs(TokenType.Function) || CurTokenIs(TokenType.Class))
            {
                stmt.Declaration = ParseStatement();
            }
            else if (CurTokenIs(TokenType.LBrace))
            {
                // export { x, y }
                // Reuse ParseImportSpecifiers logic partially or implement similar
                // For now, skip implementation detail for named exports from list
                while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof)) NextToken();
            }

            if (PeekTokenIs(TokenType.Semicolon)) NextToken();
            return stmt;
        }

        private Statement ParseAsyncFunctionDeclaration()
        {
            // async function name() { ... }
            var token = _curToken;
            NextToken(); // Move past 'async'

            if (!ExpectPeek(TokenType.Function)) return null;

            var funcLit = ParseFunctionLiteral() as FunctionLiteral;
            
            // Convert to AsyncFunctionExpression or similar
            // For statement, we can wrap it in LetStatement like regular function declaration
            
            if (funcLit != null && funcLit.Name != null)
            {
                var asyncFunc = new AsyncFunctionExpression
                {
                    Token = token,
                    Name = new Identifier(token, funcLit.Name),
                    Parameters = funcLit.Parameters,
                    Body = funcLit.Body
                };

                return new LetStatement 
                { 
                    Token = token, 
                    Name = asyncFunc.Name, 
                    Value = asyncFunc 
                };
            }
            
            return null;
        }

        private Expression ParseAwaitExpression()
        {
            var expression = new AwaitExpression { Token = _curToken };
            NextToken(); // Move past 'await'
            expression.Argument = ParseExpression(Precedence.Prefix);
            return expression;
        }

        private Expression ParseAsyncPrefix()
        {
            var token = _curToken;
            
            // Check if it's likely an async function or arrow function
            if (PeekTokenIs(TokenType.Function))
            {
                NextToken(); // Move past 'async'
                // Parse as async function expression
                var funcLit = ParseFunctionLiteral() as FunctionLiteral;
                if (funcLit != null)
                {
                    return new AsyncFunctionExpression
                    {
                        Token = token,
                        Name = funcLit.Name != null ? new Identifier(token, funcLit.Name) : null,
                        Parameters = funcLit.Parameters,
                        Body = funcLit.Body
                    };
                }
            }
            
            // If not followed by function, treat 'async' as an identifier
            // This allows 'async' to be used as a variable name in expressions
            return new Identifier(token, "async");
        }

        // Parse for-in: for (x in obj) { ... }
        private Statement ParseForInStatement(Token forToken, Token varToken)
        {
            var stmt = new ForInStatement { Token = forToken };
            stmt.Variable = new Identifier(varToken, varToken.Literal);

            NextToken(); // Move past 'in'
            NextToken(); // Move to object expression
            
            stmt.Object = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen)) return null;

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();

            return stmt;
        }

        // Parse switch: switch (expr) { case val: ... default: ... }
        private Statement ParseSwitchStatement()
        {
            var stmt = new SwitchStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.LParen)) return null;
            NextToken();
            stmt.Discriminant = ParseExpression(Precedence.Lowest);
            if (!ExpectPeek(TokenType.RParen)) return null;
            if (!ExpectPeek(TokenType.LBrace)) return null;

            // Parse cases
            while (!PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Eof))
            {
                NextToken();
                var switchCase = new SwitchCase { Token = _curToken };

                if (CurTokenIs(TokenType.Case))
                {
                    NextToken();
                    switchCase.Test = ParseExpression(Precedence.Lowest);
                }
                else if (CurTokenIs(TokenType.Default))
                {
                    switchCase.Test = null;
                }
                else
                {
                    continue; // Skip unexpected tokens
                }

                if (!ExpectPeek(TokenType.Colon)) return null;

                // Parse statements until next case/default/}
                while (!PeekTokenIs(TokenType.Case) && !PeekTokenIs(TokenType.Default) && 
                       !PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Eof))
                {
                    NextToken();
                    var s = ParseStatement();
                    if (s != null) switchCase.Consequent.Add(s);
                }

                stmt.Cases.Add(switchCase);
            }

            if (!ExpectPeek(TokenType.RBrace)) return null;
            return stmt;
        }

        // Parse break statement
        private Statement ParseBreakStatement()
        {
            var stmt = new BreakStatement { Token = _curToken };
            
            // Check for optional label
            if (PeekTokenIs(TokenType.Identifier))
            {
                NextToken();
                stmt.Label = new Identifier(_curToken, _curToken.Literal);
            }
            
            if (PeekTokenIs(TokenType.Semicolon)) NextToken();
            return stmt;
        }

        // Parse continue statement
        private Statement ParseContinueStatement()
        {
            var stmt = new ContinueStatement { Token = _curToken };
            
            // Check for optional label
            if (PeekTokenIs(TokenType.Identifier))
            {
                NextToken();
                stmt.Label = new Identifier(_curToken, _curToken.Literal);
            }
            
            if (PeekTokenIs(TokenType.Semicolon)) NextToken();
            return stmt;
        }

        // Parse do-while: do { } while (condition);
        private Statement ParseDoWhileStatement()
        {
            var stmt = new DoWhileStatement { Token = _curToken };

            // Parse body
            stmt.Body = ParseBodyAsBlock();
            if (stmt.Body == null) return null;

            // Expect 'while'
            if (!ExpectPeek(TokenType.While)) return null;
            if (!ExpectPeek(TokenType.LParen)) return null;

            NextToken();
            stmt.Condition = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen)) return null;
            if (PeekTokenIs(TokenType.Semicolon)) NextToken();

            return stmt;
        }

        private Expression ParseNull()
        {
            return new NullLiteral { Token = _curToken };
        }

        private Expression ParseUndefined()
        {
            return new UndefinedLiteral { Token = _curToken };
        }

        // Empty expression for error recovery (;, trailing comma, etc.)
        private Expression ParseEmptyExpression()
        {
            return new EmptyExpression { Token = _curToken };
        }

        // Parse ternary conditional: condition ? consequent : alternate
        private Expression ParseConditionalExpression(Expression condition)
        {
            var exp = new ConditionalExpression { Token = _curToken, Condition = condition };

            NextToken(); // Move past '?'
            exp.Consequent = ParseExpression(Precedence.Ternary);

            if (!ExpectPeek(TokenType.Colon))
            {
                _errors.Add("Expected ':' in ternary expression");
                return null;
            }

            NextToken(); // Move past ':'
            exp.Alternate = ParseExpression(Precedence.Ternary);

            return exp;
        }

        // Parse arrow function when => is encountered after grouped expression (params)
        private Expression ParseArrowFunctionFromParams(Expression left)
        {
            var arrow = new ArrowFunctionExpression { Token = _curToken };

            // Extract parameters from left side (should be grouped expression or identifier)
            arrow.Parameters = ExtractArrowParameters(left);

            NextToken(); // Move past '=>'

            // Parse body: either block statement or expression
            if (CurTokenIs(TokenType.LBrace))
            {
                arrow.Body = ParseBlockStatement();
            }
            else
            {
                arrow.Body = ParseExpression(Precedence.Lowest);
            }

            return arrow;
        }

        // Helper to convert left-side expression(s) into parameter list
        private List<Identifier> ExtractArrowParameters(Expression left)
        {
            var parameters = new List<Identifier>();

            if (left == null)
            {
                return parameters;
            }

            // Single identifier: x => x * 2
            if (left is Identifier id)
            {
                parameters.Add(id);
            }
            // Grouped expression parsed as comma-separated: (a, b)
            else if (left is InfixExpression infix && infix.Operator == ",")
            {
                FlattenCommaExpression(infix, parameters);
            }
            // Just a grouped single param: (x)
            // This comes as just the inner expression
            else
            {
                // Try to extract if it's some other form
                // For safety, we ignore malformed cases
            }

            return parameters;
        }

        // Flatten comma-separated expressions into parameter list
        private void FlattenCommaExpression(InfixExpression infix, List<Identifier> parameters)
        {
            // Left side
            if (infix.Left is InfixExpression leftInfix && leftInfix.Operator == ",")
            {
                FlattenCommaExpression(leftInfix, parameters);
            }
            else if (infix.Left is Identifier leftId)
            {
                parameters.Add(leftId);
            }

            // Right side
            if (infix.Right is Identifier rightId)
            {
                parameters.Add(rightId);
            }
        }

        // Parse compound assignment: x += 1
        private Expression ParseCompoundAssignment(Expression left)
        {
            var token = _curToken;
            var op = token.Literal;  // +=, -=, etc.

            var precedence = CurPrecedence();
            NextToken();
            var right = ParseExpression(precedence);

            // Convert x += 1 to x = x + 1 internally
            // Create the binary operation
            var binaryOp = op.Substring(0, op.Length - 1);  // Remove '=' to get +, -, *, /
            var binaryExpr = new InfixExpression 
            { 
                Token = token, 
                Left = left, 
                Operator = binaryOp, 
                Right = right 
            };

            // Create assignment
            return new AssignmentExpression 
            { 
                Token = token, 
                Left = left, 
                Right = binaryExpr 
            };
        }

        // Parse prefix increment: ++x
        private Expression ParsePrefixIncrement()
        {
            var token = _curToken;
            NextToken();
            var operand = ParseExpression(Precedence.Prefix);
            return new PrefixExpression { Token = token, Operator = "++", Right = operand };
        }

        // Parse prefix decrement: --x
        private Expression ParsePrefixDecrement()
        {
            var token = _curToken;
            NextToken();
            var operand = ParseExpression(Precedence.Prefix);
            return new PrefixExpression { Token = token, Operator = "--", Right = operand };
        }

        // Parse postfix increment: x++
        private Expression ParsePostfixIncrement(Expression left)
        {
            return new InfixExpression { Token = _curToken, Left = left, Operator = "++", Right = null };
        }

        // Parse postfix decrement: x--
        private Expression ParsePostfixDecrement(Expression left)
        {
            return new InfixExpression { Token = _curToken, Left = left, Operator = "--", Right = null };
        }

        // Parse regex literal when starting with /
        // This is called when we see a Slash token in prefix position
        private Expression ParseRegexLiteral()
        {
            // At this point _curToken is Slash
            // We need to read ahead to find the closing /
            // This is a simplified implementation - real regex parsing is complex
            
            var startToken = _curToken;
            var pattern = new System.Text.StringBuilder();
            var flags = "";
            
            // Read the pattern until we find closing /
            // Note: This accesses internal lexer state which isn't ideal
            // For now, just return a placeholder regex
            // In production, the lexer would need context awareness
            
            return new RegexLiteral 
            { 
                Token = startToken, 
                Pattern = ".*",  // Placeholder 
                Flags = "" 
            };
        }

        // Parse already-lexed regex token
        private Expression ParseRegexToken()
        {
            // If lexer provides a regex token, parse it
            var literal = _curToken.Literal;
            var pattern = "";
            var flags = "";
            
            // Parse /pattern/flags format
            if (literal.StartsWith("/") && literal.Length > 1)
            {
                var lastSlash = literal.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    pattern = literal.Substring(1, lastSlash - 1);
                    flags = literal.Substring(lastSlash + 1);
                }
            }
            
            return new RegexLiteral { Token = _curToken, Pattern = pattern, Flags = flags };
        }
    }
}
