namespace FenBrowser.Js.Bytecode;

public enum OpCode : byte
{
    LoadConst,
    LoadVar,
    StoreVar,
    Move,
    Add,
    Sub,
    Mul,
    Div,
    Return
}
