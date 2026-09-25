using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.2.1 / 9.4.5 GetNewTarget: an arrow has no new.target of its own
// and reads the one of its lexically enclosing function, wherever the arrow is
// later called from.
public sealed class ArrowNewTargetTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void ArrowCalledLaterKeepsItsConstructorsNewTarget()
    {
        Assert.True(RunBool("function F() { this.a = () => new.target; } var f = new F(); f.a() === F;"));
    }

    [Fact]
    public void ArrowCalledFromAnotherConstructorIgnoresTheCaller()
    {
        Assert.True(RunBool(@"
            function F() { this.a = () => new.target; }
            var f = new F();
            function G() { return f.a(); }
            new G() === F;"));
    }

    [Fact]
    public void ArrowCreatedInAPlainCallSeesUndefinedEvenWhenCalledUnderNew()
    {
        Assert.True(RunBool(@"
            function make() { return () => new.target; }
            var arrow = make();
            function G() { this.seen = arrow(); }
            new G().seen === undefined;"));
    }

    [Fact]
    public void TopLevelArrowSeesUndefined()
    {
        Assert.True(RunBool("(() => new.target)() === undefined;"));
    }

    [Fact]
    public void NestedArrowsReachTheEnclosingFunction()
    {
        Assert.True(RunBool(@"
            function H() { return (() => () => new.target)()(); }
            new H() === H && H() === undefined;"));
    }

    [Fact]
    public void ArrowsInClassConstructorsSeeTheDerivedTarget()
    {
        Assert.True(RunBool(@"
            class A { constructor() { this.f = () => new.target; } }
            class B extends A { constructor() { super(); this.g = () => new.target; } }
            var b = new B();
            b.f() === B && b.g() === B && new A().f() === A;"));
    }

    [Fact]
    public void ReflectConstructNewTargetReachesTheArrow()
    {
        Assert.True(RunBool(@"
            Reflect.construct(function () { return { t: (() => new.target)() }; }, [], Array).t === Array;"));
    }

    // Field initializers are compiled into the constructor but are methods of
    // their own with no new.target (ECMA-262 7.3.34 DefineField).
    [Fact]
    public void FieldInitializerSeesUndefined()
    {
        Assert.True(RunBool(@"
            class C { x = new.target; y = () => new.target; z = (() => () => new.target)(); }
            var c = new C();
            c.x === undefined && c.y() === undefined && c.z() === undefined;"));
    }

    [Fact]
    public void DerivedFieldInitializerSeesUndefined()
    {
        Assert.True(RunBool(@"
            class A {}
            class B extends A { f = () => new.target; constructor() { super(); this.g = () => new.target; } }
            var b = new B();
            b.f() === undefined && b.g() === B;"));
    }

    [Fact]
    public void EvalInAFieldInitializerSeesUndefined()
    {
        Assert.True(RunBool(@"
            class C { x = eval('new.target'); y = eval('() => new.target'); }
            var c = new C();
            c.x === undefined && c.y() === undefined;"));
    }

    [Fact]
    public void EvalInAConstructorSeesItsNewTarget()
    {
        Assert.True(RunBool(@"
            function F() { this.a = eval('new.target'); this.b = eval('() => new.target'); }
            var f = new F();
            f.a === F && f.b() === F && (0, eval)('typeof (() => 1)') === 'function';"));
    }
}
