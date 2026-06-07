using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class UnaryPlusRuntimeTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void UnaryPlusPrefersValueOfOverThrowingToString()
    {
        Assert.True(RunBool("""
            var object = { valueOf: function() { return 1; }, toString: function() { throw "error"; } };
            +object === 1;
            """));
    }

    [Fact]
    public void UnaryPlusHandlesFullOrdinaryToPrimitiveSequence()
    {
        Assert.True(RunBool("""
            function Test262Error(message) { this.message = message; }

            var object = {valueOf: function() {return 1}};
            if (+object !== 1) {
              throw new Test262Error('#1');
            }

            object = {valueOf: function() {return 1}, toString: function() {return 0}};
            if (+object !== 1) {
              throw new Test262Error('#2');
            }

            object = {valueOf: function() {return 1}, toString: function() {return {}}};
            if (+object !== 1) {
              throw new Test262Error('#3');
            }

            try {
              object = {valueOf: function() {return 1}, toString: function() {throw "error"}};
              if (+object !== 1) {
                throw new Test262Error('#4.1');
              }
            } catch (e) {
              if (e === "error") {
                throw new Test262Error('#4.2');
              }
            }

            object = {toString: function() {return 1}};
            if (+object !== 1) {
              throw new Test262Error('#5');
            }

            object = {valueOf: function() {return {}}, toString: function() {return 1}};
            if (+object !== 1) {
              throw new Test262Error('#6');
            }

            try {
              object = {valueOf: function() {throw "error"}, toString: function() {return 1}};
              +object;
              throw new Test262Error('#7.1');
            } catch (e) {
              if (e !== "error") {
                throw new Test262Error('#7.2');
              }
            }

            try {
              object = {valueOf: function() {return {}}, toString: function() {return {}}};
              +object;
              throw new Test262Error('#8.1');
            } catch (e) {
              if (!(e instanceof TypeError)) {
                throw new Test262Error('#8.2');
              }
            }

            true;
            """));
    }

    [Fact]
    public void UnaryMinusUsesToNumericForBigIntWrappers()
    {
        Assert.True(RunBool("""
            -Object(1n) === -1n &&
            -({
              valueOf: function() { return 1n; },
              toString: function() { throw new Error("should not reach toString"); }
            }) === -1n;
            """));
    }
}
