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
        Dup = 0x12,
        Pop = 0x13,
        PopAccumulator = 0x14,

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
        MakeClosure = 0x52,

        // 0x60 - 0x6F: Objects & Arrays
        MakeArray = 0x60,
        MakeObject = 0x61,
        LoadProp = 0x62,
        StoreProp = 0x63,

        // 0x6A - 0x6F: Iteration
        MakeKeysIterator = 0x6A,
        IteratorMoveNext = 0x6B,
        IteratorCurrent = 0x6C,
        MakeValuesIterator = 0x6D,

        // 0x80 - 0x8F: Exceptions
        PushExceptionHandler = 0x80,
        PopExceptionHandler = 0x81,
        Throw = 0x82,

        // 0x70 - 0x7F: Unary & Bitwise
        BitwiseAnd = 0x70,
        BitwiseOr = 0x71,
        BitwiseXor = 0x72,
        LeftShift = 0x73,
        RightShift = 0x74,
        UnsignedRightShift = 0x75,
        BitwiseNot = 0x76,
        Negate = 0x77,
        Typeof = 0x78,

        // 0xFF: End of program
        Halt = 0xFF
    }
}
