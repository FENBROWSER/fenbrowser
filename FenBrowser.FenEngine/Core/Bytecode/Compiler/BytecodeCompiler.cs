using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Core.Types;
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
        private readonly Dictionary<string, int> _localSlotByName = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _localSlotNames = new List<string>();
        private readonly HashSet<string> _localBindings = new HashSet<string>(StringComparer.Ordinal);
        private int _syntheticNameCounter;

        public BytecodeCompiler(bool isEval = false)
            : this(enableLocalSlots: false, functionParameters: null, functionName: null, isEval: isEval)
        {
        }

        private BytecodeCompiler(bool enableLocalSlots, List<Identifier> functionParameters, string functionName, bool isEval = false)
        {
            _enableLocalSlots = enableLocalSlots;
            _functionParameters = functionParameters;
            _functionName = functionName;
            _isEval = isEval;
        }

        private static BytecodeCompiler CreateFunctionCompiler(List<Identifier> parameters, string functionName)
        {
            return new BytecodeCompiler(enableLocalSlots: true, functionParameters: parameters, functionName: functionName, isEval: false);
        }

        public CodeBlock Compile(AstNode root)
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

            if (_enableLocalSlots)
            {
                InitializeLocalBindings(root);
            }

            if (_isEval)
            {
                HoistEvalBlockFunctions(root);
            }

            Visit(root);

            // Ensure every block ends with a Halt
            Emit(OpCode.Halt);

            var localSlots = _enableLocalSlots ? new List<string>(_localSlotNames) : null;
            return new CodeBlock(_instructions.ToArray(), new List<FenValue>(_constants), localSlots);
        }

        private void HoistEvalBlockFunctions(AstNode root)
        {
            // Traverse the AST to find block-scoped function declarations and explicitly hoist them to global.
            // In eval, Annex B hoisting means functions declared in blocks leak to the global scope.
            
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
                    if (!isTopLevel)
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
            
            // Now emit the global variable initialization for these functions explicitly
            foreach (var hoisted in functionToHoist)
            {
                if (string.IsNullOrEmpty(hoisted.Function?.Name)) continue;
                
                Emit(OpCode.LoadUndefined);
                EmitStoreVarByName(hoisted.Function.Name);
                Emit(OpCode.Pop);
            }
        }

        private void Visit(AstNode node)
        {
            if (node == null) return;

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
                if ((binExpr.Operator == "++" || binExpr.Operator == "--") && binExpr.Right == null)
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
                    
                    switch (binExpr.Operator)
                {
                    case "+": Emit(OpCode.Add); break;
                    case "-": Emit(OpCode.Subtract); break;
                    case "*": Emit(OpCode.Multiply); break;
                    case "/": Emit(OpCode.Divide); break;
                    case "%": Emit(OpCode.Modulo); break;
                    case "**": Emit(OpCode.Exponent); break;
                    case "==": Emit(OpCode.Equal); break;
                    case "===": Emit(OpCode.StrictEqual); break;
                    case "!=": Emit(OpCode.NotEqual); break;
                    case "!==": Emit(OpCode.StrictNotEqual); break;
                    case "<": Emit(OpCode.LessThan); break;
                    case ">": Emit(OpCode.GreaterThan); break;
                    case "<=": Emit(OpCode.LessThanOrEqual); break;
                    case ">=": Emit(OpCode.GreaterThanOrEqual); break;
                    case "in": Emit(OpCode.InOperator); break;
                    case "instanceof": Emit(OpCode.InstanceOf); break;
                    case "&": Emit(OpCode.BitwiseAnd); break;
                    case "|": Emit(OpCode.BitwiseOr); break;
                    case "^": Emit(OpCode.BitwiseXor); break;
                    case "<<": Emit(OpCode.LeftShift); break;
                    case ">>": Emit(OpCode.RightShift); break;
                    case ">>>": Emit(OpCode.UnsignedRightShift); break;
                    default:
                        throw new NotImplementedException($"Compiler: Binary operator '{binExpr.Operator}' not supported in Phase 1.");
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
                // Current runtime behavior treats BigInt literals as number fallback.
                // Keep bytecode semantics aligned until dedicated BigInt runtime representation lands.
                double numericValue = 0;
                if (!double.TryParse(bigIntLit.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out numericValue))
                {
                    numericValue = 0;
                }

                int idx = AddConstant(FenValue.FromNumber(numericValue));
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
                        throw new NotImplementedException("Compiler: Optional computed chain missing property expression.");
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
                                throw new NotImplementedException($"Compiler: Prefix operator '{prefixExpr.Operator}' not supported.");
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
                if (assign.Left is Identifier idNode)
                {
                    Visit(assign.Right);
                    EmitUpdateVarByName(idNode.Value);
                }
                else if (assign.Left is MemberExpression assignMember)
                {
                    Visit(assignMember.Object);
                    int idx = AddConstant(FenValue.FromString(assignMember.Property));
                    Emit(OpCode.LoadConst);
                    EmitInt32(idx);
                    Visit(assign.Right);
                    Emit(OpCode.StoreProp);
                }
                else if (assign.Left is IndexExpression assignIndex)
                {
                    Visit(assignIndex.Left);
                    Visit(assignIndex.Index);
                    Visit(assign.Right);
                    Emit(OpCode.StoreProp);
                }
                else if (assign.Left is ArrayLiteral || assign.Left is ObjectLiteral)
                {
                    EmitDestructuringAssignmentExpression(assign.Left, assign.Right);
                }
                else
                {
                    throw new NotImplementedException("Compiler: Complex assignment targets not supported in Phase 1.");
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
                    Visit(letStmt.Value);
                    if (letStmt.Name != null)
                    {
                        EmitStoreVarByName(letStmt.Name.Value);
                    }
                    Emit(OpCode.Pop); // declarations are statements; discard the stored value from the stack
                }
            }
            else if (node is EmptyExpression)
            {
                Emit(OpCode.LoadUndefined);
            }
            else if (node is BlockStatement blockStmt)
            {
                foreach (var stmt in blockStmt.Statements)
                {
                    Visit(stmt);
                }
            }
            else if (node is IfStatement ifStmt)
            {
                Visit(ifStmt.Condition);
                int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
                
                Visit(ifStmt.Consequence);

                if (ifStmt.Alternative != null)
                {
                    int jumpOverAltOffset = EmitJump(OpCode.Jump);
                    PatchJump(jumpIfFalseOffset);
                    Visit(ifStmt.Alternative);
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
                var funcCompiler = CreateFunctionCompiler(funcLit.Parameters, funcLit.Name);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(funcLit.Body, funcLit.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);

                var templateFunc = new FenFunction(funcLit.Parameters, compiledBlock, null)
                {
                    IsAsync = funcLit.IsAsync,
                    IsGenerator = funcLit.IsGenerator,
                    NeedsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap),
                    LocalMap = localMap
                };
                int idx = AddConstant(FenValue.FromFunction(templateFunc));
                
                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is AsyncFunctionExpression asyncFuncExpr)
            {
                ValidateSupportedParameterList(asyncFuncExpr.Parameters, "AsyncFunctionExpression");
                var funcCompiler = CreateFunctionCompiler(asyncFuncExpr.Parameters, asyncFuncExpr.Name?.Value);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(asyncFuncExpr.Body, asyncFuncExpr.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);

                var templateFunc = new FenFunction(asyncFuncExpr.Parameters, compiledBlock, null)
                {
                    Name = asyncFuncExpr.Name?.Value,
                    IsAsync = true,
                    NeedsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap),
                    LocalMap = localMap
                };
                int idx = AddConstant(FenValue.FromFunction(templateFunc));

                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is ArrowFunctionExpression arrowExpr)
            {
                ValidateSupportedParameterList(arrowExpr.Parameters, "ArrowFunctionExpression");
                var funcCompiler = CreateFunctionCompiler(arrowExpr.Parameters, null);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(arrowExpr.Body, arrowExpr.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);

                var templateFunc = new FenFunction(arrowExpr.Parameters, compiledBlock, null)
                {
                    IsArrowFunction = true,
                    IsAsync = arrowExpr.IsAsync,
                    NeedsArgumentsObject = false,
                    LocalMap = localMap
                };
                int idx = AddConstant(FenValue.FromFunction(templateFunc));

                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is FunctionDeclarationStatement funcDecl)
            {
                ValidateSupportedParameterList(funcDecl.Function.Parameters, "FunctionDeclarationStatement");
                var funcCompiler = CreateFunctionCompiler(funcDecl.Function.Parameters, funcDecl.Function.Name);
                var compiledBlock = funcCompiler.Compile(BuildCallableBody(funcDecl.Function.Body, funcDecl.Function.Parameters));
                var localMap = BuildFunctionLocalMap(compiledBlock);
                
                var templateFunc = new FenFunction(funcDecl.Function.Parameters, compiledBlock, null)
                {
                    Name = funcDecl.Function.Name,
                    IsAsync = funcDecl.Function.IsAsync,
                    IsGenerator = funcDecl.Function.IsGenerator,
                    NeedsArgumentsObject = BytecodeBlockMayReferenceArguments(compiledBlock, localMap),
                    LocalMap = localMap
                };
                int funcIdx = AddConstant(FenValue.FromFunction(templateFunc));

                Emit(OpCode.MakeClosure);
                EmitInt32(funcIdx);

                if (funcDecl.Function.Name != null)
                {
                    EmitStoreVarByName(funcDecl.Function.Name);
                }
                Emit(OpCode.Pop); // function declarations are statements; clean up the closure value from the stack
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
                EmitInt32(0);
                int finallyOffsetIndex = _instructions.Count;
                EmitInt32(-1); // finally not fully supported in VM yet

                Visit(tryStmt.Block);
                Emit(OpCode.PopExceptionHandler);
                
                int jumpOverCatch = EmitJump(OpCode.Jump);
                
                int catchStart = _instructions.Count;
                if (tryStmt.CatchBlock != null)
                {
                    byte[] catchBytes = BitConverter.GetBytes(catchStart);
                    for (int i=0; i<4; i++) _instructions[catchOffsetIndex+i] = catchBytes[i];
                    
                    if (tryStmt.CatchParameter != null)
                    {
                        EmitStoreVarByName(tryStmt.CatchParameter.Value);
                        Emit(OpCode.Pop); // pop stored exception value

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
                    byte[] cbytes = BitConverter.GetBytes(-1);
                    for(int i=0; i<4; i++) _instructions[catchOffsetIndex+i] = cbytes[i];
                }
                
                PatchJump(jumpOverCatch);
                
                if (tryStmt.FinallyBlock != null)
                {
                    Visit(tryStmt.FinallyBlock);
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
                throw new NotImplementedException($"Compiler: Node type {node.GetType().Name} not supported in Bytecode Phase.");
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
            Emit(OpCode.MakeObject);
            EmitInt32(0);

            foreach (var pair in objLit.Pairs)
            {
                Emit(OpCode.Dup);

                if (pair.Key.StartsWith("__spread_", StringComparison.Ordinal) && pair.Value is SpreadElement spreadElement)
                {
                    Visit(spreadElement.Argument);
                    Emit(OpCode.ObjectSpread);
                    Emit(OpCode.Pop);
                    continue;
                }

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

                Visit(pair.Value);
                Emit(OpCode.StoreProp);
                Emit(OpCode.Pop);
            }
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

            if (operand is OptionalChainExpression)
            {
                // Keep optional-chain delete on fallback path until optional-delete semantics
                // are lowered explicitly (delete obj?.prop / delete obj?.[key]).
                throw new NotImplementedException("Compiler: delete optional chaining is not supported in Bytecode Phase.");
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
                        // \xNN — valid only if followed by exactly 2 hex digits
                        bool valid = i + 3 < pattern.Length
                            && IsHexDigit(pattern[i + 2]) && IsHexDigit(pattern[i + 3]);
                        if (valid)
                        {
                            sb.Append('\\'); sb.Append('x'); i += 2;
                        }
                        else
                        {
                            // Annex B identity escape: \x → x
                            sb.Append('x'); i += 2;
                        }
                    }
                    else if (next == 'u')
                    {
                        // \uNNNN — valid only if followed by exactly 4 hex digits
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
                            // Annex B identity escape: \u → u
                            sb.Append('u'); i += 2;
                        }
                    }
                    else if (next == 'k' && i + 2 < pattern.Length && pattern[i + 2] == '<')
                    {
                        // \k<name> — invalid if named group doesn't exist
                        int closeIdx = pattern.IndexOf('>', i + 3);
                        if (closeIdx >= 0)
                        {
                            string groupName = pattern.Substring(i + 3, closeIdx - (i + 3));
                            if (!namedGroups.Contains(groupName))
                            {
                                // Identity escape: \k → k, keep <name> as literal
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

            throw new NotImplementedException($"Compiler: {owner} requires expression-bodied block in Bytecode Phase.");
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
            Visit(forInStmt.Object);
            Emit(OpCode.MakeKeysIterator);

            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext();
            var loopContext = PushLoopContext(loopStart);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorMoveNext);
            int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorCurrent);

            if (forInStmt.Variable != null)
            {
                EmitStoreVarByName(forInStmt.Variable.Value);
                Emit(OpCode.Pop); // pop the assigned key value
            }
            else if (forInStmt.DestructuringPattern != null)
            {
                string destructureSource = NextSyntheticName("forin");
                EmitStoreVarByName(destructureSource);
                Emit(OpCode.Pop); // pop the stored key value
                EmitDestructuringFromVariable(forInStmt.DestructuringPattern, destructureSource);
            }
            else
            {
                Emit(OpCode.Pop); // no binding target, discard yielded key
            }

            Visit(forInStmt.Body);
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
            Visit(forOfStmt.Iterable);
            Emit(OpCode.MakeValuesIterator);

            int loopStart = _instructions.Count;
            var breakContext = PushBreakContext();
            var loopContext = PushLoopContext(loopStart);
            var labelContext = labelName != null ? PushLabelContext(labelName, loopContext) : null;

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorMoveNext);
            int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);

            Emit(OpCode.Dup);
            Emit(OpCode.IteratorCurrent);

            if (forOfStmt.Variable != null)
            {
                EmitStoreVarByName(forOfStmt.Variable.Value);
                Emit(OpCode.Pop); // pop the assigned value
            }
            else if (forOfStmt.DestructuringPattern != null)
            {
                string destructureSource = NextSyntheticName("forof");
                EmitStoreVarByName(destructureSource);
                Emit(OpCode.Pop); // pop the stored value
                EmitDestructuringFromVariable(forOfStmt.DestructuringPattern, destructureSource);
            }
            else
            {
                Emit(OpCode.Pop); // no binding target, discard yielded value
            }

            Visit(forOfStmt.Body);
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

        private void EmitBreakStatement(BreakStatement breakStmt)
        {
            if (breakStmt.Label != null)
            {
                if (!TryFindLabelContext(breakStmt.Label.Value, out var labelContext))
                {
                    throw new NotImplementedException($"Compiler: break label '{breakStmt.Label.Value}' is not supported in Bytecode Phase.");
                }

                int labeledJumpOffset = EmitJump(OpCode.Jump);
                labelContext.BreakJumpOffsets.Add(labeledJumpOffset);
                return;
            }

            if (_breakContexts.Count == 0)
            {
                throw new NotImplementedException("Compiler: break used outside loop/switch is not supported in Bytecode Phase.");
            }

            int jumpOffset = EmitJump(OpCode.Jump);
            _breakContexts.Peek().BreakJumpOffsets.Add(jumpOffset);
        }

        private void EmitContinueStatement(ContinueStatement continueStmt)
        {
            if (continueStmt.Label != null)
            {
                if (!TryFindLabelContext(continueStmt.Label.Value, out var labelContext))
                {
                    throw new NotImplementedException($"Compiler: continue label '{continueStmt.Label.Value}' is not supported in Bytecode Phase.");
                }

                if (labelContext.LoopContext == null)
                {
                    throw new NotImplementedException($"Compiler: continue label '{continueStmt.Label.Value}' does not reference a loop in Bytecode Phase.");
                }

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
                throw new NotImplementedException("Compiler: continue used outside loop is not supported in Bytecode Phase.");
            }

            var loopContext = _loopContexts.Peek();
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

        private BreakContext PushBreakContext()
        {
            var context = new BreakContext();
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

        private LoopContext PushLoopContext(int continueTarget)
        {
            var context = new LoopContext
            {
                ContinueTarget = continueTarget
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
                    throw new NotImplementedException($"Compiler: Logical assignment operator '{logicalAssignExpr.Operator}' not supported.");
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
                throw new NotImplementedException($"Compiler: Compound assignment operator '{op}' not supported.");
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
                    throw new NotImplementedException($"Compiler: Compound assignment operator '{op}' not supported.");
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
        }

        private void EmitMethodDefinition(MethodDefinition methodDefinition)
        {
            if (methodDefinition?.Value == null)
            {
                Emit(OpCode.LoadUndefined);
                return;
            }

            Visit(methodDefinition.Value);
        }

        private void EmitClassProperty(ClassProperty classProperty)
        {
            if (classProperty?.Value != null)
            {
                Visit(classProperty.Value);
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
                Visit(exportDeclaration.DefaultExpression);
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
                className = NextSyntheticName("class_expr");
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
            if (classStatement?.Name == null || string.IsNullOrEmpty(classStatement.Name.Value))
            {
                throw new NotImplementedException("Compiler: Anonymous class statement is not supported in Bytecode Phase.");
            }

            string className = classStatement.Name.Value;
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

                    EmitLoadVarByName(className);
                    string propertyName = classProperty.IsPrivate
                        ? "#" + (classProperty.Key?.Value ?? string.Empty)
                        : (classProperty.Key?.Value ?? string.Empty);
                    int propKeyIdx = AddConstant(FenValue.FromString(propertyName));
                    Emit(OpCode.LoadConst);
                    EmitInt32(propKeyIdx);
                    if (classProperty.Value != null)
                    {
                        Visit(classProperty.Value);
                    }
                    else
                    {
                        Emit(OpCode.LoadUndefined);
                    }
                    Emit(OpCode.StoreProp);
                    Emit(OpCode.Pop);
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

            string methodName = methodDefinition.IsPrivate
                ? "#" + methodDefinition.Key.Value
                : methodDefinition.Key.Value;
            int methodNameIdx = AddConstant(FenValue.FromString(methodName ?? string.Empty));
            Emit(OpCode.LoadConst);
            EmitInt32(methodNameIdx);

            Visit(methodDefinition.Value);

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

        /// <summary>
        /// Emit an assignment (not declaration): walks the scope chain to update an existing binding,
        /// falling back to implicit global creation if not found.
        /// Use this for x = value (AssignmentExpression), NOT for var/let/const declarations.
        /// </summary>
        private void EmitUpdateVarByName(string variableName)
        {
            // Local slots are always in the current frame's environment — StoreLocal is correct for assignment too.
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
                    AddLocalBinding(forInStatement.Variable?.Value);
                    CollectBindingNamesFromPattern(forInStatement.DestructuringPattern);
                    CollectLocalBindings(forInStatement.Body);
                    break;
                case ForOfStatement forOfStatement:
                    AddLocalBinding(forOfStatement.Variable?.Value);
                    CollectBindingNamesFromPattern(forOfStatement.DestructuringPattern);
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

            int skipOffsetNull = -1;
            int skipOffsetType = -1;
            if (applyObjectGuard)
            {
                // Skip destructuring if source is null (real JS throws TypeError; here we silently skip)
                EmitLoadVarByName(sourceVariableName);
                Emit(OpCode.LoadNull);
                Emit(OpCode.StrictEqual);
                skipOffsetNull = EmitJump(OpCode.JumpIfTrue);

                // Skip destructuring if source is undefined
                EmitLoadVarByName(sourceVariableName);
                Emit(OpCode.LoadUndefined);
                Emit(OpCode.StrictEqual);
                skipOffsetType = EmitJump(OpCode.JumpIfTrue);
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
                EmitApplyDefaultIfUndefined(sourceVariableName, assignmentPattern.Right);
                EmitDestructuringFromVariableCore(assignmentPattern.Left, sourceVariableName, applyObjectGuard: false);
            }
            else if (!(pattern is EmptyExpression) && !(pattern is UndefinedLiteral))
            {
                throw new NotImplementedException($"Compiler: Unsupported destructuring pattern node '{pattern.GetType().Name}'.");
            }

            if (applyObjectGuard)
            {
                int patternEnd = _instructions.Count;
                PatchJumpTo(skipOffsetNull, patternEnd);
                PatchJumpTo(skipOffsetType, patternEnd);
            }
        }

        private void EmitDestructuringArrayBinding(ArrayLiteral arrayPattern, string sourceVariableName)
        {
            if (arrayPattern?.Elements == null)
            {
                return;
            }

            for (int i = 0; i < arrayPattern.Elements.Count; i++)
            {
                var element = arrayPattern.Elements[i];
                if (element == null || element is UndefinedLiteral || element is EmptyExpression)
                {
                    continue;
                }

                if (element is SpreadElement spreadElement)
                {
                    EmitArrayRestBinding(sourceVariableName, i, spreadElement.Argument);
                    break;
                }

                string elementVariable = NextSyntheticName("arr_elem");
                EmitLoadPropertyByKeyToVariable(sourceVariableName, FenValue.FromNumber(i), elementVariable);
                EmitDestructuringTargetBinding(element, elementVariable);
            }
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
                    if (spreadTarget == null)
                    {
                        throw new NotImplementedException("Compiler: Object destructuring spread targets are not supported in Bytecode Phase.");
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
                        throw new NotImplementedException("Compiler: Computed object destructuring key expression is missing.");
                    }

                    EmitLoadPropertyByExpressionToVariable(sourceVariableName, computedKeyExpression, propertyVariable);
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
                EmitApplyDefaultIfUndefined(valueVariable, assignmentTarget.Right);
                EmitDestructuringTargetBinding(assignmentTarget.Left, valueVariable);
                return;
            }

            if (target is ArrayLiteral || target is ObjectLiteral)
            {
                EmitDestructuringFromVariable(target, valueVariable);
                return;
            }

            if (target is EmptyExpression || target is UndefinedLiteral)
            {
                return;
            }

            throw new NotImplementedException($"Compiler: Unsupported destructuring binding target '{target.GetType().Name}'.");
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
                        throw new NotImplementedException("Compiler: Computed object destructuring key expression is missing.");
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

        private void EmitApplyDefaultIfUndefined(string valueVariable, Expression defaultExpression)
        {
            if (string.IsNullOrEmpty(valueVariable) || defaultExpression == null)
            {
                return;
            }

            EmitLoadVarByName(valueVariable);
            Emit(OpCode.LoadUndefined);
            Emit(OpCode.StrictEqual);
            int skipDefaultOffset = EmitJump(OpCode.JumpIfFalse);

            Visit(defaultExpression);
            EmitStoreVarByName(valueVariable);
            Emit(OpCode.Pop);

            PatchJumpTo(skipDefaultOffset, _instructions.Count);
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
                throw new NotImplementedException($"Compiler: Callable body type '{body?.GetType().Name ?? "null"}' is not supported.");
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
                    throw new NotImplementedException($"Compiler: Null parameter in {owner} is not supported.");
                }

                if (parameter.DestructuringPattern != null && !IsSupportedDestructuringPattern(parameter.DestructuringPattern))
                {
                    throw new NotImplementedException($"Compiler: Unsupported destructuring parameter pattern in {owner} is not supported in Bytecode Phase.");
                }
            }
        }

        private sealed class BreakContext
        {
            public readonly List<int> BreakJumpOffsets = new List<int>();
        }

        private sealed class LoopContext
        {
            public int ContinueTarget { get; set; } = -1;
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
