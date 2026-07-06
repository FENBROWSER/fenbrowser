using System.Globalization;
using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class CssAnimationEngineFrameTests
{
    [Fact]
    public void ApplyKeyframeAt_TreatsProgressAsPercent()
    {
        var engine = new CssAnimationEngine();
        var animation = CreateOpacityAnimation();

        InvokeApplyKeyframeAt(engine, animation, 50d);

        Assert.True(animation.ComputedProperties.TryGetValue("opacity", out var opacity));
        Assert.Equal(0.5d, double.Parse(opacity, CultureInfo.InvariantCulture), 6);
        engine.Stop();
    }

    [Fact]
    public void ApplyAnimationFrame_SuppressesUnchangedComputedProperties()
    {
        var engine = new CssAnimationEngine();
        var element = new Element("div");
        var style = new CssComputed();
        element.SetComputedStyle(style);
        var animation = CreateOpacityAnimation();

        var first = InvokeApplyAnimationFrame(engine, element, animation, 100d);
        var second = InvokeApplyAnimationFrame(engine, element, animation, 100d);

        Assert.True(first);
        Assert.False(second);
        Assert.NotNull(style.AnimationOverlay);
        Assert.Equal("1", style.AnimationOverlay["opacity"]);
        engine.Stop();
    }

    private static CssAnimationEngine.ActiveAnimation CreateOpacityAnimation()
    {
        return new CssAnimationEngine.ActiveAnimation
        {
            Keyframes = new CssLoader.CssKeyframes
            {
                Name = "fade",
                Frames =
                {
                    new CssLoader.CssKeyframe
                    {
                        Percentage = 0,
                        Properties = { ["opacity"] = "0" }
                    },
                    new CssLoader.CssKeyframe
                    {
                        Percentage = 100,
                        Properties = { ["opacity"] = "1" }
                    }
                }
            }
        };
    }

    private static void InvokeApplyKeyframeAt(
        CssAnimationEngine engine,
        CssAnimationEngine.ActiveAnimation animation,
        double progressPercent)
    {
        var method = typeof(CssAnimationEngine).GetMethod(
            "ApplyKeyframeAt",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        method.Invoke(engine, new object[] { animation, progressPercent });
    }

    private static bool InvokeApplyAnimationFrame(
        CssAnimationEngine engine,
        Element element,
        CssAnimationEngine.ActiveAnimation animation,
        double progressPercent)
    {
        var method = typeof(CssAnimationEngine).GetMethod(
            "ApplyAnimationFrame",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method.Invoke(engine, new object[] { element, animation, progressPercent });
        Assert.NotNull(result);
        return (bool)result;
    }
}
