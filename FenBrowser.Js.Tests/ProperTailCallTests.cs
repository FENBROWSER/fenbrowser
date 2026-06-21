using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class ProperTailCallTests
{
    [Fact]
    public void StrictDirectTailCallDoesNotGrowInterpreterCallStack()
    {
        var source = new SourceText(@"
            'use strict';
            var count = 0;
            (function recur(n) {
                if (n === 0) { count++; return; }
                switch (0) { case 0: return recur(n - 1); }
            }(5000));
            count;
        ");
        var function = new BytecodeCompiler().CompileScript(source);
        new BytecodeVerifier().Verify(function);

        Assert.Equal(1d, new BytecodeInterpreter().Execute(function).AsNumber());
    }
}
