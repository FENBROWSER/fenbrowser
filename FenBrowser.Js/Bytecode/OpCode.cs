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
    Add,
    Sub,
    Mul,
    Div,
    Return
}
