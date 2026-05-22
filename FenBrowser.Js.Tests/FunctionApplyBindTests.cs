using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class FunctionApplyBindTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void ApplyForwardsArrayAsArgs()
    {
        Assert.Equal(3, RunNum("function add(a,b){return a+b;} add.apply(null, [1,2]);"));
    }

    [Fact]
    public void ApplyWithNullArgsArrayInvokesWithNoArgs()
    {
        Assert.Equal(0, RunNum("function n(){return arguments.length;} n.apply(null, null);"));
    }

    [Fact]
    public void ApplyWithUndefinedArgsArrayInvokesWithNoArgs()
    {
        Assert.Equal(0, RunNum("function n(){return arguments.length;} n.apply(null);"));
    }

    [Fact]
    public void ApplySetsThisArg()
    {
        Assert.Equal(7, RunNum("function f(){return this.v;} f.apply({v:7});"));
    }

    [Fact]
    public void ApplyArrayLikeObjectWorks()
    {
        Assert.Equal(11, RunNum("function add(a,b){return a+b;} add.apply(null, {length:2, 0:5, 1:6});"));
    }

    [Fact]
    public void ApplyNonObjectNonNullArgsArrayThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("(function(){}).apply(null, 42);"));
    }

    [Fact]
    public void BindReturnsCallableThatPinsThis()
    {
        Assert.Equal(9, RunNum("function f(){return this.v;} var b = f.bind({v:9}); b();"));
    }

    [Fact]
    public void BindPartialApplication()
    {
        Assert.Equal(7, RunNum("function add(a,b,c){return a+b+c;} var p = add.bind(null, 1, 2); p(4);"));
    }

    [Fact]
    public void BindWithExtraArgsAtCallSite()
    {
        Assert.Equal(10, RunNum("function add(a,b,c,d){return a+b+c+d;} var p = add.bind(null, 1); p(2,3,4);"));
    }

    [Fact]
    public void BindChainedPartialApplicationWorks()
    {
        Assert.Equal(7, RunNum("function add(a,b){return a+b;} var p = add.bind(null, 3).bind(null, 4); p();"));
    }
}
