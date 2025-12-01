using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Core
{
    public enum Precedence
    {
        Lowest,
        Comma,       // ,
        Assignment,  // =
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
            { TokenType.Or, Precedence.LogicalOr },
            { TokenType.And, Precedence.LogicalAnd },
            { TokenType.Eq, Precedence.Equals },
            { TokenType.NotEq, Precedence.Equals },
            { TokenType.Lt, Precedence.LessGreater },
            { TokenType.Gt, Precedence.LessGreater },
            { TokenType.LtEq, Precedence.LessGreater },
            { TokenType.GtEq, Precedence.LessGreater },
            { TokenType.Plus, Precedence.Sum },
            { TokenType.Minus, Precedence.Sum },
            { TokenType.Slash, Precedence.Product },
            { TokenType.Asterisk, Precedence.Product },
            { TokenType.LParen, Precedence.Call },
            { TokenType.Dot, Precedence.Call },  // Member access has same precedence as function calls
            { TokenType.LBracket, Precedence.Index },
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
                case TokenType.Const: // Treat const/var as let for now
                    return ParseLetStatement();
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
                case TokenType.Semicolon:
                    return null; // Ignore empty statements
                default:
                    return ParseExpressionStatement();
            }
        }

        private LetStatement ParseLetStatement()
        {
            var stmt = new LetStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.Identifier))
            {
                return null;
            }

            stmt.Name = new Identifier(_curToken, _curToken.Literal);

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

            if (!ExpectPeek(TokenType.LBrace))
            {
                return null;
            }

            expression.Consequence = ParseBlockStatement();

            if (PeekTokenIs(TokenType.Else))
            {
                NextToken();

                if (!ExpectPeek(TokenType.LBrace))
                {
                    return null;
                }

                expression.Alternative = ParseBlockStatement();
            }

            return expression;
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

        private List<Identifier> ParseFunctionParameters()
        {
            var identifiers = new List<Identifier>();

            if (PeekTokenIs(TokenType.RParen))
            {
                NextToken();
                return identifiers;
            }

            NextToken();

            identifiers.Add(new Identifier(_curToken, _curToken.Literal));

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken();
                NextToken();
                identifiers.Add(new Identifier(_curToken, _curToken.Literal));
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
            args.Add(ParseExpression(Precedence.Lowest));

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken();
                NextToken();
                args.Add(ParseExpression(Precedence.Lowest));
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
                
                // Key can be Identifier or String
                if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String))
                {
                    key = _curToken.Literal;
                }
                else
                {
                    // Error
                    return null;
                }

                if (!ExpectPeek(TokenType.Colon))
                {
                    _errors.Add($"[Debug] ParseObjectLiteral failed for key: {key}, Next token: {_peekToken.Type}, Literal: {_peekToken.Literal}");
                    return null;
                }

                NextToken();
                var value = ParseExpression(Precedence.Lowest);

                obj.Pairs[key] = value;

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
                return list;
            }

            NextToken();
            list.Add(ParseExpression(Precedence.Lowest));

            while (PeekTokenIs(TokenType.Comma))
            {
                NextToken();
                NextToken();
                list.Add(ParseExpression(Precedence.Lowest));
            }

            if (!ExpectPeek(end))
            {
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

            if (!ExpectPeek(TokenType.LBrace)) return null;

            stmt.Body = ParseBlockStatement();

            return stmt;
        }

        private Statement ParseForStatement()
        {
            var stmt = new ForStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.LParen)) return null;

            NextToken();

            // Init
            if (!PeekTokenIs(TokenType.Semicolon))
            {
                NextToken(); // Advance to start of init
                if (CurTokenIs(TokenType.Var) || CurTokenIs(TokenType.Let) || CurTokenIs(TokenType.Const))
                {
                    stmt.Init = ParseLetStatement();
                }
                else
                {
                    stmt.Init = ParseExpressionStatement();
                }
            }
            
            if (stmt.Init == null)
            {
                if (!ExpectPeek(TokenType.Semicolon)) 
                {
                    _errors.Add($"[Debug] ParseForStatement failed. Expected Semicolon after null init, got {_peekToken.Type}");
                    return null;
                }
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

            if (!ExpectPeek(TokenType.LBrace)) return null;

            stmt.Body = ParseBlockStatement();

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
    }
}
