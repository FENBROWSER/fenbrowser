using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.Tests.Layout
{
    public class Acid2LayoutTests
    {
        // Helper to run layout on a small tree
        private (MinimalLayoutComputer computer, BoxModel box) LayoutElement(Element root, CssComputed style)
        {
            var styles = new Dictionary<Node, CssComputed> { { root, style } };
            // Ensure implicit body/html like structure if needed, or just root
            // For absolute positioning, we often need a container.
            
            // Create a viewport-sized container (ICB)
            var doc = new Document();
            doc.AppendChild(root);
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            return (computer, computer.GetBox(root));
        }

        [Fact]
        public void AbsolutePositioning_Respects_MinHeight_Constraint()
        {
            // Scenario: Acid2 Scalp/Forehead
            // .top class in Acid2 often uses absolute positioning with constraints
            
            var doc = new Document();
            var container = new Element("DIV");
            doc.AppendChild(container);
            
            var absChild = new Element("DIV");
            container.AppendChild(absChild);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            // Container: Relative, 100x100
            var containerStyle = new CssComputed();
            containerStyle.Display = "block";
            containerStyle.Position = "relative";
            containerStyle.Width = 100;
            containerStyle.Height = 100;
            styles[container] = containerStyle;
            
            // AbsChild: Absolute, Top:0, Bottom:auto, Height:auto, MinHeight: 50
            // If content is empty, height should resolve to 0 normally, but min-height should force it to 50.
            var absStyle = new CssComputed();
            absStyle.Position = "absolute";
            absStyle.Top = 0;
            // Height is auto by default
            absStyle.MinHeight = 50;
            absStyle.BackgroundColor = SKColors.Red;
            styles[absChild] = absStyle;
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var childBox = computer.GetBox(absChild);
            Assert.NotNull(childBox);
            
            Assert.Equal(50, childBox.ContentBox.Height);
        }

        [Fact]
        public void AbsolutePositioning_NegativeMargins()
        {
            // Verify negative margins move the element outside the container
            var doc = new Document();
            var container = new Element("DIV");
            doc.AppendChild(container);
            var absChild = new Element("DIV");
            container.AppendChild(absChild);
            
            var styles = new Dictionary<Node, CssComputed>();
            
            var containerStyle = new CssComputed();
            containerStyle.Display = "block";
            containerStyle.Position = "relative";
            containerStyle.Width = 100;
            containerStyle.Height = 100;
            containerStyle.Margin = new FenBrowser.Core.Thickness(50); // Move container to 50,50
            styles[container] = containerStyle;
            
            var absStyle = new CssComputed();
            absStyle.Position = "absolute";
            absStyle.Top = 0;
            absStyle.Left = 0;
            absStyle.Width = 20;
            absStyle.Height = 20;
            absStyle.Margin = new FenBrowser.Core.Thickness(-10, -10, 0, 0); // Left -10, Top -10
            styles[absChild] = absStyle;
            
            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var containerBox = computer.GetBox(container);
            var childBox = computer.GetBox(absChild);
            Assert.NotNull(containerBox);
            Assert.NotNull(childBox);
            
            var cbX = containerBox.PaddingBox.Left;
            var cbY = containerBox.PaddingBox.Top;
            
            // Child position relative to CB:
            // Top = 0, Left = 0.
            // Margin Top = -10, Margin Left = -10.
            // X = Left + MarginLeft = 0 - 10 = -10.
            // Y = Top + MarginTop = 0 - 10 = -10.
            // Absolute X/Y in document space should be Container.PaddingBox.X - 10
            
            var expectedX = cbX - 10;
            var actualX = childBox.BorderBox.Left;
            
            // Note: Assert.Equal had issues with precision/reporting, using explicit check
            Assert.True(Math.Abs(expectedX - actualX) < 1.0f, $"FAILURE: Expected {expectedX} (CB={cbX}), Actual {actualX}. Box={childBox.BorderBox}");
            
            Assert.True(Math.Abs((cbY - 10) - childBox.BorderBox.Top) < 1.0f, "Y Mismatch");
        }

        [Fact]
        public async Task Acid2Intro_CopyFitsOnSingleLineAfterFontInheritance()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .intro { font: 2em sans-serif; margin: 3.5em 2em; padding: 0.5em; border: solid thin; background: white; color: black; position: relative; z-index: 2; }
   .intro * { font: inherit; margin: 0; padding: 0; }
   .intro h1 { font-size: 1em; font-weight: bolder; margin: 0; padding: 0; }
   .intro :link { color: blue; }
  </style>
</head>
<body>
  <div class='intro'>
    <h1>Standards compliant?</h1>
    <p id='copy'><a id='lead' href='#top'>Take The Acid2 Test</a> and compare it to <a id='tail' href='reference.html'>the reference rendering</a>.</p>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 1920, viewportHeight: 1080);

            var computer = new MinimalLayoutComputer(styles, 1920, 1080);
            computer.Measure(doc, new SKSize(1920, 1080));
            computer.Arrange(doc, new SKRect(0, 0, 1920, 1080));

            var paragraph = doc.Descendants().OfType<Element>().First(e => e.Id == "copy");
            var paragraphBox = computer.GetBox(paragraph);
            Assert.NotNull(paragraphBox);
            Assert.True(paragraphBox.ContentBox.Height < 40f, $"Expected a single intro line, got paragraph height {paragraphBox.ContentBox.Height}.");

            var leadText = doc.Descendants().OfType<Text>().First(t => t.Data.Contains("Take The Acid2 Test", StringComparison.Ordinal));
            var middleText = doc.Descendants().OfType<Text>().First(t => t.Data.Contains("and compare it to", StringComparison.Ordinal));
            var tailText = doc.Descendants().OfType<Text>().First(t => t.Data.Contains("the reference rendering", StringComparison.Ordinal));

            var leadBox = computer.GetBox(leadText);
            var middleBox = computer.GetBox(middleText);
            var tailBox = computer.GetBox(tailText);

            Assert.NotNull(leadBox);
            Assert.NotNull(middleBox);
            Assert.NotNull(tailBox);

            Assert.InRange(Math.Abs(leadBox.ContentBox.Top - middleBox.ContentBox.Top), 0f, 2f);
            Assert.InRange(Math.Abs(tailBox.ContentBox.Top - middleBox.ContentBox.Top), 0f, 2f);
        }

        [Fact]
        public void Acid2Eyes_ObjectFallbackClassificationMatchesNestedPayloads()
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
  </style>
</head>
<body>
  <div class='eyes'>
    <div id='eyes-a'>
      <object id='outer' data='data:application/x-unknown,ERROR'>
        <object data='http://example.invalid/' type='text/html'>
          <object id='inner' data='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAABnRSTlMAAAAAAABupgeRAAAABmJLR0QA/wD/AP+gvaeTAAAAEUlEQVR42mP4/58BCv7/ZwAAHfAD/FabwPj4AAAAASUVORK5CYII='>ERROR</object>
        </object>
      </object>
    </div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var objects = doc.Descendants().OfType<Element>().Where(e => string.Equals(e.TagName, "object", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.Equal(3, objects.Count);
            Assert.True(ReplacedElementSizing.ShouldUseObjectFallbackContent(objects[0]), "The outer Acid2 object should expose fallback content.");
            Assert.True(ReplacedElementSizing.ShouldUseObjectFallbackContent(objects[1]), "The middle Acid2 object should expose fallback content.");
            Assert.False(ReplacedElementSizing.ShouldUseObjectFallbackContent(objects[2]), "The innermost Acid2 object should remain replaced because it carries the final eye image.");
        }

        [Fact]
        public async Task Acid2Smile_AbsoluteAutoWidthStaysWithinContainingBlock()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
   .smile div div { position: absolute; top: 0; right: 1em; width: auto; height: 0; margin: 0; border: yellow solid 1em; }
   .smile div div span { display: inline; margin: -1em 0 0 0; border: solid 1em transparent; border-style: none solid; float: right; background: black; height: 1em; }
   .smile div div span em { float: inherit; border-top: solid yellow 1em; border-bottom: solid black 1em; }
   .smile div div span em strong { width: 6em; display: block; margin-bottom: -1em; }
  </style>
</head>
<body>
  <div class='smile'><div id='smile-outer'><div id='smile-inner'><span><em><strong></strong></em></span></div></div></div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var outer = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-outer");
            var inner = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-inner");

            var outerBox = computer.GetBox(outer);
            var innerBox = computer.GetBox(inner);

            Assert.NotNull(outerBox);
            Assert.NotNull(innerBox);
            Assert.True(innerBox.ContentBox.Width <= outerBox.ContentBox.Width, $"Expected abs auto width to shrink within the smile container, got inner={innerBox.ContentBox.Width} outer={outerBox.ContentBox.Width}.");
            Assert.True(innerBox.BorderBox.Left >= outerBox.ContentBox.Left - 1f, $"Expected smile abs box to stay inside the containing block, got inner left {innerBox.BorderBox.Left} outer left {outerBox.ContentBox.Left}.");
        }

        [Fact]
        public async Task Acid2Smile_RelativeBottomShiftStaysNearOneEm()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
   .smile div div { position: absolute; top: 0; right: 1em; width: auto; height: 0; margin: 0; border: yellow solid 1em; }
   .smile div div span { display: inline; margin: -1em 0 0 0; border: solid 1em transparent; border-style: none solid; float: right; background: black; height: 1em; }
   .smile div div span em { float: inherit; border-top: solid yellow 1em; border-bottom: solid black 1em; }
   .smile div div span em strong { width: 6em; display: block; margin-bottom: -1em; }
  </style>
</head>
<body>
  <div id='smile' class='smile'><div id='smile-outer'><div id='smile-inner'><span><em><strong></strong></em></span></div></div></div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var smile = doc.Descendants().OfType<Element>().First(e => e.Id == "smile");
            var outer = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-outer");

            var smileBox = FindBox(rootBox, smile);
            var outerBox = FindBox(rootBox, outer);

            Assert.NotNull(smileBox);
            Assert.NotNull(outerBox);

            float offset = outerBox.Geometry.BorderBox.Top - smileBox.Geometry.ContentBox.Top;
            Assert.InRange(
                offset,
                10f,
                20f);
        }

        [Fact]
        public async Task Acid2Smile_NestedFloatStack_BlockifiesInlineSmileSegments()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
   .smile div div { position: absolute; top: 0; right: 1em; width: auto; height: 0; margin: 0; border: yellow solid 1em; }
   .smile div div span { display: inline; margin: -1em 0 0 0; border: solid 1em transparent; border-style: none solid; float: right; background: black; height: 1em; }
   .smile div div span em { float: inherit; border-top: solid yellow 1em; border-bottom: solid black 1em; }
   .smile div div span em strong { width: 6em; display: block; margin-bottom: -1em; }
  </style>
</head>
<body>
  <div class='smile'><div id='smile-outer'><div id='smile-inner'><span id='smile-span'><em id='smile-em'><strong></strong></em></span></div></div></div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var smileInner = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-inner");
            var smileSpan = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-span");
            var smileEm = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-em");

            var innerBox = computer.GetBox(smileInner);
            var spanBox = computer.GetBox(smileSpan);
            var emBox = computer.GetBox(smileEm);

            Assert.NotNull(innerBox);
            Assert.NotNull(spanBox);
            Assert.NotNull(emBox);
            Assert.True(spanBox.BorderBox.Height >= 30f, $"Expected the floated smile span to blockify and include its border-driven height, got {spanBox.BorderBox}.");
            Assert.True(emBox.BorderBox.Height >= 20f, $"Expected the inherited floated <em> to establish the nested smile stroke, got {emBox.BorderBox}.");
            Assert.True(spanBox.BorderBox.Right <= innerBox.ContentBox.Right + 1f, $"Expected the floated smile span to stay inside the absolute smile container, got span={spanBox.BorderBox} inner={innerBox.ContentBox}.");
        }


        [Fact]
        public async Task Acid2Eyes_AbsoluteAutoWidthIgnoresFloatDisplacementWhenShrinkWrapping()
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
   #eyes-b { float: left; width: 10em; height: 2em; background: yellow; border-left: solid 1em black; border-right: solid 1em red; }
   #eyes-c { display: block; background: red; border-left: 2em solid yellow; width: 10em; height: 2em; }
  </style>
</head>
<body>
  <div id='eyes' class='eyes'>
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

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var eyes = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes");
            var eyesA = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-a");
            var eyesB = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-b");
            var eyesC = doc.Descendants().OfType<Element>().First(e => e.Id == "eyes-c");

            var eyesBox = FindBox(rootBox, eyes);
            var eyesABox = FindBox(rootBox, eyesA);
            var eyesBBox = FindBox(rootBox, eyesB);
            var eyesCBox = FindBox(rootBox, eyesC);

            Assert.NotNull(eyesBox);
            Assert.NotNull(eyesABox);
            Assert.NotNull(eyesBBox);
            Assert.NotNull(eyesCBox);

            Assert.True(
                eyesBox.Geometry.ContentBox.Width >= 140f && eyesBox.Geometry.ContentBox.Width <= 156f,
                $"Expected Acid2 eyes to shrink-wrap near a single eye-band width, got eyes={eyesBox.Geometry.ContentBox} eyes-a={eyesABox.Geometry.MarginBox} eyes-b={eyesBBox.Geometry.MarginBox} eyes-c={eyesCBox.Geometry.MarginBox}.");
            Assert.True(eyesCBox.Geometry.MarginBox.Left <= eyesBBox.Geometry.MarginBox.Left + 1f,
                $"Expected shrink-wrap width to ignore float displacement, got eyes={eyesBox.Geometry.ContentBox} eyes-b={eyesBBox.Geometry.MarginBox} eyes-c={eyesCBox.Geometry.MarginBox}.");
            Assert.True(Math.Abs(eyesCBox.Geometry.MarginBox.Top - eyesBBox.Geometry.MarginBox.Top) <= 1f,
                $"Expected the block eye layer to remain in normal-flow top alignment under the float, got eyes-b={eyesBBox.Geometry.MarginBox} eyes-c={eyesCBox.Geometry.MarginBox}.");
        }

        [Fact]
        public void NestedFloatIntrusion_UsesLocalFormattingContextCoordinates()
        {
            var doc = new Document();
            var container = new Element("DIV");
            var floated = new Element("DIV");
            var follower = new Element("DIV");
            doc.AppendChild(container);
            container.AppendChild(floated);
            container.AppendChild(follower);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "block",
                    Width = 200,
                    Margin = new FenBrowser.Core.Thickness(100, 0, 0, 0)
                },
                [floated] = new CssComputed
                {
                    Display = "block",
                    Float = "left",
                    Width = 50,
                    Height = 20
                },
                [follower] = new CssComputed
                {
                    Display = "block",
                    Width = 200,
                    Height = 20
                }
            };

            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var floatBox = computer.GetBox(floated);
            var followerBox = computer.GetBox(follower);

            Assert.NotNull(followerBox);
            var floatText = floatBox is null ? "<null>" : floatBox.MarginBox.ToString();
            Assert.True(
                Math.Abs(followerBox.BorderBox.Top - 20f) <= 0.5f,
                $"Expected the follower block to keep local formatting-context coordinates instead of being pushed by absolute float coordinates. Float={floatText}, follower={followerBox.BorderBox}.");
        }

        [Fact]
        public async Task Acid2UpperFrame_AbsoluteAutoWidthShrinkWrapsAroundRightFloat()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   .picture { position: relative; border: 1em solid transparent; margin: 0 0 0 3em; }
   [class~=one].first.one { position: absolute; top: 0; margin: 36px 0 0 60px; padding: 0; border: black 2em; border-style: none solid; }
   [class~=one][class~=first] [class=second\ two][class=""second two""] { float: right; width: 48px; height: 12px; background: yellow; margin: 0; padding: 0; }
  </style>
</head>
<body>
  <div class='picture'>
    <blockquote id='frame' class='first one'><address id='bar' class='second two'></address></blockquote>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var frame = doc.Descendants().OfType<Element>().First(e => e.Id == "frame");
            var bar = doc.Descendants().OfType<Element>().First(e => e.Id == "bar");

            var frameBox = FindBox(rootBox, frame);
            var barBox = FindBox(rootBox, bar);

            Assert.NotNull(frameBox);
            Assert.NotNull(barBox);

            Assert.InRange(barBox.Geometry.MarginBox.Width, 47f, 49f);
            Assert.InRange(barBox.Geometry.MarginBox.Height, 11f, 13f);
            Assert.InRange(frameBox.Geometry.ContentBox.Width, 48f, 52f);
            Assert.InRange(frameBox.Geometry.MarginBox.Width, 95f, 100f);
        }

        [Fact]
        public async Task Acid2UpperFrame_FloatedAddressStaysInsideAbsoluteFrame()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   .picture { position: relative; border: 1em solid transparent; margin: 0 0 0 3em; }
   [class~=one].first.one { position: absolute; top: 0; margin: 36px 0 0 60px; padding: 0; border: black 2em; border-style: none solid; }
   [class~=one][class~=first] [class=second\ two][class=""second two""] { float: right; width: 48px; height: 12px; background: yellow; margin: 0; padding: 0; }
  </style>
</head>
<body>
  <div class='picture'>
    <blockquote id='frame' class='first one'><address id='bar' class='second two'></address></blockquote>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var frame = doc.Descendants().OfType<Element>().First(e => e.Id == "frame");
            var bar = doc.Descendants().OfType<Element>().First(e => e.Id == "bar");

            var frameBox = FindBox(rootBox, frame);
            var barBox = FindBox(rootBox, bar);

            Assert.NotNull(frameBox);
            Assert.NotNull(barBox);
            Assert.True(
                barBox.Geometry.MarginBox.Left >= frameBox.Geometry.ContentBox.Left - 0.5f &&
                barBox.Geometry.MarginBox.Right <= frameBox.Geometry.ContentBox.Right + 0.5f,
                $"Expected the floated Acid2 address to stay inside the absolute frame content box, got frame={frameBox.Geometry.ContentBox} address={barBox.Geometry.MarginBox}.");
            Assert.True(
                barBox.Geometry.MarginBox.Top >= frameBox.Geometry.BorderBox.Top - 0.5f &&
                barBox.Geometry.MarginBox.Bottom <= frameBox.Geometry.BorderBox.Bottom + 0.5f,
                $"Expected the floated Acid2 address to remain vertically inside the absolute frame, got frame={frameBox.Geometry.BorderBox} address={barBox.Geometry.MarginBox}.");
        }

        [Fact]
        public async Task Acid2TableTail_UlDisplayTableFormsSingleHorizontalRow()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   ul { display: table; padding: 0; margin: 0; background: red; }
   ul li { padding: 0; margin: 0; }
   ul li.first-part { display: table-cell; height: 1em; width: 1em; background: black; }
   ul li.second-part { display: table; height: 1em; width: 1em; background: black; }
   ul li.third-part { display: table-cell; height: 0.5em; width: 1em; background: black; }
   ul li.fourth-part { list-style: none; height: 1em; width: 1em; background: black; }
  </style>
</head>
<body>
  <ul id='tail'>
    <li id='one' class='first-part'></li>
    <li id='two' class='second-part'></li>
    <li id='three' class='third-part'></li>
    <li id='four' class='fourth-part'></li>
  </ul>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var ul = doc.Descendants().OfType<Element>().First(e => e.Id == "tail");
            var one = doc.Descendants().OfType<Element>().First(e => e.Id == "one");
            var two = doc.Descendants().OfType<Element>().First(e => e.Id == "two");
            var three = doc.Descendants().OfType<Element>().First(e => e.Id == "three");
            var four = doc.Descendants().OfType<Element>().First(e => e.Id == "four");

            var ulBox = FindBox(rootBox, ul);
            var oneBox = FindBox(rootBox, one);
            var twoBox = FindBox(rootBox, two);
            var threeBox = FindBox(rootBox, three);
            var fourBox = FindBox(rootBox, four);

            Assert.NotNull(ulBox);
            Assert.NotNull(oneBox);
            Assert.NotNull(twoBox);
            Assert.NotNull(threeBox);
            Assert.NotNull(fourBox);

            Assert.True(ulBox.Geometry.ContentBox.Width <= 60f, $"Expected Acid2 tail table to shrink-wrap near 4em, got {ulBox.Geometry.ContentBox.Width}.");
            Assert.True(Math.Abs(oneBox.Geometry.MarginBox.Top - twoBox.Geometry.MarginBox.Top) < 0.5f, "Expected first two cells on the same row.");
            Assert.True(Math.Abs(twoBox.Geometry.MarginBox.Top - threeBox.Geometry.MarginBox.Top) < 0.5f, "Expected third cell on the same row.");
            Assert.True(Math.Abs(threeBox.Geometry.MarginBox.Top - fourBox.Geometry.MarginBox.Top) < 0.5f, "Expected fourth cell on the same row.");
            Assert.True(oneBox.Geometry.MarginBox.Left < twoBox.Geometry.MarginBox.Left &&
                        twoBox.Geometry.MarginBox.Left < threeBox.Geometry.MarginBox.Left &&
                        threeBox.Geometry.MarginBox.Left < fourBox.Geometry.MarginBox.Left,
                "Expected Acid2 tail cells to advance horizontally, not stack vertically.");
        }

        [Fact]
        public async Task Acid2Nose_PercentageHeightFallsBackToMaxHeightInsideAutoHeightFace()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   .picture { position: relative; border: 1em solid transparent; margin: 0; }
   .nose { float: left; margin: -2em 2em -1em; border: solid 1em black; border-top: 0; min-height: 80%; height: 60%; max-height: 3em; padding: 0; width: 12em; }
   .nose > div { padding: 1em 1em 3em; height: 0; background: yellow; }
   .nose div div { width: 2em; height: 2em; background: red; margin: auto; }
  </style>
</head>
<body>
  <div class='picture'>
    <div id='nose' class='nose'><div><div></div></div></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var nose = doc.Descendants().OfType<Element>().First(e => e.Id == "nose");
            var noseBox = FindBox(rootBox, nose);

            Assert.NotNull(noseBox);
            Assert.InRange(noseBox.Geometry.ContentBox.Height, 20f, 30f);
        }

        [Fact]
        public async Task Acid2Forehead_NbspRunPreservesLineHeight()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   .picture { position: relative; border: 1em solid transparent; margin: 0; }
   .forehead { margin: 4em; width: 8em; border-left: solid black 1em; border-right: solid black 1em; background: yellow; }
   .forehead * { width: 12em; line-height: 1em; }
  </style>
</head>
<body>
  <div class='picture'>
    <div id='forehead' class='forehead'><div id='forehead-fill'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</div></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var forehead = doc.Descendants().OfType<Element>().First(e => e.Id == "forehead");
            var fill = doc.Descendants().OfType<Element>().First(e => e.Id == "forehead-fill");

            var foreheadBox = FindBox(rootBox, forehead);
            var fillBox = FindBox(rootBox, fill);

            Assert.NotNull(foreheadBox);
            Assert.NotNull(fillBox);
            Assert.True(fillBox.Geometry.MarginBox.Height >= 12f, $"Expected NBSP fill to preserve a 1em line box, got {fillBox.Geometry.MarginBox.Height}.");
            Assert.True(foreheadBox.Geometry.MarginBox.Height >= 12f, $"Expected Acid2 forehead to keep visible height, got {foreheadBox.Geometry.MarginBox.Height}.");
        }

        [Fact]
        public async Task Acid2Empty_PercentHeightFallsBackToAutoAndCollapsesThroughNegativeChildMargin()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   .picture { position: relative; border: 1em solid transparent; margin: 0; }
   .empty { margin: 6.25em; height: 10%; }
   .empty div { margin: 0 2em -6em 4em; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
  </style>
</head>
<body>
  <div class='picture'>
    <div id='empty' class='empty'><div></div></div>
    <div id='smile' class='smile'><div></div></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var empty = doc.Descendants().OfType<Element>().First(e => e.Id == "empty");
            var smile = doc.Descendants().OfType<Element>().First(e => e.Id == "smile");

            var emptyBox = FindBox(rootBox, empty);
            var smileBox = FindBox(rootBox, smile);

            Assert.NotNull(emptyBox);
            Assert.NotNull(smileBox);
            Assert.InRange(emptyBox.Geometry.ContentBox.Height, 0f, 1f);
            Assert.True(smileBox.Geometry.MarginBox.Top - emptyBox.Geometry.MarginBox.Top < 110f,
                $"Expected collapsed-through empty block to avoid a viewport-sized gap, got empty={emptyBox.Geometry.MarginBox} smile={smileBox.Geometry.MarginBox}.");
        }

        [Fact]
        public async Task Acid2Smile_ClearBoth_AcceptsNegativeClearanceFromPrecedingFloat()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; }
   body { margin: 0; padding: 0; }
   .picture { position: relative; border: 1em solid transparent; margin: 0; }
   .nose { float: left; margin: -2em 2em -1em; border: solid 1em black; border-top: 0; min-height: 80%; height: 60%; max-height: 3em; padding: 0; width: 12em; }
   .nose > div { padding: 1em 1em 3em; height: 0; background: yellow; }
   .nose div div { width: 2em; height: 2em; background: red; margin: auto; }
   .empty { margin: 6.25em; height: 10%; }
   .empty div { margin: 0 2em -6em 4em; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
  </style>
</head>
<body>
  <div class='picture'>
    <div id='nose' class='nose'><div><div></div></div></div>
    <div id='empty' class='empty'><div></div></div>
    <div id='smile' class='smile'><div></div></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 800, viewportHeight: 600);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var nose = doc.Descendants().OfType<Element>().First(e => e.Id == "nose");
            var smile = doc.Descendants().OfType<Element>().First(e => e.Id == "smile");

            var noseBox = FindBox(rootBox, nose);
            var smileBox = FindBox(rootBox, smile);

            Assert.NotNull(noseBox);
            Assert.NotNull(smileBox);
            Assert.True(
                smileBox.Geometry.MarginBox.Top - noseBox.Geometry.MarginBox.Top < 80f,
                $"Expected Acid2 smile to accept the negative clearance induced by the preceding floated nose, got nose={noseBox.Geometry.MarginBox} smile={smileBox.Geometry.MarginBox}.");
        }

        [Fact]
        public async Task Acid2Smile_CombinedFaceSlice_KeepsSmileStrokeNearAbsoluteAnchor()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style type='text/css'>
   html { font: 12px sans-serif; margin: 0; padding: 0; overflow: hidden; background: white; color: red; }
   body { margin: 0; padding: 0; }
   .eyes { line-height: 2em; }
   #eyes-a { height: 0; text-align: right; }
   #eyes-a object { display: inline; vertical-align: bottom; }
   #eyes-a object object object { border-right: solid 1em black; padding: 0 12px 0 11px; background: url(data:image/png;base64,AAAA) fixed 1px 0; }
   #eyes-b { float: left; width: 10em; height: 2em; border-left: solid 1em black; border-right: solid 1em red; }
   #eyes-c { display: block; border-left: 2em solid yellow; width: 10em; height: 2em; }
   .nose { margin: 1em 4em; border: solid black; border-width: 1em 1em 0; min-height: 80%; height: 60%; max-height: 3em; width: 12em; }
   .nose div { margin: 0 1em; height: 2em; }
   .nose div div { margin: 0; height: 2em; width: 2em; }
   .empty { float: left; width: 100%; height: 0; margin-top: -2em; }
   .smile { margin: 5em 3em; clear: both; }
   .smile div { margin-top: 0.25em; background: black; width: 12em; height: 2em; position: relative; bottom: -1em; }
   .smile div div { position: absolute; top: 0; right: 1em; width: auto; height: 0; margin: 0; border: yellow solid 1em; }
   .smile div div span { display: inline; margin: -1em 0 0 0; border: solid 1em transparent; border-style: none solid; float: right; background: black; height: 1em; }
   .smile div div span em { float: inherit; border-top: solid yellow 1em; border-bottom: solid black 1em; }
   .smile div div span em strong { width: 6em; display: block; margin-bottom: -1em; }
   .chin { margin: -4em 4em 0; width: 8em; line-height: 1em; border-left: solid 1em black; border-right: solid 1em black; }
   .chin div { display: inline; font: 2px/4px serif; }
  </style>
</head>
<body>
  <div class='eyes'>
    <div id='eyes-a'><object data='data:application/x-unknown,ERROR'><object data='http://www.damowmow.com/404/' type='text/html'><object data='data:image/png;base64,BBBB'></object></object></object></div>
    <div id='eyes-b'></div>
    <div id='eyes-c'></div>
  </div>
  <div class='nose'><div><div></div></div></div>
  <div class='empty'><div></div></div>
  <div class='smile'><div id='smile-outer'><div id='smile-inner'><span id='smile-span'><em id='smile-em'><strong></strong></em></span></div></div></div>
  <div class='chin'><div>&nbsp;</div></div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://acid2.acidtests.org/"));
            var doc = parser.Parse();
            var root = doc.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://acid2.acidtests.org/"), null, viewportWidth: 1920, viewportHeight: 927);

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            var state = new LayoutState(new SKSize(1920, 927), 1920, 927, 1920, 927);
            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var smileInner = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-inner");
            var smileSpan = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-span");
            var smileEm = doc.Descendants().OfType<Element>().First(e => e.Id == "smile-em");

            var innerBox = FindBox(rootBox, smileInner);
            var spanBox = FindBox(rootBox, smileSpan);
            var emBox = FindBox(rootBox, smileEm);

            Assert.NotNull(innerBox);
            Assert.NotNull(spanBox);
            Assert.NotNull(emBox);

            float spanOffset = spanBox.Geometry.BorderBox.Top - innerBox.Geometry.BorderBox.Top;
            float emOffset = emBox.Geometry.BorderBox.Top - innerBox.Geometry.BorderBox.Top;

            Assert.InRange(spanOffset, -2f, 14f);
            Assert.InRange(emOffset, -2f, 26f);
        }

        private static LayoutBox FindBox(LayoutBox root, Node node)
        {
            if (root == null || node == null)
            {
                return null;
            }

            if (ReferenceEquals(root.SourceNode, node))
            {
                return root;
            }

            foreach (var child in root.Children)
            {
                var match = FindBox(child, node);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
