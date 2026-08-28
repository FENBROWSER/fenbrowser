using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class HtmlSerializationRawTextTests
{
    [Fact]
    public void HtmlSerializationPreservesRawTextButEscapesNormalText()
    {
        const string scriptSource = "if (a < b && c > d) x &= y;";
        const string styleSource = ".x > .y { content: \"<&>\"; }";
        var document = HtmlParser.ParseDocument(
            $"<html><body><script>{scriptSource}</script><style>{styleSource}</style><p>&lt;&amp;&gt;</p></body></html>");
        var script = document.Descendants().OfType<Element>().Single(element => element.LocalName == "script");
        var style = document.Descendants().OfType<Element>().Single(element => element.LocalName == "style");
        var paragraph = document.Descendants().OfType<Element>().Single(element => element.LocalName == "p");

        Assert.Equal(scriptSource, script.InnerHTML);
        Assert.Equal($"<script>{scriptSource}</script>", script.OuterHTML);
        Assert.Equal(scriptSource, script.FirstChild!.ToHtml());
        Assert.Equal(styleSource, style.InnerHTML);
        Assert.Equal("&lt;&amp;&gt;", paragraph.InnerHTML);

        var serialized = DomSerializer.Serialize(document, prettyPrint: false);
        Assert.Contains($"<SCRIPT>{scriptSource}</SCRIPT>", serialized);
        Assert.Contains($"<STYLE>{styleSource}</STYLE>", serialized);
        Assert.Contains("<P>&lt;&amp;&gt;</P>", serialized);
    }
}
