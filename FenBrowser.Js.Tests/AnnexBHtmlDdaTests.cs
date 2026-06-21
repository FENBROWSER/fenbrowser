using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AnnexBHtmlDdaTests
{
    [Fact]
    public void HtmlDdaUsesAnnexBBooleanEqualityAndTypeOfSemantics()
    {
        var interpreter = new BytecodeInterpreter(new JsHeap());
        var htmlDda = interpreter.AllocateNativeFunction("IsHTMLDDA", (_, _) => JsValue.Null);
        interpreter.MarkAsHtmlDda(htmlDda);
        interpreter.RegisterGlobalValue("htmlDda", htmlDda);

        var source = new SourceText(@"
            var prototypeGets = 0;
            Object.defineProperty(htmlDda, 'prototype', {
                get: function () { prototypeGets += 1; }
            });
            var replaceGets = 0;
            Object.defineProperty(htmlDda, Symbol.replace, {
                get: function () {
                    replaceGets += 1;
                    return htmlDda;
                }
            });
            var heritageThrew = false;
            try {
                class C extends htmlDda {}
            } catch (error) {
                heritageThrew = error instanceof TypeError;
            }
            var checks = [
                !htmlDda,
                htmlDda == null,
                htmlDda == undefined,
                htmlDda !== null,
                typeof htmlDda === 'undefined',
                (htmlDda || 2) === 2,
                (htmlDda && 2) === htmlDda,
                heritageThrew,
                prototypeGets === 0,
                ''.replace(htmlDda) === null,
                ''.replaceAll(htmlDda) === null,
                replaceGets === 2
            ];
            checks.every(function (value) { return value; });
        ", "annex-b-html-dda.js");
        var function = new BytecodeCompiler().CompileScript(source);

        Assert.True(interpreter.Execute(function).AsBoolean());
    }
}
