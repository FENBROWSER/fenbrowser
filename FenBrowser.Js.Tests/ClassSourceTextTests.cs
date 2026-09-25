using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 15.7.14 step 18 / 20.2.3.5: a class's [[SourceText]] is the whole
// class, which Function.prototype.toString returns.
public sealed class ClassSourceTextTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AClassDeclarationPrintsAsTheWholeClass()
    {
        Assert.Equal("class A extends Object { constructor() { super(); } m() {} }", Run(@"
            class A extends Object { constructor() { super(); } m() {} }
            String(A);"));
    }

    [Fact]
    public void AClassExpressionWithoutAConstructorPrintsAsTheClass()
    {
        Assert.Equal("class { static x = 1; }|class Named {}", Run(@"
            var a = class { static x = 1; };
            var b = class Named {};
            a.toString() + '|' + b.toString();"));
    }
}
