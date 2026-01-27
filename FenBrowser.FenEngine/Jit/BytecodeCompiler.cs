using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Jit
{
    public class BytecodeCompiler
    {
        private List<Instruction> _instructions = new List<Instruction>();
        private List<string> _locals = new List<string>();

        public BytecodeUnit Compile(FenBrowser.FenEngine.Core.Program program)
        {
            _instructions = new List<Instruction>();
            _locals = new List<string>();

            foreach (var stmt in program.Statements)
            {
                CompileNode(stmt);
            }

            return new BytecodeUnit
            {
                Name = "main",
                Instructions = _instructions,
                Locals = _locals
            };
        }

        public BytecodeUnit CompileFunction(FunctionLiteral func)
        {
            _instructions = new List<Instruction>();
            _locals = new List<string>(func.Parameters.Select(p => p.Value));

            CompileNode(func.Body);

            return new BytecodeUnit
            {
                Name = func.Name ?? "anonymous",
                Instructions = _instructions,
                Parameters = func.Parameters.Select(p => p.Value).ToList(),
                Locals = _locals,
                IsAsync = func.IsAsync,
                IsGenerator = func.IsGenerator,
                LocalMap = _locals.Select((name, idx) => new { name, idx }).ToDictionary(x => x.name, x => x.idx)
            };
        }

        private void CompileNode(AstNode node)
        {
            if (node == null) return;

            switch (node)
            {
                case ExpressionStatement exprStmt:
                    CompileExpression(exprStmt.Expression);
                    Emit(OpCode.Pop);
                    break;

                case BlockStatement block:
                    foreach (var s in block.Statements) CompileNode(s);
                    break;

                case LetStatement letStmt:
                    CompileExpression(letStmt.Value);
                    EmitVarStore(letStmt.Name.Value);
                    break;

                case ReturnStatement retStmt:
                    CompileExpression(retStmt.ReturnValue);
                    Emit(OpCode.Return);
                    break;

                case IfExpression ifExpr:
                    CompileIfExpression(ifExpr);
                    break;

                case WhileStatement whileStmt:
                    CompileWhileStatement(whileStmt);
                    break;

                case ForStatement forStmt:
                    CompileForStatement(forStmt);
                    break;
                
                case Expression expr:
                    CompileExpression(expr);
                    break;
            }
        }

        private void CompileExpression(Expression expr)
        {
            if (expr == null)
            {
                Emit(OpCode.PushConst, null);
                return;
            }

            switch (expr)
            {
                case IntegerLiteral intLit:
                    Emit(OpCode.PushConst, intLit.Value);
                    break;

                case DoubleLiteral doubleLit:
                    Emit(OpCode.PushConst, doubleLit.Value);
                    break;

                case StringLiteral strLit:
                    Emit(OpCode.PushConst, strLit.Value);
                    break;

                case BooleanLiteral boolLit:
                    Emit(OpCode.PushConst, boolLit.Value);
                    break;

                case NullLiteral _:
                    Emit(OpCode.PushConst, null);
                    break;

                case UndefinedLiteral _:
                    Emit(OpCode.PushConst, null); // Or better, add a specific Undefined opcode if PushConst handles null as Null
                    break;

                case Identifier ident:
                    EmitVarLoad(ident.Value);
                    break;

                case InfixExpression infix:
                    if (infix.Operator == "&&")
                    {
                        CompileExpression(infix.Left);
                        Emit(OpCode.Dup);
                        int jumpIdx = Emit(OpCode.JumpIfFalse, 0);
                        Emit(OpCode.Pop);
                        CompileExpression(infix.Right);
                        PatchJump(jumpIdx, _instructions.Count);
                    }
                    else if (infix.Operator == "||")
                    {
                        CompileExpression(infix.Left);
                        Emit(OpCode.Dup);
                        int jumpIdx = Emit(OpCode.JumpIfTrue, 0);
                        Emit(OpCode.Pop);
                        CompileExpression(infix.Right);
                        PatchJump(jumpIdx, _instructions.Count);
                    }
                    else if (infix.Operator == "++" && infix.Right == null)
                    {
                        CompilePostfixUpdate(infix.Left, "++");
                    }
                    else if (infix.Operator == "--" && infix.Right == null)
                    {
                        CompilePostfixUpdate(infix.Left, "--");
                    }
                    else
                    {
                        CompileInfixExpression(infix);
                    }
                    break;

                case PrefixExpression prefix:
                    if (prefix.Operator == "++" || prefix.Operator == "--")
                    {
                        CompilePrefixUpdate(prefix.Right, prefix.Operator);
                    }
                    else
                    {
                        CompilePrefixExpression(prefix);
                    }
                    break;

                case CallExpression call:
                    if (call.Function is MemberExpression me)
                    {
                        foreach (var arg in call.Arguments) CompileExpression(arg);
                        CompileExpression(me.Object);
                        Emit(OpCode.Dup);
                        Emit(OpCode.GetProp, me.Property);
                    }
                    else
                    {
                        foreach (var arg in call.Arguments) CompileExpression(arg);
                        Emit(OpCode.PushConst, null); // This context (null for globals)
                        CompileExpression(call.Function); // The function itself at the TOP
                    }
                    Emit(OpCode.Call, call.Arguments.Count);
                    break;

                case MemberExpression member:
                    CompileExpression(member.Object);
                    Emit(OpCode.GetProp, member.Property);
                    break;

                case AssignmentExpression assign:
                    if (assign.Left is Identifier id)
                    {
                        CompileExpression(assign.Right);
                        EmitVarStore(id.Value);
                    }
                    else if (assign.Left is MemberExpression m)
                    {
                        CompileExpression(m.Object);
                        CompileExpression(assign.Right);
                        Emit(OpCode.SetProp, m.Property);
                    }
                    break;

                case CompoundAssignmentExpression compound:
                    CompileCompoundAssignment(compound);
                    break;

                case ArrayLiteral arrayLit:
                    foreach (var el in arrayLit.Elements) CompileExpression(el);
                    Emit(OpCode.CreateArray, arrayLit.Elements.Count);
                    break;

                case ObjectLiteral objectLit:
                    foreach (var pair in objectLit.Pairs)
                    {
                        Emit(OpCode.PushConst, pair.Key);
                        CompileExpression(pair.Value);
                    }
                    Emit(OpCode.CreateObject, objectLit.Pairs.Count);
                    break;
            }
        }

        private void CompileInfixExpression(InfixExpression infix)
        {
            CompileExpression(infix.Left);
            CompileExpression(infix.Right);

            switch (infix.Operator)
            {
                case "+": Emit(OpCode.Add); break;
                case "-": Emit(OpCode.Sub); break;
                case "*": Emit(OpCode.Mul); break;
                case "/": Emit(OpCode.Div); break;
                case "%": Emit(OpCode.Mod); break;
                case "==": 
                case "===": Emit(OpCode.Eq); break;
                case "!=": 
                case "!==": Emit(OpCode.NotEq); break;
                case "<": Emit(OpCode.Lt); break;
                case ">": Emit(OpCode.Gt); break;
                case "<=": Emit(OpCode.LtEq); break;
                case ">=": Emit(OpCode.GtEq); break;
            }
        }

        private void CompilePrefixExpression(PrefixExpression prefix)
        {
            CompileExpression(prefix.Right);
            switch (prefix.Operator)
            {
                case "-": Emit(OpCode.PushConst, -1L); Emit(OpCode.Mul); break;
                case "!": Emit(OpCode.Not); break;
                case "typeof": Emit(OpCode.Typeof); break;
            }
        }

        private void CompileIfExpression(IfExpression ifExpr)
        {
            CompileExpression(ifExpr.Condition);
            int jumpIfFalseIdx = Emit(OpCode.JumpIfFalse, 0);
            CompileNode(ifExpr.Consequence);
            if (ifExpr.Alternative != null)
            {
                int jumpAfterIdx = Emit(OpCode.Jump, 0);
                PatchJump(jumpIfFalseIdx, _instructions.Count);
                CompileNode(ifExpr.Alternative);
                PatchJump(jumpAfterIdx, _instructions.Count);
            }
            else
            {
                PatchJump(jumpIfFalseIdx, _instructions.Count);
            }
        }

        private void CompileWhileStatement(WhileStatement stmt)
        {
            int startIdx = _instructions.Count;
            CompileExpression(stmt.Condition);
            int exitJumpIdx = Emit(OpCode.JumpIfFalse, 0);
            CompileNode(stmt.Body);
            Emit(OpCode.Jump, startIdx);
            PatchJump(exitJumpIdx, _instructions.Count);
        }

        private void CompileForStatement(ForStatement stmt)
        {
            if (stmt.Init != null) CompileNode(stmt.Init);
            int conditionIdx = _instructions.Count;
            if (stmt.Condition != null) CompileExpression(stmt.Condition);
            else Emit(OpCode.PushConst, true);
            int exitJumpIdx = Emit(OpCode.JumpIfFalse, 0);
            CompileNode(stmt.Body);
            if (stmt.Update != null) CompileNode(stmt.Update);
            Emit(OpCode.Jump, conditionIdx);
            PatchJump(exitJumpIdx, _instructions.Count);
        }

        private int Emit(OpCode op, object operand = null)
        {
            _instructions.Add(new Instruction(op, operand));
            return _instructions.Count - 1;
        }

        private void PatchJump(int idx, int address)
        {
            var inst = _instructions[idx];
            inst.Operand = address;
            _instructions[idx] = inst;
        }

        private void CompileCompoundAssignment(CompoundAssignmentExpression compound)
        {
            if (compound.Left is Identifier id)
            {
                EmitVarLoad(id.Value);
                CompileExpression(compound.Right);
                EmitOpForCompound(compound.Operator);
                EmitVarStore(id.Value);
            }
            else if (compound.Left is MemberExpression m)
            {
                CompileExpression(m.Object);
                Emit(OpCode.Dup);
                Emit(OpCode.GetProp, m.Property);
                CompileExpression(compound.Right);
                EmitOpForCompound(compound.Operator);
                Emit(OpCode.SetProp, m.Property);
            }
        }

        private void EmitOpForCompound(string op)
        {
            switch (op.Substring(0, op.Length - 1))
            {
                case "+": Emit(OpCode.Add); break;
                case "-": Emit(OpCode.Sub); break;
                case "*": Emit(OpCode.Mul); break;
                case "/": Emit(OpCode.Div); break;
                case "%": Emit(OpCode.Mod); break;
                case "**": Emit(OpCode.Exp); break;
            }
        }

        private void CompilePrefixUpdate(Expression operand, string op)
        {
            if (operand is Identifier id)
            {
                EmitVarLoad(id.Value);
                Emit(OpCode.PushConst, 1.0);
                Emit(op == "++" ? OpCode.Add : OpCode.Sub);
                EmitVarStore(id.Value);
            }
            else if (operand is MemberExpression m)
            {
                CompileExpression(m.Object);
                Emit(OpCode.Dup);
                Emit(OpCode.GetProp, m.Property);
                Emit(OpCode.PushConst, 1.0);
                Emit(op == "++" ? OpCode.Add : OpCode.Sub);
                Emit(OpCode.SetProp, m.Property);
            }
        }

        private void CompilePostfixUpdate(Expression operand, string op)
        {
            if (operand is Identifier id)
            {
                EmitVarLoad(id.Value);
                Emit(OpCode.Dup); // [Old, Old]
                Emit(OpCode.PushConst, 1.0);
                Emit(op == "++" ? OpCode.Add : OpCode.Sub);
                EmitVarStore(id.Value); // pops Res, returns Res. Stack: [Old, Res]
                Emit(OpCode.Pop); // Clear Res. Stack: [Old]
            }
            else if (operand is MemberExpression m)
            {
                // [Obj] -> [Obj, Obj] -> [Obj, Old] -> [Old, Obj] -> [Old, Obj, Obj] -> [Old, Obj, Old] -> [Old, Obj, Res] -> [Old, Res] -> [Old]
                CompileExpression(m.Object);
                Emit(OpCode.Dup);
                Emit(OpCode.GetProp, m.Property); // [Obj, Old]
                Emit(OpCode.Swap);                // [Old, Obj]
                Emit(OpCode.Dup);                 // [Old, Obj, Obj]
                Emit(OpCode.Swap);                // [Old, Obj, Old]
                Emit(OpCode.PushConst, 1.0);
                Emit(op == "++" ? OpCode.Add : OpCode.Sub); // [Old, Obj, Res]
                Emit(OpCode.SetProp, m.Property); // pops Res, pops Obj, leaves Res. Stack: [Old, Res]
                Emit(OpCode.Pop);                 // pops Res. Stack: [Old]
            }
        }

        private void EmitVarLoad(string name)
        {
            int idx = GetLocalIndex(name);
            if (idx != -1) Emit(OpCode.LoadLocal, idx);
            else Emit(OpCode.LoadVar, name);
        }

        private void EmitVarStore(string name)
        {
            int idx = GetLocalIndex(name);
            if (idx == -1)
            {
                _locals.Add(name);
                idx = _locals.Count - 1;
            }
            Emit(OpCode.StoreLocal, idx);
        }

        private int GetLocalIndex(string name)
        {
            for (int i = 0; i < _locals.Count; i++)
            {
                if (_locals[i] == name) return i;
            }
            return -1;
        }
    }
}
