using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Backends;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public sealed class PaintTreePillRenderingContractTests
    {
        [Fact]
        public async System.Threading.Tasks.Task VariableFallbackPillBackground_FillsBorderBoxAndCentersText()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #pill {
      background: var(--gm3-sys-color-primary,#0b57d0);
      box-sizing: border-box;
      color: #fff;
      display: inline-block;
      font-size: 14px;
      height: 40px;
      line-height: 20px;
      min-width: 85px;
      padding: 10px 12px;
      text-align: center;
    }
  </style>
</head>
<body><a id='pill'>Sign in</a></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);
            var nodes = Flatten(tree.Roots);
            var pill = doc.Descendants().OfType<Element>().First(e => e.Id == "pill");

            var background = nodes
                .OfType<BackgroundPaintNode>()
                .FirstOrDefault(n => ReferenceEquals(n.SourceNode, pill));

            Assert.NotNull(background);
            Assert.Equal(new SKColor(0x0b, 0x57, 0xd0), background.Color);
            Assert.True(boxes.TryGetValue(pill, out var pillBox));
            Assert.Equal(pillBox.BorderBox.Left, background.Bounds.Left, 1);
            Assert.Equal(pillBox.BorderBox.Top, background.Bounds.Top, 1);
            Assert.Equal(pillBox.BorderBox.Right, background.Bounds.Right, 1);
            Assert.Equal(pillBox.BorderBox.Bottom, background.Bounds.Bottom, 1);

            var text = nodes
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => string.Equals(n.FallbackText, "Sign in", StringComparison.Ordinal));

            Assert.NotNull(text);
            float pillCenterX = pillBox.ContentBox.Left + pillBox.ContentBox.Width * 0.5f;
            float textCenterX = text.Bounds.Left + text.Bounds.Width * 0.5f;
            float pillCenterY = pillBox.ContentBox.Top + pillBox.ContentBox.Height * 0.5f;
            float textCenterY = text.Bounds.Top + text.Bounds.Height * 0.5f;

            Assert.InRange(Math.Abs(textCenterX - pillCenterX), 0f, 2.5f);
            Assert.InRange(Math.Abs(textCenterY - pillCenterY), 0f, 2.5f);
        }

        [Fact]
        public async System.Threading.Tasks.Task TextAlignCenter_MixedInlineChildren_PaintsAtLaidOutRunPositions()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #languages {
      display: inline-block;
      font-size: 14px;
      line-height: 28px;
      text-align: center;
      width: 700px;
    }
    #languages a { display: inline; }
  </style>
</head>
<body>
  <div id='languages'>Google offered in: <a>Hindi</a> <a>Bangla</a></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);
            var nodes = Flatten(tree.Roots);
            var promptText = doc.Descendants()
                .OfType<Text>()
                .First(t => t.Data?.Contains("Google offered in", StringComparison.Ordinal) == true);
            var firstLinkText = doc.Descendants()
                .OfType<Text>()
                .First(t => string.Equals(t.Data, "Hindi", StringComparison.Ordinal));

            Assert.True(boxes.TryGetValue(promptText, out var promptBox));
            var promptPaint = nodes
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => ReferenceEquals(n.SourceNode, promptText));
            var linkPaint = nodes
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => ReferenceEquals(n.SourceNode, firstLinkText));

            Assert.NotNull(promptPaint);
            Assert.NotNull(linkPaint);
            Assert.InRange(Math.Abs(promptPaint.Bounds.Left - promptBox.ContentBox.Left), 0f, 2f);
            Assert.True(promptPaint.Bounds.Right <= linkPaint.Bounds.Left + 1f,
                $"Expected prompt to paint before the first language link. prompt={promptPaint.Bounds} link={linkPaint.Bounds}");
        }

        [Fact]
        public async System.Threading.Tasks.Task NestedSpanPill_CentersTextAgainstOuterAnchor()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #pill {
      background: #0b57d0;
      box-sizing: border-box;
      color: #fff;
      display: inline-block;
      font-size: 14px;
      line-height: 18px;
      min-height: 40px;
      min-width: 85px;
      padding: 10px 12px;
      text-align: center;
    }
    #label {
      display: inline;
      max-width: 100%;
      overflow: hidden;
    }
  </style>
</head>
<body><a id='pill'><span id='label'>Sign in</span></a></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var boxes = new ConcurrentDictionary<Node, BoxModel>(computer.GetAllBoxes());
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 800, 600, null);
            var nodes = Flatten(tree.Roots);
            var pill = doc.GetElementById("pill");
            var text = doc.Descendants()
                .OfType<Text>()
                .First(t => string.Equals(t.Data, "Sign in", StringComparison.Ordinal));

            Assert.True(boxes.TryGetValue(pill, out var pillBox));
            var textPaint = nodes
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => ReferenceEquals(n.SourceNode, text));

            Assert.NotNull(textPaint);
            float pillCenter = pillBox.ContentBox.Left + pillBox.ContentBox.Width * 0.5f;
            float textCenter = textPaint.Bounds.Left + textPaint.Bounds.Width * 0.5f;
            Assert.InRange(Math.Abs(textCenter - pillCenter), 0f, 2.5f);
        }

        [Fact]
        public void InlineSvgWithEmIntrinsicSize_DoesNotPreserveOnePixelPaintBox()
        {
            ImageLoader.ClearCache();

            var root = new Element("div");
            var wrapper = new Element("span");
            var svg = new Element("svg");
            var path = new Element("path");

            root.AppendChild(wrapper);
            wrapper.AppendChild(svg);
            svg.AppendChild(path);

            svg.SetAttribute("width", "1em");
            svg.SetAttribute("height", "1em");
            svg.SetAttribute("viewBox", "0 0 24 24");
            svg.SetAttribute("fill", "currentColor");
            path.SetAttribute("d", "M4 4h16v16H4z");

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 80, Height = 80 },
                [wrapper] = new CssComputed { Display = "inline-flex", Width = 40, Height = 40 },
                [svg] = new CssComputed
                {
                    Display = "inline-block",
                    FontSize = 16,
                    ForegroundColor = SKColors.White
                },
                [path] = new CssComputed { Display = "inline" }
            };

            var boxes = new Dictionary<Node, BoxModel>
            {
                [root] = BoxModel.FromContentBox(0, 0, 80, 80),
                [wrapper] = BoxModel.FromContentBox(0, 0, 40, 40),
                [svg] = BoxModel.FromContentBox(0, 0, 1, 1)
            };

            var tree = NewPaintTreeBuilder.Build(root, boxes, styles, 80, 80, null);
            var nodes = Flatten(tree.Roots);
            var image = nodes
                .OfType<ImagePaintNode>()
                .FirstOrDefault(n => ReferenceEquals(n.SourceNode, svg));

            Assert.NotNull(image);
            Assert.True(image.Bounds.Width >= 15f, $"Expected em-sized SVG paint width, got {image.Bounds.Width}.");
            Assert.True(image.Bounds.Height >= 15f, $"Expected em-sized SVG paint height, got {image.Bounds.Height}.");
        }

        [Fact]
        public void InlineSvgCurrentColor_RasterizesInheritedForeground()
        {
            ImageLoader.ClearCache();

            var root = new Element("div");
            var wrapper = new Element("span");
            var svg = new Element("svg");
            var path = new Element("path");

            root.AppendChild(wrapper);
            wrapper.AppendChild(svg);
            svg.AppendChild(path);

            svg.SetAttribute("width", "1em");
            svg.SetAttribute("height", "1em");
            svg.SetAttribute("viewBox", "0 0 24 24");
            svg.SetAttribute("fill", "currentColor");
            path.SetAttribute("d", "M4 4h16v16H4z");

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 80, Height = 80 },
                [wrapper] = new CssComputed
                {
                    Display = "inline-flex",
                    Width = 40,
                    Height = 40,
                    ForegroundColor = SKColors.White
                },
                [svg] = new CssComputed
                {
                    Display = "inline-block",
                    FontSize = 16,
                    ForegroundColor = SKColors.White
                },
                [path] = new CssComputed { Display = "inline" }
            };

            var boxes = new Dictionary<Node, BoxModel>
            {
                [root] = BoxModel.FromContentBox(0, 0, 80, 80),
                [wrapper] = BoxModel.FromContentBox(0, 0, 40, 40),
                [svg] = BoxModel.FromContentBox(0, 0, 22, 22)
            };

            var tree = NewPaintTreeBuilder.Build(root, boxes, styles, 80, 80, null);
            var nodes = Flatten(tree.Roots);
            var image = nodes
                .OfType<ImagePaintNode>()
                .FirstOrDefault(n => ReferenceEquals(n.SourceNode, svg));

            Assert.NotNull(image);
            Assert.NotNull(image.Bitmap);

            int whitePixels = 0;
            int darkPixels = 0;
            for (int y = 0; y < image.Bitmap.Height; y++)
            {
                for (int x = 0; x < image.Bitmap.Width; x++)
                {
                    var color = image.Bitmap.GetPixel(x, y);
                    if (color.Alpha == 0)
                    {
                        continue;
                    }

                    if (color.Red > 220 && color.Green > 220 && color.Blue > 220)
                    {
                        whitePixels++;
                    }
                    else if (color.Red < 40 && color.Green < 40 && color.Blue < 40)
                    {
                        darkPixels++;
                    }
                }
            }

            Assert.True(whitePixels > 0, "Expected inherited white currentColor pixels in the SVG bitmap.");
            Assert.True(whitePixels > darkPixels, $"Expected white SVG pixels to dominate; white={whitePixels}, dark={darkPixels}.");
        }

        [Fact]
        public async System.Threading.Tasks.Task PseudoElements_WithLayoutBoxes_DoNotEmitFallbackPaintText()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #card { display: block; width: 180px; padding: 8px; }
    #card::before {
      content: 'before visible';
      display: block;
      color: white;
      background: #14b8a6;
      border: 3px solid #0f766e;
      padding: 4px;
    }
    #card::after {
      content: 'after visible';
      display: block;
      color: white;
      background: #f97316;
      border: 3px solid #ea580c;
      padding: 4px;
    }
  </style>
</head>
<body><div id='card'>Body</div></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var layoutComputer = new LayoutEngineComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SKSize(800, 600));
            layoutComputer.Arrange(root, new SKRect(0, 0, 800, 600));

            var card = doc.GetElementById("card");
            Assert.NotNull(card);
            var beforePseudo = computed[card].Before?.PseudoElementInstance;
            var afterPseudo = computed[card].After?.PseudoElementInstance;
            Assert.NotNull(beforePseudo);
            Assert.NotNull(afterPseudo);

            var boxes = layoutComputer.GetAllBoxes().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Assert.True(boxes.ContainsKey(beforePseudo), "Expected ::before to have a layout box.");
            Assert.True(boxes.ContainsKey(afterPseudo), "Expected ::after to have a layout box.");

            var tree = NewPaintTreeBuilder.Build(root, boxes, computed, 800, 600, null);
            var nodes = Flatten(tree.Roots);

            Assert.DoesNotContain(nodes
                .OfType<CustomPaintNode>(),
                n => ReferenceEquals(n.SourceNode, beforePseudo) || ReferenceEquals(n.SourceNode, afterPseudo));
            Assert.Single(nodes.OfType<BackgroundPaintNode>(), n => ReferenceEquals(n.SourceNode, beforePseudo));
            Assert.Single(nodes.OfType<BackgroundPaintNode>(), n => ReferenceEquals(n.SourceNode, afterPseudo));
        }

        [Fact]
        public void PseudoElementFallback_WithAbsoluteSeparator_UsesPseudoGeometryAndOpacity()
        {
            var intro = new Element("div");
            intro.SetAttribute("class", "lp-Intro");

            var afterStyle = new CssComputed
            {
                Content = "\"\"",
                Position = "absolute",
                Left = 0,
                Right = 0,
                Bottom = 0,
                Height = 1,
                Opacity = 0.2,
                BackgroundImage = "linear-gradient(90deg,#fff0,#fff,#fff,#fff0)"
            };

            var styles = new Dictionary<Node, CssComputed>
            {
                [intro] = new CssComputed
                {
                    Display = "block",
                    Position = "relative",
                    BackgroundColor = new SKColor(0x0d, 0x11, 0x17),
                    After = afterStyle
                }
            };

            var boxes = new Dictionary<Node, BoxModel>
            {
                [intro] = BoxModel.FromContentBox(0, 0, 1280, 1278)
            };

            var tree = NewPaintTreeBuilder.Build(intro, boxes, styles, 1280, 720, null);
            var nodes = Flatten(tree.Roots);

            var afterGroup = Assert.Single(nodes.OfType<OpacityGroupPaintNode>(), n =>
                n.SourceNode is PseudoElement pseudo &&
                string.Equals(pseudo.PseudoType, "after", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(0.2f, afterGroup.Opacity, 3);
            Assert.InRange(afterGroup.Bounds.Height, 0.5f, 1.5f);
            Assert.Equal(1277f, afterGroup.Bounds.Top, 1);

            var afterImage = Assert.Single(afterGroup.Children.OfType<ImagePaintNode>());
            Assert.True(afterImage.IsBackgroundImage);
            Assert.InRange(afterImage.Bounds.Height, 0.5f, 1.5f);
            Assert.IsType<PseudoElement>(afterImage.SourceNode);
        }

        [Fact]
        public async System.Threading.Tasks.Task GradientBackgroundImage_WithSizeAndPosition_EmitsTiledPaintImage()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    body { margin: 0; }
    #check {
      width: 80px;
      height: 60px;
      background-color: transparent;
      background-image:
        linear-gradient(45deg, #ccc 25%, transparent 25%),
        linear-gradient(-45deg, #ccc 25%, transparent 25%),
        linear-gradient(45deg, transparent 75%, #ccc 75%),
        linear-gradient(-45deg, transparent 75%, #ccc 75%);
      background-size: 20px 20px;
      background-position: 0 0, 0 10px, 10px -10px, -10px 0;
    }
  </style>
</head>
<body><div id='check'></div></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var layoutComputer = new LayoutEngineComputer(computed, 800, 600);
            layoutComputer.Measure(root, new SKSize(800, 600));
            layoutComputer.Arrange(root, new SKRect(0, 0, 800, 600));

            var check = doc.GetElementById("check");
            var boxes = layoutComputer.GetAllBoxes().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var tree = NewPaintTreeBuilder.Build(root, boxes, computed, 800, 600, null);
            var nodes = Flatten(tree.Roots);

            var image = Assert.Single(nodes.OfType<ImagePaintNode>(), n => ReferenceEquals(n.SourceNode, check) && n.IsBackgroundImage);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(20, image.Bitmap.Width);
            Assert.Equal(20, image.Bitmap.Height);

            int opaque = 0;
            int transparent = 0;
            int unexpected = 0;
            for (int y = 0; y < image.Bitmap.Height; y++)
            {
                for (int x = 0; x < image.Bitmap.Width; x++)
                {
                    var pixel = image.Bitmap.GetPixel(x, y);
                    if (pixel.Alpha == 0)
                    {
                        transparent++;
                    }
                    else if (pixel.Red > 150 && pixel.Green > 150 && pixel.Blue > 150)
                    {
                        opaque++;
                    }
                    else
                    {
                        unexpected++;
                    }
                }
            }

            Assert.True(opaque > 0, "Expected generated checker tile to contain gray gradient pixels.");
            Assert.True(transparent > 0, "Expected generated checker tile to preserve transparent gaps.");
            Assert.Equal(0, unexpected);
        }

        [Fact]
        public void SkiaBorderBackend_RendersPerSideDashDotAndDoubleStyles()
        {
            using var bitmap = new SKBitmap(90, 90);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            var backend = new SkiaRenderBackend(canvas);
            backend.DrawBorder(new SKRect(10, 10, 80, 80), new BorderStyle
            {
                TopWidth = 8,
                RightWidth = 8,
                BottomWidth = 8,
                LeftWidth = 8,
                TopColor = SKColors.Red,
                RightColor = SKColors.Green,
                BottomColor = SKColors.Blue,
                LeftColor = SKColors.Orange,
                TopStyle = "solid",
                RightStyle = "dashed",
                BottomStyle = "dotted",
                LeftStyle = "double"
            });

            int rightPainted = 0;
            int rightTransparent = 0;
            for (int y = 14; y < 76; y++)
            {
                if (bitmap.GetPixel(76, y).Alpha > 0)
                {
                    rightPainted++;
                }
                else
                {
                    rightTransparent++;
                }
            }

            Assert.True(rightPainted > 0, "Expected dashed right border to paint visible runs.");
            Assert.True(rightTransparent > 0, "Expected dashed right border to leave gaps.");
            Assert.True(bitmap.GetPixel(11, 45).Alpha > 0, "Expected outer stroke of double left border.");
            Assert.True(bitmap.GetPixel(17, 45).Alpha > 0, "Expected inner stroke of double left border.");
            Assert.True(bitmap.GetPixel(14, 45).Alpha == 0, "Expected transparent gap inside double left border.");
        }

        [Fact]
        public void SkiaBackend_TransparentBitmapShader_CompositesOverExistingPixels()
        {
            using var tile = new SKBitmap(2, 2);
            tile.SetPixel(0, 0, SKColors.Transparent);
            tile.SetPixel(1, 0, new SKColor(229, 231, 235));
            tile.SetPixel(0, 1, new SKColor(229, 231, 235));
            tile.SetPixel(1, 1, SKColors.Transparent);

            using var bitmap = new SKBitmap(4, 4);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            var backend = new SkiaRenderBackend(canvas);

            using var shader = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            backend.DrawRect(new SKRect(0, 0, 4, 4), shader);

            Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));
            Assert.Equal(new SKColor(229, 231, 235), bitmap.GetPixel(1, 0));
        }

        private static List<PaintNodeBase> Flatten(IEnumerable<PaintNodeBase> nodes)
        {
            var list = new List<PaintNodeBase>();
            if (nodes == null)
            {
                return list;
            }

            foreach (var node in nodes)
            {
                list.Add(node);
                if (node.Children != null)
                {
                    list.AddRange(Flatten(node.Children));
                }
            }

            return list;
        }
    }
}
