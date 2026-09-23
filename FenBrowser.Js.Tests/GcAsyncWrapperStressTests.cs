using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// The WebDriver execute/async wrapper - an async function created and immediately
/// .call()ed, its promise .catch()ed - must keep the function it just created alive
/// through a collection after every allocation.
/// </summary>
public sealed class GcAsyncWrapperStressTests
{
    [Fact]
    public void AFreshAsyncFunctionSurvivesUntilItIsCalled()
    {
        var interpreter = new BytecodeInterpreter(new JsHeap(GcStressMode.AfterEveryAlloc));
        var fn = new BytecodeCompiler().CompileScript(new SourceText(@"
            var window = globalThis;
            var out = [];
            for (var i = 0; i < 20; i++) {
                var __args = [1, 2];
                var __callback = function (result) { out.push(result); };
                __args.push(__callback);
                (async function (arguments) {
                    var cb = arguments[arguments.length - 1];
                    cb(typeof (function () {}).call);
                }).call(window, __args).catch(function (e) { out.push('error:' + e); });
            }
            out.length + ':' + out[0];
        "));
        new BytecodeVerifier().Verify(fn);

        Assert.Equal("20:function", interpreter.Execute(fn).AsString());
    }
}
