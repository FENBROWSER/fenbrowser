using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// Regression tests for LAYOUT-002: styled inline elements must fragment
    /// across line boxes instead of behaving as atomic units.
    /// </summary>
    public class InlineFragmentationTests
    {
        [Fact]
        public void StyledSpan_WithMultiWordText_WrapsAcrossLineBoxes()
        {
            var root = new Element("div");
            var paragraph = new Element("p");
            var span = new Element("span");
            var text = new Text("alpha beta gamma delta epsilon");

            span.AppendChild(text);
            paragraph.AppendChild(span);
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 140, Height = 200 },
                [paragraph] = new CssComputed
                {
                    Display = "block",
                    Width = 140,
                    FontSize = 13,
                    LineHeight = 16,
                    FontFamilyName = "Arial"
                },
                [span] = new CssComputed { Display = "inline" }
            };

            var rootBox = LayoutRoot(root, styles, 140, 200);
            var textBoxes = new List<TextLayoutBox>();
            CollectTextBoxes(rootBox, textBoxes);
            var textBox = FindTextBox(textBoxes, text);

            Assert.NotNull(textBox);
            Assert.True(
                textBox!.Geometry.Lines.Count >= 2,
                $"Expected styled inline text to fragment across lines, got {textBox.Geometry.Lines.Count}.");
            Assert.All(textBox.Geometry.Lines, line => Assert.True(line.Width <= 141f, $"Line exceeded container: '{line.Text}' width={line.Width}"));
        }

        [Fact]
        public void StyledSpan_KeepsWrapperGeometry_WhileFragmenting()
        {
            var root = new Element("div");
            var paragraph = new Element("p");
            var span = new Element("span");
            var text = new Text("one two three four five six seven");

            span.AppendChild(text);
            paragraph.AppendChild(span);
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 120, Height = 200 },
                [paragraph] = new CssComputed
                {
                    Display = "block",
                    Width = 120,
                    FontSize = 13,
                    LineHeight = 16,
                    FontFamilyName = "Arial"
                },
                [span] = new CssComputed { Display = "inline" }
            };

            var rootBox = LayoutRoot(root, styles, 120, 200);
            var spanBox = FindBox(rootBox, span);
            var textBoxes = new List<TextLayoutBox>();
            CollectTextBoxes(rootBox, textBoxes);
            var textBox = FindTextBox(textBoxes, text);

            Assert.NotNull(textBox);
            Assert.True(textBox!.Geometry.Lines.Count >= 2, $"Expected fragmented lines, got {textBox.Geometry.Lines.Count}.");
            Assert.NotNull(spanBox);
        }

        private static LayoutBox LayoutRoot(Element root, Dictionary<Node, CssComputed> styles, float width, float height)
        {
            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            Assert.NotNull(rootBox);

            var state = new LayoutState(
                new SKSize(width, height),
                width,
                height,
                width,
                height);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);
            return rootBox;
        }

        private static LayoutBox FindBox(LayoutBox box, Node target)
        {
            if (box == null)
            {
                return null;
            }

            if (ReferenceEquals(box.SourceNode, target))
            {
                return box;
            }

            foreach (var child in box.Children)
            {
                var found = FindBox(child, target);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void CollectTextBoxes(LayoutBox box, List<TextLayoutBox> result)
        {
            if (box == null)
            {
                return;
            }

            if (box is TextLayoutBox textBox)
            {
                result.Add(textBox);
            }

            foreach (var child in box.Children)
            {
                CollectTextBoxes(child, result);
            }
        }

        private static TextLayoutBox FindTextBox(IEnumerable<TextLayoutBox> textBoxes, Text text)
        {
            foreach (var box in textBoxes)
            {
                if (ReferenceEquals(box.SourceNode, text))
                {
                    return box;
                }
            }

            return null;
        }
    }
}
