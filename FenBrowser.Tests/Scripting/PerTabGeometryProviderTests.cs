using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Element geometry comes from the tab whose document holds the element: every
/// tab registers its own provider, and disposing one tab leaves the others.
/// </summary>
[Collection("Engine Tests")]
public sealed class PerTabGeometryProviderTests
{
    [Fact]
    public void EachTabAnswersForItsOwnElementsAndDisposalIsPerTab()
    {
        var tabA = new object();
        var tabB = new object();
        var docA = new Document();
        var docB = new Document();
        var a = docA.CreateElement("div");
        var b = docB.CreateElement("div");
        docA.AppendChild(a);
        docB.AppendChild(b);
        try
        {
            JavaScriptEngine.SetVisualRectProvider(tabA, () => docA, _ => new SKRect(1, 1, 11, 11));
            JavaScriptEngine.SetVisualRectProvider(tabB, () => docB, _ => new SKRect(2, 2, 22, 22));

            Assert.True(JavaScriptEngine.TryGetVisualRect(a, out var ax, out _, out var aw, out _));
            Assert.Equal((1d, 10d), (ax, aw));
            Assert.True(JavaScriptEngine.TryGetVisualRect(b, out var bx, out _, out var bw, out _));
            Assert.Equal((2d, 20d), (bx, bw));

            JavaScriptEngine.RemoveProviders(tabB);

            Assert.True(JavaScriptEngine.TryGetVisualRect(a, out ax, out _, out _, out _));
            Assert.Equal(1d, ax);
        }
        finally
        {
            JavaScriptEngine.RemoveProviders(tabA);
            JavaScriptEngine.RemoveProviders(tabB);
        }
    }
}
