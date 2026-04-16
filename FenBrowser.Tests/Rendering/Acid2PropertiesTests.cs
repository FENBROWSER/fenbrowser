using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Backends;
using FenBrowser.FenEngine.Rendering.Painting;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;
using System.Reflection;

namespace FenBrowser.Tests.Rendering
{
    public class Acid2PropertiesTests
    {
        private (MinimalLayoutComputer computer, Dictionary<Node, BoxModel> boxes, ImmutablePaintTree tree) RunPipeline(Element root, Dictionary<Node, CssComputed> styles)
        {
            var doc = new Document();
            doc.AppendChild(root);
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            
            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var boxes = boxesField.GetValue(computer) as System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>;
            
            var boxDict = new Dictionary<Node, BoxModel>(boxes);
            
            var tree = NewPaintTreeBuilder.Build(doc, boxDict, styles, 800, 600, null);
            return (computer, boxDict, tree);
        }

        [Fact]
        public void Visibility_Hidden_With_Visible_Child()
        {
            // Scenario: Acid2 Eyes/Pupils often rely on this.
            // .parent { visibility: hidden }
            // .child { visibility: visible }
            // Parent should NOT paint background/border, but Child SHOULD paint.
            
            var parent = new Element("DIV");
            var child = new Element("DIV");
            parent.AppendChild(child);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var pStyle = new CssComputed();
            pStyle.Display = "block";
            pStyle.Width = 100;
            pStyle.Height = 100;
            pStyle.BackgroundColor = SKColors.Red; // Should NOT match
            pStyle.Visibility = "hidden";
            styles[parent] = pStyle;
            
            var cStyle = new CssComputed();
            cStyle.Display = "block";
            cStyle.Width = 50;
            cStyle.Height = 50;
            cStyle.BackgroundColor = SKColors.Green; // Should match
            cStyle.Visibility = "visible";
            styles[child] = cStyle;
            
            var (computer, boxes, tree) = RunPipeline(parent, styles);
            
            // Check Layout: Parent should still exist and take space
            Assert.True(boxes.ContainsKey(parent));
            var pBox = boxes[parent];
            Assert.Equal(100, pBox.BorderBox.Width);
            Assert.Equal(100, pBox.BorderBox.Height);
            
            // Perform Flatten
            var paintNodes = FlattenTree(tree.Roots);
            
            // Verify Parent (Red) is NOT present
            var redNode = paintNodes.OfType<BackgroundPaintNode>().FirstOrDefault(n => n.Color.Equals(SKColors.Red));
            Assert.Null(redNode);
            
            // Verify Child (Green) IS present
            var greenNode = paintNodes.OfType<BackgroundPaintNode>().FirstOrDefault(n => n.Color.Equals(SKColors.Green));
            Assert.NotNull(greenNode);
        }

        [Fact]
        public void Overflow_Hidden_Clips_Content()
        {
            // Scenario: Acid2 overlapping elements often use overflow hidden to clip shapes
            var container = new Element("DIV");
            var child = new Element("DIV");
            container.AppendChild(child);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var cStyle = new CssComputed();
            cStyle.Display = "block";
            cStyle.Width = 50;
            cStyle.Height = 50;
            cStyle.Overflow = "hidden";
            cStyle.BackgroundColor = SKColors.Blue;
            styles[container] = cStyle;
            
            var childStyle = new CssComputed();
            childStyle.Display = "block";
            childStyle.Width = 100;
            childStyle.Height = 100;
            childStyle.BackgroundColor = SKColors.Red;
            styles[child] = childStyle;
            
            var (computer, boxes, tree) = RunPipeline(container, styles);
            
            // Verify Child Layout (should still be 100x100)
            var childBox = boxes[child];
            Assert.Equal(100, childBox.BorderBox.Width);
            
            // Verify Paint Tree contains ClipPaintNode
            var paintNodes = FlattenTree(tree.Roots);
            
            // We expect a ClipPaintNode wrapping the child content
            // The container generates a BackgroundPaintNode AND a ClipPaintNode (for children).
            var clipNode = paintNodes.OfType<ClipPaintNode>().FirstOrDefault();
            Assert.NotNull(clipNode);
            
            // Clip rect should be approx 50x50 (container padding box)
            // Note: ClipRect property on PaintNodeBase vs ClipPaintNode specific logic
            // In PaintNodeBase.cs, ClipPaintNode has ClipPath. But wait, PaintNodeBase has ClipRect property too.
            // Let's check ClipPaintNode definition again. It has ClipPath.
            // If it uses ClipRect from base, distinct.
            // Assuming ClipRect is what we want or the Bounds of the ClipPaintNode.
            Assert.True(clipNode.Bounds.Width <= 50); 
        }

        [Fact]
        public void Overflow_Hidden_OnInlineChild_PreservesInlinePaintOrder()
        {
            var parent = new Element("DIV");
            var before = new Element("SPAN");
            var clipped = new Element("SPAN");
            var after = new Element("SPAN");

            parent.AppendChild(before);
            parent.AppendChild(clipped);
            parent.AppendChild(after);

            var clippedChild = new Element("DIV");
            clipped.AppendChild(clippedChild);

            var styles = new Dictionary<Node, CssComputed>();

            styles[parent] = new CssComputed
            {
                Display = "block",
                Width = 120,
                LineHeight = 20
            };
            styles[before] = new CssComputed
            {
                Display = "inline-block",
                Width = 20,
                Height = 12,
                BackgroundColor = SKColors.Red
            };
            styles[clipped] = new CssComputed
            {
                Display = "inline-block",
                Width = 20,
                Height = 12,
                Overflow = "hidden",
                BackgroundColor = SKColors.Blue
            };
            styles[clippedChild] = new CssComputed
            {
                Display = "block",
                Width = 40,
                Height = 12,
                BackgroundColor = SKColors.Black
            };
            styles[after] = new CssComputed
            {
                Display = "inline-block",
                Width = 20,
                Height = 12,
                BackgroundColor = SKColors.Green
            };

            var (_, _, tree) = RunPipeline(parent, styles);
            var paintNodes = FlattenTree(tree.Roots);

            int beforeIndex = paintNodes.FindIndex(n => n is BackgroundPaintNode bg && bg.SourceNode == before);
            int clipIndex = paintNodes.FindIndex(n => n is ClipPaintNode clip && clip.SourceNode == clipped);
            int afterIndex = paintNodes.FindIndex(n => n is BackgroundPaintNode bg && bg.SourceNode == after);

            Assert.True(beforeIndex >= 0, "Expected leading inline sibling to paint.");
            Assert.True(clipIndex >= 0, "Expected clipped inline child to produce a clip node.");
            Assert.True(afterIndex >= 0, "Expected trailing inline sibling to paint.");
            Assert.True(beforeIndex < clipIndex, "Expected clipped inline child to remain after preceding inline sibling in paint order.");
            Assert.True(clipIndex < afterIndex, "Expected clipped inline child to remain before trailing inline sibling in paint order.");
        }
        
        [Fact]
        public void ZIndex_Stacking_Order()
        {
            // Scenario: Acid2 layering
            // Red: z-index 10, Blue: z-index 5. 
            // Both absolute/relative.
            // Order should be Blue then Red (Red on top).
            
            var container = new Element("DIV");
            var blue = new Element("DIV");
            var red = new Element("DIV");
            container.AppendChild(red); // Red first in DOM
            container.AppendChild(blue);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var conStyle = new CssComputed();
            conStyle.Position = "relative";
            conStyle.Width = 100;
            conStyle.Height = 100;
            conStyle.ZIndex = 0; // Stacking Context Root
            styles[container] = conStyle;
            
            var redStyle = new CssComputed();
            redStyle.Position = "absolute";
            redStyle.Width = 50;
            redStyle.Height = 50;
            redStyle.BackgroundColor = SKColors.Red;
            redStyle.ZIndex = 10;
            styles[red] = redStyle;
            
            var blueStyle = new CssComputed();
            blueStyle.Position = "absolute";
            blueStyle.Width = 50;
            blueStyle.Height = 50;
            blueStyle.BackgroundColor = SKColors.Blue;
            blueStyle.ZIndex = 5;
            styles[blue] = blueStyle;
            
            var (computer, boxes, tree) = RunPipeline(container, styles);
            
            var paintNodes = FlattenTree(tree.Roots).OfType<BackgroundPaintNode>().ToList();
            
            // Should find Blue and Red
            var blueNode = paintNodes.FirstOrDefault(n => n.Color.Equals(SKColors.Blue));
            var redNode = paintNodes.FirstOrDefault(n => n.Color.Equals(SKColors.Red));
             Assert.NotNull(blueNode);
            Assert.NotNull(redNode);
            
            // Index of Blue should be less than Red
            var blueIdx = paintNodes.IndexOf(blueNode);
            var redIdx = paintNodes.IndexOf(redNode);
            
            Assert.True(blueIdx < redIdx, $"Blue (Index {blueIdx}) should be painted BEFORE Red (Index {redIdx})");
        }

        [Fact]
        public void Object_WithData_PaintsReplacedContent_WithoutFallbackText()
        {
            var root = new Element("DIV");
            var obj = new Element("OBJECT");
            obj.SetAttribute("data", CreatePngDataUrl(SKColors.Red));
            obj.AppendChild(new Text("FAIL"));
            root.AppendChild(obj);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 120, Height = 80 },
                [obj] = new CssComputed { Display = "block", Width = 40, Height = 30 }
            };

            var (_, _, tree) = RunPipeline(root, styles);
            var paintNodes = FlattenTree(tree.Roots);

            var objectImage = paintNodes.OfType<ImagePaintNode>().FirstOrDefault(n => ReferenceEquals(n.SourceNode, obj));
            Assert.NotNull(objectImage);

            var failText = paintNodes
                .OfType<TextPaintNode>()
                .FirstOrDefault(n => string.Equals(n.FallbackText, "FAIL", StringComparison.Ordinal));
            Assert.Null(failText);
        }

        [Fact]
        public async System.Threading.Tasks.Task Acid2Eyes_BlockLayer_ProducesPaintCoverage_BeneathFloatLayer()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .eyes { position: absolute; top: 5em; left: 3em; margin: 0; padding: 0; background: red; }
   #eyes-a { height: 0; line-height: 2em; text-align: right; }
   #eyes-a object { display: inline; vertical-align: bottom; }
   #eyes-a object[type] { width: 7.5em; height: 2.5em; }
   #eyes-a object object object { border-right: solid 1em black; padding: 0 12px 0 11px; background: yellow; }
   #eyes-b { float: left; width: 10em; height: 2em; background: fixed url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAABnRSTlMAAAAAAABupgeRAAAABmJLR0QA/wD/AP+gvaeTAAAAEUlEQVR42mP4/58BCv7/ZwAAHfAD/FabwPj4AAAAASUVORK5CYII=); border-left: solid 1em black; border-right: solid 1em red; }
   #eyes-c { display: block; background: red; border-left: 2em solid yellow; width: 10em; height: 2em; }
  </style>
</head>
<body>
  <div class='eyes'>
    <div id='eyes-a'>
      <object data='data:application/x-unknown,ERROR'>
        <object data='http://example.invalid/' type='text/html'>
          <object data='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAABnRSTlMAAAAAAABupgeRAAAABmJLR0QA/wD/AP+gvaeTAAAAEUlEQVR42mP4/58BCv7/ZwAAHfAD/FabwPj4AAAAASUVORK5CYII='>ERROR</object>
        </object>
      </object>
    </div>
    <div id='eyes-b'></div>
    <div id='eyes-c'></div>
  </div>
</body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 400, viewportHeight: 300);

            var computer = new MinimalLayoutComputer(styles, 400, 300);
            computer.Measure(doc, new SKSize(400, 300));
            computer.Arrange(doc, new SKRect(0, 0, 400, 300));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", BindingFlags.NonPublic | BindingFlags.Instance);
            var boxes = boxesField.GetValue(computer) as System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>;
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 400, 300, null);
            var paintNodes = FlattenTree(tree.Roots);

            var eyesB = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-b");
            var eyesC = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-c");

            int blockLayerIndex = paintNodes.FindIndex(n => ReferenceEquals(n.SourceNode, eyesC));
            int floatLayerIndex = paintNodes.FindIndex(n => ReferenceEquals(n.SourceNode, eyesB));

            Assert.True(blockLayerIndex >= 0, "Expected Acid2 #eyes-c block layer to materialize in the paint tree.");
            Assert.True(floatLayerIndex >= 0, "Expected Acid2 #eyes-b float layer to materialize in the paint tree.");
            Assert.True(blockLayerIndex < floatLayerIndex, $"Expected block layer to paint beneath float layer, got eyes-c index {blockLayerIndex} and eyes-b index {floatLayerIndex}.");
        }

        [Fact]
        public async System.Threading.Tasks.Task Acid2LowerFace_SmileAndParser_SubtreesProducePaintCoverage()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .nose { float: left; margin: -2em 2em -1em; border: solid 1em black; border-top: 0; min-height: 80%; height: 60%; max-height: 3em; padding: 0; width: 12em; }
   .nose > div { padding: 1em 1em 3em; height: 0; background: yellow; }
   .nose div div { width: 2em; height: 2em; background: red; margin: auto; }
   .empty { margin: 6.25em; height: 10%; }
   .empty div { margin: 0 2em -6em 4em; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
   .smile div div { position: absolute; top: 0; right: 1em; width: auto; height: 0; margin: 0; border: yellow solid 1em; }
   .smile div div span { display: inline; margin: -1em 0 0 0; border: solid 1em transparent; border-style: none solid; float: right; background: black; height: 1em; }
   .smile div div span em { float: inherit; border-top: solid yellow 1em; border-bottom: solid black 1em; }
   .smile div div span em strong { width: 6em; display: block; margin-bottom: -1em; }
   .chin { margin: -4em 4em 0; width: 8em; line-height: 1em; border-left: solid 1em black; border-right: solid 1em black; background: yellow url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAFSDNYfAAAAaklEQVR42u3XQQrAIAwAQeP//6wf8CJBJTK9lnQ7FpHGaOurt1I34nfH9pMMZAZ8BwMGEvvh+BsJCAgICLwIOA8EBAQEBAQEBAQEBK79H5RfIQAAAAAAAAAAAAAAAAAAAAAAAAAAAID/ABMSqAfj/sLmvAAAAABJRU5ErkJggg==) no-repeat fixed; }
   .chin div { display: inline; font: 2px/4px serif; }
   .parser-container div { color: maroon; border: solid; color: orange; }
   div.parser-container * { border-color: black; }
   * div.parser { border-width: 0 2em; }
   .parser { margin: 0 5em 1em; padding: 0 1em; width: 2em; height: 1em; error: \}; background: yellow; }
   * html .parser { background: gray; }
   \\.parser { padding: 2em; }
   .parser { m\\argin: 2em; };
   .parser { height: 3em; }
   .parser { width: 200; }
   .parser { border: 5em solid red ! error; }
   .parser { background: red pink; }
   ul { display: table; padding: 0; margin: -1em 7em 0; background: red; }
   ul li { padding: 0; margin: 0; }
   ul li.first-part { display: table-cell; height: 1em; width: 1em; background: black; }
   ul li.second-part { display: table; height: 1em; width: 1em; background: black; }
   ul li.third-part { display: table-cell; height: 0.5em; width: 1em; background: black; }
   ul li.fourth-part { list-style: none; height: 1em; width: 1em; background: black; }
  </style>
</head>
<body>
  <div class='nose'><div><div></div></div></div>
  <div class='empty'><div></div></div>
  <div class='smile'><div id='smile-outer'><div id='smile-inner'><span id='smile-span'><em><strong></strong></em></span></div></div></div>
  <div class='chin'><div>&nbsp;</div></div>
  <div class='parser-container'><div id='parser' class='parser'><!-- ->ERROR<!- --></div></div>
  <ul id='tail'>
    <li class='first-part'></li>
    <li class='second-part'></li>
    <li class='third-part'></li>
    <li class='fourth-part'></li>
  </ul>
</body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 500, viewportHeight: 400);

            var computer = new MinimalLayoutComputer(styles, 500, 400);
            computer.Measure(doc, new SKSize(500, 400));
            computer.Arrange(doc, new SKRect(0, 0, 500, 400));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", BindingFlags.NonPublic | BindingFlags.Instance);
            var boxes = boxesField.GetValue(computer) as System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>;
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 500, 400, null);
            var paintNodes = FlattenTree(tree.Roots);

            var smileOuter = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-outer");
            var smileInner = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-inner");
            var smileSpan = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-span");
            var parserBox = doc.Descendants().OfType<Element>().First(e => e.Id == "parser");
            var tail = doc.Descendants().OfType<Element>().First(e => e.Id == "tail");
            var parserStyle = styles[parserBox];

            Assert.False(
                parserStyle.Map.TryGetValue("border-left-color", out var parserLeftColor) &&
                parserLeftColor.Contains("error", StringComparison.OrdinalIgnoreCase),
                "Malformed '! error' declaration must be discarded and must not override parser border color.");

            Assert.True(
                parserStyle.Map.TryGetValue("border-color", out var parserBorderColor) &&
                parserBorderColor.Contains("black", StringComparison.OrdinalIgnoreCase),
                "Valid parser border-color declaration should remain effective after malformed declarations are dropped.");

            Assert.Contains(paintNodes, n => ReferenceEquals(n.SourceNode, smileOuter));
            Assert.Contains(paintNodes, n => ReferenceEquals(n.SourceNode, smileInner));
            Assert.Contains(paintNodes, n => ReferenceEquals(n.SourceNode, smileSpan));
            Assert.Contains(paintNodes, n => ReferenceEquals(n.SourceNode, parserBox));
            Assert.Contains(paintNodes, n => ReferenceEquals(n.SourceNode, tail));
        }

        [Fact]
        public async System.Threading.Tasks.Task BorderPaintNode_Preserves_Acid2_Side_Styles_And_Colors()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    #target {
      width: 20px;
      height: 20px;
      border-style: none solid;
      border-width: 10px;
      border-color: transparent black transparent red;
    }
  </style>
</head>
<body><div id='target'></div></body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 200, viewportHeight: 200);

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var computer = new MinimalLayoutComputer(styles, 200, 200);
            computer.Measure(doc, new SKSize(200, 200));
            computer.Arrange(doc, new SKRect(0, 0, 200, 200));
            var boxes = (System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>)boxesField.GetValue(computer);
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 200, 200, null);

            var border = FlattenTree(tree.Roots).OfType<BorderPaintNode>().FirstOrDefault(n => (n.SourceNode as Element)?.Id == "target");
            Assert.NotNull(border);
            Assert.Equal("none", border.Styles[0]);
            Assert.Equal("solid", border.Styles[1]);
            Assert.Equal("none", border.Styles[2]);
            Assert.Equal("solid", border.Styles[3]);
            Assert.Equal(SKColors.Transparent, border.Colors[0]);
            Assert.Equal(SKColors.Black, border.Colors[1]);
            Assert.Equal(SKColors.Transparent, border.Colors[2]);
            Assert.Equal(SKColors.Red, border.Colors[3]);
        }

        [Fact]
        public void SkiaRenderer_BorderPaintNode_Renders_Acid2_ZeroHeightBorderBox()
        {
            using var bitmap = new SKBitmap(128, 64);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            var renderer = new SkiaRenderer();
            var backend = new SkiaRenderBackend(canvas);
            var drawBorder = typeof(SkiaRenderer).GetMethod("DrawBorder", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(drawBorder);

            var node = new BorderPaintNode
            {
                Bounds = new SKRect(16, 16, 112, 16),
                Widths = new[] { 12f, 12f, 12f, 12f },
                Colors = new[] { SKColors.Yellow, SKColors.Yellow, SKColors.Yellow, SKColors.Yellow },
                Styles = new[] { "solid", "solid", "solid", "solid" }
            };

            drawBorder.Invoke(renderer, new object[] { backend, node });

            bool foundPaintedPixel = false;
            for (int y = 0; y < bitmap.Height && !foundPaintedPixel; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) == SKColors.Yellow)
                    {
                        foundPaintedPixel = true;
                        break;
                    }
                }
            }

            Assert.True(foundPaintedPixel, "Expected zero-height Acid2-style border box to paint visible border pixels.");
        }

        [Fact]
        public void BackgroundAttachment_Fixed_UsesViewportInsteadOfElementBox()
        {
            using var bitmap = new SKBitmap(160, 160);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            var imagePainter = new ImagePainter();
            var boxPainter = new BoxPainter();
            string dataUrl = CreatePngDataUrl(SKColors.Red);
            imagePainter.CacheImage(dataUrl, imagePainter.LoadFromDataUri(dataUrl));

            var style = new CssComputed
            {
                BackgroundImage = $"url('{dataUrl}')",
                BackgroundRepeat = "no-repeat",
                BackgroundAttachment = "fixed"
            };

            var box = new SKRect(80, 80, 120, 120);
            boxPainter.PaintBackground(canvas, box, style, imagePainter: imagePainter);

            bool foundRedInElement = false;
            for (int y = 80; y < 120 && !foundRedInElement; y++)
            {
                for (int x = 80; x < 120; x++)
                {
                    if (bitmap.GetPixel(x, y) == SKColors.Red)
                    {
                        foundRedInElement = true;
                        break;
                    }
                }
            }

            Assert.False(foundRedInElement, "Expected fixed background image to stay anchored to the viewport, not repaint at the element's local origin.");
        }

        [Fact]
        public void BackgroundPosition_PixelOffsets_ShiftFixedImageFromViewportOrigin()
        {
            using var bitmap = new SKBitmap(16, 16);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            using var tile = new SKBitmap(2, 2);
            tile.Erase(SKColors.Red);

            var backend = new SkiaRenderBackend(canvas);
            var renderer = new SkiaRenderer();
            var drawImage = typeof(SkiaRenderer).GetMethod("DrawImage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(drawImage);

            var node = new ImagePaintNode
            {
                Bounds = new SKRect(0, 0, 12, 12),
                Bitmap = tile,
                ObjectFit = "none",
                IsBackgroundImage = true,
                TileModeX = SKShaderTileMode.Decal,
                TileModeY = SKShaderTileMode.Decal,
                BackgroundAttachmentFixed = true,
                BackgroundPosition = new SKPoint(1, 0),
                FixedViewportOrigin = SKPoint.Empty
            };

            drawImage.Invoke(renderer, new object[] { backend, node });

            Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(1, 0));

            bool foundShiftedRed = false;
            for (int x = 1; x < 8 && !foundShiftedRed; x++)
            {
                if (bitmap.GetPixel(x, 0) == SKColors.Red)
                {
                    foundShiftedRed = true;
                }
            }

            Assert.True(foundShiftedRed, "Expected pixel-positioned background image to move away from the viewport origin.");
        }

        [Fact]
        public void Acid2EyeTiles_OnePixelOffset_ComposeIntoSolidYellowBand()
        {
            using var bitmap = new SKBitmap(16, 4);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Red);

            using var tile = new SKBitmap(2, 2);
            tile.SetPixel(0, 0, SKColors.Yellow);
            tile.SetPixel(1, 0, SKColors.Transparent);
            tile.SetPixel(0, 1, SKColors.Transparent);
            tile.SetPixel(1, 1, SKColors.Yellow);

            var backend = new SkiaRenderBackend(canvas);
            var renderer = new SkiaRenderer();
            var drawBackgroundImage = typeof(SkiaRenderer).GetMethod("DrawBackgroundImageNode", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(drawBackgroundImage);

            var baseNode = new ImagePaintNode
            {
                Bounds = new SKRect(0, 0, 12, 2),
                Bitmap = tile,
                IsBackgroundImage = true,
                TileModeX = SKShaderTileMode.Repeat,
                TileModeY = SKShaderTileMode.Repeat,
                BackgroundAttachmentFixed = true,
                BackgroundOrigin = SKPoint.Empty,
                FixedViewportOrigin = SKPoint.Empty,
                BackgroundPosition = SKPoint.Empty
            };

            var offsetNode = new ImagePaintNode
            {
                Bounds = baseNode.Bounds,
                Bitmap = baseNode.Bitmap,
                IsBackgroundImage = baseNode.IsBackgroundImage,
                TileModeX = baseNode.TileModeX,
                TileModeY = baseNode.TileModeY,
                BackgroundAttachmentFixed = baseNode.BackgroundAttachmentFixed,
                BackgroundOrigin = baseNode.BackgroundOrigin,
                FixedViewportOrigin = baseNode.FixedViewportOrigin,
                BackgroundPosition = new SKPoint(1, 0)
            };

            drawBackgroundImage.Invoke(renderer, new object[] { backend, baseNode });
            drawBackgroundImage.Invoke(renderer, new object[] { backend, offsetNode });

            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 12; x++)
                {
                    Assert.Equal(
                        SKColors.Yellow,
                        bitmap.GetPixel(x, y));
                }
            }
        }

        [Fact]
        public void Acid2EyeTiles_FixedViewportOrigin_RemainsSolidAfterHostScrollTranslation()
        {
            using var bitmap = new SKBitmap(16, 4);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Red);
            canvas.Translate(0, -100);

            using var tile = new SKBitmap(2, 2);
            tile.SetPixel(0, 0, SKColors.Yellow);
            tile.SetPixel(1, 0, SKColors.Transparent);
            tile.SetPixel(0, 1, SKColors.Transparent);
            tile.SetPixel(1, 1, SKColors.Yellow);

            var backend = new SkiaRenderBackend(canvas);
            var renderer = new SkiaRenderer();
            var drawBackgroundImage = typeof(SkiaRenderer).GetMethod("DrawBackgroundImageNode", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(drawBackgroundImage);

            var baseNode = new ImagePaintNode
            {
                Bounds = new SKRect(0, 100, 12, 102),
                Bitmap = tile,
                IsBackgroundImage = true,
                TileModeX = SKShaderTileMode.Repeat,
                TileModeY = SKShaderTileMode.Repeat,
                BackgroundAttachmentFixed = true,
                BackgroundOrigin = new SKPoint(0, 100),
                FixedViewportOrigin = new SKPoint(0, 100),
                BackgroundPosition = SKPoint.Empty
            };

            var offsetNode = new ImagePaintNode
            {
                Bounds = baseNode.Bounds,
                Bitmap = baseNode.Bitmap,
                IsBackgroundImage = baseNode.IsBackgroundImage,
                TileModeX = baseNode.TileModeX,
                TileModeY = baseNode.TileModeY,
                BackgroundAttachmentFixed = baseNode.BackgroundAttachmentFixed,
                BackgroundOrigin = baseNode.BackgroundOrigin,
                FixedViewportOrigin = baseNode.FixedViewportOrigin,
                BackgroundPosition = new SKPoint(1, 0)
            };

            drawBackgroundImage.Invoke(renderer, new object[] { backend, baseNode });
            drawBackgroundImage.Invoke(renderer, new object[] { backend, offsetNode });

            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 12; x++)
                {
                    Assert.Equal(
                        SKColors.Yellow,
                        bitmap.GetPixel(x, y));
                }
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Acid2EyeAndChin_BackgroundShorthands_ResolveComputedBackgroundFields()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
   html { font: 12px sans-serif; }
   .eyes { position: absolute; top: 5em; left: 3em; margin: 0; padding: 0; background: red; }
   #eyes-b { float: left; width: 10em; height: 2em; background: fixed url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAABnRSTlMAAAAAAABupgeRAAAABmJLR0QA/wD/AP+gvaeTAAAAEUlEQVR42mP4/58BCv7/ZwAAHfAD/abwPj4AAAAASUVORK5CYII=); border-left: solid 1em black; border-right: solid 1em red; }
   .chin { margin: -4em 4em 0; width: 8em; line-height: 1em; border-left: solid 1em black; border-right: solid 1em black; background: yellow url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAFSDNYfAAAAaklEQVR42u3XQQrAIAwAQeP//6wf8CJBJTK9lnQ7FpHGaOurt1I34nfH9pMMZAZ8BwMGEvvh+BsJCAgICLwIOA8EBAQEBAQEBAQEBK79H5RfIQAAAAAAAAAAAAAAAAAAAAAAAAAAAID/ABMSqAfj/sLmvAAAAABJRU5ErkJggg==) no-repeat fixed; }
  </style>
</head>
<body>
  <div class='eyes'><div id='eyes-b'></div></div>
  <div class='chin'></div>
</body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 400, viewportHeight: 300);

            var eyesB = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-b");
            var chin = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("chin"));

            Assert.Equal("fixed", styles[eyesB].BackgroundAttachment);
            Assert.True(string.IsNullOrEmpty(styles[eyesB].BackgroundRepeat) || styles[eyesB].BackgroundRepeat == "repeat");
            Assert.Contains("url(", styles[eyesB].BackgroundImage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("fixed", styles[chin].BackgroundAttachment);
            Assert.Equal("no-repeat", styles[chin].BackgroundRepeat);
            Assert.Contains("url(", styles[chin].BackgroundImage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async System.Threading.Tasks.Task Acid2EyeBackgroundImage_UsesBorderBoxPaintAndPaddingBoxOrigin()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
   html { font: 12px sans-serif; }
   #eyes-b {
     float: left;
     width: 10em;
     height: 2em;
     background: fixed url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAABnRSTlMAAAAAAABupgeRAAAABmJLR0QA/wD/AP+gvaeTAAAAEUlEQVR42mP4/58BCv7/ZwAAHfAD/abwPj4AAAAASUVORK5CYII=);
     border-left: solid 1em black;
     border-right: solid 1em red;
   }
  </style>
</head>
<body>
  <div id='eyes-b'></div>
</body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 400, viewportHeight: 300);

            var computer = new MinimalLayoutComputer(styles, 400, 300);
            computer.Measure(doc, new SKSize(400, 300));
            computer.Arrange(doc, new SKRect(0, 0, 400, 300));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", BindingFlags.NonPublic | BindingFlags.Instance);
            var boxes = (System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>)boxesField.GetValue(computer);
            var boxMap = new Dictionary<Node, BoxModel>(boxes);
            var tree = NewPaintTreeBuilder.Build(doc, boxMap, styles, 400, 300, null);
            var nodes = FlattenTree(tree.Roots);

            var eyesB = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-b");
            var imageNode = Assert.IsType<ImagePaintNode>(nodes.First(n => n is ImagePaintNode img && ReferenceEquals(img.SourceNode, eyesB)));
            var eyesBBox = boxMap[eyesB];

            Assert.Equal(eyesBBox.BorderBox.Left, imageNode.Bounds.Left);
            Assert.Equal(eyesBBox.BorderBox.Right, imageNode.Bounds.Right);
            Assert.Equal(eyesBBox.PaddingBox.Left, imageNode.BackgroundOrigin.X);
            Assert.Equal(eyesBBox.PaddingBox.Top, imageNode.BackgroundOrigin.Y);
        }

        [Fact]
        public void BackgroundImageNode_FixedAttachment_UsesViewportScrollOrigin()
        {
            var root = new Element("DIV");
            var child = new Element("DIV");
            root.AppendChild(child);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 200, Height = 200 },
                [child] = new CssComputed
                {
                    Display = "block",
                    Width = 40,
                    Height = 20,
                    BackgroundImage = $"url('{CreatePngDataUrl(SKColors.Red)}')",
                    BackgroundAttachment = "fixed"
                }
            };

            var doc = new Document();
            doc.AppendChild(root);
            var computer = new MinimalLayoutComputer(styles, 200, 200);
            computer.Measure(doc, new SKSize(200, 200));
            computer.Arrange(doc, new SKRect(0, 0, 200, 200));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", BindingFlags.NonPublic | BindingFlags.Instance);
            var boxes = (System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>)boxesField.GetValue(computer);

            var scrollManager = new ScrollManager();
            scrollManager.SetScrollPosition(null, 0, 1888);

            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 200, 200, scrollManager, null);
            var paintNodes = FlattenTree(tree.Roots);
            var imageNode = Assert.IsType<ImagePaintNode>(paintNodes.First(n => n is ImagePaintNode img && ReferenceEquals(img.SourceNode, child)));

            Assert.Equal(1888f, imageNode.FixedViewportOrigin.Y);
        }

        [Fact]
        public async System.Threading.Tasks.Task ObjectFallbackChain_PaintsInnermostSupportedObject()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
   #eyes-a object object object {
     border-right: solid 1em black;
     padding: 0 12px 0 11px;
     background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAABnRSTlMAAAAAAABupgeRAAAABmJLR0QA/wD/AP+gvaeTAAAAEUlEQVR42mP4/58BCv7/ZwAAHfAD/abwPj4AAAAASUVORK5CYII=) fixed 1px 0;
   }
  </style>
</head>
<body>
  <div id='eyes-a'>
    <object data='data:application/x-unknown,ERROR'>
      <object data='http://www.damowmow.com/404/' type='text/html'>
        <object id='target' data='data:image/png;base64,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'>ERROR</object>
      </object>
    </object>
  </div>
</body>
</html>";

            var parser = new FenBrowser.Core.Parsing.HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 500, viewportHeight: 200);

            var computer = new MinimalLayoutComputer(styles, 500, 200);
            computer.Measure(doc, new SKSize(500, 200));
            computer.Arrange(doc, new SKRect(0, 0, 500, 200));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", BindingFlags.NonPublic | BindingFlags.Instance);
            var boxes = (System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>)boxesField.GetValue(computer);
            var tree = NewPaintTreeBuilder.Build(doc, new Dictionary<Node, BoxModel>(boxes), styles, 500, 200, null);
            var paintNodes = FlattenTree(tree.Roots);

            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "target");
            Assert.Contains(paintNodes, n => n is ImagePaintNode img && ReferenceEquals(img.SourceNode, target));
            Assert.Contains(paintNodes, n => n is ImagePaintNode img && ReferenceEquals(img.SourceNode, target) && img.IsBackgroundImage);
            Assert.DoesNotContain(paintNodes.OfType<TextPaintNode>(), n => string.Equals(n.FallbackText, "ERROR", StringComparison.Ordinal));
        }

        [Fact]
        public void SkiaBorderRenderer_DoesNotPaint_Suppressed_Sides()
        {
            using var bitmap = new SKBitmap(64, 64);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            var backend = new SkiaRenderBackend(canvas);
            backend.DrawBorder(
                new SKRect(16, 16, 48, 48),
                new BorderStyle
                {
                    TopWidth = 8,
                    RightWidth = 8,
                    BottomWidth = 8,
                    LeftWidth = 8,
                    TopColor = SKColors.Blue,
                    RightColor = SKColors.Black,
                    BottomColor = SKColors.Blue,
                    LeftColor = SKColors.Red,
                    TopStyle = "none",
                    RightStyle = "solid",
                    BottomStyle = "none",
                    LeftStyle = "solid"
                });

            Assert.Equal(SKColors.White, bitmap.GetPixel(32, 16));
            Assert.Equal(SKColors.White, bitmap.GetPixel(32, 47));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(16, 32));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(47, 32));
        }

        // Helper to recursively flatten paint nodes
        private List<PaintNodeBase> FlattenTree(IEnumerable<PaintNodeBase> nodes)
        {
            var list = new List<PaintNodeBase>();
            if (nodes == null) return list;
            
            foreach (var node in nodes)
            {
                list.Add(node);
                // Recursively add children
                if (node.Children != null)
                {
                    list.AddRange(FlattenTree(node.Children));
                }
            }
            return list;
        }

        private static string CreatePngDataUrl(SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(2, 2));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }
    }
}
