using System;
using System.Collections.Generic;
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

        public BytecodeCompiler()
        {
        }

        public CodeBlock Compile(AstNode root)
        {
            _instructions.Clear();
            _constants.Clear();

            Visit(root);

            // Ensure every block ends with a Halt
            Emit(OpCode.Halt);

            return new CodeBlock(_instructions.ToArray(), new List<FenValue>(_constants));
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
            else if (node is Identifier identifier)
            {
                int idx = AddConstant(FenValue.FromString(identifier.Value));
                Emit(OpCode.LoadVar);
                EmitInt32(idx);
            }
            else if (node is ArrayLiteral arrayLit)
            {
                foreach (var el in arrayLit.Elements)
                {
                    Visit(el);
                }
                Emit(OpCode.MakeArray);
                EmitInt32(arrayLit.Elements.Count);
            }
            else if (node is ObjectLiteral objLit)
            {
                foreach (var prop in objLit.Pairs)
                {
                    int idx = AddConstant(FenValue.FromString(prop.Key));
                    Emit(OpCode.LoadConst);
                    EmitInt32(idx);
                    
                    Visit(prop.Value);
                }
                Emit(OpCode.MakeObject);
                EmitInt32(objLit.Pairs.Count);
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
                    Visit(prefixExpr.Right);
                    switch (prefixExpr.Operator)
                    {
                        case "-": Emit(OpCode.Negate); break;
                        case "!": Emit(OpCode.LogicalNot); break;
                        case "~": Emit(OpCode.BitwiseNot); break;
                        case "typeof": Emit(OpCode.Typeof); break;
                        default:
                            throw new NotImplementedException($"Compiler: Prefix operator '{prefixExpr.Operator}' not supported.");
                    }
                }
            }
            else if (node is LogicalAssignmentExpression logicalAssignExpr)
            {
                VisitLogicalAssignment(logicalAssignExpr);
            }
            else if (node is AssignmentExpression assign)
            {
                if (assign.Left is Identifier idNode)
                {
                    Visit(assign.Right);
                    int idx = AddConstant(FenValue.FromString(idNode.Value));
                    Emit(OpCode.StoreVar);
                    EmitInt32(idx);
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
                else
                {
                    throw new NotImplementedException("Compiler: Complex assignment targets not supported in Phase 1.");
                }
            }
            else if (node is LetStatement letStmt)
            {
                if (letStmt.Value != null)
                {
                    Visit(letStmt.Value);
                    if (letStmt.Name != null)
                    {
                        int idx = AddConstant(FenValue.FromString(letStmt.Name.Value));
                        Emit(OpCode.StoreVar);
                        EmitInt32(idx);
                    }
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
                int loopStart = _instructions.Count;
                Visit(whileStmt.Condition);
                int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
                
                Visit(whileStmt.Body);
                
                Emit(OpCode.Jump);
                EmitInt32(loopStart);
                
                PatchJump(jumpIfFalseOffset);
            }
            else if (node is DoWhileStatement doWhileStmt)
            {
                int loopStart = _instructions.Count;
                Visit(doWhileStmt.Body);
                Visit(doWhileStmt.Condition);
                Emit(OpCode.JumpIfTrue);
                EmitInt32(loopStart);
            }
            else if (node is ForStatement forStmt)
            {
                if (forStmt.Init != null) Visit(forStmt.Init);
                
                int loopStart = _instructions.Count;
                int jumpIfFalseOffset = -1;
                
                if (forStmt.Condition != null)
                {
                    Visit(forStmt.Condition);
                    jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
                }
                
                if (forStmt.Body != null) Visit(forStmt.Body);
                if (forStmt.Update != null) Visit(forStmt.Update);
                
                Emit(OpCode.Jump);
                EmitInt32(loopStart);
                
                if (jumpIfFalseOffset != -1)
                {
                    PatchJump(jumpIfFalseOffset);
                }
            }
            else if (node is ForInStatement forInStmt)
            {
                Visit(forInStmt.Object);
                Emit(OpCode.MakeKeysIterator);
                
                int loopStart = _instructions.Count;
                Emit(OpCode.Dup);
                Emit(OpCode.IteratorMoveNext);
                int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
                
                Emit(OpCode.Dup);
                Emit(OpCode.IteratorCurrent);
                
                if (forInStmt.Variable != null)
                {
                    int varIdx = AddConstant(FenValue.FromString(forInStmt.Variable.Value));
                    Emit(OpCode.StoreVar);
                    EmitInt32(varIdx);
                }
                Emit(OpCode.Pop); // pop the assigned/yielded key
                
                Visit(forInStmt.Body);
                Emit(OpCode.Jump);
                EmitInt32(loopStart);
                
                PatchJump(jumpIfFalseOffset);
                Emit(OpCode.Pop); // Pop iterator
            }
            else if (node is ForOfStatement forOfStmt)
            {
                Visit(forOfStmt.Iterable);
                Emit(OpCode.MakeValuesIterator);
                
                int loopStart = _instructions.Count;
                Emit(OpCode.Dup);
                Emit(OpCode.IteratorMoveNext);
                int jumpIfFalseOffset = EmitJump(OpCode.JumpIfFalse);
                
                Emit(OpCode.Dup);
                Emit(OpCode.IteratorCurrent);
                
                if (forOfStmt.Variable != null)
                {
                    int varIdx = AddConstant(FenValue.FromString(forOfStmt.Variable.Value));
                    Emit(OpCode.StoreVar);
                    EmitInt32(varIdx);
                }
                Emit(OpCode.Pop); // pop the assigned/yielded value
                
                Visit(forOfStmt.Body);
                Emit(OpCode.Jump);
                EmitInt32(loopStart);
                
                PatchJump(jumpIfFalseOffset);
                Emit(OpCode.Pop); // Pop iterator
            }
            else if (node is FunctionLiteral funcLit)
            {
                var funcCompiler = new BytecodeCompiler();
                var compiledBlock = funcCompiler.Compile(funcLit.Body);
                
                var templateFunc = new FenFunction(funcLit.Parameters, compiledBlock, null);
                int idx = AddConstant(FenValue.FromFunction(templateFunc));
                
                Emit(OpCode.MakeClosure);
                EmitInt32(idx);
            }
            else if (node is FunctionDeclarationStatement funcDecl)
            {
                var funcCompiler = new BytecodeCompiler();
                var compiledBlock = funcCompiler.Compile(funcDecl.Function.Body);
                
                var templateFunc = new FenFunction(funcDecl.Function.Parameters, compiledBlock, null);
                int funcIdx = AddConstant(FenValue.FromFunction(templateFunc));
                
                Emit(OpCode.MakeClosure);
                EmitInt32(funcIdx);
                
                if (funcDecl.Function.Name != null)
                {
                    int nameIdx = AddConstant(FenValue.FromString(funcDecl.Function.Name));
                    Emit(OpCode.StoreVar);
                    EmitInt32(nameIdx);
                }
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
            else if (node is BitwiseNotExpression bitwiseNotExpr)
            {
                Visit(bitwiseNotExpr.Operand);
                Emit(OpCode.BitwiseNot);
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
                        int varIdx = AddConstant(FenValue.FromString(tryStmt.CatchParameter.Value));
                        Emit(OpCode.StoreVar);
                        EmitInt32(varIdx);
                    }
                    Emit(OpCode.Pop); // Pop exception value
                    
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
                Visit(callExpr.Function);
                foreach (var arg in callExpr.Arguments)
                {
                    Visit(arg);
                }
                Emit(OpCode.Call);
                EmitInt32(callExpr.Arguments.Count);
            }
            else if (node is NewExpression newExpr)
            {
                Visit(newExpr.Constructor);
                foreach (var arg in newExpr.Arguments)
                {
                    Visit(arg);
                }
                Emit(OpCode.Construct);
                EmitInt32(newExpr.Arguments.Count);
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
    }
}
