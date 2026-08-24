using System.Collections.Generic;
using Xunit;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using FenBrowser.Tests.Layout;
using SkiaSharp;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Regression tests for the Google /sorry CAPTCHA interstitial layout.
    ///
    /// Symptom: with <body><div><hr><br><form>…<div 304×78><iframe 304×78>…</form></div></body>,
    /// everything inside the <form> collapsed to zero-size boxes, so the reCAPTCHA
    /// anchor widget never rendered and clicks over it hit-tested to <body>.
    ///
    /// Minimal trigger isolated from the live page: an anonymous block containing
    /// a <br> followed by a block whose subtree contains an atomic replaced element.
    /// </summary>
    public class GoogleSorryPageLayoutRegressionTests
    {
        private static Element BuildCaptchaDom()
        {
            // Mirrors scripts/captcha_repro.html structure.
            var html = new Element("html");
            var body = new Element("body");
            var outer = new Element("div");          // max-width:400px
            var br = new Element("br");              // <br> after first <hr>
            var form = new Element("form");          // id=captcha-form
            var recaptcha = new Element("div");      // id=recaptcha class=g-recaptcha
            var sized = new Element("div");          // width:304px;height:78px
            var inner = new Element("div");
            var iframe = new Element("iframe");      // width=304 height=78 anchor

            html.AppendChild(body);
            body.AppendChild(outer);
            outer.AppendChild(new Text("\n"));
            outer.AppendChild(br);
            outer.AppendChild(new Text("\n"));
            outer.AppendChild(form);
            form.AppendChild(new Text("\n"));
            form.AppendChild(recaptcha);
            recaptcha.AppendChild(sized);
            sized.AppendChild(inner);
            inner.AppendChild(iframe);

            return html;
        }

        [Fact]
        public void BrBeforeFormContainingSizedReplacedElement_FormKeepsSize()
        {
            var html = BuildCaptchaDom();

            Element ByTag(string tag) => html.DescendantsAndSelf()
                .OfType<Element>()
                .First(e => string.Equals(e.TagName, tag, System.StringComparison.OrdinalIgnoreCase));

            var br = ByTag("BR");
            var form = ByTag("FORM");
            var iframe = ByTag("IFRAME");

            var sized = (Element)iframe.ParentNode;

            var styles = new Dictionary<Node, CssComputed>
            {
                [html] = new CssComputed { Display = "block" },
                [(Element)html.ChildNodes[0]] = new CssComputed { Display = "block" },
                [(Element)((Element)html.ChildNodes[0]).ChildNodes[0]] = new CssComputed { Display = "block", MaxWidth = 400 },
                [br] = new CssComputed { Display = "inline" },
                [form] = new CssComputed { Display = "block" },
                [(Element)form.ChildNodes[1]] = new CssComputed { Display = "block" },
                [sized] = new CssComputed { Display = "block", Width = 304, Height = 78 },
                [(Element)sized.ChildNodes[0]] = new CssComputed { Display = "block" },
                [iframe] = new CssComputed { Display = "inline-block", Width = 304, Height = 78 }
            };

            var engine = new LayoutEngine(styles, 1280, 800);
            engine.ComputeLayout(html, 1280, 800);
            var boxes = engine.AllBoxes;

            Assert.True(boxes.TryGetValue(form, out var formBox), "FORM must receive a layout box.");
            Assert.True(formBox.BorderBox.Height >= 78f,
                $"Expected FORM to be at least 78px tall (the sized widget wrapper), got {formBox.BorderBox.Height}.");

            Assert.True(boxes.TryGetValue(iframe, out var iframeBox), "IFRAME must receive a layout box.");
            Assert.Equal(304f, iframeBox.ContentBox.Width, 1);
            Assert.Equal(78f, iframeBox.ContentBox.Height, 1);
            Assert.False(float.IsNaN(formBox.BorderBox.Top), "FORM top must not be NaN.");
        }

        [Fact]
        public void BrInsideAnonymousBlock_LineHeightIsFinite()
        {
            // Directly exercise: anonymous block containing only a BR must produce
            // a finite line-box strut height, never NaN/Infinity, so subsequent
            // siblings keep flowing.
            var root = new Element("div");
            var br = new Element("br");
            var following = new Element("p");

            root.AppendChild(new Text("\n"));
            root.AppendChild(br);
            root.AppendChild(new Text("\n"));
            root.AppendChild(following);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 400 },
                [br] = new CssComputed { Display = "inline" },
                [following] = new CssComputed { Display = "block", Width = 100, Height = 20 }
            };

            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);

            string Describe(LayoutBox b, int depth)
            {
                var pad = new string(' ', depth * 2);
                var s = $"{pad}{b.GetType().Name} src={(b.SourceNode as Element)?.TagName ?? (b.SourceNode is Text t ? $"#text({t.Data?.Trim()})" : "null")}\n";
                foreach (var c in b.Children) s += Describe(c, depth + 1);
                return s;
            }
            var treeText = Describe(rootBox, 0);

            // Diagnostic: does the P element exist anywhere in the built tree?
            var pFound = FindDescendant(rootBox, following);
            Assert.True(rootBox.Children.Count == 2,
                $"Expected [AnonBlock(ws,BR,ws), P], got {rootBox.Children.Count}; rootType={rootBox.GetType().Name}; rootSrc={(rootBox.SourceNode as Element)?.TagName ?? "null"};\n{treeText}\nP found={pFound != null}");
            Assert.IsType<FenBrowser.FenEngine.Layout.Tree.AnonymousBlockBox>(rootBox.Children[0]);

            var state = new LayoutState(
                new SKSize(400, 600),
                400,
                600,
                400,
                600);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);

            var anon = rootBox.Children[0];
            var pBox = rootBox.Children[1];

            Assert.NotNull(anon.Geometry);
            Assert.False(float.IsNaN(anon.Geometry.ContentBox.Height),
                $"Anonymous BR block height must be finite, got {anon.Geometry.ContentBox.Height}.");
            Assert.True(float.IsFinite(pBox.Geometry.ContentBox.Top),
                $"Following sibling top must be finite, got {pBox.Geometry.ContentBox.Top}.");
            Assert.True(pBox.Geometry.ContentBox.Top >= anon.Geometry.ContentBox.Bottom - 0.5f,
                $"Following sibling must flow below the BR anonymous block: pTop={pBox.Geometry.ContentBox.Top} anonBottom={anon.Geometry.ContentBox.Bottom}.");
        }

        private static LayoutBox FindDescendant(LayoutBox box, Node source)
        {
            if (box == null) return null;
            if (ReferenceEquals(box.SourceNode, source)) return box;
            foreach (var child in box.Children)
            {
                var found = FindDescendant(child, source);
                if (found != null) return found;
            }
            return null;
        }
    }
}
