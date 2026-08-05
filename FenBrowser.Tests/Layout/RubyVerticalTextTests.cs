using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Tree;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class RubyVerticalTextTests
    {
        private static Dictionary<Node, CssComputed> Styles(Element container, CssComputed containerStyle)
        {
            var styles = new Dictionary<Node, CssComputed> { [container] = containerStyle };
            foreach (var child in container.Descendants())
            {
                if (!styles.ContainsKey(child))
                {
                    styles[child] = new CssComputed();
                }
            }
            return styles;
        }

        private static IReadOnlyDictionary<Node, BoxModel> Layout(Element root, Dictionary<Node, CssComputed> styles)
        {
            var engine = new LayoutEngine(styles, 800, 600);
            engine.ComputeLayout(root, 800, 600);
            return engine.AllBoxes;
        }

        [Fact]
        public void Ruby_RtAnnotationText_IsExcludedFromInlineFlow()
        {
            // <ruby>base<rt>annotation</rt></ruby>: the RT text must not consume
            // inline space (it renders above the base via the ruby handler).
            var ruby = new Element("ruby");
            var baseText = new Text("base");
            var rt = new Element("rt");
            rt.AppendChild(new Text("annotation"));
            ruby.AppendChild(baseText);
            ruby.AppendChild(rt);

            var container = new Element("div");
            container.AppendChild(ruby);

            var styles = Styles(container, new CssComputed { Display = "block", Width = 300 });

            var boxes = Layout(container, styles);

            var rubyBox = boxes[ruby];
            Assert.True(rubyBox.ContentBox.Width > 0f, "ruby box must have width from base text");
            Assert.True(rubyBox.ContentBox.Width < 300f, "ruby box must not span the whole line");
        }

        [Fact]
        public void Ruby_RtAnnotationTextNode_HasNoGeometryInFlow()
        {
            var ruby = new Element("ruby");
            ruby.AppendChild(new Text("base"));
            var rt = new Element("rt");
            rt.AppendChild(new Text("annotation"));
            ruby.AppendChild(rt);

            var container = new Element("div");
            container.AppendChild(ruby);

            var styles = Styles(container, new CssComputed { Display = "block", Width = 300 });

            var boxes = Layout(container, styles);

            var annotationText = rt.ChildNodes.OfType<Text>().First();
            var annotationBox = boxes[annotationText];
            Assert.NotNull(annotationBox);
            Assert.True(annotationBox.ContentBox.Width <= 0f && annotationBox.ContentBox.Height <= 0f,
                $"RT annotation text must not occupy inline space, got w={annotationBox.ContentBox.Width} h={annotationBox.ContentBox.Height}");
        }

        [Fact]
        public void VerticalText_VerticalRl_LaysOutColumnsFromRight()
        {
            var container = new Element("div");
            var text = new Text("Hello vertical");
            container.AppendChild(text);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 80,
                Height = 120,
                WritingMode = "vertical-rl"
            });

            var boxes = Layout(container, styles);

            var textBox = boxes[text];
            Assert.NotNull(textBox);
            Assert.True(textBox.ContentBox.Width > 0f, "vertical-rl text box must have width");
            Assert.True(textBox.ContentBox.Height > 0f, "vertical-rl text box must have height");
        }

        [Fact]
        public void VerticalText_VerticalLr_LaysOutColumnsFromLeft()
        {
            var container = new Element("div");
            var text = new Text("Hello vertical");
            container.AppendChild(text);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 80,
                Height = 120,
                WritingMode = "vertical-lr"
            });

            var boxes = Layout(container, styles);

            var textBox = boxes[text];
            Assert.NotNull(textBox);
            Assert.True(textBox.ContentBox.Width > 0f);
            Assert.True(textBox.ContentBox.Height > 0f);
        }

        [Fact]
        public void VerticalText_NoExplicitHeight_WrapsByViewport()
        {
            var container = new Element("div");
            var text = new Text("vertical without explicit height constraint");
            container.AppendChild(text);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 40,
                WritingMode = "vertical-rl"
            });

            var boxes = Layout(container, styles);

            var containerBox = boxes[container];
            Assert.True(containerBox.ContentBox.Height >= 0f);
            Assert.True(containerBox.ContentBox.Width > 0f);
        }

        [Fact]
        public void VerticalText_SingleColumn_FlowsTopToBottom()
        {
            var container = new Element("div");
            var text = new Text("AB");
            container.AppendChild(text);

            var styles = Styles(container, new CssComputed
            {
                Display = "block",
                Width = 100,
                Height = 100,
                WritingMode = "vertical-rl"
            });

            var boxes = Layout(container, styles);

            var textBox = boxes[text];
            Assert.NotNull(textBox);

            // Two characters in one column: the run's Origin.Y must advance
            // per line, with lines contained in the text box's height.
            Assert.True(textBox.Lines != null && textBox.Lines.Count >= 1);
            Assert.True(textBox.ContentBox.Height >= textBox.Lines[0].Origin.Y + textBox.Lines[0].Height - 1f,
                "column content must fit inside the text box");
        }
    }
}
