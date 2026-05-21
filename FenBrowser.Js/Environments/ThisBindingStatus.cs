namespace FenBrowser.Js.Environments;

// ECMA-262 9.1.1.3 - the [[ThisBindingStatus]] slot of a function Environment Record.
//
// "lexical" : The function is an arrow function and does not have a local `this`
//             value. References to `this` resolve through the outer scope chain.
// "initialized" : The function has been called and `this` has been bound to the
//                 supplied value.
// "uninitialized" : The function has not yet bound `this`. Reading it throws a
//                   ReferenceError. Binding it a second time also throws.
public enum ThisBindingStatus : byte
{
    Lexical,
    Initialized,
    Uninitialized,
}
