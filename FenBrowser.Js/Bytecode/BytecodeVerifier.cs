namespace FenBrowser.Js.Bytecode;

public sealed class BytecodeVerifier
{
    public void Verify(BytecodeFunction function)
    {
        if (function.RegisterCount < 1)
        {
            throw new InvalidOperationException("Register count must be >= 1.");
        }

        if (function.Instructions.Count == 0)
        {
            throw new InvalidOperationException("Function must contain instructions.");
        }

        var pushHandlerCount = 0;
        var popHandlerCount = 0;

        for (var ip = 0; ip < function.Instructions.Count; ip++)
        {
            var ins = function.Instructions[ip];
            ValidateOperands(function, ip, ins);

            if (ins.OpCode == OpCode.LoadConst && (ins.B < 0 || ins.B >= function.Constants.Count))
            {
                throw new InvalidOperationException($"Invalid constant index {ins.B} at ip {ip}.");
            }

            // Tier 5 #26: handler balance. Across the linear instruction
            // stream, PushHandler and PopHandler must occur in matched
            // counts. Per-path balance would require full CFG analysis;
            // counting catches the common compiler bugs (forgotten Pop,
            // duplicated Push) without false positives.
            if (ins.OpCode == OpCode.PushHandler) pushHandlerCount++;
            if (ins.OpCode == OpCode.PopHandler) popHandlerCount++;
        }

        if (pushHandlerCount != popHandlerCount)
        {
            throw new InvalidOperationException(
                $"Handler stack unbalanced: {pushHandlerCount} PushHandler, {popHandlerCount} PopHandler.");
        }

        if (function.Instructions[^1].OpCode != OpCode.Return)
        {
            throw new InvalidOperationException("Function must end with Return.");
        }
    }

    // Tier 5 #26: reachability analysis. Returns the count of unreachable
    // instructions from the function entry, following all jump and exception
    // handler targets. Provided for diagnostics and fuzz harnesses; not
    // currently invoked by Verify() because legitimate compiler output may
    // emit dead instructions after a Return as a structural anchor for the
    // exception handler tables.
    public int CountUnreachableInstructions(BytecodeFunction function)
    {
        if (function.Instructions.Count == 0) return 0;

        var reachable = new bool[function.Instructions.Count];
        var work = new Stack<int>();
        work.Push(0);

        while (work.Count > 0)
        {
            var ip = work.Pop();
            if (ip < 0 || ip >= reachable.Length || reachable[ip]) continue;
            reachable[ip] = true;
            var ins = function.Instructions[ip];

            switch (ins.OpCode)
            {
                case OpCode.Return:
                case OpCode.Throw:
                    break;
                case OpCode.Jump:
                    work.Push(ins.A);
                    break;
                case OpCode.JumpIfFalse:
                    work.Push(ins.B);
                    work.Push(ip + 1);
                    break;
                case OpCode.PushHandler:
                    if (ins.A >= 0) work.Push(ins.A);
                    if (ins.D >= 0) work.Push(ins.D);
                    work.Push(ip + 1);
                    break;
                default:
                    work.Push(ip + 1);
                    break;
            }
        }

        var count = 0;
        for (var i = 0; i < reachable.Length; i++)
        {
            if (!reachable[i]) count++;
        }
        return count;
    }

    private static void ValidateRegister(int reg, int regCount, int ip, string field)
    {
        if (reg < 0 || reg >= regCount)
        {
            throw new InvalidOperationException($"Invalid register {field}={reg} at ip {ip}.");
        }
    }

    private static void ValidateOperands(BytecodeFunction function, int ip, Instruction ins)
    {
        switch (ins.OpCode)
        {
            case OpCode.LoadConst:
                // LoadConst.A is the destination register; LoadConst.B is an index
                // into the constant pool (the VM reads function.Constants[ins.B]),
                // not a register, so it must be range-checked against the pool.
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateConstantIndex(function, ip, ins.B);
                break;
            case OpCode.LoadThis:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.LoadVar:
            case OpCode.StoreVar:
            case OpCode.InitVar:
            case OpCode.StoreVarTop:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateVariableSlot(function, ip, ins.B);
                break;
            case OpCode.Move:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.SetFunctionName:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.SetElemDefine:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.Add:
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Div:
            case OpCode.Exp:
            case OpCode.BitAnd:
            case OpCode.BitOr:
            case OpCode.BitXor:
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.UnsignedShiftRight:
            case OpCode.Eq:
            case OpCode.Neq:
            case OpCode.StrictEq:
            case OpCode.StrictNeq:
            case OpCode.In:
            case OpCode.InstanceOf:
            case OpCode.Lt:
            case OpCode.Gt:
            case OpCode.Le:
            case OpCode.Ge:
            case OpCode.And:
            case OpCode.Or:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.Jump:
                ValidateJumpTarget(function, ip, ins.A);
                break;
            case OpCode.JumpIfFalse:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateJumpTarget(function, ip, ins.B);
                break;
            case OpCode.PushHandler:
                if (ins.A >= 0)
                    ValidateJumpTarget(function, ip, ins.A);
                if (ins.D >= 0)
                    ValidateJumpTarget(function, ip, ins.D);
                break;
            case OpCode.PopHandler:
                break;
            case OpCode.Throw:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.NewObject:
            case OpCode.NewArray:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.NewRegExp:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                if (ins.B < 0 || ins.B >= function.Constants.Count)
                    throw new InvalidOperationException($"Invalid constant index {ins.B} at ip {ip}.");
                break;
            case OpCode.SetPrototype:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.DefineGetter:
            case OpCode.DefineSetter:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidatePropertyName(function, ip, ins.B);
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.DefineGetterByReg:
            case OpCode.DefineSetterByReg:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.DefineMethod:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidatePropertyName(function, ip, ins.B);
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.PrologueEnd:
                // No operands.
                break;
            case OpCode.DefineMethodByReg:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.CallSpread:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                if (ins.D != 0)
                    ValidateRegister(ins.D, function.RegisterCount, ip, "D (thisReg)");
                break;
            case OpCode.EnterScope:
                break;
            case OpCode.PushWithEnvironment:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.LeaveScope:
            case OpCode.EndFinally:
                break;
            case OpCode.SetHomeObject:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.LoadSuperProperty:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidatePropertyName(function, ip, ins.B);
                break;
            case OpCode.LoadSuperElement:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.DynamicImport:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.ImportMeta:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.ImportSource:
            case OpCode.ImportDefer:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.LoadSuperConstructor:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.LoadNewTarget:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
            case OpCode.InitThisBinding:
                break;
            case OpCode.Yield:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.YieldStar:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.DefinePrivateField:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidatePropertyName(function, ip, ins.B);
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.GetPrivateField:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidatePropertyName(function, ip, ins.C);
                break;
            case OpCode.SetPrivateField:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidatePropertyName(function, ip, ins.B);
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.SetPropByName:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidatePropertyName(function, ip, ins.B);
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.GetPropByName:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidatePropertyName(function, ip, ins.C);
                break;
            case OpCode.DeletePropByName:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidatePropertyName(function, ip, ins.C);
                break;
            case OpCode.SetElem:
            case OpCode.SpreadAppend:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.CopyDataProperties:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.GetElem:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.DeleteElem:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.EnumerateKeys:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.ForInNext:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateJumpTarget(function, ip, ins.C);
                break;
            case OpCode.EnumerateValues:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.ForOfNext:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateJumpTarget(function, ip, ins.C);
                break;
            case OpCode.IteratorClose:
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.CreateFunction:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                if (ins.B < 0 || ins.B >= function.NestedFunctions.Count)
                {
                    throw new InvalidOperationException($"Invalid nested function index {ins.B} at ip {ip}.");
                }

                break;
            case OpCode.Call0:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.Call1:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.CallN:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                if (ins.D < 0)
                {
                    throw new InvalidOperationException($"Invalid CallN arg count {ins.D} at ip {ip}.");
                }

                if (ins.C < 0 || ins.C + Math.Max(0, ins.D - 1) >= function.RegisterCount)
                {
                    throw new InvalidOperationException($"Invalid CallN arg register window start={ins.C} count={ins.D} at ip {ip}.");
                }

                break;
            case OpCode.CallMethod0:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.CallMethod1:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                ValidateRegister(ins.D, function.RegisterCount, ip, "D");
                break;
            case OpCode.CallMethodN:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                if (ins.E < 0)
                {
                    throw new InvalidOperationException($"Invalid CallMethodN arg count {ins.E} at ip {ip}.");
                }

                if (ins.D < 0 || ins.D + Math.Max(0, ins.E - 1) >= function.RegisterCount)
                {
                    throw new InvalidOperationException($"Invalid CallMethodN arg register window start={ins.D} count={ins.E} at ip {ip}.");
                }

                break;
            case OpCode.Construct0:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.Construct1:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.ConstructN:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                if (ins.D < 0)
                {
                    throw new InvalidOperationException($"Invalid ConstructN arg count {ins.D} at ip {ip}.");
                }

                if (ins.C < 0 || ins.C + Math.Max(0, ins.D - 1) >= function.RegisterCount)
                {
                    throw new InvalidOperationException($"Invalid ConstructN arg register window start={ins.C} count={ins.D} at ip {ip}.");
                }

                break;
            case OpCode.Not:
            case OpCode.Pos:
            case OpCode.Neg:
            case OpCode.BitNot:
            case OpCode.Void:
            case OpCode.TypeOf:
            case OpCode.Await:
            case OpCode.ToNumeric:
            case OpCode.Increment:
            case OpCode.Decrement:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.TypeOfName:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateVariableSlot(function, ip, ins.B);
                break;
            case OpCode.Delete:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateVariableSlot(function, ip, ins.B);
                break;
            case OpCode.Return:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                break;
        }
    }

    private static void ValidateJumpTarget(BytecodeFunction function, int ip, int target)
    {
        if (target < 0 || target >= function.Instructions.Count)
        {
            throw new InvalidOperationException($"Invalid jump target {target} at ip {ip}.");
        }
    }

    private static void ValidateVariableSlot(BytecodeFunction function, int ip, int slot)
    {
        if (slot < 0 || slot >= Math.Max(1, function.VariableSlots.Count))
        {
            throw new InvalidOperationException($"Invalid variable slot {slot} at ip {ip}.");
        }
    }

    private static void ValidateConstantIndex(BytecodeFunction function, int ip, int index)
    {
        if (index < 0 || index >= function.Constants.Count)
        {
            throw new InvalidOperationException($"Invalid constant index {index} at ip {ip}.");
        }
    }

    private static void ValidatePropertyName(BytecodeFunction function, int ip, int index)
    {
        if (index < 0 || index >= function.PropertyNames.Count)
        {
            throw new InvalidOperationException($"Invalid property name index {index} at ip {ip}.");
        }
    }
}
