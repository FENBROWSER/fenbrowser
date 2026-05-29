using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.1.2 OrdinarySetPrototypeOf invariants: cycle rejection, non-extensible
// rejection, immutable-prototype (%Object.prototype%), shared by Object.setPrototypeOf,
// Reflect.setPrototypeOf, and Object.prototype.__proto__.
public sealed class SetPrototypeOfTests
{
    // Returns the constructor name of any thrown error, else "<no throw>".
    private static string CtorName(string call)
    {
        var src = "var n = '<no throw>'; try { " + call + " } catch (e) { n = e.constructor.name; } n;";
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static string Eval(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ProtoSetterRejectsCycle()
        => Assert.Equal("TypeError",
            CtorName("var r={},i=Object.create(r),l=Object.create(i); r.__proto__ = l;"));

    [Fact]
    public void ProtoSetterRejectsNonExtensible()
        => Assert.Equal("TypeError",
            CtorName("var p={},s=Object.create(p); Object.preventExtensions(s); s.__proto__ = {};"));

    [Fact]
    public void ProtoSetterAllowsSameValueOnNonExtensible()
        // Setting the SAME prototype is a no-op success even when non-extensible.
        => Assert.Equal("<no throw>",
            CtorName("var p={},s=Object.create(p); Object.preventExtensions(s); s.__proto__ = p;"));

    [Fact]
    public void ObjectPrototypeIsImmutable()
        => Assert.Equal("TypeError", CtorName("Object.prototype.__proto__ = {};"));

    [Fact]
    public void SetPrototypeOfRejectsCycle()
        => Assert.Equal("TypeError",
            CtorName("var a={},b=Object.create(a); Object.setPrototypeOf(a, b);"));

    [Fact]
    public void NormalProtoChangeSucceeds()
        => Assert.Equal("42", Eval("var p={x:42}, o={}; o.__proto__ = p; String(o.x);"));

    [Fact]
    public void ReflectSetPrototypeOfReturnsFalseOnCycle()
        => Assert.Equal("false",
            Eval("var a={},b=Object.create(a); String(Reflect.setPrototypeOf(a, b));"));

    [Fact]
    public void ReflectSetPrototypeOfThrowsOnNonObjectProto()
        => Assert.Equal("TypeError", CtorName("Reflect.setPrototypeOf({}, 5);"));
}
