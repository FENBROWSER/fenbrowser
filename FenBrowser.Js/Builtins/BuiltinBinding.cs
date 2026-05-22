using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Builtins;

// One name/value pair a builtin module asks to be installed on the global object.
// Mirrors the shape of a JavaScript property descriptor without depending on the
// JsObject internal representation: the registry is responsible for converting these
// into JsPropertyDescriptor instances at install time, so the same module definition
// can be reused by realms that disagree on object representation (e.g. the standalone
// JsObject vs. a future host-provided global).
//
// Attributes default to writable+enumerable+configurable, matching the spec defaults
// for builtin properties on the global object (e.g. Math is a writable, non-enumerable,
// configurable own property of the global; modules carrying NaN / Infinity must
// override Enumerable=false and Writable=false explicitly).
public readonly record struct BuiltinBinding(
    string Name,
    JsValue Value,
    bool Writable = true,
    bool Enumerable = true,
    bool Configurable = true)
{
    // Convenience factory for the standard "non-enumerable own data property" shape
    // that virtually every builtin uses (Math, JSON, Date, RegExp, the error
    // constructors). Matches ECMA-262 19.x where builtin globals are
    // {[[Writable]]: true, [[Enumerable]]: false, [[Configurable]]: true}.
    public static BuiltinBinding NonEnumerable(string name, JsValue value)
        => new(name, value, Writable: true, Enumerable: false, Configurable: true);

    // Convenience factory for the immutable global constants
    // {[[Writable]]: false, [[Enumerable]]: false, [[Configurable]]: false} that
    // ECMA-262 19.1.1 nails down: NaN, Infinity, undefined.
    public static BuiltinBinding Frozen(string name, JsValue value)
        => new(name, value, Writable: false, Enumerable: false, Configurable: false);
}
