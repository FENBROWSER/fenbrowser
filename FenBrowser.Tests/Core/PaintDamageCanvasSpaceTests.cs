using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// Damage is where the change is drawn: a node's paint extent mapped through its
    /// ancestors' transforms and offsets. Raw node bounds put it where the layout box
    /// is, which a retained raster then trusts, leaving stale pixels where it is drawn.
    /// </summary>
    public class PaintDamageCanvasSpaceTests
    {
        private static readonly SKRect Viewport = new(0, 0, 2000, 2000);

        private static ImmutablePaintTree Tree(Element parent, Element child, SKMatrix? transform, SKColor color) =>
            new(new List<PaintNodeBase>
            {
                new StackingContextPaintNode
                {
                    SourceNode = parent,
                    Bounds = new SKRect(0, 0, 100, 100),
                    Transform = transform,
                    Children = new List<PaintNodeBase>
                    {
                        new BackgroundPaintNode { SourceNode = child, Bounds = new SKRect(10, 10, 30, 30), Color = color }
                    }
                }
            });

        [Fact]
        public void ChangeUnderATranslatedParent_IsDamagedWhereItIsDrawn()
        {
            var parent = new Element("div");
            var child = new Element("span");
            var move = SKMatrix.CreateTranslation(1000, 500);

            var damage = new PaintDamageTracker().ComputeDamageRegions(
                Tree(parent, child, move, SKColors.Red),
                Tree(parent, child, move, SKColors.Blue),
                Viewport);

            var region = Assert.Single(damage);
            Assert.True(region.Contains(new SKRect(1010, 510, 1030, 530)), $"damage {region} misses the drawn box");
            Assert.False(region.IntersectsWith(new SKRect(10, 10, 30, 30)), $"damage {region} is at the layout box");
        }

        [Fact]
        public void ChangeUnderARotatedParent_CoversTheRotatedBox()
        {
            var parent = new Element("div");
            var child = new Element("span");
            var rotate = SKMatrix.CreateRotationDegrees(45, 50, 50).PostConcat(SKMatrix.CreateTranslation(600, 600));

            var damage = new PaintDamageTracker().ComputeDamageRegions(
                Tree(parent, child, rotate, SKColors.Red),
                Tree(parent, child, rotate, SKColors.Blue),
                Viewport);

            var drawn = rotate.MapRect(new SKRect(10, 10, 30, 30));
            var region = Assert.Single(damage);
            Assert.True(region.Contains(drawn), $"damage {region} misses the drawn box {drawn}");
        }

        [Fact]
        public void IdenticalTrees_HaveNoDamage()
        {
            var parent = new Element("div");
            var child = new Element("span");
            var damage = new PaintDamageTracker().ComputeDamageRegions(
                Tree(parent, child, null, SKColors.Red),
                Tree(parent, child, null, SKColors.Red),
                Viewport);

            Assert.Empty(damage);
        }
    }
}
