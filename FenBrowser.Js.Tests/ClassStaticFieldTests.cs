using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 15.7.14 ClassDefinitionEvaluation: elements are evaluated in
// source order (computed keys included), then static fields and static blocks
// run in source order, each as a method of the class, and a field's value is
// defined on its target with CreateDataPropertyOrThrow (7.3.34 DefineField).
public sealed class ClassStaticFieldTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AStaticInitializerSeesTheClassAsThis()
    {
        Assert.Equal("true,true,C", Run(@"
            var name;
            class C { static y = this; static z = () => this; static n = (name = this.name); }
            (C.y === C) + ',' + (C.z() === C) + ',' + name;"));
    }

    [Fact]
    public void SuperPropertiesWorkInFieldInitializers()
    {
        Assert.Equal("7,3,4", Run(@"
            class A { get v() { return 7; } static s() { return 3; } static get t() { return 4; } }
            class B extends A { x = super.v; static y = super.s(); static z = (() => super.t)(); }
            new B().x + ',' + B.y + ',' + B.z;"));
    }

    [Fact]
    public void StaticFieldsAndBlocksRunInSourceOrder()
    {
        Assert.Equal("f1,b1,f2,b2", Run(@"
            var seq = [];
            class C { static x = seq.push('f1'); static { seq.push('b1'); } static y = seq.push('f2'); static { seq.push('b2'); } }
            seq.join();"));
    }

    [Fact]
    public void ComputedKeysAreEvaluatedInElementOrderBeforeAnyInitializer()
    {
        Assert.Equal("4,5,3,6,false,false", Run(@"
            let i = 0;
            class C { [i++] = i++; static [i++] = i++; [i++] = i++; }
            var c = new C();
            [c[0], c[2], C[1], i, c.hasOwnProperty('1'), C.hasOwnProperty('0')].join();"));
    }

    [Fact]
    public void AFunctionValuedFieldIsAFieldNotAMethod()
    {
        Assert.Equal("y,true,false,true,x", Run(@"
            class A { static y = function () {}; x = function () {}; }
            var a = new A();
            [A.y.name, Object.getOwnPropertyDescriptor(A, 'y').enumerable, A.prototype.hasOwnProperty('x'),
             a.hasOwnProperty('x'), a.x.name].join();"));
    }

    [Fact]
    public void StaticFieldsAreDefinedNotAssigned()
    {
        Assert.Equal("test262,45,TypeError,undefined", Run(@"
            class C { static f = 'test'; static f = this.f + '262'; static g() { return 45; } static g = this.g(); }
            var err;
            try { class D { static ['prototype'] = 1; } } catch (e) { err = e.constructor.name; }
            class E { static t = new.target; }
            [C.f, C.g, err, String(E.t)].join();"));
    }

    [Fact]
    public void SuperCallIsStillAnEarlyErrorInAFieldInitializer()
    {
        Assert.Equal("SyntaxError", Run(@"
            var name;
            try { eval('class C extends Object { x = super(); }'); } catch (e) { name = e.constructor.name; }
            name;"));
    }
}
