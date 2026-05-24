using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Plan H.5: brand validation for private fields.
// A foreign object reading a mangled private key must throw TypeError.
public class PrivateFieldBrandTests
{
    private JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void PrivateFieldReadFromOwnInstance()
    {
        Assert.Equal(42d, Run("class C { #x = 42; getX() { return this.#x; } } new C().getX();").AsNumber());
    }

    [Fact]
    public void PrivateFieldWriteFromOwnInstance()
    {
        Assert.Equal(99d, Run("class C { #x = 0; setX(v) { this.#x = v; } getX() { return this.#x; } } var c = new C(); c.setX(99); c.getX();").AsNumber());
    }

    [Fact]
    public void PrivateMethodCalledFromOwnInstance()
    {
        Assert.Equal(50d, Run("class C { #double(v) { return v * 2; } call(v) { return this.#double(v); } } new C().call(25);").AsNumber());
    }

    [Fact]
    public void PrivateFieldAccessOnForeignObjectThrowsTypeError()
    {
        // A regular object with a manually-set __priv-like property should NOT
        // allow private field access (the brand is missing).
        Assert.Throws<JsThrownException>(() => Run(@"
            class C { #x = 1; getX() { return this.#x; } }
            var c = new C();
            var fake = {__priv0__x: 99};
            C.prototype.getX.call(fake);
        "));
    }

    [Fact]
    public void PrivateFieldAccessOnPrototypeThrowsTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run(@"
            class C { #x = 1; getX() { return this.#x; } }
            C.prototype.getX.call(C.prototype);
        "));
    }

    [Fact]
    public void PrivateFieldReadBeforeInitReturnsUndefined()
    {
        // Field initializers run before the constructor body (ECMA-262 15.7.10).
        // So this.#x is already defined (as undefined) when constructor code runs.
        Assert.True(Run(@"
            class C {
                constructor() { var before = this.#x; this.#x = 1; var after = this.#x; }
                #x;
            }
            new C();
            true;
        ").AsBoolean());
    }

    [Fact]
    public void PrivateFieldAcrossSiblingClassesAreIsolated()
    {
        // Different classes with same private name should have different brands.
        Assert.True(Run(@"
            class A { #v = 1; getV() { return this.#v; } }
            class B { #v = 2; getV() { return this.#v; } }
            new A().getV() === 1 && new B().getV() === 2;
        ").AsBoolean());
    }
}
