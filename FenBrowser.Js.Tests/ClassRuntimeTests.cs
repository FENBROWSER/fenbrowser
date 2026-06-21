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
    public void SuperPropertyAssignmentWritesThroughThis()
    {
        Assert.Equal(1d, Run(@"
            class A {}
            class B extends A { go() { super.makeBugs = 1; return this.makeBugs; } }
            (new B()).go();
        ").AsNumber());
    }

    [Fact]
    public void BaseClassConstructorSuperPropertyResolvesFromObjectPrototype()
    {
        Assert.Equal(1d, Run(@"
            class A {
                constructor() { super.toString(); }
                dontDoThis() { super.makeBugs = 1; }
            }
            var a = new A();
            a.dontDoThis();
            a.makeBugs;
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

    [Fact]
    public void DerivedConstructorThisBeforeSuperThrowsReferenceError()
    {
        Assert.Equal("ReferenceError", Run(@"
            class A { constructor() {} }
            class B extends A { constructor() { this.x = 1; super(); } }
            try { new B(); 'no-throw'; } catch (e) { e.name; }
        ").AsString());
    }

    [Fact]
    public void ArrowCapturesDerivedConstructorThisTdzUntilSuperReturns()
    {
        Assert.True(Run(@"
            var readThis;
            var beforeSuper;
            var afterSuper;
            class A {
                constructor() {
                    try { readThis(); }
                    catch (error) { beforeSuper = error.name; }
                }
            }
            class B extends A {
                constructor() {
                    readThis = () => this;
                    super();
                    afterSuper = readThis() === this;
                }
            }
            new B();
            beforeSuper === 'ReferenceError' && afterSuper;
        ").AsBoolean());
    }

    [Fact]
    public void ClassConstructorCannotBeCalledViaApply()
    {
        Assert.Equal("TypeError", Run(@"
            class Base {}
            class Derived extends Base {}
            try {
                Derived.apply({}, []);
                'no-throw';
            } catch (e) {
                e.name;
            }
        ").AsString());
    }

    [Fact]
    public void ClassMethodUsesRestrictedArgumentsObject()
    {
        Assert.Equal("TypeError", Run(@"
            var D = class extends function() {
              arguments.callee;
            } {};
            try {
              new D();
              'no-throw';
            } catch (e) {
              e.name;
            }
        ").AsString());
    }

    // H.5 - computed property names.

    [Fact]
    public void ComputedStringKeyMethod()
    {
        Assert.Equal(42d, Run(@"
            class C {
                ['foo']() { return 42; }
            }
            (new C()).foo();
        ").AsNumber());
    }

    [Fact]
    public void ComputedStringKeyViaBracketNotation()
    {
        Assert.Equal(42d, Run(@"
            class C {
                ['bar']() { return 42; }
            }
            var c = new C();
            c['bar']();
        ").AsNumber());
    }

    [Fact]
    public void ComputedNumericKeyMethod()
    {
        Assert.Equal(99d, Run(@"
            class C {
                [0]() { return 99; }
            }
            (new C())[0]();
        ").AsNumber());
    }

    [Fact]
    public void ComputedStringKeyGetter()
    {
        Assert.Equal("computed-get", Run(@"
            class C {
                get ['greeting']() { return 'computed-get'; }
            }
            (new C()).greeting;
        ").AsString());
    }

    [Fact]
    public void ComputedStringKeySetter()
    {
        Assert.Equal(123d, Run(@"
            class C {
                set ['val'](x) { this._v = x; }
            }
            var c = new C();
            c.val = 123;
            c._v;
        ").AsNumber());
    }

    [Fact]
    public void StaticMethodWithComputedKey()
    {
        Assert.Equal("static-computed", Run(@"
            class C {
                static ['greet']() { return 'static-computed'; }
            }
            C.greet();
        ").AsString());
    }

    [Fact]
    public void InstanceMethodWithComputedKeyOnPrototype()
    {
        Assert.True(Run(@"
            class C {
                ['dynamicMethod']() { return true; }
            }
            var proto = C.prototype;
            var c = new C();
            proto['dynamicMethod'] === c['dynamicMethod'];
        ").AsBoolean());
    }

    [Fact]
    public void MultipleComputedKeys()
    {
        Assert.Equal(9d, Run(@"
            class C {
                ['add'](a, b) { return a + b; }
                ['sub'](a, b) { return a - b; }
                ['mul'](a, b) { return a * b; }
            }
            var c = new C();
            c['add'](1, 2) + c['sub'](5, 1) + c['mul'](2, 1);
        ").AsNumber()); // 3 + 4 + 2 = 9
    }

    // H.5 - static initialization blocks (ECMA-262 15.7.10).
    [Fact]
    public void StaticBlockRunsAtClassDefinition()
    {
        Assert.Equal(7d, Run(@"
            class C {
                static { C.x = 7; }
            }
            C.x;
        ").AsNumber());
    }

    [Fact]
    public void StaticBlockThisIsTheClass()
    {
        Assert.True(Run(@"
            var captured;
            class C {
                static { captured = this; }
            }
            captured === C;
        ").AsBoolean());
    }

    [Fact]
    public void MultipleStaticBlocksRunInSourceOrder()
    {
        Assert.Equal("ab", Run(@"
            class C {
                static { C.s = 'a'; }
                static { C.s += 'b'; }
            }
            C.s;
        ").AsString());
    }

    [Fact]
    public void StaticBlockInterleavesWithStaticMethods()
    {
        Assert.Equal(11d, Run(@"
            class C {
                static add(a, b) { return a + b; }
                static { C.total = C.add(4, 7); }
            }
            C.total;
        ").AsNumber());
    }

    [Fact]
    public void StaticBlockCanReadStaticGetter()
    {
        Assert.Equal(5d, Run(@"
            class C {
                static get five() { return 5; }
                static { C.captured = C.five; }
            }
            C.captured;
        ").AsNumber());
    }

    [Fact]
    public void StaticBlockSeesLocalScopeOfBlock()
    {
        // Per spec the block has its own lexical scope; `let x` here must not
        // leak onto the class.
        Assert.Equal("undefined", Run(@"
            class C {
                static { let x = 99; C.y = x + 1; }
            }
            typeof C.x + ':' + C.y;
        ").AsString().Substring(0, 9));
    }

    // H.5 - public instance fields.
    [Fact]
    public void InstanceFieldInitializerSetsOnEachInstance()
    {
        Assert.Equal(10d, Run(@"
            class C { x = 10; }
            (new C()).x;
        ").AsNumber());
    }

    [Fact]
    public void InstanceFieldWithoutInitializerIsUndefined()
    {
        Assert.Equal("undefined", Run(@"
            class C { x; }
            typeof (new C()).x;
        ").AsString());
    }

    [Fact]
    public void InstanceFieldRunsBeforeConstructorBody()
    {
        Assert.Equal(15d, Run(@"
            class C {
                x = 5;
                constructor() { this.x += 10; }
            }
            (new C()).x;
        ").AsNumber());
    }

    [Fact]
    public void MultipleInstanceFieldsInitInOrder()
    {
        Assert.Equal(6d, Run(@"
            class C {
                a = 1;
                b = this.a + 2;
                c = this.b + 3;
            }
            var c = new C();
            c.c;
        ").AsNumber());
    }

    [Fact]
    public void InstanceFieldIsOwnNotPrototype()
    {
        Assert.True(Run(@"
            class C { x = 1; }
            var c = new C();
            Object.hasOwn(c, 'x') && !Object.hasOwn(C.prototype, 'x');
        ").AsBoolean());
    }

    // H.5 - computed-name public fields.
    [Fact]
    public void ComputedInstanceFieldKeyEvaluatedAtClassDefinition()
    {
        Assert.Equal(7d, Run(@"
            var key = 'x';
            class C { [key] = 7; }
            (new C()).x;
        ").AsNumber());
    }

    [Fact]
    public void ComputedStaticFieldKey()
    {
        Assert.Equal(3d, Run(@"
            class C { static ['foo'] = 3; }
            C.foo;
        ").AsNumber());
    }

    // ECMA-262 13.3.7.1 — explicit spread in call expressions.
    [Fact]
    public void ExplicitSpreadInCallUnpacksArgs()
    {
        Assert.Equal(6d, Run(@"
            function add(a, b, c) { return a + b + c; }
            add(...[1, 2, 3]);
        ").AsNumber());
    }

    // H.5 - derived classes with default constructor + instance fields.
    [Fact]
    public void DerivedClassDefaultConstructorCallsSuper()
    {
        Assert.Equal(5d, Run(@"
            class A { constructor() { this.a = 5; } }
            class B extends A {}
            (new B()).a;
        ").AsNumber());
    }

    // ECMA-262 15.7.10 — derived default constructor forwards arguments
    // via super(...arguments).
    [Fact]
    public void DerivedDefaultConstructorForwardsArguments()
    {
        Assert.Equal("hello", Run(@"
            class A { constructor(x) { this.name = x; } }
            class B extends A {}
            (new B('hello')).name;
        ").AsString());
    }

    [Fact]
    public void DerivedDefaultConstructorForwardsMultipleArguments()
    {
        Assert.Equal(42d, Run(@"
            class A { constructor(a, b) { this.sum = a + b; } }
            class B extends A {}
            (new B(20, 22)).sum;
        ").AsNumber());
    }

    [Fact]
    public void DerivedClassInstanceFieldInitsAfterDefaultSuper()
    {
        Assert.Equal(8d, Run(@"
            class A { constructor() { this.x = 1; } }
            class B extends A { y = this.x + 7; }
            (new B()).y;
        ").AsNumber());
    }

    [Fact]
    public void DerivedClassExplicitSuperThenFieldsThenBody()
    {
        Assert.Equal(30d, Run(@"
            class A { constructor() { this.v = 10; } }
            class B extends A {
                w = this.v * 2;
                constructor() { super(); this.r = this.w + this.v; }
            }
            (new B()).r;
        ").AsNumber());
    }

    // H.5 - public static fields.
    [Fact]
    public void StaticFieldInitializerSetsOnClass()
    {
        Assert.Equal(42d, Run(@"
            class C { static x = 42; }
            C.x;
        ").AsNumber());
    }

    [Fact]
    public void StaticFieldVisibleToStaticBlock()
    {
        Assert.Equal(8d, Run(@"
            class C {
                static x = 3;
                static { C.x += 5; }
            }
            C.x;
        ").AsNumber());
    }

    // H.5 - private fields and methods (mangled-name implementation).
    [Fact]
    public void PrivateFieldRoundTripsViaAccessorMethod()
    {
        Assert.Equal(42d, Run(@"
            class C {
                #x = 42;
                getX() { return this.#x; }
            }
            (new C()).getX();
        ").AsNumber());
    }

    [Fact]
    public void PrivateFieldWriteFromMethod()
    {
        Assert.Equal(99d, Run(@"
            class C {
                #x = 0;
                setX(v) { this.#x = v; }
                getX() { return this.#x; }
            }
            var c = new C();
            c.setX(99);
            c.getX();
        ").AsNumber());
    }

    [Fact]
    public void PrivateMethodCallableFromPublicMethod()
    {
        Assert.Equal(50d, Run(@"
            class C {
                #double(v) { return v * 2; }
                run(v) { return this.#double(v); }
            }
            (new C()).run(25);
        ").AsNumber());
    }

    [Fact]
    public void PrivateGetterReadableWithNoPrivateField()
    {
        // ECMA-262 PrivateBrandAdd: a class whose only private member is an accessor
        // must still brand its instances so `this.#m` resolves through the accessor.
        Assert.Equal(7d, Run(@"
            class C { get #m() { return 7; } read() { return this.#m; } }
            (new C()).read();
        ").AsNumber());
    }

    [Fact]
    public void PrivateSetterInvokedWithNoPrivateField()
    {
        Assert.Equal(9d, Run(@"
            class C { set #m(v) { this._v = v; } run() { this.#m = 9; return this._v; } }
            (new C()).run();
        ").AsNumber());
    }

    [Fact]
    public void PrivateGetterOnlyWriteThrowsTypeError()
    {
        // PrivateSet on a getter-only accessor is a TypeError.
        Assert.True(Run(@"
            class C { get #m() { return 1; } run() { this.#m = 2; } }
            var ok = false;
            try { (new C()).run(); } catch (e) { ok = e instanceof TypeError; }
            ok;
        ").AsBoolean());
    }

    [Fact]
    public void StaticPrivateFieldReadableFromStaticMethod()
    {
        // The class constructor object carries the class brand so `C.#s` resolves.
        Assert.Equal(5d, Run(@"
            class C { static #s = 5; static get() { return C.#s; } }
            C.get();
        ").AsNumber());
    }

    [Fact]
    public void StaticPrivateMethodCallableFromStaticMethod()
    {
        Assert.Equal(8d, Run(@"
            class C { static #m() { return 8; } static run() { return C.#m(); } }
            C.run();
        ").AsNumber());
    }

    [Fact]
    public void PrivateNameIsNotEnumerableAsPublic()
    {
        // Private names are mangled, so user code can't reach them via the
        // `#x` syntax from outside the class.
        Assert.True(Run(@"
            class C { #x = 1; }
            var c = new C();
            !Object.keys(c).includes('#x');
        ").AsBoolean());
    }

    [Fact]
    public void PrivateNamesAreScopedPerClass()
    {
        // Two classes with the same private name should not collide; each gets
        // its own mangled slot.
        Assert.Equal(12d, Run(@"
            class A { #v = 5; get() { return this.#v; } }
            class B { #v = 7; get() { return this.#v; } }
            (new A()).get() + (new B()).get();
        ").AsNumber());
    }

    // H.5 - new.target meta-property.
    [Fact]
    public void NewTargetInPlainConstructorFunction()
    {
        // Use a captured-via-closure pattern: assigning new.target to a property
        // of `this` exposes a separate issue with property reflection on the
        // returned instance; that's tracked separately.
        Assert.Equal("function", Run(@"
            var captured;
            function F() { captured = new.target; }
            new F();
            typeof captured;
        ").AsString());
    }

    [Fact]
    public void NewTargetCapturedFromClassConstructor()
    {
        Assert.Equal("function", Run(@"
            var captured;
            class C { constructor() { captured = new.target; } }
            new C();
            typeof captured;
        ").AsString());
    }

    [Fact]
    public void NewTargetClassIdentityFromCapture()
    {
        Assert.True(Run(@"
            var captured;
            class C { constructor() { captured = new.target; } }
            new C();
            captured === C;
        ").AsBoolean());
    }

    [Fact]
    public void NewTargetIsUndefinedInPlainFunctionCall()
    {
        Assert.Equal("undefined", Run(@"
            function f() { return typeof new.target; }
            f();
        ").AsString());
    }

    // H.5 - ECMA-262 12.2.6.7: numeric-literal class member names install
    // under their ToString(Number) canonical key.
    [Fact]
    public void NumericLiteralClassMemberInstallsUnderCanonicalKey()
    {
        Assert.Equal("get string", Run(@"
            class C {
                get 0b10() { return 'get string'; }
            }
            C.prototype['2'];
        ").AsString());
    }

    [Fact]
    public void StringLiteralClassMemberInstallsUnderUnquotedKey()
    {
        Assert.Equal("hi", Run(@"
            class C {
                get 'greeting'() { return 'hi'; }
            }
            (new C()).greeting;
        ").AsString());
    }

    [Fact]
    public void HexEscapeInStringLiteralClassMemberKey()
    {
        Assert.Equal("ok", Run(@"
            class C {
                get '\x41'() { return 'ok'; }
            }
            (new C())['A'];
        ").AsString());
    }

    [Fact]
    public void UnicodeEscapeInStringLiteralClassMemberKey()
    {
        Assert.Equal("ok", Run(@"
            class C {
                get 'B'() { return 'ok'; }
            }
            (new C())['B'];
        ").AsString());
    }

    [Fact]
    public void UnicodeBraceEscapeInStringLiteralClassMemberKey()
    {
        Assert.Equal("ok", Run(@"
            class C {
                get '\u{0043}'() { return 'ok'; }
            }
            (new C())['C'];
        ").AsString());
    }

    [Fact]
    public void EmptyStringLiteralClassMemberKey()
    {
        Assert.Equal(9d, Run(@"
            class C {
                ['']() { return 9; }
            }
            (new C())['']();
        ").AsNumber());
    }

    [Fact]
    public void SmallDecimalNumericLiteralCanonicalForm()
    {
        // ECMA-262 6.1.6.1.13: 0.0000001 stringifies to "1e-7" (lowercase
        // 'e', signed exponent with no leading zero), not "1E-07".
        Assert.Equal("ok", Run(@"
            class C { get 0.0000001() { return 'ok'; } }
            C.prototype['1e-7'];
        ").AsString());
    }

    [Fact]
    public void HexLiteralClassMemberInstallsUnderCanonicalKey()
    {
        Assert.Equal(7d, Run(@"
            class C {
                static 0xff() { return 7; }
            }
            C['255']();
        ").AsNumber());
    }

    [Fact]
    public void EmptyStaticBlockIsValid()
    {
        Assert.Equal("function", Run(@"
            class C {
                static {}
            }
            typeof C;
        ").AsString());
    }

    [Fact]
    public void SuperComputedMemberReadStringKey()
    {
        // ECMA-262 13.3.7.3 MakeSuperPropertyReference with computed key.
        Assert.Equal(7d, Run(@"
            class A { m() { return 7; } }
            class B extends A {
                use() { return super['m'](); }
            }
            (new B()).use();
        ").AsNumber());
    }

    [Fact]
    public void SuperComputedMemberReadSymbolKey()
    {
        // The original failing pattern from String.prototype.replaceAll
        // searchValue tests: super[Symbol.replace](...args).
        Assert.Equal(42d, Run(@"
            const SYM = Symbol('m');
            class A { }
            A.prototype[SYM] = function() { return 42; };
            class B extends A {
                use() { return super[SYM](); }
            }
            (new B()).use();
        ").AsNumber());
    }

    [Fact]
    public void SuperComputedMemberReadDynamicKey()
    {
        Assert.Equal("xy", Run(@"
            class A { x() { return 'x'; } y() { return 'y'; } }
            class B extends A {
                go(k1, k2) { return super[k1]() + super[k2](); }
            }
            (new B()).go('x','y');
        ").AsString());
    }
}
