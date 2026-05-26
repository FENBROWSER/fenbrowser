namespace FenBrowser.Js.Objects;

// Plan Section 18: formal function kind classification.
// Distinguishes how a function was created so the runtime can enforce
// [[Construct]] rules, super() access, new.target, and this-binding.
public enum FunctionKind
{
    // function foo() {}, (function() {}), function* foo() {}
    Ordinary,

    // () => {}, async () => {}
    Arrow,

    // class { method() {} }, { method() {} }
    Method,

    // class Foo { constructor() {} } - only constructors have [[Construct]]
    Constructor,

    // Function.prototype.bind() result
    Bound,

    // Builtins implemented as NativeFunctionObject (parseInt, Math.abs, etc.)
    Native,

    // async function foo() {}
    Async,

    // function* foo() {}
    Generator,

    // async function* foo() {}
    AsyncGenerator
}
