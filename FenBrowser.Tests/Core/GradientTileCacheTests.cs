using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// A gradient background is rendered into a tile once and shared by later paint
    /// tree builds; rebuilding the tile every frame was ~85ms of each github.com rebuild.
    /// </summary>
    public class GradientTileCacheTests
    {
        private static SKBitmap BuildTile(string backgroundImage, int width, int height)
        {
            var element = new Element("div");
            var styles = new Dictionary<Node, CssComputed>
            {
                [element] = new CssComputed { Display = "block", BackgroundImage = backgroundImage },
            };
            var boxes = new Dictionary<Node, BoxModel> { [element] = BoxModel.FromContentBox(0, 0, width, height) };
            var tree = NewPaintTreeBuilder.Build(element, boxes, styles, width, height, null);

            var nodes = new List<PaintNodeBase>();
            var stack = new Stack<PaintNodeBase>(tree.Roots);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                nodes.Add(node);
                foreach (var child in node.Children) stack.Push(child);
            }

            return Assert.Single(nodes.OfType<ImagePaintNode>(), n => n.IsBackgroundImage && n.Bitmap != null).Bitmap;
        }

        [Fact]
        public void SameGradientAndTileSize_ReusesOneImmutableTile()
        {
            const string gradient = "linear-gradient(to right, rgb(1, 2, 3), rgb(200, 100, 50))";
            var first = BuildTile(gradient, 120, 40);
            var second = BuildTile(gradient, 120, 40);

            Assert.Same(first, second);
            Assert.True(first.IsImmutable);
        }

        [Fact]
        public void DifferentTileSize_GetsItsOwnTile()
        {
            const string gradient = "linear-gradient(to right, rgb(4, 5, 6), rgb(200, 100, 50))";
            var small = BuildTile(gradient, 120, 40);
            var large = BuildTile(gradient, 240, 40);

            Assert.NotSame(small, large);
            Assert.Equal(240, large.Width);
        }
    }
}
