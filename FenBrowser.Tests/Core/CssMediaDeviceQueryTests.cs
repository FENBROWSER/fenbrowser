using System.Reflection;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Media Queries 4 §4.4: device-width/device-height compare against the output
/// device. w3schools' codemirror.css switches to a phone font under
/// `only screen and (max-device-width: 480px)`, which matched on the desktop.
/// </summary>
public sealed class CssMediaDeviceQueryTests
{
    [Fact]
    public void DeviceWidth_IsComparedAgainstTheDevice()
    {
        Assert.False(EvaluateMediaQuery("only screen and (max-device-width: 480px)", 1280));
        Assert.True(EvaluateMediaQuery("only screen and (max-device-width: 480px)", 400));
        Assert.True(EvaluateMediaQuery("(min-device-width: 1000px)", 1280));
        Assert.False(EvaluateMediaQuery("(min-device-width: 1000px)", 800));
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
