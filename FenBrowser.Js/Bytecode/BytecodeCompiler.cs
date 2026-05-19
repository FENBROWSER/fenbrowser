using FenBrowser.Js.Ast;
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

    private readonly List<Instruction> _instructions = new();
    private readonly List<JsValue> _constants = new();
    private readonly Dictionary<string, int> _variables = new(StringComparer.Ordinal);
    private readonly List<string> _propertyNames = new();
    private readonly Dictionary<string, int> _propertyNameToIndex = new(StringComparer.Ordinal);
    private readonly List<BytecodeFunction> _nestedFunctions = new();
    private readonly List<string> _parameterNames = new();
    private readonly Stack<LoopContext> _loopStack = new();
    private string? _name;
    private int _nextRegister = 1;

    public BytecodeFunction CompileScript(SourceText source)
    {
        var program = JsParser.ParseScript(source);
        return CompileProgram(program);
    }

    public BytecodeFunction CompileProgram(ProgramNode program)
    {
        return CompileProgramCore(program, parameters: Array.Empty<string>(), name: null);
    }

    private BytecodeFunction CompileProgramCore(ProgramNode program, IReadOnlyList<string> parameters, string? name)
    {
        _instructions.Clear();
        _constants.Clear();
        _variables.Clear();
        _propertyNames.Clear();
        _propertyNameToIndex.Clear();
        _nestedFunctions.Clear();
        _parameterNames.Clear();
        _loopStack.Clear();
        _name = name;
        _nextRegister = 1;
        foreach (var p in parameters)
        {
            _parameterNames.Add(p);
            _ = GetOrCreateVariableSlot(p);
        }

        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
        }

        _instructions.Add(new Instruction(OpCode.Return, 0, 0, 0));

        return new BytecodeFunction
        {
            Name = _name,
            Instructions = _instructions.ToArray(),
            Constants = _constants.ToArray(),
            VariableSlots = new Dictionary<string, int>(_variables),
            PropertyNames = _propertyNames.ToArray(),
            ParameterNames = _parameterNames.ToArray(),
            NestedFunctions = _nestedFunctions.ToArray(),
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
            case ForStatementNode forStmt:
                CompileForStatement(forStmt);
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
                CompileFunctionDeclaration(functionDecl);
                break;
            default:
                // Minimal compiler slice currently targets literals/arithmetic/variables.
                break;
        }
    }

    private void CompileFunctionDeclaration(FunctionDeclarationNode functionDecl)
    {
        var nestedProgram = new ProgramNode(ProgramKind.Script, functionDecl.Body.Statements, functionDecl.Body.Span);
        var childCompiler = new BytecodeCompiler();
        var nestedFunction = childCompiler.CompileProgramCore(nestedProgram, functionDecl.Parameters, functionDecl.Name);
        var nestedIndex = _nestedFunctions.Count;
        _nestedFunctions.Add(nestedFunction);

        var dest = AllocateRegister();
        _instructions.Add(new Instruction(OpCode.CreateFunction, dest, nestedIndex, 0));
        var slot = GetOrCreateVariableSlot(functionDecl.Name);
        _instructions.Add(new Instruction(OpCode.StoreVar, dest, slot, 0));
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
            case AssignmentExpressionNode assign when assign.Left is IdentifierExpressionNode id:
            {
                var rightReg = CompileExpression(assign.Right);
                var slot = GetOrCreateVariableSlot(id.Name);
                _instructions.Add(new Instruction(OpCode.StoreVar, rightReg, slot, 0));
                return rightReg;
            }
            case AssignmentExpressionNode assign when assign.Left is MemberExpressionNode member:
            {
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
                    _instructions.Add(new Instruction(OpCode.SetPropByName, objectReg, nameIndex, valueReg));
                }

                return valueReg;
            }
            case MemberExpressionNode member:
            {
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
                    _instructions.Add(new Instruction(OpCode.GetPropByName, dest, objectReg, nameIndex));
                }

                return dest;
            }
            case CallExpressionNode call:
            {
                var calleeReg = -1;
                var thisReg = -1;
                var isMethodCall = false;
                if (call.Callee is MemberExpressionNode memberCallee)
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
                else
                {
                    calleeReg = CompileExpression(call.Callee);
                }

                var dest = AllocateRegister();
                switch (call.Arguments.Count)
                {
                    case 0:
                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethod0, dest, calleeReg, thisReg)
                            : new Instruction(OpCode.Call0, dest, calleeReg, 0));
                        return dest;
                    case 1:
                    {
                        var arg0 = CompileExpression(call.Arguments[0]);
                        _instructions.Add(isMethodCall
                            ? new Instruction(OpCode.CallMethod1, dest, calleeReg, thisReg, arg0)
                            : new Instruction(OpCode.Call1, dest, calleeReg, arg0));
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
                        return dest;
                    }
                }
            }
            case UnaryExpressionNode unary:
            {
                if (unary.Operator == "delete")
                {
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
                var nestedFunction = childCompiler.CompileProgramCore(nestedProgram, fnExpr.Parameters, fnExpr.Name);
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
