using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Classes on the register-window loop: derived constructors (ECMA-262 10.2.2
// [[Construct]], 13.3.7.1 SuperCall), and the opcodes a class definition and a
// constructor use - heritage, private and computed fields, SetFunctionName.
// Each case defines its classes inside a function so the definition itself
// runs on this loop too.
public sealed class Interp2ClassTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string body) =>
        new BytecodeInterpreter().Execute(Compile("function t() { " + body + " } String(t());")).AsString();

    [Fact]
    public void DerivedConstructorsAndClassDefinitionsRunOnTheRegisterWindow()
    {
        var script = Compile(
            "function t() { var k = 'k'; class A { #p = 1; [k] = 2; } class B extends A { constructor() { super(); } } }");
        var definer = script.NestedFunctions[0];
        Assert.Equal(Interp2Bailout.None, FrameLayout.For(definer).Bailout);
        foreach (var constructor in definer.NestedFunctions)
        {
            Assert.Equal(Interp2Bailout.None, FrameLayout.ForConstruct(constructor).Bailout);
        }
    }

    [Fact]
    public void OnlyTheConstructorItselfIsMarkedAClassConstructor()
    {
        // A field initializer's class, its method, and a function and an arrow
        // made in a derived constructor are ordinary functions of their own.
        var script = Compile(
            "class A {} class B extends A { f = class { m() { return 1; } }; " +
            "constructor() { super(); this.g = function () { return 2; }; this.h = () => 3; } }");
        var constructor = Assert.Single(script.NestedFunctions, fn => fn.IsClassConstructor && fn.IsDerivedConstructor);
        foreach (var nested in Descendants(constructor))
        {
            Assert.False(nested.IsClassConstructor && nested.Kind != FenBrowser.Js.Objects.FunctionKind.Constructor);
            Assert.False(nested.IsDerivedConstructor && nested.Kind != FenBrowser.Js.Objects.FunctionKind.Constructor);
            if (nested.Kind != FenBrowser.Js.Objects.FunctionKind.Constructor)
            {
                Assert.Equal(Interp2Bailout.None, FrameLayout.For(nested).Bailout);
            }
        }

        Assert.Equal("1,2,3", new BytecodeInterpreter().Execute(Compile(
            "class A {} class B extends A { f = class { m() { return 1; } }; " +
            "constructor() { super(); this.g = function () { return 2; }; this.h = () => 3; } }" +
            " var b = new B(); [new b.f().m(), b.g(), b.h()].join();")).AsString());
    }

    private static IEnumerable<BytecodeFunction> Descendants(BytecodeFunction function)
    {
        foreach (var nested in function.NestedFunctions)
        {
            yield return nested;
            foreach (var deeper in Descendants(nested))
            {
                yield return deeper;
            }
        }
    }

    [Theory]
    [InlineData("class A { constructor(x) { this.x = x; } } class B extends A { constructor() { super(5); this.y = this.x + 1; } }" +
                " var b = new B(); return b.x + ',' + b.y;", "5,6")]
    // The default derived constructor forwards every argument.
    [InlineData("class A { constructor(x, y) { this.s = x + y; } } class B extends A {} return new B(3, 4).s;", "7")]
    [InlineData("class A { constructor() { this.nt = new.target; } } class B extends A {} return new B().nt === B;", "true")]
    [InlineData("class A {} class B extends A { f = this.constructor.name; } return new B().f;", "B")]
    // super() inside an arrow binds the constructor's `this`.
    [InlineData("class A { constructor(v) { this.v = v; } } class B extends A { constructor() { var f = () => super(3); f(); this.z = this.v * 2; } }" +
                " return new B().z;", "6")]
    [InlineData("class A {} class B extends A { constructor() { super(); var g = () => this; this.same = g() === this; } }" +
                " return new B().same;", "true")]
    // An object returned from a derived constructor is the result.
    [InlineData("class A {} class B extends A { constructor() { super(); return { own: 1 }; } } return new B().own;", "1")]
    [InlineData("class A {} class B extends A { constructor() { super(); return undefined; } } return new B() instanceof B;", "true")]
    public void DerivedConstructorsBindThisThroughSuper(string body, string expected)
    {
        Assert.Equal(expected, Run(body));
    }

    [Theory]
    [InlineData("class A {} class B extends A { constructor() { this.a = 1; super(); } }", "ReferenceError")]
    [InlineData("class A {} class B extends A { constructor() { super(); super(); } }", "ReferenceError")]
    [InlineData("class A {} class B extends A { constructor() { } }", "ReferenceError")]
    [InlineData("class A {} class B extends A { constructor() { super(); return 1; } }", "TypeError")]
    [InlineData("class B extends null { constructor() { super(); } }", "TypeError")]
    // super.x reads `this` first (ECMA-262 13.3.7.1).
    [InlineData("class A {} class B extends A { constructor() { super.x; super(); } }", "ReferenceError")]
    [InlineData("class A {} class B extends A { constructor() { super['x']; super(); } }", "ReferenceError")]
    public void DerivedConstructorsReportMisuse(string classes, string expected)
    {
        Assert.Equal(expected, Run(classes + " try { new B(); return 'no error'; } catch (e) { return e.constructor.name; }"));
    }

    [Fact]
    public void ASuperConstructorThatIsNotOneFailsAfterTheArguments()
    {
        // ECMA-262 13.3.7.1: GetSuperConstructor, then the arguments, then the
        // IsConstructor check.
        Assert.Equal("TypeError:1", Run(
            "var log = []; class A {} class C extends A { constructor() { super(log.push('arg')); } }" +
            " Object.setPrototypeOf(C, null);" +
            " try { new C(); } catch (e) { return e.constructor.name + ':' + log.length; }"));
    }

    [Theory]
    [InlineData("class MyArray extends Array {} var a = new MyArray(); a.push(1); return a.length + ',' + (a instanceof MyArray);", "1,true")]
    [InlineData("class MyError extends Error { constructor(m) { super(m); this.name = 'MyError'; } } var e = new MyError('x');" +
                " return e.message + ',' + e.name + ',' + (e instanceof Error);", "x,MyError,true")]
    public void ANativeBaseConstructsThroughSuper(string body, string expected)
    {
        Assert.Equal(expected, Run(body));
    }

    [Theory]
    [InlineData("class P { #v = 1; get() { return this.#v; } } return new P().get();", "1")]
    [InlineData("class P { #v = 1; get() { return this.#v; } } class Q extends P { #w = 2; sum() { return this.#w + this.get(); } }" +
                " return new Q().sum();", "3")]
    [InlineData("var k = 'dyn'; class R { [k] = 4; } return new R().dyn;", "4")]
    [InlineData("var o = { [Symbol.iterator]: function () {} }; return o[Symbol.iterator].name;", "[Symbol.iterator]")]
    [InlineData("try { class X extends 5 {} } catch (e) { return e.constructor.name; }", "TypeError")]
    public void ClassDefinitionOpcodesBehave(string body, string expected)
    {
        Assert.Equal(expected, Run(body));
    }
}
