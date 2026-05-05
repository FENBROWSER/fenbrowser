using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class HitTesterContractTests
    {
        [Fact]
        public void HitTest_UsesFrontMostPaintedElement()
        {
            var back = new Element("div");
            var front = new Element("div");
            var ctx = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = back,
                        Bounds = new SKRect(0, 0, 100, 100),
                        Color = SKColors.Red
                    },
                    new BackgroundPaintNode
                    {
                        SourceNode = front,
                        Bounds = new SKRect(0, 0, 100, 100),
                        Color = SKColors.Blue
                    }
                }
            };

            var hit = HitTester.HitTest(ctx, 10, 10);
            Assert.Same(front, hit);
        }

        [Fact]
        public void HitTest_SkipsPointerEventsNoneOverlay()
        {
            var back = new Element("div");
            var overlay = new Element("div");
            overlay.SetComputedStyle(new CssComputed { PointerEvents = "none" });

            var ctx = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = back,
                        Bounds = new SKRect(0, 0, 100, 100),
                        Color = SKColors.Red
                    },
                    new BackgroundPaintNode
                    {
                        SourceNode = overlay,
                        Bounds = new SKRect(0, 0, 100, 100),
                        Color = SKColors.Blue
                    }
                }
            };

            var hit = HitTester.HitTest(ctx, 10, 10);
            Assert.Same(back, hit);
        }

        [Fact]
        public void HitTest_AppliesScrollOffsetWhenTestingChildren()
        {
            var scroller = new Element("div");
            var child = new Element("div");

            var ctx = new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new ScrollPaintNode
                    {
                        SourceNode = scroller,
                        Bounds = new SKRect(0, 0, 100, 100),
                        ScrollX = 0,
                        ScrollY = 40,
                        Children = new List<PaintNodeBase>
                        {
                            new BackgroundPaintNode
                            {
                                SourceNode = child,
                                Bounds = new SKRect(0, 50, 100, 80),
                                Color = SKColors.Green
                            }
                        }
                    }
                }
            };

            var hit = HitTester.HitTest(ctx, 10, 20);
            Assert.Same(child, hit);
        }
    }
}
