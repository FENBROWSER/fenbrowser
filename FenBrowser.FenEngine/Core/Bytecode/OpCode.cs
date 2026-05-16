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
        LoadLocal = 0x15,
        StoreLocal = 0x16,
        UpdateVar = 0x17,
        LoadVarSafe = 0x18,
        StoreVarDeclaration = 0x19,
        DeclareTdz = 0x1A,
        StoreLocalDeclaration = 0x1B,
        DeclareVar = 0x1C,
        LoadCaptured = 0x1D,

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
        CallMethod = 0x56,
        CallMethodFromArray = 0x57,

        // 0x60 - 0x6F: Objects & Arrays
        MakeArray = 0x60,
        MakeObject = 0x61,
        LoadProp = 0x62,
        StoreProp = 0x63,
        DeleteProp = 0x64,
        ArrayAppend = 0x65,
        ArrayAppendSpread = 0x66,
        ObjectSpread = 0x67,

        // 0x68 - 0x69: Async Iteration (ECMA-262 §14.7.5.10 for await..of)
        // MakeAsyncValuesIterator: pops the iterable, calls [Symbol.asyncIterator]() if present,
        //   otherwise falls back to [Symbol.iterator](). Pushes an iterator-wrapper FenObject.
        MakeAsyncValuesIterator = 0x68,
        // IteratorAwaitMoveNext: calls iterator.next(), awaits the resulting promise,
        //   pushes bool done onto stack (true = exhausted). The value is stored in the iterator object
        //   for retrieval by IteratorCurrent.
        IteratorAwaitMoveNext = 0x69,

        // 0x6A - 0x6F: Iteration
        MakeKeysIterator = 0x6A,
        IteratorMoveNext = 0x6B,
        IteratorCurrent = 0x6C,
        MakeValuesIterator = 0x6D,
        Yield = 0x6E,
        IteratorClose = 0x6F,

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
        DirectEval = 0x7E,
        SetFunctionHomeObject = 0x7F,

        // 0x90 - 0x9F: Superinstructions (fused common patterns)
        // Operands: localSlot (int32), constIndex (int32). Effect: local[slot] = local[slot] + constants[constIndex]
        // (number fast-path; falls back to generic Add when types disagree). Saves 3 dispatches per use.
        IncrementLocalByConst = 0x90,
        // Operands: localSlot (int32), constIndex (int32). Pushes local[slot] < constants[constIndex] as boolean.
        LocalLessThanConst = 0x91,
        // Operands: localSlot (int32), constIndex (int32). Pushes local[slot] - constants[constIndex] as number.
        // Hot path for fib's n-1, n-2 and similar. Saves 2 dispatches per use.
        LocalSubtractByConst = 0x92,
        // Operands: localSlot (int32). Pushes local[slot] onto stack. Same effect as LoadLocal, but
        // emitted directly when AST shows a bare-local in a context that doesn't need slot remapping.
        // (Reserved — not yet used by the compiler; placeholder for future fusion patterns.)
        LoadLocalReturn = 0x93,
        // Operands: localSlot (int32). Expects [object, propertyKey] on stack, loads local[slot] as
        // value and performs the same write semantics as StoreProp. Saves one LoadLocal dispatch.
        StorePropLocal = 0x94,
        // Operands: localSlot (int32), constIndex (int32 as string property key constant). Pushes
        // local[slot][constKey] with identical semantics to LoadProp by reusing the LoadProp path.
        LoadPropLocalConst = 0x95,
        // Operands: nameConstIndex (int32), constIndex (int32). Effect: var[name] = var[name] + const.
        // Global/script-scope counterpart to IncrementLocalByConst.
        IncrementVarByConst = 0x96,
        // Operands: nameConstIndex (int32), constIndex (int32). Pushes var[name] < const as boolean.
        // Global/script-scope counterpart to LocalLessThanConst.
        VarLessThanConst = 0x97,

        // 0xFF: End of program
        Halt = 0xFF
    }
}

