using System.Numerics;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JitConstantFoldIdentityTests
{
    [Fact]
    public void ConstantFoldPreservesSymbolIdentity()
    {
        var symbol = JsValue.FromSymbol("same");

        Assert.True(FoldStrictEquality(symbol, symbol));
        Assert.False(FoldStrictEquality(symbol, JsValue.FromSymbol("same")));
    }

    [Fact]
    public void ConstantFoldComparesBigIntValues()
    {
        Assert.True(FoldStrictEquality(
            JsValue.FromBigInt(BigInteger.Parse("123456789012345678901234567890")),
            JsValue.FromBigInt(BigInteger.Parse("123456789012345678901234567890"))));
        Assert.False(FoldStrictEquality(JsValue.FromBigInt(BigInteger.One), JsValue.FromBigInt(BigInteger.Zero)));
    }

    [Fact]
    public void ConstantFoldPreservesObjectHandleIdentity()
    {
        var handle = ObjectHandle.FromInt64(7);

        Assert.True(FoldStrictEquality(JsValue.FromObject(handle), JsValue.FromObject(handle)));
        Assert.False(FoldStrictEquality(JsValue.FromObject(handle), JsValue.FromObject(ObjectHandle.FromInt64(8))));
    }

    private static bool FoldStrictEquality(JsValue left, JsValue right)
    {
        var function = new BytecodeFunction
        {
            RegisterCount = 3,
            Constants = new[] { left, right },
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            ParameterNames = Array.Empty<string>(),
            NestedFunctions = Array.Empty<BytecodeFunction>(),
            Instructions = new[]
            {
                new Instruction(OpCode.LoadConst, 0, 0, 0),
                new Instruction(OpCode.LoadConst, 1, 1, 0),
                new Instruction(OpCode.StrictEq, 2, 0, 1),
                new Instruction(OpCode.Return, 2, 0, 0)
            }
        };

        var compiled = Assert.IsType<JitCompiler.JitDelegate>(JitCompiler.TryCompile(function));
        return compiled(null!, null!).AsBoolean();
    }
}
