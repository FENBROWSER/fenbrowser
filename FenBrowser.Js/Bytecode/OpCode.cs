namespace FenBrowser.Js.Bytecode;

public enum OpCode : byte
{
    LoadConst,
    LoadVar,
    LoadThis,
    StoreVar,
    InitVar,
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
    DeletePropByName,
    SetElem,
    GetElem,
    DeleteElem,
    EnumerateKeys,
    ForInNext,
    // ECMA-262 13.7.5 ForIn/OfHeadEvaluation + 7.4 Iterator Records.
    // EnumerateValues materialises an iteration state for the for-of head; ForOfNext
    // advances it, writing the next value into a register or jumping to the loop end.
    EnumerateValues,
    ForOfNext,
    CreateFunction,
    Call0,
    Call1,
    CallN,
    CallMethod0,
    CallMethod1,
    CallMethodN,
    Construct0,
    Construct1,
    ConstructN,
    Not,
    Pos,
    Neg,
    Void,
    Delete,
    TypeOf,
    Eq,
    Neq,
    StrictEq,
    StrictNeq,
    In,
    InstanceOf,
    Lt,
    Gt,
    Le,
    Ge,
    And,
    Or,
    Add,
    Sub,
    Mul,
    Mod,
    Div,
    Return,

    // H.2 - SetPrototype(A, B): set the [[Prototype]] of the object in register A
    // to either the object in register B (when B holds a JsValueTag.Object) or
    // to null (when B holds JsValueTag.Null). Any other type is a TypeError -
    // ECMA-262 7.3.5 OrdinarySetPrototypeOf. Used by class extends to wire the
    // base.prototype as the child prototype's parent, and base itself as the
    // child constructor's parent (so static methods inherit).
    SetPrototype,
}
