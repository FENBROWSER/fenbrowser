using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// CSS Values 4 §5.1.2: <c>rem</c> is the font size of the <em>document element</em>.
/// </summary>
/// <remarks>
/// An incremental recascade is rooted at whichever subtree went dirty, and the rem
/// basis used to be taken from whatever that cascade root happened to be. So
/// re-styling any subtree redefined rem for the whole page. On youtube.com the
/// search field is <c>font-size: 1.6rem</c> under an <c>html { font-size: 10px }</c>
/// reset: each restyle made 1.6rem mean 1.6× the field's own size, so its text grew
/// by 1.6× on every click — 16px, 25.6, 40.96, 65.5 — without bound.
/// </remarks>
public sealed class RemUnitRootBasisTests
{
    private const string Markup =
        "<html><head><style>" +
        "html { font-size: 10px; }" +
        "body { font-size: 16px; }" +
        "input.search { font-size: 1.6rem; }" +
        "</style></head><body><form><input class='search' id='q'></form></body></html>";

    private static (Element Root, Element Field) Parse()
    {
        var baseUri = new Uri("https://rem.test/");
        var document = new HtmlParser(Markup, baseUri).Parse();
        var root = document.Children.OfType<Element>()
            .First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
        var field = Assert.IsType<Element>(document.GetElementById("q"));
        return (root, field);
    }

    [Fact]
    public async Task RemResolvesAgainstTheDocumentElement_NotTheEnclosingFontSize()
    {
        var (root, field) = Parse();

        var styles = await CssLoader.ComputeAsync(
            root, new Uri("https://rem.test/"), null, viewportWidth: 1280, viewportHeight: 800);

        // 1.6rem against html's 10px, not against body's 16px.
        Assert.Equal(16d, styles[field].FontSize.Value, 3);
    }

    [Fact]
    public async Task RecascadingASubtree_DoesNotRedefineTheRemBasis()
    {
        var baseUri = new Uri("https://rem.test/");
        var (root, field) = Parse();

        await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 1280, viewportHeight: 800);

        // What a click does: the field's own subtree is recascaded, so it becomes
        // the cascade root. That must not make it the rem basis.
        for (var pass = 0; pass < 4; pass++)
        {
            var subtree = await CssLoader.ComputeSubtreeAsync(
                root, field, baseUri, null, viewportWidth: 1280, viewportHeight: 800);

            Assert.Equal(16d, subtree[field].FontSize.Value, 3);
        }
    }
}
