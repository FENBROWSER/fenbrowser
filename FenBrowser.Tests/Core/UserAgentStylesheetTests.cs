using System;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class UserAgentStylesheetTests
{
    [Fact]
    public async Task UnstyledAnchor_UsesStandardUnvisitedLinkColor()
    {
        var document = new HtmlParser(
            "<!doctype html><html><body><a id='reason' href='#'>Why did this happen?</a></body></html>",
            new Uri("https://example.test/challenge")).Parse();
        var anchor = Assert.IsType<Element>(document.GetElementById("reason"));

        var computed = await CssLoader.ComputeAsync(
            document.DocumentElement,
            new Uri("https://example.test/challenge"),
            _ => Task.FromResult(string.Empty),
            viewportWidth: 800,
            viewportHeight: 600);

        Assert.Equal(new SKColor(0, 0, 238), computed[anchor].ForegroundColor);
    }
}
