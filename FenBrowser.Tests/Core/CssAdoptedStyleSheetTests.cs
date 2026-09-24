using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// CSSOM 6.2 adoptedStyleSheets: a document's or shadow root's adopted sheets
/// cascade after its own stylesheets, in array order, and a shadow root's only
/// style its own tree.
/// </summary>
public class CssAdoptedStyleSheetTests
{
    private static (Document Doc, Element Root) Parse(string body)
    {
        var doc = new HtmlParser("<!doctype html><html><head><style>#t { color: rgb(255, 0, 0); }</style></head><body>" + body + "</body></html>").Parse();
        var root = doc.Children.OfType<Element>().First(element => element.TagName == "HTML");
        return (doc, root);
    }

    private static Element ById(Node scope, string id)
        => scope.Descendants().OfType<Element>().First(element => element.Id == id);

    [Fact]
    public async Task DocumentAdoptedSheet_CascadesAfterTheDocumentsOwnSheets()
    {
        CssLoader.ClearCaches();
        var (doc, root) = Parse("<p id='t'>x</p>");

        doc.SetAdoptedStyleSheets(new[] { new AdoptedStyleSheet("#t { color: rgb(0, 128, 0); }") });
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

        Assert.Equal("rgb(0, 128, 0)", computed[ById(doc, "t")].Map["color"]);
    }

    [Fact]
    public async Task LaterAdoptedSheetWins_AndMediaListGatesTheSheet()
    {
        CssLoader.ClearCaches();
        var (doc, root) = Parse("<p id='t'>x</p>");

        doc.SetAdoptedStyleSheets(new[]
        {
            new AdoptedStyleSheet("#t { color: rgb(0, 128, 0); }"),
            new AdoptedStyleSheet("#t { color: rgb(0, 0, 255); }"),
            new AdoptedStyleSheet("#t { color: rgb(1, 1, 1); }", mediaText: "print"),
        });
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null, viewportWidth: 800, viewportHeight: 600);

        Assert.Equal("rgb(0, 0, 255)", computed[ById(doc, "t")].Map["color"]);
    }

    [Fact]
    public async Task ShadowRootAdoptedSheet_StylesOnlyItsOwnTree()
    {
        CssLoader.ClearCaches();
        var (doc, root) = Parse("<p id='t'>x</p><div id='host'></div>");
        var shadow = ById(doc, "host").AttachShadow(new ShadowRootInit { Mode = ShadowRootMode.Open });
        var inner = doc.CreateElement("span");
        inner.Id = "inner";
        shadow.AppendChild(inner);

        shadow.SetAdoptedStyleSheets(new[] { new AdoptedStyleSheet("span, #t { color: rgb(0, 128, 0); }") });
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

        Assert.Equal("rgb(0, 128, 0)", computed[inner].Map["color"]);
        Assert.Equal("rgb(255, 0, 0)", computed[ById(doc, "t")].Map["color"]);
    }

    [Fact]
    public void SettingAdoptedSheets_InvalidatesTheDocumentsStyle()
    {
        var (doc, root) = Parse("<p id='t'>x</p>");
        root.ClearStyleDirty();

        doc.SetAdoptedStyleSheets(new[] { new AdoptedStyleSheet("p { color: blue; }") });

        Assert.True(root.StyleDirty);
    }
}
