using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AssignmentTargetRuntimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void CallExpressionEqualsAssignmentThrowsReferenceErrorAfterLhsSideEffects()
    {
        var result = Run("""
            var fCalled = false;
            var fValueOfCalled = false;
            function f() {
              fCalled = true;
              return { valueOf: function() { fValueOfCalled = true; return 1; } };
            }
            var gCalled = false;
            function g() {
              gCalled = true;
              return 1;
            }

            var threw = false;
            try {
              f() = g();
            } catch (e) {
              threw = e && e.name === "ReferenceError";
            }

            threw && fCalled && !fValueOfCalled && !gCalled;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CallExpressionPrefixUpdateThrowsReferenceErrorAfterLhsSideEffects()
    {
        var result = Run("""
            var fCalled = false;
            var fValueOfCalled = false;
            function f() {
              fCalled = true;
              return { valueOf: function() { fValueOfCalled = true; return 1; } };
            }

            var threw = false;
            try {
              ++f();
            } catch (e) {
              threw = e && e.name === "ReferenceError";
            }

            threw && fCalled && !fValueOfCalled;
            """);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CallExpressionForInAndForOfTargetsThrowReferenceError()
    {
        var result = Run("""
            function async() {}
            var threwForIn = false;
            var threwForOf = false;
            try {
              for (async() in [1]) {}
            } catch (e) {
              threwForIn = ("" + e).indexOf("ReferenceError") === 0;
            }
            try {
              for (async() of [1]) {}
            } catch (e) {
              threwForOf = ("" + e).indexOf("ReferenceError") === 0;
            }
            threwForIn && threwForOf;
            """);

        Assert.True(result.AsBoolean());
    }
}
