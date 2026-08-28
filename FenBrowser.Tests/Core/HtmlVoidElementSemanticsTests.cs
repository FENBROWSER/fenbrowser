using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class HtmlVoidElementSemanticsTests
{
    [Fact]
    public void LegacyRenderingShimDelegatesToCanonicalHtmlSemantics()
    {
        var names = new[]
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr",
            "div", "path", "circle", "unknown", ""
        };

        foreach (var name in names)
        {
            Assert.Equal(HtmlElementSemantics.IsVoid(name), LiteDomUtil.IsVoid(name));
        }
    }
}
