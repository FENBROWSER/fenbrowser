using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class BytecodeInterpreterEnvironmentTests
{
    [Fact]
    public void ExecuteWithEnvironmentKeepsTopLevelBindingsOutOfGlobalObject()
    {
        var interpreter = new BytecodeInterpreter();
        var environment = new ModuleEnvironmentRecord(outerEnv: null);
        var function = new BytecodeCompiler().CompileScript(new SourceText("var local = 7; local;"));

        var result = interpreter.ExecuteWithEnvironment(function, environment);

        Assert.Equal(7d, result.AsNumber());
        Assert.True(interpreter.TryReadBinding(environment, "local", out var local));
        Assert.Equal(7d, local.AsNumber());
        Assert.False(interpreter.TryReadGlobalValue("local", out _));
    }

    [Fact]
    public void TryReadBindingReturnsFalseForMissingBinding()
    {
        var interpreter = new BytecodeInterpreter();
        var environment = new ModuleEnvironmentRecord(outerEnv: null);

        Assert.False(interpreter.TryReadBinding(environment, "missing", out var value));
        Assert.Equal(JsValueTag.Undefined, value.Tag);
    }
}
