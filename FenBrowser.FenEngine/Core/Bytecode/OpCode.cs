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
        StoreVar = 0x11,   // Declare: always sets in current scope (for let/var/const declarations, function declarations)
        Dup = 0x12,
        Pop = 0x13,
        PopAccumulator = 0x14,
        LoadLocal = 0x15,
        StoreLocal = 0x16,
        UpdateVar = 0x17,  // Assign: walks scope chain to update existing binding (for x = value assignments)

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
        InOperator = 0x39,
        InstanceOf = 0x3A,

        // 0x40 - 0x4F: Flow Control
        Jump = 0x40,
        JumpIfFalse = 0x41,
        JumpIfTrue = 0x42,

        // 0x50 - 0x5F: Functions & Constructors
        Call = 0x50,
        Return = 0x51,
        MakeClosure = 0x52,
        Construct = 0x53,
        CallFromArray = 0x54,
        ConstructFromArray = 0x55,
        CallMethod = 0x56,           // [receiver, callee, arg0..argN] -> result (passes receiver as 'this')
        CallMethodFromArray = 0x57,  // [receiver, callee, argsArray] -> result (passes receiver as 'this')

        // 0x60 - 0x6F: Objects & Arrays
        MakeArray = 0x60,
        MakeObject = 0x61,
        LoadProp = 0x62,
        StoreProp = 0x63,
        DeleteProp = 0x64,
        ArrayAppend = 0x65,
        ArrayAppendSpread = 0x66,
        ObjectSpread = 0x67,

        // 0x6A - 0x6F: Iteration
        MakeKeysIterator = 0x6A,
        IteratorMoveNext = 0x6B,
        IteratorCurrent = 0x6C,
        MakeValuesIterator = 0x6D,

        // 0x80 - 0x8F: Exceptions & Scoping
        PushExceptionHandler = 0x80,
        PopExceptionHandler = 0x81,
        Throw = 0x82,
        PushScope = 0x83,
        PopScope = 0x84,
        EnterFinally = 0x85,
        ExitFinally = 0x86,

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
        ToNumber = 0x79,
        LoadNewTarget = 0x7A,
        Await = 0x7B,
        EnterWith = 0x7C,
        ExitWith = 0x7D,

        // 0xFF: End of program
        Halt = 0xFF
    }
}
