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

        for (var ip = 0; ip < function.Instructions.Count; ip++)
        {
            var ins = function.Instructions[ip];
            ValidateOperands(function, ip, ins);

            if (ins.OpCode == OpCode.LoadConst && (ins.B < 0 || ins.B >= function.Constants.Count))
            {
                throw new InvalidOperationException($"Invalid constant index {ins.B} at ip {ip}.");
            }
        }

        if (function.Instructions[^1].OpCode != OpCode.Return)
        {
            throw new InvalidOperationException("Function must end with Return.");
        }
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
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.LoadVar:
            case OpCode.StoreVar:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateVariableSlot(function, ip, ins.B);
                break;
            case OpCode.Move:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                break;
            case OpCode.Add:
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Div:
            case OpCode.Eq:
            case OpCode.Neq:
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
                ValidateJumpTarget(function, ip, ins.A);
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
            case OpCode.SetElem:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
                break;
            case OpCode.GetElem:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
                ValidateRegister(ins.C, function.RegisterCount, ip, "C");
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
            case OpCode.Not:
            case OpCode.Neg:
                ValidateRegister(ins.A, function.RegisterCount, ip, "A");
                ValidateRegister(ins.B, function.RegisterCount, ip, "B");
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

    private static void ValidatePropertyName(BytecodeFunction function, int ip, int index)
    {
        if (index < 0 || index >= function.PropertyNames.Count)
        {
            throw new InvalidOperationException($"Invalid property name index {index} at ip {ip}.");
        }
    }
}
