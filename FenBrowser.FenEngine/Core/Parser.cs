using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Core
{
    public enum Precedence
    {
        Lowest,
        Comma,            // ,
        Assignment,       // =, +=, -=, etc.
        NullishCoalesce,  // ??
        Ternary,          // ? :  (conditional)
        LogicalOr,        // ||
        LogicalAnd,       // &&
        BitwiseOr,        // |
        BitwiseXor,       // ^
        BitwiseAnd,       // &
        Equals,           // ==, ===, !=, !==
        LessGreater,      // >, <, >=, <=, in, instanceof
        Shift,            // <<, >>, >>>
        Sum,              // +, -
        Product,          // *, /, %
        Exponent,         // ** (right-associative)
        Prefix,           // -X or !X
        Call,             // myFunction(X)
        Member,           // obj.prop, obj?.
        Index             // array[index]
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
            { TokenType.NullishAssign, Precedence.Assignment },
            { TokenType.OrAssign, Precedence.Assignment },
            { TokenType.AndAssign, Precedence.Assignment },
            { TokenType.ExponentAssign, Precedence.Assignment },
            { TokenType.NullishCoalescing, Precedence.NullishCoalesce },
            { TokenType.Or, Precedence.LogicalOr },
            { TokenType.And, Precedence.LogicalAnd },
            { TokenType.BitwiseOr, Precedence.BitwiseOr },
            { TokenType.BitwiseXor, Precedence.BitwiseXor },
            { TokenType.BitwiseAnd, Precedence.BitwiseAnd },
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
            { TokenType.LeftShift, Precedence.Shift },
            { TokenType.RightShift, Precedence.Shift },
            { TokenType.UnsignedRightShift, Precedence.Shift },
            { TokenType.Plus, Precedence.Sum },
            { TokenType.Minus, Precedence.Sum },
            { TokenType.Slash, Precedence.Product },
            { TokenType.Asterisk, Precedence.Product },
            { TokenType.Percent, Precedence.Product },
            { TokenType.Exponent, Precedence.Exponent },
            { TokenType.LParen, Precedence.Call },
            { TokenType.Dot, Precedence.Member },
            { TokenType.OptionalChain, Precedence.Member },
            { TokenType.TemplateString, Precedence.Call },
            { TokenType.LBracket, Precedence.Index },
            { TokenType.Question, Precedence.Ternary },
            { TokenType.Arrow, Precedence.Assignment },
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
            RegisterPrefix(TokenType.Number, ParseNumberLiteral);
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
            RegisterPrefix(TokenType.Class, ParseClassExpression); // NEW: Class expression
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
            RegisterPrefix(TokenType.BitwiseNot, ParseBitwiseNotExpression);  // ~x
            RegisterPrefix(TokenType.Ellipsis, ParseSpreadExpression); // ...expr (spread in arrays, objects, etc.)
            RegisterPrefix(TokenType.Default, ParseDefaultExpression); // default (in export default or switch case recovery)
            RegisterPrefix(TokenType.Catch, ParseEmptyExpression);     // Recovery for catch keyword in expression context
            RegisterPrefix(TokenType.Case, ParseEmptyExpression);      // Recovery for case keyword in expression context
            RegisterPrefix(TokenType.Assign, ParseEmptyExpression);    // Recovery for bare = in expression context
            RegisterPrefix(TokenType.Throw, ParseThrowExpression);     // throw as expression (for throw new Error pattern)
            RegisterPrefix(TokenType.Arrow, ParseEmptyExpression);     // Recovery for bare => in expression context

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
            
            // ES6+ operators
            RegisterInfix(TokenType.OptionalChain, ParseOptionalChainExpression);  // ?.
            RegisterInfix(TokenType.NullishCoalescing, ParseNullishCoalescingExpression);  // ??
            RegisterInfix(TokenType.NullishAssign, ParseLogicalAssignmentExpression);  // ??=
            RegisterInfix(TokenType.OrAssign, ParseLogicalAssignmentExpression);  // ||=
            RegisterInfix(TokenType.AndAssign, ParseLogicalAssignmentExpression);  // &&=
            RegisterInfix(TokenType.Exponent, ParseExponentiationExpression);  // **
            RegisterInfix(TokenType.ExponentAssign, ParseCompoundAssignment);  // **=
            RegisterInfix(TokenType.BitwiseAnd, ParseInfixExpression);  // &
            RegisterInfix(TokenType.BitwiseOr, ParseInfixExpression);   // |
            RegisterInfix(TokenType.BitwiseXor, ParseInfixExpression);  // ^
            RegisterInfix(TokenType.LeftShift, ParseInfixExpression);   // <<
            RegisterInfix(TokenType.RightShift, ParseInfixExpression);  // >>
            RegisterInfix(TokenType.UnsignedRightShift, ParseInfixExpression);  // >>>

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

        // ASI: Check if Automatic Semicolon Insertion should apply
        // Per ECMAScript spec, semicolon can be inserted when:
        // 1. The offending token is separated by line terminator from previous
        // 2. The offending token is }
        // 3. We've reached end of input
        private bool CanInsertSemicolon()
        {
            return _peekToken.HadLineTerminatorBefore || 
                   PeekTokenIs(TokenType.RBrace) ||
                   PeekTokenIs(TokenType.Eof);
        }

        // ASI: Expect semicolon with automatic insertion fallback
        private bool ExpectSemicolonWithASI()
        {
            if (PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
                return true;
            }
            // Apply ASI if conditions met
            return CanInsertSemicolon();
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
                    // Check for labeled statement: identifier ':'
                    if (_curToken.Type == TokenType.Identifier && PeekTokenIs(TokenType.Colon))
                    {
                        return ParseLabeledStatement();
                    }
                    return ParseExpressionStatement();
            }
        }

        private LabeledStatement ParseLabeledStatement()
        {
            var label = new Identifier(_curToken, _curToken.Literal);
            NextToken(); // consume the ':'
            NextToken(); // move to the body statement
            var body = ParseStatement();
            return new LabeledStatement
            {
                Token = label.Token,
                Label = label,
                Body = body
            };
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

            // ASI: Restricted production - if line terminator before expression,
            // semicolon, rbrace, or eof, insert semicolon and return undefined
            if (_curToken.HadLineTerminatorBefore || CurTokenIs(TokenType.Semicolon) || 
                CurTokenIs(TokenType.RBrace) || CurTokenIs(TokenType.Eof))
            {
                if (CurTokenIs(TokenType.Semicolon)) NextToken();
                return stmt; // Return undefined
            }

            stmt.ReturnValue = ParseExpression(Precedence.Lowest);

            ExpectSemicolonWithASI();

            return stmt;
        }

        private ExpressionStatement ParseExpressionStatement()
        {
            var stmt = new ExpressionStatement { Token = _curToken };

            stmt.Expression = ParseExpression(Precedence.Lowest);

            // Use ASI-aware semicolon handling
            ExpectSemicolonWithASI();

            return stmt;
        }

        public Expression ParseExpression(Precedence precedence)
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

        private Expression ParseNumberLiteral()
        {
            var literal = _curToken.Literal;

            // Handle hex (0x), octal (0o), binary (0b) prefixes
            if (literal.Length > 2 && literal[0] == '0')
            {
                char prefix = literal[1];
                if (prefix == 'x' || prefix == 'X')
                {
                    if (long.TryParse(literal.Substring(2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var hexVal))
                    {
                        return new IntegerLiteral { Token = _curToken, Value = hexVal };
                    }
                }
                else if (prefix == 'o' || prefix == 'O')
                {
                    try
                    {
                        long octalVal = Convert.ToInt64(literal.Substring(2), 8);
                        return new IntegerLiteral { Token = _curToken, Value = octalVal };
                    }
                    catch { /* fall through to error */ }
                }
                else if (prefix == 'b' || prefix == 'B')
                {
                    try
                    {
                        long binaryVal = Convert.ToInt64(literal.Substring(2), 2);
                        return new IntegerLiteral { Token = _curToken, Value = binaryVal };
                    }
                    catch { /* fall through to error */ }
                }
            }

            if (long.TryParse(literal, out var longValue))
            {
                return new IntegerLiteral { Token = _curToken, Value = longValue };
            }

            if (double.TryParse(literal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            {
                return new DoubleLiteral { Token = _curToken, Value = doubleValue };
            }

            _errors.Add($"could not parse {literal} as number");
            return null;
        }
        
        private Expression ParseStringLiteral()
        {
            return new StringLiteral { Token = _curToken, Value = _curToken.Literal };
        }

        // Parse a regular template literal: `Hello ${name}!`
        private Expression ParseTemplateLiteral()
        {
            var tmpl = new TemplateLiteral { Token = _curToken };
            string content = _curToken.Literal;

            int pos = 0;
            while (pos < content.Length)
            {
                int dollarPos = content.IndexOf("${", pos);
                if (dollarPos == -1)
                {
                    // No more expressions, add the remaining text
                    tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = content.Substring(pos), Tail = true });
                    break;
                }

                // Add the string part before ${}
                tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = content.Substring(pos, dollarPos - pos), Tail = false });

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
                    var parsed = exprParser.ParseExpression(Precedence.Lowest);
                    if (parsed != null)
                        tmpl.Expressions.Add(parsed);
                    else
                        tmpl.Expressions.Add(new UndefinedLiteral { Token = _curToken });
                }
                else
                {
                    tmpl.Expressions.Add(new UndefinedLiteral { Token = _curToken });
                }

                pos = bracePos;
            }

            // If no expressions were found, just return a plain string
            if (tmpl.Expressions.Count == 0 && tmpl.Quasis.Count <= 1)
            {
                return new StringLiteral { Token = _curToken, Value = content };
            }

            // Ensure there's a trailing quasi if the template ends with an expression
            if (tmpl.Quasis.Count == tmpl.Expressions.Count)
            {
                tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = "", Tail = true });
            }

            return tmpl;
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
                // Recovery: skip to RParen or statement boundary
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Semicolon) && 
                       !CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
                // Return the expression we parsed so far
                return exp ?? new UndefinedLiteral { Token = _curToken };
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
                // Recovery: skip to RParen or LBrace
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.LBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

            // Handle body - with or without braces
            expression.Consequence = ParseBodyAsBlock();
            if (expression.Consequence  == null) return null;

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
                if (stmt  == null) return null;
                
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
            ParseSingleParameter(identifiers);

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken(); // consume comma
                NextToken(); // move to next parameter
                ParseSingleParameter(identifiers);
            }

            if (!ExpectPeek(TokenType.RParen))
            {
                return null;
            }

            return identifiers;
        }

        /// <summary>
        /// Parse a single function parameter - handles identifiers, destructuring patterns, and rest params
        /// </summary>
        private void ParseSingleParameter(List<Identifier> identifiers)
        {
            // Handle rest parameter: ...args
            if (CurTokenIs(TokenType.Ellipsis))
            {
                NextToken();
                if (CurTokenIs(TokenType.Identifier))
                {
                    var ident = new Identifier(_curToken, _curToken.Literal) { IsRest = true };
                    identifiers.Add(ident);
                }
                else if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
                {
                    // Rest with destructuring: ...{a, b} or ...[x, y]
                    var pattern = ParseDestructuringPattern();
                    var ident = new Identifier(_curToken, $"__rest_{identifiers.Count}") { IsRest = true };
                    identifiers.Add(ident);
                }
                return;
            }

            // Handle simple identifier: a, a = 1
            if (CurTokenIs(TokenType.Identifier))
            {
                var ident = new Identifier(_curToken, _curToken.Literal);
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Comma);
                }
                identifiers.Add(ident);
                return;
            }

            // Handle object destructuring: {a, b, c = 1}
            if (CurTokenIs(TokenType.LBrace))
            {
                var pattern = ParseDestructuringPattern();
                var ident = new Identifier(_curToken, $"__destructure_{identifiers.Count}");
                
                // Check for default value: {a, b} = {}
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Comma);
                }
                identifiers.Add(ident);
                return;
            }

            // Handle array destructuring: [a, b, c = 1]
            if (CurTokenIs(TokenType.LBracket))
            {
                var pattern = ParseDestructuringPattern();
                var ident = new Identifier(_curToken, $"__array_destructure_{identifiers.Count}");
                
                // Check for default value: [a, b] = []
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Comma);
                }
                identifiers.Add(ident);
                return;
            }

            // Recovery: skip unexpected token
            return;
        }

        /// <summary>
        /// Parse a destructuring pattern (object or array)
        /// This consumes tokens until the closing brace/bracket, handling nested patterns
        /// </summary>
        private Expression ParseDestructuringPattern()
        {
            int depth = 1;
            var startToken = _curToken;
            
            // Track opening token type
            TokenType openType = _curToken.Type;
            TokenType closeType = openType == TokenType.LBrace ? TokenType.RBrace : TokenType.RBracket;
            
            // Consume until we close the pattern
            while (depth > 0 && !CurTokenIs(TokenType.Eof))
            {
                NextToken();
                
                if (CurTokenIs(openType)) depth++;
                else if (CurTokenIs(closeType)) depth--;
            }
            
            // Return a placeholder - actual destructuring is handled at runtime
            return new ObjectLiteral { Token = startToken };
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
                NextToken(); // consume comma
                
                // Handle trailing comma: func(a,b,)
                if (PeekTokenIs(TokenType.RParen))
                {
                    NextToken(); // move to RParen
                    return args;
                }
                
                NextToken(); // move to next argument
                
                // Safety check - if we hit RParen after advancing, bail out
                if (CurTokenIs(TokenType.RParen))
                {
                    return args;
                }
                
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
                // Recovery: skip to RParen or statement boundary
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Semicolon) && 
                       !CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
                return args; // Return partial args
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
                
                // Handle spread in object: { ...obj }
                if (CurTokenIs(TokenType.Ellipsis))
                {
                    NextToken(); // Move past '...'
                    var spreadArg = ParseExpression(Precedence.Assignment);
                    // Store spread as special key
                    obj.Pairs[$"__spread_{obj.Pairs.Count}"] = new SpreadElement { Token = _curToken, Argument = spreadArg };
                    
                    if (PeekTokenIs(TokenType.Comma)) NextToken();
                    continue;
                }
                
                string key = "";
                Expression computedKey = null;
                bool isComputed = false;
                
                // Handle computed property name: { [expr]: value }
                if (CurTokenIs(TokenType.LBracket))
                {
                    isComputed = true;
                    NextToken(); // Move past '['
                    computedKey = ParseExpression(Precedence.Lowest);
                    if (!ExpectPeek(TokenType.RBracket)) return null;
                    
                    // Use a placeholder key for computed properties
                    key = $"__computed_{obj.Pairs.Count}";
                }
                // Handle numeric key: { 0: value }
                else if (CurTokenIs(TokenType.Number))
                {
                    key = _curToken.Literal;
                }
                // Key can be Identifier or String or Keyword
                else if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String) || 
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

                // Check for async method: { async foo() {} }
                if (key == "async" && PeekTokenIs(TokenType.Identifier))
                {
                    // It's likely an async method
                    // Store current token in case we need to "backtrack" (conceptually)
                    // But actually if we consume 'async' and 'foo', we are committed to parsing a method or it's a syntax error
                    // because { async foo } is invalid.
                    
                    NextToken(); // Consume 'async', move to method name
                    key = _curToken.Literal;
                    
                    if (PeekTokenIs(TokenType.LParen))
                    {
                        NextToken(); // Move to '('
                        var methodParams = ParseFunctionParameters();
                        
                        if (PeekTokenIs(TokenType.LBrace))
                        {
                            NextToken(); // Move to '{'
                            var methodBody = ParseBlockStatement();
                            
                            var methodFunc = new FunctionLiteral 
                            { 
                                Token = _curToken, 
                                Parameters = methodParams, 
                                Body = methodBody,
                                IsAsync = true
                            };
                            
                            obj.Pairs[key] = methodFunc;
                            
                            if (PeekTokenIs(TokenType.Comma)) NextToken();
                            continue;
                        }
                    }
                    // If we get here, it looked like async method but wasn't fully valid?
                    // e.g. { async foo : 1 } - Invalid syntax
                    // { async foo } - Invalid
                    // So we can probably let it error out or return null?
                    // But we consumed tokens. Code structure suggests we return null or add error.
                    _errors.Add($"[Debug] ParseObjectLiteral: expected async method body");
                    return null;
                }

                // Check for getter/setter: { get foo() {}, set foo(v) {} }
                if ((key == "get" || key == "set") && PeekTokenIs(TokenType.Identifier))
                {
                    var accessor = key;
                    NextToken(); // Move to property name
                    key = _curToken.Literal;
                    
                    if (PeekTokenIs(TokenType.LParen))
                    {
                        NextToken(); // Move to '('
                        var methodParams = ParseFunctionParameters();
                        
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
                            
                            // Prefix key with getter/setter marker
                            obj.Pairs[$"__{accessor}_{key}"] = methodFunc;
                            
                            if (PeekTokenIs(TokenType.Comma)) NextToken();
                            continue;
                        }
                    }
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
                    // Arrow function method: { foo: (x) => x + 1 } parsed as method call by mistake
                    // Try to recover by treating "key" + "(args...)" as a call expression value
                    // This happens when: obj.method(args) inside an object literal
                    // Skip to next comma or rbrace to recover
                    _errors.Add($"[Debug] ParseObjectLiteral: expected {{ after method params, got: {_peekToken.Type}, recovering...");
                    // Try to continue parsing by skipping to comma or rbrace
                    while (!PeekTokenIs(TokenType.Comma) && !PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Eof))
                    {
                        NextToken();
                    }
                    if (PeekTokenIs(TokenType.Comma)) NextToken();
                    continue;
                }

                // Check for regular key: value, destructuring key = value, or shorthand key
                if (PeekTokenIs(TokenType.Colon))
                {
                    NextToken(); // Consume :
                    NextToken(); // Move to value
                    var value = ParseExpression(Precedence.Comma);
                    obj.Pairs[key] = value;
                    if (isComputed && computedKey != null)
                        obj.ComputedKeys[key] = computedKey;
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

            NextToken(); // Move past '.'
            
            // In JavaScript, keywords can be used as property names after dot
            // Examples: obj.default, obj.catch, obj.class, obj.function
            if (CurTokenIs(TokenType.Identifier))
            {
                exp.Property = _curToken.Literal;
            }
            else if (IsKeywordToken(_curToken.Type))
            {
                // Allow keywords as property names
                exp.Property = _curToken.Literal;
            }
            else
            {
                _errors.Add($"expected property name, got {_curToken.Type} instead");
                return null;
            }

            return exp;
        }

        /// <summary>
        /// Check if a token type is a JavaScript keyword that can be used as property name
        /// </summary>
        private bool IsKeywordToken(TokenType type)
        {
            return type == TokenType.Default ||
                   type == TokenType.Catch ||
                   type == TokenType.Class ||
                   type == TokenType.Function ||
                   type == TokenType.Async ||
                   type == TokenType.Await ||
                   type == TokenType.Return ||
                   type == TokenType.Throw ||
                   type == TokenType.New ||
                   type == TokenType.Delete ||
                   type == TokenType.Typeof ||
                   type == TokenType.Void ||
                   type == TokenType.In ||
                   type == TokenType.Instanceof ||
                   type == TokenType.This ||
                   type == TokenType.If ||
                   type == TokenType.Else ||
                   type == TokenType.While ||
                   type == TokenType.For ||
                   type == TokenType.Do ||
                   type == TokenType.Break ||
                   type == TokenType.Continue ||
                   type == TokenType.Switch ||
                   type == TokenType.Case ||
                   type == TokenType.Try ||
                   type == TokenType.Finally ||
                   type == TokenType.Import ||
                   type == TokenType.Export ||
                   type == TokenType.From ||
                   type == TokenType.As ||
                   type == TokenType.Const ||
                   type == TokenType.Let ||
                   type == TokenType.Var ||
                   type == TokenType.Static ||
                   type == TokenType.Extends ||

                   type == TokenType.Yield ||
                   type == TokenType.True ||
                   type == TokenType.False ||
                   type == TokenType.Null;
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
            
            // Parse the right side with Precedence.Comma to allow assignments but stop at comma
            exp.Right = ParseExpression(Precedence.Comma);
            
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

            // Automatic Semicolon Insertion (ASI)
            // If we expect a semicolon, but don't see one, we can insert one if:
            // 1. The next token is preceded by a line terminator
            // 2. The next token is } (closing brace)
            // 3. The next token is EOF
            if (type == TokenType.Semicolon)
            {
                if (_peekToken.HadLineTerminatorBefore || 
                    _peekToken.Type == TokenType.RBrace || 
                    _peekToken.Type == TokenType.Eof)
                {
                    // Virtual semicolon inserted - do not consume next token
                    return true;
                }
            }

            PeekError(type);
            return false;
        }

        private void PeekError(TokenType type)
        {
            var msg = $"expected next token to be {type}, got {_peekToken.Type} instead";
            if (_lexer != null)
            {
                 msg += $"\nContext:\n{_lexer.GetCodeContext(_peekToken.Line, _peekToken.Column)}";
            }
            _errors.Add(msg);
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
                
                // Optional catch parameter: catch(e) or catch({message}) or catch (no param - ES2019)
                if (PeekTokenIs(TokenType.LParen))
                {
                    NextToken(); // move to (
                    NextToken(); // move past (
                    
                    // Handle destructuring: catch({message})
                    if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
                    {
                        // Skip destructuring pattern
                        ParseDestructuringPattern();
                        stmt.CatchParameter = new Identifier(_curToken, "__catch_destructure");
                    }
                    else if (CurTokenIs(TokenType.Identifier))
                    {
                        stmt.CatchParameter = new Identifier(_curToken, _curToken.Literal);
                    }
                    // else: empty catch parameter - unusual but allowed in some cases
                    
                    if (!ExpectPeek(TokenType.RParen)) return null;
                }
                // else: catch without parameter (ES2019 optional catch binding)

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
                return list;
            }

            NextToken();
            
            // Handle leading elision: [,a] or [,,a]
            while (CurTokenIs(TokenType.Comma))
            {
                list.Add(new UndefinedLiteral { Token = _curToken }); // Elided element
                NextToken();
            }
            
            // Check if we're now at the end after elisions
            if (CurTokenIs(end))
            {
                return list;
            }
            
            // Parse first actual element
            if (CurTokenIs(TokenType.Ellipsis))
            {
                NextToken();
                list.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Assignment) });
            }
            else
            {
                var expr = ParseExpression(Precedence.Assignment);
                list.Add(expr);
            }

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken(); // consume comma
                
                // Handle elision: [a,,b] or trailing comma [a,]
                if (PeekTokenIs(TokenType.Comma))
                {
                    // Elided element - insert undefined
                    list.Add(new UndefinedLiteral { Token = _curToken });
                    continue;
                }
                
                if (PeekTokenIs(end))
                {
                    // Trailing comma - just break
                    break;
                }
                
                NextToken(); // move to next element
                
                if (CurTokenIs(TokenType.Ellipsis))
                {
                    NextToken();
                    list.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Assignment) });
                }
                else
                {
                    var expr = ParseExpression(Precedence.Assignment);
                    list.Add(expr);
                }
            }

            if (!ExpectPeek(end))
            {
                // Recovery: try to skip to end token instead of returning null
                while (!CurTokenIs(end) && !CurTokenIs(TokenType.Eof) && 
                       !CurTokenIs(TokenType.Semicolon) && !CurTokenIs(TokenType.RBrace))
                {
                    NextToken();
                }
                // Return partial list instead of null to allow parsing to continue
                return list;
            }

            return list;
        }



        private Statement ParseThrowStatement()
        {
            var stmt = new ThrowStatement { Token = _curToken };

            NextToken();

            // ASI: Restricted production - line terminator before expression is illegal
            // but we handle it gracefully by continuing
            if (_curToken.HadLineTerminatorBefore)
            {
                // This is a syntax error per spec, but we recover
                return stmt;
            }

            stmt.Value = ParseExpression(Precedence.Lowest);

            ExpectSemicolonWithASI();

            return stmt;
        }



        private Statement ParseWhileStatement()
        {
            var stmt = new WhileStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.LParen)) return null;

            NextToken();
            stmt.Condition = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
            {
                // Recovery: skip to RParen or LBrace
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.LBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

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

            // Check for destructuring pattern: for (const [a,b] of ...) or for (const {a,b} of ...)
            if (CurTokenIs(TokenType.LBracket) || CurTokenIs(TokenType.LBrace))
            {
                var pattern = CurTokenIs(TokenType.LBracket) ? ParseArrayLiteral() : ParseObjectLiteral();
                if (PeekTokenIs(TokenType.Of))
                {
                    NextToken(); // move to 'of'
                    NextToken(); // move to iterable
                    var iterExpr = ParseExpression(Precedence.Lowest);
                    if (!ExpectPeek(TokenType.RParen)) return null;
                    return new ForOfStatement { Token = forToken, DestructuringPattern = pattern, Iterable = iterExpr, Body = ParseBodyAsBlock() };
                }
                if (PeekTokenIs(TokenType.In))
                {
                    NextToken(); // move to 'in'
                    NextToken(); // move to object
                    var objExpr = ParseExpression(Precedence.Lowest);
                    if (!ExpectPeek(TokenType.RParen)) return null;
                    return new ForInStatement { Token = forToken, DestructuringPattern = pattern, Object = objExpr, Body = ParseBodyAsBlock() };
                }
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
                // Recovery: skip to semicolon or RParen
                while (!CurTokenIs(TokenType.Semicolon) && !CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

            // Condition
            if (!PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
                stmt.Condition = ParseExpression(Precedence.Lowest);
            }
            if (!ExpectPeek(TokenType.Semicolon))
            {
                // Recovery: skip to next semicolon or RParen
                while (!CurTokenIs(TokenType.Semicolon) && !CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

            // Update
            if (!PeekTokenIs(TokenType.RParen))
            {
                NextToken();
                var exp = ParseExpression(Precedence.Lowest);
                stmt.Update = new ExpressionStatement { Expression = exp };
            }

            if (!ExpectPeek(TokenType.RParen))
            {
                // Recovery: skip to RParen or LBrace
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.LBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

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

        private Expression ParseClassExpression()
        {
            var exp = new ClassExpression { Token = _curToken };

            // Optional name
            if (PeekTokenIs(TokenType.Identifier))
            {
                NextToken();
                exp.Name = new Identifier(_curToken, _curToken.Literal);
            }

            if (PeekTokenIs(TokenType.Extends))
            {
                NextToken(); // extends
                NextToken(); // superClassExpression - usually identifier but can be expression
                // For simplicity, assume identifier for now as per ClassStatement, or execute ParseExpression(Precedence.Lowest) if needed?
                // ClassStatement uses Identifier. Let's stick to Identifier for consistency with ClassStatement for now.
                // But Class expressions allow expressions: class extends (mixin(Base)) {}
                // Keep it simple first.
                exp.SuperClass = new Identifier(_curToken, _curToken.Literal);
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
                    exp.Methods.Add(method);
                }
                else if (member is ClassProperty prop)
                {
                    exp.Properties.Add(prop);
                }
                NextToken();
            }

            if (!ExpectPeek(TokenType.RBrace))
            {
                return null;
            }

            return exp;
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
            
            // Caller guarantees _curToken is LBrace, so we just consume it to enter the block
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
            
            // Check for Async Arrow Function: async x => ...
            // Must be on the same line to be an arrow function arg
            if (PeekTokenIs(TokenType.Identifier) && !_peekToken.HadLineTerminatorBefore)
            {
                // This is likely 'async arg => ...'
                // Consume 'async' (already current) and move to arg
                NextToken(); 
                var arg = new Identifier(_curToken, _curToken.Literal);
                
                if (PeekTokenIs(TokenType.Arrow))
                {
                    var arrow = new ArrowFunctionExpression 
                    { 
                        Token = token,
                        IsAsync = true,
                        Parameters = new List<Identifier> { arg }
                    };
                    
                    NextToken(); // Move to '=>'
                    NextToken(); // Move past '=>'
                    
                    // Parse body
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
                
                 // If '=>' is NOT next, we might have consumed an identifier we shouldn't have?
                 // But 'async x' is invalid expression syntax anyway.
                 _errors.Add($"expected '=>' after async argument, got {_peekToken.Type}");
                 return null;
            }

            // If not followed by function, treat 'async' as an identifier
            // This allows 'async' to be used as a variable name in expressions
            return new Identifier(token, "async");
        }

        /// <summary>
        /// Parse spread expression: ...expr
        /// Used in arrays, function calls, and object literals
        /// </summary>
        private Expression ParseSpreadExpression()
        {
            var token = _curToken;
            NextToken(); // Move past '...'
            
            var argument = ParseExpression(Precedence.Assignment);
            return new SpreadElement { Token = token, Argument = argument };
        }

        /// <summary>
        /// Parse default keyword in expression context
        /// Primarily for 'export default' or recovery scenarios
        /// </summary>
        private Expression ParseDefaultExpression()
        {
            // In 'export default expr', default acts like a prefix
            // Just treat as identifier for recovery
            return new Identifier(_curToken, "default");
        }

        /// <summary>
        /// Parse throw as an expression (for arrow function throw patterns)
        /// e.g., const f = () => throw new Error()
        /// </summary>
        private Expression ParseThrowExpression()
        {
            var token = _curToken;
            NextToken(); // Move past 'throw'
            var argument = ParseExpression(Precedence.Lowest);
            // Return as a special expression - the interpreter handles it
            return new ThrowExpression { Token = token, Value = argument };
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

                if (!ExpectPeek(TokenType.Colon))
        {
            // Recovery: skip to next case/default/rbrace
            while (!CurTokenIs(TokenType.Colon) && !CurTokenIs(TokenType.Case) && 
                   !CurTokenIs(TokenType.Default) && !CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
            {
                NextToken();
            }
        }

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
            if (stmt.Body  == null) return null;

            // Expect 'while'
            if (!ExpectPeek(TokenType.While)) return null;
            if (!ExpectPeek(TokenType.LParen)) return null;

            NextToken();
            stmt.Condition = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
    {
        // Recovery: skip to RParen or semicolon
        while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Semicolon) && !CurTokenIs(TokenType.Eof))
        {
            NextToken();
        }
    }
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
            exp.Consequent = ParseExpression(Precedence.Comma);

            if (!ExpectPeek(TokenType.Colon))
            {
                _errors.Add($"Expected ':' in ternary expression, got {_peekToken.Type}");
                return null;
            }

            NextToken(); // Move past ':'
            exp.Alternate = ParseExpression(Precedence.Comma);

            return exp;
        }

        // Parse arrow function when => is encountered after grouped expression (params)
        private Expression ParseArrowFunctionFromParams(Expression left)
        {
            var arrow = new ArrowFunctionExpression { Token = _curToken };

            // Handle async (args) => ...
            // The parser sees 'async' (identifier) + '(' (call) -> CallExpression
            if (left is CallExpression callExp && 
                callExp.Function is Identifier funcId && 
                funcId.Value == "async")
            {
                arrow.IsAsync = true;
                arrow.Parameters = new List<Identifier>();
                
                // Convert call arguments to parameters
                foreach (var arg in callExp.Arguments)
                {
                    if (arg is Identifier id)
                    {
                        arrow.Parameters.Add(id);
                    }
                    else if (arg is AssignmentExpression assign && assign.Left is Identifier assignId)
                    {
                        // Default value: async (x = 1) => ...
                        assignId.DefaultValue = assign.Right;
                        arrow.Parameters.Add(assignId);
                    }
                    else
                    {
                         // Recover from complex patterns by creating placeholder
                         arrow.Parameters.Add(new Identifier(arg.Token, $"__async_arg_{arrow.Parameters.Count}"));
                    }
                }
            }
            else
            {
                // Extract parameters from left side (grouped expression or identifier)
                arrow.Parameters = ExtractArrowParameters(left);
            }

            NextToken(); // Move past '=>'

            // Parse body: either block statement or expression
            if (CurTokenIs(TokenType.LBrace))
            {
                arrow.Body = ParseBlockStatement();
            }
            else
            {
                arrow.Body = ParseExpression(Precedence.Comma);
            }

            return arrow;
        }

        // Helper to convert left-side expression(s) into parameter list
        private List<Identifier> ExtractArrowParameters(Expression left)
        {
            var parameters = new List<Identifier>();

            if (left  == null)
            {
                return parameters;
            }

            ExtractSingleParam(left, parameters);
            return parameters;
        }

        /// <summary>
        /// Extract a single parameter from an expression - handles identifiers, destructuring, defaults
        /// </summary>
        private void ExtractSingleParam(Expression expr, List<Identifier> parameters)
        {
            if (expr == null) return;

            // Simple identifier: x => x * 2
            if (expr is Identifier id)
            {
                parameters.Add(id);
                return;
            }

            // Comma-separated: (a, b) 
            if (expr is InfixExpression infix && infix.Operator == ",")
            {
                ExtractSingleParam(infix.Left, parameters);
                ExtractSingleParam(infix.Right, parameters);
                return;
            }

            // Assignment expression for default value: (a = 1)
            if (expr is AssignmentExpression assign && assign.Left is Identifier assignId)
            {
                assignId.DefaultValue = assign.Right;
                parameters.Add(assignId);
                return;
            }

            // Object destructuring: ({a, b}) - create placeholder parameter
            if (expr is ObjectLiteral objLit)
            {
                var placeholder = new Identifier(objLit.Token, $"__destructure_{parameters.Count}");
                parameters.Add(placeholder);
                return;
            }

            // Array destructuring: ([a, b]) - create placeholder parameter
            if (expr is ArrayLiteral arrLit)
            {
                var placeholder = new Identifier(arrLit.Token, $"__array_destructure_{parameters.Count}");
                parameters.Add(placeholder);
                return;
            }

            // Spread element: (...args)
            if (expr is SpreadElement spread && spread.Argument is Identifier spreadId)
            {
                spreadId.IsRest = true;
                parameters.Add(spreadId);
                return;
            }

            // For unrecognized patterns, create a placeholder to allow parsing to continue
            if (expr is Expression)
            {
                var placeholder = new Identifier(expr.Token, $"__unknown_{parameters.Count}");
                parameters.Add(placeholder);
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
            // Verify if we actully enter here
            Console.WriteLine("[Parser] ENTERING ParseRegexLiteral (Slash prefix)");
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
            Console.WriteLine($"[Parser] ENTERING ParseRegexToken. Literal: {_curToken.Literal}");
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

        // ES6+ Parse optional chaining: obj?.prop, obj?.[key], obj?.()
        private Expression ParseOptionalChainExpression(Expression left)
        {
            var token = _curToken;
            var expr = new OptionalChainExpression { Token = token, Object = left };
            
            NextToken(); // Move past ?.
            
            // Check what follows the ?.
            if (CurTokenIs(TokenType.Identifier))
            {
                // obj?.prop
                expr.PropertyName = _curToken.Literal;
                expr.IsComputed = false;
            }
            else if (CurTokenIs(TokenType.LBracket))
            {
                // obj?.[expr]
                NextToken(); // Move past [
                expr.Property = ParseExpression(Precedence.Lowest);
                expr.IsComputed = true;
                if (!ExpectPeek(TokenType.RBracket))
                {
                    return null;
                }
            }
            else if (CurTokenIs(TokenType.LParen))
            {
                // obj?.()
                expr.IsCall = true;
                expr.Arguments = ParseCallArguments();
            }
            
            return expr;
        }

        // ES6+ Parse nullish coalescing: a ?? b
        private Expression ParseNullishCoalescingExpression(Expression left)
        {
            var token = _curToken;
            var precedence = CurPrecedence();
            NextToken();
            var right = ParseExpression(precedence);
            
            return new NullishCoalescingExpression
            {
                Token = token,
                Left = left,
                Right = right
            };
        }

        // ES6+ Parse logical assignment: a ||= b, a &&= b, a ??= b
        private Expression ParseLogicalAssignmentExpression(Expression left)
        {
            var token = _curToken;
            var op = token.Literal;
            NextToken();
            var right = ParseExpression(Precedence.Assignment);
            
            return new LogicalAssignmentExpression
            {
                Token = token,
                Left = left,
                Operator = op,
                Right = right
            };
        }

        // ES6+ Parse exponentiation: a ** b (right-associative)
        private Expression ParseExponentiationExpression(Expression left)
        {
            var token = _curToken;
            NextToken();
            // Right-associative: parse with same precedence - 1 to allow right recursion
            var right = ParseExpression(Precedence.Exponent - 1);
            
            return new ExponentiationExpression
            {
                Token = token,
                Left = left,
                Right = right
            };
        }

        // ES6+ Parse bitwise NOT prefix: ~x
        private Expression ParseBitwiseNotExpression()
        {
            var token = _curToken;
            NextToken();
            return new BitwiseNotExpression
            {
                Token = token,
                Operand = ParseExpression(Precedence.Prefix)
            };
        }
    }
}
