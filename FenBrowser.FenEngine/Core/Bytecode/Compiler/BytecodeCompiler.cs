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
            }
            else if (node is InfixExpression binExpr)
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
                    case "==": Emit(OpCode.Equal); break;
                    case "===": Emit(OpCode.StrictEqual); break;
                    case "<": Emit(OpCode.LessThan); break;
                    case ">": Emit(OpCode.GreaterThan); break;
                    default:
                        throw new NotImplementedException($"Compiler: Binary operator '{binExpr.Operator}' not supported in Phase 1.");
                }
            }
            else if (node is IntegerLiteral intLit)
            {
                int idx = AddConstant(FenValue.FromNumber(intLit.Value));
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
            else if (node is Identifier identifier)
            {
                int idx = AddConstant(FenValue.FromString(identifier.Value));
                Emit(OpCode.LoadVar);
                EmitInt32(idx);
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
            else
            {
                throw new NotImplementedException($"Compiler: Node type {node.GetType().Name} not supported in Bytecode Phase 1.");
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
    }
}
