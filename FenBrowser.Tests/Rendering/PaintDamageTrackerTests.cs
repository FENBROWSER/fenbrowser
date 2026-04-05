using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class PaintDamageTrackerTests
    {
        [Fact]
        public void FirstFrame_ReturnsFullViewportDamage()
        {
            var tracker = new PaintDamageTracker();
            var viewport = new SKRect(0, 0, 800, 600);

            var damage = tracker.ComputeDamageRegions(previousTree: null, currentTree: ImmutablePaintTree.Empty, viewport);

            Assert.Single(damage);
            Assert.Equal(viewport, damage[0]);
        }

        [Fact]
        public void GeometryChange_ReturnsUnionOfOldAndNewBounds()
        {
            var source = new Element("div");
            var previous = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(source, new SKRect(0, 0, 10, 10)),
            });
            var current = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(source, new SKRect(20, 20, 30, 30)),
            });

            var tracker = new PaintDamageTracker();
            var damage = tracker.ComputeDamageRegions(previous, current, new SKRect(0, 0, 100, 100));

            Assert.Single(damage);
            Assert.Equal(new SKRect(0, 0, 30, 30), damage[0]);
        }

        [Fact]
        public void HoverStateChange_ReturnsLocalizedDamageForAffectedBounds()
        {
            var source = new Element("div");
            var bounds = new SKRect(20, 20, 120, 60);
            var previous = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(source, bounds, isHovered: false),
            });
            var current = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(source, bounds, isHovered: true),
            });

            var tracker = new PaintDamageTracker();
            var damage = tracker.ComputeDamageRegions(previous, current, new SKRect(0, 0, 200, 200));

            Assert.Single(damage);
            Assert.Equal(bounds, damage[0]);
        }

        [Fact]
        public void BackgroundColorChange_ReturnsLocalizedDamageForAffectedBounds()
        {
            var source = new Element("div");
            var bounds = new SKRect(24, 24, 144, 84);
            var previous = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(source, bounds, color: SKColors.White),
            });
            var current = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(source, bounds, color: new SKColor(15, 23, 42, 224)),
            });

            var tracker = new PaintDamageTracker();
            var damage = tracker.ComputeDamageRegions(previous, current, new SKRect(0, 0, 200, 200));

            Assert.Single(damage);
            Assert.Equal(bounds, damage[0]);
        }

        [Fact]
        public void ExcessiveRegions_CollapseToSingleUnionRegion()
        {
            var current = new ImmutablePaintTree(new List<PaintNodeBase>
            {
                NewBackgroundNode(new Element("div"), new SKRect(0, 0, 10, 10)),
                NewBackgroundNode(new Element("span"), new SKRect(20, 20, 30, 30)),
                NewBackgroundNode(new Element("p"), new SKRect(40, 40, 50, 50)),
            });

            var tracker = new PaintDamageTracker(maxDamageRegions: 2, mergeTolerancePx: 0);
            var damage = tracker.ComputeDamageRegions(ImmutablePaintTree.Empty, current, new SKRect(0, 0, 100, 100));

            Assert.Single(damage);
            Assert.Equal(new SKRect(0, 0, 50, 50), damage[0]);
        }

        private static BackgroundPaintNode NewBackgroundNode(Element source, SKRect bounds, bool isHovered = false, bool isFocused = false, SKColor? color = null)
        {
            return new BackgroundPaintNode
            {
                SourceNode = source,
                Bounds = bounds,
                Color = color ?? SKColors.Black,
                IsHovered = isHovered,
                IsFocused = isFocused,
                Children = new List<PaintNodeBase>()
            };
        }
    }
}
