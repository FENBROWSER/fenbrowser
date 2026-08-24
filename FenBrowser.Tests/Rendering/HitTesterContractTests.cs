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

        [Fact]
        public void HitTest_IgnoresPaintNodeOutsideSourceLayoutBox()
        {
            var document = new Document();
            var back = new Element("iframe", document);
            var falseFront = new Element("div", document);
            var ctx = new RenderContext
            {
                Boxes = new Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel>
                {
                    [falseFront] = FenBrowser.FenEngine.Layout.BoxModel.FromContentBox(0, 120, 100, 40)
                },
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
                        SourceNode = falseFront,
                        Bounds = new SKRect(0, 0, 100, 100),
                        Color = SKColors.Blue
                    }
                }
            };

            var hit = HitTester.HitTest(ctx, 10, 10);
            Assert.Same(back, hit);
        }

        [Fact]
        public void HitTest_ProjectsTitleFromHitAncestorIntoTooltip()
        {
            var button = new Element("button");
            button.SetAttribute("title", "Open settings");
            var icon = new Element("span");
            button.AppendChild(icon);

            Assert.True(HitTester.HitTest(ContextFor(icon), 10, 10, out var hit));
            Assert.Equal("Open settings", hit.Tooltip);
        }

        [Fact]
        public void HitTest_UsesAriaLabelForInteractiveControlTooltip()
        {
            var button = new Element("button");
            button.SetAttribute("aria-label", "Start voice search");

            Assert.True(HitTester.HitTest(ContextFor(button), 10, 10, out var hit));
            Assert.Equal("Start voice search", hit.Tooltip);
        }

        [Fact]
        public void HitTest_BoundsUntrustedTooltipText()
        {
            var button = new Element("button");
            button.SetAttribute("title", new string('x', 2_000));

            Assert.True(HitTester.HitTest(ContextFor(button), 10, 10, out var hit));
            Assert.Equal(512, hit.Tooltip.Length);
        }

        private static RenderContext ContextFor(Element element)
        {
            return new RenderContext
            {
                PaintTreeRoots = new List<PaintNodeBase>
                {
                    new BackgroundPaintNode
                    {
                        SourceNode = element,
                        Bounds = new SKRect(0, 0, 100, 100),
                        Color = SKColors.Transparent
                    }
                }
            };
        }
    }
}
