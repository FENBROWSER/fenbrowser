using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Errors;
using FenValue = FenBrowser.FenEngine.Core.FenValue;

namespace FenBrowser.FenEngine.Core.Bytecode.Compiler
{
    /// <summary>
    /// Translates FenBrowser AST nodes into a flat CodeBlock array of instructions.
    /// Phase 1 implements basic expressions to prove the execution model.
    /// </summary>
    public class BytecodeCompiler
    {
        private readonly List<byte> _instructions = new List<byte>();
        private readonly List<FenValue> _constants = new List<FenValue>();
        private readonly Dictionary<string, int> _stringConstantIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<double, int> _numberConstantIndex = new Dictionary<double, int>();
        private readonly Stack<BreakContext> _breakContexts = new Stack<BreakContext>();
        private readonly Stack<LoopContext> _loopContexts = new Stack<LoopContext>();
        private readonly Stack<LabelContext> _labelContexts = new Stack<LabelContext>();
        private readonly bool _enableLocalSlots;
        private readonly List<Identifier> _functionParameters;
        private readonly string _functionName;
        private readonly bool _isEval;
        private readonly HashSet<FunctionDeclarationStatement> _topLevelHoistedFunctions = new HashSet<FunctionDeclarationStatement>();
        private readonly Dictionary<string, int> _localSlotByName = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _localSlotNames = new List<string>();
        private readonly HashSet<string> _localBindings = new HashSet<string>(StringComparer.Ordinal);
        private readonly bool _forceStrictRoot;
        private bool _currentCompileIsStrict;
        private int _visitDepth;
        // Production bundles routinely exceed small synthetic depth caps through
        // nested expression trees and generated wrapper functions.
        private const int MaxVisitDepth = 4096;
        [ThreadStatic]
        private static int _compileDepth;
        private const int MaxCompileDepth = 256;
        private int _syntheticNameCounter;
        private int _scopeDepth;
        private bool _insideBlock;
        private string _currentInferredName;
        // Annex B §B.3.3.1: block-scoped function names found during eval compilation
        private List<string> _annexBBlockFunctionNames;
        private readonly HashSet<FunctionDeclarationStatement> _annexBVarScopedBlockFunctions = new HashSet<FunctionDeclarationStatement>();

        public BytecodeCompiler(bool isEval = false)
            : this(enableLocalSlots: false, functionParameters: null, functionName: null, isEval: isEval)
        {
        }

        private BytecodeCompiler(bool enableLocalSlots, List<Identifier> functionParameters, string functionName, bool isEval = false, bool forceStrictRoot = false)
        {
            _enableLocalSlots = enableLocalSlots;
            _functionParameters = functionParameters;
            _functionName = functionName;
            _isEval = isEval;
            _forceStrictRoot = forceStrictRoot;
        }

        private static BytecodeCompiler CreateFunctionCompiler(List<Identifier> parameters, string functionName, bool forceStrictRoot = false)
        {
            return new BytecodeCompiler(
                enableLocalSlots: true,
                functionParameters: parameters,
                functionName: functionName,
                isEval: false,
                forceStrictRoot: forceStrictRoot);
        }

        public CodeBlock Compile(AstNode root)
        {
            if (++_compileDepth > MaxCompileDepth)
            {
                _compileDepth--;
                throw new InvalidOperationException("Bytecode compiler nesting depth exceeded");
            }

            try
            {
            _instructions.Clear();
            _constants.Clear();
            _stringConstantIndex.Clear();
            _numberConstantIndex.Clear();
            _breakContexts.Clear();
            _loopContexts.Clear();
            _labelContexts.Clear();
            _syntheticNameCounter = 0;
            _localBindings.Clear();
            _localSlotByName.Clear();
            _localSlotNames.Clear();
            _topLevelHoistedFunctions.Clear();
            _annexBVarScopedBlockFunctions.Clear();
            _visitDepth = 0;
            _currentCompileIsStrict = _forceStrictRoot || IsStrictRoot(root);

            if (_enableLocalSlots)
            {
                InitializeLocalBindings(root);
            }

            AnalyzeAnnexBBlockFunctions(root);
            HoistTopLevelFunctionDeclarations(root);
            HoistVarDeclarations(root);

            // Annex B: Pre-initialize var-scoped bindings for block-scoped function declarations (eval only)
            if (_isEval)
            {
                HoistBlockFunctions(root);
            }

            Visit(root);

            // Ensure every block ends with a Halt
            Emit(OpCode.Halt);

            var localSlots = _enableLocalSlots ? new List<string>(_localSlotNames) : null;
            var codeBlock = new CodeBlock(_instructions.ToArray(), new List<FenValue>(_constants), localSlots)
            {
                IsStrict = _currentCompileIsStrict,
                AnnexBBlockFunctionNames = _isEval ? _annexBBlockFunctionNames : null
            };
            return codeBlock;
            }
            finally
            {
                _compileDepth--;
            }
        }

        private static bool IsStrictRoot(AstNode root)
        {
            if (root is Program program)
            {
                if (program.IsStrict)
                {
                    return true;
                }

                return HasUseStrictDirective(program.Statements);
            }

            if (root is BlockStatement block)
            {
                return HasUseStrictDirective(block.Statements);
            }

            return false;
        }

        private static bool HasUseStrictDirective(List<Statement> statements)
        {
            if (statements == null)
            {
                return false;
            }

            foreach (var statement in statements)
            {
                if (statement is ExpressionStatement expressionStatement &&
                    expressionStatement.Expression is StringLiteral stringLiteral)
                {
                    if (IsUseStrictLiteral(stringLiteral))
                    {
                        return true;
                    }

                    continue;
                }

                break;
            }

            return false;
        }

        private static bool IsUseStrictLiteral(StringLiteral literal)
        {
            if (literal == null)
            {
                return false;
            }

            var value = literal.Value;
            return string.Equals(value, "use strict", StringComparison.Ordinal)
                || string.Equals(value, "\"use strict\"", StringComparison.Ordinal)
                || string.Equals(value, "'use strict'", StringComparison.Ordinal);
        }

        private static string GetFunctionSourceText(FunctionLiteral functionLiteral, string fallbackName)
        {
            if (!string.IsNullOrEmpty(functionLiteral?.Source))
            {
                return functionLiteral.Source;
            }

            if (functionLiteral == null)
            {
                return "function() { }";
            }

            string effectiveName = !string.IsNullOrEmpty(functionLiteral.Name)
                ? functionLiteral.Name
                : (fallbackName ?? string.Empty);
            string parameterList = FormatFunctionParameterList(functionLiteral.Parameters);

            if (functionLiteral.IsMethodDefinition)
            {
                string asyncPrefix = functionLiteral.IsAsync ? "async " : string.Empty;
                string generatorMarker = functionLiteral.IsGenerator ? "*" : string.Empty;
                return $"{asyncPrefix}{generatorMarker}{effectiveName}({parameterList}) {{ }}";
            }

            string functionAsyncPrefix = functionLiteral.IsAsync ? "async " : string.Empty;
            string functionGeneratorMarker = functionLiteral.IsGenerator ? "*" : string.Empty;
            string functionNameSegment = string.IsNullOrEmpty(effectiveName) ? string.Empty : $" {effectiveName}";
            return $"{functionAsyncPrefix}function{functionGeneratorMarker}{functionNameSegment}({parameterList}) {{ }}";
        }

        private static string FormatFunctionParameterList(List<Identifier> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return string.Empty;
            }

            var names = new List<string>(parameters.Count);
            foreach (var parameter in parameters)
            {
                string name;
                if (parameter?.DestructuringPattern != null)
                {
                    name = parameter.DestructuringPattern is ArrayLiteral ? "[]" : "{}";
                }
                else
                {
                    name = string.IsNullOrEmpty(parameter?.Value) ? "_" : parameter.Value;
                }

                if (parameter?.IsRest == true)
                {
                    name = "..." + name;
                }

                if (parameter?.DefaultValue != null)
                {
                    name += " = undefined";
                }

                names.Add(name);
            }

            return string.Join(", ", names);
        }

        private void HoistBlockFunctions(AstNode root)
        {
            // Annex B §B.3.3.1: Traverse the AST to find block-scoped function declarations and
            // pre-initialize their names to undefined in the enclosing function/global scope.
            // In eval code this also requires pre-initializing the outer variable scope (done in the VM).

            var functionToHoist = new List<FunctionDeclarationStatement>();

            void TraverseForHoisting(AstNode node, bool isTopLevel)
            {
                if (node == null) return;

                if (node is Program prog)
                {
                    foreach (var stmt in prog.Statements) TraverseForHoisting(stmt, true);
                }
                else if (node is BlockStatement block)
                {
                    foreach (var stmt in block.Statements) TraverseForHoisting(stmt, false);
                }
                else if (node is FunctionDeclarationStatement funcDecl)
                {
                    if (!isTopLevel && _annexBVarScopedBlockFunctions.Contains(funcDecl))
                    {
                        functionToHoist.Add(funcDecl);
                    }
                    // Do not traverse into function bodies
                }
                else if (node is IfStatement ifStmt)
                {
                    TraverseForHoisting(ifStmt.Consequence, false);
                    TraverseForHoisting(ifStmt.Alternative, false);
                }
                else if (node is WhileStatement whileStmt) TraverseForHoisting(whileStmt.Body, false);
                else if (node is DoWhileStatement doWhileStmt) TraverseForHoisting(doWhileStmt.Body, false);
                else if (node is ForStatement forStmt) TraverseForHoisting(forStmt.Body, false);
                else if (node is ForInStatement forInStmt) TraverseForHoisting(forInStmt.Body, false);
                else if (node is ForOfStatement forOfStmt) TraverseForHoisting(forOfStmt.Body, false);
                else if (node is TryStatement tryStmt)
                {
                    TraverseForHoisting(tryStmt.Block, false);
                    TraverseForHoisting(tryStmt.CatchBlock, false);
                    TraverseForHoisting(tryStmt.FinallyBlock, false);
                }
                else if (node is SwitchStatement switchStmt)
                {
                    if (switchStmt.Cases != null)
                    {
                        foreach (var c in switchStmt.Cases)
                        {
                            if (c.Consequent != null)
                            {
                                foreach (var stmt in c.Consequent) TraverseForHoisting(stmt, false);
                            }
                        }
                    }
                }
                else if (node is LabeledStatement labelStmt) TraverseForHoisting(labelStmt.Body, false);
            }

            TraverseForHoisting(root, true);

            // Collect names for Annex B outer-scope pre-initialization (used by ExecuteDirectEval)
            _annexBBlockFunctionNames = new List<string>();

            // Now emit the eval-scope variable initialization for these functions explicitly
            foreach (var hoisted in functionToHoist)
            {
                if (string.IsNullOrEmpty(hoisted.Function?.Name)) continue;

                Emit(OpCode.LoadUndefined);
                EmitStoreVarDeclarationByName(hoisted.Function.Name);
                Emit(OpCode.Pop);

                _annexBBlockFunctionNames.Add(hoisted.Function.Name);
            }
        }

        private void HoistTopLevelFunctionDeclarations(AstNode root)
        {
            List<Statement> topLevelStatements = null;
            if (root is Program program)
            {
                topLevelStatements = program.Statements;
            }
            else if (root is BlockStatement block)
            {
                topLevelStatements = block.Statements;
            }

            if (topLevelStatements == null)
            {
                return;
            }

            foreach (var statement in topLevelStatements)
            {
                if (!(statement is FunctionDeclarationStatement functionDeclaration))
                {
                    continue;
                }

                if (functionDeclaration.Function == null || string.IsNullOrEmpty(functionDeclaration.Function.Name))
                {
                    continue;
                }

                EmitFunctionDeclaration(functionDeclaration);
                _topLevelHoistedFunctions.Add(functionDeclaration);
            }
        }

        private void HoistVarDeclarations(AstNode root)
        {
            var hoistedNames = new HashSet<string>(StringComparer.Ordinal);

            void Collect(AstNode node)
            {
                if (node == null)
                {
                    return;
                }

                switch (node)
                {
                    case Program program:
                        foreach (var statement in program.Statements)
                        {
                            Collect(statement);
                        }
                        break;
                    case BlockStatement block:
                        foreach (var statement in block.Statements)
                        {
                            Collect(statement);
                        }
                        break;
                    case LetStatement letStatement when letStatement.Kind == DeclarationKind.Var:
                        if (!string.IsNullOrEmpty(letStatement.Name?.Value))
                        {
                            hoistedNames.Add(letStatement.Name.Value);
                        }
                        break;
                    case ForInStatement forInStatement when forInStatement.BindingKind == DeclarationKind.Var:
                        if (!string.IsNullOrEmpty(forInStatement.Variable?.Value))
                        {
                            hoistedNames.Add(forInStatement.Variable.Value);
                        }
                        Collect(forInStatement.Body);
                        break;
                    case ForOfStatement forOfStatement when forOfStatement.BindingKind == DeclarationKind.Var:
                        if (!string.IsNullOrEmpty(forOfStatement.Variable?.Value))
                        {
                            hoistedNames.Add(forOfStatement.Variable.Value);
                        }
                        Collect(forOfStatement.Body);
                        break;
                    case IfStatement ifStatement:
                        Collect(ifStatement.Consequence);
                        Collect(ifStatement.Alternative);
                        break;
                    case WhileStatement whileStatement:
                        Collect(whileStatement.Body);
                        break;
                    case DoWhileStatement doWhileStatement:
                        Collect(doWhileStatement.Body);
                        break;
                    case ForStatement forStatement:
                        Collect(forStatement.Init);
                        Collect(forStatement.Body);
                        break;
                    case TryStatement tryStatement:
                        Collect(tryStatement.Block);
                        Collect(tryStatement.CatchBlock);
                        Collect(tryStatement.FinallyBlock);
                        break;
                    case SwitchStatement switchStatement:
                        if (switchStatement.Cases != null)
                        {
                            foreach (var switchCase in switchStatement.Cases)
                            {
                                if (switchCase.Consequent == null)
                                {
                                    continue;
                                }

                                foreach (var statement in switchCase.Consequent)
                                {
                                    Collect(statement);
                                }
                            }
                        }
                        break;
                    case LabeledStatement labeledStatement:
                        Collect(labeledStatement.Body);
                        break;
                    case FunctionDeclarationStatement:
                    case FunctionLiteral:
                    case ClassStatement:
                        break;
                }
            }

            Collect(root);

            foreach (var hoistedName in hoistedNames)
            {
                EmitDeclareVarByName(hoistedName);
            }
        }
        private void Visit(AstNode node)
        {
            if (node == null) return;
            if (++_visitDepth > MaxVisitDepth)
            {
                _visitDepth--;
                throw new InvalidOperationException("Bytecode compiler recursion depth exceeded");
            }


            try
            {

            // Phase 1: Only handling a subset of nodes for proof of concept
            if (node is Program prog)
            {
                foreach (var stmt in prog.Statements)
                {
                    Visit(stmt);
                }
            }
            else if (node is ExpressionStatement exprStmt)
            {
                Visit(exprStmt.Expression);
                Emit(OpCode.PopAccumulator);
            }
            else if (node is InfixExpression binExpr)
            {
                if (TryEmitLinearInfixExpression(binExpr))
                {
                }
                else if ((binExpr.Operator == "++" || binExpr.Operator == "--") && binExpr.Right == null)
                {
                    EmitPostfixUpdate(binExpr.Left, binExpr.Operator);
                }
                else if (binExpr.Operator == ",")
                {
                    // Comma operator: evaluate left for side effects, then return right.
                    Visit(binExpr.Left);
                    Emit(OpCode.Pop);
                    Visit(binExpr.Right);
                }
                else if (binExpr.Operator == "&&")
                {
                    Visit(binExpr.Left);
                    Emit(OpCode.Dup);
                    int jumpEnd = EmitJump(OpCode.JumpIfFalse);
                    Emit(OpCode.Pop);
                    Visit(binExpr.Right);
                    PatchJump(jumpEnd);
                }
                else if (binExpr.Operator == "||")
                {
                    Visit(binExpr.Left);
                    Emit(OpCode.Dup);
                    int jumpEnd = EmitJump(OpCode.JumpIfTrue);
                    Emit(OpCode.Pop);
                    Visit(binExpr.Right);
                    PatchJump(jumpEnd);
                }
                else
                {
                    // Push Left, Push Right, Execute Op
                    Visit(binExpr.Left);
                    Visit(binExpr.Right);

                    if (!TryEmitBinaryOperator(binExpr.Operator))
                    {
                        int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: Operator '{binExpr.Operator}' not supported."));
                        Emit(OpCode.LoadConst);
                        EmitInt32(msgIdx);
                        Emit(OpCode.Throw);
                    }
                }
            }
            else if (node is BitwiseExpression bitwiseExpr)
            {
                Visit(new InfixExpression
                {
                    Left = bitwiseExpr.Left,
                    Operator = bitwiseExpr.Operator,
                    Right = bitwiseExpr.Right
                });
            }
            else if (node is IntegerLiteral intLit)
            {
                int idx = AddConstant(FenValue.FromNumber(intLit.Value));
                Emit(OpCode.LoadConst);
                EmitInt32(idx);
            }
            else if (node is DoubleLiteral doubleLit)
            {
                int idx = AddConstant(FenValue.FromNumber(doubleLit.Value));
                Emit(OpCode.LoadConst);
                EmitInt32(idx);
            }
            else if (node is BigIntLiteral bigIntLit)
            {
                int idx = AddConstant(FenValue.FromBigInt(new JsBigInt(bigIntLit.Value)));
                Emit(OpCode.LoadConst);
                EmitInt32(idx);
            }
            else if (node is RegexLiteral regexLit)
            {
                EmitRegexLiteral(regexLit);
            }
            else if (node is StringLiteral strLit)
            {
                int idx = AddConstant(FenValue.FromString(strLit.Value));
                Emit(OpCode.LoadConst);
                EmitInt32(idx);
            }
            else if (node is BooleanLiteral boolLit)
            {
                if (boolLit.Value) Emit(OpCode.LoadTrue);
                else Emit(OpCode.LoadFalse);
            }
            else if (node is NullLiteral)
            {
                Emit(OpCode.LoadNull);
            }
            else if (node is UndefinedLiteral)
            {
                Emit(OpCode.LoadUndefined);
            }
            else if (node is ExponentiationExpression expExpr)
            {
                Visit(expExpr.Left);
                Visit(expExpr.Right);
                Emit(OpCode.Exponent);
            }
            else if (node is ConditionalExpression condExpr)
            {
                Visit(condExpr.Condition);
                int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

                Visit(condExpr.Consequent);
                int jumpOverAltOffset = EmitJump(OpCode.Jump);

                PatchJump(jumpIfFalseOffset);
                if (condExpr.Alternate != null)
                {
                    Visit(condExpr.Alternate);
                }
                else
                {
                    Emit(OpCode.LoadUndefined);
                }
                PatchJump(jumpOverAltOffset);
            }
            else if (node is IfExpression ifExpr)
            {
                EmitIfExpression(ifExpr);
            }
            else if (node is NullishCoalescingExpression nullishExpr)
            {
                // left ?? right
                // Stack flow:
                //   [left] -> [left,left] -> [left, left==null]
                //   if false => keep original left
                //   if true  => pop original left, evaluate right
                Visit(nullishExpr.Left);
                Emit(OpCode.Dup);
                Emit(OpCode.LoadNull);
                Emit(OpCode.Equal);
                int jumpKeepLeft = EmitJump(OpCode.JumpIfFalse);

                Emit(OpCode.Pop);
                Visit(nullishExpr.Right);
                int jumpEnd = EmitJump(OpCode.Jump);

                PatchJump(jumpKeepLeft);
                PatchJump(jumpEnd);
            }
            else if (node is OptionalChainExpression optionalChainExpr)
            {
                // Evaluate base and short-circuit to undefined if null/undefined.
                Visit(optionalChainExpr.Object);

                Emit(OpCode.Dup);
                Emit(OpCode.LoadNull);
                Emit(OpCode.Equal);
                int jumpNullish = EmitJump(OpCode.JumpIfTrue);

                Emit(OpCode.Dup);
                Emit(OpCode.LoadUndefined);
                Emit(OpCode.StrictEqual);
                int jumpUndefined = EmitJump(OpCode.JumpIfTrue);

                if (optionalChainExpr.IsCall)
                {
                    // Optional call should return undefined for non-functions.
                    Emit(OpCode.Dup);
                    Emit(OpCode.Typeof);
                    int functionTypeConst = AddConstant(FenValue.FromString("function"));
                    Emit(OpCode.LoadConst);
                    EmitInt32(functionTypeConst);
                    Emit(OpCode.StrictEqual);
                    int jumpNotCallable = EmitJump(OpCode.JumpIfFalse);

                    foreach (var arg in optionalChainExpr.Arguments)
                    {
                        Visit(arg);
                    }
                    Emit(OpCode.Call);
                    EmitInt32(optionalChainExpr.Arguments.Count);
                    int jumpEndCall = EmitJump(OpCode.Jump);

                    int notCallableTarget = _instructions.Count;
                    PatchJumpTo(jumpNotCallable, notCallableTarget);
                    Emit(OpCode.Pop);
                    Emit(OpCode.LoadUndefined);

                    int endCallTarget = _instructions.Count;
                    PatchJumpTo(jumpEndCall, endCallTarget);
                }
                else if (optionalChainExpr.IsComputed)
                {
                    if (optionalChainExpr.Property == null)
                    {
                        int msgIdx = AddConstant(FenValue.FromString("SyntaxError: Optional computed chain missing property expression."));
                        Emit(OpCode.LoadConst);
                        EmitInt32(msgIdx);
                        Emit(OpCode.Throw);
                    }

                    Visit(optionalChainExpr.Property);
                    Emit(OpCode.LoadProp);
                }
                else
                {
                    int propertyConst = AddConstant(FenValue.FromString(optionalChainExpr.PropertyName ?? string.Empty));
                    Emit(OpCode.LoadConst);
                    EmitInt32(propertyConst);
                    Emit(OpCode.LoadProp);
                }

                int jumpEnd = EmitJump(OpCode.Jump);

                int nullishTarget = _instructions.Count;
                PatchJumpTo(jumpNullish, nullishTarget);
                PatchJumpTo(jumpUndefined, nullishTarget);
                Emit(OpCode.Pop); // drop short-circuit base value
                Emit(OpCode.LoadUndefined);

                int endTarget = _instructions.Count;
                PatchJumpTo(jumpEnd, endTarget);
            }
            else if (node is SpreadElement spreadElement)
            {
                // Spread is structurally handled by array/call/new/object lowering.
                // If it appears as a standalone expression, evaluate its argument as a best-effort fallback.
                Visit(spreadElement.Argument);
            }
            else if (node is TemplateLiteral templateLiteral)
            {
                EmitTemplateLiteral(templateLiteral);
            }
            else if (node is TaggedTemplateExpression taggedTemplateExpr)
            {
                EmitTaggedTemplateExpression(taggedTemplateExpr);
            }
            else if (node is PrivateIdentifier privateIdentifier)
            {
                EmitPrivateIdentifier(privateIdentifier);
            }
            else if (node is Identifier identifier)
            {
                EmitLoadVarByName(identifier.Value);
            }
            else if (node is ArrayLiteral arrayLit)
            {
                EmitArrayLiteral(arrayLit);
            }
            else if (node is ObjectLiteral objLit)
            {
                EmitObjectLiteral(objLit);
            }
            else if (node is MemberExpression memberExpr)
            {
                Visit(memberExpr.Object);
                int idx = AddConstant(FenValue.FromString(memberExpr.Property));
                Emit(OpCode.LoadConst);
                EmitInt32(idx);
                Emit(OpCode.LoadProp);
            }
            else if (node is IndexExpression indexExpr)
            {
                Visit(indexExpr.Left);
                Visit(indexExpr.Index);
                Emit(OpCode.LoadProp);
            }
            else if (node is PrefixExpression prefixExpr)
            {
                if (prefixExpr.Operator == "++" || prefixExpr.Operator == "--")
                {
                    EmitPrefixUpdate(prefixExpr.Right, prefixExpr.Operator);
                }
                else
                {
                    if (prefixExpr.Operator == "delete")
                    {
                        EmitDeleteExpression(prefixExpr.Right);
                    }
                    else
                    {
                        if (prefixExpr.Operator == "typeof" && prefixExpr.Right is Identifier typeofIdent)
                        {
                            // typeof undeclaredVar must NOT throw ReferenceError (spec 13.5.3)
                            if (TryGetLocalSlot(typeofIdent.Value, out int localSlot))
                            {
                                // Local variable: always defined, LoadLocal is safe
                                Emit(OpCode.LoadLocal);
                                EmitInt32(localSlot);
                            }
                            else
                            {
                                // Non-local: use LoadVarSafe to avoid ReferenceError
                                int nameIdx = AddConstant(FenValue.FromString(typeofIdent.Value));
                                Emit(OpCode.LoadVarSafe);
                                EmitInt32(nameIdx);
                            }
                            Emit(OpCode.Typeof);
                        }
                        else
                        {
                            Visit(prefixExpr.Right);
                            switch (prefixExpr.Operator)
                            {
                                case "+": Emit(OpCode.ToNumber); break;
                                case "-": Emit(OpCode.Negate); break;
                                case "!": Emit(OpCode.LogicalNot); break;
                                case "~": Emit(OpCode.BitwiseNot); break;
                                case "void":
                                    Emit(OpCode.Pop);
                                    Emit(OpCode.LoadUndefined);
                                    break;
                                case "typeof": Emit(OpCode.Typeof); break;
                                default:
                                    int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: Prefix operator '{prefixExpr.Operator}' not supported."));
                                    Emit(OpCode.LoadConst);
                                    EmitInt32(msgIdx);
                                    Emit(OpCode.Throw);
                                    break;
                            }
                        }
                    }
                }
            }
            else if (node is LogicalAssignmentExpression logicalAssignExpr)
            {
                VisitLogicalAssignment(logicalAssignExpr);
            }
            else if (node is CompoundAssignmentExpression compoundAssignExpr)
            {
                Visit(LowerCompoundAssignment(compoundAssignExpr));
            }
            else if (node is AssignmentExpression assign)
            {
                var inferredName = GetInferredAssignmentName(assign.Left);
                if (assign.Left is Identifier idNode)
                {
                    VisitFunctionWithInferredName(assign.Right, inferredName);
                    EmitUpdateVarByName(idNode.Value);
                }
                else if (assign.Left is MemberExpression assignMember)
                {
                    Visit(assignMember.Object);
                    int idx = AddConstant(FenValue.FromString(assignMember.Property));
                    Emit(OpCode.LoadConst);
                    EmitInt32(idx);
                    VisitFunctionWithInferredName(assign.Right, inferredName);
                    Emit(OpCode.StoreProp);
                }
                else if (assign.Left is IndexExpression assignIndex)
                {
                    Visit(assignIndex.Left);
                    Visit(assignIndex.Index);
                    VisitFunctionWithInferredName(assign.Right, inferredName);
                    Emit(OpCode.StoreProp);
                }
                else if (assign.Left is ArrayLiteral || assign.Left is ObjectLiteral)
                {
                    EmitDestructuringAssignmentExpression(assign.Left, assign.Right);
                }
                else
                {
                    Visit(assign.Right);
                    int msgIdx = AddConstant(FenValue.FromString("ReferenceError: Invalid left-hand side in assignment"));
                    Emit(OpCode.LoadConst);
                    EmitInt32(msgIdx);
                    Emit(OpCode.Throw);
                }
            }
            else if (node is LetStatement letStmt)
            {
                if (letStmt.DestructuringPattern != null)
                {
                    Expression destructuringPattern = letStmt.DestructuringPattern;
                    Expression destructuringSource = letStmt.Value;

                    // Parser recovery may encode `let { a } = obj` as:
                    //   DestructuringPattern = AssignmentExpression({ a }, obj), Value = null.
                    // Normalize to explicit pattern/source lowering.
                    if (destructuringSource == null &&
                        destructuringPattern is AssignmentExpression declarationAssignment &&
                        declarationAssignment.Left != null &&
                        declarationAssignment.Right != null &&
                        (declarationAssignment.Left is ObjectLiteral ||
                         declarationAssignment.Left is ArrayLiteral ||
                         declarationAssignment.Left is Identifier))
                    {
                        destructuringPattern = declarationAssignment.Left;
                        destructuringSource = declarationAssignment.Right;
                    }

                    EmitDestructuringBinding(destructuringPattern, destructuringSource);
                }
                else if (letStmt.Value != null)
                {
                    var inferredName = letStmt.Name?.Value;
                    if (!string.IsNullOrEmpty(inferredName))
                    {
                        VisitFunctionWithInferredName(letStmt.Value, inferredName);
                    }
                    else
                    {
                        Visit(letStmt.Value);
                    }
                    if (letStmt.Name != null)
                    {
                        if (letStmt.Kind == DeclarationKind.Var)
                        {
                            EmitStoreVarDeclarationByName(letStmt.Name.Value);
                        }
                        else
                        {
                            EmitStoreVarByName(letStmt.Name.Value);
                        }
                    }
                    Emit(OpCode.Pop); // declarations are statements; discard the stored value from the stack
                }
                else if (letStmt.Name != null)
                {
                    if (letStmt.Kind == DeclarationKind.Var)
                    {
                        EmitDeclareVarByName(letStmt.Name.Value);
                    }
                    else
                    {
                        // Lexical declarations initialize on execution.
                        Emit(OpCode.LoadUndefined);
                        EmitStoreVarByName(letStmt.Name.Value);
                        Emit(OpCode.Pop);
                    }
                }
            }
            else if (node is EmptyExpression)
            {
                Emit(OpCode.LoadUndefined);
            }
            else if (node is BlockStatement blockStmt)
            {
                // Only create a new scope if the block contains let/const/class/function declarations.
                bool needsScope = false;
                if (_scopeDepth > 0)
                {
                    foreach (var s in blockStmt.Statements)
                    {
                        if ((s is LetStatement blockLetStmt && blockLetStmt.Kind != DeclarationKind.Var) ||
                            s is ClassStatement ||
                            s is FunctionDeclarationStatement)
                        {
                            needsScope = true;
                            break;
                        }
                    }
                }
                if (needsScope) Emit(OpCode.PushScope);
                _scopeDepth++;
                bool previousInsideBlock = _insideBlock;
                _insideBlock = true;
                foreach (var stmt in blockStmt.Statements)
                {
                    Visit(stmt);
                }
                _insideBlock = previousInsideBlock;
                _scopeDepth--;
                if (needsScope) Emit(OpCode.PopScope);
            }
            else if (node is IfStatement ifStmt)
            {
                Visit(ifStmt.Condition);
                int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

                VisitScopedStatementClause(ifStmt.Consequence);

                if (ifStmt.Alternative != null)
                {
                    int jumpOverAltOffset = EmitJump(OpCode.Jump);
                    PatchJump(jumpIfFalseOffset);
                    VisitScopedStatementClause(ifStmt.Alternative);
                    PatchJump(jumpOverAltOffset);
                }
                else
                {
                    PatchJump(jumpIfFalseOffset);
                }
            }
            else if (node is WhileStatement whileStmt)
            {
                EmitWhileStatement(whileStmt);
            }
            else if (node is DoWhileStatement doWhileStmt)
            {
                EmitDoWhileStatement(doWhileStmt);
            }
            else if (node is ForStatement forStmt)
            {
                EmitForStatement(forStmt);
            }
            else if (node is ForInStatement forInStmt)
            {
                EmitForInStatement(forInStmt);
            }
            else if (node is ForOfStatement forOfStmt)
            {
                EmitForOfStatement(forOfStmt);
            }
            else if (node is ClassStatement classStmt)
            {
                EmitClassStatement(classStmt, emitResultValue: false);
            }
            else if (node is ClassExpression classExpr)
            {
                EmitClassExpression(classExpr);
            }
            else if (node is FunctionLiteral funcLit)
            {
                ValidateSupportedParameterList(funcLit.Parameters, "FunctionLiteral");
                string functionName = !string.IsNullOrEmpty(funcLit.Name) ? funcLit.Name : _currentInferredName;
                var funcCompiler = CreateFunctionCompiler(funcLit.Parameters, functionName, funcLit.IsStrict);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(funcLit.Body, funcLit.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);

                var templateFunc = new FenFunction(funcLit.Parameters, compiledBlock, null)
                {
                    IsAsync = funcLit.IsAsync,
                    IsGenerator = funcLit.IsGenerator,
                    IsMethodDefinition = funcLit.IsMethodDefinition,
                    Source = GetFunctionSourceText(funcLit, functionName),
                    NeedsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap),
                    LocalMap = localMap
                };
                if (!string.IsNullOrEmpty(functionName))
                {
                    templateFunc.Name = functionName;
                }
                int idx = AddConstant(FenValue.FromFunction(templateFunc));
                
                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is AsyncFunctionExpression asyncFuncExpr)
            {
                ValidateSupportedParameterList(asyncFuncExpr.Parameters, "AsyncFunctionExpression");
                string asyncFunctionName = asyncFuncExpr.Name?.Value ?? _currentInferredName;
                var funcCompiler = CreateFunctionCompiler(asyncFuncExpr.Parameters, asyncFunctionName, _currentCompileIsStrict);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(asyncFuncExpr.Body, asyncFuncExpr.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);

                var templateFunc = new FenFunction(asyncFuncExpr.Parameters, compiledBlock, null)
                {
                    IsAsync = true,
                    IsMethodDefinition = false,
                    NeedsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap),
                    LocalMap = localMap
                };
                if (!string.IsNullOrEmpty(asyncFunctionName))
                {
                    templateFunc.Name = asyncFunctionName;
                }
                int idx = AddConstant(FenValue.FromFunction(templateFunc));

                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is ArrowFunctionExpression arrowExpr)
            {
                ValidateSupportedParameterList(arrowExpr.Parameters, "ArrowFunctionExpression");
                var funcCompiler = CreateFunctionCompiler(arrowExpr.Parameters, _currentInferredName, _currentCompileIsStrict);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(arrowExpr.Body, arrowExpr.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);

                var templateFunc = new FenFunction(arrowExpr.Parameters, compiledBlock, null)
                {
                    IsArrowFunction = true,
                    IsAsync = arrowExpr.IsAsync,
                    IsMethodDefinition = false,
                    NeedsArgumentsObject = false,
                    LocalMap = localMap
                };
                if (!string.IsNullOrEmpty(_currentInferredName))
                {
                    templateFunc.Name = _currentInferredName;
                }
                int idx = AddConstant(FenValue.FromFunction(templateFunc));

                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is FunctionDeclarationStatement funcDecl)
            {
                if (_topLevelHoistedFunctions.Contains(funcDecl))
                {
                    return;
                }

                EmitFunctionDeclaration(funcDecl);
            }
            else if (node is ReturnStatement retStmt)
            {
                if (retStmt.ReturnValue != null)
                {
                    Visit(retStmt.ReturnValue);
                }
                else
                {
                    Emit(OpCode.LoadUndefined);
                }
                Emit(OpCode.Return);
            }
            else if (node is ThrowStatement throwStmt)
            {
                Visit(throwStmt.Value);
                Emit(OpCode.Throw);
            }
            else if (node is ThrowExpression throwExpr)
            {
                Visit(throwExpr.Value);
                Emit(OpCode.Throw);
            }
            else if (node is YieldExpression yieldExpr)
            {
                EmitYieldExpression(yieldExpr);
            }
            else if (node is AwaitExpression awaitExpr)
            {
                Visit(awaitExpr.Argument);
                Emit(OpCode.Await);
            }
            else if (node is ImportMetaExpression)
            {
                EmitImportMetaExpression();
            }
            else if (node is NewTargetExpression)
            {
                Emit(OpCode.LoadNewTarget);
            }
            else if (node is MethodDefinition methodDefinition)
            {
                EmitMethodDefinition(methodDefinition);
            }
            else if (node is ClassProperty classProperty)
            {
                EmitClassProperty(classProperty);
            }
            else if (node is StaticBlock staticBlock)
            {
                EmitStaticBlock(staticBlock);
            }
            else if (node is ImportDeclaration importDeclaration)
            {
                EmitImportDeclaration(importDeclaration);
            }
            else if (node is ExportDeclaration exportDeclaration)
            {
                EmitExportDeclaration(exportDeclaration);
            }
            else if (node is WithStatement withStatement)
            {
                EmitWithStatement(withStatement);
            }
            else if (node is BitwiseNotExpression bitwiseNotExpr)
            {
                Visit(bitwiseNotExpr.Operand);
                Emit(OpCode.BitwiseNot);
            }
            else if (node is SwitchStatement switchStmt)
            {
                EmitSwitchStatement(switchStmt);
            }
            else if (node is LabeledStatement labeledStmt)
            {
                EmitLabeledStatement(labeledStmt);
            }
            else if (node is BreakStatement breakStmt)
            {
                EmitBreakStatement(breakStmt);
            }
            else if (node is ContinueStatement continueStmt)
            {
                EmitContinueStatement(continueStmt);
            }
            else if (node is TryStatement tryStmt)
            {
                Emit(OpCode.PushExceptionHandler);
                int catchOffsetIndex = _instructions.Count;
                EmitInt32(0);   // catch offset (patched below)
                int finallyOffsetIndex = _instructions.Count;
                EmitInt32(-1);  // finally offset (patched below if present)

                Visit(tryStmt.Block);
                Emit(OpCode.PopExceptionHandler);

                // Jump over catch block on normal completion
                int jumpOverCatch = EmitJump(OpCode.Jump);

                // --- Catch block ---
                int catchStart = _instructions.Count;
                if (tryStmt.CatchBlock != null)
                {
                    byte[] catchBytes = BitConverter.GetBytes(catchStart);
                    for (int i = 0; i < 4; i++) _instructions[catchOffsetIndex + i] = catchBytes[i];

                    if (tryStmt.CatchParameter != null)
                    {
                        EmitStoreVarByName(tryStmt.CatchParameter.Value);
                        Emit(OpCode.Pop);

                        if (tryStmt.CatchParameter.DestructuringPattern != null)
                        {
                            EmitDestructuringFromVariable(tryStmt.CatchParameter.DestructuringPattern, tryStmt.CatchParameter.Value);
                        }
                    }
                    else
                    {
                        Emit(OpCode.Pop); // Pop exception value when catch has no parameter
                    }

                    Visit(tryStmt.CatchBlock);
                }
                else
                {
                    // No catch: patch catch offset to -1 so VM knows to skip catch
                    byte[] cbytes = BitConverter.GetBytes(-1);
                    for (int i = 0; i < 4; i++) _instructions[catchOffsetIndex + i] = cbytes[i];
                }

                PatchJump(jumpOverCatch);

                // --- Finally block ---
                if (tryStmt.FinallyBlock != null)
                {
                    // Patch the finally offset so the VM can find it
                    int finallyStart = _instructions.Count;
                    byte[] finallyBytes = BitConverter.GetBytes(finallyStart);
                    for (int i = 0; i < 4; i++) _instructions[finallyOffsetIndex + i] = finallyBytes[i];

                    Emit(OpCode.EnterFinally);
                    Visit(tryStmt.FinallyBlock);
                    Emit(OpCode.ExitFinally);
                }
            }
            else if (node is CallExpression callExpr)
            {
                // Detect method calls (obj.method() or obj[key]()) so we can pass the receiver as 'this'.
                bool isMethodCall = false;
                if (callExpr.Function is MemberExpression memberCallTarget)
                {
                    isMethodCall = true;
                    Visit(memberCallTarget.Object);         // push receiver
                    Emit(OpCode.Dup);                       // dup receiver: [recv, recv]
                    int keyIdx = AddConstant(FenValue.FromString(memberCallTarget.Property));
                    Emit(OpCode.LoadConst);
                    EmitInt32(keyIdx);
                    Emit(OpCode.LoadProp);                  // [recv, fn]
                }
                else if (callExpr.Function is IndexExpression indexCallTarget)
                {
                    isMethodCall = true;
                    Visit(indexCallTarget.Left);            // push receiver
                    Emit(OpCode.Dup);                       // dup receiver: [recv, recv]
                    Visit(indexCallTarget.Index);           // push key: [recv, recv, key]
                    Emit(OpCode.LoadProp);                  // [recv, fn]
                }
                else
                {
                    Visit(callExpr.Function);
                }

                if (ContainsSpread(callExpr.Arguments))
                {
                    EmitArgumentsArray(callExpr.Arguments);
                    Emit(isMethodCall ? OpCode.CallMethodFromArray : OpCode.CallFromArray);
                }
                else
                {
                    foreach (var arg in callExpr.Arguments)
                    {
                        Visit(arg);
                    }
                    Emit(isMethodCall ? OpCode.CallMethod : OpCode.Call);
                    EmitInt32(callExpr.Arguments.Count);
                }
            }
            else if (node is DirectEvalExpression directEvalExpr)
            {
                Visit(directEvalExpr.Source ?? new UndefinedLiteral());
                Emit(OpCode.DirectEval);

                int directEvalFlags = 0;
                if (directEvalExpr.AllowNewTarget)
                {
                    directEvalFlags |= 0x1;
                }
                if (directEvalExpr.ForceUndefinedNewTarget)
                {
                    directEvalFlags |= 0x2;
                }
                if (directEvalExpr.AllowSuperProperty)
                {
                    directEvalFlags |= 0x4;
                }

                EmitInt32(directEvalFlags);
            }
            else if (node is NewExpression newExpr)
            {
                Visit(newExpr.Constructor);
                if (ContainsSpread(newExpr.Arguments))
                {
                    EmitArgumentsArray(newExpr.Arguments);
                    Emit(OpCode.ConstructFromArray);
                }
                else
                {
                    foreach (var arg in newExpr.Arguments)
                    {
                        Visit(arg);
                    }
                    Emit(OpCode.Construct);
                    EmitInt32(newExpr.Arguments.Count);
                }
            }
            else
            {
                // ECMA-262: Emit a runtime error rather than crashing the compiler.
                // This allows the rest of the program to compile while the unsupported node
                // produces a clear error only if/when it actually executes.
                int errIdx = AddConstant(FenValue.FromString($"SyntaxError: Unsupported syntax ({node.GetType().Name}) encountered during compilation."));
                Emit(OpCode.LoadConst);
                EmitInt32(errIdx);
                Emit(OpCode.Throw);
            }
            }
            finally
            {
                _visitDepth--;
            }
        }

        private bool TryEmitLinearInfixExpression(InfixExpression root)
        {
            if (root == null || string.IsNullOrEmpty(root.Operator) || root.Right == null)
            {
                return false;
            }

            if (root.Operator == "++" || root.Operator == "--" || root.Operator == "**")
            {
                return false;
            }

            var operands = CollectLinearInfixOperands(root, root.Operator);
            if (operands.Count <= 2)
            {
                return false;
            }

            switch (root.Operator)
            {
                case ",":
                    EmitFlattenedCommaChain(operands);
                    return true;
                case "&&":
                    EmitFlattenedLogicalChain(operands, OpCode.JumpIfFalse);
                    return true;
                case "||":
                    EmitFlattenedLogicalChain(operands, OpCode.JumpIfTrue);
                    return true;
                default:
                    return TryEmitFlattenedBinaryChain(operands, root.Operator);
            }
        }

        private static List<AstNode> CollectLinearInfixOperands(InfixExpression root, string operatorToken)
        {
            var rightOperands = new Stack<AstNode>();
            AstNode current = root;

            while (current is InfixExpression infix &&
                   infix.Right != null &&
                   string.Equals(infix.Operator, operatorToken, StringComparison.Ordinal) &&
                   infix.Operator != "++" &&
                   infix.Operator != "--")
            {
                rightOperands.Push(infix.Right);
                current = infix.Left;
            }

            var operands = new List<AstNode>(rightOperands.Count + 1)
            {
                current
            };

            while (rightOperands.Count > 0)
            {
                operands.Add(rightOperands.Pop());
            }

            return operands;
        }

        private void EmitFlattenedCommaChain(List<AstNode> operands)
        {
            for (int i = 0; i < operands.Count; i++)
            {
                Visit(operands[i]);
                if (i < operands.Count - 1)
                {
                    Emit(OpCode.Pop);
                }
            }
        }

        private void EmitFlattenedLogicalChain(List<AstNode> operands, OpCode shortCircuitJump)
        {
            Visit(operands[0]);

            for (int i = 1; i < operands.Count; i++)
            {
                Emit(OpCode.Dup);
                int jumpEnd = EmitJump(shortCircuitJump);
                Emit(OpCode.Pop);
                Visit(operands[i]);
                PatchJump(jumpEnd);
            }
        }

        private bool TryEmitFlattenedBinaryChain(List<AstNode> operands, string operatorToken)
        {
            if (!IsFlattenableBinaryOperator(operatorToken))
            {
                return false;
            }

            Visit(operands[0]);
            for (int i = 1; i < operands.Count; i++)
            {
                Visit(operands[i]);
                TryEmitBinaryOperator(operatorToken);
            }

            return true;
        }

        private static bool IsFlattenableBinaryOperator(string operatorToken)
        {
            switch (operatorToken)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                case "%":
                case "==":
                case "===":
                case "!=":
                case "!==":
                case "<":
                case ">":
                case "<=":
                case ">=":
                case "in":
                case "instanceof":
                case "&":
                case "|":
                case "^":
                case "<<":
                case ">>":
                case ">>>":
                    return true;
                default:
                    return false;
            }
        }

        private bool TryEmitBinaryOperator(string operatorToken)
        {
            switch (operatorToken)
            {
                case "+":
                    Emit(OpCode.Add);
                    return true;
                case "-":
                    Emit(OpCode.Subtract);
                    return true;
                case "*":
                    Emit(OpCode.Multiply);
                    return true;
                case "/":
                    Emit(OpCode.Divide);
                    return true;
                case "%":
                    Emit(OpCode.Modulo);
                    return true;
                case "**":
                    Emit(OpCode.Exponent);
                    return true;
                case "==":
                    Emit(OpCode.Equal);
                    return true;
                case "===":
                    Emit(OpCode.StrictEqual);
                    return true;
                case "!=":
                    Emit(OpCode.NotEqual);
                    return true;
                case "!==":
                    Emit(OpCode.StrictNotEqual);
                    return true;
                case "<":
                    Emit(OpCode.LessThan);
                    return true;
                case ">":
                    Emit(OpCode.GreaterThan);
                    return true;
                case "<=":
                    Emit(OpCode.LessThanOrEqual);
                    return true;
                case ">=":
                    Emit(OpCode.GreaterThanOrEqual);
                    return true;
                case "in":
                    Emit(OpCode.InOperator);
                    return true;
                case "instanceof":
                    Emit(OpCode.InstanceOf);
                    return true;
                case "&":
                    Emit(OpCode.BitwiseAnd);
                    return true;
                case "|":
                    Emit(OpCode.BitwiseOr);
                    return true;
                case "^":
                    Emit(OpCode.BitwiseXor);
                    return true;
                case "<<":
                    Emit(OpCode.LeftShift);
                    return true;
                case ">>":
                    Emit(OpCode.RightShift);
                    return true;
                case ">>>":
                    Emit(OpCode.UnsignedRightShift);
                    return true;
                default:
                    return false;
            }
        }

        private void Emit(OpCode opcode)
        {
            _instructions.Add((byte)opcode);
        }

        private void EmitInt32(int value)
        {
            _instructions.AddRange(BitConverter.GetBytes(value));
        }

        private int AddConstant(FenValue val)
        {
            // Deduplicate string and number constants to keep the constants pool compact.
            if (val.IsString)
            {
                string str = val.AsString();
                if (_stringConstantIndex.TryGetValue(str, out int existingIdx))
                {
                    return existingIdx;
                }
                int newIdx = _constants.Count;
                _constants.Add(val);
                _stringConstantIndex[str] = newIdx;
                return newIdx;
            }

            if (val.IsNumber)
            {
                double num = val._numberValue;
                if (_numberConstantIndex.TryGetValue(num, out int existingIdx))
                {
                    return existingIdx;
                }
                int newIdx = _constants.Count;
                _constants.Add(val);
                _numberConstantIndex[num] = newIdx;
                return newIdx;
            }

            // Objects (regex, function templates) and other types are not deduplicated.
            _constants.Add(val);
            return _constants.Count - 1;
        }

        private int EmitJump(OpCode opcode)
        {
            Emit(opcode);
            _instructions.AddRange(BitConverter.GetBytes(0));
            return _instructions.Count - 4;
        }

        private void PatchJump(int offset)
        {
            int jumpTarget = _instructions.Count;
            byte[] bytes = BitConverter.GetBytes(jumpTarget);
            for (int i = 0; i < 4; i++)
            {
                _instructions[offset + i] = bytes[i];
            }
        }

        private void PatchJumpTo(int offset, int jumpTarget)
        {
            byte[] bytes = BitConverter.GetBytes(jumpTarget);
            for (int i = 0; i < 4; i++)
            {
                _instructions[offset + i] = bytes[i];
            }
        }

        private void EmitTemplateLiteral(TemplateLiteral templateLiteral)
        {
            string firstSegment = templateLiteral.Quasis.Count > 0
                ? (templateLiteral.Quasis[0]?.Value ?? string.Empty)
                : string.Empty;

            int firstConstIdx = AddConstant(FenValue.FromString(firstSegment));
            Emit(OpCode.LoadConst);
            EmitInt32(firstConstIdx);

            for (int i = 0; i < templateLiteral.Expressions.Count; i++)
            {
                Visit(templateLiteral.Expressions[i]);
                Emit(OpCode.Add);

                string nextSegment = (i + 1) < templateLiteral.Quasis.Count
                    ? (templateLiteral.Quasis[i + 1]?.Value ?? string.Empty)
                    : string.Empty;

                int nextConstIdx = AddConstant(FenValue.FromString(nextSegment));
                Emit(OpCode.LoadConst);
                EmitInt32(nextConstIdx);
                Emit(OpCode.Add);
            }
        }

        private void EmitTaggedTemplateExpression(TaggedTemplateExpression taggedTemplateExpr)
        {
            Visit(taggedTemplateExpr.Tag);
            EmitStringArrayWithRaw(taggedTemplateExpr.Strings);

            foreach (var expression in taggedTemplateExpr.Expressions)
            {
                Visit(expression);
            }

            Emit(OpCode.Call);
            EmitInt32((taggedTemplateExpr.Expressions?.Count ?? 0) + 1);
        }

        private void EmitStringArrayWithRaw(List<string> strings)
        {
            EmitStringArray(strings);
            Emit(OpCode.Dup);

            int rawConstIdx = AddConstant(FenValue.FromString("raw"));
            Emit(OpCode.LoadConst);
            EmitInt32(rawConstIdx);

            EmitStringArray(strings);
            Emit(OpCode.StoreProp);
            Emit(OpCode.Pop);
        }

        private void EmitStringArray(List<string> strings)
        {
            Emit(OpCode.MakeArray);
            EmitInt32(0);

            if (strings == null)
            {
                return;
            }

            foreach (var part in strings)
            {
                Emit(OpCode.Dup);
                int partIdx = AddConstant(FenValue.FromString(part ?? string.Empty));
                Emit(OpCode.LoadConst);
                EmitInt32(partIdx);
                Emit(OpCode.ArrayAppend);
                Emit(OpCode.Pop);
            }
        }

        private void EmitArrayLiteral(ArrayLiteral arrayLit)
        {
            Emit(OpCode.MakeArray);
            EmitInt32(0);

            foreach (var element in arrayLit.Elements)
            {
                Emit(OpCode.Dup);
                if (element is SpreadElement spreadElement)
                {
                    Visit(spreadElement.Argument);
                    Emit(OpCode.ArrayAppendSpread);
                }
                else
                {
                    Visit(element);
                    Emit(OpCode.ArrayAppend);
                }
                Emit(OpCode.Pop);
            }
        }

        private void EmitObjectLiteral(ObjectLiteral objLit)
        {
            string objectVariable = NextSyntheticName("object_literal");
            Emit(OpCode.MakeObject);
            EmitInt32(0);
            EmitStoreVarByName(objectVariable);
            Emit(OpCode.Pop);

            foreach (var pair in objLit.Pairs)
            {
                string valueVariable = null;
                if (pair.Value is FunctionLiteral functionLiteral && functionLiteral.IsMethodDefinition)
                {
                    valueVariable = NextSyntheticName("object_method");
                    VisitFunctionWithInferredName(pair.Value, GetObjectLiteralInferredName(pair.Key));
                    EmitStoreVarByName(valueVariable);
                    Emit(OpCode.Pop);

                    EmitLoadVarByName(valueVariable);
                    EmitLoadVarByName(objectVariable);
                    Emit(OpCode.SetFunctionHomeObject);
                    EmitStoreVarByName(valueVariable);
                    Emit(OpCode.Pop);
                }

                if (pair.Key.StartsWith("__spread_", StringComparison.Ordinal) && pair.Value is SpreadElement spreadElement)
                {
                    EmitLoadVarByName(objectVariable);
                    Visit(spreadElement.Argument);
                    Emit(OpCode.ObjectSpread);
                    EmitStoreVarByName(objectVariable);
                    Emit(OpCode.Pop);
                    continue;
                }

                if (pair.Key.StartsWith("__get_", StringComparison.Ordinal) ||
                    pair.Key.StartsWith("__set_", StringComparison.Ordinal))
                {
                    bool isGetter = pair.Key.StartsWith("__get_", StringComparison.Ordinal);
                    string accessorMarker = isGetter ? "__get_" : "__set_";

                    EmitLoadVarByName(objectVariable);
                    if (objLit.ComputedKeys.TryGetValue(pair.Key, out var accessorComputedKey))
                    {
                        int markerIdx = AddConstant(FenValue.FromString(accessorMarker));
                        Emit(OpCode.LoadConst);
                        EmitInt32(markerIdx);
                        Visit(accessorComputedKey);
                        Emit(OpCode.Add);
                    }
                    else
                    {
                        int keyConstIdx = AddConstant(FenValue.FromString(pair.Key));
                        Emit(OpCode.LoadConst);
                        EmitInt32(keyConstIdx);
                    }

                    if (!string.IsNullOrEmpty(valueVariable))
                    {
                        EmitLoadVarByName(valueVariable);
                    }
                    else
                    {
                        VisitFunctionWithInferredName(pair.Value, GetObjectLiteralInferredName(pair.Key));
                    }
                    Emit(OpCode.StoreProp);
                    Emit(OpCode.Pop);
                }
                else
                {
                    EmitLoadVarByName(objectVariable);

                    if (pair.Key.StartsWith("__computed_", StringComparison.Ordinal) &&
                        objLit.ComputedKeys.TryGetValue(pair.Key, out var computedKeyExpr))
                    {
                        Visit(computedKeyExpr);
                    }
                    else
                    {
                        int keyConstIdx = AddConstant(FenValue.FromString(pair.Key));
                        Emit(OpCode.LoadConst);
                        EmitInt32(keyConstIdx);
                    }

                    if (!string.IsNullOrEmpty(valueVariable))
                    {
                        EmitLoadVarByName(valueVariable);
                    }
                    else
                    {
                        VisitFunctionWithInferredName(pair.Value, GetObjectLiteralInferredName(pair.Key));
                    }
                    Emit(OpCode.StoreProp);
                    Emit(OpCode.Pop);
                }
            }

            EmitLoadVarByName(objectVariable);
        }

        private static bool ContainsSpread(List<Expression> arguments)
        {
            if (arguments == null)
            {
                return false;
            }

            foreach (var arg in arguments)
            {
                if (arg is SpreadElement)
                {
                    return true;
                }
            }

            return false;
        }

        private void EmitArgumentsArray(List<Expression> arguments)
        {
            Emit(OpCode.MakeArray);
            EmitInt32(0);

            foreach (var arg in arguments)
            {
                Emit(OpCode.Dup);
                if (arg is SpreadElement spreadArg)
                {
                    Visit(spreadArg.Argument);
                    Emit(OpCode.ArrayAppendSpread);
                }
                else
                {
                    Visit(arg);
                    Emit(OpCode.ArrayAppend);
                }
                Emit(OpCode.Pop);
            }
        }

        private void EmitDeleteExpression(Expression operand)
        {
            if (operand is Identifier)
            {
                Emit(OpCode.LoadFalse);
                return;
            }

            if (operand is MemberExpression memberExpr)
            {
                Visit(memberExpr.Object);
                int propConstIdx = AddConstant(FenValue.FromString(memberExpr.Property));
                Emit(OpCode.LoadConst);
                EmitInt32(propConstIdx);
                Emit(OpCode.DeleteProp);
                return;
            }

            if (operand is IndexExpression indexExpr)
            {
                Visit(indexExpr.Left);
                Visit(indexExpr.Index);
                Emit(OpCode.DeleteProp);
                return;
            }

            if (operand is OptionalChainExpression optionalChainExpr)
            {
                Visit(optionalChainExpr.Object);
                Emit(OpCode.Dup);
                Emit(OpCode.LoadNull);
                Emit(OpCode.StrictEqual);
                int jumpNullish = EmitJump(OpCode.JumpIfTrue);
                
                Emit(OpCode.Dup);
                Emit(OpCode.LoadUndefined);
                Emit(OpCode.StrictEqual);
                int jumpUndefined = EmitJump(OpCode.JumpIfTrue);

                if (optionalChainExpr.IsCall)
                {
                    int msgIdx = AddConstant(FenValue.FromString("SyntaxError: Invalid delete operand."));
                    Emit(OpCode.LoadConst);
                    EmitInt32(msgIdx);
                    Emit(OpCode.Throw);
                }
                else if (optionalChainExpr.IsComputed)
                {
                    if (optionalChainExpr.Property == null)
                    {
                        int msgIdx = AddConstant(FenValue.FromString("SyntaxError: Optional computed chain missing property expression."));
                        Emit(OpCode.LoadConst);
                        EmitInt32(msgIdx);
                        Emit(OpCode.Throw);
                    }
                    else
                    {
                        Visit(optionalChainExpr.Property);
                        Emit(OpCode.DeleteProp);
                    }
                }
                else
                {
                    int propertyConst = AddConstant(FenValue.FromString(optionalChainExpr.PropertyName ?? string.Empty));
                    Emit(OpCode.LoadConst);
                    EmitInt32(propertyConst);
                    Emit(OpCode.DeleteProp);
                }

                int jumpEnd = EmitJump(OpCode.Jump);
                
                int nullishTarget = _instructions.Count;
                PatchJumpTo(jumpNullish, nullishTarget);
                PatchJumpTo(jumpUndefined, nullishTarget);
                Emit(OpCode.Pop);
                Emit(OpCode.LoadTrue);
                
                int endTarget = _instructions.Count;
                PatchJumpTo(jumpEnd, endTarget);
                return;
            }

            // For non-reference operands, JavaScript delete returns true after evaluating
            // operand side effects.
            Visit(operand);
            Emit(OpCode.Pop);
            Emit(OpCode.LoadTrue);
        }

        private void EmitRegexLiteral(RegexLiteral regexLit)
        {
            var options = RegexOptions.None;
            if (regexLit.Flags != null && regexLit.Flags.Contains("i"))
            {
                options |= RegexOptions.IgnoreCase;
            }
            if (regexLit.Flags != null && regexLit.Flags.Contains("m"))
            {
                options |= RegexOptions.Multiline;
            }
            if (regexLit.Flags != null && regexLit.Flags.Contains("s"))
            {
                options |= RegexOptions.Singleline;
            }

            var rawPattern = regexLit.Pattern ?? string.Empty;
            var sanitizedPattern = SanitizeRegexPatternForDotNet(rawPattern, regexLit.Flags ?? string.Empty);

            try
            {
                var regex = new Regex(sanitizedPattern, options);
                var regexObject = new FenObject();
                regexObject.NativeObject = regex;
                regexObject.InternalClass = "RegExp";
                regexObject.Set("source", FenValue.FromString(rawPattern));
                regexObject.Set("flags", FenValue.FromString(regexLit.Flags ?? string.Empty));
                regexObject.Set("global", FenValue.FromBoolean(regexLit.Flags != null && regexLit.Flags.Contains("g")));
                regexObject.Set("ignoreCase", FenValue.FromBoolean(regexLit.Flags != null && regexLit.Flags.Contains("i")));
                regexObject.Set("multiline", FenValue.FromBoolean(regexLit.Flags != null && regexLit.Flags.Contains("m")));
                regexObject.Set("dotAll", FenValue.FromBoolean(regexLit.Flags != null && regexLit.Flags.Contains("s")));
                regexObject.Set("sticky", FenValue.FromBoolean(regexLit.Flags != null && regexLit.Flags.Contains("y")));
                regexObject.Set("lastIndex", FenValue.FromNumber(0));

                int idx = AddConstant(FenValue.FromObject(regexObject));
                Emit(OpCode.LoadConst);
                EmitInt32(idx);
            }
            catch (Exception ex)
            {
                int errorIdx = AddConstant(FenValue.FromError($"Invalid regular expression: {ex.Message}"));
                Emit(OpCode.LoadConst);
                EmitInt32(errorIdx);
            }
        }

        /// <summary>
        /// Converts JS Annex B regex escapes that .NET rejects into their identity-escape equivalents.
        /// Only applies when the unicode flag is absent (non-unicode mode).
        /// </summary>
        internal static string SanitizeRegexPatternForDotNet(string pattern, string flags)
        {
            if (string.IsNullOrEmpty(pattern)) return pattern;
            if (flags != null && flags.Contains("u")) return pattern; // unicode mode: don't relax

            // Collect named group names to validate \k<name> references
            var namedGroups = new System.Collections.Generic.HashSet<string>();
            var namedGroupRx = new Regex(@"\(\?<([A-Za-z_][A-Za-z0-9_]*)>");
            foreach (Match m in namedGroupRx.Matches(pattern))
                namedGroups.Add(m.Groups[1].Value);

            var sb = new System.Text.StringBuilder(pattern.Length);
            int i = 0;
            while (i < pattern.Length)
            {
                if (pattern[i] == '\\' && i + 1 < pattern.Length)
                {
                    char next = pattern[i + 1];
                    if (next == 'x')
                    {
                        // \xNN â€” valid only if followed by exactly 2 hex digits
                        bool valid = i + 3 < pattern.Length
                            && IsHexDigit(pattern[i + 2]) && IsHexDigit(pattern[i + 3]);
                        if (valid)
                        {
                            sb.Append('\\'); sb.Append('x'); i += 2;
                        }
                        else
                        {
                            // Annex B identity escape: \x â†’ x
                            sb.Append('x'); i += 2;
                        }
                    }
                    else if (next == 'u')
                    {
                        // \uNNNN â€” valid only if followed by exactly 4 hex digits
                        bool hasCurly = i + 2 < pattern.Length && pattern[i + 2] == '{';
                        bool valid4 = i + 5 < pattern.Length
                            && IsHexDigit(pattern[i + 2]) && IsHexDigit(pattern[i + 3])
                            && IsHexDigit(pattern[i + 4]) && IsHexDigit(pattern[i + 5]);
                        if (hasCurly || valid4)
                        {
                            sb.Append('\\'); sb.Append('u'); i += 2;
                        }
                        else
                        {
                            // Annex B identity escape: \u â†’ u
                            sb.Append('u'); i += 2;
                        }
                    }
                    else if (next == 'k' && i + 2 < pattern.Length && pattern[i + 2] == '<')
                    {
                        // \k<name> â€” invalid if named group doesn't exist
                        int closeIdx = pattern.IndexOf('>', i + 3);
                        if (closeIdx >= 0)
                        {
                            string groupName = pattern.Substring(i + 3, closeIdx - (i + 3));
                            if (!namedGroups.Contains(groupName))
                            {
                                // Identity escape: \k â†’ k, keep <name> as literal
                                sb.Append('k');
                                sb.Append('<');
                                sb.Append(groupName);
                                sb.Append('>');
                                i = closeIdx + 1;
                            }
                            else
                            {
                                sb.Append('\\'); sb.Append('k'); i += 2;
                            }
                        }
                        else
                        {
                            // No closing '>', treat as identity escape
                            sb.Append('k'); i += 2;
                        }
                    }
                    else
                    {
                        sb.Append(pattern[i]); sb.Append(pattern[i + 1]); i += 2;
                    }
                }
                else
                {
                    sb.Append(pattern[i]); i++;
                }
            }
            return sb.ToString();
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private void EmitImportMetaExpression()
        {
            Emit(OpCode.MakeObject);
            EmitInt32(0);

            Emit(OpCode.Dup);
            int urlKeyIdx = AddConstant(FenValue.FromString("url"));
            Emit(OpCode.LoadConst);
            EmitInt32(urlKeyIdx);

            int urlValueIdx = AddConstant(FenValue.FromString("file:///local/script.js"));
            Emit(OpCode.LoadConst);
            EmitInt32(urlValueIdx);

            Emit(OpCode.StoreProp);
            Emit(OpCode.Pop);
        }

        private void EmitIfExpression(IfExpression ifExpr)
        {
            Visit(ifExpr.Condition);
            int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

            EmitBlockAsExpression(ifExpr.Consequence, "IfExpression consequence");
            int jumpEndOffset = EmitJump(OpCode.Jump);

            PatchJump(jumpIfFalseOffset);
            if (ifExpr.Alternative != null)
            {
                EmitBlockAsExpression(ifExpr.Alternative, "IfExpression alternative");
            }
            else
            {
                Emit(OpCode.LoadNull);
            }

            PatchJump(jumpEndOffset);
        }

        private void EmitBlockAsExpression(BlockStatement block, string owner)
        {
            if (block == null || block.Statements.Count == 0)
            {
                Emit(OpCode.LoadNull);
                return;
            }

            for (int i = 0; i < block.Statements.Count - 1; i++)
            {
                Visit(block.Statements[i]);
            }

            var lastStatement = block.Statements[block.Statements.Count - 1];
            if (lastStatement is ExpressionStatement expressionStatement)
            {
                Visit(expressionStatement.Expression);
                return;
            }

            throw new FenSyntaxError($"Compiler: {owner} requires expression-bodied block in Bytecode Phase.");
        }

        private void VisitScopedStatementClause(Statement statement)
        {
            bool needsScope = statement is FunctionDeclarationStatement;
            if (needsScope)
            {
                Emit(OpCode.PushScope);
            }

            bool previousInsideBlock = _insideBlock;
            _insideBlock = needsScope || previousInsideBlock;
            Visit(statement);
            _insideBlock = previousInsideBlock;

            if (needsScope)
            {
                Emit(OpCode.PopScope);
            }
        }

        private void EmitWhileStatement(WhileStatement whileStmt, string labelName = null)
        {
            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext();
            var loopContext = PushLoopContext(loopStart);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;

            Visit(whileStmt.Condition);
            int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

            Visit(whileStmt.Body);

            Emit(OpCode.Jump);
            EmitInt32(loopStart);

            int loopEnd = _instructions.Count;
            PatchJumpTo(jumpIfFalseOffset, loopEnd);
            PatchBreakContext(breakContext, loopEnd);
            if (labelContext != null)
            {
                PatchLabelContext(labelContext, loopEnd);
                PopLabelContext(labelContext);
            }

            PopLoopContext(loopContext);
            PopBreakContext(breakContext);
        }

        private void EmitDoWhileStatement(DoWhileStatement doWhileStmt, string labelName = null)
        {
            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext();
            var loopContext = PushLoopContext(-1);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;

            Visit(doWhileStmt.Body);

            int conditionStart = _instructions.Count;
            SetLoopContinueTarget(loopContext, conditionStart);
            Visit(doWhileStmt.Condition);
            Emit(OpCode.JumpIfTrue);
            EmitInt32(loopStart);

            int loopEnd = _instructions.Count;
            PatchBreakContext(breakContext, loopEnd);
            if (labelContext != null)
            {
                PatchLabelContext(labelContext, loopEnd);
                PopLabelContext(labelContext);
            }

            PopLoopContext(loopContext);
            PopBreakContext(breakContext);
        }

        private void EmitForStatement(ForStatement forStmt, string labelName = null)
        {
            if (forStmt.Init != null)
            {
                Visit(forStmt.Init);
            }

            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext();
            var loopContext = PushLoopContext(-1);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;
            int jumpIfFalseOffset = -1;

            if (forStmt.Condition != null)
            {
                Visit(forStmt.Condition);
                jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
            }

            if (forStmt.Body != null)
            {
                Visit(forStmt.Body);
            }

            int continueTarget = forStmt.Update != null ? _instructions.Count : loopStart;
            SetLoopContinueTarget(loopContext, continueTarget);

            if (forStmt.Update != null)
            {
                Visit(forStmt.Update);
            }

            Emit(OpCode.Jump);
            EmitInt32(loopStart);

            int loopEnd = _instructions.Count;
            if (jumpIfFalseOffset != -1)
            {
                PatchJumpTo(jumpIfFalseOffset, loopEnd);
            }

            PatchBreakContext(breakContext, loopEnd);
            if (labelContext != null)
            {
                PatchLabelContext(labelContext, loopEnd);
                PopLabelContext(labelContext);
            }

            PopLoopContext(loopContext);
            PopBreakContext(breakContext);
        }

        private void EmitForInStatement(ForInStatement forInStmt, string labelName = null)
        {
            bool hasLexicalBinding = IsLexicalLoopBinding(forInStmt.BindingKind);
            List<string> loopBindingNames = hasLexicalBinding
                ? GetLoopBindingNames(forInStmt.Variable, forInStmt.DestructuringPattern)
                : null;

            if (hasLexicalBinding)
            {
                Emit(OpCode.PushScope);
                EmitLoopBindingTdzDeclarations(loopBindingNames);
            }

            Visit(forInStmt.Object);

            if (hasLexicalBinding)
            {
                Emit(OpCode.PopScope);
            }

            Emit(OpCode.MakeKeysIterator);

            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext(hasLexicalBinding ? 1 : 0);
            var loopContext = PushLoopContext(loopStart, hasLexicalBinding ? 1 : 0);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorMoveNext);
            int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorCurrent);

            if (hasLexicalBinding)
            {
                Emit(OpCode.PushScope);
                EmitLoopBindingTdzDeclarations(loopBindingNames);
            }

            if (forInStmt.Variable != null)
            {
                EmitForInOfIdentifierBinding(forInStmt.Variable.Value, forInStmt.BindingKind);
            }
            else if (forInStmt.DestructuringPattern != null)
            {
                string assignmentSource = NextSyntheticName("forin");
                EmitStoreVarByName(assignmentSource);
                Emit(OpCode.Pop); // pop the stored key value
                EmitForInOfBindingTarget(forInStmt.DestructuringPattern, assignmentSource);
            }
            else
            {
                Emit(OpCode.Pop); // no binding target, discard yielded key
            }

            Visit(forInStmt.Body);
            if (hasLexicalBinding)
            {
                Emit(OpCode.PopScope);
            }
            Emit(OpCode.Jump);
            EmitInt32(loopStart);

            int loopEnd = _instructions.Count;
            PatchJumpTo(jumpIfFalseOffset, loopEnd);
            Emit(OpCode.Pop); // Pop iterator

            PatchBreakContext(breakContext, loopEnd);
            if (labelContext != null)
            {
                PatchLabelContext(labelContext, loopEnd);
                PopLabelContext(labelContext);
            }

            PopLoopContext(loopContext);
            PopBreakContext(breakContext);
        }

        private void EmitForOfStatement(ForOfStatement forOfStmt, string labelName = null)
        {
            bool hasLexicalBinding = IsLexicalLoopBinding(forOfStmt.BindingKind);
            List<string> loopBindingNames = hasLexicalBinding
                ? GetLoopBindingNames(forOfStmt.Variable, forOfStmt.DestructuringPattern)
                : null;

            // ECMA-262 §14.7.5.10: for await..of uses async iteration protocol.
            bool isAsyncIteration = forOfStmt.IsAwait;

            if (hasLexicalBinding)
            {
                Emit(OpCode.PushScope);
                EmitLoopBindingTdzDeclarations(loopBindingNames);
            }

            Visit(forOfStmt.Iterable);

            if (hasLexicalBinding)
            {
                Emit(OpCode.PopScope);
            }

            // Choose sync or async iterator creation
            Emit(isAsyncIteration ? OpCode.MakeAsyncValuesIterator : OpCode.MakeValuesIterator);

            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext(hasLexicalBinding ? 1 : 0);
            var loopContext = PushLoopContext(loopStart, hasLexicalBinding ? 1 : 0);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;

            Emit(OpCode.Dup);
            // For async iteration: call .next() and await the promise before checking done
            int jumpIfFalseOffset;
            if (isAsyncIteration)
            {
                Emit(OpCode.IteratorAwaitMoveNext);
                jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
            }
            else
            {
                Emit(OpCode.IteratorMoveNext);
                jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
            }

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorCurrent);

            if (hasLexicalBinding)
            {
                Emit(OpCode.PushScope);
                EmitLoopBindingTdzDeclarations(loopBindingNames);
            }

            if (forOfStmt.Variable != null)
            {
                EmitForInOfIdentifierBinding(forOfStmt.Variable.Value, forOfStmt.BindingKind);
            }
            else if (forOfStmt.DestructuringPattern != null)
            {
                string assignmentSource = NextSyntheticName("forof");
                EmitStoreVarByName(assignmentSource);
                Emit(OpCode.Pop); // pop the stored value
                EmitForInOfBindingTarget(forOfStmt.DestructuringPattern, assignmentSource);
            }
            else
            {
                Emit(OpCode.Pop); // no binding target, discard yielded value
            }

            Visit(forOfStmt.Body);
            if (hasLexicalBinding)
            {
                Emit(OpCode.PopScope);
            }
            Emit(OpCode.Jump);
            EmitInt32(loopStart);

            int loopEnd = _instructions.Count;
            PatchJumpTo(jumpIfFalseOffset, loopEnd);
            Emit(OpCode.Pop); // Pop iterator

            PatchBreakContext(breakContext, loopEnd);
            if (labelContext != null)
            {
                PatchLabelContext(labelContext, loopEnd);
                PopLabelContext(labelContext);
            }

            PopLoopContext(loopContext);
            PopBreakContext(breakContext);
        }

        private void EmitForInOfBindingTarget(Expression target, string sourceVariableName)
        {
            if (target is ArrayLiteral || target is ObjectLiteral || target is AssignmentExpression)
            {
                EmitDestructuringFromVariable(target, sourceVariableName);
                return;
            }

            EmitDestructuringTargetBinding(target, sourceVariableName);
        }

        private void EmitSwitchStatement(SwitchStatement switchStmt)
        {
            Visit(switchStmt.Discriminant);

            var testJumpOffsets = new List<(int caseIndex, int jumpOffset)>();
            int defaultCaseIndex = -1;
            for (int i = 0; i < switchStmt.Cases.Count; i++)
            {
                var currentCase = switchStmt.Cases[i];
                if (currentCase.Test == null)
                {
                    defaultCaseIndex = i;
                    continue;
                }

                Emit(OpCode.Dup);
                Visit(currentCase.Test);
                Emit(OpCode.StrictEqual);
                int jumpToCasePre = EmitJump(OpCode.JumpIfTrue);
                testJumpOffsets.Add((i, jumpToCasePre));
            }

            int jumpAfterChecks = EmitJump(OpCode.Jump);

            int caseCount = switchStmt.Cases.Count;
            var preCaseTargets = new int[caseCount];
            var preToBodyJumps = new int[caseCount];
            for (int i = 0; i < caseCount; i++)
            {
                preCaseTargets[i] = _instructions.Count;
                Emit(OpCode.Pop); // drop switch discriminant before entering first selected case body
                preToBodyJumps[i] = EmitJump(OpCode.Jump);
            }

            int noMatchTarget = _instructions.Count;
            Emit(OpCode.Pop); // no case matched; discard discriminant
            int jumpNoMatchToEnd = EmitJump(OpCode.Jump);

            var switchBreakContext = PushBreakContext();
            var bodyTargets = new int[caseCount];
            for (int i = 0; i < caseCount; i++)
            {
                bodyTargets[i] = _instructions.Count;
                foreach (var stmt in switchStmt.Cases[i].Consequent)
                {
                    Visit(stmt);
                }
            }

            int switchEnd = _instructions.Count;
            PatchBreakContext(switchBreakContext, switchEnd);
            PopBreakContext(switchBreakContext);

            foreach (var check in testJumpOffsets)
            {
                PatchJumpTo(check.jumpOffset, preCaseTargets[check.caseIndex]);
            }

            if (defaultCaseIndex >= 0)
            {
                PatchJumpTo(jumpAfterChecks, preCaseTargets[defaultCaseIndex]);
            }
            else
            {
                PatchJumpTo(jumpAfterChecks, noMatchTarget);
            }

            PatchJumpTo(jumpNoMatchToEnd, switchEnd);
            for (int i = 0; i < caseCount; i++)
            {
                PatchJumpTo(preToBodyJumps[i], bodyTargets[i]);
            }
        }

        private void EmitScopeCleanup(int scopeCleanupDepth)
        {
            for (int i = 0; i < scopeCleanupDepth; i++)
            {
                Emit(OpCode.PopScope);
            }
        }

        private void EmitBreakStatement(BreakStatement breakStmt)
        {
            if (breakStmt.Label != null)
            {
                if (!TryFindLabelContext(breakStmt.Label.Value, out var labelContext))
                {
                    int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: break label '{breakStmt.Label.Value}' is not defined."));
                    Emit(OpCode.LoadConst);
                    EmitInt32(msgIdx);
                    Emit(OpCode.Throw);
                    return;
                }

                EmitScopeCleanup(labelContext.LoopContext?.ScopeCleanupDepth ?? 0);
                int labeledJumpOffset = EmitJump(OpCode.Jump);
                labelContext.BreakJumpOffsets.Add(labeledJumpOffset);
                return;
            }

            if (_breakContexts.Count == 0)
            {
                int msgIdx = AddConstant(FenValue.FromString("SyntaxError: break used outside loop or switch."));
                Emit(OpCode.LoadConst);
                EmitInt32(msgIdx);
                Emit(OpCode.Throw);
                return;
            }

            EmitScopeCleanup(_breakContexts.Peek().ScopeCleanupDepth);
            int jumpOffset = EmitJump(OpCode.Jump);
            _breakContexts.Peek().BreakJumpOffsets.Add(jumpOffset);
        }

        private void EmitContinueStatement(ContinueStatement continueStmt)
        {
            if (continueStmt.Label != null)
            {
                if (!TryFindLabelContext(continueStmt.Label.Value, out var labelContext))
                {
                    int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: continue label '{continueStmt.Label.Value}' is not defined."));
                    Emit(OpCode.LoadConst);
                    EmitInt32(msgIdx);
                    Emit(OpCode.Throw);
                    return;
                }

                if (labelContext.LoopContext == null)
                {
                    int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: continue label '{continueStmt.Label.Value}' does not reference a loop."));
                    Emit(OpCode.LoadConst);
                    EmitInt32(msgIdx);
                    Emit(OpCode.Throw);
                    return;
                }

                EmitScopeCleanup(labelContext.LoopContext?.ScopeCleanupDepth ?? 0);
                int labeledJumpOffset = EmitJump(OpCode.Jump);
                if (labelContext.LoopContext.ContinueTarget >= 0)
                {
                    PatchJumpTo(labeledJumpOffset, labelContext.LoopContext.ContinueTarget);
                }
                else
                {
                    labelContext.LoopContext.PendingContinueJumpOffsets.Add(labeledJumpOffset);
                }
                return;
            }

            if (_loopContexts.Count == 0)
            {
                int msgIdx = AddConstant(FenValue.FromString("SyntaxError: continue used outside loop."));
                Emit(OpCode.LoadConst);
                EmitInt32(msgIdx);
                Emit(OpCode.Throw);
                return;
            }

            var loopContext = _loopContexts.Peek();
            EmitScopeCleanup(loopContext.ScopeCleanupDepth);
            int jumpOffset = EmitJump(OpCode.Jump);
            if (loopContext.ContinueTarget >= 0)
            {
                PatchJumpTo(jumpOffset, loopContext.ContinueTarget);
            }
            else
            {
                loopContext.PendingContinueJumpOffsets.Add(jumpOffset);
            }
        }

        private void EmitLabeledStatement(LabeledStatement labeledStmt)
        {
            string labelName = labeledStmt?.Label?.Value;
            if (string.IsNullOrEmpty(labelName))
            {
                if (labeledStmt?.Body != null)
                {
                    Visit(labeledStmt.Body);
                }
                return;
            }

            if (labeledStmt.Body is WhileStatement labeledWhile)
            {
                EmitWhileStatement(labeledWhile, labelName);
                return;
            }

            if (labeledStmt.Body is DoWhileStatement labeledDoWhile)
            {
                EmitDoWhileStatement(labeledDoWhile, labelName);
                return;
            }

            if (labeledStmt.Body is ForStatement labeledFor)
            {
                EmitForStatement(labeledFor, labelName);
                return;
            }

            if (labeledStmt.Body is ForInStatement labeledForIn)
            {
                EmitForInStatement(labeledForIn, labelName);
                return;
            }

            if (labeledStmt.Body is ForOfStatement labeledForOf)
            {
                EmitForOfStatement(labeledForOf, labelName);
                return;
            }

            var labelContext = PushLabelContext(labelName, null);
            Visit(labeledStmt.Body);
            int statementEnd = _instructions.Count;
            PatchLabelContext(labelContext, statementEnd);
            PopLabelContext(labelContext);
        }

        private BreakContext PushBreakContext(int scopeCleanupDepth = 0)
        {
            var context = new BreakContext
            {
                ScopeCleanupDepth = scopeCleanupDepth
            };
            _breakContexts.Push(context);
            return context;
        }

        private void PatchBreakContext(BreakContext context, int target)
        {
            foreach (int jumpOffset in context.BreakJumpOffsets)
            {
                PatchJumpTo(jumpOffset, target);
            }
        }

        private void PopBreakContext(BreakContext expectedContext)
        {
            var popped = _breakContexts.Pop();
            if (!ReferenceEquals(popped, expectedContext))
            {
                throw new InvalidOperationException("Compiler: break context stack corruption detected.");
            }
        }

        private LoopContext PushLoopContext(int continueTarget, int scopeCleanupDepth = 0)
        {
            var context = new LoopContext
            {
                ContinueTarget = continueTarget,
                ScopeCleanupDepth = scopeCleanupDepth
            };
            _loopContexts.Push(context);
            return context;
        }

        private void SetLoopContinueTarget(LoopContext context, int continueTarget)
        {
            context.ContinueTarget = continueTarget;
            foreach (int pendingOffset in context.PendingContinueJumpOffsets)
            {
                PatchJumpTo(pendingOffset, continueTarget);
            }
            context.PendingContinueJumpOffsets.Clear();
        }

        private void PopLoopContext(LoopContext expectedContext)
        {
            var popped = _loopContexts.Pop();
            if (!ReferenceEquals(popped, expectedContext))
            {
                throw new InvalidOperationException("Compiler: loop context stack corruption detected.");
            }
        }

        private LabelContext PushLabelContext(string labelName, LoopContext loopContext)
        {
            var context = new LabelContext
            {
                Label = labelName,
                LoopContext = loopContext
            };
            _labelContexts.Push(context);
            return context;
        }

        private bool TryFindLabelContext(string labelName, out LabelContext context)
        {
            foreach (var candidate in _labelContexts)
            {
                if (string.Equals(candidate.Label, labelName, StringComparison.Ordinal))
                {
                    context = candidate;
                    return true;
                }
            }

            context = null;
            return false;
        }

        private void PatchLabelContext(LabelContext context, int target)
        {
            foreach (int jumpOffset in context.BreakJumpOffsets)
            {
                PatchJumpTo(jumpOffset, target);
            }
        }

        private void PopLabelContext(LabelContext expectedContext)
        {
            var popped = _labelContexts.Pop();
            if (!ReferenceEquals(popped, expectedContext))
            {
                throw new InvalidOperationException("Compiler: label context stack corruption detected.");
            }
        }

        private void EmitPrefixUpdate(Expression target, string updateOperator)
        {
            Visit(CreateUpdateAssignment(target, updateOperator));
        }

        private void EmitPostfixUpdate(Expression target, string updateOperator)
        {
            Visit(target);
            Visit(CreateUpdateAssignment(target, updateOperator));
            Emit(OpCode.Pop);
        }

        private AssignmentExpression CreateUpdateAssignment(Expression target, string updateOperator)
        {
            string arithmeticOp = updateOperator == "++" ? "+" : "-";
            return new AssignmentExpression
            {
                Left = target,
                Right = new InfixExpression
                {
                    Left = target,
                    Operator = arithmeticOp,
                    Right = new IntegerLiteral { Value = 1 }
                }
            };
        }

        private void VisitLogicalAssignment(LogicalAssignmentExpression logicalAssignExpr)
        {
            Visit(logicalAssignExpr.Left);

            int jumpKeepLeftOffset;
            switch (logicalAssignExpr.Operator)
            {
                case "||=":
                    Emit(OpCode.Dup);
                    jumpKeepLeftOffset = EmitJump(OpCode.JumpIfTrue);
                    break;
                case "&&=":
                    Emit(OpCode.Dup);
                    jumpKeepLeftOffset = EmitJump(OpCode.JumpIfFalse);
                    break;
                case "??=":
                    Emit(OpCode.Dup);
                    Emit(OpCode.LoadNull);
                    Emit(OpCode.Equal);
                    jumpKeepLeftOffset = EmitJump(OpCode.JumpIfFalse);
                    break;
                default:
                    int msgIdx = AddConstant(FenValue.FromString($"SyntaxError: Logical assignment operator '{logicalAssignExpr.Operator}' not supported."));
                    Emit(OpCode.LoadConst);
                    EmitInt32(msgIdx);
                    Emit(OpCode.Throw);
                    jumpKeepLeftOffset = EmitJump(OpCode.Jump);
                    break;
            }

            Emit(OpCode.Pop);
            Visit(new AssignmentExpression
            {
                Left = logicalAssignExpr.Left,
                Right = logicalAssignExpr.Right
            });

            PatchJump(jumpKeepLeftOffset);
        }

        private static AssignmentExpression LowerCompoundAssignment(CompoundAssignmentExpression compoundAssignExpr)
        {
            string op = compoundAssignExpr?.Operator ?? string.Empty;
            if (!op.EndsWith("=", StringComparison.Ordinal) || op.Length < 2)
            {
                throw new FenSyntaxError($"Compiler: Compound assignment operator '{op}' not supported.");
            }

            string infixOp = op.Substring(0, op.Length - 1);
            switch (infixOp)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                case "%":
                case "**":
                case "<<":
                case ">>":
                case ">>>":
                case "&":
                case "|":
                case "^":
                    break;
                default:
                    // Unrecognized compound operators will fall through to in-place operator eval,
                    // which emits a runtime SyntaxError instead of a compiler crash.
                    break;
            }

            return new AssignmentExpression
            {
                Left = compoundAssignExpr.Left,
                Right = new InfixExpression
                {
                    Left = compoundAssignExpr.Left,
                    Operator = infixOp,
                    Right = compoundAssignExpr.Right
                }
            };
        }

        private void EmitPrivateIdentifier(PrivateIdentifier privateIdentifier)
        {
            EmitLoadVarByName("this");
            int keyIdx = AddConstant(FenValue.FromString("#" + (privateIdentifier?.Name ?? string.Empty)));
            Emit(OpCode.LoadConst);
            EmitInt32(keyIdx);
            Emit(OpCode.LoadProp);
        }

        private void EmitYieldExpression(YieldExpression yieldExpression)
        {
            if (yieldExpression?.Value != null)
            {
                Visit(yieldExpression.Value);
            }
            else
            {
                Emit(OpCode.LoadUndefined);
            }
            // Emit Yield: suspends the generator and sends the value to the caller.
            // The value left on the stack after resumption is the argument passed to next().
            Emit(OpCode.Yield);
        }

        private void EmitMethodDefinition(MethodDefinition methodDefinition)
        {
            if (methodDefinition?.Value == null)
            {
                Emit(OpCode.LoadUndefined);
                return;
            }

            VisitFunctionWithInferredName(methodDefinition.Value, GetMethodInferredName(methodDefinition));
        }

        private void EmitClassProperty(ClassProperty classProperty)
        {
            if (classProperty?.Value != null)
            {
                VisitFunctionWithInferredName(classProperty.Value, GetClassPropertyInferredName(classProperty));
            }
            else
            {
                Emit(OpCode.LoadUndefined);
            }
        }

        private void EmitStaticBlock(StaticBlock staticBlock)
        {
            if (staticBlock?.Body != null)
            {
                Visit(staticBlock.Body);
            }
            else
            {
                Emit(OpCode.LoadUndefined);
            }
        }

        private void EmitWithStatement(WithStatement withStatement)
        {
            Visit(withStatement.Object);
            Emit(OpCode.EnterWith);

            if (withStatement.Body != null)
            {
                Visit(withStatement.Body);
            }

            Emit(OpCode.ExitWith);
        }

        private void EmitImportDeclaration(ImportDeclaration importDeclaration)
        {
            if (importDeclaration == null)
            {
                Emit(OpCode.LoadUndefined);
                return;
            }

            string moduleBindingName = GetModuleBindingName(importDeclaration.Source);
            if (importDeclaration.Specifiers == null || importDeclaration.Specifiers.Count == 0)
            {
                EmitLoadVarByName(moduleBindingName);
                Emit(OpCode.Pop);
                Emit(OpCode.LoadUndefined);
                return;
            }

            foreach (var specifier in importDeclaration.Specifiers)
            {
                string localName = specifier?.Local?.Value;
                if (string.IsNullOrEmpty(localName))
                {
                    continue;
                }

                if (string.Equals(specifier.Imported?.Value, "*", StringComparison.Ordinal))
                {
                    EmitLoadVarByName(moduleBindingName);
                    EmitStoreVarByName(localName);
                    Emit(OpCode.Pop);
                    continue;
                }

                string importedName = specifier?.Imported?.Value ?? "default";
                EmitLoadVarByName(moduleBindingName);
                int importedNameIdx = AddConstant(FenValue.FromString(importedName));
                Emit(OpCode.LoadConst);
                EmitInt32(importedNameIdx);
                Emit(OpCode.LoadProp);
                EmitStoreVarByName(localName);
                Emit(OpCode.Pop);
            }

            Emit(OpCode.LoadUndefined);
        }

        private void EmitExportDeclaration(ExportDeclaration exportDeclaration)
        {
            if (exportDeclaration == null)
            {
                Emit(OpCode.LoadUndefined);
                return;
            }

            if (exportDeclaration.DefaultExpression != null)
            {
                if (CanUseInferredName(exportDeclaration.DefaultExpression))
                {
                    VisitWithInferredName(exportDeclaration.DefaultExpression, "default");
                }
                else
                {
                    Visit(exportDeclaration.DefaultExpression);
                }
                string defaultLocalBinding = GetDefaultExportLocalBindingName(exportDeclaration.DefaultExpression);
                if (!string.IsNullOrEmpty(defaultLocalBinding))
                {
                    EmitStoreVarByName(defaultLocalBinding);
                }
                EmitStoreVarByName(GetExportBindingName("default"));
                Emit(OpCode.Pop);
                Emit(OpCode.LoadUndefined);
                return;
            }

            if (exportDeclaration.Declaration != null)
            {
                Visit(exportDeclaration.Declaration);

                if (exportDeclaration.Declaration is LetStatement letStatement && letStatement.Name != null)
                {
                    EmitCopyVariableToExport(letStatement.Name.Value, letStatement.Name.Value);
                }
                else if (exportDeclaration.Declaration is ClassStatement classStatement && classStatement.Name != null)
                {
                    EmitCopyVariableToExport(classStatement.Name.Value, classStatement.Name.Value);
                }
                else if (exportDeclaration.Declaration is FunctionDeclarationStatement functionDeclaration &&
                         !string.IsNullOrEmpty(functionDeclaration.Function?.Name))
                {
                    EmitCopyVariableToExport(functionDeclaration.Function.Name, functionDeclaration.Function.Name);
                }

                Emit(OpCode.LoadUndefined);
                return;
            }

            if (exportDeclaration.Specifiers != null && exportDeclaration.Specifiers.Count > 0)
            {
                if (!string.IsNullOrEmpty(exportDeclaration.Source))
                {
                    string moduleBindingName = GetModuleBindingName(exportDeclaration.Source);
                    foreach (var specifier in exportDeclaration.Specifiers)
                    {
                        string localName = specifier?.Local?.Value;
                        if (string.IsNullOrEmpty(localName))
                        {
                            continue;
                        }

                        string exportedName = specifier.Exported?.Value ?? localName;
                        if (localName == "*" && (specifier.Exported == null || exportedName == "*"))
                        {
                            // export * aggregation is not representable without runtime module iteration support.
                            continue;
                        }

                        if (localName == "*")
                        {
                            EmitLoadVarByName(moduleBindingName);
                            EmitStoreVarByName(GetExportBindingName(exportedName));
                            Emit(OpCode.Pop);
                            continue;
                        }

                        EmitLoadVarByName(moduleBindingName);
                        int localIdx = AddConstant(FenValue.FromString(localName));
                        Emit(OpCode.LoadConst);
                        EmitInt32(localIdx);
                        Emit(OpCode.LoadProp);
                        EmitStoreVarByName(GetExportBindingName(exportedName));
                        Emit(OpCode.Pop);
                    }
                }
                else
                {
                    foreach (var specifier in exportDeclaration.Specifiers)
                    {
                        string localName = specifier?.Local?.Value;
                        if (string.IsNullOrEmpty(localName))
                        {
                            continue;
                        }

                        string exportedName = specifier.Exported?.Value ?? localName;
                        EmitCopyVariableToExport(localName, exportedName);
                    }
                }
            }

            Emit(OpCode.LoadUndefined);
        }

        private static string GetDefaultExportLocalBindingName(Expression defaultExpression)
        {
            return defaultExpression switch
            {
                FunctionLiteral functionLiteral when !string.IsNullOrEmpty(functionLiteral.Name) => functionLiteral.Name,
                AsyncFunctionExpression asyncFunctionExpression when asyncFunctionExpression.Name != null && !string.IsNullOrEmpty(asyncFunctionExpression.Name.Value) => asyncFunctionExpression.Name.Value,
                ClassExpression classExpression when classExpression.Name != null && !string.IsNullOrEmpty(classExpression.Name.Value) => classExpression.Name.Value,
                _ => null
            };
        }

        private void EmitClassExpression(ClassExpression classExpression)
        {
            if (classExpression == null)
            {
                Emit(OpCode.LoadUndefined);
                return;
            }

            string className = classExpression.Name?.Value;
            if (string.IsNullOrEmpty(className))
            {
                className = !string.IsNullOrEmpty(_currentInferredName) && CanInferAnonymousClassName(classExpression)
                    ? _currentInferredName
                    : NextSyntheticName("class_expr");
            }

            var loweredStatement = new ClassStatement
            {
                Token = classExpression.Token,
                Name = new Identifier(classExpression.Token, className),
                SuperClass = classExpression.SuperClass,
                Methods = classExpression.Methods ?? new List<MethodDefinition>(),
                Properties = classExpression.Properties ?? new List<ClassProperty>(),
                StaticBlocks = classExpression.StaticBlocks ?? new List<StaticBlock>(),
                Decorators = classExpression.Decorators ?? new List<Decorator>()
            };

            EmitClassStatement(loweredStatement, emitResultValue: true);
        }

        private void EmitClassStatement(ClassStatement classStatement, bool emitResultValue)
        {
            string className = classStatement?.Name?.Value;
            if (string.IsNullOrEmpty(className))
            {
                className = NextSyntheticName("anonymous_class");
            }

            var constructorFunction = BuildClassConstructorFunction(classStatement);
            Visit(constructorFunction);
            EmitStoreVarByName(className);
            Emit(OpCode.Pop);

            if (classStatement.SuperClass != null && !string.IsNullOrEmpty(classStatement.SuperClass.Value))
            {
                // Basic inheritance wiring: class.prototype = superClass.prototype
                // (does not yet preserve a distinct prototype object chain).
                EmitLoadVarByName(className);
                int prototypeKeyIdx = AddConstant(FenValue.FromString("prototype"));
                Emit(OpCode.LoadConst);
                EmitInt32(prototypeKeyIdx);
                EmitLoadVarByName(classStatement.SuperClass.Value);
                Emit(OpCode.LoadConst);
                EmitInt32(prototypeKeyIdx);
                Emit(OpCode.LoadProp);
                Emit(OpCode.StoreProp);
                Emit(OpCode.Pop);
            }

            if (classStatement.Methods != null)
            {
                foreach (var method in classStatement.Methods)
                {
                    if (method == null || string.Equals(method.Kind, "constructor", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    EmitInstallClassMethod(className, method);
                }
            }

            if (classStatement.Properties != null)
            {
                foreach (var classProperty in classStatement.Properties)
                {
                    if (classProperty == null || !classProperty.Static)
                    {
                        continue;
                    }

                    EmitInstallClassStaticProperty(className, classProperty);
                }
            }

            if (classStatement.StaticBlocks != null)
            {
                foreach (var staticBlock in classStatement.StaticBlocks)
                {
                    if (staticBlock?.Body == null)
                    {
                        continue;
                    }

                    string savedThisVarName = NextSyntheticName("saved_this");
                    EmitLoadVarByName("this");
                    EmitStoreVarByName(savedThisVarName);
                    Emit(OpCode.Pop);

                    EmitLoadVarByName(className);
                    EmitStoreVarByName("this");
                    Emit(OpCode.Pop);

                    Visit(staticBlock.Body);

                    EmitLoadVarByName(savedThisVarName);
                    EmitStoreVarByName("this");
                    Emit(OpCode.Pop);
                }
            }

            if (emitResultValue)
            {
                EmitLoadVarByName(className);
            }
        }

        private FunctionLiteral BuildClassConstructorFunction(ClassStatement classStatement)
        {
            MethodDefinition constructorMethod = null;
            if (classStatement.Methods != null)
            {
                foreach (var method in classStatement.Methods)
                {
                    if (method != null && string.Equals(method.Kind, "constructor", StringComparison.Ordinal))
                    {
                        constructorMethod = method;
                        break;
                    }
                }
            }

            var constructorFunction = new FunctionLiteral
            {
                Token = classStatement.Token,
                Name = classStatement.Name?.Value,
                Parameters = constructorMethod?.Value?.Parameters != null
                    ? new List<Identifier>(constructorMethod.Value.Parameters)
                    : new List<Identifier>(),
                Body = new BlockStatement()
            };

            AppendInstanceFieldInitializers(classStatement, constructorFunction.Body);

            var sourceBody = constructorMethod?.Value?.Body;
            if (sourceBody?.Statements != null)
            {
                foreach (var statement in sourceBody.Statements)
                {
                    constructorFunction.Body.Statements.Add(statement);
                }
            }

            return constructorFunction;
        }

        private void AppendInstanceFieldInitializers(ClassStatement classStatement, BlockStatement constructorBody)
        {
            if (classStatement?.Properties == null || constructorBody == null)
            {
                return;
            }

            foreach (var classProperty in classStatement.Properties)
            {
                if (classProperty == null || classProperty.Static)
                {
                    continue;
                }

                string fieldName = classProperty.IsPrivate
                    ? "#" + (classProperty.Key?.Value ?? string.Empty)
                    : (classProperty.Key?.Value ?? string.Empty);

                var fieldAssignment = new AssignmentExpression
                {
                    Left = new MemberExpression
                    {
                        Object = new Identifier(classStatement.Token, "this"),
                        Property = fieldName
                    },
                    Right = classProperty.Value ?? new UndefinedLiteral()
                };

                constructorBody.Statements.Add(new ExpressionStatement
                {
                    Expression = fieldAssignment
                });
            }
        }

        private void EmitInstallClassMethod(string className, MethodDefinition methodDefinition)
        {
            if (methodDefinition?.Value == null || methodDefinition.Key == null)
            {
                return;
            }

            string targetVariable = NextSyntheticName("class_method_target");
            if (methodDefinition.Static)
            {
                EmitLoadVarByName(className);
            }
            else
            {
                EmitLoadVarByName(className);
                int prototypeKeyIdx = AddConstant(FenValue.FromString("prototype"));
                Emit(OpCode.LoadConst);
                EmitInt32(prototypeKeyIdx);
                Emit(OpCode.LoadProp);
            }
            EmitStoreVarByName(targetVariable);
            Emit(OpCode.Pop);

            string methodVariable = NextSyntheticName("class_method_value");
            VisitFunctionWithInferredName(methodDefinition.Value, GetMethodInferredName(methodDefinition));
            EmitStoreVarByName(methodVariable);
            Emit(OpCode.Pop);

            EmitLoadVarByName(methodVariable);
            EmitLoadVarByName(targetVariable);
            Emit(OpCode.SetFunctionHomeObject);
            EmitStoreVarByName(methodVariable);
            Emit(OpCode.Pop);

            if (string.Equals(methodDefinition.Kind, "get", StringComparison.Ordinal) ||
                string.Equals(methodDefinition.Kind, "set", StringComparison.Ordinal))
            {
                EmitDefineAccessorProperty(targetVariable, methodDefinition, methodVariable, enumerable: false);
                return;
            }

            EmitDefineMethodProperty(targetVariable, methodDefinition, methodVariable, enumerable: false);
        }

        private void EmitInstallClassStaticProperty(string className, ClassProperty classProperty)
        {
            if (classProperty == null)
            {
                return;
            }

            string valueVariable = NextSyntheticName("class_static_prop");
            if (classProperty.Value != null)
            {
                VisitFunctionWithInferredName(classProperty.Value, GetClassPropertyInferredName(classProperty));
            }
            else
            {
                Emit(OpCode.LoadUndefined);
            }
            EmitStoreVarByName(valueVariable);
            Emit(OpCode.Pop);

            EmitDefineClassProperty(className, classProperty, valueVariable, enumerable: true);
        }

        private void EmitDefineMethodProperty(string objectVariableName, MethodDefinition methodDefinition, string valueVariableName, bool enumerable)
        {
            if (methodDefinition == null)
            {
                return;
            }

            string propertyName = methodDefinition.IsPrivate
                ? "#" + (methodDefinition.Key?.Value ?? string.Empty)
                : (methodDefinition.Key?.Value ?? string.Empty);

            if (methodDefinition.Computed && methodDefinition.ComputedKeyExpression != null)
            {
                string keyVariable = NextSyntheticName("computed_method_key");
                StoreExpressionInVariable(methodDefinition.ComputedKeyExpression, keyVariable);
                EmitDefineDataPropertyByVariable(objectVariableName, keyVariable, valueVariableName, enumerable);
                return;
            }

            EmitDefineDataProperty(objectVariableName, propertyName, valueVariableName, enumerable);
        }

        private void EmitDefineClassProperty(string objectVariableName, ClassProperty classProperty, string valueVariableName, bool enumerable)
        {
            if (classProperty == null)
            {
                return;
            }

            string propertyName = classProperty.IsPrivate
                ? "#" + (classProperty.Key?.Value ?? string.Empty)
                : (classProperty.Key?.Value ?? string.Empty);

            if (classProperty.ComputedKeyExpression != null)
            {
                string keyVariable = NextSyntheticName("computed_class_prop_key");
                StoreExpressionInVariable(classProperty.ComputedKeyExpression, keyVariable);
                EmitDefineDataPropertyByVariable(objectVariableName, keyVariable, valueVariableName, enumerable);
                return;
            }

            EmitDefineDataProperty(objectVariableName, propertyName, valueVariableName, enumerable);
        }

        private void EmitDefineDataProperty(string objectVariableName, string propertyName, string valueVariableName, bool enumerable)
        {
            if (string.IsNullOrEmpty(objectVariableName) || string.IsNullOrEmpty(propertyName) || string.IsNullOrEmpty(valueVariableName))
            {
                return;
            }

            string descriptorVariable = NextSyntheticName("prop_desc");
            Emit(OpCode.MakeObject);
            EmitInt32(0);
            EmitStoreVarByName(descriptorVariable);
            Emit(OpCode.Pop);

            EmitStoreDescriptorField(descriptorVariable, "value", () => EmitLoadVarByName(valueVariableName));
            EmitStoreDescriptorField(descriptorVariable, "writable", () => Emit(OpCode.LoadTrue));
            EmitStoreDescriptorField(descriptorVariable, "enumerable", () => Emit(enumerable ? OpCode.LoadTrue : OpCode.LoadFalse));
            EmitStoreDescriptorField(descriptorVariable, "configurable", () => Emit(OpCode.LoadTrue));

            EmitLoadVarByName("Object");
            Emit(OpCode.Dup);
            int definePropertyKeyIdx = AddConstant(FenValue.FromString("defineProperty"));
            Emit(OpCode.LoadConst);
            EmitInt32(definePropertyKeyIdx);
            Emit(OpCode.LoadProp);
            EmitLoadVarByName(objectVariableName);
            int propertyNameIdx = AddConstant(FenValue.FromString(propertyName));
            Emit(OpCode.LoadConst);
            EmitInt32(propertyNameIdx);
            EmitLoadVarByName(descriptorVariable);
            Emit(OpCode.CallMethod);
            EmitInt32(3);
            Emit(OpCode.Pop);
        }

        private void EmitDefineDataPropertyByVariable(string objectVariableName, string propertyKeyVariableName, string valueVariableName, bool enumerable)
        {
            if (string.IsNullOrEmpty(objectVariableName) || string.IsNullOrEmpty(propertyKeyVariableName) || string.IsNullOrEmpty(valueVariableName))
            {
                return;
            }

            string descriptorVariable = NextSyntheticName("prop_desc");
            Emit(OpCode.MakeObject);
            EmitInt32(0);
            EmitStoreVarByName(descriptorVariable);
            Emit(OpCode.Pop);

            EmitStoreDescriptorField(descriptorVariable, "value", () => EmitLoadVarByName(valueVariableName));
            EmitStoreDescriptorField(descriptorVariable, "writable", () => Emit(OpCode.LoadTrue));
            EmitStoreDescriptorField(descriptorVariable, "enumerable", () => Emit(enumerable ? OpCode.LoadTrue : OpCode.LoadFalse));
            EmitStoreDescriptorField(descriptorVariable, "configurable", () => Emit(OpCode.LoadTrue));

            EmitLoadVarByName("Object");
            Emit(OpCode.Dup);
            int definePropertyKeyIdx = AddConstant(FenValue.FromString("defineProperty"));
            Emit(OpCode.LoadConst);
            EmitInt32(definePropertyKeyIdx);
            Emit(OpCode.LoadProp);
            EmitLoadVarByName(objectVariableName);
            EmitLoadVarByName(propertyKeyVariableName);
            EmitLoadVarByName(descriptorVariable);
            Emit(OpCode.CallMethod);
            EmitInt32(3);
            Emit(OpCode.Pop);
        }

        private void EmitDefineAccessorProperty(string objectVariableName, MethodDefinition methodDefinition, string accessorFunctionVariableName, bool enumerable)
        {
            if (string.IsNullOrEmpty(objectVariableName) || methodDefinition == null || string.IsNullOrEmpty(accessorFunctionVariableName))
            {
                return;
            }

            string descriptorVariable = NextSyntheticName("accessor_desc");
            Emit(OpCode.MakeObject);
            EmitInt32(0);
            EmitStoreVarByName(descriptorVariable);
            Emit(OpCode.Pop);

            if (string.Equals(methodDefinition.Kind, "get", StringComparison.Ordinal))
            {
                EmitStoreDescriptorField(descriptorVariable, "get", () => EmitLoadVarByName(accessorFunctionVariableName));
            }
            else
            {
                EmitStoreDescriptorField(descriptorVariable, "set", () => EmitLoadVarByName(accessorFunctionVariableName));
            }
            EmitStoreDescriptorField(descriptorVariable, "enumerable", () => Emit(enumerable ? OpCode.LoadTrue : OpCode.LoadFalse));
            EmitStoreDescriptorField(descriptorVariable, "configurable", () => Emit(OpCode.LoadTrue));

            EmitLoadVarByName("Object");
            Emit(OpCode.Dup);
            int definePropertyKeyIdx = AddConstant(FenValue.FromString("defineProperty"));
            Emit(OpCode.LoadConst);
            EmitInt32(definePropertyKeyIdx);
            Emit(OpCode.LoadProp);
            EmitLoadVarByName(objectVariableName);

            if (methodDefinition.Computed && methodDefinition.ComputedKeyExpression != null)
            {
                Visit(methodDefinition.ComputedKeyExpression);
            }
            else
            {
                string propertyName = methodDefinition.IsPrivate
                    ? "#" + (methodDefinition.Key?.Value ?? string.Empty)
                    : (methodDefinition.Key?.Value ?? string.Empty);
                int propertyNameIdx = AddConstant(FenValue.FromString(propertyName));
                Emit(OpCode.LoadConst);
                EmitInt32(propertyNameIdx);
            }

            EmitLoadVarByName(descriptorVariable);
            Emit(OpCode.CallMethod);
            EmitInt32(3);
            Emit(OpCode.Pop);
        }

        private void EmitStoreDescriptorField(string descriptorVariable, string fieldName, Action emitValue)
        {
            EmitLoadVarByName(descriptorVariable);
            int fieldKeyIdx = AddConstant(FenValue.FromString(fieldName ?? string.Empty));
            Emit(OpCode.LoadConst);
            EmitInt32(fieldKeyIdx);
            emitValue?.Invoke();
            Emit(OpCode.StoreProp);
            Emit(OpCode.Pop);
        }

        private void EmitCopyVariableToExport(string localName, string exportedName)
        {
            if (string.IsNullOrEmpty(localName) || string.IsNullOrEmpty(exportedName))
            {
                return;
            }

            EmitLoadVarByName(localName);
            EmitStoreVarByName(GetExportBindingName(exportedName));
            Emit(OpCode.Pop);
        }

        private void EmitLoadVarByName(string variableName)
        {
            if (TryGetLocalSlot(variableName, out int slotIndex))
            {
                Emit(OpCode.LoadLocal);
                EmitInt32(slotIndex);
                return;
            }

            int idx = AddConstant(FenValue.FromString(variableName ?? string.Empty));
            Emit(OpCode.LoadVar);
            EmitInt32(idx);
        }

        private void EmitStoreVarByName(string variableName)
        {
            if (TryGetLocalSlot(variableName, out int slotIndex))
            {
                Emit(OpCode.StoreLocal);
                EmitInt32(slotIndex);
                return;
            }

            int idx = AddConstant(FenValue.FromString(variableName ?? string.Empty));
            Emit(OpCode.StoreVar);
            EmitInt32(idx);
        }

        private void EmitStoreVarDeclarationByName(string variableName)
        {
            if (TryGetLocalSlot(variableName, out int slotIndex))
            {
                Emit(OpCode.StoreLocalDeclaration);
                EmitInt32(slotIndex);
                return;
            }

            int idx = AddConstant(FenValue.FromString(variableName ?? string.Empty));
            Emit(OpCode.StoreVarDeclaration);
            EmitInt32(idx);
        }

        private void EmitDeclareTdzByName(string variableName)
        {
            int idx = AddConstant(FenValue.FromString(variableName ?? string.Empty));
            Emit(OpCode.DeclareTdz);
            EmitInt32(idx);
        }

        private void EmitDeclareVarByName(string variableName)
        {
            int idx = AddConstant(FenValue.FromString(variableName ?? string.Empty));
            Emit(OpCode.DeclareVar);
            EmitInt32(idx);
        }

        /// <summary>
        /// Emit an assignment (not declaration): walks the scope chain to update an existing binding,
        /// falling back to implicit global creation if not found.
        /// Use this for x = value (AssignmentExpression), NOT for var/let/const declarations.
        /// </summary>
        private void EmitUpdateVarByName(string variableName)
        {
            // Local slots are always in the current frame's environment â€” StoreLocal is correct for assignment too.
            if (TryGetLocalSlot(variableName, out int slotIndex))
            {
                Emit(OpCode.StoreLocal);
                EmitInt32(slotIndex);
                return;
            }

            int idx = AddConstant(FenValue.FromString(variableName ?? string.Empty));
            Emit(OpCode.UpdateVar);
            EmitInt32(idx);
        }

        private bool TryGetLocalSlot(string variableName, out int slotIndex)
        {
            slotIndex = -1;
            if (!_enableLocalSlots || string.IsNullOrEmpty(variableName) || !_localBindings.Contains(variableName))
            {
                return false;
            }

            if (_localSlotByName.TryGetValue(variableName, out slotIndex))
            {
                return true;
            }

            slotIndex = _localSlotNames.Count;
            _localSlotByName[variableName] = slotIndex;
            _localSlotNames.Add(variableName);
            return true;
        }

        private void InitializeLocalBindings(AstNode root)
        {
            AddLocalBinding("this");
            AddLocalBinding("arguments");

            if (!string.IsNullOrEmpty(_functionName))
            {
                AddLocalBinding(_functionName);
            }

            if (_functionParameters != null)
            {
                foreach (var parameter in _functionParameters)
                {
                    if (parameter == null)
                    {
                        continue;
                    }

                    AddLocalBinding(parameter.Value);
                    if (parameter.DestructuringPattern != null)
                    {
                        CollectBindingNamesFromPattern(parameter.DestructuringPattern);
                    }
                }
            }

            CollectLocalBindings(root);
        }

        private static bool IsLexicalLoopBinding(DeclarationKind? bindingKind)
        {
            return bindingKind == DeclarationKind.Let || bindingKind == DeclarationKind.Const;
        }

        private static void CollectPatternBindingNames(Expression pattern, HashSet<string> names)
        {
            if (pattern == null || names == null)
            {
                return;
            }

            switch (pattern)
            {
                case Identifier identifier when !string.IsNullOrEmpty(identifier.Value):
                    names.Add(identifier.Value);
                    break;
                case AssignmentExpression assignmentExpression:
                    CollectPatternBindingNames(assignmentExpression.Left, names);
                    break;
                case SpreadElement spreadElement:
                    CollectPatternBindingNames(spreadElement.Argument, names);
                    break;
                case ArrayLiteral arrayLiteral:
                    foreach (var element in arrayLiteral.Elements)
                    {
                        CollectPatternBindingNames(element, names);
                    }
                    break;
                case ObjectLiteral objectLiteral:
                    foreach (var pair in objectLiteral.Pairs)
                    {
                        CollectPatternBindingNames(pair.Value, names);
                    }
                    break;
            }
        }

        private static List<string> GetLoopBindingNames(Identifier variable, Expression destructuringPattern)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (variable != null && !string.IsNullOrEmpty(variable.Value))
            {
                names.Add(variable.Value);
            }
            else
            {
                CollectPatternBindingNames(destructuringPattern, names);
            }

            return new List<string>(names);
        }

        private void EmitLoopBindingTdzDeclarations(List<string> bindingNames)
        {
            if (bindingNames == null)
            {
                return;
            }

            foreach (var bindingName in bindingNames)
            {
                EmitDeclareTdzByName(bindingName);
            }
        }

        private void EmitForInOfIdentifierBinding(string variableName, DeclarationKind? bindingKind)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                Emit(OpCode.Pop);
                return;
            }

            if (bindingKind == DeclarationKind.Var)
            {
                EmitStoreVarDeclarationByName(variableName);
            }
            else if (bindingKind == DeclarationKind.Let || bindingKind == DeclarationKind.Const)
            {
                EmitStoreVarByName(variableName);
            }
            else
            {
                EmitUpdateVarByName(variableName);
            }

            Emit(OpCode.Pop);
        }

        private void CollectLocalBindings(AstNode node)
        {
            if (node == null)
            {
                return;
            }

            switch (node)
            {
                case Program program:
                    foreach (var statement in program.Statements)
                    {
                        CollectLocalBindings(statement);
                    }
                    break;
                case BlockStatement block:
                    foreach (var statement in block.Statements)
                    {
                        CollectLocalBindings(statement);
                    }
                    break;
                case LetStatement letStatement:
                    AddLocalBinding(letStatement.Name?.Value);
                    CollectBindingNamesFromPattern(letStatement.DestructuringPattern);
                    break;
                case FunctionDeclarationStatement functionDeclaration:
                    AddLocalBinding(functionDeclaration.Function?.Name);
                    break;
                case ForStatement forStatement:
                    CollectLocalBindings(forStatement.Init);
                    CollectLocalBindings(forStatement.Body);
                    break;
                case ForInStatement forInStatement:
                    if (!IsLexicalLoopBinding(forInStatement.BindingKind))
                    {
                        AddLocalBinding(forInStatement.Variable?.Value);
                        CollectBindingNamesFromPattern(forInStatement.DestructuringPattern);
                    }
                    CollectLocalBindings(forInStatement.Body);
                    break;
                case ForOfStatement forOfStatement:
                    if (!IsLexicalLoopBinding(forOfStatement.BindingKind))
                    {
                        AddLocalBinding(forOfStatement.Variable?.Value);
                        CollectBindingNamesFromPattern(forOfStatement.DestructuringPattern);
                    }
                    CollectLocalBindings(forOfStatement.Body);
                    break;
                case WhileStatement whileStatement:
                    CollectLocalBindings(whileStatement.Body);
                    break;
                case DoWhileStatement doWhileStatement:
                    CollectLocalBindings(doWhileStatement.Body);
                    break;
                case IfStatement ifStatement:
                    CollectLocalBindings(ifStatement.Consequence);
                    CollectLocalBindings(ifStatement.Alternative);
                    break;
                case LabeledStatement labeledStatement:
                    CollectLocalBindings(labeledStatement.Body);
                    break;
                case SwitchStatement switchStatement:
                    if (switchStatement.Cases != null)
                    {
                        foreach (var switchCase in switchStatement.Cases)
                        {
                            if (switchCase?.Consequent == null)
                            {
                                continue;
                            }

                            foreach (var statement in switchCase.Consequent)
                            {
                                CollectLocalBindings(statement);
                            }
                        }
                    }
                    break;
                case TryStatement tryStatement:
                    AddLocalBinding(tryStatement.CatchParameter?.Value);
                    CollectBindingNamesFromPattern(tryStatement.CatchParameter?.DestructuringPattern);
                    CollectLocalBindings(tryStatement.Block);
                    CollectLocalBindings(tryStatement.CatchBlock);
                    CollectLocalBindings(tryStatement.FinallyBlock);
                    break;
                case ImportDeclaration importDeclaration:
                    if (importDeclaration.Specifiers != null)
                    {
                        foreach (var specifier in importDeclaration.Specifiers)
                        {
                            AddLocalBinding(specifier?.Local?.Value);
                        }
                    }
                    break;
                case ExportDeclaration exportDeclaration:
                    CollectLocalBindings(exportDeclaration.Declaration);
                    break;
                case ClassStatement classStatement:
                    AddLocalBinding(classStatement.Name?.Value);
                    break;
            }
        }

        private void CollectBindingNamesFromPattern(Expression pattern)
        {
            if (pattern == null)
            {
                return;
            }

            switch (pattern)
            {
                case Identifier identifier:
                    AddLocalBinding(identifier.Value);
                    break;
                case AssignmentExpression assignmentExpression:
                    CollectBindingNamesFromPattern(assignmentExpression.Left);
                    break;
                case ArrayLiteral arrayLiteral:
                    if (arrayLiteral.Elements != null)
                    {
                        foreach (var element in arrayLiteral.Elements)
                        {
                            if (element is SpreadElement spreadElement)
                            {
                                CollectBindingNamesFromPattern(spreadElement.Argument);
                            }
                            else
                            {
                                CollectBindingNamesFromPattern(element);
                            }
                        }
                    }
                    break;
                case ObjectLiteral objectLiteral:
                    if (objectLiteral.Pairs != null)
                    {
                        foreach (var pair in objectLiteral.Pairs)
                        {
                            if (pair.Value is SpreadElement spreadElement)
                            {
                                CollectBindingNamesFromPattern(spreadElement.Argument);
                            }
                            else
                            {
                                CollectBindingNamesFromPattern(pair.Value);
                            }
                        }
                    }
                    break;
            }
        }

        private void AddLocalBinding(string variableName)
        {
            if (!_enableLocalSlots || string.IsNullOrEmpty(variableName))
            {
                return;
            }

            _localBindings.Add(variableName);
        }

        private static string GetModuleBindingName(string source)
        {
            return "__fen_module_" + (source ?? string.Empty);
        }

        private static string GetExportBindingName(string exportName)
        {
            return "__fen_export_" + (exportName ?? string.Empty);
        }

        private string NextSyntheticName(string prefix)
        {
            string safePrefix = string.IsNullOrEmpty(prefix) ? "tmp" : prefix;
            string name = "__fenbc_" + safePrefix + "_" + _syntheticNameCounter++;
            AddLocalBinding(name);
            return name;
        }

        private void EmitDestructuringAssignmentExpression(Expression pattern, Expression sourceExpression)
        {
            string sourceVariable = NextSyntheticName("assign_destructure");
            StoreExpressionInVariable(sourceExpression, sourceVariable);
            EmitDestructuringFromVariable(pattern, sourceVariable);
            EmitLoadVarByName(sourceVariable);
        }

        private void EmitDestructuringBinding(Expression pattern, Expression sourceExpression)
        {
            string sourceVariable = NextSyntheticName("destructure");
            StoreExpressionInVariable(sourceExpression, sourceVariable);
            EmitDestructuringFromVariable(pattern, sourceVariable);
        }

        private void StoreExpressionInVariable(Expression sourceExpression, string destinationVariable)
        {
            Expression effectiveSource = sourceExpression ?? new UndefinedLiteral();
            Visit(effectiveSource);
            EmitStoreVarByName(destinationVariable);
            Emit(OpCode.Pop);
        }

        private void EmitDestructuringFromVariable(Expression pattern, string sourceVariableName)
        {
            EmitDestructuringFromVariableCore(pattern, sourceVariableName, applyObjectGuard: true);
        }

        private void EmitDestructuringFromVariableCore(Expression pattern, string sourceVariableName, bool applyObjectGuard)
        {
            if (pattern == null || string.IsNullOrEmpty(sourceVariableName))
            {
                return;
            }

            if (applyObjectGuard)
            {
                EmitLoadVarByName(sourceVariableName);
                Emit(OpCode.LoadNull);
                Emit(OpCode.StrictEqual);
                int skipNullThrowOffset = EmitJump(OpCode.JumpIfFalse);
                EmitThrowJsError("TypeError", "Cannot destructure null");
                PatchJumpTo(skipNullThrowOffset, _instructions.Count);

                EmitLoadVarByName(sourceVariableName);
                Emit(OpCode.LoadUndefined);
                Emit(OpCode.StrictEqual);
                int skipUndefinedThrowOffset = EmitJump(OpCode.JumpIfFalse);
                EmitThrowJsError("TypeError", "Cannot destructure undefined");
                PatchJumpTo(skipUndefinedThrowOffset, _instructions.Count);
            }

            if (pattern is Identifier identifierPattern)
            {
                EmitAssignVariableFromVariable(identifierPattern.Value, sourceVariableName);
            }
            else if (pattern is ArrayLiteral arrayPattern)
            {
                EmitDestructuringArrayBinding(arrayPattern, sourceVariableName);
            }
            else if (pattern is ObjectLiteral objectPattern)
            {
                EmitDestructuringObjectBinding(objectPattern, sourceVariableName);
            }
            else if (pattern is AssignmentExpression assignmentPattern)
            {
                EmitApplyDefaultIfUndefined(sourceVariableName, assignmentPattern.Right, GetDestructuringTargetName(assignmentPattern.Left));
                EmitDestructuringFromVariableCore(assignmentPattern.Left, sourceVariableName, applyObjectGuard: false);
            }
            else if (!(pattern is EmptyExpression) && !(pattern is UndefinedLiteral))
            {
                EmitThrowJsError("SyntaxError", "Unsupported destructuring pattern.");
                return;
            }
        }

        private void EmitDestructuringArrayBinding(ArrayLiteral arrayPattern, string sourceVariableName)
        {
            if (arrayPattern?.Elements == null)
            {
                return;
            }

            string iteratorVariable = NextSyntheticName("arr_iter");
            EmitLoadVarByName(sourceVariableName);
            Emit(OpCode.MakeValuesIterator);
            EmitStoreVarByName(iteratorVariable);
            Emit(OpCode.Pop);

            Emit(OpCode.PushExceptionHandler);
            int catchOffsetIndex = _instructions.Count;
            EmitInt32(-1);
            int finallyOffsetIndex = _instructions.Count;
            EmitInt32(0);

            for (int i = 0; i < arrayPattern.Elements.Count; i++)
            {
                var element = arrayPattern.Elements[i];

                if (element is SpreadElement spreadElement)
                {
                    EmitArrayRestBindingFromIterator(iteratorVariable, spreadElement.Argument);
                    break;
                }

                string hasValueVariable = NextSyntheticName("arr_has");
                EmitLoadVarByName(iteratorVariable);
                Emit(OpCode.IteratorMoveNext);
                EmitStoreVarByName(hasValueVariable);
                Emit(OpCode.Pop);

                if (element == null || element is UndefinedLiteral || element is EmptyExpression)
                {
                    continue;
                }

                string elementVariable = NextSyntheticName("arr_elem");
                Emit(OpCode.LoadUndefined);
                EmitStoreVarByName(elementVariable);
                Emit(OpCode.Pop);

                EmitLoadVarByName(hasValueVariable);
                int skipCurrentOffset = EmitJump(OpCode.JumpIfFalse);

                EmitLoadVarByName(iteratorVariable);
                Emit(OpCode.IteratorCurrent);
                EmitStoreVarByName(elementVariable);
                Emit(OpCode.Pop);

                PatchJumpTo(skipCurrentOffset, _instructions.Count);
                EmitDestructuringTargetBinding(element, elementVariable);
            }

            Emit(OpCode.PopExceptionHandler);
            int jumpToFinallyOffset = EmitJump(OpCode.Jump);

            int finallyStart = _instructions.Count;
            byte[] catchBytes = BitConverter.GetBytes(-1);
            for (int i = 0; i < 4; i++)
            {
                _instructions[catchOffsetIndex + i] = catchBytes[i];
            }
            byte[] finallyBytes = BitConverter.GetBytes(finallyStart);
            for (int i = 0; i < 4; i++)
            {
                _instructions[finallyOffsetIndex + i] = finallyBytes[i];
            }
            PatchJumpTo(jumpToFinallyOffset, finallyStart);

            Emit(OpCode.EnterFinally);
            EmitCloseIterator(iteratorVariable);
            Emit(OpCode.ExitFinally);
        }

        private void EmitDestructuringObjectBinding(ObjectLiteral objectPattern, string sourceVariableName)
        {
            if (objectPattern?.Pairs == null)
            {
                return;
            }

            var pairs = new List<KeyValuePair<string, Expression>>();
            foreach (var pair in objectPattern.Pairs)
            {
                pairs.Add(pair);
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                string key = pair.Key ?? string.Empty;
                Expression target = pair.Value;

                if (key.StartsWith("__spread_", StringComparison.Ordinal) || target is SpreadElement)
                {
                    var spreadTarget = target as SpreadElement;
                    if (spreadTarget == null || spreadTarget.Argument == null)
                    {
                        int msgIdx = AddConstant(FenValue.FromString("SyntaxError: Object destructuring spread target is invalid."));
                        Emit(OpCode.LoadConst);
                        EmitInt32(msgIdx);
                        Emit(OpCode.Throw);
                        continue;
                    }

                    EmitObjectRestBinding(objectPattern, sourceVariableName, pairs, i, spreadTarget.Argument);
                    continue;
                }

                string propertyVariable = NextSyntheticName("obj_prop");
                if (key.StartsWith("__computed_", StringComparison.Ordinal))
                {
                    if (objectPattern.ComputedKeys == null ||
                        !objectPattern.ComputedKeys.TryGetValue(key, out var computedKeyExpression) ||
                        computedKeyExpression == null)
                    {
                        int msgIdx = AddConstant(FenValue.FromString("SyntaxError: Computed object destructuring key expression is missing."));
                        Emit(OpCode.LoadConst);
                        EmitInt32(msgIdx);
                        Emit(OpCode.Throw);
                        continue;
                    }
                    else
                    {
                        EmitLoadPropertyByExpressionToVariable(sourceVariableName, computedKeyExpression, propertyVariable);
                    }
                }
                else
                {
                    EmitLoadPropertyByKeyToVariable(sourceVariableName, FenValue.FromString(key), propertyVariable);
                }

                EmitDestructuringTargetBinding(target, propertyVariable);
            }
        }

        private void EmitDestructuringTargetBinding(Expression target, string valueVariable)
        {
            if (target == null || string.IsNullOrEmpty(valueVariable))
            {
                return;
            }

            if (target is Identifier identifierTarget)
            {
                EmitAssignVariableFromVariable(identifierTarget.Value, valueVariable);
                return;
            }

            if (target is AssignmentExpression assignmentTarget)
            {
                EmitApplyDefaultIfUndefined(valueVariable, assignmentTarget.Right, GetDestructuringTargetName(assignmentTarget.Left));
                EmitDestructuringTargetBinding(assignmentTarget.Left, valueVariable);
                return;
            }

            if (target is ArrayLiteral || target is ObjectLiteral)
            {
                EmitDestructuringFromVariable(target, valueVariable);
                return;
            }

            if (target is MemberExpression memberTarget)
            {
                Visit(memberTarget.Object);
                int memberKeyIdx = AddConstant(FenValue.FromString(memberTarget.Property ?? string.Empty));
                Emit(OpCode.LoadConst);
                EmitInt32(memberKeyIdx);
                EmitLoadVarByName(valueVariable);
                Emit(OpCode.StoreProp);
                Emit(OpCode.Pop);
                return;
            }

            if (target is IndexExpression indexTarget)
            {
                Visit(indexTarget.Left);
                Visit(indexTarget.Index);
                EmitLoadVarByName(valueVariable);
                Emit(OpCode.StoreProp);
                Emit(OpCode.Pop);
                return;
            }

            if (target is EmptyExpression || target is UndefinedLiteral)
            {
                return;
            }

            EmitThrowJsError("SyntaxError", $"Unsupported destructuring binding target '{target.GetType().Name}'.");
        }

        private void EmitLoadPropertyByKeyToVariable(string sourceVariableName, FenValue propertyKey, string destinationVariable)
        {
            EmitLoadVarByName(sourceVariableName);
            int keyIdx = AddConstant(propertyKey);
            Emit(OpCode.LoadConst);
            EmitInt32(keyIdx);
            Emit(OpCode.LoadProp);
            EmitStoreVarByName(destinationVariable);
            Emit(OpCode.Pop);
        }

        private void EmitLoadPropertyByExpressionToVariable(string sourceVariableName, Expression propertyExpression, string destinationVariable)
        {
            string propertyKeyVariable = NextSyntheticName("obj_key");
            StoreExpressionInVariable(propertyExpression, propertyKeyVariable);
            EmitLoadPropertyByVariableToVariable(sourceVariableName, propertyKeyVariable, destinationVariable);
        }

        private void EmitLoadPropertyByVariableToVariable(string sourceVariableName, string propertyKeyVariableName, string destinationVariable)
        {
            EmitLoadVarByName(sourceVariableName);
            EmitLoadVarByName(propertyKeyVariableName);
            Emit(OpCode.LoadProp);
            EmitStoreVarByName(destinationVariable);
            Emit(OpCode.Pop);
        }

        private void EmitDeletePropertyByKeyFromVariable(string sourceVariableName, FenValue propertyKey)
        {
            EmitLoadVarByName(sourceVariableName);
            int keyIdx = AddConstant(propertyKey);
            Emit(OpCode.LoadConst);
            EmitInt32(keyIdx);
            Emit(OpCode.DeleteProp);
            Emit(OpCode.Pop);
        }

        private void EmitDeletePropertyByVariableFromVariable(string sourceVariableName, string propertyKeyVariableName)
        {
            EmitLoadVarByName(sourceVariableName);
            EmitLoadVarByName(propertyKeyVariableName);
            Emit(OpCode.DeleteProp);
            Emit(OpCode.Pop);
        }

        private void EmitObjectRestBinding(
            ObjectLiteral pattern,
            string sourceVariableName,
            List<KeyValuePair<string, Expression>> pairs,
            int restPairIndex,
            Expression restTarget)
        {
            if (restTarget == null)
            {
                return;
            }

            string restObjectVariable = NextSyntheticName("obj_rest");

            Emit(OpCode.MakeObject);
            EmitInt32(0);
            EmitLoadVarByName(sourceVariableName);
            Emit(OpCode.ObjectSpread);
            EmitStoreVarByName(restObjectVariable);
            Emit(OpCode.Pop);

            for (int i = 0; i < restPairIndex; i++)
            {
                var usedPair = pairs[i];
                string usedKey = usedPair.Key ?? string.Empty;

                if (usedKey.StartsWith("__spread_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (usedKey.StartsWith("__computed_", StringComparison.Ordinal))
                {
                    if (pattern?.ComputedKeys == null ||
                        !pattern.ComputedKeys.TryGetValue(usedKey, out var computedKeyExpression) ||
                        computedKeyExpression == null)
                    {
                        throw new FenSyntaxError("Compiler: Computed object destructuring key expression is missing.");
                    }

                    string computedKeyVariable = NextSyntheticName("obj_rest_key");
                    StoreExpressionInVariable(computedKeyExpression, computedKeyVariable);
                    EmitDeletePropertyByVariableFromVariable(restObjectVariable, computedKeyVariable);
                    continue;
                }

                EmitDeletePropertyByKeyFromVariable(restObjectVariable, FenValue.FromString(usedKey));
            }

            EmitDestructuringTargetBinding(restTarget, restObjectVariable);
        }

        private void EmitArrayRestBinding(string sourceVariableName, int startIndex, Expression restTarget)
        {
            if (restTarget == null)
            {
                return;
            }

            string restArrayVariable = NextSyntheticName("arr_rest");
            string restIndexVariable = NextSyntheticName("arr_rest_index");
            string restLengthVariable = NextSyntheticName("arr_rest_len");

            Emit(OpCode.MakeArray);
            EmitInt32(0);
            EmitStoreVarByName(restArrayVariable);
            Emit(OpCode.Pop);

            EmitLoadPropertyByKeyToVariable(sourceVariableName, FenValue.FromString("length"), restLengthVariable);

            int startIndexConst = AddConstant(FenValue.FromNumber(startIndex));
            Emit(OpCode.LoadConst);
            EmitInt32(startIndexConst);
            EmitStoreVarByName(restIndexVariable);
            Emit(OpCode.Pop);

            int loopStart = _instructions.Count;

            EmitLoadVarByName(restIndexVariable);
            EmitLoadVarByName(restLengthVariable);
            Emit(OpCode.LessThan);
            int jumpLoopEndOffset = EmitJump(OpCode.JumpIfFalse);

            EmitLoadVarByName(restArrayVariable);
            EmitLoadVarByName(sourceVariableName);
            EmitLoadVarByName(restIndexVariable);
            Emit(OpCode.LoadProp);
            Emit(OpCode.ArrayAppend);
            Emit(OpCode.Pop);

            EmitLoadVarByName(restIndexVariable);
            int oneConst = AddConstant(FenValue.FromNumber(1));
            Emit(OpCode.LoadConst);
            EmitInt32(oneConst);
            Emit(OpCode.Add);
            EmitStoreVarByName(restIndexVariable);
            Emit(OpCode.Pop);

            Emit(OpCode.Jump);
            EmitInt32(loopStart);

            PatchJumpTo(jumpLoopEndOffset, _instructions.Count);
            EmitDestructuringTargetBinding(restTarget, restArrayVariable);
        }

        private void EmitAssignVariableFromVariable(string targetVariableName, string sourceVariableName)
        {
            if (string.IsNullOrEmpty(targetVariableName) || string.IsNullOrEmpty(sourceVariableName))
            {
                return;
            }

            EmitLoadVarByName(sourceVariableName);
            EmitStoreVarByName(targetVariableName);
            Emit(OpCode.Pop);
        }

        private void EmitCloseIterator(string iteratorVariable)
        {
            if (string.IsNullOrEmpty(iteratorVariable))
            {
                return;
            }

            EmitLoadVarByName(iteratorVariable);
            Emit(OpCode.IteratorClose);
        }

        private void EmitArrayRestBindingFromIterator(string iteratorVariable, Expression restTarget)
        {
            if (restTarget == null || string.IsNullOrEmpty(iteratorVariable))
            {
                return;
            }

            string restArrayVariable = NextSyntheticName("arr_rest_iter");
            Emit(OpCode.MakeArray);
            EmitInt32(0);
            EmitStoreVarByName(restArrayVariable);
            Emit(OpCode.Pop);

            int loopStart = _instructions.Count;
            EmitLoadVarByName(iteratorVariable);
            Emit(OpCode.IteratorMoveNext);
            int jumpLoopEndOffset = EmitJump(OpCode.JumpIfFalse);

            EmitLoadVarByName(restArrayVariable);
            EmitLoadVarByName(iteratorVariable);
            Emit(OpCode.IteratorCurrent);
            Emit(OpCode.ArrayAppend);
            Emit(OpCode.Pop);

            Emit(OpCode.Jump);
            EmitInt32(loopStart);

            PatchJumpTo(jumpLoopEndOffset, _instructions.Count);
            EmitDestructuringTargetBinding(restTarget, restArrayVariable);
        }

        private void EmitApplyDefaultIfUndefined(string valueVariable, Expression defaultExpression, string inferredName = null)
        {
            if (string.IsNullOrEmpty(valueVariable) || defaultExpression == null)
            {
                return;
            }

            EmitLoadVarByName(valueVariable);
            Emit(OpCode.LoadUndefined);
            Emit(OpCode.StrictEqual);
            int skipDefaultOffset = EmitJump(OpCode.JumpIfFalse);

            VisitWithInferredName(defaultExpression, inferredName);
            EmitStoreVarByName(valueVariable);
            Emit(OpCode.Pop);

            PatchJumpTo(skipDefaultOffset, _instructions.Count);
        }

        private void VisitWithInferredName(AstNode node, string inferredName)
        {
            if (!CanUseInferredName(node) || string.IsNullOrEmpty(inferredName))
            {
                Visit(node);
                return;
            }

            string previousName = _currentInferredName;
            _currentInferredName = inferredName;
            try
            {
                Visit(node);
            }
            finally
            {
                _currentInferredName = previousName;
            }
        }

        private void AnalyzeAnnexBBlockFunctions(AstNode root)
        {
            if (_currentCompileIsStrict)
            {
                return;
            }

            static void AddLexicalName(HashSet<string> names, Identifier identifier)
            {
                if (!string.IsNullOrEmpty(identifier?.Value))
                {
                    names.Add(identifier.Value);
                }
            }

            static HashSet<string> CollectImmediateLexicalNames(IReadOnlyList<Statement> statements)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (statements == null)
                {
                    return names;
                }

                foreach (var statement in statements)
                {
                    switch (statement)
                    {
                        case LetStatement letStatement when letStatement.Kind != DeclarationKind.Var:
                            AddLexicalName(names, letStatement.Name);
                            break;
                        case ClassStatement classStatement:
                            AddLexicalName(names, classStatement.Name);
                            break;
                    }
                }

                return names;
            }

            var lexicalBlockers = new Stack<HashSet<string>>();
            var parameterBlockers = new Stack<HashSet<string>>();

            void Traverse(AstNode node, bool isTopLevel)
            {
                if (node == null)
                {
                    return;
                }

                switch (node)
                {
                    case Program program:
                        lexicalBlockers.Push(CollectImmediateLexicalNames(program.Statements));
                        parameterBlockers.Push(new HashSet<string>(StringComparer.Ordinal));
                        foreach (var statement in program.Statements)
                        {
                            Traverse(statement, true);
                        }
                        parameterBlockers.Pop();
                        lexicalBlockers.Pop();
                        break;

                    case BlockStatement block:
                        lexicalBlockers.Push(CollectImmediateLexicalNames(block.Statements));
                        foreach (var statement in block.Statements)
                        {
                            Traverse(statement, false);
                        }
                        lexicalBlockers.Pop();
                        break;

                    case FunctionDeclarationStatement functionDeclaration:
                        if (!isTopLevel && !string.IsNullOrEmpty(functionDeclaration.Function?.Name))
                        {
                            bool blocked = false;
                            foreach (var names in lexicalBlockers)
                            {
                                if (names.Contains(functionDeclaration.Function.Name))
                                {
                                    blocked = true;
                                    break;
                                }
                            }

                            if (!blocked)
                            {
                                foreach (var names in parameterBlockers)
                                {
                                    if (names.Contains(functionDeclaration.Function.Name))
                                    {
                                        blocked = true;
                                        break;
                                    }
                                }
                            }

                            if (!blocked)
                            {
                                _annexBVarScopedBlockFunctions.Add(functionDeclaration);
                            }
                        }
                        break;

                    case IfStatement ifStatement:
                        Traverse(ifStatement.Consequence, false);
                        Traverse(ifStatement.Alternative, false);
                        break;

                    case WhileStatement whileStatement:
                        Traverse(whileStatement.Body, false);
                        break;

                    case DoWhileStatement doWhileStatement:
                        Traverse(doWhileStatement.Body, false);
                        break;

                    case ForStatement forStatement:
                        Traverse(forStatement.Body, false);
                        break;

                    case ForInStatement forInStatement:
                        Traverse(forInStatement.Body, false);
                        break;

                    case ForOfStatement forOfStatement:
                        Traverse(forOfStatement.Body, false);
                        break;

                    case TryStatement tryStatement:
                    {
                        Traverse(tryStatement.Block, false);
                        if (tryStatement.CatchBlock != null)
                        {
                            var catchNames = new HashSet<string>(StringComparer.Ordinal);
                            AddLexicalName(catchNames, tryStatement.CatchParameter);
                            lexicalBlockers.Push(catchNames);
                            Traverse(tryStatement.CatchBlock, false);
                            lexicalBlockers.Pop();
                        }
                        Traverse(tryStatement.FinallyBlock, false);
                        break;
                    }

                    case SwitchStatement switchStatement:
                        if (switchStatement.Cases == null)
                        {
                            break;
                        }

                        foreach (var switchCase in switchStatement.Cases)
                        {
                            if (switchCase.Consequent == null)
                            {
                                continue;
                            }

                            lexicalBlockers.Push(CollectImmediateLexicalNames(switchCase.Consequent));
                            foreach (var statement in switchCase.Consequent)
                            {
                                Traverse(statement, false);
                            }
                            lexicalBlockers.Pop();
                        }
                        break;

                    case LabeledStatement labeledStatement:
                        Traverse(labeledStatement.Body, false);
                        break;

                    case FunctionLiteral functionLiteral:
                    {
                        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
                        if (functionLiteral.Parameters != null)
                        {
                            foreach (var parameter in functionLiteral.Parameters)
                            {
                                AddLexicalName(parameterNames, parameter);
                            }
                        }

                        parameterBlockers.Push(parameterNames);
                        Traverse(functionLiteral.Body, true);
                        parameterBlockers.Pop();
                        break;
                    }
                }
            }

            Traverse(root, true);
        }

        private void VisitFunctionWithInferredName(AstNode node, string inferredName)
        {
            if (!CanUseInferredName(node) || string.IsNullOrEmpty(inferredName))
            {
                Visit(node);
                return;
            }

            VisitWithInferredName(node, inferredName);
        }

        private static string GetInferredAssignmentName(Expression target)
        {
            return target switch
            {
                Identifier identifier => identifier.Value,
                MemberExpression member => member.Property,
                _ => null
            };
        }

        private static string GetMethodInferredName(MethodDefinition methodDefinition)
        {
            if (methodDefinition == null || methodDefinition.Key == null || methodDefinition.Computed)
            {
                return null;
            }

            string methodName = methodDefinition.Key.Value;
            if (string.IsNullOrEmpty(methodName))
            {
                return null;
            }

            return methodDefinition.IsPrivate ? $"#{methodName}" : methodName;
        }

        private static string GetClassPropertyInferredName(ClassProperty classProperty)
        {
            if (classProperty == null || classProperty.Key == null || classProperty.ComputedKeyExpression != null)
            {
                return null;
            }

            string propertyName = classProperty.Key.Value;
            if (string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            return classProperty.IsPrivate ? $"#{propertyName}" : propertyName;
        }

        private static string GetObjectLiteralInferredName(string propertyKey)
        {
            if (string.IsNullOrEmpty(propertyKey))
            {
                return null;
            }

            if (propertyKey.StartsWith("__computed_", StringComparison.Ordinal))
            {
                return null;
            }

            if (propertyKey.StartsWith("__get_", StringComparison.Ordinal))
            {
                return propertyKey.Substring("__get_".Length);
            }

            if (propertyKey.StartsWith("__set_", StringComparison.Ordinal))
            {
                return propertyKey.Substring("__set_".Length);
            }

            return propertyKey;
        }

        private static bool CanInferAnonymousClassName(ClassExpression classExpression)
        {
            if (classExpression == null)
            {
                return false;
            }

            if (classExpression.Methods != null)
            {
                foreach (var method in classExpression.Methods)
                {
                    if (method?.Static == true && !method.IsPrivate && !method.Computed && string.Equals(method.Key?.Value, "name", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            if (classExpression.Properties != null)
            {
                foreach (var property in classExpression.Properties)
                {
                    if (property?.Static == true && !property.IsPrivate && string.Equals(property.Key?.Value, "name", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool CanUseInferredName(AstNode node)
        {
            if (node is FunctionLiteral functionLiteral)
            {
                return string.IsNullOrEmpty(functionLiteral.Name);
            }

            if (node is AsyncFunctionExpression asyncFunctionExpression)
            {
                return asyncFunctionExpression.Name == null || string.IsNullOrEmpty(asyncFunctionExpression.Name.Value);
            }

            if (node is ArrowFunctionExpression)
            {
                return true;
            }

            if (node is ClassExpression classExpression)
            {
                return classExpression.Name == null || string.IsNullOrEmpty(classExpression.Name.Value);
            }

            return false;
        }

        private static string GetDestructuringTargetName(Expression target)
        {
            switch (target)
            {
                case Identifier identifier:
                    return identifier.Value;
                case MemberExpression memberExpression:
                    return memberExpression.Property;
                case IndexExpression indexExpression when indexExpression.Index is StringLiteral stringLiteral:
                    return stringLiteral.Value;
                case IndexExpression indexExpression when indexExpression.Index is IntegerLiteral integerLiteral:
                    return integerLiteral.Value.ToString();
                case AssignmentExpression assignmentExpression:
                    return GetDestructuringTargetName(assignmentExpression.Left);
                default:
                    return null;
            }
        }

        private void EmitThrowJsError(string constructorName, string message)
        {
            EmitLoadVarByName(constructorName ?? "Error");
            int messageIdx = AddConstant(FenValue.FromString(message ?? string.Empty));
            Emit(OpCode.LoadConst);
            EmitInt32(messageIdx);
            Emit(OpCode.Construct);
            EmitInt32(1);
            Emit(OpCode.Throw);
        }

        private static void RejectWithInsideCallableBody(AstNode body, string nodeKind)
        {
            if (ContainsWithStatement(body))
            {
                throw new FenSyntaxError($"Compiler: {nodeKind} with 'with' statement is not supported in bytecode-only function bodies.");
            }
        }

        private static bool ContainsWithStatement(AstNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (node is WithStatement)
            {
                return true;
            }

            if (node is FunctionLiteral || node is AsyncFunctionExpression || node is ArrowFunctionExpression)
            {
                return false;
            }

            if (node is Program program)
            {
                if (program.Statements == null)
                {
                    return false;
                }

                foreach (var statement in program.Statements)
                {
                    if (ContainsWithStatement(statement))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (node is BlockStatement block)
            {
                if (block.Statements == null)
                {
                    return false;
                }

                foreach (var statement in block.Statements)
                {
                    if (ContainsWithStatement(statement))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (node is WithStatement withStatement)
            {
                return ContainsWithStatement(withStatement.Object) || ContainsWithStatement(withStatement.Body);
            }

            if (node is IfStatement ifStatement)
            {
                return ContainsWithStatement(ifStatement.Condition)
                    || ContainsWithStatement(ifStatement.Consequence)
                    || ContainsWithStatement(ifStatement.Alternative);
            }

            if (node is WhileStatement whileStatement)
            {
                return ContainsWithStatement(whileStatement.Condition) || ContainsWithStatement(whileStatement.Body);
            }

            if (node is DoWhileStatement doWhileStatement)
            {
                return ContainsWithStatement(doWhileStatement.Condition) || ContainsWithStatement(doWhileStatement.Body);
            }

            if (node is ForStatement forStatement)
            {
                return ContainsWithStatement(forStatement.Init)
                    || ContainsWithStatement(forStatement.Condition)
                    || ContainsWithStatement(forStatement.Update)
                    || ContainsWithStatement(forStatement.Body);
            }

            if (node is ForInStatement forInStatement)
            {
                return ContainsWithStatement(forInStatement.Object) || ContainsWithStatement(forInStatement.Body);
            }

            if (node is ForOfStatement forOfStatement)
            {
                return ContainsWithStatement(forOfStatement.Iterable) || ContainsWithStatement(forOfStatement.Body);
            }

            if (node is TryStatement tryStatement)
            {
                return ContainsWithStatement(tryStatement.Block)
                    || ContainsWithStatement(tryStatement.CatchBlock)
                    || ContainsWithStatement(tryStatement.FinallyBlock);
            }

            if (node is LabeledStatement labeledStatement)
            {
                return ContainsWithStatement(labeledStatement.Body);
            }

            if (node is SwitchStatement switchStatement)
            {
                if (switchStatement.Cases == null)
                {
                    return false;
                }

                foreach (var switchCase in switchStatement.Cases)
                {
                    if (switchCase == null)
                    {
                        continue;
                    }

                    if (ContainsWithStatement(switchCase.Test))
                    {
                        return true;
                    }

                    if (switchCase.Consequent == null)
                    {
                        continue;
                    }

                    foreach (var statement in switchCase.Consequent)
                    {
                        if (ContainsWithStatement(statement))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        internal static CodeBlock CompileCallableFunctionBody(
            List<Identifier> parameters,
            AstNode body,
            string functionName,
            bool forceStrictRoot,
            out Dictionary<string, int> localMap,
            out bool needsArgumentsObject)
        {
            ValidateSupportedParameterList(parameters, "FenFunction");

            var funcCompiler = CreateFunctionCompiler(parameters, functionName, forceStrictRoot);
            var compiledBlock = funcCompiler.Compile(BuildCallableBody(body, parameters));
            localMap = BuildFunctionLocalMap(compiledBlock);
            needsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap);
            return compiledBlock;
        }

        private static AstNode BuildCallableBody(AstNode body, List<Identifier> parameters = null)
        {
            BlockStatement normalizedBody;
            if (body is BlockStatement blockBody)
            {
                normalizedBody = blockBody;
            }
            else if (body is Expression exprBody)
            {
                var syntheticBlock = new BlockStatement();
                syntheticBlock.Statements.Add(new ReturnStatement
                {
                    ReturnValue = exprBody
                });
                normalizedBody = syntheticBlock;
            }
            else
            {
                throw new FenSyntaxError($"Compiler: Callable body type '{body?.GetType().Name ?? "null"}' is not supported.");
            }

            if (!HasDefaultParameters(parameters) && !HasDestructuringParameters(parameters))
            {
                return normalizedBody;
            }

            var loweredBody = new BlockStatement();
            AppendDefaultParameterInitializers(parameters, loweredBody.Statements);
            AppendDestructuringParameterInitializers(parameters, loweredBody.Statements);
            if (normalizedBody.Statements != null)
            {
                foreach (var statement in normalizedBody.Statements)
                {
                    loweredBody.Statements.Add(statement);
                }
            }

            return loweredBody;
        }

        private static bool HasDefaultParameters(List<Identifier> parameters)
        {
            if (parameters == null)
            {
                return false;
            }

            foreach (var parameter in parameters)
            {
                if (parameter?.DefaultValue != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasDestructuringParameters(List<Identifier> parameters)
        {
            if (parameters == null)
            {
                return false;
            }

            foreach (var parameter in parameters)
            {
                if (parameter?.DestructuringPattern != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BytecodeBlockMayReferenceArguments(CodeBlock block, Dictionary<string, int> localMap)
        {
            if (block == null)
            {
                return true;
            }

            if (localMap != null &&
                localMap.TryGetValue("arguments", out int argumentsSlot) &&
                BytecodeMayUseLocalSlot(block.Instructions, argumentsSlot))
            {
                return true;
            }

            if (block.Constants == null)
            {
                return true;
            }

            foreach (var constant in block.Constants)
            {
                if (constant.IsString && string.Equals(constant.AsString(), "arguments", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BytecodeMayUseLocalSlot(byte[] instructions, int slotIndex)
        {
            if (instructions == null || instructions.Length < 5)
            {
                return false;
            }

            for (int i = 0; i <= instructions.Length - 5; i++)
            {
                byte opcode = instructions[i];
                if (opcode != (byte)OpCode.LoadLocal && opcode != (byte)OpCode.StoreLocal)
                {
                    continue;
                }

                int candidate = BitConverter.ToInt32(instructions, i + 1);
                if (candidate == slotIndex)
                {
                    return true;
                }
            }

            return false;
        }

        private void EmitFunctionDeclaration(FunctionDeclarationStatement funcDecl)
        {
            ValidateSupportedParameterList(funcDecl.Function.Parameters, "FunctionDeclarationStatement");
            var funcCompiler = CreateFunctionCompiler(funcDecl.Function.Parameters, funcDecl.Function.Name, funcDecl.Function.IsStrict);
            var compiledBlock = funcCompiler.Compile(BuildCallableBody(funcDecl.Function.Body, funcDecl.Function.Parameters));
            var localMap = BuildFunctionLocalMap(compiledBlock);

            var templateFunc = new FenFunction(funcDecl.Function.Parameters, compiledBlock, null)
            {
                Name = funcDecl.Function.Name,
                IsAsync = funcDecl.Function.IsAsync,
                IsGenerator = funcDecl.Function.IsGenerator,
                IsMethodDefinition = funcDecl.Function.IsMethodDefinition,
                Source = GetFunctionSourceText(funcDecl.Function, funcDecl.Function.Name),
                NeedsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap),
                LocalMap = localMap
            };
            int funcIdx = AddConstant(FenValue.FromFunction(templateFunc));

            Emit(OpCode.MakeClosure);
            EmitInt32(funcIdx);

            if (funcDecl.Function.Name != null)
            {
                bool isBlockScopedFunction = _insideBlock && !_topLevelHoistedFunctions.Contains(funcDecl);
                if (isBlockScopedFunction)
                {
                    EmitStoreVarByName(funcDecl.Function.Name);
                    if (_annexBVarScopedBlockFunctions.Contains(funcDecl))
                    {
                        EmitStoreVarDeclarationByName(funcDecl.Function.Name);
                    }
                }
                else
                {
                    EmitStoreVarDeclarationByName(funcDecl.Function.Name);
                }
            }

            // Function declaration is a statement; consume stored value result.
            Emit(OpCode.Pop);
        }
        private static Dictionary<string, int> BuildFunctionLocalMap(CodeBlock block)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (block?.LocalSlotNames == null)
            {
                return map;
            }

            for (int i = 0; i < block.LocalSlotNames.Count; i++)
            {
                var name = block.LocalSlotNames[i];
                if (!string.IsNullOrEmpty(name))
                {
                    map[name] = i;
                }
            }

            return map;
        }

        private static void AppendDefaultParameterInitializers(List<Identifier> parameters, List<Statement> destination)
        {
            if (parameters == null || destination == null)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                if (parameter?.DefaultValue == null)
                {
                    continue;
                }

                var parameterName = parameter.Value ?? string.Empty;
                var paramIdentifier = new Identifier(parameter.Token, parameterName);
                var assignTarget = new Identifier(parameter.Token, parameterName);
                var undefinedValue = new UndefinedLiteral { Token = parameter.Token };

                var condition = new InfixExpression
                {
                    Left = paramIdentifier,
                    Operator = "===",
                    Right = undefinedValue
                };

                var assignment = new AssignmentExpression
                {
                    Left = assignTarget,
                    Right = parameter.DefaultValue
                };

                var initBlock = new BlockStatement();
                initBlock.Statements.Add(new ExpressionStatement
                {
                    Expression = assignment
                });

                destination.Add(new IfStatement
                {
                    Condition = condition,
                    Consequence = initBlock
                });
            }
        }

        private static void AppendDestructuringParameterInitializers(List<Identifier> parameters, List<Statement> destination)
        {
            if (parameters == null || destination == null)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                if (parameter?.DestructuringPattern == null || string.IsNullOrEmpty(parameter.Value))
                {
                    continue;
                }

                destination.Add(new LetStatement
                {
                    Token = parameter.Token,
                    Name = new Identifier(parameter.Token, parameter.Value),
                    DestructuringPattern = parameter.DestructuringPattern,
                    Value = new Identifier(parameter.Token, parameter.Value),
                    Kind = DeclarationKind.Let
                });
            }
        }

        private static bool IsSupportedDestructuringPattern(Expression pattern)
        {
            if (pattern == null)
            {
                return true;
            }

            if (pattern is Identifier || pattern is EmptyExpression || pattern is UndefinedLiteral)
            {
                return true;
            }

            if (pattern is AssignmentExpression assignmentPattern)
            {
                return assignmentPattern.Left != null && IsSupportedDestructuringPattern(assignmentPattern.Left);
            }

            if (pattern is ArrayLiteral arrayPattern)
            {
                if (arrayPattern.Elements == null)
                {
                    return true;
                }

                foreach (var element in arrayPattern.Elements)
                {
                    if (element == null || element is EmptyExpression || element is UndefinedLiteral)
                    {
                        continue;
                    }

                    if (element is SpreadElement spreadElement)
                    {
                        if (!IsSupportedDestructuringPattern(spreadElement.Argument))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!IsSupportedDestructuringPattern(element))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (pattern is ObjectLiteral objectPattern)
            {
                if (objectPattern.Pairs == null)
                {
                    return true;
                }

                foreach (var pair in objectPattern.Pairs)
                {
                    var key = pair.Key ?? string.Empty;
                    var target = pair.Value;

                    if (key.StartsWith("__computed_", StringComparison.Ordinal))
                    {
                        if (objectPattern.ComputedKeys == null ||
                            !objectPattern.ComputedKeys.TryGetValue(key, out var computedKeyExpression) ||
                            computedKeyExpression == null)
                        {
                            return false;
                        }
                    }

                    if (target is SpreadElement spreadTarget)
                    {
                        if (!IsSupportedDestructuringPattern(spreadTarget.Argument))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!IsSupportedDestructuringPattern(target))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        private static void ValidateSupportedParameterList(List<Identifier> parameters, string owner)
        {
            if (parameters == null)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                if (parameter == null)
                {
                    continue;
                }
            }
        }

        private sealed class BreakContext
        {
            public int ScopeCleanupDepth { get; set; }
            public readonly List<int> BreakJumpOffsets = new List<int>();
        }

        private sealed class LoopContext
        {
            public int ContinueTarget { get; set; } = -1;
            public int ScopeCleanupDepth { get; set; }
            public readonly List<int> PendingContinueJumpOffsets = new List<int>();
        }

        private sealed class LabelContext
        {
            public string Label { get; set; }
            public LoopContext LoopContext { get; set; }
            public readonly List<int> BreakJumpOffsets = new List<int>();
        }
    }
}























