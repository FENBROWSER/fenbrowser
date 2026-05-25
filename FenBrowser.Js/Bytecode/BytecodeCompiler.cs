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
    private readonly HashSet<string> _lexicalDeclarationNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _constDeclarationNames = new(StringComparer.Ordinal);
    private readonly List<string> _propertyNames = new();
    private readonly Dictionary<string, int> _propertyNameToIndex = new(StringComparer.Ordinal);
    private readonly List<BytecodeFunction> _nestedFunctions = new();
    private readonly List<string> _parameterNames = new();
    private readonly Stack<LoopContext> _loopStack = new();
    private string? _name;
    private int _nextRegister = 1;
    private FunctionKind _currentFunctionKind = FunctionKind.Ordinary;

    public BytecodeFunction CompileScript(SourceText source)
    {
        var program = JsParser.ParseScript(source);
        return CompileProgram(program);
    }

    public BytecodeFunction CompileProgram(ProgramNode program)
    {
        return CompileProgramCore(
            program,
            parameters: Array.Empty<string>(),
            name: null,
            hasOwnArgumentsObject: false,
            functionKind: FunctionKind.Ordinary);
    }

    public BytecodeFunction CompileFunctionBody(SourceText body, IReadOnlyList<string> parameters, string? name)
    {
        var program = JsParser.ParseScript(body);
        return CompileProgramCore(
            program,
            parameters,
            name,
            hasOwnArgumentsObject: true,
            functionKind: FunctionKind.Ordinary);
    }

    private BytecodeFunction CompileProgramCore(
        ProgramNode program,
        IReadOnlyList<string> parameters,
        string? name,
        bool hasOwnArgumentsObject,
        FunctionKind functionKind)
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
        _name = name;
        _nextRegister = 1;
        _currentFunctionKind = functionKind;
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
            IsDerivedConstructor = _isDerivedConstructor,
            Instructions = _instructions.ToArray(),
            Constants = _constants.ToArray(),
            VariableSlots = new Dictionary<string, int>(_variables),
            VarDeclarationNames = _varDeclarationNames.ToArray(),
            LexicalDeclarationNames = _lexicalDeclarationNames.ToArray(),
            ConstDeclarationNames = _constDeclarationNames.ToArray(),
            PropertyNames = _propertyNames.ToArray(),
            ParameterNames = _parameterNames.ToArray(),
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
                foreach (var nested in block.Statements)
                {
                    CompileStatement(nested);
                }

                break;
            case VariableDeclarationStatementNode decl:
                foreach (var d in decl.Declarators)
                {
                    if (string.Equals(decl.Kind, "var", StringComparison.Ordinal))
                    {
                        _varDeclarationNames.Add(d.Identifier);
                    }
                    else if (string.Equals(decl.Kind, "const", StringComparison.Ordinal))
                    {
                        _constDeclarationNames.Add(d.Identifier);
                    }
                    else
                    {
                        _lexicalDeclarationNames.Add(d.Identifier);
                    }

                    var slot = GetOrCreateVariableSlot(d.Identifier);
                    if (d.Initializer is not null)
                    {
                        var reg = CompileExpression(d.Initializer);
                        var op = string.Equals(decl.Kind, "var", StringComparison.Ordinal)
                            ? OpCode.StoreVar
                            : OpCode.InitVar;
                        _instructions.Add(new Instruction(op, reg, slot, 0));
                    }
                    else if (string.Equals(decl.Kind, "let", StringComparison.Ordinal))
                    {
                        var reg = AllocateRegister();
                        var ci = AddConstant(JsValue.Undefined);
                        _instructions.Add(new Instruction(OpCode.LoadConst, reg, ci, 0));
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
            case ReturnStatementNode returnStmt:
                CompileReturnStatement(returnStmt);
                break;
            case BreakStatementNode:
                CompileBreakStatement();
                break;
            case ContinueStatementNode:
                CompileContinueStatement();
                break;
            case ThrowStatementNode throwStmt:
                CompileThrowStatement(throwStmt);
                break;
            case TryCatchStatementNode tryCatchStmt:
                CompileTryCatchStatement(tryCatchStmt);
                break;
            case TryFinallyStatementNode tryFinallyStmt:
                // Parser-subset support: keep execution of try and finally blocks ordered.
                CompileStatement(tryFinallyStmt.TryBlock);
                CompileStatement(tryFinallyStmt.FinallyBlock);
                break;
            case TryCatchFinallyStatementNode tryCatchFinallyStmt:
            {
                var tryCatchOnly = new TryCatchStatementNode(
                    tryCatchFinallyStmt.TryBlock,
                    tryCatchFinallyStmt.CatchIdentifier,
                    tryCatchFinallyStmt.CatchBlock,
                    tryCatchFinallyStmt.Span);
                CompileTryCatchStatement(tryCatchOnly);
                CompileStatement(tryCatchFinallyStmt.FinallyBlock);
                break;
            }
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
        var nestedProgram = new ProgramNode(ProgramKind.Script, functionDecl.Body.Statements, functionDecl.Body.Span);
        var childCompiler = new BytecodeCompiler();
        var nestedFunction = childCompiler.CompileProgramCore(
            nestedProgram,
            functionDecl.Parameters,
            functionDecl.Name,
            hasOwnArgumentsObject: true,
            functionKind: SelectFunctionKind(functionDecl.IsAsync, functionDecl.IsGenerator, isArrow: false));
        var nestedIndex = _nestedFunctions.Count;
        _nestedFunctions.Add(nestedFunction);

        var dest = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
        var slot = GetOrCreateVariableSlot(functionDecl.Name);
        _varDeclarationNames.Add(functionDecl.Name);
        _instructions.Add(new Instruction(OpCode.StoreVar, dest, slot, 0));
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
        var nestedProgram = new ProgramNode(ProgramKind.Script, fnExpr.Body.Statements, fnExpr.Body.Span);
        var childCompiler = new BytecodeCompiler { _compilingClassConstructor = this._compilingClassConstructor, _isDerivedConstructor = this._isDerivedConstructor, _brandTokens = this._brandTokens };
        var nestedFunction = childCompiler.CompileProgramCore(
            nestedProgram,
            fnExpr.Parameters,
            fnExpr.Name,
            hasOwnArgumentsObject: true,
            functionKind: SelectFunctionKind(fnExpr.IsAsync, fnExpr.IsGenerator, isArrow: false));
        var nestedIndex = _nestedFunctions.Count;
        _nestedFunctions.Add(nestedFunction);
        var dest = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
        return dest;
    }

    private static FunctionExpressionNode SynthesizeDefaultConstructor(string? className, bool isDerived = false)
    {
        // ECMA-262 15.7.10 Default Constructor. For base classes: empty body.
        // For derived classes: `constructor() { super(); }`. The spec actually
        // synthesises `constructor(...args) { super(...args); }`; we forward
        // zero args until call-with-spread is wired through the compiler.
        var span = default(SourceSpan);
        IReadOnlyList<StatementNode> body;
        if (isDerived)
        {
            var superCall = new CallExpressionNode(
                new SuperExpressionNode(span),
                Array.Empty<ExpressionNode>(),
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
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>()
        };
        _loopStack.Push(ctx);
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
            ContinueJumpIndices = new List<int>()
        };
        _loopStack.Push(ctx);
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
        if (!TryGetForInTargetSlot(forInStmt.Initializer, out var targetSlot))
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
        _instructions.Add(new Instruction(OpCode.StoreVar, keyReg, targetSlot, 0));

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>()
        };
        _loopStack.Push(ctx);
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
        if (!TryGetForInTargetSlot(forOfStmt.Initializer, out var targetSlot))
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
        _instructions.Add(new Instruction(OpCode.StoreVar, valueReg, targetSlot, 0));

        var ctx = new LoopContext
        {
            ContinueTarget = loopStart,
            BreakJumpIndices = new List<int>(),
            ContinueJumpIndices = new List<int>()
        };
        _loopStack.Push(ctx);
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

    private bool TryGetForInTargetSlot(StatementNode initializer, out int slot)
    {
        switch (initializer)
        {
            case VariableDeclarationStatementNode { Declarators.Count: 1 } declaration:
                slot = GetForInDeclarationTargetSlot(declaration);
                return true;
            case ExpressionStatementNode { Expression: IdentifierExpressionNode identifier }:
                slot = GetOrCreateVariableSlot(identifier.Name);
                return true;
            default:
                slot = 0;
                return false;
        }
    }

    private int GetForInDeclarationTargetSlot(VariableDeclarationStatementNode declaration)
    {
        var name = declaration.Declarators[0].Identifier;
        if (string.Equals(declaration.Kind, "var", StringComparison.Ordinal))
        {
            _varDeclarationNames.Add(name);
        }

        return GetOrCreateVariableSlot(name);
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

        var catchSlot = GetOrCreateVariableSlot(tryCatchStmt.CatchIdentifier);
        _instructions.Add(new Instruction(OpCode.StoreVar, 0, catchSlot, 0));
        CompileStatement(tryCatchStmt.CatchBlock);

        PatchJump(jumpAfterCatch, _instructions.Count);
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
            case CallExpressionNode call:
            {
                var calleeReg = -1;
                var thisReg = -1;
                var isMethodCall = false;

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
                else
                {
                    calleeReg = CompileExpression(call.Callee);
                }

                var isSuperCall = call.Callee is SuperExpressionNode;
                var dest = AllocateRegister();
                switch (call.Arguments.Count)
                {
                    case 0:
                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethod0, dest, calleeReg, thisReg)
                            : new Instruction(OpCode.Call0, dest, calleeReg, 0));
                        if (isSuperCall) _instructions.Add(new Instruction(OpCode.InitThisBinding, 0, 0, 0));
                        return dest;
                    case 1:
                    {
                        var arg0 = CompileExpression(call.Arguments[0]);
                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethod1, dest, calleeReg, thisReg, arg0)
                            : new Instruction(OpCode.Call1, dest, calleeReg, arg0));
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
                            : new Instruction(OpCode.CallN, dest, calleeReg, argStart, call.Arguments.Count));
                        if (isSuperCall) _instructions.Add(new Instruction(OpCode.InitThisBinding, 0, 0, 0));
                        return dest;
                    }
                }
            }
            case UnaryExpressionNode unary:
            {
                // ECMA-262 15.5 — yield / yield*.
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
                    if (_currentFunctionKind != FunctionKind.Async)
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
                var nestedProgram = new ProgramNode(ProgramKind.Script, fnExpr.Body.Statements, fnExpr.Body.Span);
                var childCompiler = new BytecodeCompiler();
                var nestedFunction = childCompiler.CompileProgramCore(
                    nestedProgram,
                    fnExpr.Parameters,
                    fnExpr.Name,
                    hasOwnArgumentsObject: true,
                    functionKind: SelectFunctionKind(fnExpr.IsAsync, fnExpr.IsGenerator, isArrow: false));
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

                var nestedProgram = new ProgramNode(ProgramKind.Script, statements, bodySpan);
                var childCompiler = new BytecodeCompiler();
                var nestedFunction = childCompiler.CompileProgramCore(
                    nestedProgram,
                    arrow.Parameters,
                    "<arrow>",
                    hasOwnArgumentsObject: false,
                    functionKind: SelectFunctionKind(arrow.IsAsync, isGenerator: false, isArrow: true));
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
                _instructions.Add(new Instruction(OpCode.NewObject, dest, 0, 0));
                var rawReg = AllocateRegister();
                var ci = AddConstant(JsValue.FromString(regex.RawText));
                _instructions.Add(new Instruction(OpCode.LoadConst, rawReg, ci, 0));
                var sourceIdx = GetOrCreatePropertyName("source");
                _instructions.Add(new Instruction(OpCode.SetPropByName, dest, sourceIdx, rawReg));
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
                    var nullishLeftReg = CompileExpression(bin.Left);
                    var nullishDest = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.Move, nullishDest, nullishLeftReg, 0));

                    var nullConstReg = AllocateRegister();
                    var nullConstIndex = AddConstant(JsValue.Null);
                    _instructions.Add(new Instruction(OpCode.LoadConst, nullConstReg, nullConstIndex, 0));

                    var isNullishReg = AllocateRegister();
                    _instructions.Add(new Instruction(OpCode.Eq, isNullishReg, nullishLeftReg, nullConstReg));
                    var nullishJumpIfNotNullish = EmitPlaceholder(OpCode.JumpIfFalse, isNullishReg);

                    var nullishRightReg = CompileExpression(bin.Right);
                    _instructions.Add(new Instruction(OpCode.Move, nullishDest, nullishRightReg, 0));
                    PatchJump(nullishJumpIfNotNullish, _instructions.Count);
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
            default:
                throw new InvalidOperationException($"Unsupported expression type {expr.GetType().Name}.");
        }
    }

    private int AllocateRegister() => _nextRegister++;

    private static FunctionKind SelectFunctionKind(bool isAsync, bool isGenerator, bool isArrow)
    {
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

    // H.5 — private names are mangled to __priv<N>__<name>. Detect so we can
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

    private void CompileBreakStatement()
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException("'break' is only valid inside loops.");
        }

        var jump = EmitPlaceholder(OpCode.Jump);
        _loopStack.Peek().BreakJumpIndices.Add(jump);
    }

    private void CompileContinueStatement()
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException("'continue' is only valid inside loops.");
        }

        var target = _loopStack.Peek().ContinueTarget;
        if (target >= 0)
        {
            _instructions.Add(new Instruction(OpCode.Jump, target, 0, 0));
            return;
        }

        var jump = EmitPlaceholder(OpCode.Jump);
        _loopStack.Peek().ContinueJumpIndices.Add(jump);
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
}
