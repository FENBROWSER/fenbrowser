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

    // H.4 - DefineGetter/DefineSetter(A=target reg, B=name idx, C=function reg).
    // Installs an accessor descriptor on the target object under the given
    // property name. If the target already has an accessor descriptor for that
    // key, the matching half is updated and the other half preserved; otherwise
    // a fresh accessor descriptor is created with the missing half left as
    // undefined. Used by class get/set member compilation; could also serve
    // object-literal accessors in future.
    DefineGetter,
    DefineSetter,

    // H.3 - SetHomeObject(A=function reg, B=home object reg). Records the
    // home object on a JsFunctionObject so `super.x` lookups inside that
    // method body can walk the prototype chain. No-op when A isn't a function.
    SetHomeObject,

    // H.3 - LoadSuperProperty(A=dest reg, B=name idx). Reads the property
    // named B from the prototype of the current frame function's HomeObject.
    // ECMA-262 13.3.7.3 MakeSuperPropertyReference + 9.1.2 GetSuperBase.
    // Throws ReferenceError when called from a function with no HomeObject.
    LoadSuperProperty,
}
