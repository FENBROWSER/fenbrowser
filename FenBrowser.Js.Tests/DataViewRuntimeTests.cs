using FenBrowser.Js.Objects;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DataViewRuntimeTests
{
    [Fact]
    public void GetSetUint8()
    {
        var buf = new ArrayBufferObject(4);
        var view = new DataViewObject(buf, 0, 4);
        view.SetUint8(0, 0xAB);
        Assert.Equal(0xAB, view.GetUint8(0));
    }

    [Fact]
    public void GetSetInt32_LittleEndian()
    {
        var buf = new ArrayBufferObject(8);
        var view = new DataViewObject(buf, 0, 8);
        view.SetInt32(0, -12345, littleEndian: true);
        Assert.Equal(-12345, view.GetInt32(0, littleEndian: true));
    }

    [Fact]
    public void GetSetFloat64()
    {
        var buf = new ArrayBufferObject(8);
        var view = new DataViewObject(buf, 0, 8);
        view.SetFloat64(0, 3.14159, littleEndian: true);
        Assert.Equal(3.14159, view.GetFloat64(0, littleEndian: true));
    }

    [Fact]
    public void GetSetBigEndian()
    {
        var buf = new ArrayBufferObject(4);
        var view = new DataViewObject(buf, 0, 4);
        view.SetInt32(0, 0x01020304, littleEndian: false);
        // Big-endian: MSB first. 0x01020304 → bytes [01, 02, 03, 04]
        Assert.Equal(0x01, view.GetUint8(0));
        Assert.Equal(0x02, view.GetUint8(1));
        Assert.Equal(0x03, view.GetUint8(2));
        Assert.Equal(0x04, view.GetUint8(3));
    }

    [Fact]
    public void OffsetExceedsBounds_Throws()
    {
        var buf = new ArrayBufferObject(4);
        var view = new DataViewObject(buf, 0, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.GetInt32(2, true));
    }
}
