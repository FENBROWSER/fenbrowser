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
    // ECMA-262 12.2.8 regex literal -> RegExpObject.
    // A = dest register, B = constant pool index (raw text "/pattern/flags").
    NewRegExp,
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

    // H.3.2 - LoadSuperConstructor(A=dest reg). Reads the parent class from
    // the executing constructor's HomeObject prototype slot. ECMA-262
    // 13.3.7.4 GetSuperConstructor. Used by `super(...)` to obtain the
    // base class as a callable/constructible value. Throws ReferenceError
    // when called outside a class with extends.
    LoadSuperConstructor,

    // H.5 - DefineGetterByReg(A=target reg, B=key reg, C=function reg).
    // Like DefineGetter but the property key is in register B (a JsValue)
    // instead of an index into the constant pool. Used for computed property
    // names on class getters.
    DefineGetterByReg,

    // H.5 - DefineSetterByReg(A=target reg, B=key reg, C=function reg).
    // Like DefineSetter but the property key is in register B (a JsValue)
    // instead of an index into the constant pool. Used for computed property
    // names on class setters.
    DefineSetterByReg,

    // H.5 - DefinePrivateField(A=target reg, B=field name string index, C=value reg).
    // Defines a private field (identified by the string at constant pool index B)
    // on the target object. The field is stored in the instance's private field
    // storage. ECMA-262 10.2.1.
    DefinePrivateField,

    // H.5 - GetPrivateField(A=dest reg, B=object reg, C=field name string index).
    // Reads a private field value from the target object. Throws a TypeError
    // if the accessor is not in the class that defined the field.
    GetPrivateField,

    // H.5 - SetPrivateField(A=object reg, B=field name string index, C=value reg).
    // Writes a private field value on the target object. Throws a TypeError
    // if the accessor is not in the class that defined the field.
    SetPrivateField,

    // H.5 - LoadNewTarget(A=dest reg). Reads the current frame's NewTarget.
    LoadNewTarget,

    // H.5 - InitThisBinding. Transitions the frame's FunctionEnvironmentRecord
    // ThisBindingStatus from Uninitialized to Initialized.
    InitThisBinding,

    // ECMA-262 15.5 — Yield(A=dest, B=value). Suspends generator execution and
    // returns {value, done:false} to the caller. The generator frame is saved
    // for later resumption via Generator.prototype.next().
    Yield,

    // ECMA-262 15.5 — YieldStar. Delegates to another iterator, yielding each
    // of its values in sequence before resuming with the delegated return value.
    YieldStar,

    // ECMA-262 15.8 — Await(value). Inside async functions this suspends until
    // the awaited value settles, then resumes with either the fulfillment value
    // or a throw completion for rejection.
    Await,

    // ECMA-262 13.3.7.1 — call a function with a spread argument (...args).
    // A=dest, B=callee, C=spreadArrayReg, D=thisReg (0 means no this).
    // Unpacks the array from register C into individual arguments and calls
    // the callee with the given this value.
    CallSpread,

    // ECMA-262 9.1.1.1 — push a new DeclarativeEnvironmentRecord onto the
    // frame's lexical environment chain. Used by catch blocks and block-scoped
    // declarations (let/const in blocks).
    EnterScope,

    // ECMA-262 9.1.1.1 — pop the current lexical environment, restoring
    // frame.Environment to its outer (parent) record.
    LeaveScope,

	// ECMA-262 14.4.13 — EndFinally.
	EndFinally,

	// Bitwise binary operators (&, |, ^)
	BitAnd,
	BitOr,
	BitXor,
	// Bitwise NOT (~)
	BitNot,
	// Shift operators (<<, >>, >>>)
	ShiftLeft,
	ShiftRight,
	UnsignedShiftRight,
}
