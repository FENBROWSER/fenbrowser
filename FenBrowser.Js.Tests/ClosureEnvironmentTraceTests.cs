using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ClosureEnvironmentTraceTests
{
    [Fact]
    public void FunctionTraceKeepsCapturedEnvironmentObjectsAlive()
    {
        var heap = new JsHeap();
        var capturedHandle = heap.AllocateObject(new JsObject(), AllocationSite.Current());

        var outerEnv = new DeclarativeEnvironmentRecord(outerEnv: null);
        Assert.Equal(BindingOpResult.Ok, outerEnv.CreateMutableBinding("captured", deletable: false));
        Assert.Equal(BindingOpResult.Ok, outerEnv.InitializeBinding("captured", JsValue.FromObject(capturedHandle)));
        var functionEnv = new DeclarativeEnvironmentRecord(outerEnv);

        var function = new BytecodeFunction
        {
            Instructions = Array.Empty<Instruction>(),
            Constants = Array.Empty<JsValue>(),
            VariableSlots = new Dictionary<string, int>(StringComparer.Ordinal),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            RegisterCount = 1
        };

        var functionHandle = heap.AllocateObject(new JsFunctionObject(function, functionEnv), AllocationSite.Current());
        heap.PushRoot(functionHandle);

        heap.CollectGarbage();

        var exception = Record.Exception(() => heap.Validate(capturedHandle));
        Assert.Null(exception);
    }
}
