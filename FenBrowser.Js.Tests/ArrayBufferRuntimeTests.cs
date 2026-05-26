using FenBrowser.Js.Objects;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayBufferRuntimeTests
{
    [Fact]
    public void ConstructorAllocatesBuffer()
    {
        var buf = new ArrayBufferObject(16);
        Assert.Equal(16, buf.ByteLength);
        Assert.False(buf.IsDetached);
        Assert.Equal(16, buf.Data.Length);
    }

    [Fact]
    public void DetachZeroesBuffer()
    {
        var buf = new ArrayBufferObject(8);
        buf.Data[0] = 0xFF;
        buf.Detach();
        Assert.True(buf.IsDetached);
        Assert.Equal(0, buf.ByteLength);
    }

    [Fact]
    public void CloneCreatesIndependentCopy()
    {
        var buf = new ArrayBufferObject(8);
        buf.Data[0] = 0x42;
        var clone = buf.Clone(0, 8);
        Assert.Equal(0x42, clone.Data[0]);
        clone.Data[0] = 0x99;
        Assert.Equal(0x42, buf.Data[0]);
    }
}
