using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Lexer;
using FenBrowser.Js.Regex;
using FenBrowser.Js.Source;
using System.Globalization;
using System.Numerics;

namespace FenBrowser.Js.Parser;

public sealed class JsParser
{
    private readonly record struct ParameterListInfo(
        IReadOnlyList<string> Parameters,
        IReadOnlyList<BindingPatternNode?> ParameterBindings,
        IReadOnlyList<ExpressionNode?> ParameterDefaults,
        bool IsSimple,
        bool HasDuplicateNames,
        bool RestHasInitializer,
        bool HasTrailingCommaAfterRest,
        bool HasSuperCallInInitializers,
        bool HasYieldReferenceInInitializers,
        bool HasAwaitReferenceInInitializers,
        int RestParameterIndex);

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
    // True only while parsing a statement that sits directly at the top level of
    // a Module. import/export declarations are a SyntaxError anywhere else
    // (ECMA-262 16.2.1.1 / 16.2.2.1 — they are ModuleItems, not Statements).
    private bool _atModuleTopLevel;
    private bool _inDirectivePrologue = true;
    private bool _allowYieldExpression;
    private bool _allowAwaitExpression;
    private bool _allowAnnexBForInInitializerTail;
    // ECMA-262 B.3.1: object literals with duplicate `__proto__:` setters whose
    // pattern-vs-literal fate is not yet known. Removed when validated as an
    // assignment pattern; any left at end of parse is a SyntaxError.
    private readonly List<ObjectLiteralExpressionNode> _pendingDuplicateProtoLiterals = new();
    private readonly List<ObjectLiteralExpressionNode> _pendingCoverInitializedNameLiterals = new();
    private int _classStaticBlockDepth;
    // ECMA-262 grammar parameter [~In]: while true, the `in` keyword is NOT
    // treated as a relational operator so the `for ( LHS in Iterable )` head can
    // recognise `in` as the for-in marker even when LHS is a binary/assignment
    // chain (e.g. `for (n in M = !0, g())`). Reset to false (allow `in`) at every
    // parenthesised / bracketed boundary where the grammar restores [+In].
    private bool _noIn;

    // Recursive-descent depth guard. Deeply nested source (e.g. thousands of
    // open parens or nested blocks) would otherwise exhaust the native call
    // stack and crash the process with an uncatchable StackOverflowException —
    // a denial-of-service hazard on untrusted input. Throw a structured
    // JsParserException once nesting passes a conservative bound that trips
    // well before the native stack is exhausted on the smallest stack we run
    // on (xUnit worker threads overflow around ~200 nested parens). 128 levels
    // is far deeper than any realistic hand-written or generated program nests.
    private const int MaxRecursionDepth = 128;
    private int _recursionDepth;

    private void EnterRecursion()
    {
        if (++_recursionDepth > MaxRecursionDepth)
        {
            throw new JsParserException("Maximum parser nesting depth exceeded.");
        }
    }

    private void ExitRecursion()
    {
        _recursionDepth--;
    }

    private JsParser(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens;
    }

    public static ProgramNode ParseScript(SourceText source)
    {
        return ParseScript(source, inheritedStrictMode: false);
    }

    // ECMA-262 19.2.1.1: eval inherits the strictness of its calling context.
    // When inheritedStrictMode is true, the parser starts in strict mode so
    // early-error checks (e.g. 'var arguments' / 'var eval') fire during
    // parsing — before compilation ever sees the AST.
    public static ProgramNode ParseScript(SourceText source, bool inheritedStrictMode)
    {
        var tokens = new JsLexer(source).LexAll();
        var parser = new JsParser(tokens);
        return parser.ParseProgram(ProgramKind.Script, inheritedStrictMode);
    }

    public static ProgramNode ParseModule(SourceText source)
    {
        var tokens = new JsLexer(source).LexAll();
        var parser = new JsParser(tokens);
        return parser.ParseProgram(ProgramKind.Module);
    }

    // Parses the body of a dynamically-created function (the Function/
    // GeneratorFunction/AsyncFunction constructor) as a FunctionBody, so a
    // top-level `return` inside it is valid (ECMA-262 20.2.1.1.1).
    public static ProgramNode ParseFunctionBody(SourceText source)
    {
        var tokens = new JsLexer(source).LexAll();
        var parser = new JsParser(tokens) { _functionBodyDepth = 1 };
        return parser.ParseProgram(ProgramKind.Script);
    }

    private ProgramNode ParseProgram(ProgramKind kind, bool inheritedStrictMode = false)
    {
        _moduleMode = kind == ProgramKind.Module;
        _strictMode = kind == ProgramKind.Module || inheritedStrictMode;
        _allowYieldExpression = false;
        _allowAwaitExpression = true;
        var statements = new List<StatementNode>();
        var start = Current().Span;

        while (!Is(TokenKind.EndOfFile))
        {
            _atModuleTopLevel = true;
            var statement = ParseStatement();
            statements.Add(statement);
            UpdateDirectivePrologueState(statement);
        }

        var end = Current().Span;
        var span = new SourceSpan(start.Start, Math.Max(0, end.Start - start.Start), start.Line, start.Column);
        // B.3.1: any object literal with duplicate `__proto__:` setters that was
        // not consumed as an assignment pattern is a real ObjectLiteral — an error.
        if (_pendingDuplicateProtoLiterals.Count > 0)
        {
            throw new JsParserException(
                "Duplicate __proto__ fields are not allowed in object literals.");
        }

        // ECMA-262 12.2.6.1: CoverInitializedName (e.g. `{ a = 1 }`) is only
        // valid as cover grammar for destructuring assignment patterns, never
        // in a real object literal.
        if (_pendingCoverInitializedNameLiterals.Count > 0)
        {
            throw new JsParserException(
                "CoverInitializedName is not valid in an object literal.");
        }

        ValidateDirectivePrologueStrictStringEscapes(statements);
        var program = new ProgramNode(kind, statements, span);
        // ECMA-262 lexical-declaration early errors (duplicate let/const/class, or a
        // lexical name clashing with a var/function in the same scope). Run at parse
        // time so these surface as SyntaxError.
        LexicalDeclarationChecker.Check(program);
        ModuleDeclarationChecker.Check(program);
        return program;
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

    private static bool ContainsUseStrictDirective(IReadOnlyList<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is not ExpressionStatementNode expressionStatement ||
                expressionStatement.Expression is not StringLiteralExpressionNode stringLiteral)
            {
                return false;
            }

            if (string.Equals(stringLiteral.Value, "use strict", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRestrictedIdentifier(string name, bool forbidAwaitIdentifier, bool forbidYieldIdentifier)
    {
        if (forbidAwaitIdentifier && string.Equals(name, "await", StringComparison.Ordinal))
        {
            return true;
        }

        return forbidYieldIdentifier && string.Equals(name, "yield", StringComparison.Ordinal);
    }

    private static bool IsSyntheticPatternBinding(string name) =>
        name.StartsWith("__pattern", StringComparison.Ordinal);

    private static bool ContainsSuperCallInStatements(IReadOnlyList<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            if (ContainsSuperCallInStatement(statement))
            {
                return true;
            }
        }

        return false;
    }

    // Minified bundles produce deeply left-nested expression chains (e.g. a||b||c||…
    // thousands deep) that the precedence-climbing parser builds iteratively without
    // growing the stack, but a naive recursive AST walk would blow it. Bound the
    // early-error super-call scan: failing to flag an illegal super() buried thousands
    // of levels deep is harmless (it cannot occur in hand-written code), whereas a
    // StackOverflowException is an uncatchable process kill.
    [ThreadStatic] private static int _superCallScanDepth;
    private const int MaxSuperCallScanDepth = 400;

    private static bool ContainsSuperCallInStatement(StatementNode statement)
    {
        if (++_superCallScanDepth > MaxSuperCallScanDepth)
        {
            _superCallScanDepth--;
            return false;
        }

        try
        {
            return ContainsSuperCallInStatementCore(statement);
        }
        finally
        {
            _superCallScanDepth--;
        }
    }

    private static bool ContainsSuperCallInStatementCore(StatementNode statement) =>
        statement switch
        {
            BlockStatementNode block => ContainsSuperCallInStatements(block.Statements),
            ExpressionStatementNode expressionStatement => ContainsSuperCallInExpression(expressionStatement.Expression),
            LabeledStatementNode labeled => ContainsSuperCallInStatement(labeled.Body),
            VariableDeclarationStatementNode declaration => declaration.Declarators.Any(d => d.Initializer is not null && ContainsSuperCallInExpression(d.Initializer)),
            IfStatementNode ifStatement => ContainsSuperCallInExpression(ifStatement.Test) ||
                                           ContainsSuperCallInStatement(ifStatement.Consequent) ||
                                           (ifStatement.Alternate is not null && ContainsSuperCallInStatement(ifStatement.Alternate)),
            WhileStatementNode whileStatement => ContainsSuperCallInExpression(whileStatement.Test) ||
                                                ContainsSuperCallInStatement(whileStatement.Body),
            ForStatementNode forStatement => (forStatement.Initializer is not null && ContainsSuperCallInStatement(forStatement.Initializer)) ||
                                             (forStatement.Test is not null && ContainsSuperCallInExpression(forStatement.Test)) ||
                                             (forStatement.Update is not null && ContainsSuperCallInExpression(forStatement.Update)) ||
                                             ContainsSuperCallInStatement(forStatement.Body),
            ForInStatementNode forInStatement => ContainsSuperCallInStatement(forInStatement.Initializer) ||
                                                 ContainsSuperCallInExpression(forInStatement.Iterable) ||
                                                 ContainsSuperCallInStatement(forInStatement.Body),
            ForOfStatementNode forOfStatement => ContainsSuperCallInStatement(forOfStatement.Initializer) ||
                                                 ContainsSuperCallInExpression(forOfStatement.Iterable) ||
                                                 ContainsSuperCallInStatement(forOfStatement.Body),
            ForAwaitOfStatementNode forAwaitOfStatement => ContainsSuperCallInStatement(forAwaitOfStatement.Initializer) ||
                                                 ContainsSuperCallInExpression(forAwaitOfStatement.Iterable) ||
                                                 ContainsSuperCallInStatement(forAwaitOfStatement.Body),
            ReturnStatementNode returnStatement => returnStatement.Argument is not null && ContainsSuperCallInExpression(returnStatement.Argument),
            ThrowStatementNode throwStatement => ContainsSuperCallInExpression(throwStatement.Argument),
            TryCatchStatementNode tryCatch => ContainsSuperCallInStatement(tryCatch.TryBlock) ||
                                              ContainsSuperCallInStatement(tryCatch.CatchBlock),
            TryFinallyStatementNode tryFinally => ContainsSuperCallInStatement(tryFinally.TryBlock) ||
                                                  ContainsSuperCallInStatement(tryFinally.FinallyBlock),
            TryCatchFinallyStatementNode tryCatchFinally => ContainsSuperCallInStatement(tryCatchFinally.TryBlock) ||
                                                            ContainsSuperCallInStatement(tryCatchFinally.CatchBlock) ||
                                                            ContainsSuperCallInStatement(tryCatchFinally.FinallyBlock),
            SwitchStatementNode switchStatement => ContainsSuperCallInExpression(switchStatement.Discriminant) ||
                                                   switchStatement.Cases.Any(c => (c.Test is not null && ContainsSuperCallInExpression(c.Test)) || ContainsSuperCallInStatements(c.Consequent)),
            _ => false
        };

    private static bool ContainsSuperCallInExpression(ExpressionNode expression)
    {
        if (++_superCallScanDepth > MaxSuperCallScanDepth)
        {
            _superCallScanDepth--;
            return false;
        }

        try
        {
            return ContainsSuperCallInExpressionCore(expression);
        }
        finally
        {
            _superCallScanDepth--;
        }
    }

    // ECMA-262: SuperCall (`super(...)`) is only valid in class constructors.
    // SuperProperty (`super.x`, `super[x]`) is valid in all class methods but
    // invalid in regular functions and object methods. We use two separate
    // predicates so class methods (non-constructor) can reject SuperCall while
    // allowing SuperProperty.
    private static bool ContainsSuperCallOnlyInStatements(IReadOnlyList<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            if (ContainsSuperCallOnlyInStatement(statement))
                return true;
        }
        return false;
    }

    private static bool ContainsSuperCallOnlyInStatement(StatementNode statement)
    {
        if (++_superCallScanDepth > MaxSuperCallScanDepth) { _superCallScanDepth--; return false; }
        try
        {
            return statement switch
            {
                BlockStatementNode block => ContainsSuperCallOnlyInStatements(block.Statements),
                ExpressionStatementNode es => ContainsSuperCallOnlyInExpression(es.Expression),
                LabeledStatementNode labeled => ContainsSuperCallOnlyInStatement(labeled.Body),
                VariableDeclarationStatementNode decl => decl.Declarators.Any(d => d.Initializer is not null && ContainsSuperCallOnlyInExpression(d.Initializer)),
                IfStatementNode ifStmt => ContainsSuperCallOnlyInExpression(ifStmt.Test) || ContainsSuperCallOnlyInStatement(ifStmt.Consequent) || (ifStmt.Alternate is not null && ContainsSuperCallOnlyInStatement(ifStmt.Alternate)),
                WhileStatementNode whileStmt => ContainsSuperCallOnlyInExpression(whileStmt.Test) || ContainsSuperCallOnlyInStatement(whileStmt.Body),
                ForStatementNode forStmt => (forStmt.Initializer is not null && ContainsSuperCallOnlyInStatement(forStmt.Initializer)) || (forStmt.Test is not null && ContainsSuperCallOnlyInExpression(forStmt.Test)) || (forStmt.Update is not null && ContainsSuperCallOnlyInExpression(forStmt.Update)) || ContainsSuperCallOnlyInStatement(forStmt.Body),
                ForInStatementNode forIn => ContainsSuperCallOnlyInStatement(forIn.Initializer) || ContainsSuperCallOnlyInExpression(forIn.Iterable) || ContainsSuperCallOnlyInStatement(forIn.Body),
                ForOfStatementNode forOf => ContainsSuperCallOnlyInStatement(forOf.Initializer) || ContainsSuperCallOnlyInExpression(forOf.Iterable) || ContainsSuperCallOnlyInStatement(forOf.Body),
                ForAwaitOfStatementNode forAwait => ContainsSuperCallOnlyInStatement(forAwait.Initializer) || ContainsSuperCallOnlyInExpression(forAwait.Iterable) || ContainsSuperCallOnlyInStatement(forAwait.Body),
                ReturnStatementNode ret => ret.Argument is not null && ContainsSuperCallOnlyInExpression(ret.Argument),
                ThrowStatementNode thr => ContainsSuperCallOnlyInExpression(thr.Argument),
                TryCatchStatementNode tc => ContainsSuperCallOnlyInStatement(tc.TryBlock) || ContainsSuperCallOnlyInStatement(tc.CatchBlock),
                TryFinallyStatementNode tf => ContainsSuperCallOnlyInStatement(tf.TryBlock) || ContainsSuperCallOnlyInStatement(tf.FinallyBlock),
                TryCatchFinallyStatementNode tcf => ContainsSuperCallOnlyInStatement(tcf.TryBlock) || ContainsSuperCallOnlyInStatement(tcf.CatchBlock) || ContainsSuperCallOnlyInStatement(tcf.FinallyBlock),
                SwitchStatementNode sw => sw.Cases.Any(c => (c.Test is not null && ContainsSuperCallOnlyInExpression(c.Test)) || ContainsSuperCallOnlyInStatements(c.Consequent)),
                _ => false
            };
        }
        finally { _superCallScanDepth--; }
    }

    private static bool ContainsSuperCallOnlyInExpression(ExpressionNode expression)
    {
        if (++_superCallScanDepth > MaxSuperCallScanDepth) { _superCallScanDepth--; return false; }
        try
        {
            return expression switch
            {
                // Only flag `super(...)` calls — NOT `super.x` / `super[x]`.
                CallExpressionNode { Callee: SuperExpressionNode } => true,
                OptionalCallExpressionNode { Callee: SuperExpressionNode } => true,
                ParenthesizedExpressionNode p => ContainsSuperCallOnlyInExpression(p.Expression),
                BinaryExpressionNode bin => ContainsSuperCallOnlyInExpression(bin.Left) || ContainsSuperCallOnlyInExpression(bin.Right),
                AssignmentExpressionNode assign => ContainsSuperCallOnlyInExpression(assign.Left) || ContainsSuperCallOnlyInExpression(assign.Right),
                CallExpressionNode call => ContainsSuperCallOnlyInExpression(call.Callee) || call.Arguments.Any(ContainsSuperCallOnlyInExpression),
                OptionalCallExpressionNode optCall => ContainsSuperCallOnlyInExpression(optCall.Callee) || optCall.Arguments.Any(ContainsSuperCallOnlyInExpression),
                ObjectLiteralExpressionNode objLit => objLit.Properties.Any(p => (p.ComputedKey is not null && ContainsSuperCallOnlyInExpression(p.ComputedKey)) || ContainsSuperCallOnlyInExpression(p.Value)),
                ArrayLiteralExpressionNode arrLit => arrLit.Elements.Any(ContainsSuperCallOnlyInExpression),
                SpreadElementExpressionNode spread => ContainsSuperCallOnlyInExpression(spread.Argument),
                MemberExpressionNode member => ContainsSuperCallOnlyInExpression(member.Object) || (member.PropertyExpression is not null && ContainsSuperCallOnlyInExpression(member.PropertyExpression)),
                OptionalMemberExpressionNode optMember => ContainsSuperCallOnlyInExpression(optMember.Object) || (optMember.PropertyExpression is not null && ContainsSuperCallOnlyInExpression(optMember.PropertyExpression)),
                UnaryExpressionNode unary => ContainsSuperCallOnlyInExpression(unary.Operand),
                ConditionalExpressionNode cond => ContainsSuperCallOnlyInExpression(cond.Test) || ContainsSuperCallOnlyInExpression(cond.Consequent) || ContainsSuperCallOnlyInExpression(cond.Alternate),
                NewExpressionNode n => ContainsSuperCallOnlyInExpression(n.Callee) || n.Arguments.Any(ContainsSuperCallOnlyInExpression),
                TemplateLiteralExpressionNode tl => tl.Expressions.Any(ContainsSuperCallOnlyInExpression),
                TaggedTemplateExpressionNode tt => ContainsSuperCallOnlyInExpression(tt.Tag) || ContainsSuperCallOnlyInExpression(tt.Template),
                ImportCallExpressionNode ic => ContainsSuperCallOnlyInExpression(ic.Specifier),
                ImportSourceExpressionNode iSrc => ContainsSuperCallOnlyInExpression(iSrc.Specifier),
                ImportDeferExpressionNode iDef => ContainsSuperCallOnlyInExpression(iDef.Specifier),
                _ => false
            };
        }
        finally { _superCallScanDepth--; }
    }

    private static bool ContainsSuperCallInExpressionCore(ExpressionNode expression) =>
        expression switch
        {
            // super() and super.property — both are illegal outside method context
            SuperExpressionNode => true,
            CallExpressionNode { Callee: SuperExpressionNode } => true,
            ParenthesizedExpressionNode parenthesized => ContainsSuperCallInExpression(parenthesized.Expression),
            BinaryExpressionNode binary => ContainsSuperCallInExpression(binary.Left) || ContainsSuperCallInExpression(binary.Right),
            AssignmentExpressionNode assignment => ContainsSuperCallInExpression(assignment.Left) || ContainsSuperCallInExpression(assignment.Right),
            CallExpressionNode call => ContainsSuperCallInExpression(call.Callee) || call.Arguments.Any(ContainsSuperCallInExpression),
            OptionalCallExpressionNode optionalCall => ContainsSuperCallInExpression(optionalCall.Callee) || optionalCall.Arguments.Any(ContainsSuperCallInExpression),
            ObjectLiteralExpressionNode objectLiteral => objectLiteral.Properties.Any(p =>
                (p.ComputedKey is not null && ContainsSuperCallInExpression(p.ComputedKey)) || ContainsSuperCallInExpression(p.Value)),
            ArrayLiteralExpressionNode arrayLiteral => arrayLiteral.Elements.Any(ContainsSuperCallInExpression),
            SpreadElementExpressionNode spread => ContainsSuperCallInExpression(spread.Argument),
            MemberExpressionNode member => ContainsSuperCallInExpression(member.Object) ||
                                           (member.PropertyExpression is not null && ContainsSuperCallInExpression(member.PropertyExpression)),
            OptionalMemberExpressionNode optionalMember => ContainsSuperCallInExpression(optionalMember.Object) ||
                                           (optionalMember.PropertyExpression is not null && ContainsSuperCallInExpression(optionalMember.PropertyExpression)),
            UnaryExpressionNode unary => ContainsSuperCallInExpression(unary.Operand),
            ConditionalExpressionNode conditional => ContainsSuperCallInExpression(conditional.Test) ||
                                                     ContainsSuperCallInExpression(conditional.Consequent) ||
                                                     ContainsSuperCallInExpression(conditional.Alternate),
            NewExpressionNode @new => ContainsSuperCallInExpression(@new.Callee) || @new.Arguments.Any(ContainsSuperCallInExpression),
            TemplateLiteralExpressionNode template => template.Expressions.Any(ContainsSuperCallInExpression),
            TaggedTemplateExpressionNode taggedTemplate => ContainsSuperCallInExpression(taggedTemplate.Tag) ||
                                                           ContainsSuperCallInExpression(taggedTemplate.Template),
            _ => false
        };

    private static bool ContainsIdentifierReferenceInExpression(ExpressionNode expression, string name) =>
        expression switch
        {
            IdentifierExpressionNode identifier => string.Equals(identifier.Name, name, StringComparison.Ordinal),
            ParenthesizedExpressionNode parenthesized => ContainsIdentifierReferenceInExpression(parenthesized.Expression, name),
            BinaryExpressionNode binary => ContainsIdentifierReferenceInExpression(binary.Left, name) ||
                                           ContainsIdentifierReferenceInExpression(binary.Right, name),
            AssignmentExpressionNode assignment => ContainsIdentifierReferenceInExpression(assignment.Left, name) ||
                                                   ContainsIdentifierReferenceInExpression(assignment.Right, name),
            CallExpressionNode call => ContainsIdentifierReferenceInExpression(call.Callee, name) ||
                                       call.Arguments.Any(a => ContainsIdentifierReferenceInExpression(a, name)),
            OptionalCallExpressionNode optionalCall => ContainsIdentifierReferenceInExpression(optionalCall.Callee, name) ||
                                       optionalCall.Arguments.Any(a => ContainsIdentifierReferenceInExpression(a, name)),
            ObjectLiteralExpressionNode objectLiteral => objectLiteral.Properties.Any(p =>
                (p.ComputedKey is not null && ContainsIdentifierReferenceInExpression(p.ComputedKey, name)) ||
                ContainsIdentifierReferenceInExpression(p.Value, name)),
            ArrayLiteralExpressionNode arrayLiteral => arrayLiteral.Elements.Any(e => ContainsIdentifierReferenceInExpression(e, name)),
            SpreadElementExpressionNode spread => ContainsIdentifierReferenceInExpression(spread.Argument, name),
            MemberExpressionNode member => ContainsIdentifierReferenceInExpression(member.Object, name) ||
                                           (member.PropertyExpression is not null && ContainsIdentifierReferenceInExpression(member.PropertyExpression, name)),
            OptionalMemberExpressionNode optionalMember => ContainsIdentifierReferenceInExpression(optionalMember.Object, name) ||
                                           (optionalMember.PropertyExpression is not null && ContainsIdentifierReferenceInExpression(optionalMember.PropertyExpression, name)),
            UnaryExpressionNode unary => ContainsIdentifierReferenceInExpression(unary.Operand, name),
            ConditionalExpressionNode conditional => ContainsIdentifierReferenceInExpression(conditional.Test, name) ||
                                                     ContainsIdentifierReferenceInExpression(conditional.Consequent, name) ||
                                                     ContainsIdentifierReferenceInExpression(conditional.Alternate, name),
            NewExpressionNode @new => ContainsIdentifierReferenceInExpression(@new.Callee, name) ||
                                      @new.Arguments.Any(a => ContainsIdentifierReferenceInExpression(a, name)),
            TemplateLiteralExpressionNode template => template.Expressions.Any(e => ContainsIdentifierReferenceInExpression(e, name)),
            TaggedTemplateExpressionNode taggedTemplate => ContainsIdentifierReferenceInExpression(taggedTemplate.Tag, name) ||
                                                           ContainsIdentifierReferenceInExpression(taggedTemplate.Template, name),
            _ => false
        };

    private static bool ContainsYieldReferenceInExpression(ExpressionNode expression) =>
        expression switch
        {
            IdentifierExpressionNode identifier => string.Equals(identifier.Name, "yield", StringComparison.Ordinal),
            UnaryExpressionNode unary when unary.Operator is "yield" or "yield*" => true,
            ParenthesizedExpressionNode parenthesized => ContainsYieldReferenceInExpression(parenthesized.Expression),
            BinaryExpressionNode binary => ContainsYieldReferenceInExpression(binary.Left) || ContainsYieldReferenceInExpression(binary.Right),
            AssignmentExpressionNode assignment => ContainsYieldReferenceInExpression(assignment.Left) || ContainsYieldReferenceInExpression(assignment.Right),
            CallExpressionNode call => ContainsYieldReferenceInExpression(call.Callee) || call.Arguments.Any(ContainsYieldReferenceInExpression),
            OptionalCallExpressionNode optionalCall => ContainsYieldReferenceInExpression(optionalCall.Callee) || optionalCall.Arguments.Any(ContainsYieldReferenceInExpression),
            ObjectLiteralExpressionNode objectLiteral => objectLiteral.Properties.Any(p =>
                (p.ComputedKey is not null && ContainsYieldReferenceInExpression(p.ComputedKey)) || ContainsYieldReferenceInExpression(p.Value)),
            ArrayLiteralExpressionNode arrayLiteral => arrayLiteral.Elements.Any(ContainsYieldReferenceInExpression),
            SpreadElementExpressionNode spread => ContainsYieldReferenceInExpression(spread.Argument),
            MemberExpressionNode member => ContainsYieldReferenceInExpression(member.Object) ||
                                           (member.PropertyExpression is not null && ContainsYieldReferenceInExpression(member.PropertyExpression)),
            OptionalMemberExpressionNode optionalMember => ContainsYieldReferenceInExpression(optionalMember.Object) ||
                                           (optionalMember.PropertyExpression is not null && ContainsYieldReferenceInExpression(optionalMember.PropertyExpression)),
            ConditionalExpressionNode conditional => ContainsYieldReferenceInExpression(conditional.Test) ||
                                                     ContainsYieldReferenceInExpression(conditional.Consequent) ||
                                                     ContainsYieldReferenceInExpression(conditional.Alternate),
            NewExpressionNode @new => ContainsYieldReferenceInExpression(@new.Callee) || @new.Arguments.Any(ContainsYieldReferenceInExpression),
            TemplateLiteralExpressionNode template => template.Expressions.Any(ContainsYieldReferenceInExpression),
            TaggedTemplateExpressionNode taggedTemplate => ContainsYieldReferenceInExpression(taggedTemplate.Tag) ||
                                                           ContainsYieldReferenceInExpression(taggedTemplate.Template),
            _ => false
        };

    private static HashSet<string> CollectTopLevelLexicallyDeclaredNames(IReadOnlyList<StatementNode> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case VariableDeclarationStatementNode declaration when declaration.Kind is "let" or "const":
                    foreach (var declarator in declaration.Declarators)
                    {
                        names.Add(declarator.Identifier);
                    }

                    break;
                case ClassDeclarationNode classDeclaration:
                    names.Add(classDeclaration.Name);
                    break;
                // ECMA-262 14.2.1 / Annex B.3.1.1: function declarations inside blocks
                // are lexically scoped and count as LexicallyDeclaredNames for early
                // error duplicate detection.
                case FunctionDeclarationNode funcDecl:
                    names.Add(funcDecl.Name);
                    break;
            }
        }

        return names;
    }

    // ECMA-262 14.2.1 Block Static Semantics: Early Errors.
    // Also incorporates Annex B.3.1.1 sloppy-mode exceptions.
    private void ValidateBlockEarlyErrors(IReadOnlyList<StatementNode> statements)
    {
        ValidateBlockLexicalVarConflicts(statements);
        // Check for duplicate lexical names (including function declarations).
        // In strict mode, any duplicate is an error. In sloppy mode, Annex
        // B.3.1.1 allows duplicate FunctionDeclarations whose BoundNames are
        // all in VarDeclaredNames.
        // Track not just whether we've seen a name, but ALSO whether any
        // prior declaration for that name was async/generator. Annex B.3.1.1
        // only permits duplicate *ordinary* function declarations.
        var seenFuncNames = new HashSet<string>(StringComparer.Ordinal);
        var nonOrdinaryFuncNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in statements)
        {
            if (stmt is FunctionDeclarationNode func)
            {
                if (!seenFuncNames.Add(func.Name))
                {
                    // In strict mode, any duplicate is an error. In sloppy
                    // mode, duplicates are only OK when ALL occurrences are
                    // ordinary (non-async, non-generator) function declarations.
                    if (_strictMode || func.IsAsync || func.IsGenerator
                        || nonOrdinaryFuncNames.Contains(func.Name))
                    {
                        throw new JsParserException(
                            $"Duplicate block-level function declaration '{func.Name}'.");
                    }
                }
                if (func.IsAsync || func.IsGenerator)
                {
                    nonOrdinaryFuncNames.Add(func.Name);
                }
            }
            else if (stmt is VariableDeclarationStatementNode decl && decl.Kind is "let" or "const")
            {
                foreach (var d in decl.Declarators)
                {
                    if (!seenFuncNames.Add(d.Identifier))
                    {
                        throw new JsParserException(
                            $"Duplicate lexical declaration '{d.Identifier}' in block.");
                    }
                }
            }
            else if (stmt is ClassDeclarationNode cls)
            {
                if (!seenFuncNames.Add(cls.Name))
                {
                    throw new JsParserException(
                        $"Duplicate class declaration '{cls.Name}' in block.");
                }
            }
        }
    }

    private static HashSet<string> CollectTopLevelVarDeclaredNames(IReadOnlyList<StatementNode> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectVarDeclaredNamesRecursive(statements, names);
        return names;
    }

    // ECMA-262 14.2.1 VarDeclaredNames: var declarations propagate through
    // nested blocks recursively. `{ { var x; } function x() {} }` must flag
    // the conflict because the inner `var x` contributes to the outer block's
    // VarDeclaredNames.
    private static void CollectVarDeclaredNamesRecursive(IReadOnlyList<StatementNode> statements, HashSet<string> names, bool recurseIntoBlocks = true)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case VariableDeclarationStatementNode declaration when declaration.Kind is "var":
                    foreach (var declarator in declaration.Declarators)
                    {
                        names.Add(declarator.Identifier);
                    }
                    break;
                // Annex B.3.1.1: in sloppy mode, function declarations inside blocks
                // also count as var declarations.
                case FunctionDeclarationNode funcDecl:
                    names.Add(funcDecl.Name);
                    break;
                case BlockStatementNode block when recurseIntoBlocks:
                    CollectVarDeclaredNamesRecursive(block.Statements, names, recurseIntoBlocks);
                    break;
            }
        }
    }

    private static void CollectVarConflictNamesRecursive(
        IReadOnlyList<StatementNode> statements,
        HashSet<string> varDeclNames,
        HashSet<string> funcDeclNames,
        HashSet<string> letConstClassNames)
    {
        foreach (var stmt in statements)
        {
            if (stmt is FunctionDeclarationNode func)
            {
                funcDeclNames.Add(func.Name);
                // Annex B.3.1: function decls in blocks also contribute to var names,
                // but only for the purpose of NOT treating them as conflicts with
                // themselves. We handle that at conflict-check time.
            }
            else if (stmt is VariableDeclarationStatementNode vdecl)
            {
                foreach (var d in vdecl.Declarators)
                {
                    if (vdecl.Kind is "var")
                        varDeclNames.Add(d.Identifier);
                    else
                        letConstClassNames.Add(d.Identifier);
                }
            }
            else if (stmt is ClassDeclarationNode cls)
                letConstClassNames.Add(cls.Name);
            else if (stmt is BlockStatementNode block)
                CollectVarConflictNamesRecursive(block.Statements, varDeclNames, funcDeclNames, letConstClassNames);
        }
    }

    // Var-vs-lexical conflict validation for blocks. Function declarations in
    // sloppy-mode blocks contribute to BOTH LexicallyDeclaredNames AND
    // VarDeclaredNames (Annex B.3.1.1). A conflict exists when:
    // (a) a lexical name (let/const/class/func) also appears as a var name from
    //     an explicit `var` declaration, OR
    // (b) a lexical name from let/const/class also appears as a function decl.
    private void ValidateBlockLexicalVarConflicts(IReadOnlyList<StatementNode> statements)
    {
        var lexicalNames = CollectTopLevelLexicallyDeclaredNames(statements);
        if (lexicalNames.Count == 0) return;

        // Var names ONLY from explicit `var` declarations (not function annex B).
        // Must recurse into nested blocks (ECMA-262 VarDeclaredNames propagates).
        var varDeclNames = new HashSet<string>(StringComparer.Ordinal);
        var funcDeclNames = new HashSet<string>(StringComparer.Ordinal);
        var letConstClassNames = new HashSet<string>(StringComparer.Ordinal);
        CollectVarConflictNamesRecursive(statements, varDeclNames, funcDeclNames, letConstClassNames);

        foreach (var name in lexicalNames)
        {
            // Conflict (a): lexical name vs explicit `var` declaration.
            if (varDeclNames.Contains(name))
            {
                throw new JsParserException(
                    $"Block-scoped declaration '{name}' conflicts with a var declaration in the same block.");
            }
            // Conflict (b): let/const/class name vs function declaration.
            if (letConstClassNames.Contains(name) && funcDeclNames.Contains(name))
            {
                throw new JsParserException(
                    $"Block-scoped declaration '{name}' conflicts with a function declaration in the same block.");
            }
        }
    }

    // ECMA-262 15.4.1 MethodDefinition early errors: a getter takes no
    // parameters; a setter takes exactly one, and it must not be a rest
    // parameter. Applies to class accessors and object-literal accessors.
    private static void ValidateAccessorArity(bool isGetter, bool isSetter, ParameterListInfo parameterInfo)
    {
        if (isGetter && parameterInfo.Parameters.Count != 0)
        {
            throw new JsParserException("A getter cannot have parameters.");
        }

        if (isSetter)
        {
            if (parameterInfo.Parameters.Count != 1)
            {
                throw new JsParserException("A setter must have exactly one parameter.");
            }

            if (parameterInfo.RestParameterIndex >= 0)
            {
                throw new JsParserException("A setter cannot have a rest parameter.");
            }
        }
    }

    private static void ValidateClassMethodEarlyErrors(
        ParameterListInfo parameterInfo,
        BlockStatementNode body,
        bool forbidAwaitIdentifier,
        bool forbidYieldIdentifier,
        bool strictMode,
        bool rejectSuperCallInBody,
        bool allowSuperProperty = false)
    {
        if (parameterInfo.RestHasInitializer)
        {
            throw new JsParserException("Rest parameters cannot have initializers.");
        }

        if (parameterInfo.HasTrailingCommaAfterRest)
        {
            throw new JsParserException("A trailing comma is not allowed after a rest parameter.");
        }

        if (parameterInfo.HasDuplicateNames)
        {
            throw new JsParserException("Duplicate parameter names are not allowed in class methods.");
        }

        if (parameterInfo.HasSuperCallInInitializers)
        {
            throw new JsParserException("super() is not allowed in method parameter initializers.");
        }

        if (forbidYieldIdentifier && parameterInfo.HasYieldReferenceInInitializers)
        {
            throw new JsParserException("yield is not allowed in method parameter initializers in this method context.");
        }

        if (forbidAwaitIdentifier && parameterInfo.HasAwaitReferenceInInitializers)
        {
            throw new JsParserException("await is not allowed in method parameter initializers in this method context.");
        }

        if (!parameterInfo.IsSimple && ContainsUseStrictDirective(body.Statements))
        {
            throw new JsParserException("A strict directive is not allowed with a non-simple parameter list.");
        }

        foreach (var parameter in parameterInfo.Parameters)
        {
            if (strictMode &&
                !IsSyntheticPatternBinding(parameter) &&
                (string.Equals(parameter, "eval", StringComparison.Ordinal) ||
                 string.Equals(parameter, "arguments", StringComparison.Ordinal)))
            {
                throw new JsParserException($"Restricted identifier '{parameter}' is not allowed in strict-mode parameters.");
            }

            if (!IsSyntheticPatternBinding(parameter) &&
                IsRestrictedIdentifier(parameter, forbidAwaitIdentifier, forbidYieldIdentifier))
            {
                throw new JsParserException($"Reserved identifier '{parameter}' is not allowed in this method context.");
            }
        }

        var lexicalNames = CollectTopLevelLexicallyDeclaredNames(body.Statements);
        foreach (var parameter in parameterInfo.Parameters)
        {
            if (IsSyntheticPatternBinding(parameter))
            {
                continue;
            }

            if (lexicalNames.Contains(parameter))
            {
                throw new JsParserException($"Parameter '{parameter}' conflicts with a lexical declaration in the method body.");
            }
        }

        if (rejectSuperCallInBody)
        {
            if (allowSuperProperty)
            {
                // Class methods (non-constructor): SuperProperty is valid,
                // SuperCall (super(...)) is not. ECMA-262 15.7.2.1.
                if (ContainsSuperCallOnlyInStatements(body.Statements))
                    throw new JsParserException("super() calls are not allowed in this context.");
            }
            else if (ContainsSuperCallInStatements(body.Statements))
            {
                throw new JsParserException("super() calls and super.property access are not allowed in this context.");
            }
        }

        // The body walk can only ever throw on a `await`/`yield` identifier, so when
        // neither is forbidden (the common plain-method case, ubiquitous in minified
        // object literals) skip the entire recursive descent — both for speed and to
        // avoid a deep-stack walk over thousands-deep minified expression chains.
        if (forbidAwaitIdentifier || forbidYieldIdentifier)
        {
            ValidateRestrictedIdentifiersInStatements(body.Statements, forbidAwaitIdentifier, forbidYieldIdentifier);
        }
    }

    private static void ValidateRestrictedIdentifiersInStatements(
        IReadOnlyList<StatementNode> statements,
        bool forbidAwaitIdentifier,
        bool forbidYieldIdentifier)
    {
        foreach (var statement in statements)
        {
            ValidateRestrictedIdentifiersInStatement(statement, forbidAwaitIdentifier, forbidYieldIdentifier);
        }
    }

    [ThreadStatic] private static int _restrictedIdentScanDepth;

    private static void ValidateRestrictedIdentifiersInStatement(
        StatementNode statement,
        bool forbidAwaitIdentifier,
        bool forbidYieldIdentifier)
    {
        // Bound the early-error walk so a thousands-deep minified body cannot overflow
        // the stack. Skipping detection of a restricted identifier at pathological depth
        // is harmless; a StackOverflowException is an uncatchable process kill.
        if (++_restrictedIdentScanDepth > MaxSuperCallScanDepth)
        {
            _restrictedIdentScanDepth--;
            return;
        }

        try
        {
            ValidateRestrictedIdentifiersInStatementCore(statement, forbidAwaitIdentifier, forbidYieldIdentifier);
        }
        finally
        {
            _restrictedIdentScanDepth--;
        }
    }

    private static void ValidateRestrictedIdentifiersInStatementCore(
        StatementNode statement,
        bool forbidAwaitIdentifier,
        bool forbidYieldIdentifier)
    {
        switch (statement)
        {
            case BlockStatementNode block:
                ValidateRestrictedIdentifiersInStatements(block.Statements, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case LabeledStatementNode labeled:
                if (IsRestrictedIdentifier(labeled.Label, forbidAwaitIdentifier, forbidYieldIdentifier))
                {
                    throw new JsParserException($"Reserved label identifier '{labeled.Label}' is not allowed in this method context.");
                }

                ValidateRestrictedIdentifiersInStatement(labeled.Body, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case VariableDeclarationStatementNode declaration:
                foreach (var declarator in declaration.Declarators)
                {
                    if (!IsSyntheticPatternBinding(declarator.Identifier) &&
                        IsRestrictedIdentifier(declarator.Identifier, forbidAwaitIdentifier, forbidYieldIdentifier))
                    {
                        throw new JsParserException($"Reserved identifier '{declarator.Identifier}' is not allowed in this method context.");
                    }

                    if (declarator.Initializer is not null)
                    {
                        ValidateRestrictedIdentifiersInExpression(declarator.Initializer, forbidAwaitIdentifier, forbidYieldIdentifier);
                    }
                }

                break;
            case ExpressionStatementNode expressionStatement:
                ValidateRestrictedIdentifiersInExpression(expressionStatement.Expression, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case IfStatementNode ifStatement:
                ValidateRestrictedIdentifiersInExpression(ifStatement.Test, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(ifStatement.Consequent, forbidAwaitIdentifier, forbidYieldIdentifier);
                if (ifStatement.Alternate is not null)
                {
                    ValidateRestrictedIdentifiersInStatement(ifStatement.Alternate, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case WhileStatementNode whileStatement:
                ValidateRestrictedIdentifiersInExpression(whileStatement.Test, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(whileStatement.Body, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ForStatementNode forStatement:
                if (forStatement.Initializer is not null)
                {
                    ValidateRestrictedIdentifiersInStatement(forStatement.Initializer, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                if (forStatement.Test is not null)
                {
                    ValidateRestrictedIdentifiersInExpression(forStatement.Test, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                if (forStatement.Update is not null)
                {
                    ValidateRestrictedIdentifiersInExpression(forStatement.Update, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                ValidateRestrictedIdentifiersInStatement(forStatement.Body, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ForInStatementNode forInStatement:
                ValidateRestrictedIdentifiersInStatement(forInStatement.Initializer, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(forInStatement.Iterable, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(forInStatement.Body, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ForOfStatementNode forOfStatement:
                ValidateRestrictedIdentifiersInStatement(forOfStatement.Initializer, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(forOfStatement.Iterable, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(forOfStatement.Body, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ForAwaitOfStatementNode forAwaitOfStatement:
                ValidateRestrictedIdentifiersInStatement(forAwaitOfStatement.Initializer, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(forAwaitOfStatement.Iterable, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(forAwaitOfStatement.Body, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ReturnStatementNode returnStatement when returnStatement.Argument is not null:
                ValidateRestrictedIdentifiersInExpression(returnStatement.Argument, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ThrowStatementNode throwStatement:
                ValidateRestrictedIdentifiersInExpression(throwStatement.Argument, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case TryCatchStatementNode tryCatch:
                ValidateRestrictedIdentifiersInStatement(tryCatch.TryBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                if (IsRestrictedIdentifier(tryCatch.CatchIdentifier, forbidAwaitIdentifier, forbidYieldIdentifier))
                {
                    throw new JsParserException($"Reserved identifier '{tryCatch.CatchIdentifier}' is not allowed in this method context.");
                }

                ValidateRestrictedIdentifiersInStatement(tryCatch.CatchBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case TryFinallyStatementNode tryFinally:
                ValidateRestrictedIdentifiersInStatement(tryFinally.TryBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(tryFinally.FinallyBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case TryCatchFinallyStatementNode tryCatchFinally:
                ValidateRestrictedIdentifiersInStatement(tryCatchFinally.TryBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                if (IsRestrictedIdentifier(tryCatchFinally.CatchIdentifier, forbidAwaitIdentifier, forbidYieldIdentifier))
                {
                    throw new JsParserException($"Reserved identifier '{tryCatchFinally.CatchIdentifier}' is not allowed in this method context.");
                }

                ValidateRestrictedIdentifiersInStatement(tryCatchFinally.CatchBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInStatement(tryCatchFinally.FinallyBlock, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case SwitchStatementNode switchStatement:
                ValidateRestrictedIdentifiersInExpression(switchStatement.Discriminant, forbidAwaitIdentifier, forbidYieldIdentifier);
                foreach (var switchCase in switchStatement.Cases)
                {
                    if (switchCase.Test is not null)
                    {
                        ValidateRestrictedIdentifiersInExpression(switchCase.Test, forbidAwaitIdentifier, forbidYieldIdentifier);
                    }

                    ValidateRestrictedIdentifiersInStatements(switchCase.Consequent, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case FunctionDeclarationNode:
            case ClassDeclarationNode:
                break;
        }
    }

    private static void ValidateRestrictedIdentifiersInExpression(
        ExpressionNode expression,
        bool forbidAwaitIdentifier,
        bool forbidYieldIdentifier)
    {
        if (++_restrictedIdentScanDepth > MaxSuperCallScanDepth)
        {
            _restrictedIdentScanDepth--;
            return;
        }

        try
        {
            ValidateRestrictedIdentifiersInExpressionCore(expression, forbidAwaitIdentifier, forbidYieldIdentifier);
        }
        finally
        {
            _restrictedIdentScanDepth--;
        }
    }

    private static void ValidateRestrictedIdentifiersInExpressionCore(
        ExpressionNode expression,
        bool forbidAwaitIdentifier,
        bool forbidYieldIdentifier)
    {
        switch (expression)
        {
            case IdentifierExpressionNode identifier:
                if (IsRestrictedIdentifier(identifier.Name, forbidAwaitIdentifier, forbidYieldIdentifier))
                {
                    throw new JsParserException($"Reserved identifier '{identifier.Name}' is not allowed in this method context.");
                }

                break;
            case ParenthesizedExpressionNode parenthesized:
                ValidateRestrictedIdentifiersInExpression(parenthesized.Expression, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case BinaryExpressionNode binary:
                ValidateRestrictedIdentifiersInExpression(binary.Left, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(binary.Right, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case AssignmentExpressionNode assignment:
                ValidateRestrictedIdentifiersInExpression(assignment.Left, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(assignment.Right, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case CallExpressionNode call:
                ValidateRestrictedIdentifiersInExpression(call.Callee, forbidAwaitIdentifier, forbidYieldIdentifier);
                foreach (var argument in call.Arguments)
                {
                    ValidateRestrictedIdentifiersInExpression(argument, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case OptionalCallExpressionNode optionalCall:
                ValidateRestrictedIdentifiersInExpression(optionalCall.Callee, forbidAwaitIdentifier, forbidYieldIdentifier);
                foreach (var argument in optionalCall.Arguments)
                {
                    ValidateRestrictedIdentifiersInExpression(argument, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case ObjectLiteralExpressionNode objectLiteral:
                foreach (var property in objectLiteral.Properties)
                {
                    if (property.ComputedKey is not null)
                    {
                        ValidateRestrictedIdentifiersInExpression(property.ComputedKey, forbidAwaitIdentifier, forbidYieldIdentifier);
                    }

                    ValidateRestrictedIdentifiersInExpression(property.Value, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case ArrayLiteralExpressionNode arrayLiteral:
                foreach (var element in arrayLiteral.Elements)
                {
                    ValidateRestrictedIdentifiersInExpression(element, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case SpreadElementExpressionNode spread:
                ValidateRestrictedIdentifiersInExpression(spread.Argument, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case MemberExpressionNode member:
                ValidateRestrictedIdentifiersInExpression(member.Object, forbidAwaitIdentifier, forbidYieldIdentifier);
                if (member.PropertyExpression is not null)
                {
                    ValidateRestrictedIdentifiersInExpression(member.PropertyExpression, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case OptionalMemberExpressionNode optionalMember:
                ValidateRestrictedIdentifiersInExpression(optionalMember.Object, forbidAwaitIdentifier, forbidYieldIdentifier);
                if (optionalMember.PropertyExpression is not null)
                {
                    ValidateRestrictedIdentifiersInExpression(optionalMember.PropertyExpression, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case UnaryExpressionNode unary:
                ValidateRestrictedIdentifiersInExpression(unary.Operand, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case ConditionalExpressionNode conditional:
                ValidateRestrictedIdentifiersInExpression(conditional.Test, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(conditional.Consequent, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(conditional.Alternate, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case NewExpressionNode @new:
                ValidateRestrictedIdentifiersInExpression(@new.Callee, forbidAwaitIdentifier, forbidYieldIdentifier);
                foreach (var argument in @new.Arguments)
                {
                    ValidateRestrictedIdentifiersInExpression(argument, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case TemplateLiteralExpressionNode template:
                foreach (var templateExpression in template.Expressions)
                {
                    ValidateRestrictedIdentifiersInExpression(templateExpression, forbidAwaitIdentifier, forbidYieldIdentifier);
                }

                break;
            case TaggedTemplateExpressionNode taggedTemplate:
                ValidateRestrictedIdentifiersInExpression(taggedTemplate.Tag, forbidAwaitIdentifier, forbidYieldIdentifier);
                ValidateRestrictedIdentifiersInExpression(taggedTemplate.Template, forbidAwaitIdentifier, forbidYieldIdentifier);
                break;
            case FunctionExpressionNode:
            case ArrowFunctionExpressionNode:
            case ClassExpressionNode:
                break;
        }
    }

    // ECMA-262 distinguishes StatementListItem (block/program body — Declarations
    // allowed) from Statement (single-statement bodies of if/iteration/with/label —
    // Declarations forbidden). Annex B B.3.4/B.3.2 additionally permit a *plain*
    // (non-generator, non-async) FunctionDeclaration as the body of an IfStatement
    // clause or a LabelledStatement in non-strict code, but never as an iteration
    // or `with` body.
    private enum StatementBodyContext
    {
        StatementListItem,   // block / program: all Declarations allowed
        IfClauseOrLabel,     // single Statement; Annex B sloppy FunctionDeclaration allowed
        IterationOrWith,     // single Statement; no Declarations at all
    }

    private StatementNode ParseStatement(StatementBodyContext ctx = StatementBodyContext.StatementListItem)
    {
        // Capture and clear the module-top-level flag so that nested statements
        // parsed by this call (block bodies, control-flow clauses, etc.) are not
        // treated as top-level module items.
        var atModuleTopLevel = _atModuleTopLevel;
        _atModuleTopLevel = false;
        EnterRecursion();
        try
        {
            return ParseStatementCore(ctx, atModuleTopLevel);
        }
        finally
        {
            ExitRecursion();
        }
    }

    private StatementNode ParseStatementCore(StatementBodyContext ctx, bool atModuleTopLevel)
    {
        if (IsPunctuator("@"))
        {
            ParseDecoratorList();
            if (Current().Kind == TokenKind.Keyword && Current().Text == "class")
            {
                return ParseClassDeclaration();
            }

            throw new JsParserException("Decorators are only supported before class declarations in statement position.");
        }

        if ((IsIdentifierLike(Current()) ||
             (_classStaticBlockDepth > 0 && Current().Kind == TokenKind.Keyword && Current().Text == "await")) &&
            PeekIsPunctuator(1, ":"))
        {
            if (_classStaticBlockDepth > 0 &&
                Current().Kind == TokenKind.Keyword &&
                Current().Text == "await")
            {
                throw new JsParserException("'await' cannot be used as a label inside a class static block.");
            }

            var labelToken = Advance();
            ExpectPunctuator(":");
            var body = ParseStatement(StatementBodyContext.IfClauseOrLabel);
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
                    // ECMA-262 13.3.10 / 13.3.12 — `import(...)` ImportCall and
                    // `import.meta` ImportMeta are expressions, not declarations.
                    // Fall through to ParseExpressionStatement in those positions.
                    if (PeekIsPunctuator(1, "(") || PeekIsPunctuator(1, "."))
                    {
                        break;
                    }
                    if (!_moduleMode || !atModuleTopLevel)
                    {
                        throw new JsParserException("An import declaration may only appear at the top level of a module.");
                    }
                    return ParseImportDeclaration();
                case "export":
                    if (!_moduleMode || !atModuleTopLevel)
                    {
                        throw new JsParserException("An export declaration may only appear at the top level of a module.");
                    }
                    return ParseExportDeclaration();
                case "class":
                    if (ctx != StatementBodyContext.StatementListItem)
                    {
                        throw new JsParserException("Class declaration is not allowed as a single-statement body.");
                    }
                    return ParseClassDeclaration();
                case "const":
                    if (ctx != StatementBodyContext.StatementListItem)
                    {
                        throw new JsParserException("Lexical declaration is not allowed as a single-statement body.");
                    }
                    return ParseVariableDeclarationStatement();
                case "let":
                    // `let` heads a LexicalDeclaration only in StatementListItem
                    // position and only when followed by a binding start (`[`, `{`,
                    // or a BindingIdentifier). Otherwise it is an ordinary
                    // identifier (sloppy) — `let + 1`, `let.x`, `let()` — so fall
                    // through to expression parsing. As a single-statement body it
                    // is likewise never a declaration.
                    if (ctx == StatementBodyContext.StatementListItem && StartsLetLexicalDeclaration())
                    {
                        return ParseVariableDeclarationStatement();
                    }
                    // In a single-statement body `let [` can be neither an
                    // ExpressionStatement (the `let [` lookahead restriction, which
                    // ignores line terminators) nor a Declaration — so it is a
                    // SyntaxError. `let {` and other forms fall through (identifier).
                    if (ctx != StatementBodyContext.StatementListItem && PeekIsPunctuator(1, "["))
                    {
                        throw new JsParserException("'let [' may not begin a single-statement body.");
                    }
                    break;
                case "using":
                    // ES2025 Explicit Resource Management — `using x = expr` declaration.
                    // `using` is a contextual keyword; in expression position it is a
                    // regular identifier. Only in StatementListItem position and only
                    // when followed by a binding start does it begin a UsingDeclaration.
                    if (ctx == StatementBodyContext.StatementListItem && StartsLetLexicalDeclaration())
                    {
                        return ParseUsingDeclaration(isAwaitUsing: false);
                    }
                    break;
                case "var":
                    return ParseVariableDeclarationStatement();
                case "if":
                    return ParseIfStatement();
                case "do":
                    return ParseDoWhileStatement();
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
                        if (ctx != StatementBodyContext.StatementListItem)
                        {
                            throw new JsParserException("Async function declaration is not allowed as a single-statement body.");
                        }
                        return ParseFunctionDeclaration();
                    }
                    // ES2025 Explicit Resource Management — `await using x = expr`.
                    if (PeekKeyword(1, "using"))
                    {
                        var idx2 = Math.Min(_index + 2, _tokens.Count - 1);
                        var tok2 = _tokens[idx2];
                        if (tok2.Kind == TokenKind.Punctuator && (tok2.Text == "[" || tok2.Text == "{")
                            || IsIdentifierLike(tok2))
                        {
                            if (ctx != StatementBodyContext.StatementListItem)
                            {
                                throw new JsParserException("Await using declaration is not allowed as a single-statement body.");
                            }
                            return ParseUsingDeclaration(isAwaitUsing: true);
                        }
                    }

                    break;
                case "function":
                    if (ctx != StatementBodyContext.StatementListItem)
                    {
                        // Annex B B.3.4/B.3.2: only a plain (non-generator)
                        // FunctionDeclaration in non-strict code may be an
                        // IfStatement clause or LabelledStatement body. Generators,
                        // strict mode, and iteration/with bodies always reject.
                        var isGenerator = PeekIsPunctuator(1, "*");
                        if (isGenerator || _strictMode || ctx != StatementBodyContext.IfClauseOrLabel)
                        {
                            throw new JsParserException("Function declaration is not allowed as a single-statement body here.");
                        }
                    }
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
                case "debugger":
                    return ParseDebuggerStatement();
            }
        }

        // ES2025 Explicit Resource Management — `using x = expr` and
        // `await using x = expr` declarations. `using` is not a keyword,
        // and `await` may not match a switch case. Check for both before
        // falling through to expression parsing.
        if (ctx == StatementBodyContext.StatementListItem)
        {
            if (IsUnescapedIdentifierLike(Current(), "using") && StartsLetLexicalDeclaration())
            {
                return ParseUsingDeclaration(isAwaitUsing: false);
            }
            // `await using x = expr` — check if current is `await` followed by `using`
            // and a binding start. Outside async functions, `await` is an identifier.
            if (IsUnescapedIdentifierLike(Current(), "await") && PeekIdentifierLike(1, "using"))
            {
                var idx2 = Math.Min(_index + 2, _tokens.Count - 1);
                var tok2 = _tokens[idx2];
                if (tok2.Kind == TokenKind.Punctuator && (tok2.Text == "[" || tok2.Text == "{")
                    || IsIdentifierLike(tok2))
                {
                    return ParseUsingDeclaration(isAwaitUsing: true);
                }
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
            var statement = ParseStatement();
            // ECMA-262 15.1.1: directive prologue may set strict mode for the
            // remainder of this block (profiled for function/program bodies).
            UpdateDirectivePrologueState(statement);
            statements.Add(statement);
        }

        ExpectPunctuator("}");
        var close = Previous();
        // ECMA-262 14.2.1: early error check for duplicate declarations
        // and var-vs-lexical conflicts within the block.
        ValidateBlockEarlyErrors(statements);
        return new BlockStatementNode(statements, MergeSpan(open.Span, close.Span));
    }

    private readonly record struct ParsedVariableBinding(
        string Identifier,
        BindingPatternNode? Pattern,
        SourceSpan Span);

    // ES2025 Explicit Resource Management — `using x = expr` and `await using x = expr`.
    // Parsed as a let-like declaration with kind "using" or "await using".
    // Full runtime disposal semantics are not yet implemented; the compiler treats
    // these as let declarations so the syntax tests pass.
    private VariableDeclarationStatementNode ParseUsingDeclaration(bool isAwaitUsing)
    {
        var start = Advance(); // 'using' (or 'await' for await-using)
        if (isAwaitUsing)
            Advance(); // 'using' after 'await'
        var kind = isAwaitUsing ? "await using" : "using";
        var declarators = new List<VariableDeclaratorNode>();

        while (true)
        {
            var binding = ParseVariableDeclaratorBinding();
            ExpressionNode? initializer = null;
            if (IsPunctuator("="))
            {
                Advance();
                initializer = ParseExpression(2);
            }
            // using declarations require an initializer per spec, but we defer
            // the early-error check to the runtime.
            var span = initializer is null ? binding.Span : MergeSpan(binding.Span, initializer.Span);
            declarators.Add(new VariableDeclaratorNode(binding.Identifier, initializer, span, binding.Pattern));
            if (!IsPunctuator(","))
                break;
            Advance();
        }

        return new VariableDeclarationStatementNode(kind, declarators, MergeSpan(start.Span, Previous().Span));
    }

    private VariableDeclarationStatementNode ParseVariableDeclarationStatement(bool inForHead = false)
    {
        var start = Advance(); // let|const|var
        var declarators = new List<VariableDeclaratorNode>();

        while (true)
        {
            var binding = ParseVariableDeclaratorBinding();

            // ECMA-262 14.3.1 / 13.3.1.1 early error: `eval` and `arguments` may not
            // be bound by a var/let/const declaration in strict-mode code.
            if (_strictMode && !IsSyntheticPatternBinding(binding.Identifier) &&
                (string.Equals(binding.Identifier, "eval", StringComparison.Ordinal) ||
                 string.Equals(binding.Identifier, "arguments", StringComparison.Ordinal)))
            {
                throw new JsParserException(
                    $"'{binding.Identifier}' may not be declared as a binding name in strict mode.");
            }

            ExpressionNode? initializer = null;
            if (IsPunctuator("="))
            {
                Advance();
                var priorAnnexBFlag = _allowAnnexBForInInitializerTail;
                _allowAnnexBForInInitializerTail =
                    priorAnnexBFlag ||
                    (inForHead && string.Equals(start.Text, "var", StringComparison.Ordinal));
                try
                {
                    initializer = ParseExpression(2);
                }
                finally
                {
                    _allowAnnexBForInInitializerTail = priorAnnexBFlag;
                }
            }

            if (binding.Pattern is not null && initializer is null && !inForHead)
            {
                throw new JsParserException("Missing initializer in destructuring declaration.");
            }

            var declaratorSpan = initializer is null ? binding.Span : MergeSpan(binding.Span, initializer.Span);
            declarators.Add(new VariableDeclaratorNode(binding.Identifier, initializer, declaratorSpan, binding.Pattern));

            if (!IsPunctuator(","))
            {
                break;
            }

            Advance();
        }

        // ECMA-262 14.3.1.1: const declarations must have an initializer
        // (except in for-in/for-of heads where the iteration supplies the value).
        if (!inForHead && string.Equals(start.Text, "const", StringComparison.Ordinal))
        {
            foreach (var d in declarators)
            {
                if (d.Initializer is null)
                {
                    throw new JsParserException("Missing initializer in const declaration.");
                }
            }
        }

        // ECMA-262 14.3.1.1: "let" may not be used as a binding name in a
        // let/const declaration. Covers `let let;`, `const let = 1;`, and
        // ASI edge cases like `let\nlet;`.
        // ECMA-262 15.7.3: class static blocks disallow 'await' as a binding name.
        // This covers both simple bindings (e.g. `const await = 0`) and destructuring
        // patterns (e.g. `var [await] = []` / `var {await} = {}`).
        if (_classStaticBlockDepth > 0)
        {
            foreach (var d in declarators)
            {
                if (d.BindingPattern is not null)
                    ValidateBindingPatternAwait(d.BindingPattern);
                else if (!IsSyntheticPatternBinding(d.Identifier) && string.Equals(d.Identifier, "await", StringComparison.Ordinal))
                    throw new JsParserException("'await' may not be used as a binding name inside a class static block.");
            }
        }

        if (start.Text is "let" or "const")
        {
            foreach (var d in declarators)
            {
                if (string.Equals(d.Identifier, "let", StringComparison.Ordinal))
                {
                    throw new JsParserException(
                        "'let' may not be used as a binding name in a let or const declaration.");
                }
            }
        }

        // ECMA-262 13.7.5: duplicate bound names in for-in/of declarations.
        if (start.Text is "let" or "const")
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in declarators)
            {
                if (d.BindingPattern is not null)
                    ValidateBindingPatternDuplicates(d.BindingPattern, seen);
                else if (!IsSyntheticPatternBinding(d.Identifier) && !seen.Add(d.Identifier))
                    throw new JsParserException($"Duplicate declaration '{d.Identifier}'.");
            }
        }

        // ECMA-262 ASI: after a declaration, require `;`, newline, `}`, or EOF.
        // Same-line tokens that aren't continuation (`=`, `,`) are errors.
        var lastTok = Previous();
        if (IsPunctuator(";"))
            Advance();
        else if (!inForHead && !Is(TokenKind.EndOfFile) && !IsPunctuator("}") &&
                 !HasLineTerminatorBetween(lastTok, Current()))
            throw new JsParserException($"Unexpected token '{Current().Text}' after declaration.");

        var end = Previous();
        return new VariableDeclarationStatementNode(start.Text, declarators, MergeSpan(start.Span, end.Span));
    }

    private ParsedVariableBinding ParseVariableDeclaratorBinding()
    {
        if (IsIdentifierLike(Current()))
        {
            var token = Advance();
            return new ParsedVariableBinding(token.Text, null, token.Span);
        }

        if (IsPunctuator("[") || IsPunctuator("{"))
        {
            var pattern = ParseBindingPattern();
            var name = $"__pattern{_syntheticBindingCounter++}";
            return new ParsedVariableBinding(name, pattern, pattern.Span);
        }

        throw new JsParserException($"Expected identifier, found '{Current().Text}'{Where()}.");
    }

    private BindingPatternNode ParseBindingPattern()
    {
        if (IsIdentifierLike(Current()))
        {
            var identifier = Advance();
            return new IdentifierBindingPatternNode(identifier.Text, identifier.Span);
        }

        if (IsPunctuator("["))
        {
            return ParseArrayBindingPattern();
        }

        if (IsPunctuator("{"))
        {
            return ParseObjectBindingPattern();
        }

        throw new JsParserException($"Expected binding pattern, found '{Current().Text}'.");
    }

    private ArrayBindingPatternNode ParseArrayBindingPattern()
    {
        var open = Advance();
        var elements = new List<ArrayBindingElementNode>();

        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("]"))
        {
            if (IsPunctuator(","))
            {
                var comma = Advance();
                elements.Add(new ArrayBindingElementNode(null, null, IsRest: false, comma.Span));
                continue;
            }

            var isRest = false;
            SourceSpan elementStart;
            if (IsPunctuator("..."))
            {
                isRest = true;
                elementStart = Advance().Span;
            }
            else
            {
                elementStart = Current().Span;
            }

            var target = ParseBindingPattern();
            ExpressionNode? initializer = null;
            if (IsPunctuator("="))
            {
                if (isRest)
                {
                    throw new JsParserException("Array binding rest elements cannot have initializers.");
                }

                Advance();
                initializer = ParseExpression(2);
            }

            if (isRest && IsPunctuator(","))
            {
                throw new JsParserException("Array binding rest elements must be the final element.");
            }

            var elementSpan = initializer is null ? MergeSpan(elementStart, target.Span) : MergeSpan(elementStart, initializer.Span);
            elements.Add(new ArrayBindingElementNode(target, initializer, isRest, elementSpan));

            if (!IsPunctuator(","))
            {
                break;
            }

            Advance();
        }

        ExpectPunctuator("]");
        var close = Previous();
        return new ArrayBindingPatternNode(elements, MergeSpan(open.Span, close.Span));
    }

    private ObjectBindingPatternNode ParseObjectBindingPattern()
    {
        var open = Advance();
        var properties = new List<ObjectBindingPropertyNode>();
        BindingPatternNode? rest = null;

        while (!Is(TokenKind.EndOfFile) && !IsPunctuator("}"))
        {
            if (IsPunctuator("..."))
            {
                var spread = Advance();
                rest = ParseBindingPattern();
                if (IsPunctuator("="))
                {
                    throw new JsParserException("Object binding rest properties cannot have initializers.");
                }

                if (IsPunctuator(","))
                {
                    throw new JsParserException("Object binding rest property must be the final property.");
                }

                _ = spread;
                break;
            }

            var property = ParseObjectBindingProperty();
            properties.Add(property);

            if (!IsPunctuator(","))
            {
                break;
            }

            Advance();
        }

        ExpectPunctuator("}");
        var close = Previous();
        return new ObjectBindingPatternNode(properties, rest, MergeSpan(open.Span, close.Span));
    }

    private ObjectBindingPropertyNode ParseObjectBindingProperty()
    {
        var start = Current().Span;
        string? key = null;
        ExpressionNode? computedKey = null;
        var isComputed = false;
        BindingPatternNode target;
        ExpressionNode? initializer = null;

        if (IsPunctuator("["))
        {
            isComputed = true;
            Advance();
            computedKey = ParseExpression(0);
            ExpectPunctuator("]");
            ExpectPunctuator(":");
            target = ParseBindingPattern();
        }
        else
        {
            Token keyToken;
            // A BindingProperty's key is a PropertyName, i.e. an IdentifierName, so any
            // reserved word is allowed as the key (e.g. `let {default: r} = m`, pervasive
            // in minified bundles that destructure `import()` results). IsIdentifierLike
            // is the stricter "valid BindingIdentifier" test; a reserved word that is not
            // a valid binding can still be a key, but only in the explicit `key: target`
            // form — never as a shorthand `{default}` that would bind a reserved word.
            var keyIsBindingIdentifier = IsIdentifierLike(Current());
            if (keyIsBindingIdentifier || Current().Kind == TokenKind.Keyword)
            {
                keyToken = Advance();
                key = keyToken.Text;
            }
            else if (Is(TokenKind.String) || Is(TokenKind.Number))
            {
                keyToken = Advance();
                key = keyToken.Kind == TokenKind.String ? keyToken.Text : NormalizeNumericPropertyName(keyToken);
            }
            else
            {
                throw new JsParserException($"Expected object binding property name, found '{Current().Text}'{Where()}.");
            }

            if (IsPunctuator(":"))
            {
                Advance();
                target = ParseBindingPattern();
            }
            else if (keyIsBindingIdentifier)
            {
                target = new IdentifierBindingPatternNode(key, keyToken.Span);
            }
            else
            {
                throw new JsParserException($"'{key}' is a reserved word and cannot be a shorthand binding{Where()}.");
            }
        }

        if (IsPunctuator("="))
        {
            Advance();
            initializer = ParseExpression(2);
        }

        var endSpan = initializer is not null ? initializer.Span : target.Span;
        return new ObjectBindingPropertyNode(key, computedKey, isComputed, target, initializer, MergeSpan(start, endSpan));
    }

    private static string NormalizeNumericPropertyName(Token token)
    {
        var raw = token.Text.Replace("_", string.Empty);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString("G17", CultureInfo.InvariantCulture);
        }

        return token.Text;
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

        throw new JsParserException($"Expected identifier, found '{Current().Text}'{Where()}.");
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
        var arrayRestElementSeen = false;
        var arrayRestNestedDepth = 0;

        while (!Is(TokenKind.EndOfFile) && depth > 0)
        {
            var token = Advance();

            if (openText == "[" && token.Kind == TokenKind.Punctuator)
            {
                if (!arrayRestElementSeen && depth == 1 && token.Text == "...")
                {
                    arrayRestElementSeen = true;
                    arrayRestNestedDepth = 0;
                }
                else if (arrayRestElementSeen)
                {
                    if (token.Text is "[" or "{" or "(")
                    {
                        arrayRestNestedDepth++;
                    }
                    else if (arrayRestNestedDepth > 0 && token.Text is "]" or "}" or ")")
                    {
                        arrayRestNestedDepth--;
                    }

                    if (depth == 1 && arrayRestNestedDepth == 0)
                    {
                        if (token.Text == "=")
                        {
                            throw new JsParserException("Array binding rest elements cannot have initializers.");
                        }

                        if (token.Text == ",")
                        {
                            throw new JsParserException("Array binding rest elements must be the final element.");
                        }
                    }
                }
            }

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
        var consequent = ParseStatement(StatementBodyContext.IfClauseOrLabel);
        StatementNode? alternate = null;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "else")
        {
            Advance();
            alternate = ParseStatement(StatementBodyContext.IfClauseOrLabel);
        }

        var endSpan = alternate?.Span ?? consequent.Span;
        return new IfStatementNode(test, consequent, alternate, MergeSpan(start.Span, endSpan));
    }

    // ECMA-262 14.7.2 - do Statement while ( Expression );
    private DoWhileStatementNode ParseDoWhileStatement()
    {
        var start = Advance(); // do
        var bodyStmt = ParseStatement(StatementBodyContext.IterationOrWith);
        var body = bodyStmt is BlockStatementNode block ? block
            : new BlockStatementNode(new[] { bodyStmt }, bodyStmt.Span);
        if (!(Current().Kind == TokenKind.Keyword && Current().Text == "while"))
            throw new JsParserException("Expected 'while' after do body.");
        Advance();
        ExpectPunctuator("(");
        var test = ParseExpression(0);
        ExpectPunctuator(")");
        if (IsPunctuator(";"))
        {
            _ = Advance();
        }
        var close = Previous();
        return new DoWhileStatementNode(body, test, MergeSpan(start.Span, close.Span));
    }

    private WhileStatementNode ParseWhileStatement()
    {
        var start = Advance(); // while
        ExpectPunctuator("(");
        var test = ParseExpression(0);
        ExpectPunctuator(")");
        var body = ParseStatement(StatementBodyContext.IterationOrWith);
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
        var body = ParseStatement(StatementBodyContext.IterationOrWith);
        return new WithStatementNode(obj, body, MergeSpan(start.Span, body.Span));
    }

    private StatementNode ParseForStatement()
    {
        var start = Advance(); // for
        var isForAwait = false;
        if (Current().Kind == TokenKind.Keyword && Current().Text == "await")
        {
            Advance();
            isForAwait = true;
        }

        ExpectPunctuator("(");

        StatementNode? initializer = null;
        ExpressionNode? initializerExpression = null;
        var requireInitializerSemicolon = false;
        var initializerIsDeclaration = false;
        if (!IsPunctuator(";"))
        {
            // The for-head LHS is parsed under [~In] so a bare `in` terminates it
            // and is recognised below as the for-in marker (ECMA-262 14.7.4).
            var savedNoIn = _noIn;
            _noIn = true;
            try
            {
                if (Current().Kind == TokenKind.Keyword && (Current().Text == "let" || Current().Text == "const" || Current().Text == "var"))
                {
                    initializer = ParseVariableDeclarationStatement(inForHead: true);
                    initializerIsDeclaration = true;
                }
                else if (IsUnescapedIdentifierLike(Current(), "using") && StartsLetLexicalDeclaration())
                {
                    initializer = ParseUsingDeclaration(isAwaitUsing: false);
                    initializerIsDeclaration = true;
                }
                else if (IsUnescapedIdentifierLike(Current(), "await") && PeekIdentifierLike(1, "using"))
                {
                    var idx2 = Math.Min(_index + 2, _tokens.Count - 1);
                    var tok2 = _tokens[idx2];
                    if ((tok2.Kind == TokenKind.Punctuator && (tok2.Text == "[" || tok2.Text == "{"))
                        || IsIdentifierLike(tok2))
                    {
                        initializer = ParseUsingDeclaration(isAwaitUsing: true);
                        initializerIsDeclaration = true;
                    }
                }
                else
                {
                    var initExpr = ParseExpression(0);
                    initializerExpression = initExpr;
                    initializer = new ExpressionStatementNode(initExpr, initExpr.Span);
                    requireInitializerSemicolon = true;
                }
            }
            finally
            {
                _noIn = savedNoIn;
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
            ValidateForHeadDestructuringTarget(initializer);

            Advance(); // in
            var iterable = ParseExpression(0);
            ExpectPunctuator(")");
            var forInBody = ParseStatement(StatementBodyContext.IterationOrWith);
            ValidateIterationBodyNotLabelledFunction(forInBody);
            ValidateForHeadBodyVarConflicts(initializer, forInBody);
            return new ForInStatementNode(initializer, iterable, forInBody, MergeSpan(start.Span, forInBody.Span));
        }

        if (Current().Kind == TokenKind.Keyword && Current().Text == "of")
        {
            if (initializer is null)
            {
                throw new JsParserException("for-of requires an initializer target.");
            }

            ValidateForOfDeclarationNoInitializer(initializer);
            ValidateForHeadDestructuringTarget(initializer);
            // ECMA-262 14.7.5.1: LHS cannot be 'async' when followed by 'of'.
            if (initializer is ExpressionStatementNode { Expression: IdentifierExpressionNode { Name: "async" } })
                throw new JsParserException("'async' is not a valid left-hand side for for-of.");
            // ECMA-262 13.15.1: expression LHS of for-of must be a valid target.
            if (initializer is ExpressionStatementNode exprStmt &&
                !IsValidAssignmentTarget(exprStmt.Expression))
            {
                throw new JsParserException(
                    $"Invalid assignment target in for-of head{Where()}.");
            }
            Advance(); // of
            // ECMA-262 14.7.5: the RHS of for-of is AssignmentExpression, not
            // Expression. Parse at bp=2 to reject comma expressions like `[], []`.
            var iterable = ParseExpression(2);
            ExpectPunctuator(")");
            var forOfBody = ParseStatement(StatementBodyContext.IterationOrWith);
            ValidateIterationBodyNotLabelledFunction(forOfBody);
            ValidateForHeadBodyVarConflicts(initializer, forOfBody);
            return isForAwait
                ? new ForAwaitOfStatementNode(initializer, iterable, forOfBody, MergeSpan(start.Span, forOfBody.Span))
                : new ForOfStatementNode(initializer, iterable, forOfBody, MergeSpan(start.Span, forOfBody.Span));
        }

        if (isForAwait)
        {
            throw new JsParserException("for await requires an of-clause.");
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
                    TryGetTopLevelInBinary(initializerExpression, out var inLeftCandidate, out var inRightCandidate))
                {
                    var forInInitializer = new ExpressionStatementNode(inLeftCandidate, inLeftCandidate.Span);
                    ValidateForInInitializer(forInInitializer);
                    Advance();
                    var forInBody = ParseStatement(StatementBodyContext.IterationOrWith);
                    return new ForInStatementNode(
                        forInInitializer,
                        inRightCandidate,
                        forInBody,
                        MergeSpan(start.Span, forInBody.Span));
                }

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
                var bodyWithImplicitlyEmptyRemainder = ParseStatement(StatementBodyContext.IterationOrWith);
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
            declarationWithInitializer.Declarators.Count == 1 &&
            declarationWithInitializer.Declarators[0].Initializer is { } declarationInitializer &&
            TryGetTopLevelInBinary(declarationInitializer, out var forInInitLeft, out var forInIterable))
        {
            var declarator = declarationWithInitializer.Declarators[0];
            var hasPattern = declarator.BindingPattern is not null ||
                             declarator.Identifier.StartsWith("__pattern", StringComparison.Ordinal);
            var allowAnnexBVarInitializer =
                !_strictMode &&
                string.Equals(declarationWithInitializer.Kind, "var", StringComparison.Ordinal) &&
                !hasPattern;

            if (!allowAnnexBVarInitializer)
            {
                throw new JsParserException("for-in declaration initializers are not allowed.");
            }

            var rewrittenDeclarator = declarator with
            {
                Initializer = forInInitLeft,
                Span = MergeSpan(declarator.Span, forInInitLeft.Span)
            };
            var rewrittenDeclaration = declarationWithInitializer with
            {
                Declarators = new[] { rewrittenDeclarator },
                Span = MergeSpan(declarationWithInitializer.Span, rewrittenDeclarator.Span)
            };

            // In `for (var a = 0 in stored = a, {})`, declaration parsing stops
            // before the comma to avoid consuming declarator separators. Treat any
            // trailing comma sequence as part of the for-in RHS expression.
            var iterable = forInIterable;
            if (IsAssignmentOperator(Current()))
            {
                if (!IsValidAssignmentTarget(iterable))
                {
                    throw new JsParserException("Invalid assignment target.");
                }

                var op = Advance().Text;
                var right = ParseExpression(2);
                iterable = BuildAssignmentNode(iterable, op, right, MergeSpan(iterable.Span, right.Span));
            }

            while (IsPunctuator(","))
            {
                Advance(); // ,
                var nextSegment = ParseExpression(2);
                iterable = new BinaryExpressionNode(
                    ",",
                    iterable,
                    nextSegment,
                    MergeSpan(iterable.Span, nextSegment.Span));
            }

            ExpectPunctuator(")");
            var forInBody = ParseStatement(StatementBodyContext.IterationOrWith);
            return new ForInStatementNode(
                rewrittenDeclaration,
                iterable,
                forInBody,
                MergeSpan(start.Span, forInBody.Span));
        }

        if (IsPunctuator(")"))
        {
            Advance();
            var bodyWithImplicitlyEmptyRemainder = ParseStatement(StatementBodyContext.IterationOrWith);
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
        var body = ParseStatement(StatementBodyContext.IterationOrWith);
        return new ForStatementNode(initializer, test, update, body, MergeSpan(start.Span, body.Span));
    }

    private void ValidateForInInitializer(StatementNode initializer)
    {
        if (initializer is VariableDeclarationStatementNode declaration)
        {
            var allowAnnexBVarInitializer =
                !_strictMode &&
                string.Equals(declaration.Kind, "var", StringComparison.Ordinal);

            foreach (var declarator in declaration.Declarators)
            {
                if (declarator.Initializer is not null)
                {
                    // Annex B B.3.5: sloppy-mode `for-in` permits var initializers.
                    // Keep the relaxation narrow: only plain var bindings.
                    if (!allowAnnexBVarInitializer || declarator.BindingPattern is not null)
                    {
                        throw new JsParserException("for-in declaration initializers are not allowed.");
                    }
                }
            }

            return;
        }

        if (initializer is ExpressionStatementNode expressionStatement)
        {
            if (expressionStatement.Expression is AssignmentExpressionNode)
            {
                throw new JsParserException("for-in assignment initializers are not allowed.");
            }
            // ECMA-262 13.15.1: the LHS of for-in must be a valid assignment target.
            if (!IsValidAssignmentTarget(expressionStatement.Expression))
            {
                throw new JsParserException(
                    $"Invalid assignment target in for-in head{Where()}.");
            }
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

    // ECMA-262 13.7.5 / 14.7 / 14.6: IterationStatement bodies must not be
    // labelled function declarations (even nested labels like L1: L2: function f).
    private static void ValidateIterationBodyNotLabelledFunction(StatementNode body)
    {
        var stmt = body;
        while (stmt is LabeledStatementNode labeled)
            stmt = labeled.Body;
        if (stmt is FunctionDeclarationNode)
            throw new JsParserException(
                "Labelled function declarations are not allowed as the body of an iteration statement.");
    }

    // ECMA-262 13.7.5: the body of a for-in/for-of must not have var-declared
    // names that conflict with the head's bound names.
    private void ValidateForHeadBodyVarConflicts(StatementNode? initializer, StatementNode body)
    {
        if (initializer is not VariableDeclarationStatementNode decl) return;
        var boundNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in decl.Declarators)
        {
            if (d.BindingPattern is not null)
                CollectBoundNames(d.BindingPattern, boundNames);
            else if (!IsSyntheticPatternBinding(d.Identifier))
                boundNames.Add(d.Identifier);
        }
        if (boundNames.Count == 0) return;
        var varNames = new HashSet<string>(StringComparer.Ordinal);
        // Only collect `var` declarations (not function declarations) from the
        // body's direct statements. Function declarations in blocks hoist to the
        // enclosing function scope per Annex B.3.3, not to the for-body scope.
        foreach (var stmt in (body is BlockStatementNode b ? b.Statements : new[] { body }))
        {
            if (stmt is VariableDeclarationStatementNode vdecl && vdecl.Kind is "var")
                foreach (var d in vdecl.Declarators) varNames.Add(d.Identifier);
        }
        foreach (var name in boundNames)
            if (varNames.Contains(name))
                throw new JsParserException(
                    $"For-loop body re-declares variable '{name}' from the head declaration.");
    }

    private static void CollectBoundNames(BindingPatternNode pattern, HashSet<string> names)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode id:
                if (!IsSyntheticPatternBinding(id.Name)) names.Add(id.Name);
                break;
            case ArrayBindingPatternNode arr:
                foreach (var el in arr.Elements)
                    if (el.Target is { } t) CollectBoundNames(t, names);
                break;
            case ObjectBindingPatternNode obj:
                foreach (var prop in obj.Properties)
                    CollectBoundNames(prop.Target, names);
                if (obj.Rest is { } r) CollectBoundNames(r, names);
                break;
        }
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

    // ECMA-262 14.10.1: a ReturnStatement is only valid inside a FunctionBody.
    // Incremented while parsing any function/method/arrow block body so a `return`
    // at script/eval top level (depth 0) is rejected as an early SyntaxError.
    private int _functionBodyDepth;

    private BlockStatementNode ParseFunctionBlockBody(Func<BlockStatementNode> parse)
    {
        _functionBodyDepth++;
        // ECMA-262 15.1: directive prologue restarts for each function body
        // so "use strict" inside a function is recognised by the parser.
        var savedDirectivePrologue = _inDirectivePrologue;
        _inDirectivePrologue = true;
        try
        {
            return parse();
        }
        finally
        {
            _inDirectivePrologue = savedDirectivePrologue;
            _functionBodyDepth--;
        }
    }

    private ReturnStatementNode ParseReturnStatement()
    {
        if (_functionBodyDepth == 0)
        {
            throw new JsParserException("'return' statement is only valid inside a function.");
        }

        var start = Advance(); // return
        ExpressionNode? argument = null;
        if (!IsPunctuator(";") &&
            !IsPunctuator("}") &&
            !Is(TokenKind.EndOfFile) &&
            !HasLineTerminatorBetween(start, Current()))
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
        var (parameterInfo, body) = ParseFunctionParametersAndBody(
            allowYieldInBody: isGenerator,
            allowAwaitInBody: isAsync);
        var parameters = parameterInfo.Parameters;
        ValidateStrictModeFunctionName(name, body.Statements);
        ValidateDirectivePrologueStrictStringEscapes(body.Statements);
        // ECMA-262 early errors: same validation as function expressions.
        ValidateClassMethodEarlyErrors(
            parameterInfo,
            body,
            forbidAwaitIdentifier: isAsync,
            forbidYieldIdentifier: isGenerator,
            strictMode: _strictMode || ContainsUseStrictDirective(body.Statements),
            rejectSuperCallInBody: true);
        return new FunctionDeclarationNode(
            name.Text,
            parameters,
            body,
            MergeSpan(start.Span, body.Span),
            IsAsync: isAsync,
            IsGenerator: isGenerator,
            HasSimpleParameterList: parameterInfo.IsSimple,
            RestParameterIndex: parameterInfo.RestParameterIndex,
            ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults);
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
            string catchIdentifier;
            BindingPatternNode? catchPattern = null;

            // ES2019 optional catch binding (https://tc39.es/ecma262/#sec-try-statement):
            // `try { } catch { }` with no parameter. Ubiquitous in minified bundles
            // (every modern web app uses it), so its absence here previously forced the
            // entire script onto the legacy engine.
            if (IsPunctuator("("))
            {
                Advance(); // (
                if (IsIdentifierLike(Current()))
                {
                    catchIdentifier = Advance().Text;
                    // ECMA-262 13.15.1: `catch (eval)` and `catch (arguments)` in strict mode.
                    if (_strictMode && (catchIdentifier == "eval" || catchIdentifier == "arguments"))
                        throw new JsParserException($"'{catchIdentifier}' may not be used as a catch parameter in strict mode.");
                    // ECMA-262 15.7: `catch (await)` is forbidden in class static blocks.
                    if (_classStaticBlockDepth > 0 && string.Equals(catchIdentifier, "await", StringComparison.Ordinal))
                        throw new JsParserException("'await' may not be used as a binding name inside a class static block.");
                }
                else
                {
                    // CatchParameter may be a BindingPattern (`catch ([a, b])`,
                    // `catch ({ message })`); capture it so the binding is created.
                    catchPattern = ParseBindingPattern();
                    catchIdentifier = "<pattern>";
                }

                ExpectPunctuator(")");
            }
            else
            {
                catchIdentifier = "<no-binding>";
            }

            var catchBlock = ParseBlockStatement();
            // ECMA-262 13.15.1: catch parameter name must not conflict with
            // LexicallyDeclaredNames in the catch block.
            if (catchIdentifier != "<no-binding>" && catchIdentifier != "<pattern>")
            {
                var lexNames = CollectTopLevelLexicallyDeclaredNames(catchBlock.Statements);
                if (lexNames.Contains(catchIdentifier))
                    throw new JsParserException($"Catch parameter '{catchIdentifier}' conflicts with a lexical declaration in the catch block.");
            }
            if (Current().Kind == TokenKind.Keyword && Current().Text == "finally")
            {
                Advance(); // finally
                var finallyBlock = ParseBlockStatement();
                return new TryCatchFinallyStatementNode(tryBlock, catchIdentifier, catchBlock, finallyBlock, MergeSpan(start.Span, finallyBlock.Span), catchPattern);
            }

            return new TryCatchStatementNode(tryBlock, catchIdentifier, catchBlock, MergeSpan(start.Span, catchBlock.Span), catchPattern);
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
            throw new JsParserException($"Expected '{text}', found '{Current().Text}'{Where()}.");
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

    // ECMA-262 15.1.1 early error: a strict-mode function's BindingIdentifier may
    // not be `eval` or `arguments`. Strict applies when the surrounding code is
    // strict or the body opens with a "use strict" directive.
    private void ValidateStrictModeFunctionName(Token name, IReadOnlyList<StatementNode> bodyStatements)
    {
        if ((_strictMode || ContainsUseStrictDirective(bodyStatements)) &&
            (string.Equals(name.Text, "eval", StringComparison.Ordinal) ||
             string.Equals(name.Text, "arguments", StringComparison.Ordinal)))
        {
            throw new JsParserException(
                $"'{name.Text}' may not be used as a function name in strict mode.");
        }
    }

    // ECMA-262 13.15.1 / 13.4.1 early error: in strict-mode code the target of a
    // simple assignment or an update (++/--) expression may not be a direct
    // reference to `eval` or `arguments`.
    private void ValidateStrictAssignmentTarget(ExpressionNode target)
    {
        if (!_strictMode)
        {
            return;
        }

        if (target is IdentifierExpressionNode { Name: "eval" or "arguments" } id)
        {
            throw new JsParserException(
                $"'{id.Name}' may not be assigned to in strict mode.");
        }
    }

    // Strip redundant parentheses so early-error checks see the covered reference,
    // e.g. `delete (((x)))` is still a delete of the identifier `x`.
    private static ExpressionNode Unparenthesize(ExpressionNode expression)
    {
        while (expression is ParenthesizedExpressionNode parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static void ValidateClassNameIdentifier(Token name)
    {
        if (StrictModeReservedIdentifierNames.Contains(name.Text))
        {
            throw new JsParserException($"Reserved identifier '{name.Text}' is not allowed as a class name.");
        }
    }

    private ClassDeclarationNode ParseClassDeclaration()
    {
        var start = Advance(); // class
        var name = ExpectIdentifier();
        ValidateClassNameIdentifier(name);

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
            var nameToken = Advance();
            ValidateClassNameIdentifier(nameToken);
            name = nameToken.Text;
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

    private void ParseDecoratorList()
    {
        while (IsPunctuator("@"))
        {
            ParseDecorator();
        }
    }

    private void ParseDecorator()
    {
        ExpectPunctuator("@");

        if (IsPunctuator("("))
        {
            Advance();
            ParseDecoratorParenthesizedExpression();
            ExpectPunctuator(")");
            return;
        }

        ParseDecoratorMemberExpression();
        if (IsPunctuator("("))
        {
            _ = ParseCallArguments();
        }
    }

    private void ParseDecoratorParenthesizedExpression()
    {
        if (IsPunctuator("("))
        {
            Advance();
            ParseDecoratorParenthesizedExpression();
            ExpectPunctuator(")");
            return;
        }

        ParseDecoratorMemberExpression();
    }

    private void ParseDecoratorMemberExpression()
    {
        ParseDecoratorIdentifierReference();
        while (IsPunctuator("."))
        {
            Advance();
            if (Current().Kind != TokenKind.Identifier &&
                Current().Kind != TokenKind.Keyword &&
                Current().Kind != TokenKind.PrivateIdentifier)
            {
                throw new JsParserException($"Expected decorator member segment, found '{Current().Text}'.");
            }

            Advance();
        }
    }

    private void ParseDecoratorIdentifierReference()
    {
        var token = Current();
        if (token.Kind == TokenKind.Identifier)
        {
            Advance();
            return;
        }

        if (token.Kind == TokenKind.Keyword &&
            (token.Text == "await" || token.Text == "yield" || IsIdentifierLike(token)))
        {
            Advance();
            return;
        }

        throw new JsParserException($"Expected decorator identifier reference, found '{token.Text}'.");
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

            ParseDecoratorList();
            var memberStart = Current().Span;
            bool isStatic = false;
            // The 'static' modifier is a contextual keyword (lexed as Identifier).
            // Disambiguate against a method literally named "static" by peeking the
            // next token: if it's '(', the current token is the method name.
            if (IsUnescapedIdentifierLike(Current(), "static") && !IsPunctuatorAt(1, "("))
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
                _classStaticBlockDepth++;
                BlockStatementNode block;
                try
                {
                    block = ParseBlockStatement();
                }
                finally
                {
                    _classStaticBlockDepth--;
                }

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
            if (IsUnescapedIdentifierLike(Current(), "async") &&
                !HasLineTerminatorBetweenCurrentAnd(1) &&
                !IsPunctuatorAt(1, "(") &&
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
            if ((IsUnescapedIdentifierLike(Current(), "get") || IsUnescapedIdentifierLike(Current(), "set"))
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

                // ECMA-262 ClassStaticBlockDefinition Early Errors:
                // Field name cannot be "constructor" (instance or static).
                // Static field name cannot be "prototype".
                if (string.Equals(memberName, "constructor", StringComparison.Ordinal))
                    throw new JsParserException("Class field name 'constructor' is not allowed.");
                if (isStatic && string.Equals(memberName, "prototype", StringComparison.Ordinal))
                    throw new JsParserException("Static class field name 'prototype' is not allowed.");

                if (IsPunctuator(";"))
                {
                    Advance();
                }
                else if (!IsPunctuator("}") && Current().Span.Line == Previous().Span.Line)
                {
                    throw new JsParserException("Expected ';' after class field declaration.");
                }

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

            var (parameterInfo, body) = ParseFunctionParametersAndBody(
                allowYieldInBody: isGenerator,
                allowAwaitInBody: isAsync,
                strictModeOverride: true);
            var parameters = parameterInfo.Parameters;
            ValidateClassMethodEarlyErrors(
                parameterInfo,
                body,
                forbidAwaitIdentifier: isAsync,
                forbidYieldIdentifier: isGenerator,
                strictMode: true,
                rejectSuperCallInBody: kind != ClassMemberKind.Constructor,
                allowSuperProperty: kind != ClassMemberKind.Constructor);
            ValidateAccessorArity(kind == ClassMemberKind.Getter, kind == ClassMemberKind.Setter, parameterInfo);
            var fn = new FunctionExpressionNode(
                memberName,
                parameters,
                body,
                MergeSpan(memberStart, body.Span),
                IsAsync: isAsync,
                IsGenerator: isGenerator,
                RestParameterIndex: parameterInfo.RestParameterIndex,
                ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults,
                // Class methods/accessors are MethodDefinitions (no prototype, no
                // [[Construct]]); the constructor is forced to FunctionKind.Constructor
                // by the compiler's _isClassConstructor path regardless of this flag.
                IsMethod: true);
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

    private bool HasLineTerminatorBetweenCurrentAnd(int offset)
    {
        var idx = Math.Min(_index + offset, _tokens.Count - 1);
        return _tokens[idx].Span.Line != Current().Span.Line;
    }

    private static bool HasLineTerminatorBetween(Token left, Token right) =>
        right.Span.Line != left.Span.Line;

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
        var sawDefault = false;
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
                // ECMA-262 14.12.1: a CaseBlock may contain at most one DefaultClause.
                if (sawDefault)
                {
                    throw new JsParserException("More than one default clause in switch statement.");
                }
                sawDefault = true;
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
        string? label = null;
        if (!IsPunctuator(";") && !IsPunctuator("}") && !Is(TokenKind.EndOfFile)
            && IsIdentifierLike(Current()) && !HasLineTerminatorBetween(token, Current()))
        {
            label = Advance().Text;
        }
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new BreakStatementNode(label, token.Span);
    }

    private ContinueStatementNode ParseContinueStatement()
    {
        var token = Advance(); // continue
        string? label = null;
        if (!IsPunctuator(";") && !IsPunctuator("}") && !Is(TokenKind.EndOfFile)
            && IsIdentifierLike(Current()) && !HasLineTerminatorBetween(token, Current()))
        {
            label = Advance().Text;
        }
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new ContinueStatementNode(label, token.Span);
    }

    private DebuggerStatementNode ParseDebuggerStatement()
    {
        var token = Advance(); // debugger
        if (IsPunctuator(";"))
        {
            Advance();
        }

        return new DebuggerStatementNode(token.Span);
    }

    private T ParseWithExpressionContext<T>(bool allowYieldExpression, bool allowAwaitExpression, Func<T> parse)
    {
        var previousAllowYield = _allowYieldExpression;
        var previousAllowAwait = _allowAwaitExpression;
        _allowYieldExpression = allowYieldExpression;
        _allowAwaitExpression = allowAwaitExpression;
        try
        {
            return parse();
        }
        finally
        {
            _allowYieldExpression = previousAllowYield;
            _allowAwaitExpression = previousAllowAwait;
        }
    }

    private (ParameterListInfo Parameters, BlockStatementNode Body) ParseFunctionParametersAndBody(
        bool allowYieldInBody,
        bool allowAwaitInBody,
        bool? strictModeOverride = null)
    {
        var previousStrictMode = _strictMode;
        if (strictModeOverride.HasValue)
        {
            _strictMode = strictModeOverride.Value;
        }

        // ECMA-262 15.7: 'await' may not be a binding identifier at the top level
        // of a ClassStaticBlock, but nested functions/arrows inside the static block
        // are not at the top level — they clear the restriction. Save and restore
        // the depth so that declarations in nested functions are not rejected.
        var previousClassStaticBlockDepth = _classStaticBlockDepth;
        _classStaticBlockDepth = 0;

        try
        {
            var parameterInfo = ParseWithExpressionContext(
                allowYieldExpression: false,
                allowAwaitExpression: false,
                parse: ParseParameterList);
            var body = ParseWithExpressionContext(
                allowYieldExpression: allowYieldInBody,
                allowAwaitExpression: allowAwaitInBody,
                parse: () => ParseFunctionBlockBody(ParseBlockStatement));

            // ECMA-262 15.1.1 / 15.2.1 early errors: in strict-mode code `eval` and
            // `arguments` may not be bound as parameter names. The function is strict
            // when the surrounding code is strict or its own body opens with a
            // "use strict" directive.
            var effectiveStrict = _strictMode || ContainsUseStrictDirective(body.Statements);
            if (effectiveStrict)
            {
                foreach (var parameter in parameterInfo.Parameters)
                {
                    if (!IsSyntheticPatternBinding(parameter) &&
                        (string.Equals(parameter, "eval", StringComparison.Ordinal) ||
                         string.Equals(parameter, "arguments", StringComparison.Ordinal)))
                    {
                        throw new JsParserException(
                            $"'{parameter}' may not be used as a parameter name in strict mode.");
                    }
                }
            }

            // ECMA-262 15.1.1: duplicate parameter names are forbidden in strict-mode
            // code and whenever the parameter list is non-simple (defaults, rest, or
            // destructuring). They remain legal only in a sloppy simple-list function.
            if (parameterInfo.HasDuplicateNames && (effectiveStrict || !parameterInfo.IsSimple))
            {
                throw new JsParserException(
                    "Duplicate parameter names are not allowed in this context.");
            }

            return (parameterInfo, body);
        }
        finally
        {
            _strictMode = previousStrictMode;
            _classStaticBlockDepth = previousClassStaticBlockDepth;
        }
    }

    private ParameterListInfo ParseParameterList()
    {
        var parameters = new List<string>();
        var parameterBindings = new List<BindingPatternNode?>();
        var parameterDefaults = new List<ExpressionNode?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var isSimple = true;
        var hasDuplicateNames = false;
        var restHasInitializer = false;
        var hasTrailingCommaAfterRest = false;
        var hasSuperCallInInitializers = false;
        var hasYieldReferenceInInitializers = false;
        var hasAwaitReferenceInInitializers = false;
        var restParameterIndex = -1;

        ExpectPunctuator("(");
        while (!Is(TokenKind.EndOfFile) && !IsPunctuator(")"))
        {
            var rest = IsPunctuator("...");
            if (rest)
            {
                Advance();
                isSimple = false;
                if (restParameterIndex >= 0)
                {
                    throw new JsParserException("Only one rest parameter is allowed.");
                }

                restParameterIndex = parameters.Count;
            }

            var binding = ParseParameterBinding();
            var bindingName = binding.Identifier;
            parameters.Add(bindingName);
            parameterBindings.Add(binding.Pattern);
            parameterDefaults.Add(null);
            if (bindingName.StartsWith("__pattern", StringComparison.Ordinal))
            {
                isSimple = false;
            }
            else if (!seen.Add(bindingName))
            {
                hasDuplicateNames = true;
            }

            if (IsPunctuator("="))
            {
                if (rest)
                {
                    restHasInitializer = true;
                }

                isSimple = false;
                Advance();
                var initializer = ParseExpression(2);
                if (!rest)
                {
                    parameterDefaults[parameterDefaults.Count - 1] = initializer;
                }

                if (ContainsSuperCallInExpression(initializer))
                {
                    hasSuperCallInInitializers = true;
                }

                if (ContainsYieldReferenceInExpression(initializer))
                {
                    hasYieldReferenceInInitializers = true;
                }

                if (ContainsIdentifierReferenceInExpression(initializer, "await"))
                {
                    hasAwaitReferenceInInitializers = true;
                }
            }

            if (IsPunctuator(","))
            {
                Advance();
                if (rest)
                {
                    hasTrailingCommaAfterRest = true;
                }

                continue;
            }

            break;
        }

        ExpectPunctuator(")");
        return new ParameterListInfo(
            parameters,
            parameterBindings,
            parameterDefaults,
            IsSimple: isSimple,
            HasDuplicateNames: hasDuplicateNames,
            RestHasInitializer: restHasInitializer,
            HasTrailingCommaAfterRest: hasTrailingCommaAfterRest,
            HasSuperCallInInitializers: hasSuperCallInInitializers,
            HasYieldReferenceInInitializers: hasYieldReferenceInInitializers,
            HasAwaitReferenceInInitializers: hasAwaitReferenceInInitializers,
            RestParameterIndex: restParameterIndex);
    }

    private ParsedVariableBinding ParseParameterBinding()
    {
        if (IsIdentifierLike(Current()))
        {
            var token = Advance();
            return new ParsedVariableBinding(token.Text, null, token.Span);
        }

        if (IsPunctuator("[") || IsPunctuator("{"))
        {
            var pattern = ParseBindingPattern();
            var name = $"__pattern{_syntheticBindingCounter++}";
            return new ParsedVariableBinding(name, pattern, pattern.Span);
        }

        throw new JsParserException($"Expected identifier, found '{Current().Text}'{Where()}.");
    }

    // Parse a sub-expression with [+In] restored (used at every bracketed /
    // parenthesised boundary). A no-op unless we are inside a for-head LHS.
    private ExpressionNode ParseExpressionAllowIn(int minBindingPower)
    {
        if (!_noIn)
        {
            return ParseExpression(minBindingPower);
        }

        _noIn = false;
        try
        {
            return ParseExpression(minBindingPower);
        }
        finally
        {
            _noIn = true;
        }
    }

    private ExpressionNode ParseExpression(int minBindingPower)
    {
        EnterRecursion();
        try
        {
            return ParseExpressionCore(minBindingPower);
        }
        finally
        {
            ExitRecursion();
        }
    }

    private ExpressionNode ParseExpressionCore(int minBindingPower)
    {
        if (TryParseArrowFunction(out var arrow))
        {
            return arrow;
        }

        var left = ParsePrefix(minBindingPower);

        while (true)
        {
            if (IsPunctuator("?") && minBindingPower <= 4)
            {
                Advance();
                var consequent = ParseExpression(0);
                ExpectPunctuator(":");
                var alternate = ParseExpression(2);
                left = new ConditionalExpressionNode(left, consequent, alternate, MergeSpan(left.Span, alternate.Span));
                continue;
            }

            if (IsPunctuator("?."))
            {
                Advance();

                if (IsPunctuator("("))
                {
                    var args = ParseCallArguments();
                    var end = Previous();
                    left = new OptionalCallExpressionNode(left, args, MergeSpan(left.Span, end.Span));
                    continue;
                }

                if (IsPunctuator("["))
                {
                    Advance();
                    var propExpr = ParseExpression(0);
                    ExpectPunctuator("]");
                    var close = Previous();
                    left = new OptionalMemberExpressionNode(left, string.Empty, Computed: true, PropertyExpression: propExpr, MergeSpan(left.Span, close.Span));
                    continue;
                }

                var optionalProperty = ExpectPropertyNameAfterDot();
                left = new OptionalMemberExpressionNode(left, optionalProperty.Text, Computed: false, PropertyExpression: null, MergeSpan(left.Span, optionalProperty.Span));
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

            if ((IsPunctuator("++") || IsPunctuator("--")) && minBindingPower <= 34 &&
                !HasLineTerminatorBetween(Previous(), Current()))
            {
                // ECMA-262 13.4: UpdateExpression has `[no LineTerminator here]`
                // before the postfix ++/--. A newline forces ASI, so `x \n ++`
                // is `x; ++` (a prefix operator missing its operand), not `x++`.
                if (!IsUpdateTarget(left))
                {
                    throw new JsParserException("Invalid update expression target.");
                }

                ValidateStrictAssignmentTarget(left);
                var updateToken = Advance();
                left = BuildUpdateAssignment(left, updateToken.Text, updateToken.Span, isPostfix: true);
                continue;
            }

            if (IsAssignmentOperator(Current()) && minBindingPower <= 9)
            {
                // ECMA-262 13.15.1 Static Semantics: AssignmentTargetType.
                // The LHS of an assignment must be "simple" (Identifier or
                // MemberExpression) or — for `=` only — an array/object
                // literal that's destructuring-compatible. Anything else is
                // an early SyntaxError.
                if (!IsValidAssignmentTarget(left))
                {
                    if (_allowAnnexBForInInitializerTail &&
                        left is BinaryExpressionNode { Operator: "in" })
                    {
                        break;
                    }

                    throw new JsParserException(
                        $"Invalid assignment target ({left.GetType().Name}){Where()}.");
                }

                // A destructuring pattern (array/object literal) is only valid with
                // plain `=`; for `=` it must be a structurally valid AssignmentPattern.
                if (left is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode)
                {
                    if (Current().Text != "=")
                    {
                        throw new JsParserException(
                            $"Invalid destructuring assignment target{Where()}.");
                    }

                    ValidateAssignmentPattern(left);
                }

                ValidateStrictAssignmentTarget(left);
                var op = Advance().Text;
                // ECMA-262 13.15 AssignmentExpression : LeftHandSideExpression
                // AssignmentOperator AssignmentExpression — the RHS is itself
                // an AssignmentExpression, which is right-associative and
                // includes every operator tighter than the comma operator
                // (which has leftBp=1 in this table). Parse the RHS at
                // binding power 2 so we accept right-side `=` (right-assoc
                // chained assignment) AND any binary operator with
                // leftBp >= 2 (||, ??, &&, |, ^ — bp 5..9 — would otherwise
                // be wrongly left-associated as `(a = b) op c`).
                var assignmentRight = ParseExpression(2);
                left = BuildAssignmentNode(left, op, assignmentRight, MergeSpan(left.Span, assignmentRight.Span));
                continue;
            }

            if (!TryGetInfixBindingPower(Current(), out _, out var leftBp, out var rightBp) || leftBp < minBindingPower)
            {
                break;
            }

            var opToken = Advance();

            // ECMA-262: ExponentiationExpression does not allow UnaryExpression
            // on its left-hand side (only UpdateExpression). Unary operators like
            // ~, !, delete, void, typeof cannot directly precede **.
            if (opToken.Text == "**" && left is UnaryExpressionNode)
                throw new JsParserException("Unary expression cannot be the left-hand side of exponentiation.");

            var right = ParseExpression(rightBp);
            var span = MergeSpan(left.Span, right.Span);
            left = new BinaryExpressionNode(opToken.Text, left, right, span);
        }

        return left;
    }

    private ExpressionNode ParsePrefix(int minBindingPower)
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

            ValidateStrictAssignmentTarget(target);
            return BuildUpdateAssignment(target, op.Text, op.Span, isPostfix: false);
        }

        if (token.Kind == TokenKind.Punctuator && (token.Text == "!" || token.Text == "-" || token.Text == "+" || token.Text == "~"))
        {
            var op = Advance();
            var operand = ParseExpression(40);
            return new UnaryExpressionNode(op.Text, operand, MergeSpan(op.Span, operand.Span));
        }

        if (token.Kind == TokenKind.Keyword && (token.Text == "typeof" || token.Text == "delete" || token.Text == "void"))
        {
            var op = Advance();
            var operand = ParseExpression(40);

            // ECMA-262 13.5.1.1 early error: in strict-mode code the operand of
            // `delete` may not be a bare variable reference (a parenthesized
            // reference unwraps to the same). Deleting a property is still fine.
            if (string.Equals(op.Text, "delete", StringComparison.Ordinal) && _strictMode &&
                Unparenthesize(operand) is IdentifierExpressionNode)
            {
                throw new JsParserException("Delete of an unqualified identifier is not allowed in strict mode.");
            }

            return new UnaryExpressionNode(op.Text, operand, MergeSpan(op.Span, operand.Span));
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "await")
        {
            if (_allowAwaitExpression)
            {
                var op = Advance();
                var operand = ParseExpression(40);
                return new UnaryExpressionNode(op.Text, operand, MergeSpan(op.Span, operand.Span));
            }

            if (IsIdentifierLike(token))
            {
                Advance();
                return new IdentifierExpressionNode(token.Text, token.Span);
            }
        }

        if (token.Kind == TokenKind.Keyword && token.Text == "yield")
        {
            if (!_allowYieldExpression)
            {
                if (IsIdentifierLike(token))
                {
                    // Legacy FenJS behavior: allow yield-like expressions in non-generator
                    // function bodies when the token is not an assignment target.
                    if (minBindingPower <= 2 && !IsPunctuatorAt(1, "="))
                    {
                        // Fall through to parse as a yield expression.
                    }
                    else
                    {
                        Advance();
                        return new IdentifierExpressionNode(token.Text, token.Span);
                    }
                }
                else
                {
                    throw new JsParserException($"Unexpected token '{token.Text}' ({token.Kind}).");
                }
            }
            else if (minBindingPower > 2)
            {
                if (IsIdentifierLike(token))
                {
                    Advance();
                    return new IdentifierExpressionNode(token.Text, token.Span);
                }

                throw new JsParserException($"Unexpected token '{token.Text}' ({token.Kind}).");
            }

            var op = Advance();
            var delegated = false;
            if (IsPunctuator("*") && !HasLineTerminatorBetween(op, Current()))
            {
                delegated = true;
                Advance();
            }

            if (IsPunctuator(";") || IsPunctuator("}") || IsPunctuator(")") || IsPunctuator("]") || IsPunctuator(",") || IsPunctuator(":") || Is(TokenKind.EndOfFile))
            {
                return new UnaryExpressionNode(op.Text, new IdentifierExpressionNode("undefined", op.Span), op.Span);
            }

            var operand = ParseExpression(2);
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

        if (token.Kind == TokenKind.Keyword && token.Text == "import")
        {
            // ECMA-262 13.3.10 ImportCall + 13.3.12 ImportMeta.
            var importToken = Advance();
            if (IsPunctuator("("))
            {
                Advance();
                // ECMA-262: ImportCall arguments use [+In] — `in` is always an
                // operator, never a for-in marker, even inside a for-header.
                var savedNoIn = _noIn;
                _noIn = false;
                var specifier = ParseExpression(2);
                // ECMA-262 13.3.10.1: optional trailing comma + assertion arg.
                // We parse and discard a second argument so import(spec, {}) still
                // parses; the host loader currently ignores it.
                if (IsPunctuator(","))
                {
                    Advance();
                    if (!IsPunctuator(")"))
                    {
                        _ = ParseExpression(2);
                        // ECMA-262 13.3.10.1: trailing comma after second arg is valid.
                        if (IsPunctuator(","))
                            Advance();
                    }
                }
                _noIn = savedNoIn;
                ExpectPunctuator(")");
                return new ImportCallExpressionNode(specifier, MergeSpan(importToken.Span, Previous().Span));
            }
            if (IsPunctuator("."))
            {
                Advance();
                var prop = ExpectPropertyNameAfterDot();
                if (string.Equals(prop.Text, "meta", StringComparison.Ordinal))
                {
                    return new ImportMetaExpressionNode(MergeSpan(importToken.Span, prop.Span));
                }
                if (string.Equals(prop.Text, "source", StringComparison.Ordinal))
                {
                    // import.source(AssignmentExpression) — ES2025 Import Source proposal.
                    // import.source without parens is a SyntaxError per the grammar.
                    ExpectPunctuator("(");
                    var savedNoIn = _noIn;
                    _noIn = false;
                    var specifier = ParseExpression(2);
                    // Optional trailing comma + import attributes (parse and discard).
                    if (IsPunctuator(","))
                    {
                        Advance();
                        if (!IsPunctuator(")"))
                        {
                            _ = ParseExpression(2);
                            // ECMA-262: trailing comma after second arg is valid.
                            if (IsPunctuator(","))
                                Advance();
                        }
                    }
                    _noIn = savedNoIn;
                    ExpectPunctuator(")");
                    return new ImportSourceExpressionNode(specifier, MergeSpan(importToken.Span, Previous().Span));
                }
                if (string.Equals(prop.Text, "defer", StringComparison.Ordinal))
                {
                    // import.defer(AssignmentExpression) — ES2025 Import Defer proposal.
                    // import.defer without parens is a SyntaxError per the grammar.
                    ExpectPunctuator("(");
                    var savedNoIn = _noIn;
                    _noIn = false;
                    var specifier = ParseExpression(2);
                    // Optional trailing comma + import attributes (parse and discard).
                    if (IsPunctuator(","))
                    {
                        Advance();
                        if (!IsPunctuator(")"))
                        {
                            _ = ParseExpression(2);
                            // ECMA-262: trailing comma after second arg is valid.
                            if (IsPunctuator(","))
                                Advance();
                        }
                    }
                    _noIn = savedNoIn;
                    ExpectPunctuator(")");
                    return new ImportDeferExpressionNode(specifier, MergeSpan(importToken.Span, Previous().Span));
                }
                throw new JsParserException($"Expected 'meta', 'source', or 'defer' after 'import.', found '{prop.Text}'.");
            }
            throw new JsParserException($"Unexpected token '{Current().Text}' ({Current().Kind}) after 'import'.");
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

        if (token.Kind == TokenKind.Keyword && IsIdentifierLike(token))
        {
            Advance();
            return new IdentifierExpressionNode(token.Text, token.Span);
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

        if (token.Kind == TokenKind.BigInt)
        {
            Advance();
            return new BigIntLiteralExpressionNode(token.Text, token.Span);
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
            // ECMA-262 12.2.8.2 / 22.2.3.1: validate the regex literal pattern
            // at parse time so that invalid regex syntax (e.g. bad Unicode property
            // escapes) produces a SyntaxError in parse-negative tests.
            var raw = token.Text; // "/pattern/flags"
            var lastSlash = raw.LastIndexOf('/');
            var pattern = raw.Substring(1, lastSlash - 1);
            var flags = lastSlash + 1 < raw.Length ? raw.Substring(lastSlash + 1) : string.Empty;
            try
            {
                Regex.RegExpCompiler.ValidateLiteralSyntax(token.Text);
            }
            catch (Regex.RegexSyntaxError ex)
            {
                throw new JsParserException(ex.Message);
            }
            return new RegexLiteralExpressionNode(token.Text, token.Span);
        }

        if (token.Kind == TokenKind.Template)
        {
            return ParseTemplateLiteral(Advance());
        }

        if (IsPunctuator("("))
        {
            var open = Advance();
            // ( Expression[+In] ): a parenthesised group restores [+In].
            var savedNoIn = _noIn;
            _noIn = false;
            ExpressionNode expression;
            try
            {
                expression = ParseExpression(0);
            }
            finally
            {
                _noIn = savedNoIn;
            }
            ExpectPunctuator(")");
            var span = MergeSpan(open.Span, expression.Span);
            return new ParenthesizedExpressionNode(expression, span);
        }

        if (IsPunctuator("{") || IsPunctuator("["))
        {
            // Array/object literal members are AssignmentExpression[+In].
            var savedNoIn = _noIn;
            _noIn = false;
            try
            {
                return IsPunctuator("{") ? ParseObjectLiteral() : ParseArrayLiteral();
            }
            finally
            {
                _noIn = savedNoIn;
            }
        }

        throw new JsParserException($"Unexpected token '{token.Text}' ({token.Kind}){Where()}.");
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
            if (text.Length > 2 && TryParseRadix(text[2..], 16, out var hex))
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

        Token? nameToken = null;
        string? name = null;
        if (IsIdentifierLike(Current()))
        {
            nameToken = Current();
            name = Advance().Text;
        }

        var (parameterInfo, body) = ParseFunctionParametersAndBody(
            allowYieldInBody: isGenerator,
            allowAwaitInBody: isAsync);
        var parameters = parameterInfo.Parameters;
        if (nameToken is { } fnNameToken)
        {
            ValidateStrictModeFunctionName(fnNameToken, body.Statements);
        }

        ValidateDirectivePrologueStrictStringEscapes(body.Statements);
        // ECMA-262 early errors: yield/await as binding identifiers, super in body,
        // duplicate parameters (in strict mode), eval/arguments in strict parameters,
        // and restricted identifiers in the body must surface at parse time.
        ValidateClassMethodEarlyErrors(
            parameterInfo,
            body,
            forbidAwaitIdentifier: isAsync,
            forbidYieldIdentifier: isGenerator,
            strictMode: _strictMode || ContainsUseStrictDirective(body.Statements),
            rejectSuperCallInBody: true);
        return new FunctionExpressionNode(
            name,
            parameters,
            body,
            MergeSpan(start.Span, body.Span),
            IsAsync: isAsync,
            IsGenerator: isGenerator,
            HasSimpleParameterList: parameterInfo.IsSimple,
            RestParameterIndex: parameterInfo.RestParameterIndex,
            ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults);
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

        var callee = ParsePrefix(40);
        callee = ParsePostfix(callee, minBindingPower: 35, allowCall: false);

        // ECMA-262: ImportCall is a CallExpression, not a MemberExpression.
        // `new import(x)`, `new import.source(x)`, `new import.defer(x)` are
        // all SyntaxErrors — ImportCall cannot be preceded by `new`.
        if (callee is ImportCallExpressionNode or ImportSourceExpressionNode or ImportDeferExpressionNode)
        {
            throw new JsParserException("ImportCall cannot be preceded by 'new'.");
        }

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
        // ECMA-262 B.3.1: count `__proto__: value` colon-form data properties.
        // More than one is a SyntaxError for a real object literal (deferred —
        // permitted when reinterpreted as an assignment pattern).
        var protoSetterCount = 0;
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

                var (parameterInfo, body) = ParseFunctionParametersAndBody(
                    allowYieldInBody: false,
                    allowAwaitInBody: false);
                var parameters = parameterInfo.Parameters;
                var strictObjectMethod = _strictMode || ContainsUseStrictDirective(body.Statements);
                ValidateClassMethodEarlyErrors(
                    parameterInfo,
                    body,
                    forbidAwaitIdentifier: false,
                    forbidYieldIdentifier: false,
                    strictMode: strictObjectMethod,
                    rejectSuperCallInBody: true);
                ValidateAccessorArity(accessorKind.Text == "get", accessorKind.Text == "set", parameterInfo);
                var accessorFnName = accessorKey ?? accessorKind.Text;
                var accessorFn = new FunctionExpressionNode(accessorFnName, parameters, body, MergeSpan(accessorKind.Span, body.Span), HasSimpleParameterList: parameterInfo.IsSimple, RestParameterIndex: parameterInfo.RestParameterIndex, ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults, IsMethod: true);
                var accessorPropKind = accessorKind.Text == "get" ? ObjectPropertyKind.Getter : ObjectPropertyKind.Setter;
                properties.Add(new ObjectPropertyNode(accessorKey, accessorComputedKey, accessorIsComputed, accessorFn, accessorFn.Span, accessorPropKind));
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

                var (parameterInfo, body) = ParseFunctionParametersAndBody(
                    allowYieldInBody: false,
                    allowAwaitInBody: true);
                var parameters = parameterInfo.Parameters;
                var strictObjectMethod = _strictMode || ContainsUseStrictDirective(body.Statements);
                ValidateClassMethodEarlyErrors(
                    parameterInfo,
                    body,
                    forbidAwaitIdentifier: true,
                    forbidYieldIdentifier: false,
                    strictMode: strictObjectMethod,
                    rejectSuperCallInBody: true);
                var methodFnName = methodKey ?? "async";
                var asyncMethodFn = new FunctionExpressionNode(
                    methodFnName,
                    parameters,
                    body,
                    MergeSpan(asyncStart.Span, body.Span),
                    IsAsync: true,
                    RestParameterIndex: parameterInfo.RestParameterIndex,
                    ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults, IsMethod: true);
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

                var (parameterInfo, body) = ParseFunctionParametersAndBody(
                    allowYieldInBody: true,
                    allowAwaitInBody: true);
                var parameters = parameterInfo.Parameters;
                var strictObjectMethod = _strictMode || ContainsUseStrictDirective(body.Statements);
                ValidateClassMethodEarlyErrors(
                    parameterInfo,
                    body,
                    forbidAwaitIdentifier: true,
                    forbidYieldIdentifier: true,
                    strictMode: strictObjectMethod,
                    rejectSuperCallInBody: true);
                var methodFnName = methodKey ?? "async*";
                var asyncMethodFn = new FunctionExpressionNode(
                    methodFnName,
                    parameters,
                    body,
                    MergeSpan(asyncStart.Span, body.Span),
                    IsAsync: true,
                    IsGenerator: true,
                    RestParameterIndex: parameterInfo.RestParameterIndex,
                    ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults, IsMethod: true);
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

                var (parameterInfo, body) = ParseFunctionParametersAndBody(
                    allowYieldInBody: true,
                    allowAwaitInBody: false);
                var parameters = parameterInfo.Parameters;
                var strictObjectMethod = _strictMode || ContainsUseStrictDirective(body.Statements);
                ValidateClassMethodEarlyErrors(
                    parameterInfo,
                    body,
                    forbidAwaitIdentifier: false,
                    forbidYieldIdentifier: true,
                    strictMode: strictObjectMethod,
                    rejectSuperCallInBody: true);
                var methodFnName = methodKey ?? "*";
                var methodFn = new FunctionExpressionNode(
                    methodFnName,
                    parameters,
                    body,
                    MergeSpan(methodStart.Span, body.Span),
                    IsGenerator: true,
                    RestParameterIndex: parameterInfo.RestParameterIndex,
                    ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults, IsMethod: true);
                properties.Add(new ObjectPropertyNode(methodKey, methodComputedKey, methodIsComputed, methodFn, methodFn.Span));
                if (IsPunctuator(","))
                {
                    Advance();
                    continue;
                }

                break;
            }

            if (IsPunctuator("..."))
            {
                var spread = Advance();
                var argument = ParseExpression(2);
                var spreadExpression = new SpreadElementExpressionNode(argument, MergeSpan(spread.Span, argument.Span));
                properties.Add(new ObjectPropertyNode(null, null, false, spreadExpression, spreadExpression.Span));
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
            var isCoverInitializedName = false;
            if (IsPunctuator("("))
            {
                var (parameterInfo, body) = ParseFunctionParametersAndBody(
                    allowYieldInBody: false,
                    allowAwaitInBody: false);
                var parameters = parameterInfo.Parameters;
                var strictObjectMethod = _strictMode || ContainsUseStrictDirective(body.Statements);
                ValidateClassMethodEarlyErrors(
                    parameterInfo,
                    body,
                    forbidAwaitIdentifier: false,
                    forbidYieldIdentifier: false,
                    strictMode: strictObjectMethod,
                    rejectSuperCallInBody: true);
                value = new FunctionExpressionNode(key, parameters, body, MergeSpan(keyToken.Span, body.Span), HasSimpleParameterList: parameterInfo.IsSimple, RestParameterIndex: parameterInfo.RestParameterIndex, ParameterBindings: parameterInfo.ParameterBindings, ParameterDefaults: parameterInfo.ParameterDefaults, IsMethod: true);
            }
            else if (IsPunctuator(":"))
            {
                Advance();
                value = ParseExpression(2);
                // A non-computed `__proto__: value` is a prototype setter.
                if (!isComputed && string.Equals(key, "__proto__", StringComparison.Ordinal))
                {
                    protoSetterCount++;
                }
            }
            else if (IsPunctuator("="))
            {
                // Cover-initialized name `{ x = init }`: the shorthand target `x`
                // must be a valid IdentifierReference (12.6.1: a ReservedWord —
                // including one spelled with escapes — is not an Identifier).
                if (!IsIdentifierLike(keyToken))
                {
                    throw new JsParserException(
                        $"'{key}' is a reserved word and cannot be a shorthand property{Where()}.");
                }
                if (key is not null && _strictMode && IsStrictModeFutureReservedWord(key))
                {
                    throw new JsParserException(
                        $"'{key}' is a reserved word and cannot be a shorthand property in strict mode{Where()}.");
                }

                if (_classStaticBlockDepth > 0 && key is not null &&
                    (string.Equals(key, "await", StringComparison.Ordinal) ||
                     string.Equals(key, "arguments", StringComparison.Ordinal)))
                {
                    throw new JsParserException(
                        $"'{key}' may not be used as a shorthand property name inside a class static block.");
                }

                Advance();
                value = ParseExpression(2);
                isCoverInitializedName = true;
            }
            else if (!isComputed && key is not null)
            {
                // Bare shorthand `{ x }` is also an IdentifierReference and must
                // not be a reserved word (escaped reserved words included).
                if (!IsIdentifierLike(keyToken))
                {
                    throw new JsParserException(
                        $"'{key}' is a reserved word and cannot be a shorthand property{Where()}.");
                }
                // ECMA-262 12.2.6.1: FutureReservedWords are disallowed as
                // shorthand IdentifierReferences in strict mode.
                if (key is not null && _strictMode && IsStrictModeFutureReservedWord(key))
                {
                    throw new JsParserException(
                        $"'{key}' is a reserved word and cannot be a shorthand property in strict mode{Where()}.");
                }

                // ECMA-262 15.7: 'await'/'arguments' are not valid shorthand
                // IdentifierReferences in class static blocks.
                if (_classStaticBlockDepth > 0 && key is not null &&
                    (string.Equals(key, "await", StringComparison.Ordinal) ||
                     string.Equals(key, "arguments", StringComparison.Ordinal)))
                {
                    throw new JsParserException(
                        $"'{key}' may not be used as a shorthand property name inside a class static block.");
                }

                value = new IdentifierExpressionNode(key!, keyToken.Span);
            }
            else
            {
                throw new JsParserException($"Expected ':' or '=' after object property key, found '{Current().Text}'.");
            }
            properties.Add(new ObjectPropertyNode(key, computedKey, isComputed, value, value.Span, IsCoverInitializedName: isCoverInitializedName));
            if (IsPunctuator(","))
            {
                Advance();
                continue;
            }

            break;
        }

        ExpectPunctuator("}");
        var close = Previous();
        var literal = new ObjectLiteralExpressionNode(
            properties, MergeSpan(open.Span, close.Span), HasDuplicateProtoSetter: protoSetterCount > 1);
        if (protoSetterCount > 1)
        {
            // Defer the error: legal if this literal is reinterpreted as an
            // ObjectAssignmentPattern (cleared in ValidateObjectAssignmentPattern);
            // otherwise ParseProgram throws once parsing completes.
            _pendingDuplicateProtoLiterals.Add(literal);
        }

        // ECMA-262 12.2.6.1: CoverInitializedName cannot appear in a real
        // object literal. Defer the error because this may be reinterpreted
        // as an ObjectAssignmentPattern.
        if (properties.Any(p => p.IsCoverInitializedName))
        {
            _pendingCoverInitializedNameLiterals.Add(literal);
        }

        return literal;
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
                elements.Add(new ElisionExpressionNode(comma.Span));
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
                // ECMA-262 13.2.5.1: trailing commas are allowed after any
                // ArrayLiteral element, including SpreadElement (ES2018+).
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
                expression = ParseArrowFunctionBody(
                    new[] { parameter },
                    new BindingPatternNode?[] { null },
                    _tokens[saved].Span,
                    isAsync: true,
                    restParameterIndex: -1);
                return true;
            }

            if (IsPunctuator("("))
            {
                Advance();
                var asyncParameters = new List<string>();
                var asyncParameterBindings = new List<BindingPatternNode?>();
                var asyncParameterDefaults = new List<ExpressionNode?>();
                var asyncRestParameterIndex = -1;
                var asyncValid = true;
                if (!IsPunctuator(")"))
                {
                    while (true)
                    {
                        if (IsPunctuator("..."))
                        {
                            Advance();
                            if (asyncRestParameterIndex >= 0)
                            {
                                asyncValid = false;
                                break;
                            }

                            asyncRestParameterIndex = asyncParameters.Count;
                        }
                        if (!(IsIdentifierLike(Current()) || IsPunctuator("[") || IsPunctuator("{")))
                        {
                            asyncValid = false;
                            break;
                        }

                        try
                        {
                            var binding = ParseParameterBinding();
                            asyncParameters.Add(binding.Identifier);
                            asyncParameterBindings.Add(binding.Pattern);
                            asyncParameterDefaults.Add(null);
                            if (IsPunctuator("=") && !PeekIsPunctuator(1, ">"))
                            {
                                // ECMA-262 14.1: rest parameters may not have a default.
                                if (asyncRestParameterIndex == asyncParameters.Count - 1)
                                {
                                    throw new JsParserException("Rest parameter may not have a default value.");
                                }
                                Advance();
                                var asyncDefault = ParseExpression(2);
                                asyncParameterDefaults[asyncParameterDefaults.Count - 1] = asyncDefault;
                            }
                        }
                        catch (JsParserException)
                        {
                            asyncValid = false;
                            break;
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

                var closeParen = Advance(); // )
                // ECMA-262 14.2: [no LineTerminator here] between parameters and =>
                if (!(IsPunctuator("=") && PeekIsPunctuator(1, ">")) || HasLineTerminatorBetween(closeParen, Current()))
                {
                    _index = saved;
                    return false;
                }

                Advance(); // =
                Advance(); // >
                expression = ParseArrowFunctionBody(asyncParameters, asyncParameterBindings, _tokens[saved].Span, isAsync: true, restParameterIndex: asyncRestParameterIndex, parameterDefaults: asyncParameterDefaults);
                return true;
            }

            _index = saved;
        }

        if (IsIdentifierLike(Current()) && PeekIsPunctuator(1, "=") && PeekIsPunctuator(2, ">"))
        {
            var parameter = Advance().Text;
            Advance(); // =
            Advance(); // >
            expression = ParseArrowFunctionBody(
                new[] { parameter },
                new BindingPatternNode?[] { null },
                _tokens[saved].Span,
                isAsync: false,
                restParameterIndex: -1);
            return true;
        }

        if (IsPunctuator("("))
        {
            Advance();
            var parameters = new List<string>();
            var parameterBindings = new List<BindingPatternNode?>();
            var parameterDefaults = new List<ExpressionNode?>();
            var restParameterIndex = -1;
            var valid = true;
            if (!IsPunctuator(")"))
            {
                while (true)
                {
                    if (IsPunctuator("..."))
                    {
                        Advance();
                        if (restParameterIndex >= 0)
                        {
                            valid = false;
                            break;
                        }

                        restParameterIndex = parameters.Count;
                    }
                    if (!(IsIdentifierLike(Current()) || IsPunctuator("[") || IsPunctuator("{")))
                    {
                        valid = false;
                        break;
                    }

                    try
                    {
                        var binding = ParseParameterBinding();
                        parameters.Add(binding.Identifier);
                        parameterBindings.Add(binding.Pattern);
                        parameterDefaults.Add(null);
                        if (IsPunctuator("=") && !PeekIsPunctuator(1, ">"))
                        {
                            // ECMA-262 14.1: rest parameters may not have a default.
                            if (restParameterIndex == parameters.Count - 1)
                            {
                                throw new JsParserException("Rest parameter may not have a default value.");
                            }
                            Advance();
                            var arrowDefault = ParseExpression(2);
                            parameterDefaults[parameterDefaults.Count - 1] = arrowDefault;
                        }
                    }
                    catch (JsParserException)
                    {
                        valid = false;
                        break;
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

            var cp2 = Advance(); // )
            // ECMA-262 14.2: [no LineTerminator here] between parameters and =>
            if (!(IsPunctuator("=") && PeekIsPunctuator(1, ">")) || HasLineTerminatorBetween(cp2, Current()))
            {
                _index = saved;
                return false;
            }

            Advance(); // =
            Advance(); // >
            expression = ParseArrowFunctionBody(parameters, parameterBindings, _tokens[saved].Span, isAsync: false, restParameterIndex: restParameterIndex, parameterDefaults: parameterDefaults);
            return true;
        }

        return false;
    }

    private ArrowFunctionExpressionNode ParseArrowFunctionBody(
        IReadOnlyList<string> parameters,
        IReadOnlyList<BindingPatternNode?> parameterBindings,
        SourceSpan start,
        bool isAsync,
        int restParameterIndex,
        IReadOnlyList<ExpressionNode?>? parameterDefaults = null)
    {
        var hasSimpleParameterList = restParameterIndex < 0 &&
            parameterBindings.All(binding => binding is null) &&
            (parameterDefaults is null || parameterDefaults.All(def => def is null));

        // ECMA-262 14.2.1: arrow functions use UniqueFormalParameters which
        // ALWAYS forbid duplicate parameter names (even sloppy, simple lists).
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in parameters)
            {
                if (!IsSyntheticPatternBinding(p) && !seen.Add(p))
                    throw new JsParserException($"Duplicate parameter name '{p}' in arrow function.");
            }
        }

        // ECMA-262 14.2.1: `eval` and `arguments` may not appear as
        // parameter names in an arrow function with a strict body or
        // in strict-mode code. Async arrows are always strict.
        if (_strictMode || isAsync)
        {
            foreach (var p in parameters)
            {
                if (IsSyntheticPatternBinding(p)) continue;
                if (string.Equals(p, "eval", StringComparison.Ordinal) ||
                    string.Equals(p, "arguments", StringComparison.Ordinal))
                {
                    throw new JsParserException(
                        $"'{p}' may not be used as a parameter name in strict mode.");
                }
                if (isAsync && string.Equals(p, "await", StringComparison.Ordinal))
                {
                    throw new JsParserException(
                        "'await' may not be used as a parameter name in an async function.");
                }
            }
        }

        // ECMA-262 ArrowFunction: the body is parsed with its OWN [Yield]/[Await]
        // context, not the enclosing one. ConciseBody is [~Yield], and Await is +Await
        // only for an async arrow (AsyncConciseBody), ~Await otherwise. Without this,
        // an async arrow nested inside a non-async function inherited the enclosing
        // [~Await] and parsed `await x` as the identifier `await` followed by `x`
        // (e.g. `function m(){ let c=async()=>{ await u(); }; }` — pervasive in minified
        // bundles like x.com's main.js). Top-level async arrows only worked by accident
        // because the program default is [+Await].
        // Arrow functions clear the ClassStaticBlock depth so that `await`
        // as a binding identifier is only rejected at the static block's top
        // level, not inside nested arrow functions (ECMA-262 15.7).
        var previousClassStaticBlockDepth = _classStaticBlockDepth;
        _classStaticBlockDepth = 0;
        try
        {
        if (IsPunctuator("{"))
        {
            var block = ParseWithExpressionContext(
                allowYieldExpression: false,
                allowAwaitExpression: isAsync,
                () => ParseFunctionBlockBody(ParseBlockStatement));
            ValidateDirectivePrologueStrictStringEscapes(block.Statements);
            // ECMA-262 14.2.1: SyntaxError if the body contains "use strict"
            // and the parameter list is non-simple (destructuring, rest, defaults).
            if (!hasSimpleParameterList && ContainsUseStrictDirective(block.Statements))
            {
                throw new JsParserException("A strict directive is not allowed with a non-simple parameter list.");
            }
            // Async arrows forbid `await` as an identifier in the body.
            if (isAsync)
                ValidateRestrictedIdentifiersInStatements(block.Statements, forbidAwaitIdentifier: true, forbidYieldIdentifier: false);
            // ECMA-262 14.2.1: parameter names must not conflict with
            // lexical declarations in the function body.
            var bodyLexicalNames = CollectTopLevelLexicallyDeclaredNames(block.Statements);
            foreach (var p in parameters)
            {
                if (!IsSyntheticPatternBinding(p) && bodyLexicalNames.Contains(p))
                    throw new JsParserException($"Parameter '{p}' conflicts with a lexical declaration in the arrow body.");
            }
            return new ArrowFunctionExpressionNode(parameters, block, null, MergeSpan(start, block.Span), IsAsync: isAsync, HasSimpleParameterList: hasSimpleParameterList, RestParameterIndex: restParameterIndex, ParameterBindings: parameterBindings, ParameterDefaults: parameterDefaults);
        }

        var bodyExpression = ParseWithExpressionContext(
            allowYieldExpression: false,
            allowAwaitExpression: isAsync,
            () => ParseExpression(2));
        return new ArrowFunctionExpressionNode(parameters, null, bodyExpression, MergeSpan(start, bodyExpression.Span), IsAsync: isAsync, HasSimpleParameterList: hasSimpleParameterList, RestParameterIndex: restParameterIndex, ParameterBindings: parameterBindings, ParameterDefaults: parameterDefaults);
        }
        finally
        {
            _classStaticBlockDepth = previousClassStaticBlockDepth;
        }
    }

    private IReadOnlyList<ExpressionNode> ParseCallArguments()
    {
        var args = new List<ExpressionNode>();
        ExpectPunctuator("(");
        // Arguments are AssignmentExpression[+In]: restore [+In] for the list.
        var savedNoIn = _noIn;
        _noIn = false;
        try
        {
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
        }
        finally
        {
            _noIn = savedNoIn;
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

            if (IsPunctuator("?."))
            {
                Advance();

                if (allowCall && IsPunctuator("("))
                {
                    var args = ParseCallArguments();
                    var end = Previous();
                    left = new OptionalCallExpressionNode(left, args, MergeSpan(left.Span, end.Span));
                    continue;
                }

                if (IsPunctuator("["))
                {
                    Advance();
                    var propExpr = ParseExpression(0);
                    ExpectPunctuator("]");
                    var close = Previous();
                    left = new OptionalMemberExpressionNode(left, string.Empty, Computed: true, PropertyExpression: propExpr, MergeSpan(left.Span, close.Span));
                    continue;
                }

                var property = ExpectPropertyNameAfterDot();
                left = new OptionalMemberExpressionNode(left, property.Text, Computed: false, PropertyExpression: null, MergeSpan(left.Span, property.Span));
                continue;
            }

            if (IsPunctuator("["))
            {
                Advance();
                // MemberExpression [ Expression[+In] ]
                var propExpr = ParseExpressionAllowIn(0);
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
            _inDirectivePrologue = false,
            _allowYieldExpression = _allowYieldExpression,
            _allowAwaitExpression = _allowAwaitExpression
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
            if (ch == '/')
            {
                var commentEnd = SkipRawComment(raw, i);
                if (commentEnd > i)
                {
                    i = commentEnd - 1;
                    continue;
                }

                if (CanStartRawRegexLiteral(raw, i))
                {
                    var regexEnd = SkipRawRegexLiteral(raw, i);
                    if (regexEnd < 0)
                    {
                        return -1;
                    }

                    if (regexEnd > i)
                    {
                        i = regexEnd - 1;
                        continue;
                    }
                }
            }

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

    private static bool CanStartRawRegexLiteral(string raw, int slashIndex)
    {
        for (var i = slashIndex - 1; i >= 0; i--)
        {
            var c = raw[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return c is '(' or '{' or '[' or ',' or ';' or ':' or '?' or '=' or '!' or '&' or '|' or '^' or '~' or '+' or '-' or '*' or '%' or '<' or '>' or '/';
        }

        return true;
    }

    private static int SkipRawComment(string raw, int start)
    {
        if (start + 1 >= raw.Length)
        {
            return start;
        }

        var next = raw[start + 1];
        if (next == '/')
        {
            var i = start + 2;
            while (i < raw.Length && !IsRawLineTerminator(raw[i]))
            {
                i++;
            }

            return i;
        }

        if (next != '*')
        {
            return start;
        }

        for (var i = start + 2; i + 1 < raw.Length; i++)
        {
            if (raw[i] == '*' && raw[i + 1] == '/')
            {
                return i + 2;
            }
        }

        return -1;
    }

    private static int SkipRawRegexLiteral(string raw, int start)
    {
        var escaped = false;
        var inCharClass = false;
        for (var i = start + 1; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (!escaped)
            {
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '[')
                {
                    inCharClass = true;
                    continue;
                }

                if (ch == ']' && inCharClass)
                {
                    inCharClass = false;
                    continue;
                }

                if (ch == '/' && !inCharClass)
                {
                    i++;
                    while (i < raw.Length && char.IsLetter(raw[i]))
                    {
                        i++;
                    }

                    return i;
                }

                if (IsRawLineTerminator(ch))
                {
                    return -1;
                }
            }
            else
            {
                escaped = false;
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

    private static bool IsRawLineTerminator(char ch) => ch is '\r' or '\n' or '\u2028' or '\u2029';

    private bool TryGetInfixBindingPower(Token token, out string op, out int leftBindingPower, out int rightBindingPower)
    {
        op = token.Text;
        leftBindingPower = 0;
        rightBindingPower = 0;

        if (token.Kind == TokenKind.Keyword && (token.Text == "in" || token.Text == "instanceof"))
        {
            // [~In]: suppress `in` (but not `instanceof`) so the enclosing for-head
            // parse stops here and can treat `in` as the for-in marker.
            if (_noIn && token.Text == "in")
            {
                return false;
            }

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
            case "|":
                leftBindingPower = 8;
                rightBindingPower = 9;
                return true;
            case "^":
                leftBindingPower = 9;
                rightBindingPower = 10;
                return true;
            case "&":
                leftBindingPower = 10;
                rightBindingPower = 11;
                return true;
            case "==":
            case "!=":
            case "===":
            case "!==":
                leftBindingPower = 11;
                rightBindingPower = 12;
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

    private bool PeekIdentifierLike(int offset, string text)
    {
        var idx = Math.Min(_index + offset, _tokens.Count - 1);
        var token = _tokens[idx];
        return IsIdentifierLike(token) && token.Text == text;
    }

    private Token ExpectIdentifier()
    {
        if (!IsIdentifierLike(Current()))
        {
            throw new JsParserException($"Expected identifier, found '{Current().Text}'{Where()}.");
        }

        return Advance();
    }

    private Token ExpectPropertyNameAfterDot()
    {
        if (Current().Kind != TokenKind.Identifier &&
            Current().Kind != TokenKind.Keyword &&
            Current().Kind != TokenKind.PrivateIdentifier)
        {
            throw new JsParserException($"Expected identifier, found '{Current().Text}'{Where()}.");
        }

        return Advance();
    }

    // ECMA-262 13.3.1: `let` at the start of a statement begins a LexicalDeclaration
    // only when the next token is `[`, `{`, or a BindingIdentifier. Otherwise `let`
    // is an ordinary identifier (sloppy mode) and the statement is an
    // ExpressionStatement. (`let [` is always a declaration — ExpressionStatement
    // explicitly forbids a leading `let [`.)
    private bool StartsLetLexicalDeclaration()
    {
        var next = _tokens[Math.Min(_index + 1, _tokens.Count - 1)];
        if (next.Kind == TokenKind.Punctuator && (next.Text == "[" || next.Text == "{"))
        {
            return true;
        }
        return IsIdentifierLike(next);
    }

    // ECMA-262 12.2.6.1: FutureReservedWords disallowed as shorthand identifiers in strict mode.
    private static bool IsStrictModeFutureReservedWord(string name) => name switch
    {
        "implements" or "interface" or "let" or "package" or "private"
            or "protected" or "public" or "static" or "yield" => true,
        _ => false
    };

    private bool IsIdentifierLike(Token token)
    {
        if (token.Kind == TokenKind.Keyword)
        {
            if (token.Text == "of")
            {
                // Contextual keyword in for-of; valid IdentifierName elsewhere.
                return true;
            }

            if (token.Text == "async")
            {
                return true;
            }

            if (token.Text == "await")
            {
                return !_moduleMode;
            }

            if (token.Text == "yield")
            {
                return !_strictMode && !_moduleMode;
            }

            if (token.Text == "let")
            {
                // `let` is reserved only in strict-mode code; in sloppy code it is
                // a valid Identifier (e.g. `var let = 1`, `let: stmt`, `let + 1`).
                return !_strictMode;
            }

            return false;
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

    private static bool IsUnescapedIdentifierLike(Token token, string text) =>
        (token.Kind == TokenKind.Identifier || token.Kind == TokenKind.Keyword) &&
        !token.ContainsEscape &&
        string.Equals(token.Text, text, StringComparison.Ordinal);

    private void ExpectPunctuator(string text)
    {
        if (!IsPunctuator(text))
        {
            throw new JsParserException($"Expected '{text}', found '{Current().Text}'{Where()}.");
        }

        Advance();
    }

    // Source position of the current token, for diagnostics in parse errors.
    private string Where()
    {
        var span = Current().Span;
        return $" at {span.Line}:{span.Column} (offset {span.Start})";
    }

    private static SourceSpan MergeSpan(SourceSpan start, SourceSpan end)
    {
        var length = Math.Max(0, (end.Start + end.Length) - start.Start);
        return new SourceSpan(start.Start, length, start.Line, start.Column);
    }

    private static bool IsAssignmentOperator(Token token)
    {
        return token.Kind == TokenKind.Punctuator &&
               token.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "<<=" or ">>=" or ">>>=" or "&=" or "^=" or "|=" or "**=" or "&&=" or "||=" or "??=";
    }

    // Build the assignment expression node for `target op right`. Logical
    // assignment operators produce a LogicalAssignmentExpressionNode (which the
    // compiler lowers with short-circuit semantics); all others desugar to
    // `target = (target op' right)`.
    private static ExpressionNode BuildAssignmentNode(ExpressionNode target, string op, ExpressionNode right, SourceSpan span)
    {
        if (op is "&&=" or "||=" or "??=")
        {
            // ECMA-262 13.15.1: AssignmentTargetType of the LHS must be simple
            // (Identifier or MemberExpression) — `f() &&= 1` is an early SyntaxError.
            if (!IsSimpleAssignmentTarget(target))
            {
                throw new JsParserException("Invalid left-hand side in logical assignment (target is not simple).");
            }

            return new LogicalAssignmentExpressionNode(target, op[..^1], right, span);
        }

        var rhs = BuildAssignmentRight(target, op, right);
        return new AssignmentExpressionNode(target, rhs, span);
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
            "%=" => new BinaryExpressionNode("%", left, right, MergeSpan(left.Span, right.Span)),
            "<<=" => new BinaryExpressionNode("<<", left, right, MergeSpan(left.Span, right.Span)),
            ">>=" => new BinaryExpressionNode(">>", left, right, MergeSpan(left.Span, right.Span)),
            ">>>=" => new BinaryExpressionNode(">>>", left, right, MergeSpan(left.Span, right.Span)),
            "&=" => new BinaryExpressionNode("&", left, right, MergeSpan(left.Span, right.Span)),
            "^=" => new BinaryExpressionNode("^", left, right, MergeSpan(left.Span, right.Span)),
            "|=" => new BinaryExpressionNode("|", left, right, MergeSpan(left.Span, right.Span)),
            "**=" => new BinaryExpressionNode("**", left, right, MergeSpan(left.Span, right.Span)),
            "&&=" => new BinaryExpressionNode("&&", left, right, MergeSpan(left.Span, right.Span)),
            "||=" => new BinaryExpressionNode("||", left, right, MergeSpan(left.Span, right.Span)),
            "??=" => new BinaryExpressionNode("??", left, right, MergeSpan(left.Span, right.Span)),
            _ => throw new JsParserException($"Unsupported assignment operator '{op}'.")
        };
    }

    // ECMA-262 13.4 UpdateExpression: prefix/postfix ++/-- can only be
    // applied to identifiers and member expressions, not call expressions.
    private static bool IsUpdateTarget(ExpressionNode node) =>
        node is IdentifierExpressionNode or MemberExpressionNode;

    // ECMA-262 13.15.1 — Simple AssignmentTargetType. Returns true for
    // anything that can sit on the LHS of `=` (or compound assignments).
    // Destructuring patterns are wrapped in ArrayExpressionNode /
    // ObjectExpressionNode until the assignment-time conversion runs,
    // so we accept those too — the conversion step rejects shapes that
    // aren't valid patterns.
    // ECMA-262 13.15.2 "simple" AssignmentTargetType — Identifier or
    // MemberExpression (through parentheses). Required for compound and logical
    // assignment LHS; CallExpression and destructuring patterns are not simple.
    private static bool IsSimpleAssignmentTarget(ExpressionNode node) => node switch
    {
        IdentifierExpressionNode => true,
        MemberExpressionNode => true,
        ParenthesizedExpressionNode pe => IsSimpleAssignmentTarget(pe.Expression),
        _ => false,
    };

    // ECMA-262: recursive validation of binding patterns for duplicate names.
    // `for (const [x, x] in {})` must throw SyntaxError.
    internal static void ValidateBindingPatternAwait(BindingPatternNode pattern)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode id:
                if (!IsSyntheticPatternBinding(id.Name) && string.Equals(id.Name, "await", StringComparison.Ordinal))
                    throw new JsParserException("'await' may not be used as a binding name inside a class static block.");
                break;
            case ArrayBindingPatternNode arr:
                foreach (var el in arr.Elements)
                {
                    if (el.Target is { } t) ValidateBindingPatternAwait(t);
                }
                break;
            case ObjectBindingPatternNode obj:
                foreach (var prop in obj.Properties)
                    ValidateBindingPatternAwait(prop.Target);
                if (obj.Rest is { } r) ValidateBindingPatternAwait(r);
                break;
        }
    }

    internal static void ValidateBindingPatternDuplicates(BindingPatternNode pattern, HashSet<string> seen)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode id:
                if (!IsSyntheticPatternBinding(id.Name) && !seen.Add(id.Name))
                    throw new JsParserException($"Duplicate binding '{id.Name}' in pattern.");
                break;
            case ArrayBindingPatternNode arr:
                foreach (var el in arr.Elements)
                {
                    if (el.Target is { } t) ValidateBindingPatternDuplicates(t, seen);
                }
                break;
            case ObjectBindingPatternNode obj:
                foreach (var prop in obj.Properties)
                {
                    ValidateBindingPatternDuplicates(prop.Target, seen);
                }
                if (obj.Rest is { } r) ValidateBindingPatternDuplicates(r, seen);
                break;
        }
    }

    // ECMA-262 13.15.5 — when an ArrayLiteral/ObjectLiteral sits in a
    // destructuring assignment position (LHS of `=`, or a for-in/of head), it is
    // reparsed under the AssignmentPattern goal symbol. Shapes that are not valid
    // AssignmentPatterns are early SyntaxErrors (negative `phase: parse` tests).
    // The shallow IsValidAssignmentTarget gate accepts every array/object literal;
    // this performs the deep structural validation.
    // ECMA-262 14.7.5: the ForDeclaration/ForBinding of a for-of head (let/const
    // or var) may not carry an initializer — unlike for-in, which permits the
    // Annex B sloppy `var x = init` form. Any initializer here is a SyntaxError.
    private void ValidateForOfDeclarationNoInitializer(StatementNode? initializer)
    {
        if (initializer is VariableDeclarationStatementNode declaration)
        {
            foreach (var declarator in declaration.Declarators)
            {
                if (declarator.Initializer is not null)
                {
                    throw new JsParserException(
                        $"A for-of loop variable declaration may not have an initializer{Where()}.");
                }
            }
        }
    }

    // A for-in/for-of head whose LHS is a bare array/object literal is an
    // assignment-target pattern (lhsKind = assignment) and must be a valid
    // AssignmentPattern. `let`/`const`/`var` declaration heads use binding-pattern
    // parsing and are validated elsewhere.
    private void ValidateForHeadDestructuringTarget(StatementNode? initializer)
    {
        if (initializer is ExpressionStatementNode { Expression: var expr } &&
            expr is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode)
        {
            ValidateAssignmentPattern(expr);
        }
    }

    private void ValidateAssignmentPattern(ExpressionNode node)
    {
        switch (node)
        {
            case ArrayLiteralExpressionNode array:
                ValidateArrayAssignmentPattern(array);
                break;
            case ObjectLiteralExpressionNode obj:
                ValidateObjectAssignmentPattern(obj);
                break;
            default:
                ValidateDestructuringAssignmentTarget(node);
                break;
        }
    }

    private void ValidateArrayAssignmentPattern(ArrayLiteralExpressionNode array)
    {
        var count = array.Elements.Count;
        for (var i = 0; i < count; i++)
        {
            var element = array.Elements[i];
            switch (element)
            {
                case ElisionExpressionNode:
                    break;
                case SpreadElementExpressionNode rest:
                    // AssignmentRestElement must be the last element and cannot carry
                    // a default initializer.
                    if (i != count - 1)
                    {
                        throw new JsParserException(
                            $"Rest element must be last in a destructuring pattern{Where()}.");
                    }

                    if (rest.Argument is AssignmentExpressionNode)
                    {
                        throw new JsParserException(
                            $"Rest element cannot have an initializer{Where()}.");
                    }

                    ValidateDestructuringAssignmentTarget(rest.Argument);
                    break;
                case AssignmentExpressionNode assign:
                    // AssignmentElement : DestructuringAssignmentTarget Initializer
                    ValidateDestructuringAssignmentTarget(assign.Left);
                    break;
                default:
                    ValidateDestructuringAssignmentTarget(element);
                    break;
            }
        }
    }

    private void ValidateObjectAssignmentPattern(ObjectLiteralExpressionNode obj)
    {
        // Reinterpreted as a pattern: duplicate `__proto__:` is permitted here, so
        // withdraw any deferred B.3.1 literal error for this node.
        if (obj.HasDuplicateProtoSetter)
        {
            _pendingDuplicateProtoLiterals.Remove(obj);
        }

        // Likewise, CoverInitializedName is valid in a destructuring pattern —
        // withdraw the deferred literal error.
        _pendingCoverInitializedNameLiterals.Remove(obj);

        var count = obj.Properties.Count;
        for (var i = 0; i < count; i++)
        {
            var property = obj.Properties[i];
            if (property.Value is SpreadElementExpressionNode rest)
            {
                // AssignmentRestProperty must be last and its target must be a
                // simple DestructuringAssignmentTarget (never a nested pattern).
                if (i != count - 1)
                {
                    throw new JsParserException(
                        $"Rest element must be last in a destructuring pattern{Where()}.");
                }

                if (rest.Argument is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode)
                {
                    throw new JsParserException(
                        $"Invalid rest binding target in object destructuring pattern{Where()}.");
                }

                if (rest.Argument is AssignmentExpressionNode)
                {
                    throw new JsParserException(
                        $"Rest element cannot have an initializer{Where()}.");
                }

                ValidateDestructuringAssignmentTarget(rest.Argument);
                continue;
            }

            // `{ key = default }` cover-initialized shorthand: the target is the
            // (already-validated) identifier key; nothing further to check.
            if (property.IsCoverInitializedName)
            {
                continue;
            }

            var target = property.Value;
            if (target is AssignmentExpressionNode withDefault)
            {
                target = withDefault.Left;
            }

            ValidateDestructuringAssignmentTarget(target);
        }
    }

    // ECMA-262 DestructuringAssignmentTarget : LeftHandSideExpression. A nested
    // array/object literal is itself an assignment pattern; any other target must
    // have a "simple" AssignmentTargetType (Identifier or MemberExpression).
    private void ValidateDestructuringAssignmentTarget(ExpressionNode target)
    {
        switch (target)
        {
            case ArrayLiteralExpressionNode array:
                ValidateArrayAssignmentPattern(array);
                break;
            case ObjectLiteralExpressionNode obj:
                ValidateObjectAssignmentPattern(obj);
                break;
            case IdentifierExpressionNode:
                // 13.15.1: in strict mode `eval`/`arguments` are not valid
                // simple assignment targets, including inside a pattern.
                ValidateStrictAssignmentTarget(target);
                break;
            case MemberExpressionNode:
                break;
            case ParenthesizedExpressionNode paren:
                ValidateDestructuringAssignmentTarget(paren.Expression);
                break;
            default:
                throw new JsParserException(
                    $"Invalid destructuring assignment target ({target.GetType().Name}){Where()}.");
        }
    }

    // ECMA-262 13.15.1 Static Semantics: AssignmentTargetType.
    // Returns true when the expression can appear on the LHS of `=`,
    // `++`/`--`, or a compound assignment. CallExpression, optional
    // chaining, arrow functions, and literal values are NOT valid
    // assignment targets — the spec says these are early SyntaxErrors.
    private static bool IsValidAssignmentTarget(ExpressionNode node)
    {
        return node switch
        {
            IdentifierExpressionNode => true,
            MemberExpressionNode m => m.Object is not SuperExpressionNode,
            // Destructuring patterns parsed as array/object literals
            // (assignment pattern, not expression). Must have at least one
            // property/element or a rest element to be a valid target.
            ObjectLiteralExpressionNode objLit => objLit.Properties.Count > 0,
            ArrayLiteralExpressionNode arrLit =>
                arrLit.Elements.Count > 0 &&
                arrLit.Elements.Any(e => e is SpreadElementExpressionNode || e is not ElisionExpressionNode),
            // Tolerate parenthesised wrappers around valid targets.
            ParenthesizedExpressionNode pe => IsValidAssignmentTarget(pe.Expression),
            _ => false,
        };
    }

    private static ExpressionNode BuildUpdateAssignment(ExpressionNode target, string updateOp, SourceSpan opSpan, bool isPostfix)
    {
        var span = isPostfix ? MergeSpan(target.Span, opSpan) : MergeSpan(opSpan, target.Span);

        // ECMA-262 13.4 Update Expressions. For simple Identifier / non-super
        // member targets, carry the real update semantics (ToNumeric of the old
        // value, ±1, store, then yield the old value for postfix or the new
        // value for prefix) through a sentinel unary operator that the compiler
        // lowers. The legacy `target = target + 1` desugar evaluated to the new
        // value for both forms and string-concatenated non-numeric operands, so
        // it is wrong for postfix and for non-number operands; keep it only for
        // the exotic targets (CallExpression, super member) that the dedicated
        // lowering does not model.
        var simpleTarget = target is IdentifierExpressionNode ||
            (target is MemberExpressionNode m && m.Object is not SuperExpressionNode);
        if (simpleTarget)
        {
            var op = (updateOp, isPostfix) switch
            {
                ("++", true) => "postIncrement",
                ("++", false) => "preIncrement",
                ("--", true) => "postDecrement",
                _ => "preDecrement",
            };
            return new UnaryExpressionNode(op, target, span);
        }

        var numeric = new NumericLiteralExpressionNode(1, "1", opSpan);
        var binaryOp = updateOp == "++" ? "+" : "-";
        var right = new BinaryExpressionNode(binaryOp, target, numeric, MergeSpan(target.Span, numeric.Span));
        return new AssignmentExpressionNode(target, right, span);
    }

    private bool IsAccessorPropertyStart()
    {
        var current = Current();
        if (!(IsUnescapedIdentifierLike(current, "get") || IsUnescapedIdentifierLike(current, "set")))
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
        if (!IsUnescapedIdentifierLike(current, "async"))
        {
            return false;
        }

        if (HasLineTerminatorBetweenCurrentAnd(1))
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
        if (!IsUnescapedIdentifierLike(current, "async"))
        {
            return false;
        }

        if (HasLineTerminatorBetweenCurrentAnd(1))
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
