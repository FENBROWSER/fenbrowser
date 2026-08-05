using System;
using System.Linq;
using FenBrowser.Wasm;
using Xunit;

namespace FenBrowser.Tests.Wasm;

public sealed class WasmEngineTests
{
    // (module (func (export "add") (param i32 i32) (result i32) local.get 0 local.get 1 i32.add))
    private static readonly byte[] AddModule = HexToBytes(
        "0061736d01000000" +   // \0asm version 1
        "01070160027f7f017f" + // type section: (i32,i32)->i32
        "03020100" +           // function section: 1 func of type 0
        "070701036164640000" + // export section: "add" -> func 0
        "0a09010700200020016a0b"); // code section: local.get 0, local.get 1, i32.add, end

    // Same module plus an exported linear memory of 1 page (64 KiB).
    private static readonly byte[] AddWithMemoryModule = HexToBytes(
        "0061736d01000000" +       // \0asm version 1
        "01070160027f7f017f" +     // type section: (i32,i32)->i32
        "03020100" +               // function section: 1 func of type 0
        "0503010001" +             // memory section: 1 memory, min 1 page
        "071002036164640000" +     // export section: 2 exports
        "066d656d6f72790200" +     //   "add" -> func 0, "memory" -> memory 0
        "0a09010700200020016a0b"); // code section: local.get 0, local.get 1, i32.add, end

    private static byte[] HexToBytes(string hex)
    {
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return result;
    }

    [Fact]
    public void CompileAndInstantiate_SimpleModule_Works()
    {
        using var engine = new WasmEngine();
        using var module = engine.Compile(AddModule, "add-test");
        using var instance = module.Instantiate("add-instance");

        var result = instance.Invoke("add", 2, 3);
        Assert.True(result.Success, result.Error);
        Assert.True(result.FuelConsumed > 0, "Expected fuel to be consumed");
    }

    [Fact]
    public void InstanceLimit_IsEnforced()
    {
        var limits = new WasmResourceLimits
        {
            MaxInstances = 2
        };

        using var engine = new WasmEngine(limits);
        using var module = engine.Compile(AddModule, "limit-test");

        var instance1 = module.Instantiate("i1");
        var instance2 = module.Instantiate("i2");

        try
        {
            Assert.Throws<InvalidOperationException>(() => module.Instantiate("i3"));
        }
        finally
        {
            instance1.Dispose();
            instance2.Dispose();
        }
    }

    [Fact]
    public void MissingFunction_ReturnsFailure()
    {
        using var engine = new WasmEngine();
        using var module = engine.Compile(AddModule, "missing-fn");
        using var instance = module.Instantiate("missing-fn-instance");

        var result = instance.Invoke("does_not_exist", 1, 2);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void MemoryReadWrite_RoundTrips()
    {
        using var engine = new WasmEngine();
        using var module = engine.Compile(AddWithMemoryModule, "mem-test");
        using var instance = module.Instantiate("mem-instance");

        var data = new byte[] { 1, 2, 3, 4, 5 };
        bool written = instance.WriteMemory("memory", 16, data);
        Assert.True(written);

        var read = instance.ReadMemory("memory", 16, 5);
        Assert.NotNull(read);
        Assert.Equal(data, read);
    }
}