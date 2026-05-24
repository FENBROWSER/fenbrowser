using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Source;
using System.Globalization;
using System.Numerics;

namespace FenBrowser.Js.Parser;

public sealed class JsParser
{
    private static readonly HashSet<string> AlwaysReservedIdentifierNames = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "finally", "for", "function", "if", "import", "in",
        "instanceof", "new", "return", "super", "switch", "this", "throw", "try", "typeof",
        "var", "void", "while", "with", "enum", "true", "false", "null"
    };

    private static readonly HashSet<string> StrictModeReservedIdentifierNames = new(StringComparer.Ordinal)
    {
        "implements", "interface", "let", "package", "private", "protected", "public", "static", "yield"
    };

    private readonly IReadOnlyList<Token> _tokens;
    private int _index;
    private int _syntheticBindingCounter;
    private bool _strictMode;
    private bool _moduleMode;
    private bool _inDirectivePrologue = true;

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
        _moduleMode = kind == ProgramKind.Module;
        _strictMode = kind == ProgramKind.Module;
        var statements = new List<StatementNode>();
        var start = Current().Span;

        while (!Is(TokenKind.EndOfFile))
        {
            var statement = ParseStatement();
            statements.Add(statement);
            UpdateDirectivePrologueState(statement);
        }

        var end = Current().Span;
        var span = new SourceSpan(start.Start, Math.Max(0, end.Start - start.Start), start.Line, start.Column);
        ValidateDirectivePrologueStrictStringEscapes(statements);
        return new ProgramNode(kind, statements, span);
    }

    private void UpdateDirectivePrologueState(StatementNode statement)
    {
        if (!_inDirectivePrologue)
        {
            return;
        }

        if (statement is not ExpressionStatementNode expressionStatement ||
            expressionStatement.Expression is not StringLiteralExpressionNode stringLiteral)
        {
            _inDirectivePrologue = false;
            return;
        }

        if (string.Equals(stringLiteral.Value, "use strict", StringComparison.Ordinal))
        {
            _strictMode = true;
            return;
        }
    }

    private static void ValidateDirectivePrologueStrictStringEscapes(IReadOnlyList<StatementNode> statements)
    {
        var directiveStrings = new List<StringLiteralExpressionNode>();
        var hasUseStrict = false;
        foreach (var statement in statements)
        {
            if (statement is not ExpressionStatementNode expressionStatement ||
                expressionStatement.Expression is not StringLiteralExpressionNode stringLiteral)
            {
                break;
            }

            directiveStrings.Add(stringLiteral);
            if (string.Equals(stringLiteral.Value, "use strict", StringComparison.Ordinal))
            {
                hasUseStrict = true;
            }
        }

        if (!hasUseStrict)
        {
            return;
        }

        foreach (var stringLiteral in directiveStrings)
        {
            if (ContainsStrictModeForbiddenStringEscape(stringLiteral.RawText))
            {
                throw new JsParserException("Legacy string escape sequence is not allowed in strict directive prologue.");
            }
        }
    }

    private static bool ContainsStrictModeForbiddenStringEscape(string rawText)
    {
        for (var i = 1; i + 1 < rawText.Length; i++)
        {
            if (rawText[i] != '\\')
            {
                continue;
            }

            var escapeIndex = i + 1;
            var escaped = rawText[escapeIndex];
            if (escaped is >= '1' and <= '9')
            {
                return true;
            }

            if (escaped == '0' &&
                escapeIndex + 1 < rawText.Length - 1 &&
                rawText[escapeIndex + 1] is >= '0' and <= '9')
            {
                return true;
            }

            i = escapeIndex;
        }

        return false;
    }

    private StatementNode ParseStatement()
    {
        if (IsIdentifierLike(Current()) && PeekIsPunctuator(1, ":"))
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
                    return ParseImportDeclaration();
                case "export":
                    return ParseExportDeclaration();
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
                case "with":
                    return ParseWithStatement();
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
        else if (!Is(TokenKind.EndOfFile) && !IsPunctuator("}") && Current().Span.Line == Previous().Span.Line)
        {
            throw new JsParserException($"Expected semicolon or line terminator after expression, found '{Current().Text}'.");
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

    private WithStatementNode ParseWithStatement()
    {
        var start = Advance(); // with
        if (_strictMode)
        {
            throw new JsParserException("with statements are not allowed in strict mode.");
        }

        ExpectPunctuator("(");
        var obj = ParseExpression(0);
        ExpectPunctuator(")");
        var body = ParseStatement();
        return new WithStatementNode(obj, body, MergeSpan(start.Span, body.Span));
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
        ExpressionNode? initializerExpression = null;
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
                initializerExpression = initExpr;
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

            ValidateForInInitializer(initializer);

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
                if (initializerExpression is not null &&
                    TryGetTopLevelInBinary(initializerExpression, out var inLeft, out _) &&
                    inLeft is AssignmentExpressionNode)
                {
                    throw new JsParserException("for-in assignment initializers are not allowed.");
                }

                if (initializerExpression is AssignmentExpressionNode assignmentInitializer &&
                    TryGetTopLevelInBinary(assignmentInitializer.Right, out _, out _))
                {
                    throw new JsParserException("for-in assignment initializers are not allowed.");
                }

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

        if (initializerIsDeclaration &&
            initializer is VariableDeclarationStatementNode declarationWithInitializer &&
            IsPunctuator(")") &&
            DeclarationContainsInInitializer(declarationWithInitializer))
        {
            var hasPattern = declarationWithInitializer.Declarators.Any(d => d.Identifier.StartsWith("__pattern", StringComparison.Ordinal));
            if (!string.Equals(declarationWithInitializer.Kind, "var", StringComparison.Ordinal) || _strictMode || hasPattern)
            {
                throw new JsParserException("for-in declaration initializers are not allowed.");
            }
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

    private static void ValidateForInInitializer(StatementNode initializer)
    {
        if (initializer is VariableDeclarationStatementNode declaration)
        {
            foreach (var declarator in declaration.Declarators)
            {
                if (declarator.Initializer is not null)
                {
                    throw new JsParserException("for-in declaration initializers are not allowed.");
                }
            }

            return;
        }

        if (initializer is ExpressionStatementNode expressionStatement && expressionStatement.Expression is AssignmentExpressionNode)
        {
            throw new JsParserException("for-in assignment initializers are not allowed.");
        }
    }

    private static bool DeclarationContainsInInitializer(VariableDeclarationStatementNode declaration)
    {
        foreach (var declarator in declaration.Declarators)
        {
            if (declarator.Initializer is null)
            {
                continue;
            }

            if (TryGetTopLevelInBinary(declarator.Initializer, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetTopLevelInBinary(ExpressionNode expression, out ExpressionNode left, out ExpressionNode right)
    {
        switch (expression)
        {
            case BinaryExpressionNode binary:
                if (string.Equals(binary.Operator, "in", StringComparison.Ordinal))
                {
                    left = binary.Left;
                    right = binary.Right;
                    return true;
                }

                break;
            case ParenthesizedExpressionNode parenthesized:
                return TryGetTopLevelInBinary(parenthesized.Expression, out left, out right);
        }

        left = new IdentifierExpressionNode(string.Empty, expression.Span);
        right = new IdentifierExpressionNode(string.Empty, expression.Span);
        return false;
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
        var isAsync = false;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "async")
        {
            isAsync = true;
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

        var isGenerator = false;
        if (IsPunctuator("*"))
        {
            isGenerator = true;
            Advance();
        }

        var name = ExpectIdentifier();
        var parameters = ParseParameterList();
        var body = ParseBlockStatement();
        ValidateDirectivePrologueStrictStringEscapes(body.Statements);
        return new FunctionDeclarationNode(
            name.Text,
            parameters,
            body,
            MergeSpan(start.Span, body.Span),
            IsAsync: isAsync,
            IsGenerator: isGenerator);
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
            if (IsIdentifierLike(Current()))
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

    // E.7 - module-level import declarations. Supports the common forms:
    //   import "mod"                          -- side effect only
    //   import x from "mod"                   -- default import
    //   import * as ns from "mod"             -- namespace import
    //   import { a, b as c } from "mod"       -- named imports (with aliases)
    //   import x, { a } from "mod"            -- combined default + named
    //   import x, * as ns from "mod"          -- combined default + namespace
    private ImportDeclarationNode ParseImportDeclaration()
    {
        var start = Advance(); // import
        var entries = new List<FenBrowser.Js.Modules.ImportEntry>();
        string moduleRequest;

        // Side-effect-only form: import "mod";
        if (Current().Kind == TokenKind.String)
        {
            moduleRequest = ParseStringLiteralValue();
            ConsumeSemicolon();
            return new ImportDeclarationNode(moduleRequest, entries, MergeSpan(start.Span, Previous().Span));
        }

        // Default binding (always first if present).
        string? defaultName = null;
        if (IsIdentifierLike(Current()))
        {
            defaultName = Advance().Text;
            if (IsPunctuator(","))
            {
                Advance();
            }
        }

        // Optional namespace or named binding(s).
        if (IsPunctuator("*"))
        {
            Advance();
            ExpectKeyword("as");
            var nsName = ExpectIdentifier().Text;
            entries.Add(new FenBrowser.Js.Modules.ImportEntry(
                ModuleRequest: "", // filled in below
                ImportName: FenBrowser.Js.Modules.ImportEntry.NamespaceImport,
                LocalName: nsName));
        }
        else if (IsPunctuator("{"))
        {
            Advance();
            while (!IsPunctuator("}") && !Is(TokenKind.EndOfFile))
            {
                var importName = ExpectIdentifier().Text;
                var localName = importName;
                if (IsIdentifierLike(Current()) && Current().Text == "as")
                {
                    Advance();
                    localName = ExpectIdentifier().Text;
                }
                entries.Add(new FenBrowser.Js.Modules.ImportEntry(
                    ModuleRequest: "",
                    ImportName: importName,
                    LocalName: localName));
                if (IsPunctuator(","))
                {
                    Advance();
                }
            }
            ExpectPunctuator("}");
        }

        ExpectKeyword("from");
        moduleRequest = ParseStringLiteralValue();
        ConsumeSemicolon();

        // Stamp the module request onto every entry now that it's known. Also
        // emit a default entry if we collected one above.
        var finalEntries = new List<FenBrowser.Js.Modules.ImportEntry>(entries.Count + 1);
        if (defaultName is not null)
        {
            finalEntries.Add(new FenBrowser.Js.Modules.ImportEntry(
                moduleRequest, FenBrowser.Js.Modules.ImportEntry.DefaultImport, defaultName));
        }
        foreach (var e in entries)
        {
            finalEntries.Add(e with { ModuleRequest = moduleRequest });
        }

        return new ImportDeclarationNode(moduleRequest, finalEntries, MergeSpan(start.Span, Previous().Span));
    }

    // E.7 - module-level export declarations. Supports:
    //   export var/let/const x = ...
    //   export function f() {...}
    //   export class C {...}
    //   export default expr
    //   export { a, b as c }
    //   export { a } from "mod"           -- re-export
    //   export * from "mod"               -- star re-export
    //   export * as ns from "mod"         -- namespace re-export
    private ExportDeclarationNode ParseExportDeclaration()
    {
        var start = Advance(); // export
        var entries = new List<FenBrowser.Js.Modules.ExportEntry>();

        // export default <expr>;
        if (PeekKeyword(0, "default"))
        {
            Advance();
            var expr = ParseExpression(0);
            ConsumeSemicolon();
            entries.Add(new FenBrowser.Js.Modules.ExportEntry(
                ExportName: FenBrowser.Js.Modules.ImportEntry.DefaultImport,
                ModuleRequest: null,
                ImportName: null,
                LocalName: FenBrowser.Js.Modules.ExportEntry.DefaultLocalName));
            return new ExportDeclarationNode(entries, LocalDeclaration: null, DefaultExpression: expr,
                MergeSpan(start.Span, Previous().Span));
        }

        // export * [as ns] from "mod";
        if (IsPunctuator("*"))
        {
            Advance();
            string? exportName = null;
            string importName = FenBrowser.Js.Modules.ExportEntry.AllExports;
            if (IsIdentifierLike(Current()) && Current().Text == "as")
            {
                Advance();
                exportName = ExpectIdentifier().Text;
                importName = FenBrowser.Js.Modules.ExportEntry.AllButDefaultExports;
            }
            ExpectKeyword("from");
            var mod = ParseStringLiteralValue();
            ConsumeSemicolon();
            entries.Add(new FenBrowser.Js.Modules.ExportEntry(exportName, mod, importName, LocalName: null));
            return new ExportDeclarationNode(entries, LocalDeclaration: null, DefaultExpression: null,
                MergeSpan(start.Span, Previous().Span));
        }

        // export { a, b as c } [from "mod"];
        if (IsPunctuator("{"))
        {
            Advance();
            var names = new List<(string Local, string Exported)>();
            while (!IsPunctuator("}") && !Is(TokenKind.EndOfFile))
            {
                var local = ExpectIdentifier().Text;
                var exported = local;
                if (IsIdentifierLike(Current()) && Current().Text == "as")
                {
                    Advance();
                    exported = ExpectIdentifier().Text;
                }
                names.Add((local, exported));
                if (IsPunctuator(","))
                {
                    Advance();
                }
            }
            ExpectPunctuator("}");

            string? moduleRequest = null;
            if (IsIdentifierLike(Current()) && Current().Text == "from")
            {
                Advance();
                moduleRequest = ParseStringLiteralValue();
            }
            ConsumeSemicolon();

            foreach (var (local, exported) in names)
            {
                entries.Add(moduleRequest is null
                    ? new FenBrowser.Js.Modules.ExportEntry(exported, null, null, local)
                    : new FenBrowser.Js.Modules.ExportEntry(exported, moduleRequest, local, null));
            }
            return new ExportDeclarationNode(entries, LocalDeclaration: null, DefaultExpression: null,
                MergeSpan(start.Span, Previous().Span));
        }

        // export var x = ... / export function f() ... / export class C ...
        var inner = ParseStatement();
        foreach (var name in CollectExportableLocalNames(inner))
        {
            entries.Add(new FenBrowser.Js.Modules.ExportEntry(name, null, null, name));
        }

        return new ExportDeclarationNode(entries, LocalDeclaration: inner, DefaultExpression: null,
            MergeSpan(start.Span, Previous().Span));
    }

    private static IEnumerable<string> CollectExportableLocalNames(StatementNode stmt)
    {
        switch (stmt)
        {
            case VariableDeclarationStatementNode varDecl:
                foreach (var d in varDecl.Declarators) yield return d.Identifier;
                break;
            case FunctionDeclarationNode fn:
                yield return fn.Name;
                break;
            case ClassDeclarationNode cls:
                yield return cls.Name;
                break;
        }
    }

    private string ParseStringLiteralValue()
    {
        if (Current().Kind != TokenKind.String)
        {
            throw new JsParserException($"Expected string literal, found '{Current().Text}'.");
        }
        var tok = Advance();
        var raw = tok.Text.Length >= 2 ? tok.Text[1..^1] : string.Empty;
        return DecodeStringLiteralBody(raw);
    }

    private void ExpectKeyword(string text)
    {
        if (!(IsIdentifierLike(Current()) && Current().Text == text))
        {
            throw new JsParserException($"Expected '{text}', found '{Current().Text}'.");
        }
        Advance();
    }

    private void ConsumeSemicolon()
    {
        if (IsPunctuator(";"))
        {
            Advance();
        }
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

        var (members, close) = ParseClassBody();
        return new ClassDeclarationNode(name.Text, baseClass, members, MergeSpan(start.Span, close.Span));
    }

    private ClassExpressionNode ParseClassExpression()
    {
        var start = Advance(); // class
        string? name = null;
        if (IsIdentifierLike(Current()))
        {
            name = Advance().Text;
        }

        ExpressionNode? baseClass = null;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "extends")
        {
            Advance();
            baseClass = ParseExpression(0);
        }

        var (members, close) = ParseClassBody();
        return new ClassExpressionNode(name, baseClass, members, MergeSpan(start.Span, close.Span));
    }

    // ECMA-262 15.7 ClassBody. The minimum v1 grammar:
    //   '{' (ClassMember | ';')* '}'
    //   ClassMember := 'static'? (constructor-method | named-method | getter | setter)
    //
    // Computed keys, private fields, public field declarations, and static
    // initialization blocks are deferred. A leading 'static' modifier is
    // recognised but otherwise behaves like a tag on the synthesised member.
    private (IReadOnlyList<ClassMemberNode> Members, Token Close) ParseClassBody()
    {
        ExpectPunctuator("{");
        var members = new List<ClassMemberNode>();

        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("}"))
        {
            // Stray semicolons between members are allowed per the spec.
            if (IsPunctuator(";"))
            {
                Advance();
                continue;
            }

            var memberStart = Current().Span;
            bool isStatic = false;
            // The 'static' modifier is a contextual keyword (lexed as Identifier).
            // Disambiguate against a method literally named "static" by peeking the
            // next token: if it's '(', the current token is the method name.
            if (IsIdentifierLike(Current()) && Current().Text == "static" && !IsPunctuatorAt(1, "("))
            {
                Advance();
                isStatic = true;
            }

            // H.5 - static initialization block: `static { ... }`. ECMA-262 15.7
            // ClassStaticBlock. The block body runs once at class-definition time
            // with `this` bound to the class itself; lowered to a synthesised
            // parameterless function invoked with this=class.
            if (isStatic && IsPunctuator("{"))
            {
                var block = ParseBlockStatement();
                var staticFn = new FunctionExpressionNode(
                    null,
                    Array.Empty<string>(),
                    block,
                    MergeSpan(memberStart, block.Span));
                members.Add(new ClassMemberNode(
                    string.Empty,
                    ClassMemberKind.StaticBlock,
                    IsStatic: true,
                    staticFn,
                    MergeSpan(memberStart, block.Span)));
                continue;
            }

            var isAsync = false;
            if (IsIdentifierLike(Current()) && Current().Text == "async" && !IsPunctuatorAt(1, "(") &&
                (IsPunctuatorAt(1, "*") || IsClassMemberNameStartAt(1)))
            {
                Advance();
                isAsync = true;
            }

            var isGenerator = false;
            if (IsPunctuator("*"))
            {
                Advance();
                isGenerator = true;
            }

            // Detect getter/setter prefix - "get name() { ... }" / "set name(v) { ... }".
            ClassMemberKind kind = ClassMemberKind.Method;
            if (IsIdentifierLike(Current()) && (Current().Text == "get" || Current().Text == "set")
                && !IsPunctuatorAt(1, "(")
                && IsClassMemberNameStartAt(1))
            {
                kind = Current().Text == "get" ? ClassMemberKind.Getter : ClassMemberKind.Setter;
                Advance();
            }

            string memberName = ConsumeClassMemberName(out var computedName, out var isPrivate);
            if (kind == ClassMemberKind.Method && memberName == "constructor" && !isStatic)
            {
                kind = ClassMemberKind.Constructor;
            }

            if (!IsPunctuator("("))
            {
                ExpressionNode fieldInitializer = new IdentifierExpressionNode("undefined", memberStart);
                if (IsPunctuator("="))
                {
                    Advance();
                    fieldInitializer = ParseExpression(0);
                }

                ConsumeSemicolon();
                members.Add(new ClassMemberNode(
                    memberName,
                    ClassMemberKind.Field,
                    isStatic,
                    fieldInitializer,
                    MergeSpan(memberStart, fieldInitializer.Span),
                    isPrivate,
                    isAsync,
                    isGenerator,
                    computedName));
                continue;
            }

            var parameters = ParseParameterList();
            var body = ParseBlockStatement();
            var fn = new FunctionExpressionNode(
                memberName,
                parameters,
                body,
                MergeSpan(memberStart, body.Span),
                IsAsync: isAsync,
                IsGenerator: isGenerator);
            members.Add(new ClassMemberNode(
                memberName,
                kind,
                isStatic,
                fn,
                MergeSpan(memberStart, body.Span),
                isPrivate,
                isAsync,
                isGenerator,
                computedName));
        }

        if (!IsPunctuator("}"))
        {
            throw new JsParserException("Unterminated class body.");
        }

        var close = Advance();
        return (members, close);
    }

    // ECMA-262 12.8.4 SV (String Value) of a StringLiteral. Decodes the
    // common escape forms: single-character escapes, \xHH, \uHHHH,
    // \u{HHHHHH}, line continuations, octal `\0` (when not followed by a
    // digit). The input must NOT include the surrounding quotes - pass the
    // already-stripped body.
    internal static string DecodeStringLiteralBody(string body)
    {
        if (body.IndexOf('\\') < 0) return body;
        var sb = new System.Text.StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\\' || i + 1 >= body.Length)
            {
                sb.Append(c);
                continue;
            }
            var next = body[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '0':
                    // \0 is the null escape only when not followed by a digit.
                    if (i + 1 >= body.Length || body[i + 1] < '0' || body[i + 1] > '9')
                    {
                        sb.Append('\0');
                    }
                    else
                    {
                        sb.Append('\0');
                    }
                    break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '`': sb.Append('`'); break;
                case '\n':
                case '\r':
                    // Line continuation: skip the line terminator. If \r\n, eat both.
                    if (next == '\r' && i + 1 < body.Length && body[i + 1] == '\n') i++;
                    break;
                case 'x':
                    if (i + 2 < body.Length
                        && TryParseHex(body[i + 1], out var h1)
                        && TryParseHex(body[i + 2], out var h2))
                    {
                        sb.Append((char)((h1 << 4) | h2));
                        i += 2;
                    }
                    else
                    {
                        sb.Append('x');
                    }
                    break;
                case 'u':
                    if (i + 1 < body.Length && body[i + 1] == '{')
                    {
                        var end = body.IndexOf('}', i + 2);
                        if (end > i + 2)
                        {
                            var hex = body.Substring(i + 2, end - (i + 2));
                            if (int.TryParse(
                                    hex,
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var cp) && cp >= 0 && cp <= 0x10FFFF)
                            {
                                sb.Append(char.ConvertFromUtf32(cp));
                                i = end;
                                break;
                            }
                        }
                        sb.Append('u');
                        break;
                    }
                    if (i + 4 < body.Length
                        && TryParseHex(body[i + 1], out var u1)
                        && TryParseHex(body[i + 2], out var u2)
                        && TryParseHex(body[i + 3], out var u3)
                        && TryParseHex(body[i + 4], out var u4))
                    {
                        sb.Append((char)((u1 << 12) | (u2 << 8) | (u3 << 4) | u4));
                        i += 4;
                    }
                    else
                    {
                        sb.Append('u');
                    }
                    break;
                default:
                    sb.Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool TryParseHex(char c, out int value)
    {
        if (c >= '0' && c <= '9') { value = c - '0'; return true; }
        if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
        if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
        value = 0;
        return false;
    }

    // ECMA-262 6.1.6.1.13 Number::toString. Integers in range render without
    // a decimal point; small or very large magnitudes use scientific notation
    // with a lowercase 'e' and a signed exponent stripped of leading zeros
    // (`1e-7`, not `1E-07`). Approximate via "R" then normalize the exponent.
    private static string ToJsNumberString(double n)
    {
        if (double.IsNaN(n)) return "NaN";
        if (double.IsPositiveInfinity(n)) return "Infinity";
        if (double.IsNegativeInfinity(n)) return "-Infinity";
        if (n == 0d) return "0";
        if (n == Math.Truncate(n) && Math.Abs(n) < 1e21)
        {
            return ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var s = n.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var eIdx = s.IndexOfAny(new[] { 'e', 'E' });
        if (eIdx < 0) return s;
        var mantissa = s.Substring(0, eIdx);
        var expPart = s.Substring(eIdx + 1);
        int sign = 1;
        int j = 0;
        if (expPart.Length > 0 && (expPart[0] == '+' || expPart[0] == '-'))
        {
            if (expPart[0] == '-') sign = -1;
            j = 1;
        }
        while (j < expPart.Length - 1 && expPart[j] == '0') j++;
        var expDigits = expPart.Substring(j);
        var expStr = sign < 0 ? "-" + expDigits : expDigits;
        return mantissa + "e" + expStr;
    }

    private static string ToJsNumberString(long n) =>
        n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string ConsumeClassMemberName(out ExpressionNode? computedName, out bool isPrivate)
    {
        computedName = null;
        isPrivate = false;
        var tok = Current();
        if (IsPunctuator("["))
        {
            Advance();
            computedName = ParseExpression(0);
            ExpectPunctuator("]");
            return computedName switch
            {
                StringLiteralExpressionNode s => s.Value,
                NumericLiteralExpressionNode n => ToJsNumberString(n.Value),
                IdentifierExpressionNode id => id.Name,
                _ => "<computed>"
            };
        }
        if (tok.Kind == TokenKind.PrivateIdentifier)
        {
            Advance();
            isPrivate = true;
            return tok.Text;
        }
        if (tok.Kind == TokenKind.Identifier || tok.Kind == TokenKind.Keyword)
        {
            Advance();
            return tok.Text;
        }
        if (tok.Kind == TokenKind.String)
        {
            Advance();
            // ECMA-262 12.2.6.7: StringLiteral PropertyNames use the SV
            // (string value) of the literal: surrounding quotes stripped and
            // escape sequences decoded.
            var raw = tok.Text.Length >= 2 ? tok.Text[1..^1] : string.Empty;
            return DecodeStringLiteralBody(raw);
        }
        if (tok.Kind == TokenKind.Number)
        {
            Advance();
            // ECMA-262 12.2.6.7: a NumericLiteral used as a PropertyName
            // is converted via ToPropertyKey -> ToString applied to the
            // numeric value, NOT preserved as the source spelling. So
            // `get 0b10()` installs under the key "2".
            if (double.TryParse(
                    tok.Text.Replace("_", string.Empty),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return ToJsNumberString(parsed);
            }
            // Non-decimal literals (0x, 0o, 0b) - manually parse and stringify.
            var raw = tok.Text.Replace("_", string.Empty);
            try
            {
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return ToJsNumberString(Convert.ToInt64(raw.Substring(2), 16));
                }
                if (raw.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                {
                    return ToJsNumberString(Convert.ToInt64(raw.Substring(2), 8));
                }
                if (raw.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                {
                    return ToJsNumberString(Convert.ToInt64(raw.Substring(2), 2));
                }
            }
            catch (FormatException) { }
            catch (OverflowException) { }
            return tok.Text;
        }

        throw new JsParserException($"Expected class member name, found '{tok.Text}'.");
    }

    private bool IsClassMemberNameStartAt(int offset)
    {
        if (IsPunctuatorAt(offset, "["))
        {
            return true;
        }

        var kind = PeekKind(offset);
        return kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.String or TokenKind.Number or TokenKind.PrivateIdentifier;
    }

    private bool IsPunctuatorAt(int offset, string text) => PeekIsPunctuator(offset, text);

    private bool IsIdentifierLikeAt(int offset)
    {
        var idx = Math.Min(_index + offset, _tokens.Count - 1);
        var tok = _tokens[idx];
        return tok.Kind == TokenKind.Identifier || tok.Kind == TokenKind.Keyword;
    }

    private TokenKind PeekKind(int offset)
    {
        var idx = Math.Min(_index + offset, _tokens.Count - 1);
        return _tokens[idx].Kind;
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

            parameters.Add(ParseBindingIdentifierOrPattern().Text);

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
                if (left is NumericLiteralExpressionNode && PeekIsPunctuator(1, "."))
                {
                    Advance();
                }

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

            if (Current().Kind == TokenKind.Template)
            {
                var template = ParseTemplateLiteral(Advance(), allowInvalidEscape: true);
                left = new TaggedTemplateExpressionNode(left, template, MergeSpan(left.Span, template.Span));
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

        if (token.Kind == TokenKind.Keyword && (token.Text == "typeof" || token.Text == "delete" || token.Text == "void" || token.Text == "await"))
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

        if (token.Kind == TokenKind.Keyword && token.Text == "async")
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

        if (token.Kind == TokenKind.Keyword && token.Text == "super")
        {
            Advance();
            return new SuperExpressionNode(token.Span);
        }

        if (token.Kind == TokenKind.Keyword && (token.Text == "true" || token.Text == "false"))
        {
            Advance();
            return new BooleanLiteralExpressionNode(token.Text == "true", token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "null")
        {
            Advance();
            return new NullLiteralExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Identifier)
        {
            if (!IsIdentifierLike(token))
            {
                throw new JsParserException($"Reserved word '{token.Text}' cannot be used as an identifier.");
            }

            Advance();
            return new IdentifierExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Number)
        {
            Advance();
            if (_strictMode && IsStrictModeForbiddenNumericLiteral(token.Text))
            {
                throw new JsParserException($"Numeric literal '{token.Text}' is not allowed in strict mode.");
            }

            if (!TryParseNumberLiteral(token.Text, out var value))
            {
                throw new JsParserException($"Invalid numeric literal '{token.Text}'.");
            }

            return new NumericLiteralExpressionNode(value, token.Text, token.Span);
        }

        if (token.Kind == TokenKind.String)
        {
            Advance();
            if (_strictMode && ContainsStrictModeForbiddenStringEscape(token.Text))
            {
                throw new JsParserException("Legacy string escape sequence is not allowed in strict mode.");
            }

            var raw = token.Text.Length >= 2 ? token.Text[1..^1] : string.Empty;
            var value = DecodeStringLiteralBody(raw);
            return new StringLiteralExpressionNode(value, token.Text, token.Span);
        }

        if (token.Kind == TokenKind.RegularExpression)
        {
            Advance();
            return new RegexLiteralExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Template)
        {
            return ParseTemplateLiteral(Advance());
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
        text = text.Replace("_", string.Empty, StringComparison.Ordinal);

        if (text.EndsWith('n'))
        {
            text = text[..^1];
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length > 2 && BigInteger.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                value = (double)hex;
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

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            value = (double)integer;
            return true;
        }

        return false;
    }

    private static bool IsStrictModeForbiddenNumericLiteral(string text)
    {
        var literal = text.EndsWith('n') ? text[..^1] : text;
        var normalized = literal.Replace("_", string.Empty, StringComparison.Ordinal);
        if (normalized.Length <= 1 || normalized[0] != '0')
        {
            return false;
        }

        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.Contains(".", StringComparison.Ordinal) ||
            normalized.Contains("e", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized[1] is >= '0' and <= '9';
    }

    private static bool TryParseRadix(string text, int radix, out double value)
    {
        var numeric = BigInteger.Zero;
        foreach (var ch in text)
        {
            if (ch == '_')
            {
                continue;
            }

            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => 10 + (ch - 'a'),
                >= 'A' and <= 'F' => 10 + (ch - 'A'),
                _ => -1
            };

            if (digit < 0 || digit >= radix)
            {
                value = 0;
                return false;
            }

            numeric = (numeric * radix) + digit;
        }

        value = (double)numeric;
        return true;
    }

    private FunctionExpressionNode ParseFunctionExpression()
    {
        Token start;
        var isAsync = false;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "async")
        {
            isAsync = true;
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

        var isGenerator = false;
        if (IsPunctuator("*"))
        {
            isGenerator = true;
            Advance();
        }

        string? name = null;
        if (IsIdentifierLike(Current()))
        {
            name = Advance().Text;
        }

        var parameters = ParseParameterList();
        var body = ParseBlockStatement();
        ValidateDirectivePrologueStrictStringEscapes(body.Statements);
        return new FunctionExpressionNode(
            name,
            parameters,
            body,
            MergeSpan(start.Span, body.Span),
            IsAsync: isAsync,
            IsGenerator: isGenerator);
    }

    private ExpressionNode ParseNewExpression()
    {
        var start = Advance(); // new
        if (IsPunctuator("."))
        {
            Advance();
            var property = ExpectIdentifier();
            if (property.Text != "target")
            {
                throw new JsParserException("Only 'new.target' is a valid meta property.");
            }
            return new NewTargetExpressionNode(MergeSpan(start.Span, property.Span));
        }

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
                else if (accessorKeyToken.Kind == TokenKind.Number)
                {
                    accessorKey = Advance().Text;
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

            if ((keyToken.Kind == TokenKind.Identifier || keyToken.Kind == TokenKind.Keyword) &&
                keyToken.Text == "async" &&
                IsAsyncGeneratorMethodPropertyStart())
            {
                var asyncStart = Advance(); // async
                Advance(); // *
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
                var methodFnName = methodKey ?? "async*";
                var asyncMethodFn = new FunctionExpressionNode(methodFnName, parameters, body, MergeSpan(asyncStart.Span, body.Span));
                properties.Add(new ObjectPropertyNode(methodKey, methodComputedKey, methodIsComputed, asyncMethodFn, asyncMethodFn.Span));
                if (IsPunctuator(","))
                {
                    Advance();
                    continue;
                }

                break;
            }

            if (IsPunctuator("*"))
            {
                var methodStart = Advance();
                string? methodKey = null;
                ExpressionNode? methodComputedKey = null;
                var methodIsComputed = false;
                var methodKeyToken = Current();
                if (IsPunctuator("["))
                {
                    Advance();
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
                var methodFnName = methodKey ?? "*";
                var methodFn = new FunctionExpressionNode(methodFnName, parameters, body, MergeSpan(methodStart.Span, body.Span));
                properties.Add(new ObjectPropertyNode(methodKey, methodComputedKey, methodIsComputed, methodFn, methodFn.Span));
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
            if (IsPunctuator(","))
            {
                var comma = Advance();
                elements.Add(new IdentifierExpressionNode("undefined", comma.Span));
                continue;
            }

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

            if (IsIdentifierLike(Current()) && PeekIsPunctuator(1, "=") && PeekIsPunctuator(2, ">"))
            {
                var parameter = Advance().Text;
                Advance(); // =
                Advance(); // >
                expression = ParseArrowFunctionBody(new[] { parameter }, _tokens[saved].Span, isAsync: true);
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
                        if (IsPunctuator("..."))
                        {
                            Advance();
                        }
                        if (!(IsIdentifierLike(Current()) || IsPunctuator("[") || IsPunctuator("{")))
                        {
                            asyncValid = false;
                            break;
                        }

                        asyncParameters.Add(ParseBindingIdentifierOrPattern().Text);
                        if (IsPunctuator("=") && !PeekIsPunctuator(1, ">"))
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
                expression = ParseArrowFunctionBody(asyncParameters, _tokens[saved].Span, isAsync: true);
                return true;
            }

            _index = saved;
        }

        if (IsIdentifierLike(Current()) && PeekIsPunctuator(1, "=") && PeekIsPunctuator(2, ">"))
        {
            var parameter = Advance().Text;
            Advance(); // =
            Advance(); // >
            expression = ParseArrowFunctionBody(new[] { parameter }, _tokens[saved].Span, isAsync: false);
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
                    if (IsPunctuator("..."))
                    {
                        Advance();
                    }
                    if (!(IsIdentifierLike(Current()) || IsPunctuator("[") || IsPunctuator("{")))
                    {
                        valid = false;
                        break;
                    }

                    parameters.Add(ParseBindingIdentifierOrPattern().Text);
                    if (IsPunctuator("=") && !PeekIsPunctuator(1, ">"))
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
            expression = ParseArrowFunctionBody(parameters, _tokens[saved].Span, isAsync: false);
            return true;
        }

        return false;
    }

    private ArrowFunctionExpressionNode ParseArrowFunctionBody(
        IReadOnlyList<string> parameters,
        SourceSpan start,
        bool isAsync)
    {
        if (IsPunctuator("{"))
        {
            var block = ParseBlockStatement();
            ValidateDirectivePrologueStrictStringEscapes(block.Statements);
            return new ArrowFunctionExpressionNode(parameters, block, null, MergeSpan(start, block.Span), IsAsync: isAsync);
        }

        var bodyExpression = ParseExpression(2);
        return new ArrowFunctionExpressionNode(parameters, null, bodyExpression, MergeSpan(start, bodyExpression.Span), IsAsync: isAsync);
    }

    private IReadOnlyList<ExpressionNode> ParseCallArguments()
    {
        var args = new List<ExpressionNode>();
        ExpectPunctuator("(");
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator(")"))
        {
            if (IsPunctuator("..."))
            {
                var spread = Advance();
                var argument = ParseExpression(2);
                args.Add(new SpreadElementExpressionNode(argument, MergeSpan(spread.Span, argument.Span)));
            }
            else
            {
                args.Add(ParseExpression(2));
            }

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

            if (Current().Kind == TokenKind.Template)
            {
                var template = ParseTemplateLiteral(Advance(), allowInvalidEscape: true);
                left = new TaggedTemplateExpressionNode(left, template, MergeSpan(left.Span, template.Span));
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

    private TemplateLiteralExpressionNode ParseTemplateLiteral(Token token, bool allowInvalidEscape = false)
    {
        if (token.ContainsInvalidEscape && !allowInvalidEscape)
        {
            throw new JsParserException("Invalid escape sequence in untagged template literal.");
        }

        var raw = token.Text;
        if (raw.Length < 2 || raw[0] != '`' || raw[^1] != '`')
        {
            throw new JsParserException("Unterminated template literal.");
        }

        var quasis = new List<string>();
        var expressions = new List<ExpressionNode>();
        var segmentStart = 1;
        var index = 1;
        while (index < raw.Length - 1)
        {
            var ch = raw[index];
            if (ch == '\\')
            {
                index = Math.Min(raw.Length - 1, index + 2);
                continue;
            }

            if (ch == '$' && index + 1 < raw.Length - 1 && raw[index + 1] == '{')
            {
                quasis.Add(raw[segmentStart..index]);
                var expressionStart = index + 2;
                var expressionEnd = FindTemplateExpressionEnd(raw, expressionStart);
                if (expressionEnd < 0)
                {
                    throw new JsParserException("Unterminated template substitution expression.");
                }

                var expressionText = raw[expressionStart..expressionEnd];
                expressions.Add(ParseTemplateSubstitutionExpression(expressionText, token.Span));
                index = expressionEnd + 1;
                segmentStart = index;
                continue;
            }

            index++;
        }

        quasis.Add(raw[segmentStart..^1]);
        return new TemplateLiteralExpressionNode(quasis, expressions, token.Span);
    }

    private ExpressionNode ParseTemplateSubstitutionExpression(string expressionText, SourceSpan templateSpan)
    {
        if (string.IsNullOrWhiteSpace(expressionText))
        {
            throw new JsParserException("Template substitution expression cannot be empty.");
        }

        var tokens = new JsLexer(new SourceText(expressionText, "<template>")).LexAll();
        var parser = new JsParser(tokens)
        {
            _strictMode = _strictMode,
            _moduleMode = _moduleMode,
            _inDirectivePrologue = false
        };
        var expression = parser.ParseExpression(0);
        if (!parser.Is(TokenKind.EndOfFile))
        {
            throw new JsParserException($"Unexpected token in template substitution at {templateSpan.Line}:{templateSpan.Column}.");
        }

        return expression;
    }

    private static int FindTemplateExpressionEnd(string raw, int start)
    {
        var depth = 1;
        for (var i = start; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\'' || ch == '"')
            {
                i = SkipQuotedRaw(raw, i, ch);
                if (i < 0)
                {
                    return -1;
                }

                i--;
                continue;
            }

            if (ch == '`')
            {
                i = SkipRawTemplateLiteral(raw, i);
                if (i < 0)
                {
                    return -1;
                }

                i--;
                continue;
            }

            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int SkipRawTemplateLiteral(string raw, int start)
    {
        for (var i = start + 1; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '`')
            {
                return i + 1;
            }

            if (ch == '$' && i + 1 < raw.Length && raw[i + 1] == '{')
            {
                var end = FindTemplateExpressionEnd(raw, i + 2);
                if (end < 0)
                {
                    return -1;
                }

                i = end;
            }
        }

        return -1;
    }

    private static int SkipQuotedRaw(string raw, int start, char quote)
    {
        for (var i = start + 1; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == quote)
            {
                return i + 1;
            }
        }

        return -1;
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
            case "<<":
            case ">>":
            case ">>>":
                leftBindingPower = 18;
                rightBindingPower = 19;
                return true;
            case "*":
            case "%":
            case "/":
                leftBindingPower = 30;
                rightBindingPower = 31;
                return true;
            case "**":
                leftBindingPower = 32;
                rightBindingPower = 32;
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
        if (Current().Kind != TokenKind.Identifier &&
            Current().Kind != TokenKind.Keyword &&
            Current().Kind != TokenKind.PrivateIdentifier)
        {
            throw new JsParserException($"Expected identifier, found '{Current().Text}'.");
        }

        return Advance();
    }

    private bool IsIdentifierLike(Token token)
    {
        if (token.Kind == TokenKind.Keyword)
        {
            return token.Text == "async" || (token.Text == "await" && !_moduleMode);
        }

        if (token.Kind != TokenKind.Identifier)
        {
            return false;
        }

        if (AlwaysReservedIdentifierNames.Contains(token.Text))
        {
            return false;
        }

        if (_moduleMode && string.Equals(token.Text, "await", StringComparison.Ordinal))
        {
            return false;
        }

        return !_strictMode || !StrictModeReservedIdentifierNames.Contains(token.Text);
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

        var isSimpleName = next.Kind == TokenKind.Identifier || next.Kind == TokenKind.Keyword || next.Kind == TokenKind.String || next.Kind == TokenKind.Number;
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

    private bool IsAsyncGeneratorMethodPropertyStart()
    {
        var current = Current();
        if (!(current.Kind == TokenKind.Identifier || current.Kind == TokenKind.Keyword) || current.Text != "async")
        {
            return false;
        }

        if (!(PeekIsPunctuator(1, "*")))
        {
            return false;
        }

        var keyIndex = Math.Min(_index + 2, _tokens.Count - 1);
        var next = _tokens[keyIndex];
        if (next.Kind == TokenKind.Punctuator && next.Text == "[")
        {
            var depth = 1;
            var scan = keyIndex + 1;
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

        var afterName = Math.Min(keyIndex + 1, _tokens.Count - 1);
        return _tokens[afterName].Kind == TokenKind.Punctuator && _tokens[afterName].Text == "(";
    }
}
