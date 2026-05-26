using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class GeneratorFunctionTests
{
    private static JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // ECMA-262 27.3 — GeneratorFunction constructor exists as a global.
    [Fact]
    public void GeneratorFunction_IsGlobalProperty()
    {
        Assert.Equal("function", Run("typeof GeneratorFunction;").AsString());
    }

    [Fact]
    public void GeneratorFunction_Constructor_CreatesGenerator()
    {
        var code = @"
            var GF = GeneratorFunction;
            var genFn = new GF('yield 1; yield 2;');
            var g = genFn();
            g.next().value;
        ";
        Assert.Equal(1d, Run(code).AsNumber());
    }

    [Fact]
    public void GeneratorFunction_CreatesFunctionThatReturnsGeneratorObject()
    {
        var code = @"
            var genFn = new GeneratorFunction('yield 42;');
            var g = genFn();
            typeof g.next === 'function';
        ";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void GeneratorFunction_CanBeCalledWithoutNew()
    {
        var code = @"
            var genFn = GeneratorFunction('yield 1; yield 2;');
            var g = genFn();
            g.next().value + g.next().value;
        ";
        Assert.Equal(3d, Run(code).AsNumber());
    }

    [Fact]
    public void GeneratorFunction_WithParameters()
    {
        var code = @"
            var genFn = new GeneratorFunction('x', 'y', 'yield x; yield y;');
            var g = genFn(10, 20);
            g.next().value + g.next().value;
        ";
        Assert.Equal(30d, Run(code).AsNumber());
    }

    [Fact]
    public void GeneratorFunction_PrototypeIsGeneratorPrototype()
    {
        var code = @"
            var genFn = new GeneratorFunction('yield 1;');
            var g = genFn();
            Object.getPrototypeOf(g).constructor === GeneratorFunction;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void GeneratorFunction_GeneratesDoneTrueAfterCompletion()
    {
        var code = @"
            var genFn = new GeneratorFunction('yield 1;');
            var g = genFn();
            g.next();
            g.next().done;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void GeneratorFunction_InstanceOfGeneratorFunction()
    {
        var code = @"
            var genFn = new GeneratorFunction('yield 1;');
            genFn instanceof GeneratorFunction;
        ";
        Assert.True(Run(code).AsBoolean());
    }

    [Fact]
    public void GeneratorFunction_FunctionBodyIsEmpty_YieldsUndefined()
    {
        var code = @"
            var genFn = new GeneratorFunction('');
            var g = genFn();
            g.next().done;
        ";
        Assert.True(Run(code).AsBoolean());
    }
}
