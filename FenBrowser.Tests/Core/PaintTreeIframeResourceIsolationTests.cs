using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Interaction;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class PaintTreeIframeResourceIsolationTests
{
    [Fact]
    public void IframePlaceholderText_DoesNotReadTheSourceResource()
    {
        var document = new Document();
        var iframe = document.CreateElement("iframe");
        iframe.SetAttribute("src", "https://unreachable.example.test/frame.html");
        iframe.AppendChild(document.CreateElement("div"));

        var builder = CreateBuilder();
        var method = typeof(NewPaintTreeBuilder).GetMethod(
            "TryExtractIframeTopText",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var stopwatch = Stopwatch.StartNew();
        var result = method!.Invoke(builder, new object[] { iframe });
        stopwatch.Stop();

        Assert.Equal(string.Empty, result);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"paint-tree iframe inspection blocked for {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Null(typeof(NewPaintTreeBuilder).GetMethod(
            "TryExtractIframeTopTextFromSource",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static object CreateBuilder()
    {
        var ctor = typeof(NewPaintTreeBuilder).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(IReadOnlyDictionary<Node, BoxModel>),
                typeof(IReadOnlyDictionary<Node, CssComputed>),
                typeof(float),
                typeof(float),
                typeof(ScrollManager),
                typeof(string)
            },
            modifiers: null);

        Assert.NotNull(ctor);
        return ctor!.Invoke(new object[]
        {
            new Dictionary<Node, BoxModel>(),
            new Dictionary<Node, CssComputed>(),
            800f,
            600f,
            new ScrollManager(),
            "https://example.test/"
        });
    }
}
