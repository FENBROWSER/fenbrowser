using System.Reflection;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Core;

public sealed class CssCalcExpressionTests
{
    [Theory]
    [InlineData("calc((100px - 20px) / 2)", 40)]
    [InlineData("calc(10px + (2px * 3))", 16)]
    [InlineData("calc(-(10px + 2px) + 20px)", 8)]
    [InlineData("calc(calc(10px + 2px) * 3)", 36)]
    public void ParenthesizedExpressionsRespectGrouping(string expression, double expected)
    {
        Assert.Equal(expected, ParseCalc(expression), 6);
    }

    [Fact]
    public void RemUsesTheActiveDocumentRootFontSize()
    {
        var setRootFontSize = typeof(CssLoader).GetMethod(
            "SetRootFontSize",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(setRootFontSize);

        setRootFontSize.Invoke(null, new object[] { 20d });
        try
        {
            Assert.Equal(30d, ParseCalc("calc(1.5rem)"), 6);
        }
        finally
        {
            setRootFontSize.Invoke(null, new object[] { 16d });
        }
    }

    private static double ParseCalc(string expression)
    {
        var method = typeof(CssLoader).GetMethod(
            "TryParseCalc",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] arguments = { expression, 0d, 16d, 200d };
        var parsed = method.Invoke(null, arguments);

        Assert.True(parsed is true, $"Expected '{expression}' to parse.");
        return Assert.IsType<double>(arguments[1]);
    }
}
