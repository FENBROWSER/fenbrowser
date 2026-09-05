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

    [Fact]
    public void ApplyKeyframeAt_SynthesizesMissingFromEndpointFromUnderlyingStyle()
    {
        var engine = new CssAnimationEngine();
        var element = new Element("span");
        element.SetComputedStyle(new CssComputed());
        var animation = new CssAnimationEngine.ActiveAnimation
        {
            Element = element,
            Keyframes = new CssLoader.CssKeyframes
            {
                Name = "spin",
                Frames =
                {
                    new CssLoader.CssKeyframe
                    {
                        Percentage = 100,
                        Properties = { ["transform"] = "rotate(360deg)" }
                    }
                }
            }
        };

        InvokeApplyKeyframeAt(engine, animation, 50d);

        Assert.True(animation.ComputedProperties.TryGetValue("transform", out var transform));
        Assert.Contains("rotate(180", transform);
        engine.Stop();
    }

    [Theory]
    [InlineData("animation-name", "spinner-spin")]
    [InlineData("animation-duration", "2.5s")]
    [InlineData("animation-timing-function", "linear")]
    [InlineData("animation-iteration-count", "infinite")]
    public void GetAnimationProperty_ParsesRecaptchaSpinnerShorthand(string property, string expected)
    {
        var engine = new CssAnimationEngine();
        var style = new CssComputed();
        style.Map["animation"] = "spinner-spin 2.5s linear infinite";

        var method = typeof(CssAnimationEngine).GetMethod(
            "GetAnimationProperty",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var actual = method.Invoke(engine, new object[] { style, property, 0 });

        Assert.Equal(expected, actual);
        engine.Stop();
    }

    // rotate() takes any angle unit. An unrecognised one used to parse as zero,
    // so a keyframe ending on rotate(3turn) drove the element backwards to
    // nothing over its final segment instead of finishing the turn - which is
    // what reCAPTCHA's spinner does between its 75% and 100% frames.
    [Theory]
    [InlineData("rotate(3turn)", 540d)]
    [InlineData("rotate(1080deg)", 540d)]
    [InlineData("rotate(1200grad)", 540d)]
    [InlineData("rotate(18.8495559rad)", 540d)]
    public void InterpolateTransform_ReadsEveryAngleUnit(string to, double expectedDegreesAtHalfway)
    {
        var engine = new CssAnimationEngine();

        var interpolated = InvokeInterpolateValue(engine, "transform", "rotate(0deg)", to, 0.5d);

        Assert.Equal(expectedDegreesAtHalfway, ReadRotationDegrees(interpolated), 3);
        engine.Stop();
    }

    // The spinner's last segment: 810deg to a full three turns must keep winding
    // forward, so the halfway point sits between them rather than back near zero.
    [Fact]
    public void InterpolateTransform_SpinnerFinalSegmentKeepsWindingForward()
    {
        var engine = new CssAnimationEngine();

        var interpolated = InvokeInterpolateValue(engine, "transform", "rotate(810deg)", "rotate(3turn)", 0.5d);

        Assert.Equal(945d, ReadRotationDegrees(interpolated), 3);
        engine.Stop();
    }

    private static string InvokeInterpolateValue(
        CssAnimationEngine engine,
        string property,
        string from,
        string to,
        double progress)
    {
        var method = typeof(CssAnimationEngine).GetMethod(
            "InterpolateValue",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsType<string>(method!.Invoke(engine, new object[] { property, from, to, progress }));
    }

    private static double ReadRotationDegrees(string transform)
    {
        var match = System.Text.RegularExpressions.Regex.Match(transform, @"rotate\(\s*([-\d.]+)deg\s*\)");
        Assert.True(match.Success, $"no rotate() in '{transform}'");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
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
