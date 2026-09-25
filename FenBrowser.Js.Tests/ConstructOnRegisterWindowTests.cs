using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// An ordinary constructor the register-window loop can run is executed there
// by [[Construct]] (ECMA-262 10.2.2), with the instance created first as
// OrdinaryCreateFromConstructor does. Everything [[Construct]] promises has to
// hold on that path as it does on the old one.
public sealed class ConstructOnRegisterWindowTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void TheInstanceGetsTheConstructorsPrototypeAndThis()
    {
        Assert.True(RunBool(@"
            function P(x) { this.x = x; this.y = x * 2; }
            P.prototype.sum = function () { return this.x + this.y; };
            var ok = true;
            for (var i = 0; i < 100; i++) {
                var p = new P(i);
                ok = ok && Object.getPrototypeOf(p) === P.prototype && p.sum() === i * 3 && p instanceof P;
            }
            ok;"));
    }

    [Fact]
    public void AnObjectReturnReplacesTheInstanceAndAPrimitiveDoesNot()
    {
        Assert.True(RunBool(@"
            var other = { marker: 1 };
            function ReturnsObject() { this.x = 1; return other; }
            function ReturnsPrimitive() { this.x = 2; return 42; }
            new ReturnsObject() === other && new ReturnsPrimitive().x === 2;"));
    }

    [Fact]
    public void ReflectConstructUsesTheNewTargetsPrototype()
    {
        Assert.True(RunBool(@"
            function Base() { this.made = true; }
            function Other() {}
            Other.prototype.tag = 'other';
            var o = Reflect.construct(Base, [], Other);
            o.made === true && Object.getPrototypeOf(o) === Other.prototype && o.tag === 'other';"));
    }

    [Fact]
    public void AConstructorThatReadsNewTargetStillSeesIt()
    {
        Assert.True(RunBool(@"
            function Direct() { this.t = new.target; }
            function ViaArrow() { this.t = (() => new.target)(); }
            new Direct().t === Direct && new ViaArrow().t === ViaArrow && Reflect.construct(Direct, [], ViaArrow).t === ViaArrow;"));
    }

    [Fact]
    public void AThrowingConstructorPropagatesAndLeavesNoPendingNewTarget()
    {
        Assert.True(RunBool(@"
            function Boom() { this.x = 1; throw new TypeError('no'); }
            function After() { return new.target; }
            var caught = false;
            try { new Boom(); } catch (e) { caught = e instanceof TypeError; }
            caught && After() === undefined;"));
    }
}
