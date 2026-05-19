using FenBrowser.Js.Ast;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;

namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeCompiler
{
    private readonly List<Instruction> _instructions = new();
    private readonly List<JsValue> _constants = new();
    private readonly Dictionary<string, int> _variables = new(StringComparer.Ordinal);
    private int _nextRegister = 1;

    public BytecodeFunction CompileScript(SourceText source)
    {
        var program = JsParser.ParseScript(source);
        return CompileProgram(program);
    }

    public BytecodeFunction CompileProgram(ProgramNode program)
    {
        _instructions.Clear();
        _constants.Clear();
        _variables.Clear();
        _nextRegister = 1;

        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
        }

        _instructions.Add(new Instruction(OpCode.Return, 0, 0, 0));

        return new BytecodeFunction
        {
            Instructions = _instructions.ToArray(),
            Constants = _constants.ToArray(),
            VariableSlots = new Dictionary<string, int>(_variables),
            RegisterCount = Math.Max(2, _nextRegister)
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
                    var slot = GetOrCreateVariableSlot(d.Identifier);
                    if (d.Initializer is not null)
                    {
                        var reg = CompileExpression(d.Initializer);
                        _instructions.Add(new Instruction(OpCode.StoreVar, reg, slot, 0));
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
            case ReturnStatementNode returnStmt:
                CompileReturnStatement(returnStmt);
                break;
            case ThrowStatementNode throwStmt:
                CompileThrowStatement(throwStmt);
                break;
            case TryCatchStatementNode tryCatchStmt:
                CompileTryCatchStatement(tryCatchStmt);
                break;
            default:
                // Minimal compiler slice currently targets literals/arithmetic/variables.
                break;
        }
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
        CompileStatement(whileStmt.Body);
        _instructions.Add(new Instruction(OpCode.Jump, loopStart, 0, 0));
        PatchJump(jumpIfFalseIndex, _instructions.Count);
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
            case IdentifierExpressionNode id:
            {
                var reg = AllocateRegister();
                var slot = GetOrCreateVariableSlot(id.Name);
                _instructions.Add(new Instruction(OpCode.LoadVar, reg, slot, 0));
                return reg;
            }
            case AssignmentExpressionNode assign when assign.Left is IdentifierExpressionNode id:
            {
                var rightReg = CompileExpression(assign.Right);
                var slot = GetOrCreateVariableSlot(id.Name);
                _instructions.Add(new Instruction(OpCode.StoreVar, rightReg, slot, 0));
                return rightReg;
            }
            case BinaryExpressionNode bin:
            {
                var leftReg = CompileExpression(bin.Left);
                var rightReg = CompileExpression(bin.Right);
                var dest = AllocateRegister();
                var op = bin.Operator switch
                {
                    "+" => OpCode.Add,
                    "-" => OpCode.Sub,
                    "*" => OpCode.Mul,
                    "/" => OpCode.Div,
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
