using FenBrowser.Js.Ast;
using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeCompiler
{
    private sealed class LoopContext
    {
        public int ContinueTarget { get; set; }
        public required List<int> BreakJumpIndices { get; init; }
        public required List<int> ContinueJumpIndices { get; init; }
        public string? Label { get; set; }
        public bool IsSwitch { get; set; }
        // Open-scope depth captured at loop entry; break/continue
        // emits this-many LeaveScope ops before jumping so nested
        // let/const block scopes are torn down per spec 13.7/13.8.
        public int ScopeDepthAtEntry { get; init; }
        // Monotonic nesting sequence (see _nestingSeq). break/continue targeting
        // this loop must run every `finally` whose Seq is greater than this.
        public int Seq { get; init; }
    }

    private sealed class LabelTarget
    {
        public required string Name { get; init; }
        public required List<int> BreakJumpIndices { get; init; }
        public int ScopeDepthAtEntry { get; init; }
        public int Seq { get; init; }
    }

    // A `finally` block that is currently "pending" — i.e. an abrupt completion
    // (break/continue/return) escaping it must run it first (ECMA-262 14.15.3).
    // The compiler-driven model emits an inline copy of the block at each such
    // exit; the shared trailing copy after the try handles normal/throw completion.
    private sealed class FinallyFrame
    {
        public required StatementNode Block { get; init; }
        public required int Seq { get; init; }
        public required int ScopeDepthAtEntry { get; init; }
    }

    private static int s_privateClassCounter;
    private static long s_brandCounter;

    // H.5: true while compiling the constructor body of a class with private fields.
    // Private field writes in this context emit DefinePrivateField instead of SetPrivateField.
    private bool _compilingClassConstructor;
    private bool _isDerivedConstructor;
    private bool _isClassConstructor;

    // Per-function brand tokens for private field access validation.
    private List<long> _brandTokens = new();

    private readonly List<Instruction> _instructions = new();
    private readonly List<JsValue> _constants = new();
    private readonly Dictionary<string, int> _variables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _varDeclarationNames = new(StringComparer.Ordinal);
    // Annex B.3.3: names of block-level FunctionDeclarations (sloppy mode only)
    // eligible for the legacy var-scoped binding. Populated once per function/
    // script/eval compile; a block-level function whose name is here gets an
    // extra StoreVarTop after its block InitVar so the value reaches the
    // VariableEnvironment binding.
    private readonly HashSet<string> _annexBFunctionNames = new(StringComparer.Ordinal);
    // Running count of EnterScope ops emitted minus LeaveScope ops emitted.
    // Loop contexts snapshot this at entry so break/continue can emit
    // matching LeaveScope ops before jumping (ECMA-262 14.7 abrupt
    // completion handling â€” scopes opened inside the body must close).
    private int _openScopeDepth;
    private readonly HashSet<string> _lexicalDeclarationNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _constDeclarationNames = new(StringComparer.Ordinal);
    private readonly List<string> _propertyNames = new();
    private readonly Dictionary<string, int> _propertyNameToIndex = new(StringComparer.Ordinal);
    private readonly List<BytecodeFunction> _nestedFunctions = new();
    private readonly List<string> _parameterNames = new();
    private readonly Stack<LoopContext> _loopStack = new();
    private readonly Stack<LabelTarget> _labelStack = new();
    // Pending `finally` blocks (innermost on top) and a monotonic counter shared by
    // loops, labels, and finally frames so an abrupt completion can emit exactly the
    // finally blocks nested between it and its target, innermost-first.
    private readonly Stack<FinallyFrame> _finallyStack = new();
    private int _nestingSeq;
    // Tracks block-scoped let/const names so they are not added to
    // _lexicalDeclarationNames/_constDeclarationNames. EnterScope creates
    // their bindings instead.
    private readonly Stack<HashSet<string>> _blockScopedNameStack = new();
    private string? _name;
    // The full original source text, when available. Used to slice exact function
    // source for Function.prototype.toString. Propagated to child compilers so
    // nested-function spans (which are absolute offsets into this same text)
    // resolve at any depth. Not reset by CompileProgramCore.
    internal string? _rawSource;
    private int _nextRegister = 1;
    private FunctionKind _currentFunctionKind = FunctionKind.Ordinary;
    private bool _isStrictMode;
    private bool _captureCompletionValue;
    // When non-null, the next LoopContext pushed should take this label.
    private string? _pendingLabel;
    // ECMA-262 NamedEvaluation: when the next compiled expression is an anonymous
    // function/arrow/class definition, it adopts this name. Set immediately before
    // compiling the initializer and consumed by the function/arrow/class case.
    private string? _pendingNameHint;

    public BytecodeFunction CompileScript(SourceText source)
    {
        var program = JsParser.ParseScript(source!);
        _rawSource = source?.Text;
        // ECMA-262 Static Semantics: Early Error validation.
        new AstValidator().Validate(program, new Diagnostics.DiagnosticBag());

        if (source is not null && BytecodeCache.TryGet(source.Text, strictMode: false, out var cached))
        {
            return cached;
        }

        var compiled = CompileProgram(program);
        if (source is not null)
        {
            BytecodeCache.Put(source.Text, strictMode: false, compiled);
        }
        return compiled;
    }

    public BytecodeFunction CompileProgram(ProgramNode program)
    {
        return CompileProgram(program, inheritedStrictMode: false);
    }

    public BytecodeFunction CompileProgram(ProgramNode program, bool inheritedStrictMode)
    {
        return CompileProgramCore(
            program,
            parameters: Array.Empty<string>(),
            restParameterIndex: -1,
            name: null,
            hasOwnArgumentsObject: false,
            hasSimpleParameterList: true,
            functionKind: FunctionKind.Ordinary,
            inheritedStrictMode: inheritedStrictMode,
            captureCompletionValue: true);
    }

    public BytecodeFunction CompileFunctionBody(SourceText body, IReadOnlyList<string> parameters, string? name)
        => CompileFunctionBody(body, parameters, name, FunctionKind.Ordinary);

    public BytecodeFunction CompileFunctionBody(
        SourceText body,
        IReadOnlyList<string> parameters,
        string? name,
        FunctionKind functionKind)
    {
        var program = JsParser.ParseFunctionBody(body);
        return CompileProgramCore(
            program,
            parameters,
            restParameterIndex: -1,
            name,
            hasOwnArgumentsObject: true,
            hasSimpleParameterList: true,
            functionKind: functionKind,
            inheritedStrictMode: false,
            captureCompletionValue: false);
    }

    private BytecodeFunction CompileProgramCore(
        ProgramNode program,
        IReadOnlyList<string> parameters,
        int restParameterIndex,
        string? name,
        bool hasOwnArgumentsObject,
        bool hasSimpleParameterList,
        FunctionKind functionKind,
        bool inheritedStrictMode,
        bool captureCompletionValue,
        int prologueStatementCount = 0,
        IReadOnlyList<ExpressionNode?>? parameterDefaults = null,
        bool bindOwnNameInBody = false)
    {
        _instructions.Clear();
        _constants.Clear();
        _variables.Clear();
        _varDeclarationNames.Clear();
        _annexBFunctionNames.Clear();
        _lexicalDeclarationNames.Clear();
        _constDeclarationNames.Clear();
        _propertyNames.Clear();
        _propertyNameToIndex.Clear();
        _nestedFunctions.Clear();
        _parameterNames.Clear();
        _loopStack.Clear();
        _labelStack.Clear();
        _finallyStack.Clear();
        _nestingSeq = 0;
        _name = name;
        _nextRegister = 1;
        _currentFunctionKind = functionKind;
        _isStrictMode = inheritedStrictMode || program.Kind == ProgramKind.Module || HasUseStrictDirective(program.Body);
        _captureCompletionValue = captureCompletionValue;
        foreach (var p in parameters)
        {
            _parameterNames.Add(p);
            _ = GetOrCreateVariableSlot(p);
        }

        // Annex B.3.3 (sloppy mode only): block-level function declarations also
        // get a var-scoped binding in this VariableEnvironment. Collect the
        // eligible names first so the hoisted var bindings exist before any code
        // runs, then HoistFunctionDeclarations / block compilation emit the
        // StoreVarTop assignments at the right points.
        if (!_isStrictMode)
        {
            CollectAnnexBFunctionNames(program.Body, _annexBFunctionNames);
            foreach (var annexBName in _annexBFunctionNames)
            {
                _varDeclarationNames.Add(annexBName);
                _ = GetOrCreateVariableSlot(annexBName);
            }
        }

        HoistFunctionDeclarations(program.Body);

        int prologueEndIp = 0;
        var stmtIndex = 0;
        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
            stmtIndex++;
            if (stmtIndex == prologueStatementCount && prologueStatementCount > 0)
            {
                prologueEndIp = _instructions.Count;
                _instructions.Add(new Instruction(OpCode.PrologueEnd, 0, 0, 0));
            }
        }

        if (_captureCompletionValue)
        {
            _instructions.Add(new Instruction(OpCode.Return, 0, 0, 0));
        }
        else
        {
            var undefReg = AllocateRegister();
            var undefConst = AddConstant(JsValue.Undefined);
            _instructions.Add(new Instruction(OpCode.LoadConst, undefReg, undefConst, 0));
            _instructions.Add(new Instruction(OpCode.Return, undefReg, 0, 0));
        }

        return new BytecodeFunction
        {
            Name = _name,
            Kind = _currentFunctionKind,
            IsStrictMode = _isStrictMode,
            IsDerivedConstructor = _isDerivedConstructor,
            IsClassConstructor = _isClassConstructor,
            Instructions = _instructions.ToArray(),
            Constants = _constants.ToArray(),
            VariableSlots = new Dictionary<string, int>(_variables),
            VarDeclarationNames = _varDeclarationNames.ToArray(),
            LexicalDeclarationNames = _lexicalDeclarationNames.ToArray(),
            ConstDeclarationNames = _constDeclarationNames.ToArray(),
            PropertyNames = _propertyNames.ToArray(),
            ParameterNames = _parameterNames.ToArray(),
            RestParameterIndex = restParameterIndex,
            ExpectedArgumentCount = ComputeExpectedArgumentCount(parameters.Count, restParameterIndex, parameterDefaults),
            BindsOwnNameInBody = bindOwnNameInBody && name is { Length: > 0 },
            HasOwnArgumentsObject = hasOwnArgumentsObject,
            UsesRestrictedArgumentsObject = hasOwnArgumentsObject && (_isStrictMode || !hasSimpleParameterList),
            NestedFunctions = _nestedFunctions.ToArray(),
            RegisterCount = Math.Max(2, _nextRegister),
            BrandTokens = _brandTokens.ToArray(),
            PrologueEndIp = prologueEndIp,
        };
    }

    // Slice the exact source text for a function node from the original source,
    // and propagate the source to the child compiler that produced `nested` so
    // deeper-nested functions resolve too. `span` is an absolute offset into
    // `_rawSource`. Returns the function unchanged for fluent use.
    private BytecodeFunction AttachSource(BytecodeFunction nested, BytecodeCompiler child, SourceSpan span)
    {
        child._rawSource = _rawSource;
        if (_rawSource is not null && span.Start >= 0 && span.Length > 0 &&
            span.Start + span.Length <= _rawSource.Length)
        {
            nested.SourceText = _rawSource.Substring(span.Start, span.Length);
        }
        return nested;
    }

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case BlockStatementNode block:
                // ECMA-262 14.2 - every block creates a new lexical scope.
                // Map name â†’ isConst so EnterScope creates the right binding kind.
                var blockDecls = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var s in block.Statements)
                {
                    if (s is VariableDeclarationStatementNode vd2 &&
                        (string.Equals(vd2.Kind, "let", StringComparison.Ordinal) ||
                         string.Equals(vd2.Kind, "const", StringComparison.Ordinal)))
                    {
                        bool isConst = string.Equals(vd2.Kind, "const", StringComparison.Ordinal);
                        foreach (var d2 in vd2.Declarators)
                        {
                            foreach (var boundName in GetDeclaratorBoundNames(d2))
                            {
                                blockDecls[boundName] = isConst;
                            }
                        }
                    }
                    else if (s is FunctionDeclarationNode fd && fd.Name.Length > 0)
                    {
                        blockDecls[fd.Name] = false;
                    }
                }
                if (blockDecls.Count > 0)
                {
                    var nameSet = new HashSet<string>(blockDecls.Keys, StringComparer.Ordinal);
                    _blockScopedNameStack.Push(nameSet);
                    foreach (var kvp in blockDecls)
                    {
                        var bSlot = GetOrCreateVariableSlot(kvp.Key);
                        // B=0 â†’ mutable (let), B=1 â†’ immutable (const)
                        int bImmutable = kvp.Value ? 1 : 0;
                        _instructions.Add(new Instruction(OpCode.EnterScope, bSlot, bImmutable, 0));
                        _openScopeDepth++;
                    }
                }
                foreach (var nestedStatement in block.Statements)
                {
                    if (nestedStatement is FunctionDeclarationNode nestedFunction)
                    {
                        CompileBlockFunctionDeclaration(nestedFunction);
                    }
                }
                foreach (var nested in block.Statements)
                {
                    CompileStatement(nested);
                }
                if (blockDecls.Count > 0)
                {
                    foreach (var _ in blockDecls)
                    {
                        _instructions.Add(new Instruction(OpCode.LeaveScope));
                        _openScopeDepth--;
                    }
                    _blockScopedNameStack.Pop();
                }
                break;
            case VariableDeclarationStatementNode decl:
                foreach (var d in decl.Declarators)
                {
                    var boundNames = GetDeclaratorBoundNames(d);
                    foreach (var boundName in boundNames)
                    {
                        bool isBlockScoped = IsBlockScopedName(boundName);
                        if (string.Equals(decl.Kind, "var", StringComparison.Ordinal))
                        {
                            _varDeclarationNames.Add(boundName);
                        }
                        else if (!isBlockScoped)
                        {
                            if (string.Equals(decl.Kind, "const", StringComparison.Ordinal))
                            {
                                _constDeclarationNames.Add(boundName);
                            }
                            else
                            {
                                _lexicalDeclarationNames.Add(boundName);
                            }
                        }

                        _ = GetOrCreateVariableSlot(boundName);
                    }

                    var op = string.Equals(decl.Kind, "var", StringComparison.Ordinal)
                        ? OpCode.StoreVar
                        : OpCode.InitVar;
                    if (d.Initializer is not null)
                    {
                        if (d.BindingPattern is null)
                        {
                            // NamedEvaluation: `var f = function(){}` names the function "f".
                            var reg = CompileNamedInitializer(d.Initializer, d.Identifier);
                            var slot = GetOrCreateVariableSlot(d.Identifier);
                            _instructions.Add(new Instruction(op, reg, slot, 0));
                        }
                        else
                        {
                            var reg = CompileExpression(d.Initializer);
                            EmitBindingPatternAssignment(d.BindingPattern, reg, op);
                        }
                    }
                    else if (string.Equals(decl.Kind, "let", StringComparison.Ordinal) && d.BindingPattern is null)
                    {
                        // For block-scoped let without initializer, emit InitVar
                        // with undefined to move the binding out of TDZ.
                        // For function-scoped let, same InitVar path.
                        var reg = AllocateRegister();
                        var ci = AddConstant(JsValue.Undefined);
                        _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
                        var slot = GetOrCreateVariableSlot(d.Identifier);
                        _instructions.Add(new Instruction(OpCode.InitVar, reg, slot, 0));
                    }
                }
                break;
            case ExpressionStatementNode exprStmt:
                var exprReg = CompileExpression(exprStmt.Expression);
                if (_captureCompletionValue)
                {
                    _instructions.Add(new Instruction(OpCode.Move, 0, exprReg, 0));
                }
                break;
            case IfStatementNode ifStmt:
                EmitCompletionReset();
                CompileIfStatement(ifStmt);
                break;
            case WhileStatementNode whileStmt:
                EmitCompletionReset();
                CompileWhileStatement(whileStmt);
                break;
            case DoWhileStatementNode doWhileStmt:
                EmitCompletionReset();
                CompileDoWhileStatement(doWhileStmt);
                break;
            case WithStatementNode withStmt:
                EmitCompletionReset();
                CompileWithStatement(withStmt);
                break;
            case ForStatementNode forStmt:
                EmitCompletionReset();
                CompileForStatement(forStmt);
                break;
            case ForInStatementNode forInStmt:
                EmitCompletionReset();
                CompileForInStatement(forInStmt);
                break;
            case ForOfStatementNode forOfStmt:
                EmitCompletionReset();
                CompileForOfStatement(forOfStmt);
                break;
            case ForAwaitOfStatementNode forAwaitOfStmt:
                EmitCompletionReset();
                CompileForAwaitOfStatement(forAwaitOfStmt);
                break;
            case ReturnStatementNode returnStmt:
                CompileReturnStatement(returnStmt);
                break;
            case BreakStatementNode breakStmt:
                CompileBreakStatement(breakStmt.Label);
                break;
            case ContinueStatementNode continueStmt:
                CompileContinueStatement(continueStmt.Label);
                break;
            case DebuggerStatementNode:
                break;
            case ThrowStatementNode throwStmt:
                CompileThrowStatement(throwStmt);
                break;
            case TryCatchStatementNode tryCatchStmt:
                EmitCompletionReset();
                CompileTryCatchStatement(tryCatchStmt);
                break;
            case TryFinallyStatementNode tryFinallyStmt:
                EmitCompletionReset();
                CompileTryFinallyStatement(tryFinallyStmt);
                break;
            case TryCatchFinallyStatementNode tryCatchFinallyStmt:
                EmitCompletionReset();
                CompileTryCatchFinallyStatement(tryCatchFinallyStmt);
                break;
            case FunctionDeclarationNode functionDecl:
                // Function declarations are instantiated before statement execution
                // by HoistFunctionDeclarations, matching ECMA-262 declaration
                // instantiation and letting sibling functions resolve through the
                // environment record rather than legacy captured cells.
                _ = functionDecl;
                break;
            case ClassDeclarationNode classDecl:
                CompileClassDeclaration(classDecl);
                break;
            case SwitchStatementNode switchStmt:
                EmitCompletionReset();
                CompileSwitchStatement(switchStmt);
                break;
            case LabeledStatementNode labeledStmt:
                EmitCompletionReset();
                CompileLabeledStatement(labeledStmt);
                break;
            default:
                // Minimal compiler slice currently targets literals/arithmetic/variables.
                break;
        }
    }

    private void HoistFunctionDeclarations(IEnumerable<StatementNode> statements)
    {
        foreach (var stmt in statements)
        {
            if (stmt is FunctionDeclarationNode functionDecl)
            {
                CompileFunctionDeclaration(functionDecl);
            }
        }
    }

    // ECMA-262 ExpectedArgumentCount (function `length`): count the formal parameters
    // up to — but not including — the first one that has a default initializer or is
    // the rest parameter. A destructuring parameter without a default still counts.
    private static int ComputeExpectedArgumentCount(
        int parameterCount, int restParameterIndex, IReadOnlyList<ExpressionNode?>? parameterDefaults)
    {
        var count = 0;
        for (var i = 0; i < parameterCount; i++)
        {
            if (i == restParameterIndex) break;
            if (parameterDefaults is not null && i < parameterDefaults.Count && parameterDefaults[i] is not null) break;
            count++;
        }
        return count;
    }

    private void CompileFunctionDeclaration(FunctionDeclarationNode functionDecl)
        => CompileFunctionDeclaration(functionDecl, blockScoped: false);

    private void CompileBlockFunctionDeclaration(FunctionDeclarationNode functionDecl)
        => CompileFunctionDeclaration(functionDecl, blockScoped: true);

    private void CompileFunctionDeclaration(FunctionDeclarationNode functionDecl, bool blockScoped)
    {
        var nestedProgram = BuildFunctionProgramWithParameterBindings(
            functionDecl.Body.Statements,
            functionDecl.Body.Span,
            functionDecl.Parameters,
            functionDecl.ParameterBindings,
            out var prologueCount,
            functionDecl.ParameterDefaults);
        var childCompiler = new BytecodeCompiler();
        var nestedFunction = childCompiler.CompileProgramCore(
            nestedProgram,
            functionDecl.Parameters,
            functionDecl.RestParameterIndex,
            functionDecl.Name,
            hasOwnArgumentsObject: true,
            hasSimpleParameterList: functionDecl.HasSimpleParameterList,
            functionKind: SelectFunctionKind(functionDecl.IsAsync, functionDecl.IsGenerator, isArrow: false),
            inheritedStrictMode: _isStrictMode,
            captureCompletionValue: false,
            prologueStatementCount: prologueCount,
            parameterDefaults: functionDecl.ParameterDefaults);
        AttachSource(nestedFunction, childCompiler, functionDecl.Span);
        var nestedIndex = _nestedFunctions.Count;
        _nestedFunctions.Add(nestedFunction);

        var dest = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
        var slot = GetOrCreateVariableSlot(functionDecl.Name);
        if (blockScoped)
        {
            _instructions.Add(new Instruction(OpCode.InitVar, dest, slot, 0));
            // Annex B.3.3: also copy the function value into the VariableEnvironment
            // binding of the same name (sloppy mode, no conflicting lexical decl).
            if (!_isStrictMode && _annexBFunctionNames.Contains(functionDecl.Name))
            {
                _instructions.Add(new Instruction(OpCode.StoreVarTop, dest, slot, 0));
            }
        }
        else
        {
            _varDeclarationNames.Add(functionDecl.Name);
            _instructions.Add(new Instruction(OpCode.StoreVar, dest, slot, 0));
        }
    }

    private static ProgramNode BuildFunctionProgramWithParameterBindings(
        IReadOnlyList<StatementNode> bodyStatements,
        SourceSpan bodySpan,
        IReadOnlyList<string> parameterNames,
        IReadOnlyList<BindingPatternNode?>? parameterBindings,
        out int prologueStatementCount,
        IReadOnlyList<ExpressionNode?>? parameterDefaults = null)
    {
        prologueStatementCount = 0;
        var hasBindings = parameterBindings is not null && parameterBindings.Count > 0;
        var hasDefaults = parameterDefaults is not null && parameterDefaults.Any(d => d is not null);
        if (!hasBindings && !hasDefaults)
        {
            return new ProgramNode(ProgramKind.Script, bodyStatements, bodySpan);
        }

        // Synthesise a prologue that runs in parameter order. For each parameter:
        //  1. apply its default value (`if (p === undefined) p = <default>;`) per
        //     ECMA-262 10.2.1.3 / FunctionDeclarationInstantiation — the default is
        //     evaluated only when the argument is undefined, and later defaults may
        //     reference earlier (already-defaulted) parameters.
        //  2. destructure a binding-pattern parameter (`var {x} = p;`) so the
        //     pattern sees the defaulted value.
        var prelude = new List<StatementNode>();
        var count = parameterNames.Count;
        for (var i = 0; i < count; i++)
        {
            var defaultExpr = parameterDefaults is not null && i < parameterDefaults.Count
                ? parameterDefaults[i]
                : null;
            if (defaultExpr is not null)
            {
                // ECMA-262 10.2.1.3 step 25.c.i.2: when evaluating the default
                // initializer for parameter i, parameters i..n-1 are still
                // uninitialized (TDZ). Replace any identifier references to them
                // with a node that emits a throw ReferenceError at runtime.
                if (i < count - 1 || defaultExpr is IdentifierExpressionNode idSelf && idSelf.Name == parameterNames[i])
                {
                    var tdzParams = new HashSet<string>(StringComparer.Ordinal);
                    for (var j = i; j < count; j++)
                        tdzParams.Add(parameterNames[j]);
                    defaultExpr = ReplaceTdzParameterReferences(defaultExpr, tdzParams);
                }

                var parameterName = parameterNames[i];
                var span = defaultExpr.Span;
                var undefinedRef = new IdentifierExpressionNode("undefined", span);
                var test = new BinaryExpressionNode("===", new IdentifierExpressionNode(parameterName, span), undefinedRef, span);
                var assign = new AssignmentExpressionNode(new IdentifierExpressionNode(parameterName, span), defaultExpr, span);
                prelude.Add(new IfStatementNode(test, new ExpressionStatementNode(assign, span), null, span));
            }

            var bindingPattern = parameterBindings is not null && i < parameterBindings.Count
                ? parameterBindings[i]
                : null;
            if (bindingPattern is not null)
            {
                var parameterName = parameterNames[i];
                var parameterRef = new IdentifierExpressionNode(parameterName, bindingPattern.Span);
                var declarator = new VariableDeclaratorNode(parameterName, parameterRef, bindingPattern.Span, bindingPattern);
                prelude.Add(new VariableDeclarationStatementNode("var", new[] { declarator }, bindingPattern.Span));
            }
        }

        if (prelude.Count == 0)
        {
            return new ProgramNode(ProgramKind.Script, bodyStatements, bodySpan);
        }

        var statements = new List<StatementNode>(prelude.Count + bodyStatements.Count);
        statements.AddRange(prelude);
        statements.AddRange(bodyStatements);
        prologueStatementCount = prelude.Count;
        return new ProgramNode(ProgramKind.Script, statements, bodySpan);
    }

    /// <summary>
    /// Walk an expression tree and replace every <see cref="IdentifierExpressionNode"/>
    /// whose name is in <paramref name="tdzParameterNames"/> with a
    /// <see cref="TdzReferenceErrorExpressionNode"/>. This enforces ECMA-262 10.2.1.3
    /// step 25.c.i.2: a default initializer must throw ReferenceError when it accesses
    /// a parameter that is still uninitialized (the parameter itself or a later one).
    /// </summary>
    private static ExpressionNode ReplaceTdzParameterReferences(ExpressionNode expr, HashSet<string> tdzParameterNames)
    {
        return expr switch
        {
            IdentifierExpressionNode id when tdzParameterNames.Contains(id.Name) =>
                new TdzReferenceErrorExpressionNode(id.Name, id.Span),

            // Recursively walk compound expressions
            BinaryExpressionNode bin =>
                new BinaryExpressionNode(bin.Operator,
                    ReplaceTdzParameterReferences(bin.Left, tdzParameterNames),
                    ReplaceTdzParameterReferences(bin.Right, tdzParameterNames),
                    bin.Span),

            UnaryExpressionNode un =>
                new UnaryExpressionNode(un.Operator,
                    ReplaceTdzParameterReferences(un.Operand, tdzParameterNames),
                    un.Span),

            ConditionalExpressionNode cond =>
                new ConditionalExpressionNode(
                    ReplaceTdzParameterReferences(cond.Test, tdzParameterNames),
                    ReplaceTdzParameterReferences(cond.Consequent, tdzParameterNames),
                    ReplaceTdzParameterReferences(cond.Alternate, tdzParameterNames),
                    cond.Span),

            AssignmentExpressionNode assign =>
                new AssignmentExpressionNode(
                    ReplaceTdzParameterReferences(assign.Left, tdzParameterNames),
                    ReplaceTdzParameterReferences(assign.Right, tdzParameterNames),
                    assign.Span),

            CallExpressionNode call =>
                new CallExpressionNode(
                    ReplaceTdzParameterReferences(call.Callee, tdzParameterNames),
                    call.Arguments.Select(a => ReplaceTdzParameterReferences(a, tdzParameterNames)).ToList(),
                    call.Span),

            MemberExpressionNode member =>
                new MemberExpressionNode(
                    ReplaceTdzParameterReferences(member.Object, tdzParameterNames),
                    member.Property,
                    member.Computed,
                    member.PropertyExpression is not null
                        ? ReplaceTdzParameterReferences(member.PropertyExpression, tdzParameterNames)
                        : null,
                    member.Span),

            ParenthesizedExpressionNode paren =>
                new ParenthesizedExpressionNode(
                    ReplaceTdzParameterReferences(paren.Expression, tdzParameterNames),
                    paren.Span),

            ArrayLiteralExpressionNode arr =>
                new ArrayLiteralExpressionNode(
                    arr.Elements.Select(e => ReplaceTdzParameterReferences(e, tdzParameterNames)).ToList(),
                    arr.Span),

            ObjectLiteralExpressionNode obj =>
                new ObjectLiteralExpressionNode(
                    obj.Properties.Select(p =>
                        new ObjectPropertyNode(
                            p.Key, p.ComputedKey,
                            p.IsComputed,
                            ReplaceTdzParameterReferences(p.Value, tdzParameterNames),
                            p.Span,
                            p.Kind,
                            p.IsCoverInitializedName)).ToList(),
                    obj.Span),

            SpreadElementExpressionNode spread =>
                new SpreadElementExpressionNode(
                    ReplaceTdzParameterReferences(spread.Argument, tdzParameterNames),
                    spread.Span),

            NewExpressionNode ne =>
                new NewExpressionNode(
                    ReplaceTdzParameterReferences(ne.Callee, tdzParameterNames),
                    ne.Arguments.Select(a => ReplaceTdzParameterReferences(a, tdzParameterNames)).ToList(),
                    ne.Span),

            TemplateLiteralExpressionNode tmpl =>
                new TemplateLiteralExpressionNode(
                    tmpl.Quasis,
                    tmpl.Expressions.Select(e => ReplaceTdzParameterReferences(e, tdzParameterNames)).ToList(),
                    tmpl.Span),

            TaggedTemplateExpressionNode tagged =>
                new TaggedTemplateExpressionNode(
                    ReplaceTdzParameterReferences(tagged.Tag, tdzParameterNames),
                    (TemplateLiteralExpressionNode)ReplaceTdzParameterReferences(tagged.Template, tdzParameterNames),
                    tagged.Span),

            // For simple leaf nodes (literals, this, super, etc.), return unchanged.
            _ => expr,
        };
    }

    // Lower a class declaration into ordinary CreateFunction + NewObject +
    // SetPropByName opcodes. The synthesised value is bound to the class name
    // via the lexical declaration path so it follows ECMA-262 15.7's TDZ rule
    // ("a class binding is created before any of its computations").
    private void CompileClassDeclaration(ClassDeclarationNode classDecl)
    {
        _lexicalDeclarationNames.Add(classDecl.Name);
        var slot = GetOrCreateVariableSlot(classDecl.Name);
        // Bind the class name in the outer scope BEFORE static initialization
        // blocks run so they (and any methods they call) can resolve the class
        // by name. ECMA-262 puts the binding in an inner class-scope environment
        // that the body sees but the outer scope doesn't see until after the body
        // runs; we approximate that with a single hoisted binding for now.
        var classReg = CompileClassExpressionToRegister(
            classDecl.Name,
            classDecl.BaseClass,
            classDecl.Members,
            bindNameBeforeStaticBlocks: reg =>
                _instructions.Add(new Instruction(OpCode.InitVar, reg, slot, 0)));
    }

    // Synthesise the constructor function + prototype object, install methods
    // on the prototype (non-static) or constructor (static), and return the
    // register holding the constructor function. Extends/super are not handled
    // here yet - a base-class expression is currently ignored, which is enough
    // for the H.1 surface (constructor + methods + static methods).
    // ECMA-262 IsAnonymousFunctionDefinition: an anonymous function expression,
    // arrow function, or anonymous class expression — the forms that adopt a name
    // via NamedEvaluation when they appear on the RHS of a binding/assignment.
    private static bool IsAnonymousFunctionDefinition(ExpressionNode expr)
    {
        // The check looks through the cover grammar: `(function () {})` is still
        // an anonymous function definition, while `(0, function () {})` is not.
        while (expr is ParenthesizedExpressionNode paren)
        {
            expr = paren.Expression;
        }

        return expr switch
        {
            FunctionExpressionNode f => f.Name is null,
            ArrowFunctionExpressionNode => true,
            ClassExpressionNode c => c.Name is null,
            _ => false,
        };
    }

    // Compile an initializer expression, threading `name` into it via NamedEvaluation
    // when the expression is an anonymous function definition. Other expressions are
    // compiled normally.
    private int CompileNamedInitializer(ExpressionNode expr, string name)
    {
        if (IsAnonymousFunctionDefinition(expr))
        {
            _pendingNameHint = name;
        }

        return CompileExpression(expr);
    }

    private string? ConsumeNameHint()
    {
        var hint = _pendingNameHint;
        _pendingNameHint = null;
        return hint;
    }

    private int CompileClassExpressionToRegister(
        string? className,
        ExpressionNode? baseClass,
        IReadOnlyList<ClassMemberNode> members,
        Action<int>? bindNameBeforeStaticBlocks = null)
    {
        var savedClassStrictMode = _isStrictMode;
        _isStrictMode = true;
        try
        {
        // H.2 - evaluate the base-class expression BEFORE compiling the class body
        // so that class B extends A {} fails fast when A is a TDZ binding (ECMA-
        // 262 15.7.14 ClassDefinitionEvaluation step 7).
        var baseReg = -1;
        if (baseClass is not null)
        {
            baseReg = CompileExpression(baseClass);
        }

// H.5 - build the mangle map for private members of this class. Each
        // private name (`#x`, `#m`) maps to a unique-per-class suffixed key
        // (`#x@C7`) which is used as an ordinary property name by Get/SetProp.
        var privateMangle = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!member.IsPrivate) continue;
            if (member.Kind == ClassMemberKind.Field || member.Kind == ClassMemberKind.Method
                || member.Kind == ClassMemberKind.Getter || member.Kind == ClassMemberKind.Setter)
            {
                if (!privateMangle.ContainsKey(member.Name))
                {
                    var id = System.Threading.Interlocked.Increment(ref s_privateClassCounter);
                    // Mangle to a name that does NOT start with '#' so the
                    // `ThrowIfPrivateMemberAccess` guard on remaining raw `#x`
                    // accesses (i.e. references to an undeclared private name)
                    // still fires.
                    privateMangle[member.Name] = "__priv" + id + "__" + member.Name.Substring(1);
                }
            }
        }

        // Generate a per-class brand token for private field access validation.
        // All DefinePrivateField/GetPrivateField/SetPrivateField instructions in
        // this class's constructor will carry D=0 (index into BrandTokens).
        if (privateMangle.Count > 0 && _brandTokens.Count == 0)
        {
            _brandTokens.Add(System.Threading.Interlocked.Increment(ref s_brandCounter));
        }

        // H.5 - apply the private-name mangle to each member's function body /
        // field initializer, and to the member's installed name when the member
        // itself is private.
        if (privateMangle.Count > 0)
        {
            var rewritten = new List<ClassMemberNode>(members.Count);
            foreach (var member in members)
            {
                ExpressionNode newFn = member.Function;
                if (member.Function is FunctionExpressionNode fn)
                {
                    var newBody = new BlockStatementNode(
                        PrivateNameRewriter.RewriteStatements(fn.Body.Statements, privateMangle),
                        fn.Body.Span);
                    newFn = new FunctionExpressionNode(
                        fn.Name,
                        fn.Parameters,
                        newBody,
                        fn.Span,
                        IsAsync: fn.IsAsync,
                        IsGenerator: fn.IsGenerator,
                        HasSimpleParameterList: fn.HasSimpleParameterList,
                        RestParameterIndex: fn.RestParameterIndex,
                        ParameterBindings: fn.ParameterBindings,
                        ParameterDefaults: fn.ParameterDefaults,
                        IsMethod: fn.IsMethod);
                }
                else
                {
                    newFn = PrivateNameRewriter.Rewrite(member.Function, privateMangle);
                }

                string newName = member.Name;
                if (member.IsPrivate && privateMangle.TryGetValue(member.Name, out var mangled))
                {
                    newName = mangled;
                }
                rewritten.Add(member with { Name = newName, Function = newFn });
            }
            members = rewritten;
        }

        // Locate constructor (or synthesise an empty one). For derived classes
        // without an explicit constructor, the synthesised default is
        // `constructor() { super(); }` (the spec passes ...args; we forward
        // zero args for now since rest-spread isn't compiled).
        bool isDerived = baseClass is not null;
        FunctionExpressionNode? explicitCtor = null;
        foreach (var member in members)
        {
            if (member.Kind == ClassMemberKind.Constructor && member.Function is FunctionExpressionNode fn)
            {
                explicitCtor = fn;
                break;
            }
        }
        FunctionExpressionNode constructorFn = explicitCtor
            ?? SynthesizeDefaultConstructor(className, isDerived);

        // H.5 - public instance fields. ECMA-262 15.7.10 [[InitializeInstanceElements]]
        // runs on constructor entry (base) or right after super() returns (derived).
        var instanceFieldInits = new List<StatementNode>();
        foreach (var member in members)
        {
            if (member.Kind != ClassMemberKind.Field || member.IsStatic) continue;
            MemberExpressionNode lhs;
            if (member.ComputedName is not null)
            {
                lhs = new MemberExpressionNode(
                    new ThisExpressionNode(member.Span),
                    Property: string.Empty,
                    Computed: true,
                    PropertyExpression: member.ComputedName,
                    member.Span);
            }
            else
            {
                lhs = new MemberExpressionNode(
                    new ThisExpressionNode(member.Span),
                    member.Name,
                    Computed: false,
                    PropertyExpression: null,
                    member.Span);
            }
            var assign = new AssignmentExpressionNode(lhs, member.Function, member.Span);
            instanceFieldInits.Add(new ExpressionStatementNode(assign, member.Span));
        }
        if (instanceFieldInits.Count > 0)
        {
            IReadOnlyList<StatementNode> combined;
            if (isDerived)
            {
                // Inject after the first top-level `super(...)` ExpressionStatement.
                // If none is found, append at end (constructors that never call
                // super are a spec error we don't enforce here).
                var stmts = new List<StatementNode>(constructorFn.Body.Statements);
                int insertAt = stmts.Count;
                for (int i = 0; i < stmts.Count; i++)
                {
                    if (stmts[i] is ExpressionStatementNode es
                        && es.Expression is CallExpressionNode call
                        && call.Callee is SuperExpressionNode)
                    {
                        insertAt = i + 1;
                        break;
                    }
                }
                stmts.InsertRange(insertAt, instanceFieldInits);
                combined = stmts;
            }
            else
            {
                var list = new List<StatementNode>(instanceFieldInits.Count + constructorFn.Body.Statements.Count);
                list.AddRange(instanceFieldInits);
                list.AddRange(constructorFn.Body.Statements);
                combined = list;
            }
            var newBody = new BlockStatementNode(combined, constructorFn.Body.Span);
            constructorFn = new FunctionExpressionNode(
                constructorFn.Name,
                constructorFn.Parameters,
                newBody,
                constructorFn.Span,
                IsAsync: constructorFn.IsAsync,
                IsGenerator: constructorFn.IsGenerator);
        }

        // Compile constructor.
        // H.5: compile constructor with private field brand awareness.
        var savedConstructorContext = _compilingClassConstructor;
        var savedIsDerived = _isDerivedConstructor;
        var savedIsClassConstructor = _isClassConstructor;
        var savedStrictMode = _isStrictMode;
        _isStrictMode = true;
        _compilingClassConstructor = privateMangle.Count > 0;
        _isDerivedConstructor = isDerived;
        _isClassConstructor = true;
        var classReg = CompileFunctionExpressionToRegister(constructorFn);
        _compilingClassConstructor = savedConstructorContext;
        _isDerivedConstructor = savedIsDerived;
        _isClassConstructor = savedIsClassConstructor;
        _isStrictMode = savedStrictMode;

        // Build prototype object. A class constructor (FunctionKind.Constructor)
        // is created with a NON-writable, non-configurable own `prototype` slot
        // that already points at a fresh ordinary object carrying `constructor`.
        // Install the class's methods directly on THAT object — reassigning the
        // slot with a fresh object would be silently rejected by the non-writable
        // descriptor, dropping every method (ECMA-262 15.7.14: the prototype is
        // built once and the constructor is created with it via MakeConstructor).
        var protoReg = AllocateRegister();
        var protoNameIdx_init = GetOrCreatePropertyName("prototype");
        _instructions.Add(new Instruction(OpCode.GetPropByName, protoReg, classReg, protoNameIdx_init));

        // H.2 - wire the extends prototype chain. The new class's prototype
        // inherits from base.prototype (so instances see inherited methods); the
        // class itself inherits from base (so static methods inherit).
        if (baseReg != -1)
        {
            var protoNameIdx_extends = GetOrCreatePropertyName("prototype");
            var basePrototypeReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.GetPropByName, basePrototypeReg, baseReg, protoNameIdx_extends));
            _instructions.Add(new Instruction(OpCode.SetPrototype, protoReg, basePrototypeReg, 0));
            _instructions.Add(new Instruction(OpCode.SetPrototype, classReg, baseReg, 0));
        }

        // H.3.2 - record the class itself as the constructor's HomeObject so
        // LoadSuperConstructor can read HomeObject.[[Prototype]] to find the
        // base class. The constructor is the class object itself, so this is
        // an intentional self-reference.
        _instructions.Add(new Instruction(OpCode.SetHomeObject, classReg, classReg, 0));

        // For each non-constructor member, compile its function and install it
        // on either the prototype (instance methods) or the constructor (static).
        // Getter/setter members get accessor descriptors; method members get
        // plain data descriptors.
        // H.5 - computed property names are handled by compiling the ComputedName
        // expression and using the ByReg variants for accessors, or SetElem for methods.
        foreach (var member in members)
        {
            if (member.Kind == ClassMemberKind.Constructor) continue;
            if (member.Kind == ClassMemberKind.StaticBlock) continue;
            if (member.Function is not FunctionExpressionNode methodFn) continue;

            savedStrictMode = _isStrictMode;
            _isStrictMode = true;
            var methodReg = CompileFunctionExpressionToRegister(methodFn);
            _isStrictMode = savedStrictMode;

            var targetReg = member.IsStatic ? classReg : protoReg;
            // H.3 - record the home object so `super.x` lookups can walk the
            // prototype chain from inside the method body.
            _instructions.Add(new Instruction(OpCode.SetHomeObject, methodReg, targetReg, 0));

            // H.5 - Handle computed property names using ByReg opcodes or SetElem
            if (member.ComputedName is not null)
            {
                var keyReg = CompileExpression(member.ComputedName);
                switch (member.Kind)
                {
                    case ClassMemberKind.Getter:
                        _instructions.Add(new Instruction(OpCode.DefineGetterByReg, targetReg, keyReg, methodReg));
                        break;
                    case ClassMemberKind.Setter:
                        _instructions.Add(new Instruction(OpCode.DefineSetterByReg, targetReg, keyReg, methodReg));
                        break;
                    default:
                        // ECMA-262 15.7.13 PropertyDefinitionEvaluation: methods install
                        // with { writable, !enumerable, configurable } via CreateMethodProperty.
                        _instructions.Add(new Instruction(OpCode.DefineMethodByReg, targetReg, keyReg, methodReg));
                        break;
                }
            }
            else
            {
                var nameIndex = GetOrCreatePropertyName(member.Name);
                switch (member.Kind)
                {
                    case ClassMemberKind.Getter:
                        _instructions.Add(new Instruction(OpCode.DefineGetter, targetReg, nameIndex, methodReg));
                        break;
                    case ClassMemberKind.Setter:
                        _instructions.Add(new Instruction(OpCode.DefineSetter, targetReg, nameIndex, methodReg));
                        break;
                    default:
                        // ECMA-262 15.7.13: CreateMethodProperty -> { w:t, e:f, c:t }.
                        _instructions.Add(new Instruction(OpCode.DefineMethod, targetReg, nameIndex, methodReg));
                        break;
                }
            }
        }

        // ECMA-262 15.7.14 step 16: proto.constructor uses CreateMethodProperty
        // -> { writable: true, enumerable: false, configurable: true }. classCtor.prototype
        // is set non-enumerably here too (MakeConstructor will already have created
        // the slot during function creation; this overwrites with the class's proto).
        var ctorNameIdx = GetOrCreatePropertyName("constructor");
        _instructions.Add(new Instruction(OpCode.DefineMethod, protoReg, ctorNameIdx, classReg));
        // `prototype` is already the constructor's own (non-writable) slot — no
        // reassignment needed; methods above mutated the prototype object in place.

        // H.5 - public static fields. Installed on the class itself, before
        // static blocks so the blocks can see them.
        foreach (var member in members)
        {
            if (member.Kind != ClassMemberKind.Field || !member.IsStatic) continue;
            var initReg = CompileExpression(member.Function);
            if (member.ComputedName is not null)
            {
                var keyReg = CompileExpression(member.ComputedName);
                _instructions.Add(new Instruction(OpCode.SetElem, classReg, keyReg, initReg));
            }
            else
            {
                var nameIdx = GetOrCreatePropertyName(member.Name);
                _instructions.Add(new Instruction(OpCode.SetPropByName, classReg, nameIdx, initReg));
            }
        }

        // H.5 - bind the class name in the outer scope BEFORE running static
        // initialization blocks, so the block body can resolve the class by
        // name (e.g. `static { C.x = 1; }`). Approximates ECMA-262 15.7.14
        // step 37 ordering relative to the inner class scope.
        bindNameBeforeStaticBlocks?.Invoke(classReg);

        // H.5 - static initialization blocks. ECMA-262 15.7.10. Run each block
        // with this=class and HomeObject=class so `super.foo` walks the base
        // class's static side. Result is discarded.
        foreach (var member in members)
        {
            if (member.Kind != ClassMemberKind.StaticBlock) continue;
            if (member.Function is not FunctionExpressionNode blockFn) continue;
            savedStrictMode = _isStrictMode;
            _isStrictMode = true;
            var blockReg = CompileFunctionExpressionToRegister(blockFn);
            _isStrictMode = savedStrictMode;
            _instructions.Add(new Instruction(OpCode.SetHomeObject, blockReg, classReg, 0));
            var discard = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.CallMethod0, discard, blockReg, classReg));
        }

        return classReg;
        }
        finally
        {
            _isStrictMode = savedClassStrictMode;
        }
    }

    private int CompileFunctionExpressionToRegister(FunctionExpressionNode fnExpr)
    {
        var nestedProgram = BuildFunctionProgramWithParameterBindings(
            fnExpr.Body.Statements,
            fnExpr.Body.Span,
            fnExpr.Parameters,
            fnExpr.ParameterBindings,
            out var prologueCount,
            fnExpr.ParameterDefaults);
        var childCompiler = new BytecodeCompiler { _compilingClassConstructor = this._compilingClassConstructor, _isDerivedConstructor = this._isDerivedConstructor, _isClassConstructor = this._isClassConstructor, _brandTokens = this._brandTokens };
        var nestedFunction = childCompiler.CompileProgramCore(
            nestedProgram,
            fnExpr.Parameters,
            fnExpr.RestParameterIndex,
            fnExpr.Name,
            hasOwnArgumentsObject: true,
            hasSimpleParameterList: fnExpr.HasSimpleParameterList,
            functionKind: _isClassConstructor
                ? FunctionKind.Constructor
                : SelectFunctionKind(fnExpr.IsAsync, fnExpr.IsGenerator, isArrow: false, isMethod: fnExpr.IsMethod),
            inheritedStrictMode: _isStrictMode,
            captureCompletionValue: false,
            prologueStatementCount: prologueCount,
            parameterDefaults: fnExpr.ParameterDefaults);
        AttachSource(nestedFunction, childCompiler, fnExpr.Span);
        var nestedIndex = _nestedFunctions.Count;
        _nestedFunctions.Add(nestedFunction);
        var dest = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
        return dest;
    }

    private static FunctionExpressionNode SynthesizeDefaultConstructor(string? className, bool isDerived = false)
    {
        // ECMA-262 15.7.10 Default Constructor. For base classes: empty body.
        // For derived classes: `constructor() { super(...arguments); }`.
        var span = default(SourceSpan);
        IReadOnlyList<StatementNode> body;
        if (isDerived)
        {
            var superCall = new CallExpressionNode(
                new SuperExpressionNode(span),
                new ExpressionNode[] { new SpreadElementExpressionNode(new IdentifierExpressionNode("arguments", span), span) },
                span);
            body = new StatementNode[] { new ExpressionStatementNode(superCall, span) };
        }
        else
        {
            body = Array.Empty<StatementNode>();
        }
        return new FunctionExpressionNode(
            Name: className,
            Parameters: Array.Empty<string>(),
            Body: new BlockStatementNode(body, span),
            Span: span);
    }

    private void CompileIfStatement(IfStatementNode ifStmt)
    {
        var testReg = CompileExpression(ifStmt.Test);
        var jumpIfFalseIndex = EmitPlaceholder(OpCode.JumpIfFalse, testReg);

        CompileStatement(WrapBareAnnexBFunction(ifStmt.Consequent));

        if (ifStmt.Alternate is not null)
        {
            var jumpAfterConsequent = EmitPlaceholder(OpCode.Jump);
            PatchJump(jumpIfFalseIndex, _instructions.Count);
            CompileStatement(WrapBareAnnexBFunction(ifStmt.Alternate));
            PatchJump(jumpAfterConsequent, _instructions.Count);
        }
        else
        {
            PatchJump(jumpIfFalseIndex, _instructions.Count);
        }
    }

    private void CompileWhileStatement(WhileStatementNode whileStmt)
    {
        var loopStart = _instructions.Count;
        var testReg = CompileExpression(whileStmt.Test);
        var jumpIfFalseIndex = EmitPlaceholder(OpCode.JumpIfFalse, testReg);
        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),ContinueJumpIndices = new List<int>(),ScopeDepthAtEntry = _openScopeDepth, Seq = _nestingSeq++};
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(whileStmt.Body);
            _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
            var loopEnd = _instructions.Count;
            PatchJump(jumpIfFalseIndex, loopEnd);
            foreach (var breakJump in ctx.BreakJumpIndices)
            {
                PatchJump(breakJump, loopEnd);
            }

            foreach (var continueJump in ctx.ContinueJumpIndices)
            {
                PatchJump(continueJump, loopStart);
            }
        }
        finally
        {
            _ = _loopStack.Pop();
        }
    }

    // ECMA-262 14.7.2 - do Statement while ( Expression );
    private void CompileDoWhileStatement(DoWhileStatementNode stmt)
    {
        var bodyStart = _instructions.Count;
        var ctx = new LoopContext
        {
            ContinueTarget = -1,
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>(),
            ScopeDepthAtEntry = _openScopeDepth,
            Seq = _nestingSeq++,
        };
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(stmt.Body);
            var continueTarget = _instructions.Count;
            ctx.ContinueTarget = continueTarget;
            var testReg = CompileExpression(stmt.Test);
            var notReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.Not, notReg, testReg, 0));
            _instructions.Add(new Instruction(OpCode.JumpIfFalse, notReg, bodyStart, 0));
            var loopEnd = _instructions.Count;
            foreach (var breakJump in ctx.BreakJumpIndices)
            {
                PatchJump(breakJump, loopEnd);
            }
            foreach (var continueJump in ctx.ContinueJumpIndices)
            {
                PatchJump(continueJump, continueTarget);
            }
        }
        finally
        {
            _ = _loopStack.Pop();
        }
    }

    private void CompileReturnStatement(ReturnStatementNode returnStmt)
    {
        // ECMA-262 14.15.3 / 13.10.1: `return expr` inside a try evaluates expr, runs
        // every enclosing finally, then returns. We compute the value into a fresh
        // register first (finally code only allocates higher registers, so it can't
        // clobber it), run the pending finallies inline, then move it into the return
        // slot. When no finally is pending EmitAbruptCompletion is a no-op, preserving
        // the original fast path exactly.
        var hasPendingFinally = _finallyStack.Count > 0;

        if (returnStmt.Argument is not null)
        {
            var reg = CompileExpression(returnStmt.Argument);
            if (hasPendingFinally)
            {
                EmitAbruptCompletion(0, -1, leaveTrailingScopes: false);
            }
            _instructions.Add(new Instruction(OpCode.Move, 0, reg, 0));
        }
        else if (hasPendingFinally)
        {
            EmitAbruptCompletion(0, -1, leaveTrailingScopes: false);
            // The finally may have written register 0; restore the undefined result.
            var undef = LoadUndefinedConstant();
            _instructions.Add(new Instruction(OpCode.Move, 0, undef, 0));
        }

        _instructions.Add(new Instruction(OpCode.Return, 0, 0, 0));
    }

    private void CompileForStatement(ForStatementNode forStmt)
    {
        Dictionary<string, bool>? loopHeaderDecls = null;
        if (forStmt.Initializer is VariableDeclarationStatementNode initDecl &&
            (string.Equals(initDecl.Kind, "let", StringComparison.Ordinal) ||
             string.Equals(initDecl.Kind, "const", StringComparison.Ordinal)))
        {
            loopHeaderDecls = new Dictionary<string, bool>(StringComparer.Ordinal);
            var isConst = string.Equals(initDecl.Kind, "const", StringComparison.Ordinal);
            foreach (var declarator in initDecl.Declarators)
            {
                foreach (var boundName in GetDeclaratorBoundNames(declarator))
                {
                    loopHeaderDecls[boundName] = isConst;
                }
            }

            if (loopHeaderDecls.Count > 0)
            {
                var nameSet = new HashSet<string>(loopHeaderDecls.Keys, StringComparer.Ordinal);
                _blockScopedNameStack.Push(nameSet);
                foreach (var kvp in loopHeaderDecls)
                {
                    var slot = GetOrCreateVariableSlot(kvp.Key);
                    var immutable = kvp.Value ? 1 : 0;
                    _instructions.Add(new Instruction(OpCode.EnterScope, slot, immutable, 0));
                    _openScopeDepth++;
                }
            }
        }

        try
        {
            if (forStmt.Initializer is not null)
            {
                CompileStatement(forStmt.Initializer);
            }

            var loopStart = _instructions.Count;
            int? jumpIfFalseIndex = null;
            if (forStmt.Test is not null)
            {
                var testReg = CompileExpression(forStmt.Test);
                jumpIfFalseIndex = EmitPlaceholder(OpCode.JumpIfFalse, testReg);
            }

            var ctx = new LoopContext
            {
                ContinueTarget = -1,
                BreakJumpIndices = new List<int>(),
                ContinueJumpIndices = new List<int>(),
                ScopeDepthAtEntry = _openScopeDepth,
                Seq = _nestingSeq++
            };
            _loopStack.Push(ctx);
            if (_pendingLabel != null)
            {
                ctx.Label = _pendingLabel;
                _pendingLabel = null;
            }
            try
            {
                CompileStatement(forStmt.Body);
                var continueTarget = _instructions.Count;
                ctx.ContinueTarget = continueTarget;
                if (forStmt.Update is not null)
                {
                    _ = CompileExpression(forStmt.Update);
                }

                _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
                var loopEnd = _instructions.Count;
                if (jumpIfFalseIndex is not null)
                {
                    PatchJump(jumpIfFalseIndex.Value, loopEnd);
                }

                foreach (var breakJump in ctx.BreakJumpIndices)
                {
                    PatchJump(breakJump, loopEnd);
                }

                foreach (var continueJump in ctx.ContinueJumpIndices)
                {
                    PatchJump(continueJump, continueTarget);
                }
            }
            finally
            {
                _ = _loopStack.Pop();
            }
        }
        finally
        {
            if (loopHeaderDecls is { Count: > 0 })
            {
                foreach (var _ in loopHeaderDecls)
                {
                    _instructions.Add(new Instruction(OpCode.LeaveScope));
                    _openScopeDepth--;
                }

                _blockScopedNameStack.Pop();
            }
        }
    }

    private void CompileForInStatement(ForInStatementNode forInStmt)
    {
        MemberExpressionNode? targetMember = null;
        if (!TryGetForBindingTarget(forInStmt.Initializer, out var targetSlot, out var targetPattern))
        {
            if (forInStmt.Initializer is ExpressionStatementNode { Expression: MemberExpressionNode member }
                && member.Object is not SuperExpressionNode)
            {
                targetMember = member;
            }
            else if (forInStmt.Initializer is ExpressionStatementNode expressionInitializer)
            {
                _ = CompileExpression(expressionInitializer.Expression);
                EmitRuntimeReferenceError("Invalid left-hand side in for-in.");
                return;
            }
            else
            {
                throw new InvalidOperationException("Unsupported for-in initializer target.");
            }
        }

        // ECMA-262 14.7.5: `let`/`const` in for-head creates a lexical binding scoped
        // to the loop body. EnterScope creates a new env record + the binding;
        // InitVar initializes it each iteration; LeaveScope pops it at loop exit.
        var isLexicalLoopHead = forInStmt.Initializer is VariableDeclarationStatementNode
            { Kind: "let" or "const" };
        List<int>? lexicalSlots = null;
        bool isConst = false;
        if (isLexicalLoopHead)
        {
            var declaration = (VariableDeclarationStatementNode)forInStmt.Initializer;
            isConst = string.Equals(declaration.Kind, "const", StringComparison.Ordinal);
            lexicalSlots = new List<int>();
            foreach (var boundName in GetDeclaratorBoundNames(declaration.Declarators[0]))
            {
                lexicalSlots.Add(GetOrCreateVariableSlot(boundName));
            }
        }

        // Annex B B.3.5: sloppy-mode `for (var x = init in obj)` executes `init`
        // once before evaluating the RHS expression, then rebinds `x` per key.
        if (forInStmt.Initializer is VariableDeclarationStatementNode { Kind: "var", Declarators.Count: 1 } declaration2 &&
            declaration2.Declarators[0].Initializer is { } initializerExpression)
        {
            var initReg = CompileExpression(initializerExpression);
            EmitForBindingAssignment(targetSlot, targetPattern, initReg);
        }

        var sourceReg = CompileExpression(forInStmt.Iterable);
        var iteratorReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.EnumerateKeys, iteratorReg, sourceReg, 0));

        var loopStart = _instructions.Count;
        var keyReg = AllocateRegister();
        var nextIndex = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.ForInNext, keyReg, iteratorReg, -1));

        var scopeDepthBeforeIteration = _openScopeDepth;

        if (isLexicalLoopHead && lexicalSlots is not null)
        {
            foreach (var slot in lexicalSlots)
            {
                _instructions.Add(new Instruction(OpCode.EnterScope, slot, isConst ? 1 : 0, 0));
                _openScopeDepth++;
            }
        }

        if (targetMember is not null)
        {
            EmitMemberStore(targetMember, keyReg);
        }
        else
        {
            EmitForBindingAssignment(targetSlot, targetPattern, keyReg, useInitVar: isLexicalLoopHead);
        }

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>(),
            ScopeDepthAtEntry = scopeDepthBeforeIteration,
            Seq = _nestingSeq++
        };
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(forInStmt.Body);

            if (isLexicalLoopHead && lexicalSlots is not null)
            {
                foreach (var _ in lexicalSlots)
                {
                    _instructions.Add(new Instruction(OpCode.LeaveScope));
                    _openScopeDepth--;
                }
            }

            _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
            var loopEnd = _instructions.Count;
            _instructions[nextIndex] = _instructions[nextIndex] with { C = loopEnd };

            foreach (var breakJump in ctx.BreakJumpIndices)
            {
                PatchJump(breakJump, loopEnd);
            }

            foreach (var continueJump in ctx.ContinueJumpIndices)
            {
                PatchJump(continueJump, loopStart);
            }
        }
        finally
        {
            _ = _loopStack.Pop();
        }
    }

    // Mirror of CompileForInStatement but using EnumerateValues / ForOfNext so each
    // iteration yields the iterable's value rather than its key. Reuses the same
    // continue/break stack so labelled break still works.
    // ECMA-262 14.11 WithStatement. Evaluate the head, push an object
    // environment for the body (free names resolve against the object first via
    // the runtime name-resolution chain), then pop it. The push counts as an open
    // lexical scope so break/continue/return out of the body emit the matching
    // LeaveScope. `with` is a SyntaxError in strict mode (the parser enforces it).
    private void CompileWithStatement(WithStatementNode withStmt)
    {
        var objReg = CompileExpression(withStmt.Object);
        _instructions.Add(new Instruction(OpCode.PushWithEnvironment, objReg, 0, 0));
        _openScopeDepth++;
        CompileStatement(withStmt.Body);
        _instructions.Add(new Instruction(OpCode.LeaveScope));
        _openScopeDepth--;
    }

    private void CompileForOfStatement(ForOfStatementNode forOfStmt)
    {
        MemberExpressionNode? targetMember = null;
        if (!TryGetForBindingTarget(forOfStmt.Initializer, out var targetSlot, out var targetPattern))
        {
            // A bare (non-super) member expression is a valid for-of reference
            // target (`for (x.y of it)`); resolve and assign it each iteration.
            if (forOfStmt.Initializer is ExpressionStatementNode { Expression: MemberExpressionNode member }
                && member.Object is not SuperExpressionNode)
            {
                targetMember = member;
            }
            else if (forOfStmt.Initializer is ExpressionStatementNode expressionInitializer)
            {
                _ = CompileExpression(expressionInitializer.Expression);
                EmitRuntimeReferenceError("Invalid left-hand side in for-of.");
                return;
            }
            else
            {
                throw new InvalidOperationException("Unsupported for-of initializer target.");
            }
        }

        // ECMA-262 14.7.5: `let`/`const` in for-of head creates a lexical binding
        // scoped to the loop body.
        var isLexicalLoopHead = forOfStmt.Initializer is VariableDeclarationStatementNode
            { Kind: "let" or "const" };
        List<int>? lexicalSlots = null;
        bool isConst = false;
        if (isLexicalLoopHead)
        {
            var declaration = (VariableDeclarationStatementNode)forOfStmt.Initializer;
            isConst = string.Equals(declaration.Kind, "const", StringComparison.Ordinal);
            lexicalSlots = new List<int>();
            foreach (var boundName in GetDeclaratorBoundNames(declaration.Declarators[0]))
            {
                lexicalSlots.Add(GetOrCreateVariableSlot(boundName));
            }
        }

        var sourceReg = CompileExpression(forOfStmt.Iterable);
        var iteratorReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.EnumerateValues, iteratorReg, sourceReg, 0));

        var loopStart = _instructions.Count;
        var valueReg = AllocateRegister();
        var nextIndex = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.ForOfNext, valueReg, iteratorReg, -1));

        var scopeDepthBeforeIteration = _openScopeDepth;

        if (isLexicalLoopHead && lexicalSlots is not null)
        {
            foreach (var slot in lexicalSlots)
            {
                _instructions.Add(new Instruction(OpCode.EnterScope, slot, isConst ? 1 : 0, 0));
                _openScopeDepth++;
            }
        }

        if (targetMember is not null)
        {
            EmitMemberStore(targetMember, valueReg);
        }
        else
        {
            EmitForBindingAssignment(targetSlot, targetPattern, valueReg, useInitVar: isLexicalLoopHead);
        }

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>(),
            ScopeDepthAtEntry = scopeDepthBeforeIteration,
            Seq = _nestingSeq++
        };
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(forOfStmt.Body);

            if (isLexicalLoopHead && lexicalSlots is not null)
            {
                foreach (var _ in lexicalSlots)
                {
                    _instructions.Add(new Instruction(OpCode.LeaveScope));
                    _openScopeDepth--;
                }
            }

            _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));

            // ECMA-262 13.7.5.13 step 5.b: a normal-completion break out of a
            // for-of runs IteratorClose, but exhaustion (the ForOfNext done jump)
            // and continue do not. Route break jumps through an IteratorClose
            // instruction that then falls through to the normal exit; the done
            // jump targets the normal exit directly. Only emit the close when
            // some break actually targets this loop so we don't leave a dead
            // instruction behind for break-free loops.
            int normalExit;
            if (ctx.BreakJumpIndices.Count > 0)
            {
                var breakTarget = _instructions.Count;
                _instructions.Add(new Instruction(OpCode.IteratorClose, 0, iteratorReg, 0));
                normalExit = _instructions.Count;
                foreach (var breakJump in ctx.BreakJumpIndices)
                {
                    PatchJump(breakJump, breakTarget);
                }
            }
            else
            {
                normalExit = _instructions.Count;
            }

            _instructions[nextIndex] = _instructions[nextIndex] with { C = normalExit };

            foreach (var continueJump in ctx.ContinueJumpIndices)
            {
                PatchJump(continueJump, loopStart);
            }
        }
        finally
        {
            _ = _loopStack.Pop();
        }
    }

    // ECMA-262 13.4 Update Expressions. Reads the old value, coerces it with
    // ToNumeric, applies ±1, and stores the new value back to the same
    // reference (evaluated once). Returns the new value for prefix forms and
    // the old (coerced) value for postfix forms. Only Identifier and non-super
    // member targets reach here; the parser keeps the legacy desugar otherwise.
    // ECMA-262 13.15.2 — logical assignment (&&=, ||=, ??=). Evaluates the
    // reference once; the right side is evaluated and stored only when the
    // operator's condition is met (truthy for &&=, falsy for ||=, nullish for
    // ??=). The whole expression yields the stored value, else the existing one.
    private int CompileLogicalAssignment(LogicalAssignmentExpressionNode node)
    {
        var target = node.Target;
        while (target is ParenthesizedExpressionNode paren)
        {
            target = paren.Expression;
        }

        node = node with { Target = target };
        if (node.Target is IdentifierExpressionNode id)
        {
            var slot = GetOrCreateVariableSlot(id.Name);
            var curReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.LoadVar, curReg, slot, 0));
            var resultReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.Move, resultReg, curReg, 0));

            var skipJumps = EmitLogicalAssignTest(node.Operator, curReg);
            // NamedEvaluation: `x ||= function(){}` names the function "x".
            var valReg = CompileNamedInitializer(node.Value, id.Name);
            _instructions.Add(new Instruction(OpCode.StoreVar, valReg, slot, 0));
            _instructions.Add(new Instruction(OpCode.Move, resultReg, valReg, 0));
            foreach (var j in skipJumps)
            {
                PatchJump(j, _instructions.Count);
            }

            return resultReg;
        }

        var member = (MemberExpressionNode)node.Target;
        ThrowIfPrivateMemberAccess(member);
        var objReg = CompileExpression(member.Object);

        if (member.Computed)
        {
            var keyReg = CompileExpression(member.PropertyExpression!);
            var curReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.GetElem, curReg, objReg, keyReg));
            var resultReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.Move, resultReg, curReg, 0));

            var skipJumps = EmitLogicalAssignTest(node.Operator, curReg);
            var valReg = CompileExpression(node.Value);
            _instructions.Add(new Instruction(OpCode.SetElem, objReg, keyReg, valReg));
            _instructions.Add(new Instruction(OpCode.Move, resultReg, valReg, 0));
            foreach (var j in skipJumps)
            {
                PatchJump(j, _instructions.Count);
            }

            return resultReg;
        }

        var nameIndex = GetOrCreatePropertyName(member.Property);
        var isPrivate = IsPrivateMangled(member.Property);
        var getOp = isPrivate ? OpCode.GetPrivateField : OpCode.GetPropByName;
        var curMemberReg = AllocateRegister();
        _instructions.Add(new Instruction(getOp, curMemberReg, objReg, nameIndex));
        var resultMemberReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Move, resultMemberReg, curMemberReg, 0));

        var memberSkipJumps = EmitLogicalAssignTest(node.Operator, curMemberReg);
        var newValReg = CompileExpression(node.Value);
        var setOp = isPrivate
            ? (_compilingClassConstructor ? OpCode.DefinePrivateField : OpCode.SetPrivateField)
            : OpCode.SetPropByName;
        _instructions.Add(new Instruction(setOp, objReg, nameIndex, newValReg));
        _instructions.Add(new Instruction(OpCode.Move, resultMemberReg, newValReg, 0));
        foreach (var j in memberSkipJumps)
        {
            PatchJump(j, _instructions.Count);
        }

        return resultMemberReg;
    }

    // Emit the short-circuit test for a logical assignment. After this returns,
    // the next emitted instructions are the "do the assignment" block; the
    // returned jump placeholders must be patched to the point after that block
    // (they fire when the assignment should be SKIPPED).
    private List<int> EmitLogicalAssignTest(string op, int curReg)
    {
        switch (op)
        {
            case "&&":
                // Assign when current is truthy; skip (jump) when falsy.
                return new List<int> { EmitPlaceholder(OpCode.JumpIfFalse, curReg) };
            case "||":
            {
                // Assign when current is falsy; skip when truthy.
                var toAssign = EmitPlaceholder(OpCode.JumpIfFalse, curReg); // falsy -> assign
                var skip = EmitPlaceholder(OpCode.Jump);                    // truthy -> skip
                PatchJump(toAssign, _instructions.Count);                   // assign block begins here
                return new List<int> { skip };
            }
            default: // "??" — assign only when current is null or undefined.
            {
                var nullConst = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, nullConst, AddConstant(JsValue.Null), 0));
                var undefConst = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, undefConst, AddConstant(JsValue.Undefined), 0));
                var eq = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, eq, curReg, nullConst));
                var notNull = EmitPlaceholder(OpCode.JumpIfFalse, eq);  // not null -> check undefined
                var toAssignFromNull = EmitPlaceholder(OpCode.Jump);    // is null -> assign
                PatchJump(notNull, _instructions.Count);
                _instructions.Add(new Instruction(OpCode.StrictEq, eq, curReg, undefConst));
                var skip = EmitPlaceholder(OpCode.JumpIfFalse, eq);     // not undefined -> skip
                PatchJump(toAssignFromNull, _instructions.Count);       // assign block begins here
                return new List<int> { skip };
            }
        }
    }

    private int CompileUpdateExpression(UnaryExpressionNode unary)
    {
        var isIncrement = unary.Operator is "preIncrement" or "postIncrement";
        var isPrefix = unary.Operator is "preIncrement" or "preDecrement";
        var stepOp = isIncrement ? OpCode.Increment : OpCode.Decrement;

        if (unary.Operand is IdentifierExpressionNode id)
        {
            var slot = GetOrCreateVariableSlot(id.Name);
            var curReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.LoadVar, curReg, slot, 0));
            var oldReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.ToNumeric, oldReg, curReg, 0));
            var newReg = AllocateRegister();
            _instructions.Add(new Instruction(stepOp, newReg, oldReg, 0));
            _instructions.Add(new Instruction(OpCode.StoreVar, newReg, slot, 0));
            return isPrefix ? newReg : oldReg;
        }

        var member = (MemberExpressionNode)unary.Operand;
        ThrowIfPrivateMemberAccess(member);
        var objReg = CompileExpression(member.Object);

        if (member.Computed)
        {
            var keyReg = CompileExpression(member.PropertyExpression!);
            var curReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.GetElem, curReg, objReg, keyReg));
            var oldReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.ToNumeric, oldReg, curReg, 0));
            var newReg = AllocateRegister();
            _instructions.Add(new Instruction(stepOp, newReg, oldReg, 0));
            _instructions.Add(new Instruction(OpCode.SetElem, objReg, keyReg, newReg));
            return isPrefix ? newReg : oldReg;
        }

        var nameIndex = GetOrCreatePropertyName(member.Property);
        var isPrivate = IsPrivateMangled(member.Property);
        var getOp = isPrivate ? OpCode.GetPrivateField : OpCode.GetPropByName;
        var curMemberReg = AllocateRegister();
        _instructions.Add(new Instruction(getOp, curMemberReg, objReg, nameIndex));
        var oldMemberReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.ToNumeric, oldMemberReg, curMemberReg, 0));
        var newMemberReg = AllocateRegister();
        _instructions.Add(new Instruction(stepOp, newMemberReg, oldMemberReg, 0));
        var setOp = isPrivate
            ? (_compilingClassConstructor ? OpCode.DefinePrivateField : OpCode.SetPrivateField)
            : OpCode.SetPropByName;
        _instructions.Add(new Instruction(setOp, objReg, nameIndex, newMemberReg));
        return isPrefix ? newMemberReg : oldMemberReg;
    }

    private void CompileForAwaitOfStatement(ForAwaitOfStatementNode forAwaitOfStmt)
    {
        if (_currentFunctionKind is not FunctionKind.Async and not FunctionKind.AsyncGenerator)
        {
            throw new UnsupportedFeatureException("for-await-outside-async", FeatureSupportLevel.ParserOnly, forAwaitOfStmt.Span);
        }

        if (!TryGetForBindingTarget(forAwaitOfStmt.Initializer, out var targetSlot, out var targetPattern))
        {
            if (forAwaitOfStmt.Initializer is ExpressionStatementNode expressionInitializer)
            {
                _ = CompileExpression(expressionInitializer.Expression);
                EmitRuntimeReferenceError("Invalid left-hand side in for-await-of.");
                return;
            }

            throw new InvalidOperationException("Unsupported for-await-of initializer target.");
        }

        var sourceReg = CompileExpression(forAwaitOfStmt.Iterable);
        var iteratorReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.EnumerateValues, iteratorReg, sourceReg, 0));

        var loopStart = _instructions.Count;
        var valueReg = AllocateRegister();
        var nextIndex = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.ForOfNext, valueReg, iteratorReg, -1));

        var awaitedReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Await, awaitedReg, valueReg, 0));
        EmitForBindingAssignment(targetSlot, targetPattern, awaitedReg);

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),ContinueJumpIndices = new List<int>(),ScopeDepthAtEntry = _openScopeDepth, Seq = _nestingSeq++};
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(forAwaitOfStmt.Body);
            _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
            var loopEnd = _instructions.Count;
            _instructions[nextIndex] = _instructions[nextIndex] with { C = loopEnd };

            foreach (var breakJump in ctx.BreakJumpIndices)
            {
                PatchJump(breakJump, loopEnd);
            }

            foreach (var continueJump in ctx.ContinueJumpIndices)
            {
                PatchJump(continueJump, loopStart);
            }
        }
        finally
        {
            _ = _loopStack.Pop();
        }
    }

    private bool TryGetForBindingTarget(StatementNode initializer, out int slot, out BindingPatternNode? pattern)
    {
        switch (initializer)
        {
            case VariableDeclarationStatementNode { Declarators.Count: 1 } declaration:
                (slot, pattern) = GetForDeclarationTarget(declaration);
                return true;
            case ExpressionStatementNode { Expression: IdentifierExpressionNode identifier }:
                slot = GetOrCreateVariableSlot(identifier.Name);
                pattern = null;
                return true;
            case ExpressionStatementNode expressionInitializer when
                TryConvertForAssignmentPattern(expressionInitializer.Expression, out var assignmentPattern):
                slot = -1;
                pattern = assignmentPattern;
                return true;
            default:
                slot = 0;
                pattern = null;
                return false;
        }
    }

    private bool TryConvertForAssignmentPattern(ExpressionNode expression, out BindingPatternNode? pattern)
    {
        switch (expression)
        {
            case ParenthesizedExpressionNode parenthesized:
                return TryConvertForAssignmentPattern(parenthesized.Expression, out pattern);
            case IdentifierExpressionNode identifier:
                pattern = new IdentifierBindingPatternNode(identifier.Name, identifier.Span);
                return true;
            case MemberExpressionNode member when member.Object is not SuperExpressionNode && !IsPrivateMangled(member.Property):
                // `[o.x] = v` / `({a: o[k]} = v)` — a member-expression assignment
                // target inside a destructuring pattern. Valid only in assignment
                // patterns (the only caller of this converter).
                pattern = new MemberBindingPatternNode(member, member.Span);
                return true;
            case ArrayLiteralExpressionNode array:
            {
                var elements = new List<ArrayBindingElementNode>(array.Elements.Count);
                for (var i = 0; i < array.Elements.Count; i++)
                {
                    var element = array.Elements[i];
                    if (element is SpreadElementExpressionNode spread)
                    {
                        if (i != array.Elements.Count - 1 ||
                            !TryConvertForAssignmentPattern(spread.Argument, out var restPattern) ||
                            restPattern is null)
                        {
                            pattern = null;
                            return false;
                        }

                        elements.Add(new ArrayBindingElementNode(restPattern, null, IsRest: true, spread.Span));
                        continue;
                    }

                    // Elisions in the array-destructuring cover grammar are omitted
                    // target slots (e.g. `[, a] = x` skips index 0).
                    if (element is ElisionExpressionNode elision)
                    {
                        elements.Add(new ArrayBindingElementNode(null, null, IsRest: false, elision.Span));
                        continue;
                    }

                    ExpressionNode targetExpression = element;
                    ExpressionNode? initializer = null;
                    if (element is AssignmentExpressionNode assignment)
                    {
                        targetExpression = assignment.Left;
                        initializer = assignment.Right;
                    }

                    if (!TryConvertForAssignmentPattern(targetExpression, out var elementPattern) ||
                        elementPattern is null)
                    {
                        pattern = null;
                        return false;
                    }

                    elements.Add(new ArrayBindingElementNode(elementPattern, initializer, IsRest: false, element.Span));
                }

                pattern = new ArrayBindingPatternNode(elements, array.Span);
                return true;
            }
            case ObjectLiteralExpressionNode obj:
            {
                var properties = new List<ObjectBindingPropertyNode>(obj.Properties.Count);
                BindingPatternNode? rest = null;
                for (var i = 0; i < obj.Properties.Count; i++)
                {
                    var property = obj.Properties[i];
                    if (property.Kind != ObjectPropertyKind.Data)
                    {
                        pattern = null;
                        return false;
                    }

                    if (property.Value is SpreadElementExpressionNode spread)
                    {
                        if (i != obj.Properties.Count - 1 ||
                            rest is not null ||
                            !TryConvertForAssignmentPattern(spread.Argument, out var restPattern) ||
                            restPattern is null)
                        {
                            pattern = null;
                            return false;
                        }

                        rest = restPattern;
                        continue;
                    }

                    BindingPatternNode? targetPattern = null;
                    ExpressionNode? initializer = null;

                    if (!property.IsComputed && property.Key is not null)
                    {
                        if (property.Value is IdentifierExpressionNode identifier && identifier.Name == property.Key)
                        {
                            targetPattern = new IdentifierBindingPatternNode(identifier.Name, identifier.Span);
                        }
                        else if (property.Value is IdentifierExpressionNode shorthandDefaultIdentifier)
                        {
                            // `{ x = y }` and `{ x: y }` both parse to an IdentifierExpression
                            // value. Favor the explicit property-target interpretation first.
                            targetPattern = new IdentifierBindingPatternNode(shorthandDefaultIdentifier.Name, shorthandDefaultIdentifier.Span);
                        }
                        else if (TryConvertForAssignmentPattern(property.Value, out var valuePattern) &&
                                 valuePattern is not null)
                        {
                            targetPattern = valuePattern;
                        }
                        else if (property.Value is AssignmentExpressionNode aliasedDefault &&
                                 TryConvertForAssignmentPattern(aliasedDefault.Left, out var aliasedTarget) &&
                                 aliasedTarget is not null)
                        {
                            // `{ key: target = default }` — aliased target with a default.
                            targetPattern = aliasedTarget;
                            initializer = aliasedDefault.Right;
                        }
                        else if (property.IsCoverInitializedName)
                        {
                            // Genuine `{ key = default }` shorthand: target is the key
                            // identifier, the value is its default initializer.
                            targetPattern = new IdentifierBindingPatternNode(property.Key, property.Span);
                            initializer = property.Value;
                        }
                        else
                        {
                            // `{ key: <expr> }` where <expr> is not an identifier or a
                            // nested pattern is NOT a valid binding-pattern element (e.g.
                            // `{a: this.#field}` / `{a: o.x}` — a member-expression
                            // assignment target a binding pattern can't represent). Fail
                            // the conversion so the caller compiles the left side as an
                            // expression instead (evaluating the member reference and
                            // throwing the correct error), rather than mis-reading it as a
                            // shorthand default.
                            pattern = null;
                            return false;
                        }
                    }
                    else if (property.Value is AssignmentExpressionNode assignment)
                    {
                        if (!TryConvertForAssignmentPattern(assignment.Left, out targetPattern) || targetPattern is null)
                        {
                            pattern = null;
                            return false;
                        }

                        initializer = assignment.Right;
                    }
                    else if (!TryConvertForAssignmentPattern(property.Value, out targetPattern) ||
                             targetPattern is null)
                    {
                        pattern = null;
                        return false;
                    }

                    properties.Add(new ObjectBindingPropertyNode(
                        property.Key,
                        property.ComputedKey,
                        property.IsComputed,
                        targetPattern,
                        initializer,
                        property.Span));
                }

                pattern = new ObjectBindingPatternNode(properties, rest, obj.Span);
                return true;
            }
            default:
                pattern = null;
                return false;
        }
    }

    private (int Slot, BindingPatternNode? Pattern) GetForDeclarationTarget(VariableDeclarationStatementNode declaration)
    {
        var declarator = declaration.Declarators[0];
        var name = declarator.Identifier;
        if (string.Equals(declaration.Kind, "var", StringComparison.Ordinal))
        {
            foreach (var boundName in GetDeclaratorBoundNames(declarator))
            {
                _varDeclarationNames.Add(boundName);
            }
        }

        if (declarator.BindingPattern is null)
        {
            return (GetOrCreateVariableSlot(name), null);
        }

        foreach (var boundName in GetDeclaratorBoundNames(declarator))
        {
            _ = GetOrCreateVariableSlot(boundName);
        }

        return (-1, declarator.BindingPattern);
    }

    private void EmitForBindingAssignment(int slot, BindingPatternNode? pattern, int valueReg, bool useInitVar = false)
    {
        if (pattern is null)
        {
            _instructions.Add(new Instruction(useInitVar ? OpCode.InitVar : OpCode.StoreVar, valueReg, slot, 0));
            return;
        }

        EmitBindingPatternAssignment(pattern, valueReg, useInitVar ? OpCode.InitVar : OpCode.StoreVar);
    }

    private void CompileThrowStatement(ThrowStatementNode throwStmt)
    {
        var reg = CompileExpression(throwStmt.Argument);
        _instructions.Add(new Instruction(OpCode.Throw, reg, 0, 0));
    }

    private void CompileTryCatchStatement(TryCatchStatementNode tryCatchStmt)
    {
        var pushHandlerIndex = EmitPlaceholder(OpCode.PushHandler);
        CompileStatement(tryCatchStmt.TryBlock);
        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));
        var jumpAfterCatch = EmitPlaceholder(OpCode.Jump);

        var catchEntry = _instructions.Count;
        PatchJump(pushHandlerIndex, catchEntry);

        // ECMA-262 14.3 â€” catch creates a new EnvironmentRecord for the catch
        // parameter. We add it as a var declaration so InstantiateVarDeclarations
        // creates the binding in the function env (initialized to undefined,
        // surviving generator save/restore). StoreVar then writes the actual
        // exception value from register 0. The catch var is function-scoped
        // rather than block-scoped; proper block scoping (EnterScope/LeaveScope)
        // will replace this once those opcodes are fully debugged.
        EmitCatchParameterBinding(tryCatchStmt.CatchIdentifier, tryCatchStmt.CatchPattern);
        // The Catch block's completion starts empty; reset so an empty catch body
        // yields undefined rather than the exception value still sitting in reg 0.
        EmitCompletionReset();
        CompileStatement(tryCatchStmt.CatchBlock);

        PatchJump(jumpAfterCatch, _instructions.Count);
    }

    // Bind the caught exception (in register 0) to the CatchParameter. A plain
    // identifier stores straight into its slot; a BindingPattern destructures the
    // exception value. Bound names are registered as var declarations to match the
    // existing function-scoped catch-binding behaviour.
    private void EmitCatchParameterBinding(string catchIdentifier, BindingPatternNode? catchPattern)
    {
        if (catchPattern is null)
        {
            var catchSlot = GetOrCreateVariableSlot(catchIdentifier);
            _varDeclarationNames.Add(catchIdentifier);
            _instructions.Add(new Instruction(OpCode.StoreVar, 0, catchSlot, 0));
            return;
        }

        var names = new List<string>();
        CollectBoundNames(catchPattern, names);
        foreach (var name in names)
        {
            _ = GetOrCreateVariableSlot(name);
            _varDeclarationNames.Add(name);
        }

        // Copy the exception out of register 0 before destructuring, since pattern
        // assignment allocates and writes registers while extracting elements.
        var exReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Move, exReg, 0, 0));
        EmitBindingPatternAssignment(catchPattern, exReg, OpCode.StoreVar);
    }

    // A Finally block's normal completion value is discarded (ECMA-262 14.15.3):
    // the try/catch value is the statement's completion. Compile it with completion
    // capture suppressed so its expression statements don't overwrite reg 0.
    private void CompileFinallyBlock(StatementNode finallyBlock)
    {
        var saved = _captureCompletionValue;
        _captureCompletionValue = false;
        CompileStatement(finallyBlock);
        _captureCompletionValue = saved;
    }

    private void CompileTryFinallyStatement(TryFinallyStatementNode stmt)
    {
        var pushHandler = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.PushHandler, -1, 0, 0, D: -1));

        // The finally is pending for any break/continue/return inside the try body:
        // such an abrupt completion emits an inline copy of it before exiting
        // (ECMA-262 14.15.3). The throw/normal paths use the shared copy below.
        _finallyStack.Push(new FinallyFrame
        {
            Block = stmt.FinallyBlock,
            Seq = _nestingSeq++,
            ScopeDepthAtEntry = _openScopeDepth
        });
        CompileStatement(stmt.TryBlock);
        _ = _finallyStack.Pop();

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));

        var finallyStart = _instructions.Count;
        CompileFinallyBlock(stmt.FinallyBlock);
        _instructions.Add(new Instruction(OpCode.EndFinally, 0, 0, 0));

        var ins = _instructions[pushHandler];
        _instructions[pushHandler] = ins with { D = finallyStart };
    }

    private void CompileTryCatchFinallyStatement(TryCatchFinallyStatementNode stmt)
    {
        var pushHandler = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.PushHandler, -1, 0, 0, D: -1));

        // Pending across BOTH the try body and the catch body (ECMA-262 14.15.3).
        _finallyStack.Push(new FinallyFrame
        {
            Block = stmt.FinallyBlock,
            Seq = _nestingSeq++,
            ScopeDepthAtEntry = _openScopeDepth
        });

        CompileStatement(stmt.TryBlock);

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));
        var jumpPastCatch = EmitPlaceholder(OpCode.Jump);

        var catchEntry = _instructions.Count;

        var catchFinallyHandler = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.PushHandler, -1, 0, 0, D: -1));

        EmitCatchParameterBinding(stmt.CatchIdentifier, stmt.CatchPattern);
        EmitCompletionReset();
        CompileStatement(stmt.CatchBlock);

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));
        _ = _finallyStack.Pop();

        PatchJump(jumpPastCatch, _instructions.Count);

        var finallyStart = _instructions.Count;
        CompileFinallyBlock(stmt.FinallyBlock);
        _instructions.Add(new Instruction(OpCode.EndFinally, 0, 0, 0));

        var outerIns = _instructions[pushHandler];
        _instructions[pushHandler] = outerIns with { A = catchEntry, D = finallyStart };

        var innerIns = _instructions[catchFinallyHandler];
        _instructions[catchFinallyHandler] = innerIns with { D = finallyStart };
    }

    // ECMA-262 14.12 - switch Statement
    private void CompileSwitchStatement(SwitchStatementNode switchStmt)
    {
        var discReg = CompileExpression(switchStmt.Discriminant);

        // ECMA-262 14.12.4: the CaseBlock is one lexical scope spanning every clause
        // (BlockDeclarationInstantiation runs once over the whole CaseBlock). Collect
        // its let/const bindings and enter that scope after evaluating the discriminant
        // so inner lexical names shadow the enclosing scope instead of colliding on the
        // same slot (e.g. `let x` outside and a different `let x` inside the switch).
        var caseDecls = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var c in switchStmt.Cases)
        {
            foreach (var s in c.Consequent)
            {
                if (s is VariableDeclarationStatementNode vd &&
                    (string.Equals(vd.Kind, "let", StringComparison.Ordinal) ||
                     string.Equals(vd.Kind, "const", StringComparison.Ordinal)))
                {
                    bool isConst = string.Equals(vd.Kind, "const", StringComparison.Ordinal);
                    foreach (var d in vd.Declarators)
                        foreach (var boundName in GetDeclaratorBoundNames(d))
                            caseDecls[boundName] = isConst;
                }
            }
        }

        // Depth before the CaseBlock scope is opened: a `break` exits the whole switch,
        // so its abrupt-completion must tear down these case scopes inline before jumping
        // to the end (which is emitted before the normal-exit LeaveScope ops below).
        var outerScopeDepth = _openScopeDepth;
        if (caseDecls.Count > 0)
        {
            _blockScopedNameStack.Push(new HashSet<string>(caseDecls.Keys, StringComparer.Ordinal));
            foreach (var kvp in caseDecls)
            {
                var slot = GetOrCreateVariableSlot(kvp.Key);
                _instructions.Add(new Instruction(OpCode.EnterScope, slot, kvp.Value ? 1 : 0, 0));
                _openScopeDepth++;
            }
        }

        var caseHeaders = new List<int>(switchStmt.Cases.Count);
        int defaultCaseIndex = -1;
        for (int i = 0; i < switchStmt.Cases.Count; i++)
        {
            var c = switchStmt.Cases[i];
            if (c.Test is not null)
            {
                var testReg = CompileExpression(c.Test);
                var cmpReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, cmpReg, discReg, testReg));
                var notCmpReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.Not, notCmpReg, cmpReg, 0));
                var jumpIndex = EmitPlaceholder(OpCode.JumpIfFalse, notCmpReg);
                caseHeaders.Add(jumpIndex);
            }
            else
            {
                defaultCaseIndex = i;
                caseHeaders.Add(-1);
            }
        }
        var jumpToDefault = EmitPlaceholder(OpCode.Jump);
        var ctx = new LoopContext
        {
            ContinueTarget = -1,
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>(),
            IsSwitch = true,
            ScopeDepthAtEntry = outerScopeDepth,
            Seq = _nestingSeq++,
        };
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            for (int i = 0; i < switchStmt.Cases.Count; i++)
            {
                var bodyStart = _instructions.Count;
                if (caseHeaders[i] != -1)
                {
                    PatchJump(caseHeaders[i], bodyStart);
                }
                if (i == defaultCaseIndex)
                {
                    PatchJump(jumpToDefault, bodyStart);
                }
                foreach (var stmt in switchStmt.Cases[i].Consequent)
                {
                    CompileStatement(stmt);
                }
            }
            if (defaultCaseIndex == -1)
            {
                PatchJump(jumpToDefault, _instructions.Count);
            }
            var loopEnd = _instructions.Count;
            foreach (var breakJump in ctx.BreakJumpIndices)
            {
                PatchJump(breakJump, loopEnd);
            }
        }
        finally
        {
            _ = _loopStack.Pop();
        }

        // Normal (fall-through) exit of the CaseBlock closes its lexical scope. Abrupt
        // exits via `break` already emitted their own LeaveScope ops inline (they jump
        // to loopEnd, above this point).
        if (caseDecls.Count > 0)
        {
            foreach (var _ in caseDecls)
            {
                _instructions.Add(new Instruction(OpCode.LeaveScope));
                _openScopeDepth--;
            }
            _blockScopedNameStack.Pop();
        }
    }

    // ECMA-262 14.11 - Labeled Statement.
    // Labeled loops and switches use _pendingLabel so the loop/switch compilation
    // can tag its LoopContext. Non-loop labels push a LabelTarget for break exit.
    private void CompileLabeledStatement(LabeledStatementNode labeled)
    {
        var isLoop = labeled.Body is WhileStatementNode or ForStatementNode or DoWhileStatementNode
            or ForInStatementNode or ForOfStatementNode or ForAwaitOfStatementNode;
        var isSwitch = labeled.Body is SwitchStatementNode;

        if (isLoop || isSwitch)
        {
            _pendingLabel = labeled.Label;
            CompileStatement(labeled.Body);
            _pendingLabel = null;
        }
        else
        {
            var target = new LabelTarget
            {
                Name = labeled.Label,
                BreakJumpIndices = new List<int>(),
                ScopeDepthAtEntry = _openScopeDepth,
                Seq = _nestingSeq++
            };
            _labelStack.Push(target);
            CompileStatement(WrapBareAnnexBFunction(labeled.Body));
            var end = _instructions.Count;
            foreach (var jumpIdx in target.BreakJumpIndices)
            {
                PatchJump(jumpIdx, end);
            }
            _labelStack.Pop();
        }
    }

    // ECMA-262 Annex B.3.2/B.3.4: in sloppy mode a FunctionDeclaration may appear
    // directly as the Statement of an if/else or labelled statement; it then
    // behaves as if enclosed in a Block. Wrap it so the block-compilation path
    // gives it a block-scoped binding (and, via _annexBFunctionNames, the legacy
    // var-scoped binding). Strict mode forbids this position, so leave it unchanged.
    private StatementNode WrapBareAnnexBFunction(StatementNode stmt)
        => !_isStrictMode && stmt is FunctionDeclarationNode
            ? new BlockStatementNode(new[] { stmt }, stmt.Span)
            : stmt;

    // Returns true if the name is in any active block block-scoped name set.
    private bool IsBlockScopedName(string name)
    {
        foreach (var set in _blockScopedNameStack)
        {
            if (set.Contains(name)) return true;
        }
        return false;
    }

    // Expression-tree compilation is recursive, so a pathologically deep AST
    // (e.g. a minified bundle with thousands of chained `||`/`,`/`+` operands)
    // would otherwise exhaust the native stack and crash the process with an
    // uncatchable StackOverflowException — a denial-of-service hazard on
    // untrusted input. Bound the depth and surface a catchable error instead.
    // The limit is well above what real-world bundles need (x.com's i18n bundle
    // nests ~1550 deep) but must be reachable without overflow, so callers run
    // the compiler on an enlarged stack (see RunFenJsWithLargeStack / jstime).
    // Thread-static so the count reflects the true C# recursion depth across
    // nested-function compilation: each nested function/arrow gets its own
    // BytecodeCompiler instance, but they all recurse on one thread, and it is
    // that combined native-stack depth that risks overflow. Diagnostic only.
    [ThreadStatic] private static int _compileExpressionDepth;
    [ThreadStatic] private static int _maxObservedCompileDepth;

    // Diagnostic: deepest expression-compile recursion observed on this thread.
    public int MaxObservedCompileDepth => _maxObservedCompileDepth;

    private int CompileExpression(ExpressionNode expr)
    {
        if (++_compileExpressionDepth > _maxObservedCompileDepth)
        {
            _maxObservedCompileDepth = _compileExpressionDepth;
        }

        // Probe the *actual* remaining native stack rather than a fixed depth.
        // Real minified bundles nest deeply (x.com's i18n compiles an ~8800-deep
        // left-associative chain), so callers run on an enlarged stack; a fixed
        // limit would either reject valid code or be unreachable before overflow.
        // TryEnsureSufficientExecutionStack returns false near exhaustion, letting
        // us throw a catchable error instead of crashing the process with an
        // uncatchable StackOverflowException (a DoS hazard on untrusted input).
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            _compileExpressionDepth--;
            throw new InvalidOperationException(
                "Expression nesting too deep to compile (insufficient stack).");
        }

        try
        {
            return CompileExpressionCore(expr);
        }
        finally
        {
            _compileExpressionDepth--;
        }
    }

    private int CompileExpressionCore(ExpressionNode expr)
    {
        switch (expr)
        {
            case NumericLiteralExpressionNode number:
            {
                var reg = AllocateRegister();
                var ci = AddConstant(JsValue.FromNumber(number.Value));
                _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
                return reg;
            }
            case BigIntLiteralExpressionNode bigInt:
            {
                var reg = AllocateRegister();
                var text = bigInt.RawText.EndsWith('n') ? bigInt.RawText[..^1] : bigInt.RawText;
                var value = ParseBigIntLiteral(text);
                var ci = AddConstant(JsValue.FromBigInt(value));
                _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
                return reg;
            }
            case StringLiteralExpressionNode str:
            {
                var reg = AllocateRegister();
                var ci = AddConstant(JsValue.FromString(str.Value));
                _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
                return reg;
            }
            case TemplateLiteralExpressionNode template:
                return CompileTemplateLiteral(template);
            case TaggedTemplateExpressionNode tagged:
                return CompileTaggedTemplateExpression(tagged);
            case BooleanLiteralExpressionNode boolean:
            {
                var reg = AllocateRegister();
                var ci = AddConstant(JsValue.FromBoolean(boolean.Value));
                _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
                return reg;
            }
            case NullLiteralExpressionNode:
            {
                var reg = AllocateRegister();
                var ci = AddConstant(JsValue.Null);
                _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
                return reg;
            }
            case IdentifierExpressionNode id:
            {
                var reg = AllocateRegister();
                var slot = GetOrCreateVariableSlot(id.Name);
                _instructions.Add(new Instruction(OpCode.LoadVar, reg, slot, 0));
                return reg;
            }
            case TdzReferenceErrorExpressionNode tdz:
            {
                // ECMA-262 10.2.1.3 step 25.c.i.2: accessing a parameter that is
                // still in the TDZ during default-initializer evaluation throws a
                // ReferenceError. Add "Cannot access" so the message matches what
                // test262 expects.
                EmitRuntimeReferenceError($"Cannot access '{tdz.ParameterName}' before initialization");
                // The throw is unconditional — control never reaches here.
                // Return a dummy register so the expression pipeline stays
                // well-formed (callers expect a register index).
                return AllocateRegister();
            }
            case ThisExpressionNode:
            {
                var reg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadThis, reg, 0, 0));
                return reg;
            }
            case NewTargetExpressionNode:
            {
                var reg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadNewTarget, reg, 0, 0));
                return reg;
            }
            case ImportCallExpressionNode importCall:
            {
                var specReg = CompileExpression(importCall.Specifier);
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.DynamicImport, dest, specReg, 0));
                return dest;
            }
            case ImportMetaExpressionNode:
            {
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.ImportMeta, dest, 0, 0));
                return dest;
            }
            case ImportSourceExpressionNode importSource:
            {
                var specReg = CompileExpression(importSource.Specifier);
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.ImportSource, dest, specReg, 0));
                return dest;
            }
            case ImportDeferExpressionNode importDefer:
            {
                var specReg = CompileExpression(importDefer.Specifier);
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.ImportDefer, dest, specReg, 0));
                return dest;
            }
            case LogicalAssignmentExpressionNode logical:
                return CompileLogicalAssignment(logical);
            case AssignmentExpressionNode assign when assign.Left is IdentifierExpressionNode id:
            {
                // NamedEvaluation: `f = function(){}` names the function "f".
                var rightReg = CompileNamedInitializer(assign.Right, id.Name);
                var slot = GetOrCreateVariableSlot(id.Name);
                _instructions.Add(new Instruction(OpCode.StoreVar, rightReg, slot, 0));
                return rightReg;
            }
            case AssignmentExpressionNode assign when assign.Left is MemberExpressionNode member:
            {
                ThrowIfPrivateMemberAccess(member);
                var valueReg = CompileExpression(assign.Right);
                if (member.Object is SuperExpressionNode)
                {
                    var thisReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadThis, thisReg, 0, 0));
                    if (member.Computed)
                    {
                        var keyReg = CompileExpression(member.PropertyExpression!);
                        _instructions.Add(new Instruction(OpCode.SetElem, thisReg, keyReg, valueReg));
                    }
                    else
                    {
                        var nameIndex = GetOrCreatePropertyName(member.Property);
                        _instructions.Add(new Instruction(OpCode.SetPropByName, thisReg, nameIndex, valueReg));
                    }

                    return valueReg;
                }

                var objectReg = CompileExpression(member.Object);
                if (member.Computed)
                {
                    var keyReg = CompileExpression(member.PropertyExpression!);
                    _instructions.Add(new Instruction(OpCode.SetElem, objectReg, keyReg, valueReg));
                }
                else
                {
                    var nameIndex = GetOrCreatePropertyName(member.Property);
                    OpCode setOp;
                    if (IsPrivateMangled(member.Property))
                        setOp = _compilingClassConstructor ? OpCode.DefinePrivateField : OpCode.SetPrivateField;
                    else
                        setOp = OpCode.SetPropByName;
                    _instructions.Add(new Instruction(setOp, objectReg, nameIndex, valueReg));
                }

                return valueReg;
            }
            case AssignmentExpressionNode assign when assign.Left is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode:
            {
                // ECMA-262 13.15.5 DestructuringAssignmentEvaluation. An array/object
                // literal on the left of `=` is the assignment-pattern cover grammar
                // (e.g. `[a, b] = arr`, `({a} = obj)`). Reinterpret it as a binding
                // pattern and store into the EXISTING bindings (StoreVar), unlike a
                // declaration which initializes fresh bindings. The whole expression
                // evaluates to the right-hand-side value.
                if (TryConvertForAssignmentPattern(assign.Left, out var assignPattern) && assignPattern is not null)
                {
                    var rhsReg = CompileExpression(assign.Right);
                    EmitBindingPatternAssignment(assignPattern, rhsReg, OpCode.StoreVar);
                    return rhsReg;
                }

                // Targets the converter can't express as a binding pattern (e.g. a
                // member-expression element like `[o.x] = v`) fall through to the
                // invalid-target behavior below.
                _ = CompileExpression(assign.Left);
                EmitRuntimeReferenceError("Invalid left-hand side in assignment.");
                return LoadUndefinedConstant();
            }
            case AssignmentExpressionNode assign:
            {
                // Annex B web-compat runtime error behavior for non-reference
                // assignment targets (for example CallExpression left-hand sides):
                // evaluate left side effects, then throw ReferenceError.
                _ = CompileExpression(assign.Left);
                EmitRuntimeReferenceError("Invalid left-hand side in assignment.");
                return LoadUndefinedConstant();
            }
            case MemberExpressionNode member:
            {
                ThrowIfPrivateMemberAccess(member);
                // H.3 - super.foo lowers to LoadSuperProperty; the runtime reads
                // the executing frame's function's HomeObject prototype chain.
                if (member.Object is SuperExpressionNode && !member.Computed)
                {
                    var dest2 = AllocateRegister();
                    var nameIdx = GetOrCreatePropertyName(member.Property);
                    _instructions.Add(new Instruction(OpCode.LoadSuperProperty, dest2, nameIdx, 0));
                    return dest2;
                }

                if (member.Object is SuperExpressionNode && member.Computed)
                {
                    var keyReg = CompileExpression(member.PropertyExpression!);
                    var dest2 = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadSuperElement, dest2, keyReg, 0));
                    return dest2;
                }

                var objectReg = CompileExpression(member.Object);
                var dest = AllocateRegister();
                if (member.Computed)
                {
                    var keyReg = CompileExpression(member.PropertyExpression!);
                    _instructions.Add(new Instruction(OpCode.GetElem, dest, objectReg, keyReg));
                }
                else
                {
                    var nameIndex = GetOrCreatePropertyName(member.Property);
                    var op = IsPrivateMangled(member.Property) ? OpCode.GetPrivateField : OpCode.GetPropByName;
                    _instructions.Add(new Instruction(op, dest, objectReg, nameIndex));
                }

                return dest;
            }
            case OptionalMemberExpressionNode optionalMember:
            {
                var objectReg = CompileExpression(optionalMember.Object);
                var dest = AllocateRegister();
                var undefinedConst = AddConstant(JsValue.Undefined);
                _instructions.Add(new Instruction(OpCode.LoadConst, dest, undefinedConst, 0));

                var nullConstReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, nullConstReg, AddConstant(JsValue.Null), 0));
                var undefConstReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, undefConstReg, AddConstant(JsValue.Undefined), 0));

                var nullEqReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, nullEqReg, objectReg, nullConstReg));
                var jumpIfNotNull = EmitPlaceholder(OpCode.JumpIfFalse, nullEqReg);
                var jumpEndFromNull = EmitPlaceholder(OpCode.Jump);

                var checkUndefinedLabel = _instructions.Count;
                PatchJump(jumpIfNotNull, checkUndefinedLabel);
                var undefEqReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, undefEqReg, objectReg, undefConstReg));
                var jumpIfNotUndefined = EmitPlaceholder(OpCode.JumpIfFalse, undefEqReg);
                var jumpEndFromUndefined = EmitPlaceholder(OpCode.Jump);

                var evalPropertyLabel = _instructions.Count;
                PatchJump(jumpIfNotUndefined, evalPropertyLabel);
                var memberValueReg = AllocateRegister();
                if (optionalMember.Computed)
                {
                    var keyReg = CompileExpression(optionalMember.PropertyExpression!);
                    _instructions.Add(new Instruction(OpCode.GetElem, memberValueReg, objectReg, keyReg));
                }
                else
                {
                    var nameIndex = GetOrCreatePropertyName(optionalMember.Property);
                    _instructions.Add(new Instruction(OpCode.GetPropByName, memberValueReg, objectReg, nameIndex));
                }

                _instructions.Add(new Instruction(OpCode.Move, dest, memberValueReg, 0));
                var endLabel = _instructions.Count;
                PatchJump(jumpEndFromNull, endLabel);
                PatchJump(jumpEndFromUndefined, endLabel);
                return dest;
            }
            case CallExpressionNode call:
            {
                var calleeReg = -1;
                var thisReg = 0;
                var isMethodCall = false;
                var isDirectEvalCall = false;

                // H.3.2 - super(args) call. The base constructor is loaded via
                // LoadSuperConstructor; we pass the current frame's `this` as the
                // receiver so base-class field initialisation (this.x = ...)
                // surfaces on the derived instance.
                if (call.Callee is SuperExpressionNode)
                {
                    calleeReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadSuperConstructor, calleeReg, 0, 0));
                    thisReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadThis, thisReg, 0, 0));
                    isMethodCall = true;
                }
                else if (call.Callee is MemberExpressionNode memberCallee)
                {
                    ThrowIfPrivateMemberAccess(memberCallee);
                    // H.3 - super.method(args): callee comes from LoadSuperProperty,
                    // thisValue stays the current frame's `this`. Without this branch
                    // memberCallee.Object would compile as the (invalid) super value.
                    if (memberCallee.Object is SuperExpressionNode && !memberCallee.Computed)
                    {
                        thisReg = AllocateRegister();
                        _instructions.Add(new Instruction(OpCode.LoadThis, thisReg, 0, 0));
                        calleeReg = AllocateRegister();
                        var superName = GetOrCreatePropertyName(memberCallee.Property);
                        _instructions.Add(new Instruction(OpCode.LoadSuperProperty, calleeReg, superName, 0));
                        isMethodCall = true;
                    }
                    else if (memberCallee.Object is SuperExpressionNode && memberCallee.Computed)
                    {
                        var keyReg = CompileExpression(memberCallee.PropertyExpression!);
                        thisReg = AllocateRegister();
                        _instructions.Add(new Instruction(OpCode.LoadThis, thisReg, 0, 0));
                        calleeReg = AllocateRegister();
                        _instructions.Add(new Instruction(OpCode.LoadSuperElement, calleeReg, keyReg, 0));
                        isMethodCall = true;
                    }
                    else
                    {
                        thisReg = CompileExpression(memberCallee.Object);
                        calleeReg = AllocateRegister();
                        if (memberCallee.Computed)
                        {
                            var keyReg = CompileExpression(memberCallee.PropertyExpression!);
                            _instructions.Add(new Instruction(OpCode.GetElem, calleeReg, thisReg, keyReg));
                        }
                        else
                        {
                            var nameIndex = GetOrCreatePropertyName(memberCallee.Property);
                            _instructions.Add(new Instruction(OpCode.GetPropByName, calleeReg, thisReg, nameIndex));
                        }

                        isMethodCall = true;
                    }
                }
                else if (call.Callee is OptionalMemberExpressionNode optionalMemberCallee)
                {
                    thisReg = CompileExpression(optionalMemberCallee.Object);
                    var optionalDest = AllocateRegister();
                    var undefinedConst = AddConstant(JsValue.Undefined);
                    _instructions.Add(new Instruction(OpCode.LoadConst, optionalDest, undefinedConst, 0));

                    var nullConstReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadConst, nullConstReg, AddConstant(JsValue.Null), 0));
                    var undefConstReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadConst, undefConstReg, AddConstant(JsValue.Undefined), 0));

                    var nullEqReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.StrictEq, nullEqReg, thisReg, nullConstReg));
                    var jumpIfNotNull = EmitPlaceholder(OpCode.JumpIfFalse, nullEqReg);
                    var jumpEndFromNull = EmitPlaceholder(OpCode.Jump);

                    var checkUndefinedLabel = _instructions.Count;
                    PatchJump(jumpIfNotNull, checkUndefinedLabel);
                    var undefEqReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.StrictEq, undefEqReg, thisReg, undefConstReg));
                    var jumpIfNotUndefined = EmitPlaceholder(OpCode.JumpIfFalse, undefEqReg);
                    var jumpEndFromUndefined = EmitPlaceholder(OpCode.Jump);

                    var callLabel = _instructions.Count;
                    PatchJump(jumpIfNotUndefined, callLabel);
                    calleeReg = AllocateRegister();
                    if (optionalMemberCallee.Computed)
                    {
                        var keyReg = CompileExpression(optionalMemberCallee.PropertyExpression!);
                        _instructions.Add(new Instruction(OpCode.GetElem, calleeReg, thisReg, keyReg));
                    }
                    else
                    {
                        var nameIndex = GetOrCreatePropertyName(optionalMemberCallee.Property);
                        _instructions.Add(new Instruction(OpCode.GetPropByName, calleeReg, thisReg, nameIndex));
                    }

                    isMethodCall = true;
                    var hasSpreadOptional = false;
                    for (var i = 0; i < call.Arguments.Count; i++)
                    {
                        if (call.Arguments[i] is SpreadElementExpressionNode) { hasSpreadOptional = true; break; }
                    }

                    if (hasSpreadOptional)
                    {
                        var spreadArgReg = BuildSpreadArray(call.Arguments);
                        _instructions.Add(new Instruction(OpCode.CallSpread, optionalDest, calleeReg, spreadArgReg, thisReg));
                    }
                    else
                    {
                        switch (call.Arguments.Count)
                        {
                            case 0:
                                _instructions.Add(new Instruction(OpCode.CallMethod0, optionalDest, calleeReg, thisReg));
                                break;
                            case 1:
                            {
                                var arg0 = CompileExpression(call.Arguments[0]);
                                _instructions.Add(new Instruction(OpCode.CallMethod1, optionalDest, calleeReg, thisReg, arg0));
                                break;
                            }
                            default:
                            {
                                var argStart = AllocateRegister();
                                for (var i = 0; i < call.Arguments.Count; i++)
                                {
                                    var argReg = CompileExpression(call.Arguments[i]);
                                    _instructions.Add(new Instruction(OpCode.Move, argStart + i, argReg, 0));
                                    if (i + 1 < call.Arguments.Count)
                                    {
                                        _ = AllocateRegister();
                                    }
                                }

                                _instructions.Add(new Instruction(OpCode.CallMethodN, optionalDest, calleeReg, thisReg, argStart, call.Arguments.Count));
                                break;
                            }
                        }
                    }

                    var endLabel = _instructions.Count;
                    PatchJump(jumpEndFromNull, endLabel);
                    PatchJump(jumpEndFromUndefined, endLabel);
                    return optionalDest;
                }
                else
                {
                    calleeReg = CompileExpression(call.Callee);
                    isDirectEvalCall = IsDirectEvalCallCallee(call.Callee);
                }

                var isSuperCall = call.Callee is SuperExpressionNode;
                var dest = AllocateRegister();

                // ECMA-262 13.3.7.1 — any spread argument (...args), in any position,
                // builds a fully-expanded argument array (iterator protocol) and
                // dispatches via CallSpread.
                var hasSpread = false;
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    if (call.Arguments[i] is SpreadElementExpressionNode)
                    {
                        hasSpread = true;
                        break;
                    }
                }

                if (hasSpread)
                {
                    var spreadArg = BuildSpreadArray(call.Arguments);
                    _instructions.Add(new Instruction(
                        OpCode.CallSpread,
                        dest,
                        calleeReg,
                        spreadArg,
                        thisReg,
                        !isMethodCall && isDirectEvalCall ? 1 : 0));
                    if (isSuperCall) _instructions.Add(new Instruction(OpCode.InitThisBinding, 0, 0, 0));
                    return dest;
                }

                switch (call.Arguments.Count)
                {
                    case 0:
                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethod0, dest, calleeReg, thisReg)
                            : new Instruction(OpCode.Call0, dest, calleeReg, 0, 0, !isMethodCall && isDirectEvalCall ? 1 : 0));
                        if (isSuperCall) _instructions.Add(new Instruction(OpCode.InitThisBinding, 0, 0, 0));
                        return dest;
                    case 1:
                    {
                        var arg0 = CompileExpression(call.Arguments[0]);
                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethod1, dest, calleeReg, thisReg, arg0)
                            : new Instruction(OpCode.Call1, dest, calleeReg, arg0, 0, !isMethodCall && isDirectEvalCall ? 1 : 0));
                        if (isSuperCall) _instructions.Add(new Instruction(OpCode.InitThisBinding, 0, 0, 0));
                        return dest;
                    }
                    default:
                    {
                        var argStart = AllocateRegister();
                        for (var i = 0; i < call.Arguments.Count; i++)
                        {
                            var argReg = CompileExpression(call.Arguments[i]);
                            _instructions.Add(new Instruction(OpCode.Move, argStart + i, argReg, 0));
                            if (i + 1 < call.Arguments.Count)
                            {
                                _ = AllocateRegister();
                            }
                        }

                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethodN, dest, calleeReg, thisReg, argStart, call.Arguments.Count)
                            : new Instruction(OpCode.CallN, dest, calleeReg, argStart, call.Arguments.Count, !isMethodCall && isDirectEvalCall ? 1 : 0));
                        if (isSuperCall) _instructions.Add(new Instruction(OpCode.InitThisBinding, 0, 0, 0));
                        return dest;
                    }
                }
            }
            case OptionalCallExpressionNode optionalCall:
            {
                var calleeReg = CompileExpression(optionalCall.Callee);
                var dest = AllocateRegister();
                var undefinedConst = AddConstant(JsValue.Undefined);
                _instructions.Add(new Instruction(OpCode.LoadConst, dest, undefinedConst, 0));

                var nullConstReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, nullConstReg, AddConstant(JsValue.Null), 0));
                var undefConstReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, undefConstReg, AddConstant(JsValue.Undefined), 0));

                var nullEqReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, nullEqReg, calleeReg, nullConstReg));
                var jumpIfNotNull = EmitPlaceholder(OpCode.JumpIfFalse, nullEqReg);
                var jumpEndFromNull = EmitPlaceholder(OpCode.Jump);

                var checkUndefinedLabel = _instructions.Count;
                PatchJump(jumpIfNotNull, checkUndefinedLabel);
                var undefEqReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, undefEqReg, calleeReg, undefConstReg));
                var jumpIfNotUndefined = EmitPlaceholder(OpCode.JumpIfFalse, undefEqReg);
                var jumpEndFromUndefined = EmitPlaceholder(OpCode.Jump);

                var callLabel = _instructions.Count;
                PatchJump(jumpIfNotUndefined, callLabel);
                var hasSpread = false;
                for (var i = 0; i < optionalCall.Arguments.Count; i++)
                {
                    if (optionalCall.Arguments[i] is SpreadElementExpressionNode) { hasSpread = true; break; }
                }

                if (hasSpread)
                {
                    var spreadArgReg = BuildSpreadArray(optionalCall.Arguments);
                    _instructions.Add(new Instruction(OpCode.CallSpread, dest, calleeReg, spreadArgReg, 0));
                }
                else
                {
                    switch (optionalCall.Arguments.Count)
                    {
                        case 0:
                            _instructions.Add(new Instruction(OpCode.Call0, dest, calleeReg, 0));
                            break;
                        case 1:
                        {
                            var arg0 = CompileExpression(optionalCall.Arguments[0]);
                            _instructions.Add(new Instruction(OpCode.Call1, dest, calleeReg, arg0));
                            break;
                        }
                        default:
                        {
                            var argStart = AllocateRegister();
                            for (var i = 0; i < optionalCall.Arguments.Count; i++)
                            {
                                var argReg = CompileExpression(optionalCall.Arguments[i]);
                                _instructions.Add(new Instruction(OpCode.Move, argStart + i, argReg, 0));
                                if (i + 1 < optionalCall.Arguments.Count)
                                {
                                    _ = AllocateRegister();
                                }
                            }

                            _instructions.Add(new Instruction(OpCode.CallN, dest, calleeReg, argStart, optionalCall.Arguments.Count));
                            break;
                        }
                    }
                }

                var endLabel = _instructions.Count;
                PatchJump(jumpEndFromNull, endLabel);
                PatchJump(jumpEndFromUndefined, endLabel);
                return dest;
            }
            case UnaryExpressionNode unary:
            {
                // ECMA-262 15.5 â€” yield / yield*.
                if (unary.Operator == "yield" || unary.Operator == "yield*")
                {
                    var yieldDest = AllocateRegister();
                    var valueReg = CompileExpression(unary.Operand);
                    var yieldOp = unary.Operator == "yield*" ? OpCode.YieldStar : OpCode.Yield;
                    _instructions.Add(new Instruction(yieldOp, yieldDest, valueReg, 0));
                    return yieldDest;
                }

                if (unary.Operator == "await")
                {
                    if (_currentFunctionKind is not FunctionKind.Async and not FunctionKind.AsyncGenerator)
                    {
                        throw new UnsupportedFeatureException("await-outside-async", FeatureSupportLevel.ParserOnly, unary.Span);
                    }

                    var awaitDest = AllocateRegister();
                    var awaitValueReg = CompileExpression(unary.Operand);
                    _instructions.Add(new Instruction(OpCode.Await, awaitDest, awaitValueReg, 0));
                    return awaitDest;
                }

                if (unary.Operator == "delete")
                {
                    if (unary.Operand is IdentifierExpressionNode identifier)
                    {
                        var deleteDest = AllocateRegister();
                        var slot = GetOrCreateVariableSlot(identifier.Name);
                        _instructions.Add(new Instruction(OpCode.Delete, deleteDest, slot, 0));
                        return deleteDest;
                    }

                    if (unary.Operand is MemberExpressionNode member)
                    {
                        var objReg = CompileExpression(member.Object);
                        var deleteDest = AllocateRegister();
                        if (member.Computed)
                        {
                            var keyReg = CompileExpression(member.PropertyExpression!);
                            _instructions.Add(new Instruction(OpCode.DeleteElem, deleteDest, objReg, keyReg));
                        }
                        else
                        {
                            var nameIndex = GetOrCreatePropertyName(member.Property);
                            _instructions.Add(new Instruction(OpCode.DeletePropByName, deleteDest, objReg, nameIndex));
                        }

                        return deleteDest;
                    }

                    _ = CompileExpression(unary.Operand);
                    var defaultDeleteResult = AllocateRegister();
                    var ciDeleteTrue = AddConstant(JsValue.FromBoolean(true));
                    _instructions.Add(new Instruction(OpCode.LoadConst, defaultDeleteResult, ciDeleteTrue, 0));
                    return defaultDeleteResult;
                }

                if (unary.Operator == "typeof" && unary.Operand is IdentifierExpressionNode typeofIdentifier)
                {
                    var destTypeOf = AllocateRegister();
                    var slotTypeOf = GetOrCreateVariableSlot(typeofIdentifier.Name);
                    _instructions.Add(new Instruction(OpCode.TypeOfName, destTypeOf, slotTypeOf, 0));
                    return destTypeOf;
                }

                if (unary.Operator is "preIncrement" or "postIncrement" or "preDecrement" or "postDecrement")
                {
                    return CompileUpdateExpression(unary);
                }

                var operandReg = CompileExpression(unary.Operand);
                var dest = AllocateRegister();
                var op = unary.Operator switch
                {
                    "!" => OpCode.Not,
                    "+" => OpCode.Pos,
                    "-" => OpCode.Neg,
                    "~" => OpCode.BitNot,
                    "void" => OpCode.Void,
                    "typeof" => OpCode.TypeOf,
                    _ => throw new InvalidOperationException($"Unsupported unary operator {unary.Operator}.")
                };
                _instructions.Add(new Instruction(op, dest, operandReg, 0));
                return dest;
            }
            case ConditionalExpressionNode cond:
            {
                var testReg = CompileExpression(cond.Test);
                var dest = AllocateRegister();
                var jumpIfFalse = EmitPlaceholder(OpCode.JumpIfFalse, testReg);

                var consequentReg = CompileExpression(cond.Consequent);
                _instructions.Add(new Instruction(OpCode.Move, dest, consequentReg, 0));
                var jumpEnd = EmitPlaceholder(OpCode.Jump);

                PatchJump(jumpIfFalse, _instructions.Count);
                var alternateReg = CompileExpression(cond.Alternate);
                _instructions.Add(new Instruction(OpCode.Move, dest, alternateReg, 0));
                PatchJump(jumpEnd, _instructions.Count);
                return dest;
            }
            case FunctionExpressionNode fnExpr:
            {
                var nestedProgram = BuildFunctionProgramWithParameterBindings(
                    fnExpr.Body.Statements,
                    fnExpr.Body.Span,
                    fnExpr.Parameters,
                    fnExpr.ParameterBindings,
                    out var fnExprPrologueCount,
                    fnExpr.ParameterDefaults);
                // ECMA-262 NamedEvaluation: an anonymous function expression adopts
                // the binding/assignment name; a named expression keeps its own name.
                var fnExprName = fnExpr.Name ?? ConsumeNameHint();
                var childCompiler = new BytecodeCompiler();
                var nestedFunction = childCompiler.CompileProgramCore(
                    nestedProgram,
                    fnExpr.Parameters,
                    fnExpr.RestParameterIndex,
                    fnExprName,
                    hasOwnArgumentsObject: true,
                    hasSimpleParameterList: fnExpr.HasSimpleParameterList,
                    functionKind: SelectFunctionKind(fnExpr.IsAsync, fnExpr.IsGenerator, isArrow: false, isMethod: fnExpr.IsMethod),
                    inheritedStrictMode: _isStrictMode,
                    captureCompletionValue: false,
                    prologueStatementCount: fnExprPrologueCount,
                    parameterDefaults: fnExpr.ParameterDefaults,
                    // A named function expression binds its own name inside its body.
                    bindOwnNameInBody: fnExpr.Name is { Length: > 0 });
                AttachSource(nestedFunction, childCompiler, fnExpr.Span);
                var nestedIndex = _nestedFunctions.Count;
                _nestedFunctions.Add(nestedFunction);
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
                return dest;
            }
            case ClassExpressionNode classExpr:
                // ECMA-262 NamedEvaluation: an anonymous class expression adopts the
                // pending binding name (`let C = class {}` / `[c = class {}]`).
                return CompileClassExpressionToRegister(classExpr.Name ?? ConsumeNameHint(), classExpr.BaseClass, classExpr.Members);
            case ArrowFunctionExpressionNode arrow:
            {
                IReadOnlyList<StatementNode> statements;
                SourceSpan bodySpan;
                if (arrow.BlockBody is not null)
                {
                    statements = arrow.BlockBody.Statements;
                    bodySpan = arrow.BlockBody.Span;
                }
                else if (arrow.ExpressionBody is not null)
                {
                    statements = new[] { new ReturnStatementNode(arrow.ExpressionBody, arrow.ExpressionBody.Span) };
                    bodySpan = arrow.ExpressionBody.Span;
                }
                else
                {
                    statements = Array.Empty<StatementNode>();
                    bodySpan = arrow.Span;
                }

                var nestedProgram = BuildFunctionProgramWithParameterBindings(
                    statements,
                    bodySpan,
                    arrow.Parameters,
                    arrow.ParameterBindings,
                    out var arrowPrologueCount,
                    arrow.ParameterDefaults);
                // ECMA-262 NamedEvaluation: arrows are always anonymous, so they take
                // the binding/assignment name when one is in scope, else the empty name.
                var arrowName = ConsumeNameHint() ?? string.Empty;
                var childCompiler = new BytecodeCompiler();
                var nestedFunction = childCompiler.CompileProgramCore(
                    nestedProgram,
                    arrow.Parameters,
                    arrow.RestParameterIndex,
                    arrowName,
                    hasOwnArgumentsObject: false,
                    hasSimpleParameterList: arrow.HasSimpleParameterList,
                    functionKind: SelectFunctionKind(arrow.IsAsync, isGenerator: false, isArrow: true),
                    inheritedStrictMode: _isStrictMode,
                    captureCompletionValue: false,
                    prologueStatementCount: arrowPrologueCount,
                    parameterDefaults: arrow.ParameterDefaults);
                AttachSource(nestedFunction, childCompiler, arrow.Span);
                var nestedIndex = _nestedFunctions.Count;
                _nestedFunctions.Add(nestedFunction);
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
                return dest;
            }
            case NewExpressionNode ne:
            {
                var calleeReg = CompileExpression(ne.Callee);
                var dest = AllocateRegister();

                // ECMA-262 13.3.5.1 — any spread argument (new F(...args), in any
                // position) builds a fully-expanded argument array (iterator
                // protocol) and dispatches via ConstructSpread.
                var newHasSpread = false;
                for (var i = 0; i < ne.Arguments.Count; i++)
                {
                    if (ne.Arguments[i] is SpreadElementExpressionNode) { newHasSpread = true; break; }
                }

                if (newHasSpread)
                {
                    var spreadArg = BuildSpreadArray(ne.Arguments);
                    _instructions.Add(new Instruction(OpCode.ConstructSpread, dest, calleeReg, spreadArg));
                    return dest;
                }

                switch (ne.Arguments.Count)
                {
                    case 0:
                        _instructions.Add(new Instruction(OpCode.Construct0, dest, calleeReg, 0));
                        return dest;
                    case 1:
                    {
                        var arg0 = CompileExpression(ne.Arguments[0]);
                        _instructions.Add(new Instruction(OpCode.Construct1, dest, calleeReg, arg0));
                        return dest;
                    }
                    default:
                    {
                        var argStart = AllocateRegister();
                        for (var i = 0; i < ne.Arguments.Count; i++)
                        {
                            var argReg = CompileExpression(ne.Arguments[i]);
                            _instructions.Add(new Instruction(OpCode.Move, argStart + i, argReg, 0));
                            if (i + 1 < ne.Arguments.Count)
                            {
                                _ = AllocateRegister();
                            }
                        }

                        _instructions.Add(new Instruction(OpCode.ConstructN, dest, calleeReg, argStart, ne.Arguments.Count));
                        return dest;
                    }
                }
            }
            case RegexLiteralExpressionNode regex:
            {
                var dest = AllocateRegister();
                var rawTextIdx = AddConstant(JsValue.FromString(regex.RawText));
                _instructions.Add(new Instruction(OpCode.NewRegExp, dest, rawTextIdx, 0));
                return dest;
            }
            case ObjectLiteralExpressionNode obj:
            {
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.NewObject, dest, 0, 0));
                foreach (var prop in obj.Properties)
                {
                    // ECMA-262 13.2.5.5: object spread `{ ...src }`. Parsed as a
                    // data property with a null key whose value is a spread element.
                    // Copy own enumerable properties from the source into `dest`.
                    if (!prop.IsComputed && prop.Key is null && prop.Value is SpreadElementExpressionNode objectSpread)
                    {
                        var sourceReg = CompileExpression(objectSpread.Argument);
                        _instructions.Add(new Instruction(OpCode.CopyDataProperties, dest, sourceReg, 0));
                        continue;
                    }

                    // NamedEvaluation: a static-key data property `{ f: function(){} }`
                    // (and method shorthand `{ f(){} }`) names the function "f".
                    var valueReg = (!prop.IsComputed && prop.Kind == ObjectPropertyKind.Data && prop.Key is not null)
                        ? CompileNamedInitializer(prop.Value, prop.Key)
                        : CompileExpression(prop.Value);
                    if (prop.IsComputed)
                    {
                        if (prop.ComputedKey is null)
                        {
                            throw new InvalidOperationException("Computed object property key expression is required.");
                        }

                        var keyReg = CompileExpression(prop.ComputedKey);
                        // ECMA-262 10.2.9 SetFunctionName: for anonymous function
                        // definitions with computed keys (e.g. `{ [sym]: function(){} }`),
                        // stamp the `name` property from the computed key value.
                        if (prop.Kind == ObjectPropertyKind.Data && IsAnonymousFunctionDefinition(prop.Value))
                        {
                            _instructions.Add(new Instruction(OpCode.SetFunctionName, valueReg, keyReg, 0));
                        }
                        // Audit §1: object-literal accessors with computed keys
                        // emit DefineGetter/SetterByReg so the result installs
                        // as a real accessor descriptor, not a data property.
                        var computedOp = prop.Kind switch
                        {
                            ObjectPropertyKind.Getter => OpCode.DefineGetterByReg,
                            ObjectPropertyKind.Setter => OpCode.DefineSetterByReg,
                            _ => OpCode.SetElemDefine,
                        };
                        // D=1 marks an object-literal accessor as enumerable (class
                        // accessors leave D=0 and stay non-enumerable).
                        var computedEnum = prop.Kind is ObjectPropertyKind.Getter or ObjectPropertyKind.Setter ? 1 : 0;
                        _instructions.Add(new Instruction(computedOp, dest, keyReg, valueReg, computedEnum));
                    }
                    else
                    {
                        // Empty string is a valid property key per ECMA-262 6.1.7.
                        if (prop.Key is null)
                        {
                            throw new InvalidOperationException("Object property key is required.");
                        }

                        var nameIndex = GetOrCreatePropertyName(prop.Key);
                        // ECMA-262 B.3.1: non-computed __proto__ sets prototype
                        // directly via [[SetPrototypeOf]], bypassing the accessor.
                        OpCode namedOp;
                        int namedD = 0;
                        if (prop.Key == "__proto__" && prop.Kind == ObjectPropertyKind.Data)
                        {
                            namedOp = OpCode.SetPrototype;
                            namedD = 1; // literal __proto__: no-op for non-Object values
                        }
                        else
                            namedOp = prop.Kind switch
                            {
                                ObjectPropertyKind.Getter => OpCode.DefineGetter,
                                ObjectPropertyKind.Setter => OpCode.DefineSetter,
                                _ => OpCode.SetPropByName,
                            };
                        // D=1 marks an object-literal accessor as enumerable.
                        var namedEnum = prop.Kind is ObjectPropertyKind.Getter or ObjectPropertyKind.Setter ? 1 : 0;
                        if (namedOp == OpCode.SetPrototype)
                            _instructions.Add(new Instruction(namedOp, dest, valueReg, 0, namedD));
                        else
                            _instructions.Add(new Instruction(namedOp, dest, nameIndex, valueReg, namedD != 0 ? namedD : namedEnum));
                    }
                }

                return dest;
            }
            case ArrayLiteralExpressionNode arr:
            {
                // Any spread element means runtime expansion via the iterator
                // protocol; route through the shared builder.
                for (var i = 0; i < arr.Elements.Count; i++)
                {
                    if (arr.Elements[i] is SpreadElementExpressionNode)
                    {
                        return BuildSpreadArray(arr.Elements);
                    }
                }

                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.NewArray, dest, 0, 0));
                var hasSpread = false;
                for (var i = 0; i < arr.Elements.Count; i++)
                {
                    // Elisions are true holes: skip the store so the index stays absent
                    // (HasProperty false; iteration methods skip it). The final length is
                    // fixed up below so trailing holes still count.
                    if (arr.Elements[i] is ElisionExpressionNode)
                    {
                        continue;
                    }

                    if (arr.Elements[i] is SpreadElementExpressionNode)
                    {
                        hasSpread = true;
                    }

                    var valueReg = CompileExpression(arr.Elements[i]);
                    var indexReg = AllocateRegister();
                    var ci = AddConstant(JsValue.FromNumber(i));
                    _instructions.Add(new Instruction(OpCode.LoadConst, indexReg, ci, 0));
                    _instructions.Add(new Instruction(OpCode.SetElem, dest, indexReg, valueReg));
                }

                // Fix the length to the element count so trailing holes (e.g. `[1, , ]`)
                // are reflected. Skipped when the literal contains a spread, whose
                // element count is only known at runtime.
                if (!hasSpread && arr.Elements.Count > 0)
                {
                    var lenReg = AllocateRegister();
                    var lenConst = AddConstant(JsValue.FromNumber(arr.Elements.Count));
                    _instructions.Add(new Instruction(OpCode.LoadConst, lenReg, lenConst, 0));
                    var lenNameIdx = GetOrCreatePropertyName("length");
                    _instructions.Add(new Instruction(OpCode.SetPropByName, dest, lenNameIdx, lenReg));
                }

                return dest;
            }
            case BinaryExpressionNode bin:
            {
                if (bin.Operator == ",")
                {
                    _ = CompileExpression(bin.Left);
                    return CompileExpression(bin.Right);
                }

                if (bin.Operator == "&&")
                {
                    var andLeftReg = CompileExpression(bin.Left);
                    var andDest = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.Move, andDest, andLeftReg, 0));
                    var andJumpIfFalse = EmitPlaceholder(OpCode.JumpIfFalse, andLeftReg);
                    var andRightReg = CompileExpression(bin.Right);
                    _instructions.Add(new Instruction(OpCode.Move, andDest, andRightReg, 0));
                    PatchJump(andJumpIfFalse, _instructions.Count);
                    return andDest;
                }

                if (bin.Operator == "||")
                {
                    var orLeftReg = CompileExpression(bin.Left);
                    var orDest = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.Move, orDest, orLeftReg, 0));
                    var orJumpIfFalse = EmitPlaceholder(OpCode.JumpIfFalse, orLeftReg);
                    var orJumpEnd = EmitPlaceholder(OpCode.Jump);
                    PatchJump(orJumpIfFalse, _instructions.Count);
                    var orRightReg = CompileExpression(bin.Right);
                    _instructions.Add(new Instruction(OpCode.Move, orDest, orRightReg, 0));
                    PatchJump(orJumpEnd, _instructions.Count);
                    return orDest;
                }

                if (bin.Operator == "??")
                {
                    // ECMA-262 13.14 â€” nullish coalescing: both null and undefined are nullish
                    var nullishLeftReg = CompileExpression(bin.Left);
                    var nullishDest = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.Move, nullishDest, nullishLeftReg, 0));

                    var nullConstReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadConst, nullConstReg, AddConstant(JsValue.Null), 0));
                    var undefConstReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.LoadConst, undefConstReg, AddConstant(JsValue.Undefined), 0));

                    // left == null?
                    var nullEqReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.StrictEq, nullEqReg, nullishLeftReg, nullConstReg));
                    var jumpIfNotNull = EmitPlaceholder(OpCode.JumpIfFalse, nullEqReg);
                    var jumpToRightFromNull = EmitPlaceholder(OpCode.Jump);

                    // left == undefined?
                    var checkUndefLabel = _instructions.Count;
                    PatchJump(jumpIfNotNull, checkUndefLabel);
                    var undefEqReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.StrictEq, undefEqReg, nullishLeftReg, undefConstReg));
                    var jumpIfNotUndef = EmitPlaceholder(OpCode.JumpIfFalse, undefEqReg);

                    var rightEvalLabel = _instructions.Count;
                    PatchJump(jumpToRightFromNull, rightEvalLabel);
                    var nullishRightReg = CompileExpression(bin.Right);
                    _instructions.Add(new Instruction(OpCode.Move, nullishDest, nullishRightReg, 0));

                    PatchJump(jumpIfNotUndef, _instructions.Count);
                    return nullishDest;
                }

                var leftReg = CompileExpression(bin.Left);
                var rightReg = CompileExpression(bin.Right);
                var dest = AllocateRegister();
                var op = bin.Operator switch
                {
                    "+" => OpCode.Add,
                    "-" => OpCode.Sub,
                    "*" => OpCode.Mul,
                    "%" => OpCode.Mod,
                    "/" => OpCode.Div,
                    "**" => OpCode.Exp,
                    "&" => OpCode.BitAnd,
                    "|" => OpCode.BitOr,
                    "^" => OpCode.BitXor,
                    "<<" => OpCode.ShiftLeft,
                    ">>" => OpCode.ShiftRight,
                    ">>>" => OpCode.UnsignedShiftRight,
                    "==" => OpCode.Eq,
                    "!=" => OpCode.Neq,
                    "===" => OpCode.StrictEq,
                    "!==" => OpCode.StrictNeq,
                    "in" => OpCode.In,
                    "instanceof" => OpCode.InstanceOf,
                    "<" => OpCode.Lt,
                    ">" => OpCode.Gt,
                    "<=" => OpCode.Le,
                    ">=" => OpCode.Ge,
                    _ => throw new InvalidOperationException($"Unsupported binary operator {bin.Operator}.")
                };
                _instructions.Add(new Instruction(op, dest, leftReg, rightReg));
                return dest;
            }
            case ParenthesizedExpressionNode paren:
                return CompileExpression(paren.Expression);
            case SpreadElementExpressionNode spread:
                // ECMA-262 13.3.7.1 â€” compile the spread argument; the containing
                // CallExpressionNode emits CallSpread to unpack it.
                return CompileExpression(spread.Argument);
            default:
                throw new InvalidOperationException($"Unsupported expression type {expr.GetType().Name}.");
        }
    }

    // ECMA-262 Annex B.3.3: in sloppy-mode function/script/eval code, a
    // FunctionDeclaration nested inside a Block (or CaseBlock) also creates a
    // var-scoped binding in the VariableEnvironment, provided introducing
    // `var <name>` would not collide with an enclosing lexical declaration on the
    // path to the var scope (a catch parameter is exempt per B.3.5). This collects
    // the eligible names; the top-level statement list itself is not a nested
    // block, so its direct FunctionDeclarations are excluded (already var-scoped).
    private void CollectAnnexBFunctionNames(IReadOnlyList<StatementNode> body, HashSet<string> result)
    {
        var topLevelConflicts = new HashSet<string>(StringComparer.Ordinal);
        CollectLexicalConflictNames(body, topLevelConflicts);
        // A block-level function name that matches a formal parameter does not get
        // an Annex B var binding (the parameter already occupies the var scope).
        foreach (var p in _parameterNames)
        {
            topLevelConflicts.Add(p);
        }

        foreach (var stmt in body)
        {
            // Top-level FunctionDeclarations are ordinary var-scoped declarations,
            // not Annex B cases; only descend into nested statements.
            if (stmt is not FunctionDeclarationNode)
            {
                CollectAnnexBInStatement(stmt, topLevelConflicts, result);
            }
        }
    }

    // Recurse through a single statement looking for block-nested function
    // declarations. `conflicts` holds lexical names in scope between the current
    // position and the VariableEnvironment that would block an Annex B binding.
    private void CollectAnnexBInStatement(StatementNode stmt, HashSet<string> conflicts, HashSet<string> result)
    {
        switch (stmt)
        {
            case BlockStatementNode block:
                CollectAnnexBInBlockList(block.Statements, conflicts, result);
                break;
            case IfStatementNode ifStmt:
                // B.3.4: a FunctionDeclaration directly in an if/else position is
                // treated as if wrapped in a Block, so it is an Annex B candidate.
                CollectAnnexBInChild(ifStmt.Consequent, conflicts, result);
                if (ifStmt.Alternate is not null)
                {
                    CollectAnnexBInChild(ifStmt.Alternate, conflicts, result);
                }
                break;
            case SwitchStatementNode switchStmt:
                // A CaseBlock is a single lexical scope spanning all clauses.
                var merged = new List<StatementNode>();
                foreach (var clause in switchStmt.Cases)
                {
                    merged.AddRange(clause.Consequent);
                }
                CollectAnnexBInBlockList(merged, conflicts, result);
                break;
            case LabeledStatementNode labeled:
                // B.3.2: a labelled FunctionDeclaration is also an Annex B candidate.
                CollectAnnexBInChild(labeled.Body, conflicts, result);
                break;
            case WhileStatementNode whileStmt:
                CollectAnnexBInStatement(whileStmt.Body, conflicts, result);
                break;
            case DoWhileStatementNode doWhile:
                CollectAnnexBInStatement(doWhile.Body, conflicts, result);
                break;
            case WithStatementNode withStmt:
                CollectAnnexBInStatement(withStmt.Body, conflicts, result);
                break;
            case ForStatementNode forStmt:
                CollectAnnexBInLoop(forStmt.Initializer, forStmt.Body, conflicts, result);
                break;
            case ForInStatementNode forIn:
                CollectAnnexBInLoop(forIn.Initializer, forIn.Body, conflicts, result);
                break;
            case ForOfStatementNode forOf:
                CollectAnnexBInLoop(forOf.Initializer, forOf.Body, conflicts, result);
                break;
            case ForAwaitOfStatementNode forAwait:
                CollectAnnexBInLoop(forAwait.Initializer, forAwait.Body, conflicts, result);
                break;
            case TryCatchStatementNode tryCatch:
                CollectAnnexBInStatement(tryCatch.TryBlock, conflicts, result);
                // The catch parameter is exempt (B.3.5), so it is not added to conflicts.
                CollectAnnexBInStatement(tryCatch.CatchBlock, conflicts, result);
                break;
            case TryFinallyStatementNode tryFinally:
                CollectAnnexBInStatement(tryFinally.TryBlock, conflicts, result);
                CollectAnnexBInStatement(tryFinally.FinallyBlock, conflicts, result);
                break;
            case TryCatchFinallyStatementNode tryCatchFinally:
                CollectAnnexBInStatement(tryCatchFinally.TryBlock, conflicts, result);
                CollectAnnexBInStatement(tryCatchFinally.CatchBlock, conflicts, result);
                CollectAnnexBInStatement(tryCatchFinally.FinallyBlock, conflicts, result);
                break;
            default:
                // ExpressionStatement, return, bare FunctionDeclaration (B.3.4 — not
                // handled here), etc.: nothing to collect.
                break;
        }
    }

    private void CollectAnnexBInLoop(StatementNode? initializer, StatementNode body, HashSet<string> conflicts, HashSet<string> result)
    {
        // A `let`/`const` loop head introduces a lexical binding scoped to the body.
        if (initializer is VariableDeclarationStatementNode vd &&
            (string.Equals(vd.Kind, "let", StringComparison.Ordinal) ||
             string.Equals(vd.Kind, "const", StringComparison.Ordinal)))
        {
            var bodyConflicts = new HashSet<string>(conflicts, StringComparer.Ordinal);
            foreach (var d in vd.Declarators)
            {
                foreach (var n in GetDeclaratorBoundNames(d))
                {
                    bodyConflicts.Add(n);
                }
            }

            CollectAnnexBInStatement(body, bodyConflicts, result);
            return;
        }

        CollectAnnexBInStatement(body, conflicts, result);
    }

    // A statement in an if/else/labelled position: a bare FunctionDeclaration there
    // behaves (B.3.2 / B.3.4) as if wrapped in a Block, so it is an Annex B
    // candidate; otherwise recurse normally.
    private void CollectAnnexBInChild(StatementNode child, HashSet<string> conflicts, HashSet<string> result)
    {
        if (child is FunctionDeclarationNode fd && fd.Name.Length > 0)
        {
            if (!conflicts.Contains(fd.Name))
            {
                result.Add(fd.Name);
            }

            return;
        }

        CollectAnnexBInStatement(child, conflicts, result);
    }

    // Process the statement list of a Block/CaseBlock: its own lexical declarations
    // join the conflict set, then each direct FunctionDeclaration is an Annex B
    // candidate (added unless a conflict shadows it).
    private void CollectAnnexBInBlockList(IReadOnlyList<StatementNode> statements, HashSet<string> conflicts, HashSet<string> result)
    {
        var blockConflicts = new HashSet<string>(conflicts, StringComparer.Ordinal);
        CollectLexicalConflictNames(statements, blockConflicts);

        foreach (var stmt in statements)
        {
            if (stmt is FunctionDeclarationNode fd && fd.Name.Length > 0)
            {
                if (!blockConflicts.Contains(fd.Name))
                {
                    result.Add(fd.Name);
                }

                continue;
            }

            CollectAnnexBInStatement(stmt, blockConflicts, result);
        }
    }

    // Collect the lexically-declared names (let/const/class) directly in a
    // statement list — these would make an Annex B `var` binding an early error.
    private static void CollectLexicalConflictNames(IReadOnlyList<StatementNode> statements, HashSet<string> into)
    {
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case VariableDeclarationStatementNode vd when
                    string.Equals(vd.Kind, "let", StringComparison.Ordinal) ||
                    string.Equals(vd.Kind, "const", StringComparison.Ordinal):
                    foreach (var d in vd.Declarators)
                    {
                        foreach (var n in GetDeclaratorBoundNames(d))
                        {
                            into.Add(n);
                        }
                    }

                    break;
                case ClassDeclarationNode cd when cd.Name.Length > 0:
                    into.Add(cd.Name);
                    break;
                // ECMA-262 B.3.3.3: for-in, for-of, and for(;;) heads create lexical
                // bindings that conflict with Annex B function hoisting in the loop body.
                case ForInStatementNode forIn:
                    if (forIn.Initializer is VariableDeclarationStatementNode forInVar &&
                        (string.Equals(forInVar.Kind, "let", StringComparison.Ordinal) ||
                         string.Equals(forInVar.Kind, "const", StringComparison.Ordinal)))
                    {
                        foreach (var d in forInVar.Declarators)
                            foreach (var n in GetDeclaratorBoundNames(d))
                                into.Add(n);
                    }
                    break;
                case ForOfStatementNode forOf:
                    if (forOf.Initializer is VariableDeclarationStatementNode forOfVar &&
                        (string.Equals(forOfVar.Kind, "let", StringComparison.Ordinal) ||
                         string.Equals(forOfVar.Kind, "const", StringComparison.Ordinal)))
                    {
                        foreach (var d in forOfVar.Declarators)
                            foreach (var n in GetDeclaratorBoundNames(d))
                                into.Add(n);
                    }
                    break;
                case ForStatementNode forStmt:
                    if (forStmt.Initializer is VariableDeclarationStatementNode forVar &&
                        (string.Equals(forVar.Kind, "let", StringComparison.Ordinal) ||
                         string.Equals(forVar.Kind, "const", StringComparison.Ordinal)))
                    {
                        foreach (var d in forVar.Declarators)
                            foreach (var n in GetDeclaratorBoundNames(d))
                                into.Add(n);
                    }
                    break;
            }
        }
    }

    private static IReadOnlyList<string> GetDeclaratorBoundNames(VariableDeclaratorNode declarator)
    {
        if (declarator.BindingPattern is null)
        {
            return new[] { declarator.Identifier };
        }

        var names = new List<string>();
        CollectBoundNames(declarator.BindingPattern, names);
        return names;
    }

    private static void CollectBoundNames(BindingPatternNode pattern, List<string> names)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode identifier:
                names.Add(identifier.Name);
                break;
            case ArrayBindingPatternNode array:
                foreach (var element in array.Elements)
                {
                    if (element.Target is not null)
                    {
                        CollectBoundNames(element.Target, names);
                    }
                }

                break;
            case ObjectBindingPatternNode obj:
                foreach (var property in obj.Properties)
                {
                    CollectBoundNames(property.Target, names);
                }

                if (obj.Rest is not null)
                {
                    CollectBoundNames(obj.Rest, names);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported binding pattern type {pattern.GetType().Name}.");
        }
    }

    // Store an already-computed value into a (non-super) member-expression
    // reference, e.g. the `x.y` / `x[k]` left-hand side of `for (x.y of it)`.
    // The object (and computed key) are re-evaluated here so the reference is
    // resolved per ECMA-262 13.7.5.5 each iteration.
    private void EmitMemberStore(MemberExpressionNode member, int valueReg)
    {
        ThrowIfPrivateMemberAccess(member);
        var objectReg = CompileExpression(member.Object);
        if (member.Computed)
        {
            var keyReg = CompileExpression(member.PropertyExpression!);
            _instructions.Add(new Instruction(OpCode.SetElem, objectReg, keyReg, valueReg));
            return;
        }

        var nameIndex = GetOrCreatePropertyName(member.Property);
        var setOp = IsPrivateMangled(member.Property)
            ? (_compilingClassConstructor ? OpCode.DefinePrivateField : OpCode.SetPrivateField)
            : OpCode.SetPropByName;
        _instructions.Add(new Instruction(setOp, objectReg, nameIndex, valueReg));
    }

    private void EmitBindingPatternAssignment(BindingPatternNode pattern, int sourceReg, OpCode storeOp)
    {
        switch (pattern)
        {
            case IdentifierBindingPatternNode identifier:
            {
                var slot = GetOrCreateVariableSlot(identifier.Name);
                _instructions.Add(new Instruction(storeOp, sourceReg, slot, 0));
                return;
            }
            case MemberBindingPatternNode memberTarget:
            {
                // `[o.x] = v` etc. — store into the member reference. Only reached
                // for assignment patterns (declarations never carry member targets).
                EmitMemberStore(memberTarget.Member, sourceReg);
                return;
            }
            case ArrayBindingPatternNode array:
            {
                EmitArrayPatternIteratorAssignment(array, sourceReg, storeOp);
                return;
            }
            case ObjectBindingPatternNode obj:
            {
                // Object binding patterns require object-coercible input even
                // when they have no properties.
                var nullReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, nullReg, AddConstant(JsValue.Null), 0));
                var undefReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, undefReg, AddConstant(JsValue.Undefined), 0));

                var isNullReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, isNullReg, sourceReg, nullReg));
                var jumpIfNotNull = EmitPlaceholder(OpCode.JumpIfFalse, isNullReg);
                EmitRuntimeTypeError("Cannot destructure object from null.");

                PatchJump(jumpIfNotNull, _instructions.Count);
                var isUndefinedReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.StrictEq, isUndefinedReg, sourceReg, undefReg));
                var jumpIfNotUndefined = EmitPlaceholder(OpCode.JumpIfFalse, isUndefinedReg);
                EmitRuntimeTypeError("Cannot destructure object from undefined.");
                PatchJump(jumpIfNotUndefined, _instructions.Count);

                var excludedKeyRegs = new List<int>();
                foreach (var property in obj.Properties)
                {
                    int valueReg;
                    if (property.IsComputed)
                    {
                        var keyReg = CompileExpression(property.ComputedKey!);
                        valueReg = AllocateRegister();
                        _instructions.Add(new Instruction(OpCode.GetElem, valueReg, sourceReg, keyReg));
                        excludedKeyRegs.Add(keyReg);
                    }
                    else
                    {
                        if (property.Key is null)
                        {
                            throw new InvalidOperationException("Object binding property key cannot be null.");
                        }

                        var nameIndex = GetOrCreatePropertyName(property.Key);
                        valueReg = AllocateRegister();
                        _instructions.Add(new Instruction(OpCode.GetPropByName, valueReg, sourceReg, nameIndex));

                        var excludedKeyReg = AllocateRegister();
                        var keyConst = AddConstant(JsValue.FromString(property.Key));
                        _instructions.Add(new Instruction(OpCode.LoadConst, excludedKeyReg, keyConst, 0));
                        excludedKeyRegs.Add(excludedKeyReg);
                    }

                    if (property.Initializer is not null)
                    {
                        var propName = property.Target is IdentifierBindingPatternNode propId ? propId.Name : null;
                        valueReg = ApplyDefaultInitializerIfUndefined(valueReg, property.Initializer, propName);
                    }

                    EmitBindingPatternAssignment(property.Target, valueReg, storeOp);
                }

                if (obj.Rest is not null)
                {
                    var restReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.NewObject, restReg, 0, 0));

                    var iteratorReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.EnumerateKeys, iteratorReg, sourceReg, 0));
                    var loopStart = _instructions.Count;

                    var keyReg = AllocateRegister();
                    var nextIndex = _instructions.Count;
                    _instructions.Add(new Instruction(OpCode.ForInNext, keyReg, iteratorReg, -1));

                    var skipCopyJumps = new List<int>();
                    foreach (var excludedKeyReg in excludedKeyRegs)
                    {
                        var isExcludedReg = AllocateRegister();
                        _instructions.Add(new Instruction(OpCode.StrictEq, isExcludedReg, keyReg, excludedKeyReg));
                        var jumpIfFalse = EmitPlaceholder(OpCode.JumpIfFalse, isExcludedReg);
                        skipCopyJumps.Add(EmitPlaceholder(OpCode.Jump));
                        PatchJump(jumpIfFalse, _instructions.Count);
                    }

                    var valueReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.GetElem, valueReg, sourceReg, keyReg));
                    _instructions.Add(new Instruction(OpCode.SetElem, restReg, keyReg, valueReg));

                    var postCopy = _instructions.Count;
                    foreach (var skipCopyJump in skipCopyJumps)
                    {
                        PatchJump(skipCopyJump, postCopy);
                    }

                    _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
                    var loopEnd = _instructions.Count;
                    _instructions[nextIndex] = _instructions[nextIndex] with { C = loopEnd };

                    EmitBindingPatternAssignment(obj.Rest, restReg, storeOp);
                }

                return;
            }
            default:
                throw new InvalidOperationException($"Unsupported binding pattern type {pattern.GetType().Name}.");
        }
    }

    // ECMA-262 8.6.2 IteratorBindingInitialization / 13.15.5.5
    // IteratorDestructuringAssignmentEvaluation. Steps the RHS iterator lazily —
    // exactly one next() per element (none once exhausted; ForOfStepDone latches
    // Done), elisions step-and-discard, rest drains the remainder — and closes
    // the iterator per 7.4.11 IteratorClose: on the normal path when the pattern
    // finishes before exhaustion (loud — return() errors propagate), and on
    // abrupt completion quietly (the original exception wins).
    private void EmitArrayPatternIteratorAssignment(ArrayBindingPatternNode array, int sourceReg, OpCode storeOp)
    {
        var iterReg = AllocateRegister();
        // C=1: strict GetIterator — destructuring requires a real iterable.
        _instructions.Add(new Instruction(OpCode.EnumerateValues, iterReg, sourceReg, 1));

        var pushHandlerIndex = EmitPlaceholder(OpCode.PushHandler);

        var undefinedReg = LoadUndefinedConstant();
        foreach (var element in array.Elements)
        {
            if (element.IsRest && element.Target is not null)
            {
                var restReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.NewArray, restReg, 0, 0));
                var writeIndexReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, writeIndexReg, AddConstant(JsValue.FromNumber(0)), 0));
                var oneReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.LoadConst, oneReg, AddConstant(JsValue.FromNumber(1)), 0));

                var restValueReg = AllocateRegister();
                var loopStart = _instructions.Count;
                var restNextIndex = _instructions.Count;
                _instructions.Add(new Instruction(OpCode.ForOfNext, restValueReg, iterReg, -1));
                _instructions.Add(new Instruction(OpCode.SetElem, restReg, writeIndexReg, restValueReg));
                var bumpedReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.Add, bumpedReg, writeIndexReg, oneReg));
                _instructions.Add(new Instruction(OpCode.Move, writeIndexReg, bumpedReg, 0));
                _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
                _instructions[restNextIndex] = _instructions[restNextIndex] with { C = _instructions.Count };

                EmitBindingPatternAssignment(element.Target, restReg, storeOp);
                continue;
            }

            // Elision and plain elements both advance the iterator one step;
            // an exhausted iterator leaves the value as undefined (ForOfNext
            // jumps without writing the destination register).
            var valueReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.Move, valueReg, undefinedReg, 0));
            var nextIndex = _instructions.Count;
            _instructions.Add(new Instruction(OpCode.ForOfNext, valueReg, iterReg, -1));
            _instructions[nextIndex] = _instructions[nextIndex] with { C = _instructions.Count };

            if (element.Target is null)
            {
                continue;
            }

            if (element.Initializer is not null)
            {
                var elemName = element.Target is IdentifierBindingPatternNode elemId ? elemId.Name : null;
                valueReg = ApplyDefaultInitializerIfUndefined(valueReg, element.Initializer, elemName);
            }

            EmitBindingPatternAssignment(element.Target, valueReg, storeOp);
        }

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));
        // Normal completion: close (no-op if the iterator was exhausted).
        _instructions.Add(new Instruction(OpCode.IteratorClose, 0, iterReg, 0));
        var jumpPastHandler = EmitPlaceholder(OpCode.Jump);

        // Abrupt completion: the dispatched exception arrives in register 0.
        // Copy it out before closing (the close may run user code), close
        // quietly, and rethrow the original exception.
        PatchJump(pushHandlerIndex, _instructions.Count);
        var exceptionReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Move, exceptionReg, 0, 0));
        _instructions.Add(new Instruction(OpCode.IteratorClose, 0, iterReg, 1));
        _instructions.Add(new Instruction(OpCode.Throw, exceptionReg, 0, 0));

        PatchJump(jumpPastHandler, _instructions.Count);
    }

    // Build a fresh array from a mixed element list that contains at least one
    // spread, expanding each spread via the iterator protocol (SpreadAppend).
    // Used by array literals, call argument lists, and `new` argument lists so a
    // single mechanism handles `[a, ...b, c]`, `f(a, ...b)`, `new F(...a, b)`.
    private int BuildSpreadArray(IReadOnlyList<ExpressionNode> elements)
    {
        var arr = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.NewArray, arr, 0, 0));

        var idxReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.LoadConst, idxReg, AddConstant(JsValue.FromNumber(0)), 0));
        var oneReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.LoadConst, oneReg, AddConstant(JsValue.FromNumber(1)), 0));

        foreach (var element in elements)
        {
            if (element is ElisionExpressionNode)
            {
                // A hole still advances the index; the trailing-length fixup below
                // makes the absent slot count toward length.
                _instructions.Add(new Instruction(OpCode.Add, idxReg, idxReg, oneReg));
                continue;
            }

            if (element is SpreadElementExpressionNode spread)
            {
                var iterableReg = CompileExpression(spread.Argument);
                _instructions.Add(new Instruction(OpCode.SpreadAppend, arr, idxReg, iterableReg));
                continue;
            }

            var valueReg = CompileExpression(element);
            _instructions.Add(new Instruction(OpCode.SetElem, arr, idxReg, valueReg));
            _instructions.Add(new Instruction(OpCode.Add, idxReg, idxReg, oneReg));
        }

        // Pin length to the running index so trailing holes (e.g. `[...a, ,]`) count.
        var lenNameIdx = GetOrCreatePropertyName("length");
        _instructions.Add(new Instruction(OpCode.SetPropByName, arr, lenNameIdx, idxReg));
        return arr;
    }

    private int ApplyDefaultInitializerIfUndefined(int valueReg, ExpressionNode initializer, string? nameHint = null)
    {
        var undefinedReg = LoadUndefinedConstant();
        var isUndefinedReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.StrictEq, isUndefinedReg, valueReg, undefinedReg));
        var jumpIfNotUndefined = EmitPlaceholder(OpCode.JumpIfFalse, isUndefinedReg);

        // ECMA-262 NamedEvaluation: `[ f = function(){} ]` / `{ f = function(){} }`
        // names the default-valued anonymous function with the binding identifier.
        var initializerReg = nameHint is not null
            ? CompileNamedInitializer(initializer, nameHint)
            : CompileExpression(initializer);
        _instructions.Add(new Instruction(OpCode.Move, valueReg, initializerReg, 0));
        PatchJump(jumpIfNotUndefined, _instructions.Count);
        return valueReg;
    }

    private int AllocateRegister() => _nextRegister++;

    // ECMA-262: IfStatement, every IterationStatement, SwitchStatement,
    // WithStatement, TryStatement, and LabelledStatement evaluate to
    // `UpdateEmpty(result, undefined)` — i.e. they never produce an *empty*
    // completion; an empty inner result becomes `undefined`. The completion
    // value lives in register 0 (written by ExpressionStatement). Empty
    // producers (var/empty/declaration/block) correctly leave it untouched so
    // it inherits the prior statement's value, but these UpdateEmpty statements
    // must seed register 0 with `undefined` at entry so an empty body does not
    // leak the previous value. Only matters for the top-level script/eval body.
    private int _completionResetConstIndex = -1;

    private void EmitCompletionReset()
    {
        if (!_captureCompletionValue) return;
        if (_completionResetConstIndex < 0)
        {
            _completionResetConstIndex = AddConstant(JsValue.Undefined);
        }
        _instructions.Add(new Instruction(OpCode.LoadConst, 0, _completionResetConstIndex, 0));
    }

    private static FunctionKind SelectFunctionKind(bool isAsync, bool isGenerator, bool isArrow, bool isMethod = false)
    {
        if (isAsync && isGenerator)
        {
            return FunctionKind.AsyncGenerator;
        }

        if (isAsync)
        {
            return FunctionKind.Async;
        }

        if (isGenerator)
        {
            return FunctionKind.Generator;
        }

        if (isArrow)
        {
            return FunctionKind.Arrow;
        }

        // A concise method / accessor is an ordinary callable with no own
        // `prototype` and no [[Construct]] (ECMA-262 15.4 MethodDefinition).
        return isMethod ? FunctionKind.Method : FunctionKind.Ordinary;
    }

    private static bool IsDirectEvalCallCallee(ExpressionNode callee)
    {
        while (callee is ParenthesizedExpressionNode parenthesized)
        {
            callee = parenthesized.Expression;
        }

        return callee is IdentifierExpressionNode { Name: "eval" };
    }

    private static bool HasUseStrictDirective(IReadOnlyList<StatementNode> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is not ExpressionStatementNode expressionStatement ||
                expressionStatement.Expression is not StringLiteralExpressionNode stringLiteral)
            {
                break;
            }

            if (string.Equals(stringLiteral.Value, "use strict", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ThrowIfPrivateMemberAccess(MemberExpressionNode member)
    {
        if (!member.Computed && member.Property.StartsWith('#'))
        {
            throw new UnsupportedFeatureException("private-member-access", FeatureSupportLevel.ParserOnly, member.Span);
        }
    }

    private int CompileTemplateLiteral(TemplateLiteralExpressionNode template)
    {
        if (template.Quasis.Count != template.Expressions.Count + 1)
        {
            throw new InvalidOperationException("Template literal quasi/expression count mismatch.");
        }

        var currentReg = LoadStringConstant(template.Quasis[0]);
        for (var i = 0; i < template.Expressions.Count; i++)
        {
            var expressionReg = CompileExpression(template.Expressions[i]);
            // Template substitutions are coerced with ToString (string hint), not the
            // `+` operator's default hint, so an object's toString is preferred over
            // its valueOf. Coercing here also guarantees the subsequent Add operates
            // on two strings.
            var stringReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.ToStringCoerce, stringReg, expressionReg, 0));
            var combinedReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.Add, combinedReg, currentReg, stringReg));
            currentReg = combinedReg;

            if (template.Quasis[i + 1].Length > 0)
            {
                var quasiReg = LoadStringConstant(template.Quasis[i + 1]);
                var nextReg = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.Add, nextReg, currentReg, quasiReg));
                currentReg = nextReg;
            }
        }

        return currentReg;
    }

    // Tagged template literals lower to a normal call where the first argument is
    // a template object array and remaining arguments are substitution values.
    // We currently materialize a fresh template object per evaluation site.
    private int CompileTaggedTemplateExpression(TaggedTemplateExpressionNode tagged)
    {
        var calleeReg = -1;
        var thisReg = -1;
        var isMethodCall = false;

        if (tagged.Tag is MemberExpressionNode memberTag)
        {
            ThrowIfPrivateMemberAccess(memberTag);
            thisReg = CompileExpression(memberTag.Object);
            calleeReg = AllocateRegister();
            if (memberTag.Computed)
            {
                var keyReg = CompileExpression(memberTag.PropertyExpression!);
                _instructions.Add(new Instruction(OpCode.GetElem, calleeReg, thisReg, keyReg));
            }
            else
            {
                var nameIndex = GetOrCreatePropertyName(memberTag.Property);
                _instructions.Add(new Instruction(OpCode.GetPropByName, calleeReg, thisReg, nameIndex));
            }

            isMethodCall = true;
        }
        else
        {
            calleeReg = CompileExpression(tagged.Tag);
        }

        var argRegs = new List<int>(tagged.Template.Expressions.Count + 1)
        {
            CompileTemplateObject(tagged.Template)
        };

        foreach (var expression in tagged.Template.Expressions)
        {
            argRegs.Add(CompileExpression(expression));
        }

        var dest = AllocateRegister();
        switch (argRegs.Count)
        {
            case 1:
                _instructions.Add(isMethodCall
                    ? new Instruction(OpCode.CallMethod1, dest, calleeReg, thisReg, argRegs[0])
                    : new Instruction(OpCode.Call1, dest, calleeReg, argRegs[0]));
                return dest;
            default:
            {
                var argStart = AllocateRegister();
                for (var i = 0; i < argRegs.Count; i++)
                {
                    _instructions.Add(new Instruction(OpCode.Move, argStart + i, argRegs[i], 0));
                    if (i + 1 < argRegs.Count)
                    {
                        _ = AllocateRegister();
                    }
                }

                _instructions.Add(isMethodCall
                    ? new Instruction(OpCode.CallMethodN, dest, calleeReg, thisReg, argStart, argRegs.Count)
                    : new Instruction(OpCode.CallN, dest, calleeReg, argStart, argRegs.Count));
                return dest;
            }
        }
    }

    private int CompileTemplateObject(TemplateLiteralExpressionNode template)
    {
        var cookedArrayReg = CompileTemplateStringArray(template.Quasis);
        var rawArrayReg = CompileTemplateStringArray(template.Quasis);
        var rawNameIndex = GetOrCreatePropertyName("raw");
        _instructions.Add(new Instruction(OpCode.SetPropByName, cookedArrayReg, rawNameIndex, rawArrayReg));
        return cookedArrayReg;
    }

    private int CompileTemplateStringArray(IReadOnlyList<string> parts)
    {
        var arrayReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.NewArray, arrayReg, 0, 0));
        for (var i = 0; i < parts.Count; i++)
        {
            var indexReg = AllocateRegister();
            var indexConst = AddConstant(JsValue.FromNumber(i));
            _instructions.Add(new Instruction(OpCode.LoadConst, indexReg, indexConst, 0));
            var valueReg = LoadStringConstant(parts[i]);
            _instructions.Add(new Instruction(OpCode.SetElem, arrayReg, indexReg, valueReg));
        }

        return arrayReg;
    }

    private int LoadStringConstant(string value)
    {
        var reg = AllocateRegister();
        var ci = AddConstant(JsValue.FromString(value));
        _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
        return reg;
    }

    private void EmitRuntimeReferenceError(string message)
    {
        var referenceErrorReg = AllocateRegister();
        var referenceErrorSlot = GetOrCreateVariableSlot("ReferenceError");
        _instructions.Add(new Instruction(OpCode.LoadVar, referenceErrorReg, referenceErrorSlot, 0));

        var messageReg = LoadStringConstant(message);
        var errorObjectReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Call1, errorObjectReg, referenceErrorReg, messageReg));
        _instructions.Add(new Instruction(OpCode.Throw, errorObjectReg, 0, 0));
    }

    private void EmitRuntimeTypeError(string message)
    {
        var typeErrorReg = AllocateRegister();
        var typeErrorSlot = GetOrCreateVariableSlot("TypeError");
        _instructions.Add(new Instruction(OpCode.LoadVar, typeErrorReg, typeErrorSlot, 0));

        var messageReg = LoadStringConstant(message);
        var errorObjectReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Call1, errorObjectReg, typeErrorReg, messageReg));
        _instructions.Add(new Instruction(OpCode.Throw, errorObjectReg, 0, 0));
    }

    private int LoadUndefinedConstant()
    {
        var reg = AllocateRegister();
        var ci = AddConstant(JsValue.Undefined);
        _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
        return reg;
    }

    private int AddConstant(JsValue value)
    {
        _constants.Add(value);
        return _constants.Count - 1;
    }

    private int GetOrCreateVariableSlot(string name)
    {
        if (_variables.TryGetValue(name, out var slot))
        {
            return slot;
        }

        slot = _variables.Count;
        _variables[name] = slot;
        return slot;
    }

    private int GetOrCreatePropertyName(string name)
    {
        if (_propertyNameToIndex.TryGetValue(name, out var idx))
        {
            return idx;
        }

        idx = _propertyNames.Count;
        _propertyNames.Add(name);
        _propertyNameToIndex[name] = idx;
        return idx;
    }

    // H.5 â€” private names are mangled to __priv<N>__<name>. Detect so we can
    // emit the brand-checked GetPrivateField/SetPrivateField opcodes.
    private static bool IsPrivateMangled(string name) => name.StartsWith("__priv", StringComparison.Ordinal);

    private int EmitPlaceholder(OpCode opCode, int a = 0)
    {
        _instructions.Add(opCode switch
        {
            OpCode.Jump => new Instruction(opCode, -1, 0, 0),
            OpCode.JumpIfFalse => new Instruction(opCode, a, -1, 0),
            OpCode.PushHandler => new Instruction(opCode, -1, 0, 0),
            _ => throw new InvalidOperationException($"Unsupported placeholder opcode {opCode}.")
        });

        return _instructions.Count - 1;
    }

    private void CompileBreakStatement(string? label)
    {
        if (label == null)
        {
            if (_loopStack.Count == 0)
            {
                throw new JsParserException("'break' is only valid inside loops or switch statements.");
            }

            var target = _loopStack.Peek();
            EmitAbruptCompletion(target.ScopeDepthAtEntry, target.Seq, leaveTrailingScopes: true);
            var jump = EmitPlaceholder(OpCode.Jump);
            target.BreakJumpIndices.Add(jump);
            return;
        }

        foreach (var labelCtx in _labelStack)
        {
            if (labelCtx.Name == label)
            {
                EmitAbruptCompletion(labelCtx.ScopeDepthAtEntry, labelCtx.Seq, leaveTrailingScopes: true);
                var jump = EmitPlaceholder(OpCode.Jump);
                labelCtx.BreakJumpIndices.Add(jump);
                return;
            }
        }

        foreach (var ctx in _loopStack)
        {
            if (ctx.Label == label)
            {
                EmitAbruptCompletion(ctx.ScopeDepthAtEntry, ctx.Seq, leaveTrailingScopes: true);
                var jump = EmitPlaceholder(OpCode.Jump);
                ctx.BreakJumpIndices.Add(jump);
                return;
            }
        }

        throw new JsParserException($"Undefined label '{label}'.");
    }

    // Emit enough LeaveScope ops to bring the open-scope depth down to
    // `targetDepth`. Does NOT update _openScopeDepth because this is an
    // abrupt completion path — the surrounding block-statement compiler
    // is still tracking the depth and will emit its own LeaveScopes if
    // control flows through normally.
    private void EmitLeaveScopesForJump(int targetDepth)
    {
        var n = _openScopeDepth - targetDepth;
        for (var i = 0; i < n; i++)
        {
            _instructions.Add(new Instruction(OpCode.LeaveScope));
        }
    }

    // ECMA-262 14.15.3: an abrupt completion (break/continue/return) escaping one or
    // more `finally` blocks must run each of them, innermost-first, before reaching its
    // target. Emits an inline copy of every pending finally whose Seq > targetSeq,
    // interleaving the LeaveScope ops that tear down the scopes between each finally and
    // the next. For `break`/`continue`, pass the target loop/label's depth + Seq and
    // leaveTrailingScopes:true so remaining block scopes close before the jump. For
    // `return`, pass targetSeq:-1 (run all) and leaveTrailingScopes:false (the Return op
    // discards the frame). Pops frames as it emits them so a nested abrupt completion
    // inside a finally routes only through the still-outer finallies, then restores the
    // stack for the (possibly dead) code the surrounding compiler keeps emitting.
    private void EmitAbruptCompletion(int targetScopeDepth, int targetSeq, bool leaveTrailingScopes)
    {
        if (_finallyStack.Count == 0 || _finallyStack.Peek().Seq <= targetSeq)
        {
            if (leaveTrailingScopes) EmitLeaveScopesForJump(targetScopeDepth);
            return;
        }

        var savedFinally = _finallyStack.ToArray(); // index 0 = innermost (top of stack)
        var savedDepth = _openScopeDepth;
        // _openScopeDepth is decremented in lockstep with each emitted LeaveScope so the
        // inline-compiled finally body (and any abrupt completion nested inside it) sees
        // the true runtime scope depth, then restored for the surrounding compiler.
        while (_finallyStack.Count > 0 && _finallyStack.Peek().Seq > targetSeq)
        {
            var f = _finallyStack.Pop();
            while (_openScopeDepth > f.ScopeDepthAtEntry)
            {
                _instructions.Add(new Instruction(OpCode.LeaveScope));
                _openScopeDepth--;
            }
            CompileFinallyBlock(f.Block);
        }

        if (leaveTrailingScopes)
        {
            while (_openScopeDepth > targetScopeDepth)
            {
                _instructions.Add(new Instruction(OpCode.LeaveScope));
                _openScopeDepth--;
            }
        }

        _openScopeDepth = savedDepth;
        _finallyStack.Clear();
        for (var i = savedFinally.Length - 1; i >= 0; i--) _finallyStack.Push(savedFinally[i]);
    }

    private void CompileContinueStatement(string? label)
    {
        if (label == null)
        {
            // `continue` targets the nearest enclosing *iteration* statement.
            // A switch on top of the loop stack is not a valid target, so skip
            // switch contexts (they only patch BreakJumpIndices, never Continue).
            LoopContext? topCtx = null;
            foreach (var ctx in _loopStack)
            {
                if (!ctx.IsSwitch) { topCtx = ctx; break; }
            }
            if (topCtx == null)
            {
                throw new JsParserException("'continue' is only valid inside loops.");
            }

            EmitAbruptCompletion(topCtx.ScopeDepthAtEntry, topCtx.Seq, leaveTrailingScopes: true);
            var target = topCtx.ContinueTarget;
            if (target >= 0)
            {
                _instructions.Add(new Instruction(OpCode.Jump, target, 0, 0));
                return;
            }

            var jump = EmitPlaceholder(OpCode.Jump);
            topCtx.ContinueJumpIndices.Add(jump);
            return;
        }

        foreach (var ctx in _loopStack)
        {
            if (ctx.Label == label)
            {
                if (ctx.IsSwitch)
                {
                    throw new JsParserException($"Label '{label}' does not mark a loop.");
                }

                EmitAbruptCompletion(ctx.ScopeDepthAtEntry, ctx.Seq, leaveTrailingScopes: true);
                var target = ctx.ContinueTarget;
                if (target >= 0)
                {
                    _instructions.Add(new Instruction(OpCode.Jump, target, 0, 0));
                    return;
                }

                var jump = EmitPlaceholder(OpCode.Jump);
                ctx.ContinueJumpIndices.Add(jump);
                return;
            }
        }

        throw new JsParserException($"Undefined label '{label}'.");
    }

    private void PatchJump(int instructionIndex, int target)
    {
        var ins = _instructions[instructionIndex];
        _instructions[instructionIndex] = ins.OpCode switch
        {
            OpCode.Jump => ins with { A = target },
            OpCode.JumpIfFalse => ins with { B = target },
            OpCode.PushHandler => ins with { A = target },
            _ => throw new InvalidOperationException($"Cannot patch opcode {ins.OpCode} as jump.")
        };
    }

    private static System.Numerics.BigInteger ParseBigIntLiteral(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new JsParserException("Invalid BigInt literal.");
        }

        if (text.Length >= 2 && text[0] == '0')
        {
            switch (text[1])
            {
                case 'x' or 'X':
                    return ParseBigIntWithRadix(text[2..], 16);
                case 'o' or 'O':
                    return ParseBigIntWithRadix(text[2..], 8);
                case 'b' or 'B':
                    return ParseBigIntWithRadix(text[2..], 2);
            }
        }

        var normalized = text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (!System.Numerics.BigInteger.TryParse(
                normalized,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new JsParserException("Invalid BigInt literal.");
        }

        return value;
    }

    private static System.Numerics.BigInteger ParseBigIntWithRadix(string digits, int radix)
    {
        var result = System.Numerics.BigInteger.Zero;
        var sawDigit = false;
        foreach (var ch in digits)
        {
            if (ch == '_') continue;
            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'z' => ch - 'a' + 10,
                >= 'A' and <= 'Z' => ch - 'A' + 10,
                _ => -1
            };

            if (digit < 0 || digit >= radix)
            {
                throw new JsParserException("Invalid BigInt literal.");
            }

            result = result * radix + digit;
            sawDigit = true;
        }

        if (!sawDigit)
        {
            throw new JsParserException("Invalid BigInt literal.");
        }

        return result;
    }
}
