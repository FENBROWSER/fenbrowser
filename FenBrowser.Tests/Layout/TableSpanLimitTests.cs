using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;

namespace FenBrowser.Tests.Layout;

public sealed class TableSpanLimitTests
{
    [Theory]
    [InlineData("colspan", "2147483647", 1000)]
    [InlineData("rowspan", "2147483647", 65534)]
    [InlineData("colspan", "0", 1)]
    [InlineData("rowspan", "not-a-number", 1)]
    public void SpanAttributesAreClampedToHtmlLimits(string attribute, string rawValue, int expected)
    {
        var element = new Element("TD");
        element.SetAttribute(attribute, rawValue);

        using var store = new LayoutBoxStore(1);
        var id = store.CreateBox(element, new CssComputed(), LayoutBoxStore.BoxType.Block);
        var box = store.GetWrapper(id);

        var method = typeof(TableFormattingContext).GetMethod(
            "GetSpanAttribute",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal(expected, method.Invoke(null, new object[] { box, attribute }));
    }

    [Fact]
    public void ExtremeCombinedSpansCompleteWithBoundedGrid()
    {
        const string html = "<table><tr><td id='target' colspan='2147483647' rowspan='2147483647'>x</td></tr></table>";
        var document = new HtmlParser(html).Parse();
        var root = document.Children.OfType<Element>().First(element => element.TagName == "HTML");
        var target = document.GetElementById("target");
        Assert.NotNull(target);

        var styles = new Dictionary<Node, CssComputed>();
        AddStyles(root, styles);

        var layout = new LayoutEngineComputer(styles, 800, 600);
        layout.Measure(root, new SKSize(800, 600));
        layout.Arrange(root, new SKRect(0, 0, 800, 600));

        Assert.Contains(layout.GetAllBoxes(), entry => ReferenceEquals(entry.Key, target));
    }

    private static void AddStyles(Node node, Dictionary<Node, CssComputed> styles)
    {
        var display = node is Element element
            ? element.TagName switch
            {
                "TABLE" => "table",
                "TBODY" => "table-row-group",
                "TR" => "table-row",
                "TD" => "table-cell",
                _ => "block"
            }
            : "inline";
        styles[node] = new CssComputed { Display = display };

        foreach (var child in node.ChildNodes)
        {
            AddStyles(child, styles);
        }
    }
}
