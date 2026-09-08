using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.4.4.6 step 6 and 10.4.4.7 step 15 give an arguments object
// %Array.prototype.values% -- the intrinsic, not whatever Array.prototype.values
// holds when the call is made.
public class ArgumentsObjectIteratorTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ArgumentsIterator_IsTheIntrinsic_EvenAfterArrayPrototypeIsReplaced()
    {
        var v = Run(@"
            var intrinsic = Array.prototype.values;
            function before() { return arguments[Symbol.iterator] === intrinsic; }
            var a = before(1);
            Array.prototype.values = function () { return 'replaced'; };
            function after() { return arguments[Symbol.iterator] === intrinsic; }
            String(a) + ',' + String(after(1));
        ");
        Assert.Equal("true,true", v);
    }

    [Fact]
    public void SpreadingArguments_SurvivesAReplacedArrayPrototypeValues()
    {
        var v = Run(@"
            Array.prototype.values = function () { return 'replaced'; };
            function spread() { return [...arguments].join('-'); }
            spread(1, 2, 3);
        ");
        Assert.Equal("1-2-3", v);
    }

    [Fact]
    public void AParameterNamedArguments_ShadowsTheArgumentsObject()
    {
        var v = Run(@"
            function shadowed(arguments) { return String(arguments); }
            shadowed('given');
        ");
        Assert.Equal("given", v);
    }

    [Fact]
    public void TheArgumentsObject_StillCarriesLengthAndIndices()
    {
        var v = Run(@"
            function shape() { return arguments.length + ':' + arguments[0] + ',' + arguments[2]; }
            shape('a', 'b', 'c');
        ");
        Assert.Equal("3:a,c", v);
    }
}
