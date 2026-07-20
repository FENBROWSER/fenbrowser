using System.Reflection;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Core;

public sealed class CssMediaCalcQueryTests
{
    [Fact]
    public void CalcLengthUsesResolvedBreakpoint()
    {
        const string compact = "screen and (max-width:calc(1120px - 1px))";
        const string wide = "screen and (min-width:calc(1120px - 1px))";

        Assert.False(EvaluateMediaQuery(compact, 1280));
        Assert.True(EvaluateMediaQuery(compact, 1119));
        Assert.True(EvaluateMediaQuery(wide, 1119));
        Assert.False(EvaluateMediaQuery(wide, 1118));
    }

    private static bool EvaluateMediaQuery(string query, double viewportWidth)
    {
        var method = typeof(CssLoader).GetMethod(
            "EvaluateMediaQuery",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, new object[] { query, viewportWidth });
        return result is bool matches && matches;
    }
}
