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
            ValidateRegister(ins.A, function.RegisterCount, ip, "A");
            ValidateRegister(ins.B, function.RegisterCount, ip, "B");
            ValidateRegister(ins.C, function.RegisterCount, ip, "C");

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
}
