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
    }

    private sealed class LabelTarget
    {
        public required string Name { get; init; }
        public required List<int> BreakJumpIndices { get; init; }
    }

    private static int s_privateClassCounter;
    private static long s_brandCounter;

    // H.5: true while compiling the constructor body of a class with private fields.
    // Private field writes in this context emit DefinePrivateField instead of SetPrivateField.
    private bool _compilingClassConstructor;
    private bool _isDerivedConstructor;

    // Per-function brand tokens for private field access validation.
    private List<long> _brandTokens = new();

    private readonly List<Instruction> _instructions = new();
    private readonly List<JsValue> _constants = new();
    private readonly Dictionary<string, int> _variables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _varDeclarationNames = new(StringComparer.Ordinal);
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
    // Tracks block-scoped let/const names so they are not added to
    // _lexicalDeclarationNames/_constDeclarationNames. EnterScope creates
    // their bindings instead.
    private readonly Stack<HashSet<string>> _blockScopedNameStack = new();
    private string? _name;
    private int _nextRegister = 1;
    private FunctionKind _currentFunctionKind = FunctionKind.Ordinary;
    private bool _isStrictMode;
    // When non-null, the next LoopContext pushed should take this label.
    private string? _pendingLabel;

    public BytecodeFunction CompileScript(SourceText source)
    {
        if (source is not null && BytecodeCache.TryGet(source.Text, strictMode: false, out var cached))
        {
            return cached;
        }

        var program = JsParser.ParseScript(source!);
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
            functionKind: FunctionKind.Ordinary,
            inheritedStrictMode: inheritedStrictMode);
    }

    public BytecodeFunction CompileFunctionBody(SourceText body, IReadOnlyList<string> parameters, string? name)
    {
        var program = JsParser.ParseScript(body);
        return CompileProgramCore(
            program,
            parameters,
            restParameterIndex: -1,
            name,
            hasOwnArgumentsObject: true,
            functionKind: FunctionKind.Ordinary,
            inheritedStrictMode: false);
    }

    private BytecodeFunction CompileProgramCore(
        ProgramNode program,
        IReadOnlyList<string> parameters,
        int restParameterIndex,
        string? name,
        bool hasOwnArgumentsObject,
        FunctionKind functionKind,
        bool inheritedStrictMode)
    {
        _instructions.Clear();
        _constants.Clear();
        _variables.Clear();
        _varDeclarationNames.Clear();
        _lexicalDeclarationNames.Clear();
        _constDeclarationNames.Clear();
        _propertyNames.Clear();
        _propertyNameToIndex.Clear();
        _nestedFunctions.Clear();
        _parameterNames.Clear();
        _loopStack.Clear();
        _labelStack.Clear();
        _name = name;
        _nextRegister = 1;
        _currentFunctionKind = functionKind;
        _isStrictMode = inheritedStrictMode || program.Kind == ProgramKind.Module || HasUseStrictDirective(program.Body);
        foreach (var p in parameters)
        {
            _parameterNames.Add(p);
            _ = GetOrCreateVariableSlot(p);
        }

        HoistFunctionDeclarations(program.Body);

        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
        }

        _instructions.Add(new Instruction(OpCode.Return, 0, 0, 0));

        return new BytecodeFunction
        {
            Name = _name,
            Kind = _currentFunctionKind,
            IsStrictMode = _isStrictMode,
            IsDerivedConstructor = _isDerivedConstructor,
            Instructions = _instructions.ToArray(),
            Constants = _constants.ToArray(),
            VariableSlots = new Dictionary<string, int>(_variables),
            VarDeclarationNames = _varDeclarationNames.ToArray(),
            LexicalDeclarationNames = _lexicalDeclarationNames.ToArray(),
            ConstDeclarationNames = _constDeclarationNames.ToArray(),
            PropertyNames = _propertyNames.ToArray(),
            ParameterNames = _parameterNames.ToArray(),
            RestParameterIndex = restParameterIndex,
            HasOwnArgumentsObject = hasOwnArgumentsObject,
            NestedFunctions = _nestedFunctions.ToArray(),
            RegisterCount = Math.Max(2, _nextRegister),
            BrandTokens = _brandTokens.ToArray()
        };
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
                        var reg = CompileExpression(d.Initializer);
                        if (d.BindingPattern is null)
                        {
                            var slot = GetOrCreateVariableSlot(d.Identifier);
                            _instructions.Add(new Instruction(op, reg, slot, 0));
                        }
                        else
                        {
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
                _instructions.Add(new Instruction(OpCode.Move, 0, exprReg, 0));
                break;
            case IfStatementNode ifStmt:
                CompileIfStatement(ifStmt);
                break;
            case WhileStatementNode whileStmt:
                CompileWhileStatement(whileStmt);
                break;
            case DoWhileStatementNode doWhileStmt:
                CompileDoWhileStatement(doWhileStmt);
                break;
            case WithStatementNode withStmt:
                throw new UnsupportedFeatureException("with", FeatureSupportLevel.ParserOnly, withStmt.Span);
            case ForStatementNode forStmt:
                CompileForStatement(forStmt);
                break;
            case ForInStatementNode forInStmt:
                CompileForInStatement(forInStmt);
                break;
            case ForOfStatementNode forOfStmt:
                CompileForOfStatement(forOfStmt);
                break;
            case ForAwaitOfStatementNode forAwaitOfStmt:
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
            case ThrowStatementNode throwStmt:
                CompileThrowStatement(throwStmt);
                break;
            case TryCatchStatementNode tryCatchStmt:
                CompileTryCatchStatement(tryCatchStmt);
                break;
            case TryFinallyStatementNode tryFinallyStmt:
                CompileTryFinallyStatement(tryFinallyStmt);
                break;
            case TryCatchFinallyStatementNode tryCatchFinallyStmt:
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
                CompileSwitchStatement(switchStmt);
                break;
            case LabeledStatementNode labeledStmt:
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
            switch (stmt)
            {
                case FunctionDeclarationNode functionDecl:
                    CompileFunctionDeclaration(functionDecl);
                    break;
                case BlockStatementNode block:
                    HoistFunctionDeclarations(block.Statements);
                    break;
            }
        }
    }

    private void CompileFunctionDeclaration(FunctionDeclarationNode functionDecl)
    {
        var nestedProgram = BuildFunctionProgramWithParameterBindings(
            functionDecl.Body.Statements,
            functionDecl.Body.Span,
            functionDecl.Parameters,
            functionDecl.ParameterBindings);
        var childCompiler = new BytecodeCompiler();
        var nestedFunction = childCompiler.CompileProgramCore(
            nestedProgram,
            functionDecl.Parameters,
            functionDecl.RestParameterIndex,
            functionDecl.Name,
            hasOwnArgumentsObject: true,
            functionKind: SelectFunctionKind(functionDecl.IsAsync, functionDecl.IsGenerator, isArrow: false),
            inheritedStrictMode: _isStrictMode);
        var nestedIndex = _nestedFunctions.Count;
        _nestedFunctions.Add(nestedFunction);

        var dest = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
        var slot = GetOrCreateVariableSlot(functionDecl.Name);
        _varDeclarationNames.Add(functionDecl.Name);
        _instructions.Add(new Instruction(OpCode.StoreVar, dest, slot, 0));
    }

    private static ProgramNode BuildFunctionProgramWithParameterBindings(
        IReadOnlyList<StatementNode> bodyStatements,
        SourceSpan bodySpan,
        IReadOnlyList<string> parameterNames,
        IReadOnlyList<BindingPatternNode?>? parameterBindings)
    {
        if (parameterBindings is null || parameterBindings.Count == 0)
        {
            return new ProgramNode(ProgramKind.Script, bodyStatements, bodySpan);
        }

        var prelude = new List<StatementNode>();
        var count = Math.Min(parameterNames.Count, parameterBindings.Count);
        for (var i = 0; i < count; i++)
        {
            var bindingPattern = parameterBindings[i];
            if (bindingPattern is null)
            {
                continue;
            }

            var parameterName = parameterNames[i];
            var parameterRef = new IdentifierExpressionNode(parameterName, bindingPattern.Span);
            var declarator = new VariableDeclaratorNode(parameterName, parameterRef, bindingPattern.Span, bindingPattern);
            prelude.Add(new VariableDeclarationStatementNode("var", new[] { declarator }, bindingPattern.Span));
        }

        if (prelude.Count == 0)
        {
            return new ProgramNode(ProgramKind.Script, bodyStatements, bodySpan);
        }

        var statements = new List<StatementNode>(prelude.Count + bodyStatements.Count);
        statements.AddRange(prelude);
        statements.AddRange(bodyStatements);
        return new ProgramNode(ProgramKind.Script, statements, bodySpan);
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
    private int CompileClassExpressionToRegister(
        string? className,
        ExpressionNode? baseClass,
        IReadOnlyList<ClassMemberNode> members,
        Action<int>? bindNameBeforeStaticBlocks = null)
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
                        IsGenerator: fn.IsGenerator);
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
        _compilingClassConstructor = privateMangle.Count > 0;
        _isDerivedConstructor = isDerived;
        var classReg = CompileFunctionExpressionToRegister(constructorFn);
        _compilingClassConstructor = savedConstructorContext;
        _isDerivedConstructor = savedIsDerived;

        // Build prototype object.
        var protoReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.NewObject, protoReg, 0, 0));

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

            var methodReg = CompileFunctionExpressionToRegister(methodFn);

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
                        // Methods use SetElem for computed names
                        _instructions.Add(new Instruction(OpCode.SetElem, targetReg, keyReg, methodReg));
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
                        _instructions.Add(new Instruction(OpCode.SetPropByName, targetReg, nameIndex, methodReg));
                        break;
                }
            }
        }

        // proto.constructor = classCtor; classCtor.prototype = proto.
        var ctorNameIdx = GetOrCreatePropertyName("constructor");
        _instructions.Add(new Instruction(OpCode.SetPropByName, protoReg, ctorNameIdx, classReg));
        var protoNameIdx = GetOrCreatePropertyName("prototype");
        _instructions.Add(new Instruction(OpCode.SetPropByName, classReg, protoNameIdx, protoReg));

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
            var blockReg = CompileFunctionExpressionToRegister(blockFn);
            _instructions.Add(new Instruction(OpCode.SetHomeObject, blockReg, classReg, 0));
            var discard = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.CallMethod0, discard, blockReg, classReg));
        }

        return classReg;
    }

    private int CompileFunctionExpressionToRegister(FunctionExpressionNode fnExpr)
    {
        var nestedProgram = BuildFunctionProgramWithParameterBindings(
            fnExpr.Body.Statements,
            fnExpr.Body.Span,
            fnExpr.Parameters,
            fnExpr.ParameterBindings);
        var childCompiler = new BytecodeCompiler { _compilingClassConstructor = this._compilingClassConstructor, _isDerivedConstructor = this._isDerivedConstructor, _brandTokens = this._brandTokens };
        var nestedFunction = childCompiler.CompileProgramCore(
            nestedProgram,
            fnExpr.Parameters,
            fnExpr.RestParameterIndex,
            fnExpr.Name,
            hasOwnArgumentsObject: true,
            functionKind: SelectFunctionKind(fnExpr.IsAsync, fnExpr.IsGenerator, isArrow: false),
            inheritedStrictMode: _isStrictMode);
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

        CompileStatement(ifStmt.Consequent);

        if (ifStmt.Alternate is not null)
        {
            var jumpAfterConsequent = EmitPlaceholder(OpCode.Jump);
            PatchJump(jumpIfFalseIndex, _instructions.Count);
            CompileStatement(ifStmt.Alternate);
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
            BreakJumpIndices = new List<int>(),ContinueJumpIndices = new List<int>(),ScopeDepthAtEntry = _openScopeDepth};
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
        if (returnStmt.Argument is not null)
        {
            var reg = CompileExpression(returnStmt.Argument);
            _instructions.Add(new Instruction(OpCode.Move, 0, reg, 0));
        }

        _instructions.Add(new Instruction(OpCode.Return, 0, 0, 0));
    }

    private void CompileForStatement(ForStatementNode forStmt)
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
            ScopeDepthAtEntry = _openScopeDepth
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

    private void CompileForInStatement(ForInStatementNode forInStmt)
    {
        if (!TryGetForBindingTarget(forInStmt.Initializer, out var targetSlot, out var targetPattern))
        {
            if (forInStmt.Initializer is ExpressionStatementNode expressionInitializer)
            {
                _ = CompileExpression(expressionInitializer.Expression);
                EmitRuntimeReferenceError("Invalid left-hand side in for-in.");
                return;
            }

            throw new InvalidOperationException("Unsupported for-in initializer target.");
        }

        var sourceReg = CompileExpression(forInStmt.Iterable);
        var iteratorReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.EnumerateKeys, iteratorReg, sourceReg, 0));

        var loopStart = _instructions.Count;
        var keyReg = AllocateRegister();
        var nextIndex = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.ForInNext, keyReg, iteratorReg, -1));
        EmitForBindingAssignment(targetSlot, targetPattern, keyReg);

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),ContinueJumpIndices = new List<int>(),ScopeDepthAtEntry = _openScopeDepth};
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(forInStmt.Body);
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
    private void CompileForOfStatement(ForOfStatementNode forOfStmt)
    {
        if (!TryGetForBindingTarget(forOfStmt.Initializer, out var targetSlot, out var targetPattern))
        {
            if (forOfStmt.Initializer is ExpressionStatementNode expressionInitializer)
            {
                _ = CompileExpression(expressionInitializer.Expression);
                EmitRuntimeReferenceError("Invalid left-hand side in for-of.");
                return;
            }

            throw new InvalidOperationException("Unsupported for-of initializer target.");
        }

        var sourceReg = CompileExpression(forOfStmt.Iterable);
        var iteratorReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.EnumerateValues, iteratorReg, sourceReg, 0));

        var loopStart = _instructions.Count;
        var valueReg = AllocateRegister();
        var nextIndex = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.ForOfNext, valueReg, iteratorReg, -1));
        EmitForBindingAssignment(targetSlot, targetPattern, valueReg);

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),ContinueJumpIndices = new List<int>(),ScopeDepthAtEntry = _openScopeDepth};
        _loopStack.Push(ctx);
        if (_pendingLabel != null)
        {
            ctx.Label = _pendingLabel;
            _pendingLabel = null;
        }
        try
        {
            CompileStatement(forOfStmt.Body);
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
            BreakJumpIndices = new List<int>(),ContinueJumpIndices = new List<int>(),ScopeDepthAtEntry = _openScopeDepth};
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
            default:
                slot = 0;
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

    private void EmitForBindingAssignment(int slot, BindingPatternNode? pattern, int valueReg)
    {
        if (pattern is null)
        {
            _instructions.Add(new Instruction(OpCode.StoreVar, valueReg, slot, 0));
            return;
        }

        EmitBindingPatternAssignment(pattern, valueReg, OpCode.StoreVar);
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
        var catchSlot = GetOrCreateVariableSlot(tryCatchStmt.CatchIdentifier);
        _varDeclarationNames.Add(tryCatchStmt.CatchIdentifier);
        _instructions.Add(new Instruction(OpCode.StoreVar, 0, catchSlot, 0));
        CompileStatement(tryCatchStmt.CatchBlock);

        PatchJump(jumpAfterCatch, _instructions.Count);
    }
    private void CompileTryFinallyStatement(TryFinallyStatementNode stmt)
    {
        var pushHandler = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.PushHandler, -1, 0, 0, D: -1));

        CompileStatement(stmt.TryBlock);

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));

        var finallyStart = _instructions.Count;
        CompileStatement(stmt.FinallyBlock);
        _instructions.Add(new Instruction(OpCode.EndFinally, 0, 0, 0));

        var ins = _instructions[pushHandler];
        _instructions[pushHandler] = ins with { D = finallyStart };
    }

    private void CompileTryCatchFinallyStatement(TryCatchFinallyStatementNode stmt)
    {
        var pushHandler = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.PushHandler, -1, 0, 0, D: -1));

        CompileStatement(stmt.TryBlock);

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));
        var jumpPastCatch = EmitPlaceholder(OpCode.Jump);

        var catchEntry = _instructions.Count;
        var catchSlot = GetOrCreateVariableSlot(stmt.CatchIdentifier);
        _varDeclarationNames.Add(stmt.CatchIdentifier);

        var catchFinallyHandler = _instructions.Count;
        _instructions.Add(new Instruction(OpCode.PushHandler, -1, 0, 0, D: -1));

        _instructions.Add(new Instruction(OpCode.StoreVar, 0, catchSlot, 0));
        CompileStatement(stmt.CatchBlock);

        _instructions.Add(new Instruction(OpCode.PopHandler, 0, 0, 0));

        PatchJump(jumpPastCatch, _instructions.Count);

        var finallyStart = _instructions.Count;
        CompileStatement(stmt.FinallyBlock);
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
            ScopeDepthAtEntry = _openScopeDepth,
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
                BreakJumpIndices = new List<int>()
            };
            _labelStack.Push(target);
            CompileStatement(labeled.Body);
            var end = _instructions.Count;
            foreach (var jumpIdx in target.BreakJumpIndices)
            {
                PatchJump(jumpIdx, end);
            }
            _labelStack.Pop();
        }
    }

    // Returns true if the name is in any active block block-scoped name set.
    private bool IsBlockScopedName(string name)
    {
        foreach (var set in _blockScopedNameStack)
        {
            if (set.Contains(name)) return true;
        }
        return false;
    }

    private int CompileExpression(ExpressionNode expr)
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
            case AssignmentExpressionNode assign when assign.Left is IdentifierExpressionNode id:
            {
                var rightReg = CompileExpression(assign.Right);
                var slot = GetOrCreateVariableSlot(id.Name);
                _instructions.Add(new Instruction(OpCode.StoreVar, rightReg, slot, 0));
                return rightReg;
            }
            case AssignmentExpressionNode assign when assign.Left is MemberExpressionNode member:
            {
                ThrowIfPrivateMemberAccess(member);
                var objectReg = CompileExpression(member.Object);
                var valueReg = CompileExpression(assign.Right);
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
                    var hasSpreadOptional = call.Arguments.Count == 1 && call.Arguments[0] is SpreadElementExpressionNode;
                    if (hasSpreadOptional)
                    {
                        var spreadArgReg = CompileExpression(call.Arguments[0]);
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

                // ECMA-262 13.3.7.1 â€” handle spread arguments (...args) via CallSpread.
                var hasSpread = call.Arguments.Count == 1 && call.Arguments[0] is SpreadElementExpressionNode;
                if (hasSpread)
                {
                    var spreadArg = CompileExpression(call.Arguments[0]);
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
                var hasSpread = optionalCall.Arguments.Count == 1 && optionalCall.Arguments[0] is SpreadElementExpressionNode;
                if (hasSpread)
                {
                    var spreadArgReg = CompileExpression(optionalCall.Arguments[0]);
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
                    fnExpr.ParameterBindings);
                var childCompiler = new BytecodeCompiler();
                var nestedFunction = childCompiler.CompileProgramCore(
                    nestedProgram,
                    fnExpr.Parameters,
                    fnExpr.RestParameterIndex,
                    fnExpr.Name,
                    hasOwnArgumentsObject: true,
                    functionKind: SelectFunctionKind(fnExpr.IsAsync, fnExpr.IsGenerator, isArrow: false),
                    inheritedStrictMode: _isStrictMode);
                var nestedIndex = _nestedFunctions.Count;
                _nestedFunctions.Add(nestedFunction);
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
                return dest;
            }
            case ClassExpressionNode classExpr:
                return CompileClassExpressionToRegister(classExpr.Name, classExpr.BaseClass, classExpr.Members);
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
                    arrow.ParameterBindings);
                var childCompiler = new BytecodeCompiler();
                var nestedFunction = childCompiler.CompileProgramCore(
                    nestedProgram,
                    arrow.Parameters,
                    arrow.RestParameterIndex,
                    "<arrow>",
                    hasOwnArgumentsObject: false,
                    functionKind: SelectFunctionKind(arrow.IsAsync, isGenerator: false, isArrow: true),
                    inheritedStrictMode: _isStrictMode);
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
                    var valueReg = CompileExpression(prop.Value);
                    if (prop.IsComputed)
                    {
                        if (prop.ComputedKey is null)
                        {
                            throw new InvalidOperationException("Computed object property key expression is required.");
                        }

                        var keyReg = CompileExpression(prop.ComputedKey);
                        _instructions.Add(new Instruction(OpCode.SetElem, dest, keyReg, valueReg));
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(prop.Key))
                        {
                            throw new InvalidOperationException("Object property key is required.");
                        }

                        var nameIndex = GetOrCreatePropertyName(prop.Key);
                        _instructions.Add(new Instruction(OpCode.SetPropByName, dest, nameIndex, valueReg));
                    }
                }

                return dest;
            }
            case ArrayLiteralExpressionNode arr:
            {
                var dest = AllocateRegister();
                _instructions.Add(new Instruction(OpCode.NewArray, dest, 0, 0));
                for (var i = 0; i < arr.Elements.Count; i++)
                {
                    var valueReg = CompileExpression(arr.Elements[i]);
                    var indexReg = AllocateRegister();
                    var ci = AddConstant(JsValue.FromNumber(i));
                    _instructions.Add(new Instruction(OpCode.LoadConst, indexReg, ci, 0));
                    _instructions.Add(new Instruction(OpCode.SetElem, dest, indexReg, valueReg));
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
            case ArrayBindingPatternNode array:
            {
                var index = 0;
                foreach (var element in array.Elements)
                {
                    if (element.Target is null)
                    {
                        index++;
                        continue;
                    }

                    if (element.IsRest)
                    {
                        var restReg = BuildArrayRest(sourceReg, index);
                        EmitBindingPatternAssignment(element.Target, restReg, storeOp);
                        continue;
                    }

                    var valueReg = LoadArrayElement(sourceReg, index);
                    if (element.Initializer is not null)
                    {
                        valueReg = ApplyDefaultInitializerIfUndefined(valueReg, element.Initializer);
                    }

                    EmitBindingPatternAssignment(element.Target, valueReg, storeOp);
                    index++;
                }

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
                        valueReg = ApplyDefaultInitializerIfUndefined(valueReg, property.Initializer);
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

    private int LoadArrayElement(int arrayReg, int index)
    {
        var indexReg = AllocateRegister();
        var indexConst = AddConstant(JsValue.FromNumber(index));
        _instructions.Add(new Instruction(OpCode.LoadConst, indexReg, indexConst, 0));
        var valueReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.GetElem, valueReg, arrayReg, indexReg));
        return valueReg;
    }

    private int BuildArrayRest(int sourceReg, int startIndex)
    {
        var restReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.NewArray, restReg, 0, 0));

        var indexReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.LoadConst, indexReg, AddConstant(JsValue.FromNumber(startIndex)), 0));
        var writeIndexReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.LoadConst, writeIndexReg, AddConstant(JsValue.FromNumber(0)), 0));

        var oneReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.LoadConst, oneReg, AddConstant(JsValue.FromNumber(1)), 0));

        var lengthReg = AllocateRegister();
        var lengthNameIndex = GetOrCreatePropertyName("length");
        _instructions.Add(new Instruction(OpCode.GetPropByName, lengthReg, sourceReg, lengthNameIndex));

        var loopStart = _instructions.Count;
        var hasNextReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Lt, hasNextReg, indexReg, lengthReg));
        var jumpEnd = EmitPlaceholder(OpCode.JumpIfFalse, hasNextReg);

        var elementReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.GetElem, elementReg, sourceReg, indexReg));
        _instructions.Add(new Instruction(OpCode.SetElem, restReg, writeIndexReg, elementReg));

        var nextIndexReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Add, nextIndexReg, indexReg, oneReg));
        _instructions.Add(new Instruction(OpCode.Move, indexReg, nextIndexReg, 0));
        var nextWriteReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.Add, nextWriteReg, writeIndexReg, oneReg));
        _instructions.Add(new Instruction(OpCode.Move, writeIndexReg, nextWriteReg, 0));

        _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
        PatchJump(jumpEnd, _instructions.Count);
        return restReg;
    }

    private int ApplyDefaultInitializerIfUndefined(int valueReg, ExpressionNode initializer)
    {
        var undefinedReg = LoadUndefinedConstant();
        var isUndefinedReg = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.StrictEq, isUndefinedReg, valueReg, undefinedReg));
        var jumpIfNotUndefined = EmitPlaceholder(OpCode.JumpIfFalse, isUndefinedReg);

        var initializerReg = CompileExpression(initializer);
        _instructions.Add(new Instruction(OpCode.Move, valueReg, initializerReg, 0));
        PatchJump(jumpIfNotUndefined, _instructions.Count);
        return valueReg;
    }

    private int AllocateRegister() => _nextRegister++;

    private static FunctionKind SelectFunctionKind(bool isAsync, bool isGenerator, bool isArrow)
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

        return isArrow ? FunctionKind.Arrow : FunctionKind.Ordinary;
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
            var combinedReg = AllocateRegister();
            _instructions.Add(new Instruction(OpCode.Add, combinedReg, currentReg, expressionReg));
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
                throw new InvalidOperationException("'break' is only valid inside loops or switch statements.");
            }

            var target = _loopStack.Peek();
            EmitLeaveScopesForJump(target.ScopeDepthAtEntry);
            var jump = EmitPlaceholder(OpCode.Jump);
            target.BreakJumpIndices.Add(jump);
            return;
        }

        foreach (var labelCtx in _labelStack)
        {
            if (labelCtx.Name == label)
            {
                var jump = EmitPlaceholder(OpCode.Jump);
                labelCtx.BreakJumpIndices.Add(jump);
                return;
            }
        }

        foreach (var ctx in _loopStack)
        {
            if (ctx.Label == label)
            {
                EmitLeaveScopesForJump(ctx.ScopeDepthAtEntry);
                var jump = EmitPlaceholder(OpCode.Jump);
                ctx.BreakJumpIndices.Add(jump);
                return;
            }
        }

        throw new InvalidOperationException($"Undefined label '{label}'.");
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

    private void CompileContinueStatement(string? label)
    {
        if (label == null)
        {
            if (_loopStack.Count == 0)
            {
                throw new InvalidOperationException("'continue' is only valid inside loops.");
            }

            var topCtx = _loopStack.Peek();
            EmitLeaveScopesForJump(topCtx.ScopeDepthAtEntry);
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
                    throw new InvalidOperationException($"Label '{label}' does not mark a loop.");
                }

                EmitLeaveScopesForJump(ctx.ScopeDepthAtEntry);
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

        throw new InvalidOperationException($"Undefined label '{label}'.");
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

        return System.Numerics.BigInteger.Parse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static System.Numerics.BigInteger ParseBigIntWithRadix(string digits, int radix)
    {
        var result = System.Numerics.BigInteger.Zero;
        foreach (var ch in digits)
        {
            if (ch == '_') continue;
            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'z' => ch - 'a' + 10,
                >= 'A' and <= 'Z' => ch - 'A' + 10,
                _ => 0
            };
            result = result * radix + digit;
        }
        return result;
    }
}

