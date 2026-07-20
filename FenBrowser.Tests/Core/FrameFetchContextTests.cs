using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FrameFetchContextTests
{
    [Fact]
    public void NestedFrameFetch_SeparatesFrameInitiatorFromTopLevelSite()
    {
        var frameDocumentUri = new Uri("https://challenge.test/widget");
        var topLevelUri = new Uri("https://parent.test/page");
        var nestedFrameUri = new Uri("https://assets.challenge.test/nested");
        var document = HtmlParser.ParseDocument(
            "<html><body><iframe id='nested'></iframe></body></html>",
            new HtmlParserOptions { BaseUri = frameDocumentUri });
        var frame = Assert.IsType<Element>(document.GetElementById("nested"));

        var context = BrowserHost.CreateFrameFetchContext(frame, nestedFrameUri, topLevelUri);

        Assert.Equal(nestedFrameUri, context.RequestUri);
        Assert.Equal(frameDocumentUri, context.InitiatorUri);
        Assert.Equal(frameDocumentUri, context.FrameDocumentUri);
        Assert.Equal(topLevelUri, context.TopLevelDocumentUri);
        Assert.Equal("iframe", context.Destination);
        Assert.Equal("navigate", context.Mode);
        Assert.False(context.IsTopLevelNavigation);
        Assert.False(context.IsUserInitiated);
    }
}
