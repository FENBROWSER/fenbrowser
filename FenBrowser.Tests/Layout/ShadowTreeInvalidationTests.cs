using System.Collections.Generic;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// A change inside a shadow tree has to be picked up by the incremental recascade and
    /// the renderer, and their flags have to be cleared there too. Both walked light-tree
    /// children only, so shadow content kept the style and box it was created with:
    /// Cloudflare Turnstile's iframe stayed at 0x0 after the widget resized it.
    /// </summary>
    public sealed class ShadowTreeInvalidationTests
    {
        [Theory]
        [InlineData(ShadowRootMode.Open)]
        [InlineData(ShadowRootMode.Closed)]
        public void DirtyShadowTree_MakesItsHostARecascadeRoot(ShadowRootMode mode)
        {
            var (doc, host, box) = CreateShadowTree(mode);
            ClearAll(doc.DocumentElement, InvalidationKind.Style);

            box.SetAttribute("style", "width: 300px; height: 65px");

            var collect = typeof(CustomHtmlEngine).GetMethod(
                "CollectDirtySubtrees",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(collect);
            var dirtyRoots = new List<Element>();
            collect!.Invoke(null, new object[] { doc.DocumentElement, dirtyRoots, 0 });

            Assert.Contains(host, dirtyRoots);
        }

        [Theory]
        [InlineData(ShadowRootMode.Open)]
        [InlineData(ShadowRootMode.Closed)]
        public void ClearingLayoutFlags_ReachesShadowContent_SoTheNextChangePropagates(ShadowRootMode mode)
        {
            var (doc, host, box) = CreateShadowTree(mode);
            var renderer = new SkiaDomRenderer();

            box.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
            renderer.RecursivelyClearDirty(doc.DocumentElement, InvalidationKind.Layout | InvalidationKind.Paint);
            Assert.False(box.LayoutDirty);
            Assert.False(host.ChildLayoutDirty);

            box.MarkDirty(InvalidationKind.Layout);

            Assert.True(host.ChildLayoutDirty);
            Assert.True(doc.DocumentElement.ChildLayoutDirty);
        }

        private static (Document Doc, Element Host, Element Box) CreateShadowTree(ShadowRootMode mode)
        {
            var doc = HtmlParser.ParseDocument("<body><div id='host'></div></body>");
            var host = doc.GetElementById("host");
            var shadowRoot = host.AttachShadow(new ShadowRootInit { Mode = mode });
            var box = doc.CreateElement("div");
            shadowRoot.AppendChild(box);
            return (doc, host, box);
        }

        private static void ClearAll(Node node, InvalidationKind kind)
        {
            node.ClearDirty(kind);
            if (node is Element element && element.GetAttachedShadowRoot() is { } shadowRoot)
            {
                ClearAll(shadowRoot, kind);
            }

            foreach (var child in node.ChildNodes)
            {
                ClearAll(child, kind);
            }
        }
    }
}
