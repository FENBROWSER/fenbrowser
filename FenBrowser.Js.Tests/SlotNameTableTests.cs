using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SlotNameTableTests
{
    private static BytecodeFunction MakeFunction(IReadOnlyDictionary<string, int> slots)
    {
        return new BytecodeFunction
        {
            RegisterCount = 1,
            Constants = Array.Empty<JsValue>(),
            VariableSlots = slots,
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            Instructions = new[] { new Instruction(OpCode.Return, 0, 0, 0) }
        };
    }

    [Fact]
    public void ReturnsNameForKnownSlot()
    {
        var fn = MakeFunction(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["x"] = 0,
            ["y"] = 1,
            ["z"] = 2,
        });

        Assert.Equal("x", SlotNameTable.GetName(fn, 0));
        Assert.Equal("y", SlotNameTable.GetName(fn, 1));
        Assert.Equal("z", SlotNameTable.GetName(fn, 2));
    }

    [Fact]
    public void ReturnsNullForOutOfRangeSlot()
    {
        var fn = MakeFunction(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["only"] = 0,
        });

        Assert.Null(SlotNameTable.GetName(fn, 1));
        Assert.Null(SlotNameTable.GetName(fn, 99));
        Assert.Null(SlotNameTable.GetName(fn, -1));
    }

    [Fact]
    public void ReturnsNullForFunctionWithNoSlots()
    {
        var fn = MakeFunction(new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.Null(SlotNameTable.GetName(fn, 0));
    }

    [Fact]
    public void HandlesSparseSlotIndices()
    {
        var fn = MakeFunction(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["a"] = 0,
            ["c"] = 5,
        });

        Assert.Equal("a", SlotNameTable.GetName(fn, 0));
        Assert.Null(SlotNameTable.GetName(fn, 1));
        Assert.Null(SlotNameTable.GetName(fn, 2));
        Assert.Null(SlotNameTable.GetName(fn, 3));
        Assert.Null(SlotNameTable.GetName(fn, 4));
        Assert.Equal("c", SlotNameTable.GetName(fn, 5));
        Assert.Null(SlotNameTable.GetName(fn, 6));
    }

    [Fact]
    public void CachesResultPerFunction()
    {
        var fn = MakeFunction(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["k"] = 0,
        });

        var first = SlotNameTable.GetName(fn, 0);
        var second = SlotNameTable.GetName(fn, 0);

        Assert.Equal("k", first);
        Assert.Equal("k", second);
        Assert.Same(first, second);
    }

    [Fact]
    public void NullFunctionThrows()
    {
        Assert.Throws<ArgumentNullException>(() => SlotNameTable.GetName(null!, 0));
    }
}
