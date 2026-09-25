using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 7.3.34 DefineField: an instance field is defined on the new object
// with CreateDataPropertyOrThrow - it is not an assignment, so an inherited
// setter never runs and a proxy sees its defineProperty trap.
public sealed class ClassFieldDefineTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AnInheritedSetterDoesNotRunForAField()
    {
        Assert.Equal("|1,true", Run(@"
            var hit = '';
            class A { set x(v) { hit = 'setter'; } }
            class B extends A { x = 1; }
            var b = new B();
            hit + '|' + b.x + ',' + b.hasOwnProperty('x');"));
    }

    [Fact]
    public void AProxyReturnedByTheBaseSeesDefineProperty()
    {
        Assert.Equal("f,3,true,g,Test262,true|Test262Error", Run(@"
            var arr = [];
            function ProxyBase() {
                return new Proxy(this, { defineProperty: function (t, k, d) { arr.push(k, d.value, d.writable); return Reflect.defineProperty(t, k, d); } });
            }
            class T extends ProxyBase { f = 3; g = 'Test262'; }
            new T();
            function Throwing() { return new Proxy(this, { defineProperty: function () { throw new Test262Error(); } }); }
            function Test262Error() {}
            class U extends Throwing { f = 1; }
            var name;
            try { new U(); } catch (e) { name = e instanceof Test262Error ? 'Test262Error' : String(e); }
            arr.join() + '|' + name;"));
    }

    [Fact]
    public void AnonymousFunctionsInFieldsAreNamedAfterTheField()
    {
        Assert.Equal("x,computed,#p", Run(@"
            var key = 'computed';
            class C { x = function () {}; [key] = () => {}; #p = function () {}; get p() { return this.#p; } }
            var c = new C();
            [c.x.name, c.computed.name, c.p.name].join();"));
    }

    [Fact]
    public void DefiningOverANonConfigurableOwnPropertyThrows()
    {
        Assert.Equal("TypeError", Run(@"
            function Base() { Object.defineProperty(this, 'x', { value: 0, configurable: false }); }
            class D extends Base { x = 1; }
            var name;
            try { new D(); } catch (e) { name = e.constructor.name; }
            name;"));
    }
}
