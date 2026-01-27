using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Jit
{
    public enum OpCode
    {
        // Stack manipulation
        PushConst,      // [Value]
        Pop,            // []
        Dup,            // [Val] -> [Val, Val]
        Swap,           // [V1, V2] -> [V2, V1]

        // Variable access
        LoadVar,        // [Name] -> [Value]
        StoreVar,       // [Name, Value] -> []
        LoadLocal,      // [Index] -> [Value] (NEW: Indexed fast access)
        StoreLocal,     // [Index, Value] -> [] (NEW: Indexed fast access)
        LoadGlobal,     // [Name] -> [Value]
        
        // Property access
        GetProp,        // [Object, PropName] -> [Value]
        SetProp,        // [Object, PropName, Value] -> []
        GetIndex,       // [Object, Index] -> [Value]
        SetIndex,       // [Object, Index, Value] -> []

        // Arithmetic
        Add, Sub, Mul, Div, Mod, Exp,
        
        // Bitwise
        BitAnd, BitOr, BitXor, BitNot, Shl, Shr, Ushr,

        // Logic & Comparison
        Eq, NotEq, StrictEq, StrictNotEq,
        Lt, Gt, LtEq, GtEq,
        LAnd, LOr, Not,

        // Control flow
        Jump,           // [Address]
        JumpIfFalse,    // [Condition, Address]
        JumpIfTrue,     // [Condition, Address]
        Return,         // [Value]
        
        // Function/Object
        Call,           // [Func, This, Arg1, Arg2, ...]
        New,            // [Ctor, Arg1, Arg2, ...]
        CreateArray,    // [Size, El1, El2, ...]
        CreateObject,   // [Size, Key1, Val1, ...]

        // Misc
        Typeof,
        Instanceof,
        In,
        Throw,
        EnterTry,
        ExitTry,
        EnterCatch,
        ExitCatch
    }

    public struct Instruction
    {
        public OpCode OpCode;
        public object Operand;
        public int Line;

        public Instruction(OpCode op, object operand = null, int line = 0)
        {
            OpCode = op;
            Operand = operand;
            Line = line;
        }

        public override string ToString() => Operand != null ? $"{OpCode} {Operand}" : OpCode.ToString();
    }

    public class BytecodeUnit
    {
        public string Name { get; set; }
        public List<Instruction> Instructions { get; set; } = new List<Instruction>();
        public List<string> Parameters { get; set; } = new List<string>();
        public List<string> Locals { get; set; } = new List<string>();
        public Dictionary<string, int> LocalMap { get; set; } = new Dictionary<string, int>();
        public bool IsAsync { get; set; }
        public bool IsGenerator { get; set; }
    }
}
