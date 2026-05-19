namespace FenBrowser.Js.Bytecode;

public enum OpCode : byte
{
    LoadConst,
    LoadVar,
    StoreVar,
    Move,
    Jump,
    JumpIfFalse,
    PushHandler,
    PopHandler,
    Throw,
    NewObject,
    NewArray,
    SetPropByName,
    GetPropByName,
    SetElem,
    GetElem,
    Add,
    Sub,
    Mul,
    Div,
    Return
}
