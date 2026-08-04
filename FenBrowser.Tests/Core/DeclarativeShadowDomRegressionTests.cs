using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class DeclarativeShadowDomRegressionTests
    {
        [Fact]
        public async Task RenderAsync_MovesAllTemplateNodesIntoShadowRoot()
        {
            const string html = @"<!DOCTYPE html>
<html>
<body>
  <mdn-dropdown id='host'>
    <template shadowrootmode='open'>shadow text<slot name='button'></slot></template>
    <button slot='button'>HTML</button>
  </mdn-dropdown>
</body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = false
            };

            await engine.RenderAsync(
                html,
                new Uri("https://shadow.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800);

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            var host = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.Id, "host", StringComparison.Ordinal));
            var shadow = Assert.IsType<ShadowRoot>(host.ShadowRoot);
            var shadowButton = host.ChildNodes.OfType<Element>()
                .First(e => string.Equals(e.TagName, "BUTTON", StringComparison.OrdinalIgnoreCase));

            Assert.Contains(shadow.ChildNodes, node => node is Text text && text.Data.Contains("shadow text", StringComparison.Ordinal));
            Assert.Contains(shadow.ChildNodes.OfType<Element>(), element => string.Equals(element.TagName, "SLOT", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(host.ChildNodes.OfType<Element>(), element => string.Equals(element.TagName, "TEMPLATE", StringComparison.OrdinalIgnoreCase));
            var rootBox = new BoxTreeBuilder(engine.LastComputedStyles).Build(root);
            Assert.True(ContainsSourceNode(rootBox, shadowButton), "Expected the composed shadow tree to generate a box for the slotted button.");
        }

        [Fact]
        public async Task RenderAsync_ShadowButtonInvalidBackgroundAndNoBorder_SuppressUaPaintFallbacks()
        {
            const string html = @"<!DOCTYPE html>
<html>
<head>
  <style>.action { background-color: #ff0000; height: 10px; }</style>
</head>
<body>
  <mdn-search id='host'>
    <template shadowrootmode='open'>
      <style>
        .action {
          background-color: var(--missing-background-token);
          border: none;
          display: flex;
          height: 34px;
          width: 80px;
        }
      </style>
      <button class='action'>Search</button>
    </template>
  </mdn-search>
  <button id='outside' class='action'>Outside</button>
</body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = false
            };

            await engine.RenderAsync(
                html,
                new Uri("https://shadow.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 800,
                viewportHeight: 600);

            await engine.RecascadeAsync();

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            var host = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.Id, "host", StringComparison.Ordinal));
            var button = host.ShadowRoot.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "BUTTON", StringComparison.OrdinalIgnoreCase));
            var outsideButton = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.Id, "outside", StringComparison.Ordinal));

            Assert.True(engine.LastComputedStyles.TryGetValue(button, out var style));
            Assert.True(engine.LastComputedStyles.TryGetValue(outsideButton, out var outsideStyle));
            Assert.Equal("flex", style.Display);
            Assert.Equal(34d, style.Height);
            Assert.Equal("transparent", style.Map["background-color"]);
            Assert.Equal("none", style.Map["border-top-style"]);
            Assert.Equal(new SKColor(0xff, 0x00, 0x00), outsideStyle.BackgroundColor);
            Assert.Equal(10d, outsideStyle.Height);
            Assert.NotEqual("flex", outsideStyle.Display);
            Assert.All(new[]
            {
                style.BorderThickness.Top,
                style.BorderThickness.Right,
                style.BorderThickness.Bottom,
                style.BorderThickness.Left
            }, width => Assert.Equal(0d, width));

            var computer = new LayoutEngineComputer(engine.LastComputedStyles, 800, 600);
            computer.Measure(root, new SKSize(800, 600));
            computer.Arrange(root, new SKRect(0, 0, 800, 600));
            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var paintTree = NewPaintTreeBuilder.Build(
                root,
                new Dictionary<Node, BoxModel>(boxes),
                engine.LastComputedStyles,
                800,
                600,
                null);
            var paintNodes = Flatten(paintTree.Roots);

            Assert.DoesNotContain(paintNodes.OfType<BackgroundPaintNode>(), node => ReferenceEquals(node.SourceNode, button));
            Assert.DoesNotContain(paintNodes.OfType<BorderPaintNode>(), node => ReferenceEquals(node.SourceNode, button));
        }

        [Fact]
        public async Task HostFunctionalSelector_CrossesShadowBoundaryForDescendantCombinator()
        {
            const string html = @"<!DOCTYPE html>
<html>
<body>
  <mdn-dropdown id='host'>
    <template shadowrootmode='open'>
      <style>
        :host(:not([loaded],:focus-within)) slot[name='dropdown'] { display: none; }
      </style>
      <slot name='dropdown'></slot>
    </template>
    <div slot='dropdown'>Menu</div>
  </mdn-dropdown>
</body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = false
            };

            await engine.RenderAsync(
                html,
                new Uri("https://shadow.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 800,
                viewportHeight: 600);

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            var host = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.Id, "host", StringComparison.Ordinal));
            var slot = host.ShadowRoot.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "SLOT", StringComparison.OrdinalIgnoreCase));

            Assert.True(engine.LastComputedStyles.TryGetValue(slot, out var hiddenStyle));
            Assert.Equal("none", hiddenStyle.Display);

            host.SetAttribute("loaded", string.Empty);
            await engine.RecascadeAsync();

            Assert.True(engine.LastComputedStyles.TryGetValue(slot, out var loadedStyle));
            Assert.NotEqual("none", loadedStyle.Display);
        }

        private static bool ContainsSourceNode(LayoutBox box, Node node)
        {
            if (box == null)
            {
                return false;
            }

            if (ReferenceEquals(box.SourceNode, node))
            {
                return true;
            }

            return box.Children.Any(child => ContainsSourceNode(child, node));
        }

        private static List<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> nodes)
        {
            var result = new List<PaintNodeBase>();
            if (nodes == null)
            {
                return result;
            }

            foreach (var node in nodes)
            {
                result.Add(node);
                result.AddRange(Flatten(node.Children));
            }

            return result;
        }
    }
}
