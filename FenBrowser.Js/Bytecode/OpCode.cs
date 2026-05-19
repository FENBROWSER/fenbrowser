namespace FenBrowser.Js.Bytecode;

public enum OpCode : byte
{
    LoadConst,
    LoadVar,
    StoreVar,
    Move,
    Jump,
    JumpIfFalse,
    Add,
    Sub,
    Mul,
    Div,
    Return
}
