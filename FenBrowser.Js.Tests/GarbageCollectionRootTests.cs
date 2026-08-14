using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class GarbageCollectionRootTests
{
    [Fact]
    public void CollectionPreservesIteratorSourcesAndCollectionEntries()
    {
        var heap = new JsHeap { YoungAllocationsPerMinorGc = 0 };
        var interpreter = new BytecodeInterpreter(heap);

        Execute(
            interpreter,
            """
            var iterator = (function () {
                var source = [{ value: 7 }];
                return source.values();
            })();
            var map = new Map([[{ key: 1 }, { value: 11 }]]);
            var set = new Set([{ value: 13 }]);
            """);

        heap.CollectGarbage();

        var result = Execute(
            interpreter,
            """
            iterator.next().value.value +
                map.values().next().value.value +
                set.values().next().value.value;
            """);

        Assert.Equal(31d, result.AsNumber());
    }

    [Fact]
    public void CollectionPreservesValuesCapturedByNativeFunctions()
    {
        var heap = new JsHeap { YoungAllocationsPerMinorGc = 0 };
        var interpreter = new BytecodeInterpreter(heap);
        var captured = interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["value"] = JsValue.FromNumber(17)
        });
        var callback = interpreter.AllocateNativeFunction(
            "capturedValue",
            (_, _) => captured,
            capturedRoots: new[] { captured });
        interpreter.RegisterGlobalValue("capturedValue", callback);

        heap.CollectGarbage();

        Assert.Equal(17d, Execute(interpreter, "capturedValue().value;").AsNumber());
    }

    private static JsValue Execute(BytecodeInterpreter interpreter, string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        return interpreter.Execute(function);
    }
}
