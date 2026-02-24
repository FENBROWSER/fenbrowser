using System;

namespace FenBrowser.FenEngine.Core.Bytecode
{
    public enum OpCode : byte
    {
        // 0x00 - 0x0F: Constants
        LoadConst = 0x01,
        LoadNull = 0x02,
        LoadUndefined = 0x03,
        LoadTrue = 0x04,
        LoadFalse = 0x05,

        // 0x10 - 0x1F: Variables & Scopes
        LoadVar = 0x10,
        StoreVar = 0x11,

        // 0x20 - 0x2F: Math
        Add = 0x20,
        Subtract = 0x21,
        Multiply = 0x22,
        Divide = 0x23,
        Modulo = 0x24,
        Exponent = 0x25,

        // 0x30 - 0x3F: Logic
        Equal = 0x30,
        StrictEqual = 0x31,
        NotEqual = 0x32,
        StrictNotEqual = 0x33,
        LessThan = 0x34,
        GreaterThan = 0x35,
        LessThanOrEqual = 0x36,
        GreaterThanOrEqual = 0x37,
        LogicalNot = 0x38,

        // 0x40 - 0x4F: Flow Control
        Jump = 0x40,
        JumpIfFalse = 0x41,
        JumpIfTrue = 0x42,

        // 0x50 - 0x5F: Functions
        Call = 0x50,
        Return = 0x51,

        // 0xFF: End of program
        Halt = 0xFF
    }
}
