using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;

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
        private bool _noIn = false; // Disables 'in' as infix operator (for for-loop init expressions)
        private bool _isStrictMode = false; // Track strict mode for reserved word validation
        private readonly bool _isModule;
        private int _functionDepth = 0;
        private int _classDepth = 0;
        private int _classStaticBlockDepth = 0;
        private int _asyncFunctionDepth = 0;
        private int _generatorFunctionDepth = 0;
        private int _arrowFunctionDepth = 0;
        private int _methodContextDepth = 0;
        private bool _inFormalParameters = false;
        private bool _inClassFieldInitializer = false;
        private int _moduleDeclarationNestingDepth = 0;
        private bool _lastParsedParamsIsSimple = true;
        private bool _lastParsedParamsHasDuplicateNames = false;
        private bool _lastParsedParamsHadTrailingCommaAfterRest = false;
        private readonly Stack<HashSet<string>> _privateNameScopeStack = new Stack<HashSet<string>>();
        private readonly Stack<bool> _classHasHeritageStack = new Stack<bool>();
        private readonly bool _allowReturnOutsideFunction = false;
        private readonly bool _allowNewTargetOutsideFunction = false;
        private readonly bool _allowSuperOutsideClass = false;
        private readonly bool _allowSuperInClassFieldInitializer = false;
        private readonly bool _allowRecovery = true;

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
            { TokenType.ModuloAssign, Precedence.Assignment },
            { TokenType.LeftShiftAssign, Precedence.Assignment },
            { TokenType.RightShiftAssign, Precedence.Assignment },
            { TokenType.UnsignedRightShiftAssign, Precedence.Assignment },
            { TokenType.BitwiseAndAssign, Precedence.Assignment },
            { TokenType.BitwiseOrAssign, Precedence.Assignment },
            { TokenType.BitwiseXorAssign, Precedence.Assignment },
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
            { TokenType.TemplateNoSubst, Precedence.Call },
            { TokenType.TemplateHead, Precedence.Call },
            { TokenType.LBracket, Precedence.Index },
            { TokenType.Question, Precedence.Ternary },
            { TokenType.Arrow, Precedence.Assignment },
            // Postfix update binds tighter than unary prefix so `!!o++` parses as `!!(o++)`.
            { TokenType.Increment, Precedence.Call },
            { TokenType.Decrement, Precedence.Call },
        };

        public Parser(
            Lexer lexer,
            bool isModule = false,
            bool allowReturnOutsideFunction = false,
            bool initialStrictMode = false,
            bool allowNewTargetOutsideFunction = false,
            bool allowSuperOutsideClass = false,
            bool allowSuperInClassFieldInitializer = false,
            bool allowRecovery = true)
        {
            _lexer = lexer;
            _isModule = isModule;
            _lexer.TreatHtmlLikeCommentsAsComments = !isModule;
            _allowReturnOutsideFunction = allowReturnOutsideFunction;
            _isStrictMode = initialStrictMode || isModule;
            _allowNewTargetOutsideFunction = allowNewTargetOutsideFunction;
            _allowSuperOutsideClass = allowSuperOutsideClass;
            _allowSuperInClassFieldInitializer = allowSuperInClassFieldInitializer;
            _allowRecovery = allowRecovery;
            _prefixParseFns = new Dictionary<TokenType, Func<Expression>>();
            _infixParseFns = new Dictionary<TokenType, Func<Expression, Expression>>();



            // Register prefix parsers
            RegisterPrefix(TokenType.Identifier, ParseIdentifier);
            RegisterPrefix(TokenType.Number, ParseNumberLiteral);
            RegisterPrefix(TokenType.BigInt, ParseBigIntLiteral);
            RegisterPrefix(TokenType.String, ParseStringLiteral);
            RegisterPrefix(TokenType.TemplateNoSubst, ParseTemplateLiteral);
            RegisterPrefix(TokenType.TemplateHead, ParseTemplateLiteral);
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
            RegisterPrefix(TokenType.Super, ParseIdentifier); // Handle 'super' as identifier
            RegisterPrefix(TokenType.Static, ParseIdentifier); // Handle 'static' as identifier in expression context
            RegisterPrefix(TokenType.Typeof, ParsePrefixExpression);  // typeof x
            RegisterPrefix(TokenType.Void, ParsePrefixExpression);    // void x
            RegisterPrefix(TokenType.Delete, ParsePrefixExpression);  // delete x
            RegisterPrefix(TokenType.Increment, ParsePrefixIncrement); // ++x
            RegisterPrefix(TokenType.Decrement, ParsePrefixDecrement); // --x
            RegisterPrefix(TokenType.Slash, ParseRegexLiteral);        // /pattern/flags
            RegisterPrefix(TokenType.Regex, ParseRegexToken);          // Already lexed regex
            RegisterPrefix(TokenType.Semicolon, ParseEmptyExpression); // Empty expression (;)
            RegisterPrefix(TokenType.Yield, ParseYieldExpression);     // yield and yield*
            RegisterPrefix(TokenType.Await, ParseAwaitExpression);     // await ...
            RegisterPrefix(TokenType.Async, ParseAsyncPrefix);         // async ...
            RegisterPrefix(TokenType.As, ParseIdentifier);             // contextual keyword as identifier reference
            RegisterPrefix(TokenType.From, ParseIdentifier);           // contextual keyword from identifier reference
            RegisterPrefix(TokenType.Of, ParseIdentifier);             // contextual keyword of identifier reference
            RegisterPrefix(TokenType.Let, ParseIdentifier);            // contextual keyword let identifier reference
            RegisterPrefix(TokenType.PrivateIdentifier, ParsePrivateIdentifier); // #field (private class fields)
            RegisterPrefix(TokenType.Plus, ParsePrefixExpression);     // +x (unary plus)
            RegisterPrefix(TokenType.BitwiseNot, ParseBitwiseNotExpression);  // ~x
            RegisterPrefix(TokenType.Ellipsis, ParseSpreadExpression); // ...expr (spread in arrays, objects, etc.)
            RegisterPrefix(TokenType.Default, ParseDefaultExpression); // default (in export default or switch case recovery)
            RegisterPrefix(TokenType.Import, ParseImportExpression);   // import.meta or import(...)
            
            // ES2021 Logical Assignment
            RegisterInfix(TokenType.OrAssign, ParseLogicalAssignmentExpression);
            RegisterInfix(TokenType.AndAssign, ParseLogicalAssignmentExpression);
            RegisterInfix(TokenType.NullishAssign, ParseLogicalAssignmentExpression);
            
            // ES2020 Nullish Coalescing
            RegisterInfix(TokenType.NullishCoalescing, ParseNullishCoalescingExpression);
            
            RegisterPrefix(TokenType.Throw, ParseThrowExpression);     // throw as expression (for throw new Error pattern)
            if (_allowRecovery)
            {
                RegisterPrefix(TokenType.Colon, ParseEmptyExpression);     // Recovery for labeled statements
                RegisterPrefix(TokenType.Comma, ParseEmptyExpression);     // Recovery for trailing commas
                RegisterPrefix(TokenType.RBrace, ParseEmptyExpression);    // Recovery for empty blocks
                RegisterPrefix(TokenType.RParen, ParseEmptyExpression);    // Recovery for empty parens
                RegisterPrefix(TokenType.RBracket, ParseEmptyExpression);  // Recovery for empty brackets
                RegisterPrefix(TokenType.Else, ParseEmptyExpression);      // Recovery for else without if
                RegisterPrefix(TokenType.Dot, ParseEmptyExpression);       // Recovery for leading dots
                RegisterPrefix(TokenType.Lt, ParseEmptyExpression);        // Recovery for bare < (like in HTML)
                RegisterPrefix(TokenType.Asterisk, ParseEmptyExpression);  // Recovery for bare *
                RegisterPrefix(TokenType.Catch, ParseEmptyExpression);     // Recovery for catch keyword in expression context
                RegisterPrefix(TokenType.Case, ParseEmptyExpression);      // Recovery for case keyword in expression context
                RegisterPrefix(TokenType.Assign, ParseEmptyExpression);    // Recovery for bare = in expression context
                RegisterPrefix(TokenType.Arrow, ParseEmptyExpression);     // Recovery for bare => in expression context
            }



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
            RegisterInfix(TokenType.TemplateString, ParseTaggedTemplate);  // Legacy tagged-template token
            RegisterInfix(TokenType.TemplateNoSubst, ParseTaggedTemplate); // tag`template`
            RegisterInfix(TokenType.TemplateHead, ParseTaggedTemplate);    // tag`template ${expr}`
            
            // ES6+ operators
            RegisterInfix(TokenType.OptionalChain, ParseOptionalChainExpression);  // ?.
            RegisterInfix(TokenType.NullishCoalescing, ParseNullishCoalescingExpression);  // ??
            RegisterInfix(TokenType.NullishAssign, ParseLogicalAssignmentExpression);  // ??=
            RegisterInfix(TokenType.OrAssign, ParseLogicalAssignmentExpression);  // ||=
            RegisterInfix(TokenType.AndAssign, ParseLogicalAssignmentExpression);  // &&=
            RegisterInfix(TokenType.Exponent, ParseExponentiationExpression);  // **
            RegisterInfix(TokenType.ExponentAssign, ParseCompoundAssignment);  // **=
            RegisterInfix(TokenType.ModuloAssign, ParseCompoundAssignment);  // %=
            RegisterInfix(TokenType.LeftShiftAssign, ParseCompoundAssignment);  // <<=
            RegisterInfix(TokenType.RightShiftAssign, ParseCompoundAssignment);  // >>=
            RegisterInfix(TokenType.UnsignedRightShiftAssign, ParseCompoundAssignment);  // >>>=
            RegisterInfix(TokenType.BitwiseAndAssign, ParseCompoundAssignment);  // &=
            RegisterInfix(TokenType.BitwiseOrAssign, ParseCompoundAssignment);  // |=
            RegisterInfix(TokenType.BitwiseXorAssign, ParseCompoundAssignment);  // ^=
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
            // Console.WriteLine("[UNIQUE_TRACE] Parser.ParseProgram called");
            var program = new Program();

            // Detect "use strict" directive at the beginning of the program
            if (_curToken.Type == TokenType.String && (_curToken.Literal == "use strict" || _curToken.Literal == "\"use strict\"" || _curToken.Literal == "'use strict'"))
            {
                _isStrictMode = true;
            }

            while (_curToken.Type != TokenType.Eof)
            {
                var stmt = ParseStatement();
                if (stmt != null)
                {
                    program.Statements.Add(stmt);
                    // Check first statement for "use strict" directive
                    if (program.Statements.Count == 1 && stmt is ExpressionStatement es &&
                        es.Expression is StringLiteral sl && (sl.Value == "use strict" || sl.Value == "\"use strict\"" || sl.Value == "'use strict'"))
                    {
                        _isStrictMode = true;
                    }
                }

                if (_curToken.Type != TokenType.Eof)
                {
                    NextToken();
                }
            }

            ValidateModuleTopLevelEarlyErrors(program);
            if (_isStrictMode && ProgramContainsLegacyOctalLiteral(program))
            {
                _errors.Add("SyntaxError: Legacy octal literals are not allowed in strict mode");
            }
            program.IsStrict = _isStrictMode;
            return program;
        }

        private Statement ParseStatement()
        {
            // Debug via errors to ensure visibility
            // _errors.Add($"[DEBUG-STMT] ParseStatement: {_curToken.Type}");

            if (CurTokenIs(TokenType.Await) &&
                !_peekToken.HadLineTerminatorBefore &&
                _peekToken.Type == TokenType.Identifier &&
                string.Equals(_peekToken.Literal, "using", StringComparison.Ordinal))
            {
                return ParseUsingDeclarationStatement(isAwaitUsing: true);
            }

            if (_isModule && IsModuleHtmlCommentTokenStart())
            {
                _errors.Add("SyntaxError: HTML-like comments are not allowed in module code");
            }

            // Recovery: if the parser has already advanced to a new switch-clause
            // boundary, let the enclosing switch parser consume it instead of
            // mis-parsing `default:` as an expression statement.
            if (CurTokenIs(TokenType.Default) && PeekTokenIs(TokenType.Colon))
            {
                return null;
            }
            
            // Recovery: Orphaned 'catch' or 'finally' clause at statement level.
            // This occurs in minified JS (e.g. `}catch(e){...}`) when the block-depth
            // heuristic misidentifies a nested `}` as the try block's closing brace,
            // causing the outer loop to advance past it and exposing `catch`/`finally`.
            if (CurTokenIs(TokenType.Catch))
            {
                _errors.Add($"SyntaxError: Orphaned 'catch' clause (no preceding try block) at line {_curToken.Line}, col {_curToken.Column} - skipping clause body");
                return null;
            }
            
            if (CurTokenIs(TokenType.Finally))
            {
                _errors.Add($"SyntaxError: Orphaned 'finally' clause at line {_curToken.Line}, col {_curToken.Column} - skipping clause body");
                return null;
            }
            
            switch (_curToken.Type)
            {
                case TokenType.LBrace:
                    // Use consumeTerminator:false so the closing '}' remains as _curToken.
                    // The caller's loop (ParseProgram or outer ParseBlockStatement) will advance past it,
                    // preventing the double-advance bug that skips the next statement token.
                    return ParseBlockStatement(consumeTerminator: false);
                case TokenType.Let:
                    if ((_peekToken.HadLineTerminatorBefore || _peekToken.Line > _curToken.Line) && !PeekTokenIs(TokenType.LBracket))
                    {
                        return ParseExpressionStatement();
                    }
                    return ParseLetStatement();
                case TokenType.Const:
                case TokenType.Var:
                    return ParseLetStatement();
                case TokenType.Return:
                    if (_functionDepth == 0 && !_allowReturnOutsideFunction && !_isModule)
                    {
                        _errors.Add("SyntaxError: Illegal return statement");
                    }
                    return ParseReturnStatement();
                case TokenType.Try:
                    return ParseTryStatement();
                case TokenType.If:
                    return ParseIfStatement();
                case TokenType.Switch:
                    return ParseSwitchStatement();
                case TokenType.Throw:
                    return ParseThrowStatement();
                case TokenType.While:
                    return ParseWhileStatement();
                case TokenType.For:
                    return ParseForStatement();
                case TokenType.Break:
                    return ParseBreakStatement();
                case TokenType.Continue:
                    return ParseContinueStatement();
                case TokenType.Do:
                    return ParseDoWhileStatement();
                case TokenType.Function:
                    {
                        var funcExp = ParseFunctionLiteral(forceAsync: false, allowBodyExpressionContinuation: false) as FunctionLiteral;
                        // Return FunctionDeclarationStatement to allow Annex B-compatible function-declaration handling.
                        return new FunctionDeclarationStatement 
                        { 
                            Token = funcExp?.Token ?? _curToken, 
                            Function = funcExp 
                        };
                    }
                case TokenType.At:
                    // Parse decorators, then expect class
                    var decorators = ParseDecorators();
                    if (CurTokenIs(TokenType.Class) || PeekTokenIs(TokenType.Class))
                    {
                        if (PeekTokenIs(TokenType.Class)) NextToken();
                        var classStmt = ParseClassStatement();
                        if (classStmt is ClassStatement cs)
                        {
                            cs.Decorators = decorators;
                        }
                        return classStmt;
                    }
                    _errors.Add("Decorators must be followed by a class declaration");
                    return null;
                case TokenType.Class:
                    return ParseClassStatement();
                case TokenType.Import:
                    // import(...) and import.meta are expressions, not declarations.
                    if (PeekTokenIs(TokenType.LParen) || PeekTokenIs(TokenType.Dot))
                    {
                        return ParseExpressionStatement();
                    }
                    if (!_isModule)
                    {
                        _errors.Add("SyntaxError: import declaration not allowed in script goal");
                    }
                    else if (_moduleDeclarationNestingDepth > 0)
                    {
                        _errors.Add("SyntaxError: import declaration may only appear at top level of a module");
                    }
                    return ParseImportDeclaration();
                case TokenType.Export:
                    if (!_isModule)
                    {
                        _errors.Add("SyntaxError: export declaration not allowed in script goal");
                    }
                    else if (_moduleDeclarationNestingDepth > 0)
                    {
                        _errors.Add("SyntaxError: export declaration may only appear at top level of a module");
                    }
                    return ParseExportDeclaration();
                case TokenType.Async:
                    // Only parse as async function declaration if followed by 'function' keyword
                    if (PeekTokenIs(TokenType.Function))
                        return ParseAsyncFunctionDeclaration();
                    // Otherwise treat 'async' as an identifier in expression statement
                    return ParseExpressionStatement();
                case TokenType.With:
                    return ParseWithStatement();
                case TokenType.Semicolon:
                    return null; // Ignore empty statements
                case TokenType.Identifier:
                     if (string.Equals(_curToken.Literal, "using", StringComparison.Ordinal))
                     {
                         return ParseUsingDeclarationStatement(isAwaitUsing: false);
                     }
                     if (PeekTokenIs(TokenType.Colon))
                     {
                         return ParseLabeledStatement();
                     }
                     break;
            }
            // Escaped "async" must not form an async function declaration.
            if (_curToken.Type == TokenType.Identifier &&
                string.Equals(_curToken.Literal, "async", StringComparison.Ordinal) &&
                PeekTokenIs(TokenType.Function) &&
                !_peekToken.HadLineTerminatorBefore)
            {
                _errors.Add("SyntaxError: 'async' keyword cannot contain escape sequences");
                return ParseExpressionStatement();
            }

            // In generator/async-generator bodies, `yield:` is an early error label.
            if (_curToken.Type == TokenType.Yield && PeekTokenIs(TokenType.Colon))
            {
                if (_generatorFunctionDepth > 0)
                {
                    _errors.Add("SyntaxError: 'yield' cannot be used as a label in generator/async-generator bodies");
                }
                return ParseLabeledStatement();
            }
            return ParseExpressionStatement();
        }

        private Statement ParseUsingDeclarationStatement(bool isAwaitUsing)
        {
            if (isAwaitUsing)
            {
                NextToken(); // move from 'await' to contextual 'using'
            }

            var usingToken = _curToken;
            var declarationToken = new Token(
                TokenType.Const,
                isAwaitUsing ? "await using" : "using",
                usingToken.Line,
                usingToken.Column,
                usingToken.HadLineTerminatorBefore)
            {
                Position = usingToken.Position
            };

            _curToken = declarationToken;
            return ParseLetStatement();
        }

        private LabeledStatement ParseLabeledStatement()
        {
            if (IsReservedIdentifierReference(_curToken))
            {
                _errors.Add($"SyntaxError: Unexpected reserved word '{_curToken.Literal}'");
            }
            if (_generatorFunctionDepth > 0 && _curToken.Literal == "yield")
            {
                _errors.Add("SyntaxError: 'yield' cannot be used as a label in generator/async-generator bodies");
            }
            if (_asyncFunctionDepth > 0 && _curToken.Literal == "await")
            {
                _errors.Add("SyntaxError: 'await' cannot be used as a label in async/async-generator bodies");
            }
            var label = new Identifier(_curToken, _curToken.Literal);
            NextToken(); // consume the ':'
            NextToken(); // move to the body statement
            Statement body;
            _moduleDeclarationNestingDepth++;
            try
            {
                body = ParseStatement();
            }
            finally
            {
                _moduleDeclarationNestingDepth--;
            }

            if (IsInvalidSingleStatementBody(body))
            {
                _errors.Add("SyntaxError: Invalid labelled statement body");
            }

            return new LabeledStatement
            {
                Token = label.Token,
                Label = label,
                Body = body
            };
        }

        private Statement ParseLetStatement()
        {
            var declarationToken = _curToken;
            var kind = DeclarationKind.Var;

            if (_curToken.Type == TokenType.Const)
            {
                kind = DeclarationKind.Const;
            }
            else if (_curToken.Type == TokenType.Let)
            {
                kind = DeclarationKind.Let;
            }

            var declarations = new List<Statement>();
            NextToken(); // move to first declarator

            while (true)
            {
                var declarator = new LetStatement
                {
                    Token = declarationToken,
                    Kind = kind
                };

                if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
                {
                    var patternOrInitializer = ParseExpression(Precedence.Comma);
                    declarator.Name = new Identifier(declarationToken, "_destructured");

                    if (TryNormalizeDestructuringAssignment(patternOrInitializer, out var bindingPattern, out var initializer))
                    {
                        declarator.DestructuringPattern = bindingPattern;
                        declarator.Value = initializer;
                    }
                    else
                    {
                        declarator.DestructuringPattern = patternOrInitializer;

                        if (PeekTokenIs(TokenType.Assign))
                        {
                            NextToken(); // '='
                            NextToken(); // initializer start
                            declarator.Value = ParseExpression(Precedence.Comma);
                        }
                        else if (kind == DeclarationKind.Const)
                        {
                            _errors.Add("SyntaxError: Missing initializer in const declaration");
                        }
                    }

                    ValidateBindingPattern(declarator.DestructuringPattern);
                }
                else
                {
                    if (!IsIdentifierNameToken(_curToken.Type))
                    {
                        if (IsKeywordToken(_curToken.Type) && !_contextualKeywords.Contains(_curToken.Type))
                        {
                            _errors.Add($"SyntaxError: Unexpected reserved word '{_curToken.Literal}'");
                        }
                        else if (!IsKeywordToken(_curToken.Type))
                        {
                            _errors.Add($"SyntaxError: Expected identifier in {kind.ToString().ToLowerInvariant()} declaration");
                        }

                        while (!CurTokenIs(TokenType.Comma) && !CurTokenIs(TokenType.Semicolon) && !CurTokenIs(TokenType.Eof))
                        {
                            NextToken();
                        }
                    }
                    else if (!ValidateBindingIdentifier(_curToken))
                    {
                        return null;
                    }
                    else
                    {
                        declarator.Name = new Identifier(_curToken, _curToken.Literal);
                        if (_functionDepth == 0 && kind != DeclarationKind.Var && declarator.Name.Value == "undefined")
                        {
                            _errors.Add("SyntaxError: Lexical declaration cannot redeclare restricted global property 'undefined'");
                        }

                        if (PeekTokenIs(TokenType.Assign))
                        {
                            NextToken(); // '='
                            NextToken(); // initializer start
                            declarator.Value = ParseExpression(Precedence.Comma);
                        }
                        else if (kind == DeclarationKind.Const)
                        {
                            _errors.Add("SyntaxError: Missing initializer in const declaration");
                        }
                    }
                }

                declarations.Add(declarator);

                if (PeekTokenIs(TokenType.Comma))
                {
                    NextToken(); // ','
                    NextToken(); // next declarator
                    continue;
                }

                break;
            }

            if (PeekTokenIs(TokenType.Semicolon))
            {
                NextToken();
            }

            if (declarations.Count == 1)
            {
                return declarations[0];
            }

            return new BlockStatement
            {
                Token = declarationToken,
                Statements = declarations
            };
        }

        private ReturnStatement ParseReturnStatement()
        {
            var stmt = new ReturnStatement { Token = _curToken };

            if (_functionDepth == 0 && !_allowReturnOutsideFunction)
            {
                 // We already reported error before calling this, or should we?
                 // The Check in ParseStatement detected it.
                 // But strictly, we should ensure we don't error if allowed.
            }
            // Note: ParseStatement checks _functionDepth == 0. We need to update that check or rely on this.
            // ParseStatement has:
            // case TokenType.Return:
            //     if (_functionDepth == 0) _errors.Add("SyntaxError: Illegal return statement");
            //     return ParseReturnStatement();
            
            // Expected that caller handles the check. We will update caller too.

            NextToken();

            // ASI: Restricted production - if line terminator before expression,
            // semicolon, rbrace, or eof, insert semicolon and return undefined
            if (_curToken.HadLineTerminatorBefore || CurTokenIs(TokenType.Semicolon) ||
                CurTokenIs(TokenType.RBrace) || CurTokenIs(TokenType.Eof))
            {
                // Don't advance past ';' ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the outer ParseBlockStatement/ParseProgram loop
                // will do NextToken() to advance to the next statement.
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

            // Console.WriteLine($"[DEBUG] ParseExpression StartLoop: prec={precedence}, Peek={_peekToken}, PeekPrec={PeekPrecedence()}");
            while (!PeekTokenIs(TokenType.Semicolon) &&
                   (precedence < PeekPrecedence() ||
                    (precedence == PeekPrecedence() && PeekTokenIs(TokenType.Arrow))))
            {
                // Debug assignment (disabled for perf)
                // if (_peekToken.Type == TokenType.Assign)
                //    Console.WriteLine($"[DEBUG] ParseExpression: Cur={_curToken.Type}, Peek={_peekToken.Type}, Prec={precedence}, PeekPrec={PeekPrecedence()}");

                // Skip 'in' as infix when _noIn flag is set (for-loop init expressions)
                if (_noIn && PeekTokenIs(TokenType.In))
                    break;

                if (!_infixParseFns.TryGetValue(_peekToken.Type, out var infix))
                {
                    return leftExp;
                }

                NextToken();

                leftExp = infix(leftExp);
            }
            
            // Console.WriteLine($"[DEBUG] ParseExpression End: prec={precedence}, Peek={_peekToken}, PeekPrec={PeekPrecedence()}");

            return leftExp;
        }

        private Expression ParseIdentifier()
        {
            if (_curToken.Type == TokenType.Identifier && IsReservedIdentifierReference(_curToken))
            {
                _errors.Add($"SyntaxError: Unexpected reserved word '{_curToken.Literal}'");
            }
            if (_curToken.Type == TokenType.Super &&
                _classDepth == 0 &&
                _methodContextDepth == 0 &&
                !_allowSuperOutsideClass)
            {
                _errors.Add("SyntaxError: 'super' keyword unexpected here");
            }
            if (_curToken.Type == TokenType.Super &&
                _inClassFieldInitializer &&
                _functionDepth == 0 &&
                !_allowSuperInClassFieldInitializer)
            {
                _errors.Add("SyntaxError: 'super' is not valid in class field initializers");
            }
            if (_curToken.Literal == "arguments" && _inClassFieldInitializer && _functionDepth == 0)
            {
                _errors.Add("SyntaxError: 'arguments' is not valid in class field initializers");
            }
            if (_classStaticBlockDepth > 0 && _functionDepth == 0)
            {
                if (_curToken.Literal == "await")
                {
                    _errors.Add("SyntaxError: Unexpected identifier 'await' in class static block");
                }

                if (_curToken.Literal == "arguments")
                {
                    _errors.Add("SyntaxError: Unexpected identifier 'arguments' in class static block");
                }
            }
            if ((_asyncFunctionDepth > 0 || _isModule) && _curToken.Literal == "await")
            {
                _errors.Add("SyntaxError: Unexpected identifier 'await' in async function");
            }
            if (_generatorFunctionDepth > 0 && _curToken.Literal == "yield")
            {
                _errors.Add("SyntaxError: Unexpected identifier 'yield' in generator function");
            }
            return new Identifier(_curToken, _curToken.Literal);
        }

        private Expression ParsePrivateIdentifier()
        {
            if (_classDepth == 0)
            {
                _errors.Add("SyntaxError: Private field '#name' must be declared in an enclosing class");
            }
            // Token literal is "#fieldName", extract just the name
            string name = _curToken.Literal.Substring(1); // Remove '#'
            return new PrivateIdentifier(_curToken, name);
        }

        private Expression ParseNumberLiteral()
        {
            var literal = _curToken.Literal;

            // In strict mode, both LegacyOctalIntegerLiteral (070) and
            // NonOctalDecimalIntegerLiteral (078/079) are early errors.
            if (_isStrictMode && IsLegacyStyleLeadingZeroIntegerLiteral(literal))
            {
                _errors.Add($"SyntaxError: Legacy octal literals are not allowed in strict mode: {literal}");
                return null;
            }

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

            if (IsLegacyOctalIntegerLiteral(literal))
            {
                try
                {
                    long legacyOctalVal = Convert.ToInt64(literal, 8);
                    return new IntegerLiteral { Token = _curToken, Value = legacyOctalVal };
                }
                catch
                {
                    // Fall through to ordinary numeric parsing below.
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

        private static bool IsLegacyStyleLeadingZeroIntegerLiteral(string literal)
        {
            if (string.IsNullOrEmpty(literal) || literal.Length <= 1 || literal[0] != '0')
            {
                return false;
            }

            char second = literal[1];
            if (second == 'x' || second == 'X' ||
                second == 'o' || second == 'O' ||
                second == 'b' || second == 'B' ||
                second == '.' || second == 'e' || second == 'E')
            {
                return false;
            }

            for (int i = 1; i < literal.Length; i++)
            {
                if (!char.IsDigit(literal[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLegacyOctalIntegerLiteral(string literal)
        {
            if (!IsLegacyStyleLeadingZeroIntegerLiteral(literal))
            {
                return false;
            }

            for (int i = 1; i < literal.Length; i++)
            {
                if (literal[i] < '0' || literal[i] > '7')
                {
                    return false;
                }
            }

            return true;
        }
        
        private Expression ParseBigIntLiteral()
        {
            return new BigIntLiteral { Token = _curToken, Value = _curToken.Literal };
        }

        private Expression ParseStringLiteral()
        {
            return new StringLiteral { Token = _curToken, Value = _curToken.Literal };
        }

        // Parse a regular template literal: `Hello ${name}!`
        // Refactored to use stateful Lexer tokens (recursive parsing)
        private Expression ParseTemplateLiteral()
        {
            var tmpl = new TemplateLiteral { Token = _curToken };
            
            // Case 1: Simple string (no substitutions) `foo`
            if (_curToken.Type == TokenType.TemplateNoSubst)
            {
                // Lexer already verified it ends with `
                tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = _curToken.Literal, Tail = true });
                return tmpl;
            }
            
            // Case 2: Start of template with substitutions `head${
            // Lexer verification ensures it ends with ${
            tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = _curToken.Literal, Tail = false });
            
            while (true)
            {
                // We expect an expression next
                NextToken(); 
                
                var expression = ParseExpression(Precedence.Lowest);
                tmpl.Expressions.Add(expression);
                
                // After expression, we normally expect '}' (RBrace).
                // But in template context, this brace is actually the start of Middle or Tail.
                // We must coordinate with Lexer to reinterpret/consume it correctly.
                
                if (CurTokenIs(TokenType.RBrace))
                {
                    // Some nested parsers can legitimately leave us already sitting on the
                    // template expression closer. In that case, ignore the stale peek token
                    // and rescan the continuation directly from the lexer state.
                    _peekToken = _lexer.ReadTemplateContinuation();
                }
                else if (PeekTokenIs(TokenType.RBrace))
                {
                    // PeekToken is '}'. Lexer has already acted on it as RBrace.
                    // We consume this token BUT ask lexer to scan the *continuation* (string part).
                    // Since Lexer.NextToken() sets _peekToken and advances position past the token,
                    // valid code flow implies we are at the position *after* the brace.
                    // Lexer.ReadTemplateContinuation() has been updated to expect this state.
                    
                    // Manually advance:
                    _curToken = _peekToken; // The '}' token placeholder
                    _peekToken = _lexer.ReadTemplateContinuation(); // The real Middle/Tail token
                }
                else
                {
                    _errors.Add($"SyntaxError: Expected '}}' in template literal, got {_peekToken?.Type}");
                    return null;
                }
                
                // Now check what we got back
                if (PeekTokenIs(TokenType.TemplateTail))
                {
                    NextToken(); // Consume Tail
                    tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = _curToken.Literal, Tail = true });
                    break;
                }
                else if (PeekTokenIs(TokenType.TemplateMiddle))
                {
                    NextToken(); // Consume Middle around to next expression
                    tmpl.Quasis.Add(new TemplateElement { Token = _curToken, Value = _curToken.Literal, Tail = false });
                    // Loop continues to parse next expression
                }
                else
                {
                     _errors.Add($"SyntaxError: Unexpected token in template literal: {_peekToken?.Type}");
                     return null;
                }
            }
            
            return tmpl;
        }

        // Parse a tagged template literal: tag`Hello ${name}!`
        private Expression ParseTaggedTemplate(Expression left)
        {
            var token = _curToken;
            var template = ParseTemplateLiteral() as TemplateLiteral;
            if (template == null)
            {
                return null;
            }

            var tagged = new TaggedTemplateExpression
            {
                Token = token,
                Tag = left,
                Expressions = template.Expressions
            };

            foreach (var quasi in template.Quasis)
            {
                tagged.Strings.Add(quasi?.Value ?? string.Empty);
            }

            if (tagged.Strings.Count == 0)
            {
                tagged.Strings.Add(string.Empty);
            }

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

            // In generator/async-generator bodies, `void yield` (and similar unary forms)
            // must not treat `yield` as an identifier reference.
            if (_generatorFunctionDepth > 0 &&
                CurTokenIs(TokenType.Yield) &&
                (expression.Operator == "void" || expression.Operator == "typeof" || expression.Operator == "delete"))
            {
                _errors.Add("SyntaxError: Unexpected identifier 'yield' in generator function");
            }

            expression.Right = ParseExpression(Precedence.Prefix);

            if (expression.Right == null || expression.Right is EmptyExpression)
            {
                _errors.Add($"SyntaxError: Missing operand after unary operator '{expression.Operator}'");
            }

            // delete on private references is always an early error in class strict code.
            if (expression.Operator == "delete" && ContainsPrivateReference(expression.Right))
            {
                _errors.Add("SyntaxError: Private fields cannot be deleted");
            }

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
                    _functionDepth++;
                    try
                    {
                        _arrowFunctionDepth++;
                        if (CurTokenIs(TokenType.LBrace))
                        {
                            // Keep call/member delimiters (',' ')' etc.) for the outer parser.
                            arrow.Body = ParseBlockStatement(consumeTerminator: false);
                        }
                        else
                        {
                            // AssignmentExpression grammar allows assignment but excludes the comma operator.
                            arrow.Body = ParseExpression(Precedence.Comma);
                        }
                    }
                    finally
                    {
                        _arrowFunctionDepth--;
                        _functionDepth--;
                    }
                    return arrow;
                }
                // Empty parens not followed by arrow - this is unusual but return null
                return null;
            }

            var exp = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
            {
                // Keep token stream synchronized even in strict mode (allowRecovery: false).
                // Runtime paths still fail from parser.Errors; this prevents follow-up
                // cascades caused by stale delimiter position.
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Semicolon) &&
                       !CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }

                if (!_allowRecovery)
                {
                    return null;
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


        private Statement ParseIfStatement()
        {
            var statement = new IfStatement { Token = _curToken };

            if (!ExpectPeek(TokenType.LParen))
            {
                return null;
            }

            NextToken();
            statement.Condition = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
            {
                // Recovery matching ParseIfExpression logic
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.LBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

            bool consequenceHasBraces = PeekTokenIs(TokenType.LBrace);
            statement.Consequence = ParseBodyAsBlock();
            if (statement.Consequence == null) return null;
            if (!consequenceHasBraces && statement.Consequence.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(statement.Consequence.Statements[0], "if statement");
            }

            if (PeekTokenIs(TokenType.Else))
            {
                NextToken();

                if (PeekTokenIs(TokenType.If))
                {
                    NextToken();
                    var elseIfStmt = ParseIfStatement();
                    if (elseIfStmt != null)
                    {
                        statement.Alternative = new BlockStatement
                        {
                            Token = _curToken,
                            Statements = new System.Collections.Generic.List<Statement> { elseIfStmt }
                        };
                    }
                }
                else
                {
                    bool alternativeHasBraces = PeekTokenIs(TokenType.LBrace);
                    statement.Alternative = ParseBodyAsBlock();
                    if (!alternativeHasBraces &&
                        statement.Alternative is BlockStatement alternativeBlock &&
                        alternativeBlock.Statements.Count > 0)
                    {
                        ReportInvalidSingleStatementBody(alternativeBlock.Statements[0], "if statement");
                    }
                }
            }

            return statement;
        }

        // Helper: Parse a body that may or may not have braces
        private BlockStatement ParseBodyAsBlock()
        {
            if (PeekTokenIs(TokenType.LBrace))
            {
                NextToken(); // Move to '{'
                return ParseBlockStatement(consumeTerminator: false);
                // _curToken is now at '}', not past it.
            }
            else
            {
                // Single statement without braces
                NextToken();
                if (CurTokenIs(TokenType.Semicolon))
                {
                    return new BlockStatement { Token = _curToken };
                }

                Statement stmt;
                _moduleDeclarationNestingDepth++;
                try
                {
                    stmt = ParseStatement();
                }
                finally
                {
                    _moduleDeclarationNestingDepth--;
                }
                if (stmt  == null) return null;
                
                var block = new BlockStatement { Token = _curToken };
                block.Statements.Add(stmt);
                return block;
            }
        }

        private BlockStatement ParseFunctionBodyBlock(bool isAsync = false, bool isGenerator = false)
        {
            _functionDepth++;
            if (isAsync)
            {
                _asyncFunctionDepth++;
            }
            if (isGenerator)
            {
                _generatorFunctionDepth++;
            }

            try
            {
                return ParseBlockStatement(consumeTerminator: false);
            }
            finally
            {
                if (isGenerator)
                {
                    _generatorFunctionDepth--;
                }
                if (isAsync)
                {
                    _asyncFunctionDepth--;
                }
                _functionDepth--;
            }
        }

        private FunctionLiteral ParseMethodLikeFunctionLiteral(Token token, bool isAsync = false, bool isGenerator = false)
        {
            var funcLit = new FunctionLiteral
            {
                Token = token,
                IsAsync = isAsync,
                IsGenerator = isGenerator,
                IsMethodDefinition = true
            };

            _functionDepth++;
            _methodContextDepth++;
            if (isAsync)
            {
                _asyncFunctionDepth++;
            }
            if (isGenerator)
            {
                _generatorFunctionDepth++;
            }

            try
            {
                if (!ExpectPeek(TokenType.LParen))
                {
                    return null;
                }

                funcLit.Parameters = ParseFunctionParameters();
                if (funcLit.Parameters == null && !_allowRecovery)
                {
                    return null;
                }

                if (!ExpectPeek(TokenType.LBrace))
                {
                    return null;
                }

                funcLit.Body = ParseBlockStatement(
                    consumeTerminator: false,
                    allowExpressionContinuationAfterClosingBrace: true);
                return funcLit;
            }
            finally
            {
                if (isGenerator)
                {
                    _generatorFunctionDepth--;
                }
                if (isAsync)
                {
                    _asyncFunctionDepth--;
                }
                _methodContextDepth--;
                _functionDepth--;
            }
        }

        private bool AllowsAnnexBBlockFunctions()
        {
            return !_isModule && !_isStrictMode;
        }

        private bool IsInvalidSingleStatementBody(Statement statement)
        {
            if (statement == null)
            {
                return false;
            }

            if (statement is LabeledStatement labeledStatement)
            {
                return IsInvalidSingleStatementBody(labeledStatement.Body);
            }

            return statement is ClassStatement ||
                   (statement is FunctionDeclarationStatement && !AllowsAnnexBBlockFunctions()) ||
                   (statement is LetStatement declaration && declaration.Kind != DeclarationKind.Var);
        }

        private void ReportInvalidSingleStatementBody(Statement statement, string context)
        {
            if (IsInvalidSingleStatementBody(statement))
            {
                _errors.Add($"SyntaxError: Declaration not allowed in {context} single-statement body");
            }
        }

        private bool IsInvalidForOfRightHandSide(Expression expression)
        {
            return false;
        }

        private bool ExpressionMayLeaveTrailingInnerBrace(Expression expression)
        {
            if (expression == null)
            {
                return false;
            }

            return expression switch
            {
                ObjectLiteral => true,
                FunctionLiteral => true,
                AsyncFunctionExpression => true,
                ClassExpression => true,
                ArrowFunctionExpression arrow => arrow.Body is BlockStatement ||
                                                 arrow.Body is Expression arrowExpression &&
                                                 ExpressionMayLeaveTrailingInnerBrace(arrowExpression),
                AssignmentExpression assignment => ExpressionMayLeaveTrailingInnerBrace(assignment.Right),
                LogicalAssignmentExpression assignment => ExpressionMayLeaveTrailingInnerBrace(assignment.Right),
                InfixExpression infix => ExpressionMayLeaveTrailingInnerBrace(infix.Right),
                ConditionalExpression conditional => ExpressionMayLeaveTrailingInnerBrace(conditional.Consequent) ||
                                                     ExpressionMayLeaveTrailingInnerBrace(conditional.Alternate),
                AwaitExpression awaitExpression => ExpressionMayLeaveTrailingInnerBrace(awaitExpression.Argument),
                YieldExpression yieldExpression => ExpressionMayLeaveTrailingInnerBrace(yieldExpression.Value),
                PrefixExpression prefix => ExpressionMayLeaveTrailingInnerBrace(prefix.Right),
                ThrowExpression throwExpression => ExpressionMayLeaveTrailingInnerBrace(throwExpression.Value),
                _ => false
            };
        }

        private bool StatementMayLeaveTrailingInnerBrace(Statement statement)
        {
            if (statement == null)
            {
                return false;
            }

            return statement switch
            {
                LabeledStatement labeled => StatementMayLeaveTrailingInnerBrace(labeled.Body),
                BlockStatement => true,
                IfStatement => true,
                TryStatement => true,
                WhileStatement => true,
                DoWhileStatement => true,
                ForStatement => true,
                ForInStatement => true,
                ForOfStatement => true,
                SwitchStatement => true,
                WithStatement => true,
                FunctionDeclarationStatement => true,
                ClassStatement => true,
                ReturnStatement returnStatement => ExpressionMayLeaveTrailingInnerBrace(returnStatement.ReturnValue),
                ThrowStatement throwStatement => ExpressionMayLeaveTrailingInnerBrace(throwStatement.Value),
                ExpressionStatement expressionStatement => ExpressionMayLeaveTrailingInnerBrace(expressionStatement.Expression),
                LetStatement letStatement => ExpressionMayLeaveTrailingInnerBrace(letStatement.Value),
                _ => false
            };
        }

        private bool IsCurrentTokenClosingCurrentBlock(Token blockToken)
        {
            if (blockToken == null ||
                !CurTokenIs(TokenType.RBrace) ||
                _lexer?.Source == null ||
                blockToken.Position < 0 ||
                _curToken.Position < blockToken.Position)
            {
                return CurTokenIs(TokenType.RBrace);
            }

            // In minified bundles, `...};` often closes an inner declaration/expression
            // inside the current block, not the block itself.
            if (PeekTokenIs(TokenType.Semicolon))
            {
                return false;
            }

            int length = (_curToken.Position - blockToken.Position) + Math.Max(_curToken.Literal?.Length ?? 0, 1);
            if (length <= 0 || blockToken.Position + length > _lexer.Source.Length)
            {
                return CurTokenIs(TokenType.RBrace);
            }

            var probe = new Lexer(_lexer.Source.Substring(blockToken.Position, length))
            {
                TreatHtmlLikeCommentsAsComments = _lexer.TreatHtmlLikeCommentsAsComments
            };

            int depth = 0;
            for (var token = probe.NextToken(); token.Type != TokenType.Eof; token = probe.NextToken())
            {
                if (token.Type == TokenType.LBrace ||
                    token.Type == TokenType.TemplateHead ||
                    token.Type == TokenType.TemplateMiddle)
                {
                    depth++;
                }
                else if (token.Type == TokenType.RBrace)
                {
                    depth--;
                }
            }

            return depth == 0;
        }

        private BlockStatement ParseBlockStatement(
            bool consumeTerminator = true,
            bool enableDirectiveStrictMode = false,
            bool allowExpressionContinuationAfterClosingBrace = false)
        {
            var block = new BlockStatement { Token = _curToken };
            _moduleDeclarationNestingDepth++;
            try
            {
                NextToken();

                bool inDirectivePrologue = enableDirectiveStrictMode;

                while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    var stmt = ParseStatement();
                    bool stmtMayLeaveTrailingInnerBrace = StatementMayLeaveTrailingInnerBrace(stmt);
                    if (stmt != null)
                    {
                        block.Statements.Add(stmt);

                        if (inDirectivePrologue)
                        {
                            if (stmt is ExpressionStatement es && es.Expression is StringLiteral sl)
                            {
                                if (sl.Value == "use strict")
                                {
                                    _isStrictMode = true;
                                }
                            }
                            else
                            {
                                inDirectivePrologue = false;
                            }
                        }
                    }

                    // Nested statement/expression parses may leave us on an inner '}'.
                    // Keep delimiter ownership conservative here to avoid desyncs in minified bundles
                    // (notably try/catch and dense class method chains).
                    if (CurTokenIs(TokenType.RBrace))
                    {
                        // `try { ... } catch/finally ...` ownership belongs to ParseTryStatement.
                        if (PeekTokenIs(TokenType.Catch) || PeekTokenIs(TokenType.Finally))
                        {
                            break;
                        }

                        // `if (...) { ... } else ...` ownership belongs to ParseIfStatement.
                        if (PeekTokenIs(TokenType.Else))
                        {
                            break;
                        }

                        if (stmt is TryStatement)
                        {
                            // Try/catch/finally statements always end on their own closing brace.
                            // Advance once so the surrounding block can decide ownership on the
                            // next delimiter token.
                            NextToken();
                            continue;
                        }

                        if (IsCurrentTokenClosingCurrentBlock(block.Token))
                        {
                            break;
                        }

                        if (!_allowRecovery && stmtMayLeaveTrailingInnerBrace)
                        {
                            // The parsed statement can leave us on an inner '}'.
                            // In strict runtime mode, consume it so parsing continues at
                            // the current block boundary instead of desyncing delimiters.
                            NextToken();
                            continue;
                        }

                        // Unknown ownership for this brace. Consume it and continue
                        // scanning so outer expressions can claim their own delimiters.
                        NextToken();
                        continue;
                    }

                    if (_curToken.Type != TokenType.Eof && _curToken.Type != TokenType.RBrace)
                    {
                        NextToken();
                    }
                }

                if (CurTokenIs(TokenType.RBrace))
                {
                    block.EndPosition = _curToken.Position + 1;
                }

                if (consumeTerminator && CurTokenIs(TokenType.RBrace)) NextToken();
                // Console.WriteLine($"[DEBUG] ParseBlockStatement Exit: consume={consumeTerminator}, Cur={_curToken.Type}, Peek={_peekToken.Type}");

            }
            finally
            {
                _moduleDeclarationNestingDepth--;
            }

            return block;
        }

        private Expression ParseFunctionLiteral()
        {
            return ParseFunctionLiteral(forceAsync: false, allowBodyExpressionContinuation: true);
        }

        private Expression ParseFunctionLiteral(bool forceAsync, bool allowBodyExpressionContinuation = true)
        {
            var lit = new FunctionLiteral { Token = _curToken };
            lit.IsAsync = forceAsync;
            int startPos = _curToken.Position; // Capture start position
            int previousMethodContextDepth = _methodContextDepth;
            _methodContextDepth = 0;
            
            try
            {
                // Check for generator function: function*
                if (PeekTokenIs(TokenType.Asterisk))
                {
                    NextToken(); // consume *
                    lit.IsGenerator = true;
                }

                if (PeekTokenIs(TokenType.Identifier) || PeekTokenIs(TokenType.Async) || PeekTokenIs(TokenType.Let) || PeekTokenIs(TokenType.Of) || PeekTokenIs(TokenType.From) || PeekTokenIs(TokenType.As) || PeekTokenIs(TokenType.Static))
                {
                    NextToken();
                    if (!ValidateBindingIdentifier(_curToken)) return null;
                    lit.Name = _curToken.Literal;
                }

                _functionDepth++;
                if (lit.IsAsync) _asyncFunctionDepth++;
                if (lit.IsGenerator) _generatorFunctionDepth++;
                bool previousStrictMode = _isStrictMode;
                bool inheritedStrictMode = _isModule || previousStrictMode;
                try
                {
                    if (!ExpectPeek(TokenType.LParen))
                    {
                        return null;
                    }

                    lit.Parameters = ParseFunctionParameters();
                    if (lit.Parameters == null && !_allowRecovery)
                    {
                        return null;
                    }
                    bool fnParamsSimple = _lastParsedParamsIsSimple;
                    bool fnParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                    bool fnTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;

                    if (!ExpectPeek(TokenType.LBrace))
                    {
                        return null;
                    }

                    if (inheritedStrictMode)
                    {
                        _isStrictMode = true;
                    }

                    lit.Body = ParseBlockStatement(
                        consumeTerminator: false,
                        enableDirectiveStrictMode: true,
                        allowExpressionContinuationAfterClosingBrace: allowBodyExpressionContinuation);

                    if (lit.IsAsync)
                    {
                        if (fnTrailingCommaAfterRest)
                        {
                            _errors.Add("SyntaxError: Rest parameter must be last formal parameter");
                        }

                        if (ContainsUseStrictDirective(lit.Body) && !fnParamsSimple)
                        {
                            _errors.Add("SyntaxError: 'use strict' directive is invalid with non-simple parameter list");
                        }

                        if (ParametersContainIdentifier(lit.Parameters, "await"))
                        {
                            _errors.Add("SyntaxError: Unexpected identifier 'await' in async function parameters");
                        }

                        if (BodyHasLexicalParameterNameCollision(lit.Body, lit.Parameters))
                        {
                            _errors.Add("SyntaxError: Formal parameter name conflicts with a lexical declaration in function body");
                        }
                    }

                    if (fnParamsDuplicate)
                    {
                        bool isStrict = _isModule || _isStrictMode || ContainsUseStrictDirective(lit.Body);
                        bool isRestricted = lit.IsAsync || lit.IsGenerator || !fnParamsSimple;

                        if (isStrict || isRestricted)
                        {
                            _errors.Add("SyntaxError: Duplicate parameter name not allowed in this context");
                        }
                    }

                    bool functionIsStrict = _isModule || inheritedStrictMode || ContainsUseStrictDirective(lit.Body);
                    lit.IsStrict = functionIsStrict;
                    if (functionIsStrict)
                    {
                        if (BodyContainsWithStatement(lit.Body))
                        {
                            _errors.Add("SyntaxError: Strict mode code may not include a with statement");
                        }

                        if (BodyContainsLegacyOctalLiteral(lit.Body))
                        {
                            _errors.Add("SyntaxError: Legacy octal literals are not allowed in strict mode");
                        }
                    }
                }
                finally
                {
                    _isStrictMode = previousStrictMode;
                    if (lit.IsGenerator) _generatorFunctionDepth--;
                    if (lit.IsAsync) _asyncFunctionDepth--;
                    _functionDepth--;
                }

                int endPos = lit.Body?.EndPosition ?? -1;
                if (startPos >= 0 && endPos > startPos && _lexer.Source != null && endPos <= _lexer.Source.Length)
                {
                    lit.Source = _lexer.Source.Substring(startPos, endPos - startPos);
                }

                return lit;
            }
            finally
            {
                _methodContextDepth = previousMethodContextDepth;
            }
        }
        
        private Expression ParseYieldExpression()
        {
            if (_generatorFunctionDepth == 0)
            {
                if (_isStrictMode || _isModule)
                {
                    _errors.Add("SyntaxError: Unexpected strict mode reserved word 'yield'");
                }
                return new Identifier(_curToken, "yield");
            }

            var yield = new YieldExpression { Token = _curToken };
            
            // Check for yield*
            if (PeekTokenIs(TokenType.Asterisk))
            {
                NextToken(); // consume *
                yield.Delegate = true;
            }
            
            // Check if there's a value after yield
            if (!IsYieldTerminator(_peekToken))
            {
                NextToken();
                // YieldExpression grammar uses AssignmentExpression, not comma expressions.
                yield.Value = ParseExpression(Precedence.Assignment);
            }
            
            return yield;
        }

        private static bool IsYieldTerminator(Token token)
        {
            return token.HadLineTerminatorBefore ||
                   token.Type == TokenType.Semicolon ||
                   token.Type == TokenType.RBrace ||
                   token.Type == TokenType.RBracket ||
                   token.Type == TokenType.RParen ||
                   token.Type == TokenType.Comma ||
                   token.Type == TokenType.Colon ||
                   token.Type == TokenType.Eof;
        }

        private List<Identifier> ParseFunctionParameters()
        {
            var identifiers = new List<Identifier>();
            _lastParsedParamsIsSimple = true;
            _lastParsedParamsHasDuplicateNames = false;
            _lastParsedParamsHadTrailingCommaAfterRest = false;
            bool previousInFormalParameters = _inFormalParameters;
            _inFormalParameters = true;
            try
            {
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

                    // Rest parameter must be final and cannot be followed by a comma.
                    if (identifiers.Count > 0 && identifiers[identifiers.Count - 1].IsRest)
                    {
                        _lastParsedParamsHadTrailingCommaAfterRest = true;
                        _errors.Add("SyntaxError: Rest parameter must be last formal parameter");
                    }

                    if (PeekTokenIs(TokenType.RParen))
                    {
                        break; // Trailing comma
                    }

                    NextToken(); // move to next parameter
                    ParseSingleParameter(identifiers);
                }

                if (!ExpectPeek(TokenType.RParen))
                {
                    return null;
                }

                // Track parameter list shape for early-error checks.
                _lastParsedParamsIsSimple = true;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ident in identifiers)
                {
                    if (ident.IsRest || ident.DefaultValue != null || ident.DestructuringPattern != null)
                    {
                        _lastParsedParamsIsSimple = false;
                    }

                    if (!IsSyntheticParameterName(ident.Value))
                    {
                        if (!seen.Add(ident.Value))
                        {
                            _lastParsedParamsHasDuplicateNames = true;
                        }
                    }
                }

                return identifiers;
            }
            finally
            {
                _inFormalParameters = previousInFormalParameters;
            }
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
                if (IsIdentifierNameToken(_curToken.Type))
                {
                    if (!ValidateBindingIdentifier(_curToken)) return;
                    var ident = new Identifier(_curToken, _curToken.Literal) { IsRest = true };
                    identifiers.Add(ident);
                }
                else if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
                {
                    // Rest with destructuring: ...{a, b} or ...[x, y]
                    var pattern = ParseExpression(Precedence.Comma);
                    Expression ignoredInitializer = null;
                    if (TryNormalizeDestructuringAssignment(pattern, out var normalizedPattern, out var invalidInitializer))
                    {
                        pattern = normalizedPattern;
                        ignoredInitializer = invalidInitializer;
                    }

                    ValidateBindingPattern(pattern);
                    var ident = new Identifier(_curToken, $"__rest_{identifiers.Count}") { IsRest = true, DestructuringPattern = pattern };
                    identifiers.Add(ident);

                    if (ignoredInitializer != null)
                    {
                        _errors.Add("SyntaxError: Rest parameter cannot have a default initializer");
                    }
                }

                if (PeekTokenIs(TokenType.Assign))
                {
                    _errors.Add("SyntaxError: Rest parameter cannot have a default initializer");
                    NextToken(); // consume '='
                    NextToken(); // advance to initializer expression start
                    ParseExpression(Precedence.Assignment);
                }
                return;
            }

            // Handle simple identifier: a, a = 1
            if (IsIdentifierNameToken(_curToken.Type))
            {
                if (!ValidateBindingIdentifier(_curToken)) return;
                var ident = new Identifier(_curToken, _curToken.Literal);
                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Assignment);
                }
                identifiers.Add(ident);
                return;
            }

            // Handle object destructuring: {a, b, c = 1}
            if (CurTokenIs(TokenType.LBrace))
            {
                var pattern = ParseExpression(Precedence.Comma);
                Expression defaultValue = null;
                if (TryNormalizeDestructuringAssignment(pattern, out var normalizedPattern, out var normalizedDefaultValue))
                {
                    pattern = normalizedPattern;
                    defaultValue = normalizedDefaultValue;
                }

                ValidateBindingPattern(pattern);
                var ident = new Identifier(_curToken, $"__destructure_{identifiers.Count}")
                {
                    DestructuringPattern = pattern,
                    DefaultValue = defaultValue
                };

                // Check for default value: {a, b} = {}
                if (defaultValue == null && PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Assignment);
                }
                identifiers.Add(ident);
                return;
            }

            // Handle array destructuring: [a, b, c = 1]
            if (CurTokenIs(TokenType.LBracket))
            {
                var pattern = ParseExpression(Precedence.Comma);
                Expression defaultValue = null;
                if (TryNormalizeDestructuringAssignment(pattern, out var normalizedPattern, out var normalizedDefaultValue))
                {
                    pattern = normalizedPattern;
                    defaultValue = normalizedDefaultValue;
                }

                ValidateBindingPattern(pattern);
                var ident = new Identifier(_curToken, $"__array_destructure_{identifiers.Count}")
                {
                    DestructuringPattern = pattern,
                    DefaultValue = defaultValue
                };

                // Check for default value: [a, b] = []
                if (defaultValue == null && PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // =
                    NextToken(); // value
                    ident.DefaultValue = ParseExpression(Precedence.Assignment);
                }
                identifiers.Add(ident);
                return;
            }

            if (!_allowRecovery)
            {
                _errors.Add($"SyntaxError: Invalid parameter pattern starting with '{_curToken.Literal}'");
            }

            // Recovery: skip unexpected token
            return;
        }

        private static bool TryNormalizeDestructuringAssignment(
            Expression patternOrAssignment,
            out Expression bindingPattern,
            out Expression initializer)
        {
            bindingPattern = patternOrAssignment;
            initializer = null;

            if (patternOrAssignment is AssignmentExpression declarationAssignment &&
                declarationAssignment.Left != null &&
                declarationAssignment.Right != null &&
                (declarationAssignment.Left is ObjectLiteral || declarationAssignment.Left is ArrayLiteral))
            {
                bindingPattern = declarationAssignment.Left;
                initializer = declarationAssignment.Right;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Validates that an expression is a valid binding pattern
        /// </summary>
        private bool ValidateBindingPattern(Expression node)
        {
            if (node == null) return true;

            if (node is Identifier id)
            {
                return ValidateBindingIdentifier(id.Token);
            }

            if (node is ObjectLiteral obj)
            {
                int index = 0;
                int count = obj.Pairs.Count;
                foreach (var pair in obj.Pairs)
                {
                    if (pair.Value is SpreadElement objRest)
                    {
                        if (index != count - 1)
                        {
                            _errors.Add("SyntaxError: Rest element must be last in object binding pattern");
                            return false;
                        }
                        if (objRest.Argument is AssignmentExpression)
                        {
                            _errors.Add("SyntaxError: Rest element cannot have a default initializer");
                            return false;
                        }
                    }
                    if (!ValidateBindingPattern(pair.Value)) return false;
                    index++;
                }
                return true;
            }

            if (node is ArrayLiteral arr)
            {
                for (int i = 0; i < arr.Elements.Count; i++)
                {
                    var elem = arr.Elements[i];
                    if (elem is UndefinedLiteral) continue; // Elision
                    if (elem is SpreadElement rest)
                    {
                        if (i != arr.Elements.Count - 1)
                        {
                            _errors.Add("SyntaxError: Rest element must be last in array binding pattern");
                            return false;
                        }
                        if (rest.Argument is AssignmentExpression)
                        {
                            _errors.Add("SyntaxError: Rest element cannot have a default initializer");
                            return false;
                        }
                    }
                    if (!ValidateBindingPattern(elem)) return false;
                }
                return true;
            }

            if (node is AssignmentExpression assign)
            {
                // Default value pattern: binding = default
                return ValidateBindingPattern(assign.Left);
            }

            if (node is SpreadElement spread)
            {
                return ValidateBindingPattern(spread.Argument);
            }

            // Any other node type is invalid in a binding pattern
             _errors.Add($"Invalid element in binding pattern: {node.GetType().Name}");
            return false;
        }


        private Expression ParseCallExpression(Expression function)
        {
            var exp = new CallExpression { Token = _curToken, Function = function };
            exp.Arguments = ParseCallArguments();

            if (function is Identifier identifier &&
                string.Equals(identifier.Value, "eval", StringComparison.Ordinal))
            {
                bool allowNewTarget = _inClassFieldInitializer || _functionDepth > 0;
                return new DirectEvalExpression
                {
                    Token = exp.Token,
                    Source = exp.Arguments.Count > 0 ? exp.Arguments[0] : new UndefinedLiteral(),
                    AllowNewTarget = allowNewTarget,
                    ForceUndefinedNewTarget = _inClassFieldInitializer,
                    AllowSuperProperty = _classHasHeritageStack.Count > 0 && _classHasHeritageStack.Peek()
                };
            }

            return exp;
        }

        private List<Expression> ParseCallArguments()
        {
            // Console.WriteLine($"[DEBUG] ParseCallArguments: Start. Cur={_curToken.Type}, Peek={_peekToken.Type}");
            var args = new List<Expression>();

            if (PeekTokenIs(TokenType.RParen))
            {
                NextToken();
                return args;
            }

            NextToken();
            // Console.WriteLine($"[DEBUG] ParseCallArguments: First Arg. Cur={_curToken.Type}");
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
                // Console.WriteLine($"[DEBUG] ParseCallArguments: Loop check pass. Peek={_peekToken.Type}");
                NextToken(); // consume comma
                
                // Handle trailing comma: func(a,b,)
                if (PeekTokenIs(TokenType.RParen))
                {
                    NextToken(); // move to RParen
                    return args;
                }
                
                NextToken(); // move to next argument
                // Console.WriteLine($"[DEBUG] ParseCallArguments: Next Arg. Cur={_curToken.Type}, Peek={_peekToken.Type}");
                
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
                // Console.WriteLine($"[DEBUG] ParseCallArguments: After Arg Parse. Cur={_curToken.Type}, Peek={_peekToken.Type}");
            }

            // Nested parses can legitimately leave us positioned on the
            // closing ')' for this call. Accept that terminal state directly.
            if (CurTokenIs(TokenType.RParen) && !PeekTokenIs(TokenType.RParen))
            {
                return args;
            }

            // Console.WriteLine($"[DEBUG] ParseCallArguments Loop End. Cur={_curToken.Type}, Peek={_peekToken.Type}");
            if (!ExpectPeek(TokenType.RParen))
            {
                if (_allowRecovery)
                {
                    // Console.WriteLine($"[DEBUG-FAIL] ParseCallArguments ExpectPeek(RParen) failed! Cur={_curToken.Type}, Peek={_peekToken.Type}");
                    // Recovery: skip to RParen or statement boundary
                    while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.Semicolon) && 
                           !CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                    {
                        NextToken();
                    }
                }
                return args;
            }

            return args;
        }

        private Expression ParseNewExpression()
        {
            // ES2015: new.target
            if (PeekTokenIs(TokenType.Dot))
            {
                var newToken = _curToken;
                NextToken(); // consume 'new'
                NextToken(); // consume '.'
                
                if (CurTokenIs(TokenType.Identifier) && _curToken.Literal == "target")
                {
                    if ((_functionDepth - _arrowFunctionDepth) == 0 && !_allowNewTargetOutsideFunction)
                    {
                        _errors.Add("SyntaxError: new.target expression is not allowed here");
                    }
                    return new NewTargetExpression { Token = newToken };
                }
                
                // If we get here, it was new.somethingElse which is invalid
                _errors.Add($"Unexpected token in new expression: {_curToken.Literal}. Expected 'target'.");
                return null;
            }

            var exp = new NewExpression { Token = _curToken };
            NextToken(); // consume 'new'

            // Parse constructor - use Call precedence to stop before parsing arguments
            exp.Constructor = ParseExpression(Precedence.Call);
            if (exp.Constructor == null || exp.Constructor is EmptyExpression)
            {
                _errors.Add("SyntaxError: Missing constructor in new expression");
            }

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

        private Expression ParseComputedPropertyKeyExpression()
        {
            bool previousNoIn = _noIn;
            _noIn = false;
            try
            {
                return ParseExpression(Precedence.Lowest);
            }
            finally
            {
                _noIn = previousNoIn;
            }
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
                    var spreadArg = ParseExpression(Precedence.Comma);
                    // Store spread as special key
                    obj.Pairs[$"__spread_{obj.Pairs.Count}"] = new SpreadElement { Token = _curToken, Argument = spreadArg };
                    
                    if (PeekTokenIs(TokenType.Comma)) NextToken();
                    continue;
                }
                
                // Handle generator method: { *method() {} }
                if (CurTokenIs(TokenType.Asterisk))
                {
                    NextToken(); // consume '*', move to method name
                    string genKey;
                    Expression genComputedKey = null;

                    if (CurTokenIs(TokenType.LBracket))
                    {
                        // Computed generator: { *[expr]() {} }
                        NextToken();
                        genComputedKey = ParseComputedPropertyKeyExpression();
                        if (!ExpectPeek(TokenType.RBracket)) return null;
                        genKey = $"__computed_gen_{obj.Pairs.Count}";
                    }
                    else
                    {
                        genKey = _curToken.Literal;
                    }

                    if (PeekTokenIs(TokenType.LParen))
                    {
                        var genFunc = ParseMethodLikeFunctionLiteral(_curToken, isGenerator: true);
                        if (genFunc == null) return null;
                        bool genParamsSimple = _lastParsedParamsIsSimple;
                        bool genParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                        bool genTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                        ValidateMethodParameterEarlyErrors(
                            genFunc.Parameters,
                            genFunc.Body,
                            genParamsSimple,
                            genParamsDuplicate,
                            genTrailingCommaAfterRest,
                            false);
                        obj.Pairs[genKey] = genFunc;
                        if (genComputedKey != null) obj.ComputedKeys[genKey] = genComputedKey;
                        if (PeekTokenIs(TokenType.Comma)) NextToken();
                        continue;
                    }
                }

                string key = "";
                Expression computedKey = null;
                bool isComputed = false;

                // Handle computed property name: { [expr]: value }
                if (CurTokenIs(TokenType.LBracket))
                {
                    isComputed = true;
                    NextToken(); // Move past '['
                    computedKey = ParseComputedPropertyKeyExpression();
                    if (!ExpectPeek(TokenType.RBracket)) return null;
                    
                    // Use a placeholder key for computed properties
                    key = $"__computed_{obj.Pairs.Count}";
                }
                // Handle numeric key: { 0: value }
                else if (CurTokenIs(TokenType.Number))
                {
                    key = _curToken.Literal;
                }
                // Key can be Identifier or String or any keyword (JS allows keywords as property names)
                else if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String) || IsKeywordToken(_curToken.Type))
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

                // Check for async method: { async foo() {} } or { async *foo() {} }
                if (key == "async" && (PeekTokenIs(TokenType.Identifier) || PeekTokenIs(TokenType.Asterisk) || PeekTokenIs(TokenType.LBracket)))
                {
                    bool isGenerator = false;
                    NextToken(); // Consume 'async', move to '*' or method name

                    // Handle async generator: { async *foo() {} }
                    if (CurTokenIs(TokenType.Asterisk))
                    {
                        isGenerator = true;
                        NextToken(); // consume '*', move to name
                    }
                    // Handle computed async method: { async [expr]() {} }
                    if (CurTokenIs(TokenType.LBracket))
                    {
                        NextToken(); // move past '['
                        var asyncComputedKey = ParseComputedPropertyKeyExpression();
                        if (!ExpectPeek(TokenType.RBracket)) return null;
                        key = $"__computed_async_{obj.Pairs.Count}";
                        if (PeekTokenIs(TokenType.LParen))
                        {
                            var methodFunc = ParseMethodLikeFunctionLiteral(_curToken, isAsync: true, isGenerator: isGenerator);
                            if (methodFunc == null) return null;
                            bool methodParamsSimple = _lastParsedParamsIsSimple;
                            bool methodParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                            bool methodTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                            ValidateMethodParameterEarlyErrors(
                                methodFunc.Parameters,
                                methodFunc.Body,
                                methodParamsSimple,
                                methodParamsDuplicate,
                                methodTrailingCommaAfterRest,
                                true);
                            obj.Pairs[key] = methodFunc;
                            obj.ComputedKeys[key] = asyncComputedKey;
                            if (PeekTokenIs(TokenType.Comma)) NextToken();
                            continue;
                        }
                    }
                    else
                    {
                        key = _curToken.Literal;
                    }
                    
                    if (PeekTokenIs(TokenType.LParen))
                    {
                        var methodFunc = ParseMethodLikeFunctionLiteral(_curToken, isAsync: true, isGenerator: isGenerator);
                        if (methodFunc == null) return null;
                        bool methodParamsSimple = _lastParsedParamsIsSimple;
                        bool methodParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                        bool methodTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                        ValidateMethodParameterEarlyErrors(
                            methodFunc.Parameters,
                            methodFunc.Body,
                            methodParamsSimple,
                            methodParamsDuplicate,
                            methodTrailingCommaAfterRest,
                            true);
                        
                        obj.Pairs[key] = methodFunc;
                        
                        if (PeekTokenIs(TokenType.Comma)) NextToken();
                        continue;
                    }
                    // If we get here, it looked like async method but wasn't fully valid?
                    // e.g. { async foo : 1 } - Invalid syntax
                    // { async foo } - Invalid
                    // So we can probably let it error out or return null?
                    // But we consumed tokens. Code structure suggests we return null or add error.
                    _errors.Add($"[Debug] ParseObjectLiteral: expected async method body");
                    return null;
                }

                // Check for getter/setter: { get foo() {}, set foo(v) {} } or computed: { get [expr]() {} }
                if ((key == "get" || key == "set") && (PeekTokenIs(TokenType.Identifier) || PeekTokenIs(TokenType.LBracket) || PeekTokenIs(TokenType.String) || PeekTokenIs(TokenType.Number) || IsKeywordToken(_peekToken.Type)))
                {
                    var accessor = key;

                    // Handle computed accessor: get [expr]() {}
                    if (PeekTokenIs(TokenType.LBracket))
                    {
                        NextToken(); // Move to '['
                        NextToken(); // Move past '['
                        var accessorComputedKey = ParseComputedPropertyKeyExpression();
                        if (!ExpectPeek(TokenType.RBracket)) return null;
                        key = $"__computed_{accessor}_{obj.Pairs.Count}";

                        if (PeekTokenIs(TokenType.LParen))
                        {
                            var methodFunc = ParseMethodLikeFunctionLiteral(_curToken);
                            if (methodFunc == null) return null;
                            bool methodParamsSimple = _lastParsedParamsIsSimple;
                            bool methodParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                            bool methodTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                            ValidateMethodParameterEarlyErrors(
                                methodFunc.Parameters,
                                methodFunc.Body,
                                methodParamsSimple,
                                methodParamsDuplicate,
                                methodTrailingCommaAfterRest,
                                false);
                            var pairKey = $"__{accessor}_{key}";
                            obj.Pairs[pairKey] = methodFunc;
                            obj.ComputedKeys[pairKey] = accessorComputedKey;
                            if (PeekTokenIs(TokenType.Comma)) NextToken();
                            continue;
                        }
                    }
                    else
                    {
                        NextToken(); // Move to property name
                        key = _curToken.Literal;

                        if (PeekTokenIs(TokenType.LParen))
                        {
                            var methodFunc = ParseMethodLikeFunctionLiteral(_curToken);
                            if (methodFunc == null) return null;
                            bool methodParamsSimple = _lastParsedParamsIsSimple;
                            bool methodParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                            bool methodTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                            ValidateMethodParameterEarlyErrors(
                                methodFunc.Parameters,
                                methodFunc.Body,
                                methodParamsSimple,
                                methodParamsDuplicate,
                                methodTrailingCommaAfterRest,
                                false);

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
                    var methodFunc = ParseMethodLikeFunctionLiteral(_curToken);
                    if (methodFunc == null) return null;
                    bool methodParamsSimple = _lastParsedParamsIsSimple;
                    bool methodParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                    bool methodTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                    ValidateMethodParameterEarlyErrors(
                        methodFunc.Parameters,
                        methodFunc.Body,
                        methodParamsSimple,
                        methodParamsDuplicate,
                        methodTrailingCommaAfterRest,
                        false);
                    
                    obj.Pairs[key] = methodFunc;
                    
                    if (!PeekTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.Comma))
                    {
                        if (PeekTokenIs(TokenType.Eof)) break;
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
                    // Destructuring default: { key = defaultValue }
                    // Store as AssignmentExpression so ValidateBindingPattern recurses on left (Identifier)
                    var leftIdent = new Identifier(_curToken, key);
                    NextToken(); // Consume =
                    var assignToken = _curToken;
                    NextToken(); // Move to value
                    var defaultValue = ParseExpression(Precedence.Comma);
                    var assignExpr = new AssignmentExpression
                    {
                        Token = assignToken,
                        Left = leftIdent,
                        Right = defaultValue
                    };
                    obj.Pairs[key] = assignExpr;
                }
                else if (PeekTokenIs(TokenType.Comma) || PeekTokenIs(TokenType.RBrace))
                {
                    // Shorthand property: { key } === { key: key }
                    if (IsReservedIdentifierReference(_curToken))
                    {
                        _errors.Add($"SyntaxError: Unexpected reserved word '{_curToken.Literal}'");
                    }
                    obj.Pairs[key] = new Identifier(_curToken, key);
                }
                else
                {
                    var debugMessage = $"[Debug] ParseObjectLiteral failed for key: {key}, Next token: {_peekToken.Type}";
                    if (_lexer != null)
                    {
                        debugMessage += $"\nContext:\n{_lexer.GetCodeContext(_peekToken.Line, _peekToken.Column)}";
                    }
                    _errors.Add(debugMessage);
                    return null;
                }

                // Value parsing can legitimately leave us on the object's closing brace
                // when the object literal is immediately followed by an outer closer.
                // Example: `({a:1})` or `var x={a:1};`
                bool endedObjectDuringValueParse = CurTokenIs(TokenType.RBrace) &&
                                                   (PeekTokenIs(TokenType.RParen) ||
                                                    PeekTokenIs(TokenType.Semicolon) ||
                                                    PeekTokenIs(TokenType.RBracket) ||
                                                    PeekTokenIs(TokenType.Eof));
                if (endedObjectDuringValueParse)
                {
                    break;
                }

                if (!PeekTokenIs(TokenType.RBrace) && !ExpectPeek(TokenType.Comma))
                {
                    return null;
                }
            }

            if (CurTokenIs(TokenType.RBrace) && !PeekTokenIs(TokenType.RBrace))
            {
                return obj;
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
            else if (CurTokenIs(TokenType.PrivateIdentifier))
            {
                // Support private fields access: obj.#field
                // Store with # prefix (already in token literal)
                if (_classDepth == 0)
                {
                    _errors.Add("SyntaxError: Private field '#name' must be declared in an enclosing class");
                }
                if (obj is Identifier ident && ident.Value == "super")
                {
                    _errors.Add("SyntaxError: Private fields cannot be accessed on super");
                }
                exp.Property = _curToken.Literal;
            }
            else if (IsKeywordToken(_curToken.Type))
            {
                // Allow keywords as property names
                exp.Property = _curToken.Literal;
            }
            else
            {
                var msg = $"expected property name, got {_curToken.Type} instead";
                if (_lexer != null)
                {
                    msg += $"\nContext:\n{_lexer.GetCodeContext(_curToken.Line, _curToken.Column)}";
                }
                _errors.Add(msg);
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
                   type == TokenType.Static ||
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
                   type == TokenType.Of ||
                   type == TokenType.With ||
                   type == TokenType.Super ||
                   type == TokenType.True ||
                   type == TokenType.False ||
                   type == TokenType.Null ||
                   type == TokenType.Undefined;
        }

        // IdentifierName in grammar: Identifier plus reserved words/keywords.
        private bool IsIdentifierNameToken(TokenType type)
        {
            return type == TokenType.Identifier || IsKeywordToken(type);
        }

        // ES2020: Parse import.meta or dynamic import(...)
        private Expression ParseImportExpression()
        {
            var token = _curToken;
            
            // Check for import.meta
            if (PeekTokenIs(TokenType.Dot))
            {
                NextToken(); // consume .
                if (PeekTokenIs(TokenType.Identifier) && _peekToken.Literal == "meta")
                {
                    NextToken(); // consume meta
                    return new ImportMetaExpression { Token = token };
                }
                
                // Error recovery: import.somethingElse is invalid
                _errors.Add($"Expected 'meta' after 'import.', got {_peekToken.Literal}");
                return null;
            }
            
            // Check for dynamic import(...)
            if (PeekTokenIs(TokenType.LParen))
            {
                NextToken(); // consume (
                var args = ParseExpressionList(TokenType.RParen);
                if (args.Count == 0)
                {
                    _errors.Add("import() requires at least one argument");
                    return null;
                }
                 
                // Return as CallExpression for now, or a specific DynamicImportExpression
                // Using CallExpression with unique callee name for runtime to handle
                return new CallExpression 
                { 
                    Token = token,
                    Function = new Identifier(token, "import"), // special identifier
                    Arguments = args
                };
            }
            
            // If used as expression but not meta or dynamic import, it's a syntax error
            // (e.g. "var x = import;")
            _errors.Add("Unexpected token import");
            return null;
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
            
            if (!IsValidAssignmentTarget(left, allowDestructuring: true))
            {
                _errors.Add($"Invalid left-hand side in assignment: {left?.GetType().Name}");
                return null;
            }
            
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

        private bool ExpectPeek(TokenType type, [CallerMemberName] string caller = null)
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

            PeekError(type, caller);
            return false;
        }

        private void PeekError(TokenType type, string caller = null)
        {
            var callerPrefix = string.IsNullOrEmpty(caller) ? string.Empty : $"[{caller}] ";
            var msg = $"{callerPrefix}expected next token to be {type}, got {_peekToken.Type} instead (cur={_curToken.Type}, curLiteral='{_curToken.Literal}')";
            // Console.WriteLine($"[DEBUG] PeekError: {msg} at line {_peekToken.Line} col {_peekToken.Column}. CurToken={_curToken.Type}");
            if (_lexer != null)
            {
                 msg += $"\nContext:\n{_lexer.GetCodeContext(_peekToken.Line, _peekToken.Column)}";
            }
            _errors.Add(msg);
        }

        private void NoPrefixParseFnError(TokenType type)
        {
            var msg = $"no prefix parse function for {type} found at line {_curToken.Line}, column {_curToken.Column}";
            if (_lexer != null)
            {
                msg += $"\nContext:\n{_lexer.GetCodeContext(_curToken.Line, _curToken.Column)}";
            }

            _errors.Add(msg);
        }



        private Precedence CurPrecedence()
        {
            if (_precedences.TryGetValue(_curToken.Type, out var p)) return p;
            return Precedence.Lowest;
        }




        
        /* ... */
        
        private Precedence PeekPrecedence()
    {
        if (_precedences.TryGetValue(_peekToken.Type, out var p))
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

            stmt.Block = ParseBlockStatement(consumeTerminator: false);
            // _curToken is now on '}' of the try block.

            bool AtCatchOrFinallyBoundary() =>
                CurTokenIs(TokenType.Catch) ||
                PeekTokenIs(TokenType.Catch) ||
                CurTokenIs(TokenType.Finally) ||
                PeekTokenIs(TokenType.Finally);

            // Recovery for minified bundles: the try-block parser can stop on an inner
            // `...};` boundary. Continue consuming statements inside this try until the
            // real `} catch/finally` boundary is reached.
            if (!AtCatchOrFinallyBoundary() && CurTokenIs(TokenType.RBrace) && PeekTokenIs(TokenType.Semicolon))
            {
                int safety = 0;
                while (!CurTokenIs(TokenType.Eof) && safety++ < 8192)
                {
                    if (CurTokenIs(TokenType.RBrace) &&
                        (PeekTokenIs(TokenType.Catch) || PeekTokenIs(TokenType.Finally)))
                    {
                        break;
                    }

                    if (CurTokenIs(TokenType.RBrace))
                    {
                        NextToken();
                        continue;
                    }

                    var recovered = ParseStatement();
                    if (recovered != null)
                    {
                        stmt.Block.Statements.Add(recovered);
                    }

                    if (CurTokenIs(TokenType.RBrace) &&
                        (PeekTokenIs(TokenType.Catch) || PeekTokenIs(TokenType.Finally)))
                    {
                        break;
                    }

                    if (!CurTokenIs(TokenType.Eof))
                    {
                        NextToken();
                    }
                }
            }

            if (CurTokenIs(TokenType.Catch) || PeekTokenIs(TokenType.Catch))
            {
                if (!CurTokenIs(TokenType.Catch))
                {
                    NextToken(); // consume '}' of try block, _curToken = 'catch'
                }
                // Optional catch parameter: catch(e) or catch({message}) or catch (no param - ES2019)
                if (PeekTokenIs(TokenType.LParen))
                {
                    NextToken(); // move to (
                    NextToken(); // move past (

                    // Handle destructuring: catch({message})
                    if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
                    {
                        // Skip destructuring pattern
                        var pattern = ParseExpression(Precedence.Comma);
                        ValidateBindingPattern(pattern);
                        stmt.CatchParameter = new Identifier(_curToken, "__catch_destructure") { DestructuringPattern = pattern };
                    }
                    else if (CurTokenIs(TokenType.Identifier))
                    {
                        ValidateBindingIdentifier(_curToken);
                        stmt.CatchParameter = new Identifier(_curToken, _curToken.Literal);
                    }
                    // else: empty catch parameter - unusual but allowed in some cases

                    if (!ExpectPeek(TokenType.RParen)) return null;
                }
                // else: catch without parameter (ES2019 optional catch binding)

                if (!ExpectPeek(TokenType.LBrace)) return null;
                stmt.CatchBlock = ParseBlockStatement(consumeTerminator: false);
                // _curToken is now on '}' of catch block
            }

            if (CurTokenIs(TokenType.Finally) || PeekTokenIs(TokenType.Finally))
            {
                if (!CurTokenIs(TokenType.Finally))
                {
                    NextToken(); // consume '}' of catch/try block
                }
                if (!ExpectPeek(TokenType.LBrace)) return null;
                stmt.FinallyBlock = ParseBlockStatement(consumeTerminator: false);
                // _curToken is now on '}' of finally block
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
                list.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Comma) });
            }
            else
            {
                var expr = ParseExpression(Precedence.Comma);
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
                    list.Add(new SpreadElement { Token = _curToken, Argument = ParseExpression(Precedence.Comma) });
                }
                else
                {
                    var expr = ParseExpression(Precedence.Comma);
                    list.Add(expr);
                }
            }

            if (!ExpectPeek(end))
            {
                if (_allowRecovery)
                {
                    // Recovery: try to skip to end token instead of returning null
                    while (!CurTokenIs(end) && !CurTokenIs(TokenType.Eof) && 
                           !CurTokenIs(TokenType.Semicolon) && !CurTokenIs(TokenType.RBrace))
                    {
                        NextToken();
                    }
                }
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
                _errors.Add("SyntaxError: Illegal newline after throw");
                return stmt;
            }

            stmt.Value = ParseExpression(Precedence.Lowest);
            if (stmt.Value == null || stmt.Value is EmptyExpression)
            {
                _errors.Add("SyntaxError: Missing throw expression");
            }

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

            bool bodyHasBraces = PeekTokenIs(TokenType.LBrace);

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();
            if (!bodyHasBraces && stmt.Body?.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(stmt.Body.Statements[0], "while statement");
            }

            return stmt;
        }

        // ES5.1 with statement: with (expression) statement
        private Statement ParseWithStatement()
        {
            var stmt = new WithStatement { Token = _curToken };

            if (_isStrictMode || _isModule)
            {
                _errors.Add("SyntaxError: Strict mode code may not include a with statement");
            }

            if (!ExpectPeek(TokenType.LParen)) return null;

            NextToken();
            stmt.Object = ParseExpression(Precedence.Lowest);

            if (!ExpectPeek(TokenType.RParen))
            {
                // Recovery: skip to RParen or LBrace
                while (!CurTokenIs(TokenType.RParen) && !CurTokenIs(TokenType.LBrace) && !CurTokenIs(TokenType.Eof))
                {
                    NextToken();
                }
            }

            bool bodyHasBraces = PeekTokenIs(TokenType.LBrace);

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();

            // In single-statement position, declarations are not valid statement bodies.
            if (!bodyHasBraces && stmt.Body is BlockStatement singleBodyBlock && singleBodyBlock.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(singleBodyBlock.Statements[0], "with statement");
            }

            return stmt;
        }

        private Statement ParseForStatement()
        {
            var forToken = _curToken;
            
            // ES2018: Check for 'for await' pattern
            bool isAwait = false;
            if (PeekTokenIs(TokenType.Await))
            {
                NextToken(); // consume 'await'
                isAwait = true;
            }

            if (!ExpectPeek(TokenType.LParen)) return null;
            NextToken();

            var stmt = new ForStatement { Token = forToken };

            // Disambiguate lexical declaration vs identifier-reference for `let`.
            bool hasForDeclaration =
                CurTokenIs(TokenType.Var) ||
                CurTokenIs(TokenType.Const) ||
                (CurTokenIs(TokenType.Let) && !PeekTokenIs(TokenType.In) && !PeekTokenIs(TokenType.Of));

            Token declarationToken = null;
            bool hasLexicalForDeclaration = false;
            IEnumerable<string> lexicalForBoundNames = null;
            if (hasForDeclaration)
            {
                declarationToken = _curToken;
                NextToken();
            }

            if (hasForDeclaration)
            {
                if (declarationToken.Type == TokenType.Var)
                {
                    var varDeclarators = ParseForDeclarationList(declarationToken, DeclarationKind.Var);
                    if (varDeclarators.Count == 1 && (PeekTokenIs(TokenType.In) || PeekTokenIs(TokenType.Of)))
                    {
                        var singleDeclarator = varDeclarators[0];
                        bool isOf = PeekTokenIs(TokenType.Of);

                        if (singleDeclarator.Value != null)
                        {
                            _errors.Add("SyntaxError: for-in/of declaration may not have an initializer");
                        }

                        NextToken(); // 'in' or 'of'
                        NextToken(); // rhs expression

                        var rhsExpr = ParseExpression(isOf ? Precedence.Assignment : Precedence.Lowest);
                        if (rhsExpr == null || rhsExpr is EmptyExpression)
                        {
                            _errors.Add($"SyntaxError: Missing right-hand side in for-{(isOf ? "of" : "in")} statement");
                        }
                        else if (isOf && IsInvalidForOfRightHandSide(rhsExpr))
                        {
                            _errors.Add("SyntaxError: Invalid right-hand side in for-of statement");
                        }

                        if (!ExpectPeek(TokenType.RParen)) return null;

                        if (isOf)
                        {
                            bool forOfBodyHasBraces = PeekTokenIs(TokenType.LBrace);
                            var body = ParseBodyAsBlock();
                            if (!forOfBodyHasBraces && body?.Statements.Count > 0)
                            {
                                ReportInvalidSingleStatementBody(body.Statements[0], "for-of statement");
                            }

                            return new ForOfStatement
                            {
                                Token = forToken,
                                Variable = singleDeclarator.Name,
                                DestructuringPattern = singleDeclarator.DestructuringPattern,
                                BindingKind = DeclarationKind.Var,
                                Iterable = rhsExpr,
                                Body = body,
                                IsAwait = isAwait
                            };
                        }

                        bool forInBodyHasBraces = PeekTokenIs(TokenType.LBrace);
                        var forInBody = ParseBodyAsBlock();
                        if (!forInBodyHasBraces && forInBody?.Statements.Count > 0)
                        {
                            ReportInvalidSingleStatementBody(forInBody.Statements[0], "for-in statement");
                        }

                        return new ForInStatement
                        {
                            Token = forToken,
                            Variable = singleDeclarator.Name,
                            DestructuringPattern = singleDeclarator.DestructuringPattern,
                            BindingKind = DeclarationKind.Var,
                            Object = rhsExpr,
                            Body = forInBody
                        };
                    }

                    stmt.Init = varDeclarators.Count == 1
                        ? varDeclarators[0]
                        : new BlockStatement
                        {
                            Token = declarationToken,
                            Statements = new List<Statement>(varDeclarators)
                        };
                }
                else
                {
                Identifier bindingIdentifier = null;
                Expression bindingPattern = null;
                Expression initializer = null;

                if (CurTokenIs(TokenType.LBracket) || CurTokenIs(TokenType.LBrace))
                {
                    bindingPattern = CurTokenIs(TokenType.LBracket) ? ParseArrayLiteral() : ParseObjectLiteral();
                    ValidateBindingPattern(bindingPattern);
                }
                else if (IsIdentifierNameToken(_curToken.Type))
                {
                    if (!ValidateBindingIdentifier(_curToken))
                    {
                        return null;
                    }

                    bindingIdentifier = new Identifier(_curToken, _curToken.Literal);
                }
                else
                {
                    _errors.Add($"SyntaxError: Expected binding identifier in for declaration, got {_curToken.Type}");
                }

                bool isLexicalForDeclaration = declarationToken.Type == TokenType.Let || declarationToken.Type == TokenType.Const;
                if (isLexicalForDeclaration)
                {
                    hasLexicalForDeclaration = true;
                    lexicalForBoundNames = bindingIdentifier != null
                        ? new[] { bindingIdentifier.Value }
                        : ExtractBindingNames(bindingPattern);
                    if (bindingIdentifier != null && bindingIdentifier.Value == "let")
                    {
                        _errors.Add("SyntaxError: 'let' is not a valid lexical binding name");
                    }

                    if (bindingPattern != null)
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var bindingName in ExtractBindingNames(bindingPattern))
                        {
                            if (bindingName == "let")
                            {
                                _errors.Add("SyntaxError: 'let' is not a valid lexical binding name");
                            }

                            if (!seen.Add(bindingName))
                            {
                                _errors.Add($"SyntaxError: Duplicate declaration '{bindingName}'");
                            }
                        }
                    }
                }

                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // '='
                    NextToken(); // initializer start
                    initializer = ParseExpression(Precedence.Lowest);
                }

                if (PeekTokenIs(TokenType.In) || PeekTokenIs(TokenType.Of))
                {
                    bool isOf = PeekTokenIs(TokenType.Of);

                    if (initializer != null)
                    {
                        _errors.Add("SyntaxError: for-in/of declaration may not have an initializer");
                    }

                    NextToken(); // 'in' or 'of'
                    NextToken(); // rhs expression

                    var rhsExpr = ParseExpression(isOf ? Precedence.Assignment : Precedence.Lowest);
                    if (rhsExpr == null || rhsExpr is EmptyExpression)
                    {
                        _errors.Add($"SyntaxError: Missing right-hand side in for-{(isOf ? "of" : "in")} statement");
                    }
                    else if (isOf && IsInvalidForOfRightHandSide(rhsExpr))
                    {
                        _errors.Add("SyntaxError: Invalid right-hand side in for-of statement");
                    }

                    if (!ExpectPeek(TokenType.RParen)) return null;

                    if (isOf)
                    {
                        bool forOfBodyHasBraces = PeekTokenIs(TokenType.LBrace);
                        var body = ParseBodyAsBlock();
                        if (!forOfBodyHasBraces && body?.Statements.Count > 0)
                        {
                            ReportInvalidSingleStatementBody(body.Statements[0], "for-of statement");
                        }

                        if (hasLexicalForDeclaration)
                        {
                            ValidateLoopBodyVarRedeclarations(lexicalForBoundNames, body, "for-of statement");
                        }

                        return new ForOfStatement
                        {
                            Token = forToken,
                            Variable = bindingIdentifier,
                            DestructuringPattern = bindingPattern,
                            BindingKind = declarationToken.Type == TokenType.Const ? DeclarationKind.Const : declarationToken.Type == TokenType.Let ? DeclarationKind.Let : DeclarationKind.Var,
                            Iterable = rhsExpr,
                            Body = body,
                            IsAwait = isAwait
                        };
                    }

                    bool forInBodyHasBraces = PeekTokenIs(TokenType.LBrace);
                    var forInBody = ParseBodyAsBlock();
                    if (!forInBodyHasBraces && forInBody?.Statements.Count > 0)
                    {
                        ReportInvalidSingleStatementBody(forInBody.Statements[0], "for-in statement");
                    }

                    if (hasLexicalForDeclaration)
                    {
                        ValidateLoopBodyVarRedeclarations(lexicalForBoundNames, forInBody, "for-in statement");
                    }

                    return new ForInStatement
                    {
                        Token = forToken,
                        Variable = bindingIdentifier,
                        DestructuringPattern = bindingPattern,
                        BindingKind = declarationToken.Type == TokenType.Const ? DeclarationKind.Const : declarationToken.Type == TokenType.Let ? DeclarationKind.Let : DeclarationKind.Var,
                        Object = rhsExpr,
                        Body = forInBody
                    };
                }

                stmt.Init = new LetStatement
                {
                    Token = declarationToken,
                    Kind = declarationToken.Type == TokenType.Const
                        ? DeclarationKind.Const
                        : declarationToken.Type == TokenType.Let
                            ? DeclarationKind.Let
                            : DeclarationKind.Var,
                    Name = bindingIdentifier,
                    DestructuringPattern = bindingPattern,
                    Value = initializer
                };
                }
            }
            else if (!CurTokenIs(TokenType.Semicolon))
            {
                Expression exp;
                if (CurTokenIs(TokenType.LBracket) || CurTokenIs(TokenType.LBrace))
                {
                    exp = CurTokenIs(TokenType.LBracket) ? ParseArrayLiteral() : ParseObjectLiteral();
                }
                else
                {
                    _noIn = true;
                    exp = ParseExpression(Precedence.Lowest);
                    _noIn = false;
                }

                if (PeekTokenIs(TokenType.In) || PeekTokenIs(TokenType.Of))
                {
                    bool isOf = PeekTokenIs(TokenType.Of);

                    if (!IsValidForInOfTarget(exp))
                    {
                        _errors.Add($"SyntaxError: Invalid left-hand side in for-{(isOf ? "of" : "in")} statement");
                    }

                    NextToken(); // 'in' or 'of'
                    NextToken(); // rhs expression

                    var rhsExpr = ParseExpression(isOf ? Precedence.Assignment : Precedence.Lowest);
                    if (rhsExpr == null || rhsExpr is EmptyExpression)
                    {
                        _errors.Add($"SyntaxError: Missing right-hand side in for-{(isOf ? "of" : "in")} statement");
                    }
                    else if (isOf && IsInvalidForOfRightHandSide(rhsExpr))
                    {
                        _errors.Add("SyntaxError: Invalid right-hand side in for-of statement");
                    }

                    if (!ExpectPeek(TokenType.RParen)) return null;

                    if (isOf)
                    {
                        bool forOfBodyHasBraces = PeekTokenIs(TokenType.LBrace);
                        var body = ParseBodyAsBlock();
                        if (!forOfBodyHasBraces && body?.Statements.Count > 0)
                        {
                            ReportInvalidSingleStatementBody(body.Statements[0], "for-of statement");
                        }

                        if (exp is Identifier identOf)
                        {
                            return new ForOfStatement
                            {
                                Token = forToken,
                                Variable = identOf,
                                Iterable = rhsExpr,
                                Body = body,
                                IsAwait = isAwait
                            };
                        }

                        if (exp is ArrayLiteral || exp is ObjectLiteral)
                        {
                            ValidateBindingPattern(exp);
                        }

                        return new ForOfStatement
                        {
                            Token = forToken,
                            DestructuringPattern = exp,
                            Iterable = rhsExpr,
                            Body = body,
                            IsAwait = isAwait
                        };
                    }

                    bool forInBodyHasBraces = PeekTokenIs(TokenType.LBrace);
                    var forInBody = ParseBodyAsBlock();
                    if (!forInBodyHasBraces && forInBody?.Statements.Count > 0)
                    {
                        ReportInvalidSingleStatementBody(forInBody.Statements[0], "for-in statement");
                    }

                    if (exp is Identifier identIn)
                    {
                        return new ForInStatement
                        {
                            Token = forToken,
                            Variable = identIn,
                            Object = rhsExpr,
                            Body = forInBody
                        };
                    }

                    if (exp is ArrayLiteral || exp is ObjectLiteral)
                    {
                        ValidateBindingPattern(exp);
                    }

                    return new ForInStatement
                    {
                        Token = forToken,
                        DestructuringPattern = exp,
                        Object = rhsExpr,
                        Body = forInBody
                    };
                }

                stmt.Init = new ExpressionStatement { Expression = exp };
            }

            if (!CurTokenIs(TokenType.Semicolon) && !ExpectPeek(TokenType.Semicolon))
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

            bool bodyHasBraces = PeekTokenIs(TokenType.LBrace);

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();
            if (!bodyHasBraces && stmt.Body?.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(stmt.Body.Statements[0], "for statement");
            }

            if (hasLexicalForDeclaration)
            {
                ValidateLoopBodyVarRedeclarations(lexicalForBoundNames, stmt.Body, "for statement");
            }

            return stmt;
        }

        private List<LetStatement> ParseForDeclarationList(Token declarationToken, DeclarationKind kind)
        {
            var declarations = new List<LetStatement>();

            while (true)
            {
                var declarator = ParseForDeclarationDeclarator(declarationToken, kind);
                declarations.Add(declarator);

                if (!PeekTokenIs(TokenType.Comma))
                {
                    break;
                }

                NextToken(); // ','
                NextToken(); // next declarator
            }

            return declarations;
        }

        private LetStatement ParseForDeclarationDeclarator(Token declarationToken, DeclarationKind kind)
        {
            var declarator = new LetStatement
            {
                Token = declarationToken,
                Kind = kind
            };

            if (CurTokenIs(TokenType.LBrace) || CurTokenIs(TokenType.LBracket))
            {
                var patternOrInitializer = ParseExpression(Precedence.Comma);
                declarator.Name = new Identifier(declarationToken, "_destructured");

                if (TryNormalizeDestructuringAssignment(patternOrInitializer, out var bindingPattern, out var initializer))
                {
                    declarator.DestructuringPattern = bindingPattern;
                    declarator.Value = initializer;
                }
                else
                {
                    declarator.DestructuringPattern = patternOrInitializer;

                    if (PeekTokenIs(TokenType.Assign))
                    {
                        NextToken(); // '='
                        NextToken(); // initializer start
                        declarator.Value = ParseExpression(Precedence.Comma);
                    }
                    else if (kind == DeclarationKind.Const)
                    {
                        _errors.Add("SyntaxError: Missing initializer in const declaration");
                    }
                }

                ValidateBindingPattern(declarator.DestructuringPattern);
                return declarator;
            }

            if (!IsIdentifierNameToken(_curToken.Type))
            {
                if (IsKeywordToken(_curToken.Type) && !_contextualKeywords.Contains(_curToken.Type))
                {
                    _errors.Add($"SyntaxError: Unexpected reserved word '{_curToken.Literal}'");
                }
                else if (!IsKeywordToken(_curToken.Type))
                {
                    _errors.Add($"SyntaxError: Expected identifier in {kind.ToString().ToLowerInvariant()} declaration");
                }

                return declarator;
            }

            if (!ValidateBindingIdentifier(_curToken))
            {
                return declarator;
            }

            declarator.Name = new Identifier(_curToken, _curToken.Literal);
            if (_functionDepth == 0 && kind != DeclarationKind.Var && declarator.Name.Value == "undefined")
            {
                _errors.Add("SyntaxError: Lexical declaration cannot redeclare restricted global property 'undefined'");
            }

            if (PeekTokenIs(TokenType.Assign))
            {
                NextToken(); // '='
                NextToken(); // initializer start
                declarator.Value = ParseExpression(Precedence.Comma);
            }
            else if (kind == DeclarationKind.Const)
            {
                _errors.Add("SyntaxError: Missing initializer in const declaration");
            }

            return declarator;
        }

        // Parse for-of: for (x of iterable) { ... } or for await (x of asyncIterable) { ... }
        private Statement ParseForOfStatement(Token forToken, Token varToken, bool isAwait = false)
        {
            var stmt = new ForOfStatement { Token = forToken, IsAwait = isAwait };
            stmt.Variable = new Identifier(varToken, varToken.Literal);

            NextToken(); // Move past 'of'
            NextToken(); // Move to iterable expression
            
            stmt.Iterable = ParseExpression(Precedence.Assignment);
            if (stmt.Iterable != null && !(stmt.Iterable is EmptyExpression) && IsInvalidForOfRightHandSide(stmt.Iterable))
            {
                _errors.Add("SyntaxError: Invalid right-hand side in for-of statement");
            }

            if (!ExpectPeek(TokenType.RParen)) return null;

            bool bodyHasBraces = PeekTokenIs(TokenType.LBrace);
            stmt.Body = ParseBodyAsBlock();
            if (!bodyHasBraces && stmt.Body?.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(stmt.Body.Statements[0], "for-of statement");
            }
            return stmt;
        }

        private Expression ParseClassExpression()
        {
            var exp = new ClassExpression { Token = _curToken };

            // Optional name
            if (IsIdentifierNameToken(_peekToken.Type) && _peekToken.Type != TokenType.Extends)
            {
                NextToken();
                ValidateBindingIdentifier(_curToken);
                exp.Name = new Identifier(_curToken, _curToken.Literal);
            }

            if (PeekTokenIs(TokenType.Extends))
            {
                NextToken(); // extends
                NextToken(); // superClassExpression
                var superClassExpr = ParseExpression(Precedence.Lowest);
                exp.SuperClass = superClassExpr;
            }

            if (!ExpectPeek(TokenType.LBrace))
            {
                return null;
            }

            // Parse class body — advance past { then parse members
            bool prevStrictMode = _isStrictMode;
            var inheritedPrivateScope = _privateNameScopeStack.Count > 0
                ? new HashSet<string>(_privateNameScopeStack.Peek(), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            _classDepth++;
            _isStrictMode = true; // Class bodies are always strict mode.
            _privateNameScopeStack.Push(inheritedPrivateScope);
            _classHasHeritageStack.Push(exp.SuperClass != null);
            try
            {
                NextToken(); // move past '{' to first member or '}'
                while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    // Skip semicolons between members
                    if (CurTokenIs(TokenType.Semicolon))
                    {
                        NextToken();
                        continue;
                    }
                    var member = ParseClassMember();
                    if (member is MethodDefinition method)
                    {
                        exp.Methods.Add(method);
                    }
                    else if (member is ClassProperty prop)
                    {
                        exp.Properties.Add(prop);
                    }
                    else if (member is StaticBlock block)
                    {
                        exp.StaticBlocks.Add(block);
                    }
                    NextToken(); // advance past last token of member to next member or '}'
                }
                ValidateClassEarlyErrors(exp.Methods, exp.Properties, exp.SuperClass != null);
            }
            finally
            {
                _classHasHeritageStack.Pop();
                _privateNameScopeStack.Pop();
                _isStrictMode = prevStrictMode;
                _classDepth--;
            }

            // cur should be '}' now
            return exp;
        }

        private Statement ParseClassStatement()
        {
            var stmt = new ClassStatement { Token = _curToken };

            if (!IsIdentifierNameToken(_peekToken.Type))
            {
                return null;
            }

            NextToken();
            if (!ValidateBindingIdentifier(_curToken)) return null;
            stmt.Name = new Identifier(_curToken, _curToken.Literal);

            if (PeekTokenIs(TokenType.Extends))
            {
                NextToken();
                NextToken();
                var superClassExpr = ParseExpression(Precedence.Lowest);
                stmt.SuperClass = superClassExpr;
            }

            if (!ExpectPeek(TokenType.LBrace))
            {
                return null;
            }

            // Parse class body ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â advance past { then parse members
            bool prevStrictMode = _isStrictMode;
            var inheritedPrivateScope = _privateNameScopeStack.Count > 0
                ? new HashSet<string>(_privateNameScopeStack.Peek(), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            _classDepth++;
            _isStrictMode = true; // Class bodies are always strict mode.
            _privateNameScopeStack.Push(inheritedPrivateScope);
            _classHasHeritageStack.Push(stmt.SuperClass != null);
            try
            {
                NextToken(); // move past '{' to first member or '}'
                while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    // Skip semicolons between members
                    if (CurTokenIs(TokenType.Semicolon))
                    {
                        NextToken();
                        continue;
                    }
                    var member = ParseClassMember();
                    if (member is MethodDefinition method)
                    {
                        stmt.Methods.Add(method);
                    }
                    else if (member is ClassProperty prop)
                    {
                        stmt.Properties.Add(prop);
                    }
                    else if (member is StaticBlock block)
                    {
                        stmt.StaticBlocks.Add(block);
                    }
                    NextToken(); // advance past last token of member to next member or '}'
                }
                ValidateClassEarlyErrors(stmt.Methods, stmt.Properties, stmt.SuperClass != null);
            }
            finally
            {
                _classHasHeritageStack.Pop();
                _privateNameScopeStack.Pop();
                _isStrictMode = prevStrictMode;
                _classDepth--;
            }

            // cur should be '}' now
            return stmt;
        }


        // Parse decorators (Stage 3) - @decorator or @decorator(args)
        private List<Decorator> ParseDecorators()
        {
            var decorators = new List<Decorator>();
            
            while (CurTokenIs(TokenType.At))
            {
                var token = _curToken;
                NextToken(); // consume @
                
                // Parse decorator expression (identifier or call expression)
                var expr = ParseExpression(Precedence.Call);
                if (expr == null)
                {
                    _errors.Add($"Expected decorator expression after @");
                    return decorators;
                }
                
                decorators.Add(new Decorator { Token = token, Expression = expr });
                
                // Move to next token for potential next decorator or class/method
                if (PeekTokenIs(TokenType.At))
                {
                    NextToken();
                }
                else
                {
                    break;
                }
            }
            
            return decorators;
        }
        private Statement ParseClassMember()
        {
            bool isStatic = false;
            bool isAsync = false;
            bool isGenerator = false;

            // Parse decorators for methods and properties
            List<Decorator> memberDecorators = new List<Decorator>();
            if (CurTokenIs(TokenType.At))
            {
                memberDecorators = ParseDecorators();
            }

            // Check for 'static' keyword
            // On entry, _curToken is already positioned on the first token of the member
            if (CurTokenIs(TokenType.Static))
            {
                // Check if this is a static block: static { ... }
                if (PeekTokenIs(TokenType.LBrace))
                {
                    var block = new StaticBlock();
                    _classStaticBlockDepth++;
                    try
                    {
                        block.Body = ParseBodyAsBlock();
                    }
                    finally
                    {
                        _classStaticBlockDepth--;
                    }
                    return block;
                }

                // Check if 'static' is the name of the method/field: static() {} or static = 1
                if (PeekTokenIs(TokenType.LParen) || PeekTokenIs(TokenType.Assign) || PeekTokenIs(TokenType.Semicolon) || PeekTokenIs(TokenType.RBrace))
                {
                     isStatic = false;
                     // 'static' is the name ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â don't advance, handle below
                }
                else
                {
                    isStatic = true;
                    NextToken(); // Move past 'static' to the actual member name
                }
            }
            // No else needed ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â _curToken is already on the right token

            // Check for async modifier
            if (CurTokenIs(TokenType.Async) || (CurTokenIs(TokenType.Identifier) && _curToken.Literal == "async"))
            {
                // async could be a method name or modifier
                // It's a modifier if followed by: identifier, *, #, [, get, set, string, number, keyword
                if (PeekTokenIs(TokenType.Identifier) || PeekTokenIs(TokenType.Asterisk) ||
                    PeekTokenIs(TokenType.PrivateIdentifier) || PeekTokenIs(TokenType.LBracket) ||
                    PeekTokenIs(TokenType.String) || PeekTokenIs(TokenType.Number))
                {
                    isAsync = true;
                    NextToken(); // consume 'async'
                }
                // else 'async' is the method/field name itself
            }

            // Check for generator (*)
            if (CurTokenIs(TokenType.Asterisk))
            {
                isGenerator = true;
                NextToken(); // consume '*'
            }

            // Now _curToken is the actual key or a special token
            // Parse the key
            Expression memberKey = null;
            bool isPrivate = false;
            bool isComputed = false;
            string keyName = null;

            if (CurTokenIs(TokenType.PrivateIdentifier))
            {
                isPrivate = true;
                keyName = _curToken.Literal.Substring(1); // Remove '#'
                memberKey = new Identifier(_curToken, keyName);
                RegisterPrivateNameInCurrentScope(keyName);
            }
            else if (CurTokenIs(TokenType.LBracket))
            {
                // Computed property key: [expression]
                isComputed = true;
                NextToken(); // consume '['
                memberKey = ParseComputedPropertyKeyExpression();
                if (!ExpectPeek(TokenType.RBracket)) return null;
                keyName = "[computed]";
            }
            else if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String) ||
                     CurTokenIs(TokenType.Number) || CurTokenIs(TokenType.Static) ||
                     IsKeywordToken(_curToken.Type))
            {
                keyName = _curToken.Literal;
                memberKey = new Identifier(_curToken, keyName);
            }
            else
            {
                // Unknown token as class member key.
                _errors.Add($"SyntaxError: Unexpected token in class element: {_curToken.Type}");
                return null;
            }

            // If not async/generator, check for contextual keywords: get, set, async
            if (!isAsync && !isGenerator && !isComputed && !isPrivate)
            {
                // Check for getter: get name() {}
                if (keyName == "get" && !PeekTokenIs(TokenType.LParen) && !PeekTokenIs(TokenType.Assign) &&
                    !PeekTokenIs(TokenType.Semicolon) && !PeekTokenIs(TokenType.RBrace))
                {
                    NextToken();
                    return ParseClassAccessor("get", isStatic, memberDecorators);
                }

                // Check for setter: set name() {}
                if (keyName == "set" && !PeekTokenIs(TokenType.LParen) && !PeekTokenIs(TokenType.Assign) &&
                    !PeekTokenIs(TokenType.Semicolon) && !PeekTokenIs(TokenType.RBrace))
                {
                    NextToken();
                    return ParseClassAccessor("set", isStatic, memberDecorators);
                }

                // Check for async modifier (when async is parsed as Identifier, not Async token type)
                if (keyName == "async" && !PeekTokenIs(TokenType.LParen) && !PeekTokenIs(TokenType.Assign) &&
                    !PeekTokenIs(TokenType.Semicolon) && !PeekTokenIs(TokenType.RBrace))
                {
                    isAsync = true;
                    NextToken(); // consume 'async'

                    // Check for generator after async
                    if (CurTokenIs(TokenType.Asterisk))
                    {
                        isGenerator = true;
                        NextToken(); // consume '*'
                    }

                    // Re-parse key
                    isPrivate = false;
                    isComputed = false;

                    if (CurTokenIs(TokenType.PrivateIdentifier))
                    {
                        isPrivate = true;
                        keyName = _curToken.Literal.Substring(1);
                        memberKey = new Identifier(_curToken, keyName);
                        RegisterPrivateNameInCurrentScope(keyName);
                    }
                    else if (CurTokenIs(TokenType.LBracket))
                    {
                        isComputed = true;
                        NextToken();
                        memberKey = ParseComputedPropertyKeyExpression();
                        if (!ExpectPeek(TokenType.RBracket)) return null;
                        keyName = "[computed]";
                    }
                    else if (CurTokenIs(TokenType.Identifier) || CurTokenIs(TokenType.String) ||
                             CurTokenIs(TokenType.Number) || CurTokenIs(TokenType.Static) ||
                             IsKeywordToken(_curToken.Type))
                    {
                        keyName = _curToken.Literal;
                        memberKey = new Identifier(_curToken, keyName);
                    }
                }
            }

            // Check for constructor
            if (keyName == "constructor" && !isStatic && !isAsync && !isGenerator && !isComputed && !isPrivate)
            {
                var method = new MethodDefinition
                {
                    Key = new Identifier(_curToken, keyName),
                    ComputedKeyExpression = null,
                    Kind = "constructor",
                    Static = false,
                    IsPrivate = false,
                    Decorators = memberDecorators
                };

                var funcLit = ParseMethodLikeFunctionLiteral(_curToken);
                if (funcLit == null) return null;
                bool ctorParamsSimple = _lastParsedParamsIsSimple;
                bool ctorParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                bool ctorTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                ValidateMethodParameterEarlyErrors(funcLit.Parameters, funcLit.Body, ctorParamsSimple, ctorParamsDuplicate, ctorTrailingCommaAfterRest, false);
                method.Value = funcLit;
                return method;
            }

            // Is it a method or a property?
            if (PeekTokenIs(TokenType.LParen) || isGenerator)
            {
                // It's a method
                var method = new MethodDefinition
                {
                    Key = memberKey is Identifier ? (Identifier)memberKey : new Identifier(_curToken, keyName ?? ""),
                    ComputedKeyExpression = isComputed ? memberKey : null,
                    Kind = "method",
                    Static = isStatic,
                    IsPrivate = isPrivate,
                    Decorators = memberDecorators,
                    Computed = isComputed
                };

                var funcLit = ParseMethodLikeFunctionLiteral(_curToken, isAsync: isAsync, isGenerator: isGenerator);
                if (funcLit == null) return null;
                bool methodParamsSimple = _lastParsedParamsIsSimple;
                bool methodParamsDuplicate = _lastParsedParamsHasDuplicateNames;
                bool methodTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
                ValidateMethodParameterEarlyErrors(funcLit.Parameters, funcLit.Body, methodParamsSimple, methodParamsDuplicate, methodTrailingCommaAfterRest, isAsync);
                method.Value = funcLit;
                return method;
            }
            else
            {
                // It's a property/field
                var prop = new ClassProperty
                {
                    Key = memberKey is Identifier ? (Identifier)memberKey : new Identifier(_curToken, keyName ?? ""),
                    ComputedKeyExpression = isComputed ? memberKey : null,
                    Static = isStatic,
                    IsPrivate = isPrivate
                };

                if (PeekTokenIs(TokenType.Assign))
                {
                    NextToken(); // consume =
                    NextToken(); // move to value
                    bool prevInFieldInit = _inClassFieldInitializer;
                    _inClassFieldInitializer = true;
                    try
                    {
                        prop.Value = ParseExpression(Precedence.Lowest);
                    }
                    finally
                    {
                        _inClassFieldInitializer = prevInFieldInit;
                    }
                }

                if (!PeekTokenIs(TokenType.Semicolon) &&
                    !PeekTokenIs(TokenType.RBrace) &&
                    !_peekToken.HadLineTerminatorBefore)
                {
                    _errors.Add("SyntaxError: Class fields must be separated by a line terminator or semicolon");
                }

                // Skip optional semicolon
                if (PeekTokenIs(TokenType.Semicolon))
                {
                    NextToken();
                }
                return prop;
            }
        }

        /// <summary>
        /// Parse a getter or setter in a class body. _curToken is the property name.
        /// </summary>
        private Statement ParseClassAccessor(string kind, bool isStatic, List<Decorator> decorators)
        {
            bool accessorIsPrivate = CurTokenIs(TokenType.PrivateIdentifier);
            bool accessorIsComputed = false;
            Expression accessorKey;
            string accessorName;

            if (CurTokenIs(TokenType.LBracket))
            {
                accessorIsComputed = true;
                NextToken();
                accessorKey = ParseComputedPropertyKeyExpression();
                if (!ExpectPeek(TokenType.RBracket)) return null;
                accessorName = "[computed]";
            }
            else
            {
                accessorName = accessorIsPrivate ? _curToken.Literal.Substring(1) : _curToken.Literal;
                accessorKey = new Identifier(_curToken, accessorName);
                if (accessorIsPrivate)
                {
                    RegisterPrivateNameInCurrentScope(accessorName);
                }
            }

            var method = new MethodDefinition
            {
                Key = accessorKey is Identifier ? (Identifier)accessorKey : new Identifier(_curToken, accessorName),
                ComputedKeyExpression = accessorIsComputed ? accessorKey : null,
                Kind = kind,
                Static = isStatic,
                IsPrivate = accessorIsPrivate,
                Decorators = decorators,
                Computed = accessorIsComputed
            };

            var funcLit = ParseMethodLikeFunctionLiteral(_curToken);
            if (funcLit == null) return null;
            bool accessorParamsSimple = _lastParsedParamsIsSimple;
            bool accessorParamsDuplicate = _lastParsedParamsHasDuplicateNames;
            bool accessorTrailingCommaAfterRest = _lastParsedParamsHadTrailingCommaAfterRest;
            ValidateMethodParameterEarlyErrors(funcLit.Parameters, funcLit.Body, accessorParamsSimple, accessorParamsDuplicate, accessorTrailingCommaAfterRest, false);
            method.Value = funcLit;
            return method;
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
                if (funcLit.Parameters == null && !_allowRecovery)
                {
                    return null;
                }
                
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
                if (!ConsumeImportAttributesClauseIfPresent()) return null;
                if (PeekTokenIs(TokenType.Semicolon)) NextToken();
                return stmt;
            }

            // import * as ns from "module" (namespace import)
            if (CurTokenIs(TokenType.Asterisk))
            {
                if (!ExpectPeek(TokenType.As)) return null;
                if (!ExpectPeek(TokenType.Identifier)) return null;

                var specifier = new ImportSpecifier
                {
                    Local = new Identifier(_curToken, _curToken.Literal),
                    Imported = new Identifier(_curToken, "*") // Use "*" to mark namespace import
                };
                stmt.Specifiers.Add(specifier);

                if (!ExpectPeek(TokenType.From)) return null;
                if (!ExpectPeek(TokenType.String)) return null;

                stmt.Source = _curToken.Literal;
                if (!ConsumeImportAttributesClauseIfPresent()) return null;
            }
            // import { x, y } from "module"
            else if (CurTokenIs(TokenType.LBrace))
            {
                stmt.Specifiers = ParseImportSpecifiers();

                if (!ExpectPeek(TokenType.From)) return null;

                if (!ExpectPeek(TokenType.String)) return null;

                stmt.Source = _curToken.Literal;
                if (!ConsumeImportAttributesClauseIfPresent()) return null;
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
                    else if (PeekTokenIs(TokenType.Asterisk))
                    {
                        NextToken(); // Move to *
                        if (!ExpectPeek(TokenType.As)) return null;
                        if (!ExpectPeek(TokenType.Identifier)) return null;

                        var nsSpecifier = new ImportSpecifier
                        {
                            Local = new Identifier(_curToken, _curToken.Literal),
                            Imported = new Identifier(_curToken, "*")
                        };
                        stmt.Specifiers.Add(nsSpecifier);
                    }
                }

                if (!ExpectPeek(TokenType.From)) return null;

                if (!ExpectPeek(TokenType.String)) return null;

                stmt.Source = _curToken.Literal;
                if (!ConsumeImportAttributesClauseIfPresent()) return null;
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
                 
                if (!IsIdentifierNameToken(_curToken.Type) && !CurTokenIs(TokenType.String)) return specifiers;
                 
                var ident = new Identifier(_curToken, _curToken.Literal);
                ValidateModuleStringNameIfNeeded(_curToken, "import");
                specifier.Imported = ident;
                specifier.Local = ident; // Default to same name

                if (PeekTokenIs(TokenType.As))
                {
                    NextToken(); // Move to 'as'
                    NextToken(); // Move to local binding
                    if (!IsIdentifierNameToken(_curToken.Type)) return specifiers;
                    if (!ValidateBindingIdentifier(_curToken)) return specifiers;
                    specifier.Local = new Identifier(_curToken, _curToken.Literal);
                }
                else if (CurTokenIs(TokenType.String))
                {
                    _errors.Add("SyntaxError: Import specifier with string module name requires `as` binding");
                    return specifiers;
                }
                else if (!ValidateBindingIdentifier(_curToken))
                {
                    return specifiers;
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
            bool requiresTerminator = false;
            NextToken(); // Move past 'export'

            if (CurTokenIs(TokenType.Default))
            {
                NextToken(); // Move past 'default'
                if (CurTokenIs(TokenType.Function))
                {
                    stmt.DefaultExpression = ParseFunctionLiteral(forceAsync: false, allowBodyExpressionContinuation: false);
                }
                else if (CurTokenIs(TokenType.Class))
                {
                    stmt.DefaultExpression = ParseClassExpression();
                }
                else if (CurTokenIs(TokenType.Async) && PeekTokenIs(TokenType.Function))
                {
                    stmt.DefaultExpression = ParseAsyncPrefix();
                }
                else
                {
                    // "export default" with expression uses AssignmentExpression grammar
                    // (comma expressions are not allowed).
                    stmt.DefaultExpression = ParseExpression(Precedence.Comma);
                    if (PeekTokenIs(TokenType.Comma))
                    {
                        _errors.Add("SyntaxError: Unexpected token ',' in export default declaration");
                    }
                    requiresTerminator = true;
                }

                if ((stmt.DefaultExpression is FunctionLiteral ||
                     stmt.DefaultExpression is AsyncFunctionExpression ||
                     stmt.DefaultExpression is ClassExpression) &&
                    PeekTokenIs(TokenType.LParen) &&
                    !_peekToken.HadLineTerminatorBefore)
                {
                    _errors.Add("SyntaxError: Unexpected token '(' after default declaration");
                }
            }
            else if (CurTokenIs(TokenType.Var) || CurTokenIs(TokenType.Let) || CurTokenIs(TokenType.Const) || 
                     CurTokenIs(TokenType.Function) || CurTokenIs(TokenType.Class) || CurTokenIs(TokenType.Async))
            {
                stmt.Declaration = ParseStatement();
            }
            else if (CurTokenIs(TokenType.Asterisk) ||
                     (CurTokenIs(TokenType.String) && _curToken.Literal == "*"))
            {
                // export * from "module"
                // export * as ns from "module"
                // export * as "name" from "module"
                // export "*" as "name" from "module"
                var starToken = _curToken;
                NextToken(); // consume *
                 
                if (CurTokenIs(TokenType.As))
                {
                    NextToken(); // consume as
                    if (!IsIdentifierNameToken(_curToken.Type) && !CurTokenIs(TokenType.String))
                    {
                        _errors.Add($"Expected identifier name after 'export * as', got {_curToken.Type}");
                        return null;
                    }
                    ValidateModuleStringNameIfNeeded(_curToken, "export");
                    var ns = new Identifier(_curToken, _curToken.Literal);
                    
                    var spec = new ExportSpecifier 
                    { 
                        Local = new Identifier(starToken, "*"), 
                        Exported = ns // export * as ns -> module namespace object bound to ns
                    };
                    stmt.Specifiers.Add(spec);
                    NextToken(); // consume ns
                }
                else
                {
                    // export * from "module"
                    // In this case, we use null for Exported to signify aggregation
                    // But ExportSpecifier expects Identifier. Let's use * as well or null?
                    // Typically 'export * from "mod"' merges exports.
                    // For now, let's use Local="*" and Exported=null
                    var spec = new ExportSpecifier 
                    { 
                        Local = new Identifier(starToken, "*"), 
                        Exported = null 
                    };
                    stmt.Specifiers.Add(spec);
                }

                // After `export *` or `export * as ns`, current token should be `from`.
                if (!CurTokenIs(TokenType.From))
                {
                    if (!ExpectPeek(TokenType.From)) return null;
                }
                if (!ExpectPeek(TokenType.String)) return null;
                stmt.Source = _curToken.Literal;
                if (!ConsumeImportAttributesClauseIfPresent()) return null;
            }
            else if (CurTokenIs(TokenType.LBrace))
            {
                // export { x, y }
                // export { x as y }
                NextToken(); // consume {
                
                while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
                {
                    if (!CurTokenIs(TokenType.Identifier) &&
                        !IsKeywordToken(_curToken.Type) &&
                        !CurTokenIs(TokenType.String))
                    {
                         // Keywords allowed as export names
                         _errors.Add($"Expected identifier or keyword in export list, got {_curToken.Type}");
                         // recovery
                         while(!CurTokenIs(TokenType.Comma) && !CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof)) NextToken();
                    }
                    else
                    {
                        var localName = new Identifier(_curToken, _curToken.Literal);
                        ValidateModuleStringNameIfNeeded(_curToken, "export");
                        Identifier exportedName = localName;
                        
                        // Check if next is 'as'
                        if (PeekTokenIs(TokenType.As))
                        {
                            NextToken(); // consume local
                            NextToken(); // consume as
                            if (!CurTokenIs(TokenType.Identifier) &&
                                !IsKeywordToken(_curToken.Type) &&
                                !CurTokenIs(TokenType.String)) {
                                _errors.Add($"Expected identifier after 'as', got {_curToken.Type}");
                                return null;
                            }
                            ValidateModuleStringNameIfNeeded(_curToken, "export");
                            exportedName = new Identifier(_curToken, _curToken.Literal);
                        }
                        
                        stmt.Specifiers.Add(new ExportSpecifier { Local = localName, Exported = exportedName });
                        NextToken(); // consume name or alias
                    }

                    if (CurTokenIs(TokenType.Comma))
                    {
                        NextToken();
                        continue;
                    }
                }
                
                if (!CurTokenIs(TokenType.RBrace)) return null;
                NextToken(); // consume }

                // Check for 'from' clause: export { x } from "mod"
                if (CurTokenIs(TokenType.From))
                {
                    NextToken(); // consume from
                    if (!CurTokenIs(TokenType.String)) 
                    {
                         _errors.Add("Expected string literal after export ... from");
                         return null;
                    }
                    stmt.Source = _curToken.Literal;
                    NextToken(); // consume string
                    if (!ConsumeImportAttributesClauseIfPresent()) return null;
                }
            }

            // Targeted early error: `export ... null;` on the same line requires a terminator
            // between the export declaration and following expression.
            if (!requiresTerminator)
            {
                if ((CurTokenIs(TokenType.Null) && !_curToken.HadLineTerminatorBefore) ||
                    (PeekTokenIs(TokenType.Null) && !_peekToken.HadLineTerminatorBefore))
                {
                    _errors.Add("SyntaxError: Missing semicolon or line terminator after export declaration");
                }
            }

            if (requiresTerminator)
            {
                if (CurTokenIs(TokenType.Semicolon))
                {
                    // Keep current token at ';' so ParseProgram's outer NextToken()
                    // advances to the following statement exactly once.
                }
                else if (PeekTokenIs(TokenType.Semicolon))
                {
                    NextToken();
                }
                else if (!_peekToken.HadLineTerminatorBefore &&
                         !PeekTokenIs(TokenType.Eof) &&
                         !PeekTokenIs(TokenType.RBrace))
                {
                    _errors.Add("SyntaxError: Missing semicolon or line terminator after export declaration");
                }
            }
            return stmt;
        }

        private bool ConsumeImportAttributesClauseIfPresent()
        {
            if (!PeekTokenIs(TokenType.With))
            {
                return true;
            }

            NextToken(); // consume 'with'
            if (!ExpectPeek(TokenType.LBrace))
            {
                _errors.Add("SyntaxError: Expected '{' after import attributes 'with'");
                return false;
            }

            int depth = 1;
            var attributeKeys = new HashSet<string>(StringComparer.Ordinal);
            while (depth > 0 && !PeekTokenIs(TokenType.Eof))
            {
                NextToken();
                if (depth == 1 &&
                    (CurTokenIs(TokenType.Identifier) || IsKeywordToken(_curToken.Type) || CurTokenIs(TokenType.String)) &&
                    PeekTokenIs(TokenType.Colon))
                {
                    if (!attributeKeys.Add(_curToken.Literal))
                    {
                        _errors.Add($"SyntaxError: Duplicate import attribute key '{_curToken.Literal}'");
                    }
                }

                if (CurTokenIs(TokenType.LBrace)) depth++;
                else if (CurTokenIs(TokenType.RBrace)) depth--;
            }

            if (depth != 0)
            {
                _errors.Add("SyntaxError: Unterminated import attributes clause");
                return false;
            }

            return true;
        }

        private Statement ParseAsyncFunctionDeclaration()
        {
            // async function name() { ... } or async function* name() { ... }
            var token = _curToken;
            NextToken(); // Move past 'async' (cur is now 'function')

            var funcLit = ParseFunctionLiteral(forceAsync: true, allowBodyExpressionContinuation: false) as FunctionLiteral;
            if (funcLit != null && funcLit.Name != null)
            {
                // Async function declarations are hoisted like ordinary function
                // declarations. Represent them as FunctionDeclarationStatement so
                // the compiler's declaration hoisting path applies.
                return new FunctionDeclarationStatement
                {
                    Token = token,
                    Function = funcLit
                };
            }

            return null;
        }

        private Expression ParseAwaitExpression()
        {
            var expression = new AwaitExpression { Token = _curToken };
            if (_classStaticBlockDepth > 0 && _functionDepth == 0)
            {
                _errors.Add("SyntaxError: Unexpected identifier 'await' in class static block");
                return new Identifier(_curToken, _curToken.Literal);
            }

            if (_asyncFunctionDepth == 0 && !(_isModule && _functionDepth == 0 && !_inFormalParameters))
            {
                if (!_isModule)
                {
                    return new Identifier(_curToken, _curToken.Literal);
                }

                _errors.Add("SyntaxError: await is only valid in async functions");
            }
            NextToken(); // Move past 'await'
            expression.Argument = ParseExpression(Precedence.Prefix);
            if (expression.Argument == null || expression.Argument is EmptyExpression)
            {
                _errors.Add("SyntaxError: await expects an expression");
            }
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
                var funcLit = ParseFunctionLiteral(forceAsync: true) as FunctionLiteral;
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
                var afterIdentifier = PeekSecondToken();
                if (afterIdentifier.Type == TokenType.Arrow)
                {
                    // This is 'async arg => ...'
                    NextToken(); // consume identifier parameter
                    var arg = new Identifier(_curToken, _curToken.Literal);
                    ValidateBindingIdentifier(_curToken);

                    var arrow = new ArrowFunctionExpression 
                    { 
                        Token = token,
                        IsAsync = true,
                        Parameters = new List<Identifier> { arg }
                    };

                    AnalyzeParameterList(
                        arrow.Parameters,
                        out bool arrowParamsSimple,
                        out bool arrowParamsDuplicate,
                        out bool arrowTrailingCommaAfterRest);
                    
                    NextToken(); // Move to '=>'
                    NextToken(); // Move past '=>'
                     
                    // Parse body
                    _functionDepth++;
                    _asyncFunctionDepth++;
                    bool previousStrictMode = _isStrictMode;
                    bool inheritedStrictMode = _isModule || previousStrictMode;
                    try
                    {
                        if (inheritedStrictMode)
                        {
                            _isStrictMode = true;
                        }

                        _arrowFunctionDepth++;
                        if (CurTokenIs(TokenType.LBrace))
                        {
                            arrow.Body = ParseBlockStatement(
                                consumeTerminator: false,
                                enableDirectiveStrictMode: true);
                        }
                        else
                        {
                            // AssignmentExpression grammar allows assignment but excludes the comma operator.
                            arrow.Body = ParseExpression(Precedence.Comma);
                        }

                        ValidateArrowFunctionEarlyErrors(
                            arrow.Parameters,
                            arrow.Body,
                            arrowParamsSimple,
                            arrowParamsDuplicate,
                            arrowTrailingCommaAfterRest,
                            isAsyncArrow: true,
                            inheritedStrictMode);
                    }
                    finally
                    {
                        _isStrictMode = previousStrictMode;
                        _arrowFunctionDepth--;
                        _asyncFunctionDepth--;
                        _functionDepth--;
                    }
                    return arrow;
                }
            }

            // If not followed by function, treat 'async' as an identifier
            // This allows 'async' to be used as a variable name in expressions
            return new Identifier(token, "async");
        }

        private Token PeekSecondToken()
        {
            if (_lexer?.Source == null || _peekToken.Position < 0 || _peekToken.Position >= _lexer.Source.Length)
            {
                return new Token(TokenType.Eof, string.Empty, _peekToken.Line, _peekToken.Column);
            }

            var probe = new Lexer(_lexer.Source.Substring(_peekToken.Position))
            {
                TreatHtmlLikeCommentsAsComments = _lexer.TreatHtmlLikeCommentsAsComments
            };

            _ = probe.NextToken(); // first token corresponds to _peekToken
            return probe.NextToken();
        }

        /// <summary>
        /// Parse spread expression: ...expr
        /// Used in arrays, function calls, and object literals
        /// </summary>
        private Expression ParseSpreadExpression()
        {
            var token = _curToken;
            NextToken(); // Move past '...'
            
            var argument = ParseExpression(Precedence.Comma);
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
            _errors.Add("SyntaxError: Unexpected token 'default'");
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
            // Return as a special expression handled by runtime execution.
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

            bool bodyHasBraces = PeekTokenIs(TokenType.LBrace);

            // Handle body with or without braces
            stmt.Body = ParseBodyAsBlock();
            if (!bodyHasBraces && stmt.Body?.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(stmt.Body.Statements[0], "for-in statement");
            }

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
            NextToken(); // Move to first token inside the switch body.

            // Parse cases
            while (!CurTokenIs(TokenType.RBrace) && !CurTokenIs(TokenType.Eof))
            {
                if (!CurTokenIs(TokenType.Case) && !CurTokenIs(TokenType.Default))
                {
                    NextToken();
                    continue;
                }

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
                    while (!CurTokenIs(TokenType.Colon) &&
                           !CurTokenIs(TokenType.Case) &&
                           !CurTokenIs(TokenType.Default) &&
                           !CurTokenIs(TokenType.RBrace) &&
                           !CurTokenIs(TokenType.Eof))
                    {
                        NextToken();
                    }

                    if (CurTokenIs(TokenType.Case) || CurTokenIs(TokenType.Default))
                    {
                        continue;
                    }

                    if (CurTokenIs(TokenType.RBrace) || CurTokenIs(TokenType.Eof))
                    {
                        break;
                    }
                }

                NextToken(); // Move to first consequent statement token.

                // Parse statements until next case/default/}
                while (!CurTokenIs(TokenType.Case) &&
                       !CurTokenIs(TokenType.Default) &&
                       !CurTokenIs(TokenType.RBrace) &&
                       !CurTokenIs(TokenType.Eof))
                {
                    Statement s;
                    _moduleDeclarationNestingDepth++;
                    try
                    {
                        s = ParseStatement();
                    }
                    finally
                    {
                        _moduleDeclarationNestingDepth--;
                    }
                    if (s != null) switchCase.Consequent.Add(s);

                    if (CurTokenIs(TokenType.RBrace))
                    {
                        // Nested statements inside a case (for example
                        // `if (...) { ... }`) can leave us on their closing
                        // brace. Consume that inner brace and continue
                        // parsing the same case unless we actually surfaced
                        // back to the switch boundary or the next clause.
                        NextToken();
                        if (CurTokenIs(TokenType.Case) ||
                            CurTokenIs(TokenType.Default) ||
                            CurTokenIs(TokenType.RBrace) ||
                            CurTokenIs(TokenType.Eof))
                        {
                            break;
                        }

                        continue;
                    }

                    if (CurTokenIs(TokenType.Case) ||
                        CurTokenIs(TokenType.Default) ||
                        CurTokenIs(TokenType.Eof))
                    {
                        break;
                    }

                    NextToken();
                }

                stmt.Cases.Add(switchCase);
            }

            ValidateSwitchCaseBlockEarlyErrors(stmt);

            if (!CurTokenIs(TokenType.RBrace) && !ExpectPeek(TokenType.RBrace)) return null;
            return stmt;
        }

        private void ValidateSwitchCaseBlockEarlyErrors(SwitchStatement statement)
        {
            if (statement == null)
            {
                return;
            }

            var lexicalNames = new HashSet<string>(StringComparer.Ordinal);
            var varNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var switchCase in statement.Cases)
            {
                foreach (var consequent in switchCase.Consequent)
                {
                    CollectSwitchCaseDeclaredNames(consequent, lexicalNames, varNames);
                }
            }

            foreach (var lexicalName in lexicalNames)
            {
                if (varNames.Contains(lexicalName))
                {
                    _errors.Add($"SyntaxError: Duplicate declaration '{lexicalName}' in switch case block");
                }
            }
        }

        private void CollectSwitchCaseDeclaredNames(Statement statement, HashSet<string> lexicalNames, HashSet<string> varNames)
        {
            if (statement == null)
            {
                return;
            }

            switch (statement)
            {
                case LabeledStatement labeled:
                    CollectSwitchCaseDeclaredNames(labeled.Body, lexicalNames, varNames);
                    return;

                case LetStatement letStatement:
                    var targetSet = letStatement.Kind == DeclarationKind.Var ? varNames : lexicalNames;
                    if (letStatement.Name != null)
                    {
                        AddSwitchDeclaredName(letStatement.Name.Value, targetSet);
                    }

                    if (letStatement.DestructuringPattern != null)
                    {
                        foreach (var name in ExtractBindingNames(letStatement.DestructuringPattern))
                        {
                            AddSwitchDeclaredName(name, targetSet);
                        }
                    }
                    return;

                case FunctionDeclarationStatement functionDeclaration:
                    if (functionDeclaration.Function != null &&
                        !string.IsNullOrEmpty(functionDeclaration.Function.Name))
                    {
                        if (_isStrictMode)
                        {
                            AddSwitchDeclaredName(functionDeclaration.Function.Name, lexicalNames);
                        }
                        else
                        {
                            // Annex B B.3.3.5: In non-strict mode, duplicate function declarations
                            // in switch case blocks are allowed.
                            lexicalNames.Add(functionDeclaration.Function.Name);
                        }
                    }
                    return;

                case ClassStatement classStatement:
                    if (classStatement.Name != null)
                    {
                        AddSwitchDeclaredName(classStatement.Name.Value, lexicalNames);
                    }
                    return;
            }
        }

        private void AddSwitchDeclaredName(string name, HashSet<string> targetSet)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (!targetSet.Add(name))
            {
                _errors.Add($"SyntaxError: Duplicate declaration '{name}' in switch case block");
            }
        }

        private void ValidateLoopBodyVarRedeclarations(IEnumerable<string> boundNames, Statement body, string context)
        {
            if (boundNames == null || body == null)
            {
                return;
            }

            var varNames = new HashSet<string>(StringComparer.Ordinal);
            CollectVarDeclaredNames(body, varNames);

            foreach (var boundName in boundNames)
            {
                if (!string.IsNullOrEmpty(boundName) && varNames.Contains(boundName))
                {
                    _errors.Add($"SyntaxError: Body may not re-declare loop binding '{boundName}' in {context}");
                }
            }
        }

        private void CollectVarDeclaredNames(Statement statement, HashSet<string> names)
        {
            if (statement == null)
            {
                return;
            }

            switch (statement)
            {
                case BlockStatement blockStatement:
                    foreach (var child in blockStatement.Statements)
                    {
                        CollectVarDeclaredNames(child, names);
                    }
                    return;

                case LabeledStatement labeledStatement:
                    CollectVarDeclaredNames(labeledStatement.Body, names);
                    return;

                case LetStatement letStatement:
                    if (letStatement.Kind == DeclarationKind.Var)
                    {
                        if (letStatement.Name != null)
                        {
                            names.Add(letStatement.Name.Value);
                        }

                        if (letStatement.DestructuringPattern != null)
                        {
                            foreach (var name in ExtractBindingNames(letStatement.DestructuringPattern))
                            {
                                names.Add(name);
                            }
                        }
                    }
                    return;

                case FunctionDeclarationStatement functionDeclaration:
                    if (!AllowsAnnexBBlockFunctions() &&
                        functionDeclaration.Function != null &&
                        !string.IsNullOrEmpty(functionDeclaration.Function.Name))
                    {
                        names.Add(functionDeclaration.Function.Name);
                    }
                    return;

                case IfStatement ifStatement:
                    CollectVarDeclaredNames(ifStatement.Consequence, names);
                    CollectVarDeclaredNames(ifStatement.Alternative, names);
                    return;

                case WhileStatement whileStatement:
                    CollectVarDeclaredNames(whileStatement.Body, names);
                    return;

                case DoWhileStatement doWhileStatement:
                    CollectVarDeclaredNames(doWhileStatement.Body, names);
                    return;

                case ForStatement forStatement:
                    if (forStatement.Init is LetStatement initDeclaration && initDeclaration.Kind == DeclarationKind.Var)
                    {
                        CollectVarDeclaredNames(initDeclaration, names);
                    }

                    CollectVarDeclaredNames(forStatement.Body, names);
                    return;

                case TryStatement tryStatement:
                    CollectVarDeclaredNames(tryStatement.Block, names);
                    CollectVarDeclaredNames(tryStatement.CatchBlock, names);
                    CollectVarDeclaredNames(tryStatement.FinallyBlock, names);
                    return;

                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var consequent in switchCase.Consequent)
                        {
                            CollectVarDeclaredNames(consequent, names);
                        }
                    }
                    return;

                case WithStatement withStatement:
                    CollectVarDeclaredNames(withStatement.Body, names);
                    return;
            }
        }

        private IEnumerable<string> ExtractBindingNames(Expression pattern)
        {
            var names = new List<string>();
            CollectBindingNamesRecursive(pattern, names);
            return names;
        }

        private void CollectBindingNamesRecursive(Expression node, List<string> names)
        {
            if (node == null)
            {
                return;
            }

            switch (node)
            {
                case Identifier identifier:
                    names.Add(identifier.Value);
                    return;

                case AssignmentExpression assignmentExpression:
                    CollectBindingNamesRecursive(assignmentExpression.Left, names);
                    return;

                case SpreadElement spreadElement:
                    CollectBindingNamesRecursive(spreadElement.Argument, names);
                    return;

                case ArrayLiteral arrayLiteral:
                    foreach (var element in arrayLiteral.Elements)
                    {
                        CollectBindingNamesRecursive(element, names);
                    }
                    return;

                case ObjectLiteral objectLiteral:
                    foreach (var pair in objectLiteral.Pairs)
                    {
                        CollectBindingNamesRecursive(pair.Value, names);
                    }
                    return;
            }
        }

        // Parse break statement
        private Statement ParseBreakStatement()
        {
            var stmt = new BreakStatement { Token = _curToken };
            
            // Check for optional label
            if (PeekTokenIs(TokenType.Identifier) && !_peekToken.HadLineTerminatorBefore)
            {
                NextToken();
                stmt.Label = new Identifier(_curToken, _curToken.Literal);
            }
            else if (!PeekTokenIs(TokenType.Semicolon) &&
                     !_peekToken.HadLineTerminatorBefore &&
                     !PeekTokenIs(TokenType.RBrace) &&
                     !PeekTokenIs(TokenType.Eof))
            {
                _errors.Add("SyntaxError: Illegal token after break");
            }
            
            if (PeekTokenIs(TokenType.Semicolon)) NextToken();
            return stmt;
        }

        // Parse continue statement
        private Statement ParseContinueStatement()
        {
            var stmt = new ContinueStatement { Token = _curToken };
            
            // Check for optional label
            if (PeekTokenIs(TokenType.Identifier) && !_peekToken.HadLineTerminatorBefore)
            {
                NextToken();
                stmt.Label = new Identifier(_curToken, _curToken.Literal);
            }
            else if (!PeekTokenIs(TokenType.Semicolon) &&
                     !_peekToken.HadLineTerminatorBefore &&
                     !PeekTokenIs(TokenType.RBrace) &&
                     !PeekTokenIs(TokenType.Eof))
            {
                _errors.Add("SyntaxError: Illegal token after continue");
            }
            
            if (PeekTokenIs(TokenType.Semicolon)) NextToken();
            return stmt;
        }

        // Parse do-while: do { } while (condition);
        private Statement ParseDoWhileStatement()
        {
            var stmt = new DoWhileStatement { Token = _curToken };

            bool bodyHasBraces = PeekTokenIs(TokenType.LBrace);

            // Parse body
            stmt.Body = ParseBodyAsBlock();
            if (stmt.Body  == null) return null;
            if (!bodyHasBraces && stmt.Body.Statements.Count > 0)
            {
                ReportInvalidSingleStatementBody(stmt.Body.Statements[0], "do-while statement");
            }

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
            if (_curToken.Type == TokenType.Assign)
            {
                _errors.Add("SyntaxError: Unexpected token '='");
            }
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

                // Convert call arguments to parameters using the same extraction rules as normal arrows.
                foreach (var arg in callExp.Arguments)
                {
                    ExtractSingleParam(arg, arrow.Parameters);
                }
            }
            else
            {
                // Extract parameters from left side (grouped expression or identifier)
                arrow.Parameters = ExtractArrowParameters(left);
            }

            AnalyzeParameterList(
                arrow.Parameters,
                out bool arrowParamsSimple,
                out bool arrowParamsDuplicate,
                out bool arrowTrailingCommaAfterRest);

            // Console.WriteLine($"[DEBUG] ParseArrow START: Cur={_curToken.Type}");
            NextToken(); // Move past '=>'

            // Parse body: either block statement or expression
            _functionDepth++;
            if (arrow.IsAsync) _asyncFunctionDepth++;
            bool previousStrictMode = _isStrictMode;
            bool inheritedStrictMode = _isModule || previousStrictMode;
            try
            {
                if (inheritedStrictMode)
                {
                    _isStrictMode = true;
                }

                _arrowFunctionDepth++;
                if (CurTokenIs(TokenType.LBrace))
                {
                    arrow.Body = ParseBlockStatement(
                        consumeTerminator: false,
                        enableDirectiveStrictMode: true);
                }
                else
                {
                    // AssignmentExpression grammar allows assignment but excludes the comma operator.
                    arrow.Body = ParseExpression(Precedence.Comma);
                }

                ValidateArrowFunctionEarlyErrors(
                    arrow.Parameters,
                    arrow.Body,
                    arrowParamsSimple,
                    arrowParamsDuplicate,
                    arrowTrailingCommaAfterRest,
                    arrow.IsAsync,
                    inheritedStrictMode);
            }
            finally
            {
                _isStrictMode = previousStrictMode;
                _arrowFunctionDepth--;
                if (arrow.IsAsync) _asyncFunctionDepth--;
                _functionDepth--;
            }
            // Console.WriteLine($"[DEBUG] ParseArrow Exit: Cur={_curToken.Type}, Peek={_peekToken.Type}");

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
                ValidateBindingIdentifier(id.Token);
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

            if (expr is AssignmentExpression destructuringAssign &&
                (destructuringAssign.Left is ObjectLiteral || destructuringAssign.Left is ArrayLiteral))
            {
                ValidateBindingPattern(destructuringAssign.Left);
                var placeholder = new Identifier(
                    destructuringAssign.Left.Token,
                    destructuringAssign.Left is ObjectLiteral
                        ? $"__destructure_{parameters.Count}"
                        : $"__array_destructure_{parameters.Count}")
                {
                    DestructuringPattern = destructuringAssign.Left,
                    DefaultValue = destructuringAssign.Right
                };
                parameters.Add(placeholder);
                return;
            }

            // Object destructuring: ({a, b}) - create placeholder parameter
            if (expr is ObjectLiteral objLit)
            {
                ValidateBindingPattern(objLit);
                var placeholder = new Identifier(objLit.Token, $"__destructure_{parameters.Count}")
                {
                    DestructuringPattern = objLit
                };
                parameters.Add(placeholder);
                return;
            }

            // Array destructuring: ([a, b]) - create placeholder parameter
            if (expr is ArrayLiteral arrLit)
            {
                ValidateBindingPattern(arrLit);
                var placeholder = new Identifier(arrLit.Token, $"__array_destructure_{parameters.Count}")
                {
                    DestructuringPattern = arrLit
                };
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

            if (expr is SpreadElement spreadWithDefault && spreadWithDefault.Argument is AssignmentExpression spreadAssignment)
            {
                _errors.Add("SyntaxError: Rest parameter cannot have a default initializer");

                if (spreadAssignment.Left is Identifier spreadAssignmentId)
                {
                    spreadAssignmentId.IsRest = true;
                    parameters.Add(spreadAssignmentId);
                    return;
                }

                if (spreadAssignment.Left is ObjectLiteral || spreadAssignment.Left is ArrayLiteral)
                {
                    ValidateBindingPattern(spreadAssignment.Left);
                    var placeholder = new Identifier(spreadAssignment.Left.Token, $"__rest_{parameters.Count}")
                    {
                        IsRest = true,
                        DestructuringPattern = spreadAssignment.Left
                    };
                    parameters.Add(placeholder);
                    return;
                }
            }

            if (expr is SpreadElement destructuringSpread &&
                (destructuringSpread.Argument is ObjectLiteral || destructuringSpread.Argument is ArrayLiteral))
            {
                ValidateBindingPattern(destructuringSpread.Argument);
                var placeholder = new Identifier(
                    destructuringSpread.Argument.Token,
                    $"__rest_{parameters.Count}")
                {
                    IsRest = true,
                    DestructuringPattern = destructuringSpread.Argument
                };
                parameters.Add(placeholder);
                return;
            }

            // For unrecognized patterns, create a placeholder to allow parsing to continue
            if (expr is Expression)
            {
                if (!_allowRecovery)
                {
                    _errors.Add($"SyntaxError: Invalid parameter pattern '{expr}'");
                    return;
                }

                var placeholder = new Identifier(expr.Token, $"__unknown_{parameters.Count}");
                parameters.Add(placeholder);
            }
        }


        // Parse compound assignment: x += 1
        private Expression ParseCompoundAssignment(Expression left)
        {
            var token = _curToken;
            var op = token.Literal;  // +=, -=, etc.

            if (!IsValidAssignmentTarget(left, allowDestructuring: false))
            {
                var msg = $"Invalid left-hand side in compound assignment: {left?.GetType().Name}";
                if (_lexer != null)
                {
                    msg += $"\nContext:\n{_lexer.GetCodeContext(_curToken.Line, _curToken.Column)}";
                }
                _errors.Add(msg);
                return null;
            }

            var precedence = CurPrecedence();
            NextToken();
            // Compound assignment follows AssignmentExpression grammar on the RHS,
            // so nested forms like `a |= b &= c` must parse right-associatively.
            var right = ParseExpression(Precedence.Comma);

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
            if (!IsValidUpdateTarget(operand))
            {
                _errors.Add($"Invalid left-hand side in prefix operation: {operand?.GetType().Name}");
            }
            return new PrefixExpression { Token = token, Operator = "++", Right = operand };
        }

        // Parse prefix decrement: --x
        private Expression ParsePrefixDecrement()
        {
            var token = _curToken;
            NextToken();
            var operand = ParseExpression(Precedence.Prefix);
            if (!IsValidUpdateTarget(operand))
            {
                _errors.Add($"Invalid left-hand side in prefix operation: {operand?.GetType().Name}");
            }
            return new PrefixExpression { Token = token, Operator = "--", Right = operand };
        }

        // Parse postfix increment: x++
        private Expression ParsePostfixIncrement(Expression left)
        {
            if (!IsValidUpdateTarget(left))
            {
                var msg = $"Invalid left-hand side in postfix operation: {left?.GetType().Name}";
                if (_lexer != null)
                {
                    msg += $"\nContext:\n{_lexer.GetCodeContext(_curToken.Line, _curToken.Column)}";
                }
                _errors.Add(msg);
            }
            return new InfixExpression { Token = _curToken, Left = left, Operator = "++", Right = null };
        }

        // Parse postfix decrement: x--
        private Expression ParsePostfixDecrement(Expression left)
        {
            if (!IsValidUpdateTarget(left))
            {
                var msg = $"Invalid left-hand side in postfix operation: {left?.GetType().Name}";
                if (_lexer != null)
                {
                    msg += $"\nContext:\n{_lexer.GetCodeContext(_curToken.Line, _curToken.Column)}";
                }
                _errors.Add(msg);
            }
            return new InfixExpression { Token = _curToken, Left = left, Operator = "--", Right = null };
        }

        // Parse regex literal when starting with /
        // Called when a Slash token appears in prefix position — the lexer didn't recognize it
        // as regex (context-dependent), so we re-scan from the slash position.
        private Expression ParseRegexLiteral()
        {
            var slashToken = _curToken;

            // Re-scan the source from the slash position as a regex literal
            var regexToken = _lexer.RescanSlashAsRegex(slashToken);

            // Parse the /pattern/flags from the token literal
            var literal = regexToken.Literal;
            var pattern = "";
            var flags = "";

            if (literal.StartsWith("/") && literal.Length > 1)
            {
                var lastSlash = literal.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    pattern = literal.Substring(1, lastSlash - 1);
                    flags = literal.Substring(lastSlash + 1);
                }
            }

            if (regexToken.Type == TokenType.Illegal)
            {
                throw new Errors.FenSyntaxError($"SyntaxError: Invalid regular expression: /{pattern}/{flags}");
            }

            // Advance the parser's token stream past the regex
            // The lexer is now positioned after the regex, so the next NextToken() will pick up correctly
            _peekToken = _lexer.NextToken();

            return new RegexLiteral { Token = slashToken, Pattern = pattern, Flags = flags };
        }

        // Parse already-lexed regex token
        private Expression ParseRegexToken()
        {
            // Console.WriteLine($"[Parser] ENTERING ParseRegexToken. Literal: {_curToken.Literal}");
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
            
            if (!IsValidAssignmentTarget(left, allowDestructuring: false))
            {
                _errors.Add($"Invalid left-hand side in logical assignment: {left?.GetType().Name}");
                return null;
            }

            NextToken();
            var right = ParseExpression(Precedence.Comma);
            
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

        private bool IsSyntheticParameterName(string name)
        {
            return name != null &&
                   (name.StartsWith("__rest_", StringComparison.Ordinal) ||
                    name.StartsWith("__destructure_", StringComparison.Ordinal) ||
                    name.StartsWith("__array_destructure_", StringComparison.Ordinal) ||
                    name.StartsWith("__unknown_", StringComparison.Ordinal));
        }

        private bool ContainsUseStrictDirective(BlockStatement body)
        {
            if (body?.Statements == null || body.Statements.Count == 0)
            {
                return false;
            }

            foreach (var stmt in body.Statements)
            {
                if (stmt is ExpressionStatement es && es.Expression is StringLiteral sl)
                {
                    if (sl.Value == "use strict")
                    {
                        return true;
                    }

                    // Directive prologue continues while statements are string literals.
                    continue;
                }

                break;
            }

            return false;
        }

        private void AnalyzeParameterList(
            List<Identifier> parameters,
            out bool isSimpleParameterList,
            out bool hasDuplicateParameterNames,
            out bool hadTrailingCommaAfterRest)
        {
            isSimpleParameterList = true;
            hasDuplicateParameterNames = false;
            hadTrailingCommaAfterRest = false;

            if (parameters == null || parameters.Count == 0)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                if (parameter == null)
                {
                    continue;
                }

                if (parameter.IsRest || parameter.DefaultValue != null || parameter.DestructuringPattern != null)
                {
                    isSimpleParameterList = false;
                }

                if (parameter.IsRest && i != parameters.Count - 1)
                {
                    hadTrailingCommaAfterRest = true;
                }

                if (!IsSyntheticParameterName(parameter.Value) && !seen.Add(parameter.Value))
                {
                    hasDuplicateParameterNames = true;
                }
            }
        }

        private bool BodyContainsWithStatement(BlockStatement body)
        {
            if (body == null)
            {
                return false;
            }

            return AstContains(body, ast => ast is WithStatement, skipNestedFunctions: true);
        }

        private bool BodyContainsLegacyOctalLiteral(BlockStatement body)
        {
            if (body == null)
            {
                return false;
            }

            return AstContains(body, HasLegacyOctalLiteral, skipNestedFunctions: true);
        }

        private bool ProgramContainsLegacyOctalLiteral(Program program)
        {
            if (program == null)
            {
                return false;
            }

            return AstContains(program, HasLegacyOctalLiteral, skipNestedFunctions: true);
        }

        private bool HasLegacyOctalLiteral(AstNode ast)
        {
            if (ast == null)
            {
                return false;
            }

            return ast switch
            {
                IntegerLiteral intLiteral => IsLegacyOctalTokenLiteral(intLiteral.Token?.Literal),
                DoubleLiteral doubleLiteral => IsLegacyOctalTokenLiteral(doubleLiteral.Token?.Literal),
                StringLiteral stringLiteral => stringLiteral.Token?.HasLegacyOctalEscape == true,
                _ => false,
            };
        }

        private bool IsLegacyOctalTokenLiteral(string literal)
        {
            if (string.IsNullOrEmpty(literal) || literal.Length <= 1 || literal[0] != '0')
            {
                return false;
            }

            char c = literal[1];
            return c != 'x' && c != 'X' &&
                   c != 'o' && c != 'O' &&
                   c != 'b' && c != 'B' &&
                   c != '.' && c != 'e' && c != 'E';
        }

        private bool ParametersContainIdentifier(List<Identifier> parameters, string identifier)
        {
            if (parameters == null || string.IsNullOrEmpty(identifier))
            {
                return false;
            }

            foreach (var parameter in parameters)
            {
                if (!IsSyntheticParameterName(parameter.Value) &&
                    string.Equals(parameter.Value, identifier, StringComparison.Ordinal))
                {
                    return true;
                }

                if (AstContains(parameter.DefaultValue, ast => ast is Identifier id &&
                    string.Equals(id.Value, identifier, StringComparison.Ordinal)))
                {
                    return true;
                }

                if (AstContains(parameter.DestructuringPattern, ast => ast is Identifier id &&
                    string.Equals(id.Value, identifier, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        private void ValidateMethodParameterEarlyErrors(
            List<Identifier> parameters,
            BlockStatement body,
            bool isSimpleParameterList,
            bool hasDuplicateParameterNames,
            bool hadTrailingCommaAfterRest,
            bool isAsyncMethod)
        {
            if (hadTrailingCommaAfterRest)
            {
                _errors.Add("SyntaxError: Rest parameter must be last formal parameter");
            }

            // Class methods/accessors use UniqueFormalParameters.
            if (hasDuplicateParameterNames)
            {
                _errors.Add("SyntaxError: Duplicate parameter name not allowed in this context");
            }

            if (ContainsUseStrictDirective(body) && !isSimpleParameterList)
            {
                _errors.Add("SyntaxError: 'use strict' directive is invalid with non-simple parameter list");
            }

            if (BodyHasLexicalParameterNameCollision(body, parameters))
            {
                _errors.Add("SyntaxError: Formal parameter name conflicts with a lexical declaration in function body");
            }

            // Method definitions are always strict mode code.
            if (BodyContainsWithStatement(body))
            {
                _errors.Add("SyntaxError: Strict mode code may not include a with statement");
            }

            if (BodyContainsLegacyOctalLiteral(body))
            {
                _errors.Add("SyntaxError: Legacy octal literals are not allowed in strict mode");
            }

            if (ParametersContainIdentifier(parameters, "yield"))
            {
                _errors.Add("SyntaxError: Unexpected identifier 'yield' in method parameters");
            }

            if (isAsyncMethod && ParametersContainIdentifier(parameters, "await"))
            {
                _errors.Add("SyntaxError: Unexpected identifier 'await' in async method parameters");
            }
        }

        private void ValidateArrowFunctionEarlyErrors(
            List<Identifier> parameters,
            AstNode body,
            bool isSimpleParameterList,
            bool hasDuplicateParameterNames,
            bool hadTrailingCommaAfterRest,
            bool isAsyncArrow,
            bool inheritedStrictMode)
        {
            if (hadTrailingCommaAfterRest)
            {
                _errors.Add("SyntaxError: Rest parameter must be last formal parameter");
            }

            var blockBody = body as BlockStatement;
            bool hasUseStrictDirective = blockBody != null && ContainsUseStrictDirective(blockBody);
            bool isStrict = inheritedStrictMode || hasUseStrictDirective;

            if (hasDuplicateParameterNames)
            {
                _errors.Add("SyntaxError: Duplicate parameter name not allowed in this context");
            }

            if (hasUseStrictDirective && !isSimpleParameterList)
            {
                _errors.Add("SyntaxError: 'use strict' directive is invalid with non-simple parameter list");
            }

            if (blockBody != null && BodyHasLexicalParameterNameCollision(blockBody, parameters))
            {
                _errors.Add("SyntaxError: Formal parameter name conflicts with a lexical declaration in function body");
            }

            if (isStrict && blockBody != null)
            {
                if (BodyContainsWithStatement(blockBody))
                {
                    _errors.Add("SyntaxError: Strict mode code may not include a with statement");
                }

                if (BodyContainsLegacyOctalLiteral(blockBody))
                {
                    _errors.Add("SyntaxError: Legacy octal literals are not allowed in strict mode");
                }
            }

            if (isAsyncArrow && ParametersContainIdentifier(parameters, "await"))
            {
                _errors.Add("SyntaxError: Unexpected identifier 'await' in async function parameters");
            }
        }

        private void AddPrivateBoundName(Dictionary<string, List<string>> privateKinds, string name, string kind)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (!privateKinds.TryGetValue(name, out var kinds))
            {
                kinds = new List<string>();
                privateKinds[name] = kinds;
            }

            kinds.Add(kind);
        }

        private void RegisterPrivateNameInCurrentScope(string privateName)
        {
            if (string.IsNullOrEmpty(privateName))
            {
                return;
            }

            if (_privateNameScopeStack.Count == 0)
            {
                return;
            }

            _privateNameScopeStack.Peek().Add(privateName);
        }

        private bool ContainsDirectSuperCall(BlockStatement body)
        {
            return AstContains(body, ast =>
            {
                if (ast is CallExpression call && call.Function is Identifier id)
                {
                    return string.Equals(id.Value, "super", StringComparison.Ordinal);
                }

                return false;
            }, skipNestedFunctions: true);
        }

        private bool ContainsPrivateReference(Expression expression)
        {
            return AstContains(expression, ast =>
            {
                if (ast is PrivateIdentifier)
                {
                    return true;
                }

                if (ast is MemberExpression member &&
                    !string.IsNullOrEmpty(member.Property) &&
                    member.Property.StartsWith("#", StringComparison.Ordinal))
                {
                    return true;
                }

                return false;
            });
        }

        private void ValidatePrivateNameReferences(AstNode root, HashSet<string> declaredPrivateNames)
        {
            if (root == null)
            {
                return;
            }

            VisitAstNodes(root, ast =>
            {
                if (ast is MemberExpression member &&
                    !string.IsNullOrEmpty(member.Property) &&
                    member.Property.StartsWith("#", StringComparison.Ordinal))
                {
                    string privateName = member.Property.Substring(1);

                    if (member.Object is Identifier objIdent &&
                        string.Equals(objIdent.Value, "super", StringComparison.Ordinal))
                    {
                        _errors.Add("SyntaxError: Private fields cannot be accessed on super");
                    }

                    if (!declaredPrivateNames.Contains(privateName))
                    {
                        _errors.Add($"SyntaxError: Private field '#{privateName}' must be declared in an enclosing class");
                    }
                }
                else if (ast is PrivateIdentifier privateIdentifier)
                {
                    if (!declaredPrivateNames.Contains(privateIdentifier.Name))
                    {
                        _errors.Add($"SyntaxError: Private field '#{privateIdentifier.Name}' must be declared in an enclosing class");
                    }
                }
            }, skipNestedFunctions: false);
        }

        private void ValidateClassEarlyErrors(List<MethodDefinition> methods, List<ClassProperty> properties, bool hasHeritage)
        {
            int constructorCount = 0;
            var declaredPrivateNames = _privateNameScopeStack.Count > 0
                ? new HashSet<string>(_privateNameScopeStack.Peek(), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var privateNameKinds = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var prop in properties)
            {
                if (!prop.IsPrivate) continue;
                string privateName = prop.Key?.Value ?? string.Empty;
                if (privateName == "constructor")
                {
                    _errors.Add("SyntaxError: Private fields cannot be named '#constructor'");
                }

                declaredPrivateNames.Add(privateName);
                AddPrivateBoundName(privateNameKinds, privateName, "field");
            }

            foreach (var method in methods)
            {
                string methodName = method.Key?.Value ?? string.Empty;

                if (method.Kind == "constructor" && !method.Static && !method.IsPrivate)
                {
                    constructorCount++;
                }

                if (method.IsPrivate)
                {
                    if (methodName == "constructor")
                    {
                        _errors.Add("SyntaxError: Private fields cannot be named '#constructor'");
                    }

                    declaredPrivateNames.Add(methodName);
                    AddPrivateBoundName(privateNameKinds, methodName, method.Kind ?? "method");
                }
            }

            if (constructorCount > 1)
            {
                _errors.Add("SyntaxError: Class constructor may only be declared once");
            }

            foreach (var entry in privateNameKinds)
            {
                var kinds = entry.Value;
                bool getterSetterPair = kinds.Count == 2 &&
                                        kinds.Contains("get") &&
                                        kinds.Contains("set");
                if (!getterSetterPair && kinds.Count > 1)
                {
                    _errors.Add($"SyntaxError: Duplicate private name '#{entry.Key}'");
                }
            }

            foreach (var method in methods)
            {
                string methodName = method.Key?.Value ?? string.Empty;

                if (!method.IsPrivate &&
                    !method.Computed &&
                    !method.Static &&
                    methodName == "constructor" &&
                    method.Kind != "constructor")
                {
                    _errors.Add("SyntaxError: Invalid constructor method definition");
                }

                if (!method.IsPrivate &&
                    !method.Computed &&
                    method.Static &&
                    methodName == "prototype")
                {
                    _errors.Add("SyntaxError: Static class methods cannot be named 'prototype'");
                }

                bool hasDirectSuperCall = ContainsDirectSuperCall(method.Value?.Body);
                if (hasDirectSuperCall && method.Kind != "constructor")
                {
                    _errors.Add("SyntaxError: 'super()' is only valid in class constructors");
                }

                if (hasDirectSuperCall && method.Kind == "constructor" && !hasHeritage)
                {
                    _errors.Add("SyntaxError: 'super()' is invalid in constructors without heritage");
                }

                ValidatePrivateNameReferences(method.Value?.Body, declaredPrivateNames);
            }

            foreach (var prop in properties)
            {
                if (!prop.IsPrivate && prop.ComputedKeyExpression == null)
                {
                    string propName = GetPublicClassElementName(prop);
                    if (propName == "constructor")
                    {
                        _errors.Add("SyntaxError: Class fields cannot be named 'constructor'");
                    }

                    if (prop.Static && (propName == "prototype" || propName == "constructor"))
                    {
                        _errors.Add($"SyntaxError: Static class fields cannot be named '{propName}'");
                    }
                }

                bool initializerContainsSuperCall = AstContains(prop.Value, ast =>
                {
                    if (ast is CallExpression call && call.Function is Identifier id)
                    {
                        return string.Equals(id.Value, "super", StringComparison.Ordinal);
                    }
                    return false;
                });
                if (initializerContainsSuperCall)
                {
                    _errors.Add("SyntaxError: 'super()' is not valid in class field initializers");
                }

                bool initializerContainsArguments = AstContains(prop.Value, ast =>
                    ast is Identifier id && string.Equals(id.Value, "arguments", StringComparison.Ordinal));
                if (initializerContainsArguments)
                {
                    _errors.Add("SyntaxError: 'arguments' is not valid in class field initializers");
                }

                ValidatePrivateNameReferences(prop.Value, declaredPrivateNames);
            }
        }

        private static string GetPublicClassElementName(ClassProperty property)
        {
            var name = property?.Key?.Value ?? string.Empty;
            if (name.Length >= 2 &&
                ((name[0] == '"' && name[^1] == '"') || (name[0] == '\'' && name[^1] == '\'')))
            {
                return name.Substring(1, name.Length - 2);
            }

            return name;
        }

        private bool AstContains(object node, Func<AstNode, bool> predicate, bool skipNestedFunctions = false)
        {
            if (node == null)
            {
                return false;
            }

            if (node is AstNode astNode)
            {
                if (predicate(astNode))
                {
                    return true;
                }

                if (skipNestedFunctions &&
                    (astNode is FunctionLiteral || astNode is ClassExpression || astNode is ClassStatement))
                {
                    return false;
                }

                foreach (var property in astNode.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 ||
                        property.Name == "Token")
                    {
                        continue;
                    }

                    if (AstContains(property.GetValue(astNode), predicate, skipNestedFunctions))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (node is IEnumerable enumerable && node is not string)
            {
                foreach (var item in enumerable)
                {
                    if (AstContains(item, predicate, skipNestedFunctions))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void VisitAstNodes(object node, Action<AstNode> visitor, bool skipNestedFunctions)
        {
            if (node == null)
            {
                return;
            }

            if (node is AstNode astNode)
            {
                visitor(astNode);

                if (skipNestedFunctions &&
                    (astNode is FunctionLiteral || astNode is ClassExpression || astNode is ClassStatement))
                {
                    return;
                }

                foreach (var property in astNode.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 ||
                        property.Name == "Token")
                    {
                        continue;
                    }

                    VisitAstNodes(property.GetValue(astNode), visitor, skipNestedFunctions);
                }

                return;
            }

            if (node is IEnumerable enumerable && node is not string)
            {
                foreach (var item in enumerable)
                {
                    VisitAstNodes(item, visitor, skipNestedFunctions);
                }
            }
        }
         
        private bool IsValidAssignmentTarget(Expression target, bool allowDestructuring)
        {
            if (target is Identifier identifier) return IsAssignableIdentifier(identifier);
            if (target is MemberExpression) return true;
            if (target is IndexExpression) return true;
            if (target is CallExpression) return true; // Allowed syntactically; throws ReferenceError at runtime

            if (allowDestructuring)
            {
                if (target is ArrayLiteral) return true;
                if (target is ObjectLiteral) return true;
            }

            return false;
        }

        private bool IsValidUpdateTarget(Expression target)
        {
            if (target is Identifier identifier) return IsAssignableIdentifier(identifier);
            return target is MemberExpression || target is IndexExpression;
        }

        private bool IsValidForInOfTarget(Expression target)
        {
            if (target is Identifier identifier) return IsAssignableIdentifier(identifier);

            return target is MemberExpression ||
                   target is IndexExpression ||
                   target is ArrayLiteral ||
                   target is ObjectLiteral;
        }

        private static bool IsAssignableIdentifier(Identifier identifier)
        {
            if (identifier?.Token == null)
            {
                return false;
            }

            return identifier.Token.Type != TokenType.This &&
                   identifier.Token.Type != TokenType.Super;
        }

        private bool IsReservedIdentifierReference(Token token)
        {
            if (token == null || string.IsNullOrEmpty(token.Literal))
            {
                return false;
            }

            if (token.Literal == "true" || token.Literal == "false" || token.Literal == "null")
            {
                return true;
            }

            var type = Lexer.LookupIdent(token.Literal);
            if (type != TokenType.Identifier)
            {
                if (type == TokenType.Yield)
                {
                    return _generatorFunctionDepth > 0;
                }

                if (type == TokenType.Await)
                {
                    return _isModule || _asyncFunctionDepth > 0;
                }

                // Contextual identifiers permitted in non-keyword positions.
                if (type == TokenType.Async || type == TokenType.Let || type == TokenType.Of ||
                    type == TokenType.From || type == TokenType.As || type == TokenType.Static)
                {
                    return false;
                }

                return true;
            }

            // Escaped identifiers can still denote reserved words by StringValue.
            if (token.Type == TokenType.Identifier)
            {
                var escapedLookup = Lexer.LookupIdent(token.Literal);
                if (escapedLookup == TokenType.True || escapedLookup == TokenType.False || escapedLookup == TokenType.Null)
                {
                    return true;
                }
            }

            return false;
        }

        private bool BodyHasLexicalParameterNameCollision(BlockStatement body, List<Identifier> parameters)
        {
            if (body == null || parameters == null || parameters.Count == 0)
            {
                return false;
            }

            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                if (parameter == null || IsSyntheticParameterName(parameter.Value))
                {
                    continue;
                }

                parameterNames.Add(parameter.Value);
            }

            if (parameterNames.Count == 0)
            {
                return false;
            }

            // Only check top-level LexicalDeclarations in the body.
            // Nested scopes (blocks, functions, etc.) shadow rather than collide.
            foreach (var stmt in body.Statements)
            {
                if (stmt is LetStatement letStmt &&
                    letStmt.Kind != DeclarationKind.Var &&
                    letStmt.Name != null &&
                    parameterNames.Contains(letStmt.Name.Value))
                {
                    return true;
                }

                if (stmt is ClassStatement classStmt &&
                    classStmt.Name != null &&
                    parameterNames.Contains(classStmt.Name.Value))
                {
                    return true;
                }
                
                // Function declarations in strict mode are block-scoped lexical declarations.
                // In non-strict mode they are var-scoped and hoist (complicated), but V8/SpiderMonkey
                // often treat top-level function declarations in body as colliding with params if strict.
                // Since this check is primarily for strict mode/async/generators (where it matters),
                // we should include FunctionDeclarationStatement.
                if (stmt is FunctionDeclarationStatement funcDecl &&
                    funcDecl.Function.Name != null &&
                    parameterNames.Contains(funcDecl.Function.Name))
                {
                    return true;
                }
            }

            return false;
        }

        private void ValidateModuleTopLevelEarlyErrors(Program program)
        {
            if (!_isModule || program == null)
            {
                return;
            }

            var varDeclaredNames = new HashSet<string>(StringComparer.Ordinal);
            var lexicalDeclaredNames = new HashSet<string>(StringComparer.Ordinal);
            var exportedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var statement in program.Statements)
            {
                CollectModuleTopLevelDeclaredNames(statement, lexicalDeclaredNames, varDeclaredNames);
            }

            foreach (var name in lexicalDeclaredNames)
            {
                if (varDeclaredNames.Contains(name))
                {
                    _errors.Add($"SyntaxError: Duplicate declaration '{name}' in module scope");
                }
            }

            var moduleDeclaredNames = new HashSet<string>(varDeclaredNames, StringComparer.Ordinal);
            moduleDeclaredNames.UnionWith(lexicalDeclaredNames);

            foreach (var statement in program.Statements)
            {
                ValidateModuleExportEarlyErrors(statement, moduleDeclaredNames, exportedNames);
            }

            ValidateModuleLabelAndControlFlowEarlyErrors(program);
        }

        private void CollectModuleTopLevelDeclaredNames(
            Statement statement,
            HashSet<string> lexicalDeclaredNames,
            HashSet<string> varDeclaredNames)
        {
            if (statement == null)
            {
                return;
            }

            switch (statement)
            {
                case BlockStatement blockStatement:
                    foreach (var child in blockStatement.Statements)
                    {
                        CollectModuleTopLevelDeclaredNames(child, lexicalDeclaredNames, varDeclaredNames);
                    }
                    return;

                case ExportDeclaration exportDeclaration:
                    if (exportDeclaration.Declaration != null)
                    {
                        CollectModuleTopLevelDeclaredNames(exportDeclaration.Declaration, lexicalDeclaredNames, varDeclaredNames);
                    }
                    else
                    {
                        AddNamedDefaultExportBindingIfPresent(exportDeclaration.DefaultExpression, lexicalDeclaredNames);
                    }
                    return;

                case LetStatement letStatement:
                    var targetSet = letStatement.Kind == DeclarationKind.Var ? varDeclaredNames : lexicalDeclaredNames;
                    bool reportDuplicates = letStatement.Kind != DeclarationKind.Var;
                    AddModuleDeclaredName(letStatement.Name?.Value, targetSet, reportDuplicates);
                    if (letStatement.DestructuringPattern != null)
                    {
                        foreach (var name in ExtractBindingNames(letStatement.DestructuringPattern))
                        {
                            AddModuleDeclaredName(name, targetSet, reportDuplicates);
                        }
                    }
                    return;

                case FunctionDeclarationStatement functionDeclaration:
                    AddModuleDeclaredName(functionDeclaration.Function?.Name, lexicalDeclaredNames, reportDuplicates: true);
                    return;

                case ClassStatement classStatement:
                    AddModuleDeclaredName(classStatement.Name?.Value, lexicalDeclaredNames, reportDuplicates: true);
                    return;
            }
        }

        private void AddModuleDeclaredName(string name, HashSet<string> targetSet, bool reportDuplicates)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (!targetSet.Add(name) && reportDuplicates)
            {
                _errors.Add($"SyntaxError: Duplicate declaration '{name}' in module scope");
            }
        }

        private void AddNamedDefaultExportBindingIfPresent(Expression expression, HashSet<string> lexicalDeclaredNames)
        {
            switch (expression)
            {
                case FunctionLiteral functionLiteral:
                    AddModuleDeclaredName(functionLiteral.Name, lexicalDeclaredNames, reportDuplicates: true);
                    return;

                case AsyncFunctionExpression asyncFunctionExpression:
                    AddModuleDeclaredName(asyncFunctionExpression.Name?.Value, lexicalDeclaredNames, reportDuplicates: true);
                    return;

                case ClassExpression classExpression:
                    AddModuleDeclaredName(classExpression.Name?.Value, lexicalDeclaredNames, reportDuplicates: true);
                    return;
            }
        }

        private void ValidateModuleExportEarlyErrors(
            Statement statement,
            HashSet<string> moduleDeclaredNames,
            HashSet<string> exportedNames)
        {
            if (statement is not ExportDeclaration exportDeclaration)
            {
                return;
            }

            if (exportDeclaration.DefaultExpression != null)
            {
                AddModuleExportedName("default", exportedNames);
                return;
            }

            if (exportDeclaration.Declaration != null)
            {
                foreach (var declaredName in GetModuleDeclaredNames(exportDeclaration.Declaration))
                {
                    AddModuleExportedName(declaredName, exportedNames);
                }
                return;
            }

            foreach (var specifier in exportDeclaration.Specifiers)
            {
                if (specifier == null)
                {
                    continue;
                }

                var localBindingName = specifier.Local?.Value;
                var exportedName = specifier.Exported?.Value ?? localBindingName;

                if (!(localBindingName == "*" && specifier.Exported == null))
                {
                    AddModuleExportedName(exportedName, exportedNames);
                }

                if (exportDeclaration.Source == null &&
                    !string.IsNullOrEmpty(localBindingName) &&
                    localBindingName != "*" &&
                    !moduleDeclaredNames.Contains(localBindingName))
                {
                    _errors.Add($"SyntaxError: Exported binding '{localBindingName}' is not declared in module scope");
                }
            }
        }

        private void AddModuleExportedName(string name, HashSet<string> exportedNames)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (!exportedNames.Add(name))
            {
                _errors.Add($"SyntaxError: Duplicate export '{name}' in module scope");
            }
        }

        private IEnumerable<string> GetModuleDeclaredNames(Statement statement)
        {
            if (statement == null)
            {
                yield break;
            }

            switch (statement)
            {
                case BlockStatement blockStatement:
                    foreach (var child in blockStatement.Statements)
                    {
                        foreach (var childName in GetModuleDeclaredNames(child))
                        {
                            yield return childName;
                        }
                    }
                    yield break;

                case LetStatement letStatement:
                    if (!string.IsNullOrEmpty(letStatement.Name?.Value))
                    {
                        yield return letStatement.Name.Value;
                    }

                    if (letStatement.DestructuringPattern != null)
                    {
                        foreach (var name in ExtractBindingNames(letStatement.DestructuringPattern))
                        {
                            if (!string.IsNullOrEmpty(name))
                            {
                                yield return name;
                            }
                        }
                    }
                    yield break;

                case FunctionDeclarationStatement functionDeclaration:
                    if (!string.IsNullOrEmpty(functionDeclaration.Function?.Name))
                    {
                        yield return functionDeclaration.Function.Name;
                    }
                    yield break;

                case ClassStatement classStatement:
                    if (!string.IsNullOrEmpty(classStatement.Name?.Value))
                    {
                        yield return classStatement.Name.Value;
                    }
                    yield break;
            }
        }

        private void ValidateModuleStringNameIfNeeded(Token token, string context)
        {
            if (!_isModule || token == null || token.Type != TokenType.String)
            {
                return;
            }

            if (!IsWellFormedUnicodeString(token.Literal))
            {
                _errors.Add($"SyntaxError: Ill-formed Unicode string in module {context} name");
            }
        }

        private static bool IsWellFormedUnicodeString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    {
                        return false;
                    }

                    i++;
                    continue;
                }

                if (char.IsLowSurrogate(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private bool PeekStartsEmptyCall()
        {
            if (!PeekTokenIs(TokenType.LParen) || _lexer?.Source == null)
            {
                return false;
            }

            int nextTokenStart = _peekToken.Position + Math.Max(_peekToken.Literal?.Length ?? 0, 1);
            if (nextTokenStart < 0 || nextTokenStart >= _lexer.Source.Length)
            {
                return false;
            }

            var probe = new Lexer(_lexer.Source.Substring(nextTokenStart))
            {
                TreatHtmlLikeCommentsAsComments = _lexer.TreatHtmlLikeCommentsAsComments
            };

            return probe.NextToken().Type == TokenType.RParen;
        }

        private bool IsModuleHtmlCommentTokenStart()
        {
            return (_curToken.Type == TokenType.Lt && _peekToken.Type == TokenType.Bang) ||
                   (_curToken.Type == TokenType.Decrement && _peekToken.Type == TokenType.Gt);
        }

        private void ValidateModuleLabelAndControlFlowEarlyErrors(Program program)
        {
            var activeLabels = new HashSet<string>(StringComparer.Ordinal);
            var activeIterationLabels = new HashSet<string>(StringComparer.Ordinal);

            foreach (var statement in program.Statements)
            {
                ValidateLabelAndControlFlowTargets(statement, activeLabels, activeIterationLabels);
            }
        }

        private void ValidateLabelAndControlFlowTargets(
            Statement statement,
            HashSet<string> activeLabels,
            HashSet<string> activeIterationLabels)
        {
            if (statement == null)
            {
                return;
            }

            switch (statement)
            {
                case LabeledStatement labeledStatement:
                    var labelName = labeledStatement.Label?.Value;
                    if (!string.IsNullOrEmpty(labelName) && activeLabels.Contains(labelName))
                    {
                        _errors.Add($"SyntaxError: Duplicate label '{labelName}' in module scope");
                    }

                    bool addedLabel = false;
                    bool addedIterationLabel = false;
                    if (!string.IsNullOrEmpty(labelName))
                    {
                        addedLabel = activeLabels.Add(labelName);
                        if (LabelsIterationStatement(labeledStatement.Body))
                        {
                            addedIterationLabel = activeIterationLabels.Add(labelName);
                        }
                    }

                    ValidateLabelAndControlFlowTargets(labeledStatement.Body, activeLabels, activeIterationLabels);

                    if (addedIterationLabel)
                    {
                        activeIterationLabels.Remove(labelName);
                    }

                    if (addedLabel)
                    {
                        activeLabels.Remove(labelName);
                    }
                    return;

                case BreakStatement breakStatement:
                    if (breakStatement.Label != null &&
                        !string.IsNullOrEmpty(breakStatement.Label.Value) &&
                        !activeLabels.Contains(breakStatement.Label.Value))
                    {
                        _errors.Add($"SyntaxError: Undefined break target '{breakStatement.Label.Value}'");
                    }
                    return;

                case ContinueStatement continueStatement:
                    if (continueStatement.Label != null &&
                        !string.IsNullOrEmpty(continueStatement.Label.Value) &&
                        !activeIterationLabels.Contains(continueStatement.Label.Value))
                    {
                        _errors.Add($"SyntaxError: Undefined continue target '{continueStatement.Label.Value}'");
                    }
                    return;

                case BlockStatement blockStatement:
                    foreach (var child in blockStatement.Statements)
                    {
                        ValidateLabelAndControlFlowTargets(child, activeLabels, activeIterationLabels);
                    }
                    return;

                case IfStatement ifStatement:
                    ValidateLabelAndControlFlowTargets(ifStatement.Consequence, activeLabels, activeIterationLabels);
                    ValidateLabelAndControlFlowTargets(ifStatement.Alternative, activeLabels, activeIterationLabels);
                    return;

                case WhileStatement whileStatement:
                    ValidateLabelAndControlFlowTargets(whileStatement.Body, activeLabels, activeIterationLabels);
                    return;

                case DoWhileStatement doWhileStatement:
                    ValidateLabelAndControlFlowTargets(doWhileStatement.Body, activeLabels, activeIterationLabels);
                    return;

                case ForStatement forStatement:
                    ValidateLabelAndControlFlowTargets(forStatement.Init, activeLabels, activeIterationLabels);
                    ValidateLabelAndControlFlowTargets(forStatement.Update, activeLabels, activeIterationLabels);
                    ValidateLabelAndControlFlowTargets(forStatement.Body, activeLabels, activeIterationLabels);
                    return;

                case ForInStatement forInStatement:
                    ValidateLabelAndControlFlowTargets(forInStatement.Body, activeLabels, activeIterationLabels);
                    return;

                case ForOfStatement forOfStatement:
                    ValidateLabelAndControlFlowTargets(forOfStatement.Body, activeLabels, activeIterationLabels);
                    return;

                case SwitchStatement switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        foreach (var consequent in switchCase.Consequent)
                        {
                            ValidateLabelAndControlFlowTargets(consequent, activeLabels, activeIterationLabels);
                        }
                    }
                    return;

                case TryStatement tryStatement:
                    ValidateLabelAndControlFlowTargets(tryStatement.Block, activeLabels, activeIterationLabels);
                    ValidateLabelAndControlFlowTargets(tryStatement.CatchBlock, activeLabels, activeIterationLabels);
                    ValidateLabelAndControlFlowTargets(tryStatement.FinallyBlock, activeLabels, activeIterationLabels);
                    return;

                case WithStatement withStatement:
                    ValidateLabelAndControlFlowTargets(withStatement.Body, activeLabels, activeIterationLabels);
                    return;

                case ExportDeclaration exportDeclaration when exportDeclaration.Declaration != null:
                    ValidateLabelAndControlFlowTargets(exportDeclaration.Declaration, activeLabels, activeIterationLabels);
                    return;
            }
        }

        private static bool LabelsIterationStatement(Statement statement)
        {
            return statement switch
            {
                WhileStatement => true,
                DoWhileStatement => true,
                ForStatement => true,
                ForInStatement => true,
                ForOfStatement => true,
                LabeledStatement labeledStatement => LabelsIterationStatement(labeledStatement.Body),
                _ => false
            };
        }
        
        private static readonly HashSet<TokenType> _contextualKeywords = new HashSet<TokenType>
        {
            TokenType.Async, TokenType.Let, TokenType.Of, TokenType.From,
            TokenType.As, TokenType.Static, TokenType.Yield, TokenType.Await
        };

        // ES reserved words not in the lexer's keyword table
        private static readonly HashSet<string> _reservedWords = new HashSet<string>
        {
            "enum", "debugger"
        };

        // Strict mode reserved words (future reserved words in strict mode)
        private static readonly HashSet<string> _strictReservedWords = new HashSet<string>
        {
            "implements", "interface", "package", "private", "protected", "public",
            "let", "static", "yield"
        };

        private bool ValidateBindingIdentifier(Token token)
        {
            // Check string-based reserved words (not in lexer keyword table)
            if (_reservedWords.Contains(token.Literal))
            {
                _errors.Add($"SyntaxError: Unexpected reserved word '{token.Literal}'");
                return false;
            }

            // Check strict mode reserved words
            if (_isStrictMode && _strictReservedWords.Contains(token.Literal))
            {
                _errors.Add($"SyntaxError: Unexpected strict mode reserved word '{token.Literal}'");
                return false;
            }
            
            // Check keywords from lexer
            var type = Lexer.LookupIdent(token.Literal);
            if (type != TokenType.Identifier && !_contextualKeywords.Contains(type))
            {
                _errors.Add($"SyntaxError: Unexpected reserved word '{token.Literal}'");
                return false;
            }

            // "eval" and "arguments" are invalid binding identifiers in strict mode
            if (_isStrictMode && (token.Literal == "eval" || token.Literal == "arguments"))
            {
                _errors.Add($"SyntaxError: Unexpected strict mode reserved word '{token.Literal}'");
                return false;
            }

            if (_classStaticBlockDepth > 0 && _functionDepth == 0 && token.Literal == "await")
            {
                _errors.Add("SyntaxError: Unexpected identifier 'await' in class static block");
                return false;
            }

            if (_generatorFunctionDepth > 0 && token.Literal == "yield")
            {
                _errors.Add("SyntaxError: Unexpected identifier 'yield' in generator parameter/body");
                return false;
            }

            if ((_asyncFunctionDepth > 0 || _isModule) && token.Literal == "await")
            {
                _errors.Add("SyntaxError: Unexpected identifier 'await' in async function parameter/body");
                return false;
            }

            return true;
        }
    }
}






















