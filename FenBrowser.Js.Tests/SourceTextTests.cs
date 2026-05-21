using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SourceTextTests
{
    [Fact]
    public void LineMapTreatsCrLfAsSingleLineTerminator()
    {
        var source = new SourceText("a\r\nb\u2028c\u2029");

        Assert.Equal((1, 1), source.GetLineColumn(0));
        Assert.Equal((2, 1), source.GetLineColumn(3));
        Assert.Equal((3, 1), source.GetLineColumn(5));
        Assert.Equal((4, 1), source.GetLineColumn(source.Length));
    }

    [Fact]
    public void LineMapRejectsOutOfRangePositions()
    {
        var source = new SourceText("x");

        Assert.Throws<ArgumentOutOfRangeException>(() => source.GetLineColumn(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.GetLineColumn(2));
    }
}
