using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ClassRuntimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ClassWithoutMembersIsCallable()
    {
        Assert.Equal("function", Run("class Foo {} typeof Foo;").AsString());
    }

    [Fact]
    public void DefaultConstructorProducesInstance()
    {
        Assert.Equal("object", Run("class Foo {} typeof (new Foo());").AsString());
    }

    [Fact]
    public void ConstructorInitialisesThis()
    {
        Assert.Equal(42d, Run(@"
            class Box { constructor(v) { this.value = v; } }
            (new Box(42)).value;
        ").AsNumber());
    }

    [Fact]
    public void InstanceMethodIsInheritedThroughPrototype()
    {
        Assert.Equal(15d, Run(@"
            class Adder {
                constructor(x) { this.x = x; }
                add(y) { return this.x + y; }
            }
            (new Adder(10)).add(5);
        ").AsNumber());
    }

    [Fact]
    public void MultipleMethodsLiveOnPrototype()
    {
        Assert.Equal(10d, Run(@"
            class Pair {
                constructor(a, b) { this.a = a; this.b = b; }
                sum() { return this.a + this.b; }
                diff() { return this.a - this.b; }
            }
            var p = new Pair(5, 2);
            p.sum() + p.diff();
        ").AsNumber()); // (5+2)+(5-2) = 10
    }

    [Fact]
    public void StaticMethodLivesOnConstructor()
    {
        Assert.Equal(99d, Run(@"
            class Util { static answer() { return 99; } }
            Util.answer();
        ").AsNumber());
    }

    [Fact]
    public void StaticDoesNotAppearOnInstance()
    {
        Assert.Equal("undefined", Run(@"
            class Util { static answer() { return 99; } }
            typeof (new Util()).answer;
        ").AsString());
    }

    [Fact]
    public void PrototypeConstructorReferencesClass()
    {
        Assert.True(Run(@"
            class Foo {}
            (new Foo()).constructor === Foo;
        ").AsBoolean());
    }

    [Fact]
    public void InstancePrototypeChainResolvesMethod()
    {
        // (new Foo()).hi === Foo.prototype.hi
        Assert.True(Run(@"
            class Foo { hi() { return 1; } }
            (new Foo()).hi === Foo.prototype.hi;
        ").AsBoolean());
    }

    [Fact]
    public void ClassExpressionInExpressionPosition()
    {
        Assert.Equal(11d, Run(@"
            var C = class { add(a, b) { return a + b; } };
            (new C()).add(5, 6);
        ").AsNumber());
    }

    // H.2 - extends prototype chain.

    [Fact]
    public void ExtendsInheritsBaseInstanceMethods()
    {
        Assert.Equal(1d, Run(@"
            class A { speak() { return 1; } }
            class B extends A {}
            (new B()).speak();
        ").AsNumber());
    }

    [Fact]
    public void ExtendsInheritsBaseStaticMethods()
    {
        Assert.Equal(42d, Run(@"
            class A { static answer() { return 42; } }
            class B extends A {}
            B.answer();
        ").AsNumber());
    }

    [Fact]
    public void DerivedMethodOverridesBase()
    {
        Assert.Equal(2d, Run(@"
            class A { speak() { return 1; } }
            class B extends A { speak() { return 2; } }
            (new B()).speak();
        ").AsNumber());
    }

    [Fact]
    public void DerivedPrototypeInheritsFromBasePrototype()
    {
        Assert.True(Run(@"
            class A {}
            class B extends A {}
            Object.getPrototypeOf(B.prototype) === A.prototype;
        ").AsBoolean());
    }

    [Fact]
    public void DerivedConstructorInheritsFromBaseConstructor()
    {
        Assert.True(Run(@"
            class A {}
            class B extends A {}
            Object.getPrototypeOf(B) === A;
        ").AsBoolean());
    }

    [Fact]
    public void ThreeLevelInheritanceResolves()
    {
        Assert.Equal(7d, Run(@"
            class A { f() { return 7; } }
            class B extends A {}
            class C extends B {}
            (new C()).f();
        ").AsNumber());
    }

    // H.4 - getter/setter accessor dispatch.

    [Fact]
    public void GetterRunsOnPropertyRead()
    {
        Assert.Equal(99d, Run(@"
            class Box { get answer() { return 99; } }
            (new Box()).answer;
        ").AsNumber());
    }

    [Fact]
    public void SetterRunsOnPropertyWrite()
    {
        Assert.Equal(7d, Run(@"
            class Box { set x(v) { this._x = v; } get x() { return this._x; } }
            var b = new Box();
            b.x = 7;
            b.x;
        ").AsNumber());
    }

    [Fact]
    public void PairedGetterAndSetterCoexist()
    {
        // Setting then getting through the accessor pair must round-trip.
        Assert.Equal(42d, Run(@"
            class Cell {
                get v() { return this._v; }
                set v(x) { this._v = x; }
            }
            var c = new Cell();
            c.v = 42;
            c.v;
        ").AsNumber());
    }

    [Fact]
    public void GetterOnInheritedPrototypeReachable()
    {
        Assert.Equal("hi", Run(@"
            class A { get greeting() { return 'hi'; } }
            class B extends A {}
            (new B()).greeting;
        ").AsString());
    }

    [Fact]
    public void StaticGetterLivesOnConstructor()
    {
        Assert.Equal(3d, Run(@"
            class N { static get three() { return 3; } }
            N.three;
        ").AsNumber());
    }

    // H.3 - super.method() lookup.

    [Fact]
    public void SuperMethodReadResolvesFromBasePrototype()
    {
        Assert.Equal(1d, Run(@"
            class A { f() { return 1; } }
            class B extends A { g() { return super.f(); } }
            (new B()).g();
        ").AsNumber());
    }

    [Fact]
    public void SuperMethodChainsThroughGrandparent()
    {
        Assert.Equal(7d, Run(@"
            class A { hi() { return 7; } }
            class B extends A {}
            class C extends B { call() { return super.hi(); } }
            (new C()).call();
        ").AsNumber());
    }

    [Fact]
    public void SuperPropertyAccessAsValue()
    {
        Assert.Equal("function", Run(@"
            class A { f() {} }
            class B extends A { describe() { return typeof super.f; } }
            (new B()).describe();
        ").AsString());
    }

    [Fact]
    public void DerivedSuperCallPreservesThis()
    {
        Assert.Equal(5d, Run(@"
            class A { setX() { this.x = 5; } }
            class B extends A { go() { super.setX(); return this.x; } }
            (new B()).go();
        ").AsNumber());
    }

    [Fact]
    public void SuperOutsideMethodThrowsReferenceError()
    {
        Assert.Throws<JsThrownException>(() =>
            Run("function f() { return super.x; } f();"));
    }

    // H.3.2 - super(...) constructor calls.

    [Fact]
    public void SuperConstructorCallInitialisesBaseFieldsOnDerivedInstance()
    {
        Assert.Equal(99d, Run(@"
            class A { constructor(v) { this.value = v; } }
            class B extends A { constructor(v) { super(v); } }
            (new B(99)).value;
        ").AsNumber());
    }

    [Fact]
    public void DerivedConstructorAddsItsOwnFieldsAfterSuper()
    {
        Assert.Equal(7d, Run(@"
            class A { constructor(v) { this.a = v; } }
            class B extends A {
                constructor(v) { super(v); this.b = v + 1; }
            }
            var b = new B(3);
            b.a + b.b;
        ").AsNumber()); // 3 + 4 = 7
    }

    [Fact]
    public void ThreeLevelSuperCallChainsAllConstructors()
    {
        Assert.Equal(6d, Run(@"
            class A { constructor() { this.x = 1; } }
            class B extends A { constructor() { super(); this.y = 2; } }
            class C extends B { constructor() { super(); this.z = 3; } }
            var c = new C();
            c.x + c.y + c.z;
        ").AsNumber());
    }
}
