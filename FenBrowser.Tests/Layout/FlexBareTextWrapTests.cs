using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// A bare text node that becomes a flex item is laid out as a box in its own
    /// right rather than as a child of an inline formatting context. That leaf path
    /// used to measure the whole run as one max-content line, so the text overflowed
    /// its container instead of wrapping and the container stayed one line tall.
    /// Bing search snippets are exactly this shape: a display:flex paragraph whose
    /// only child is text.
    /// </summary>
    public class FlexBareTextWrapTests
    {
        private const string Sentence =
            "The quick brown fox jumps over the lazy dog while the sleepy cat " +
            "watches from a sunny windowsill nearby.";

        private const float ContainerWidth = 300f;

        private static float LayoutParagraphHeight(string display, string text)
        {
            var paragraph = new Element("p");
            paragraph.AppendChild(new Text(text));

            var root = new Element("div");
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = ContainerWidth },
                [paragraph] = new CssComputed { Display = display, Width = ContainerWidth }
            };

            foreach (var descendant in root.Descendants())
            {
                if (!styles.ContainsKey(descendant))
                {
                    styles[descendant] = new CssComputed();
                }
            }

            var boxes = LayoutTestHelper.LayoutTree(root, styles, ContainerWidth, 600);
            return boxes[paragraph].ContentBox.Height;
        }

        [Fact]
        public void BareTextInFlexContainer_WrapsInsteadOfOverflowing()
        {
            float singleLine = LayoutParagraphHeight("flex", "short");
            float wrapped = LayoutParagraphHeight("flex", Sentence);

            Assert.True(
                wrapped > singleLine,
                $"Text in a flex container must wrap: one line was {singleLine}, the long run was {wrapped}.");
        }

        [Fact]
        public void BareTextInFlexContainer_MatchesTheBlockContainerHeight()
        {
            // Chrome reports the same height for both: the flex item shrinks to the
            // container's main size and the run breaks at the same place.
            float blockHeight = LayoutParagraphHeight("block", Sentence);
            float flexHeight = LayoutParagraphHeight("flex", Sentence);

            Assert.Equal(blockHeight, flexHeight, 1);
        }

        [Fact]
        public void NowrapTextInFlexContainer_StaysOnOneLine()
        {
            // CSS Text 3: white-space:nowrap suppresses line breaking, so the run
            // overflows on a single line rather than wrapping.
            var paragraph = new Element("p");
            paragraph.AppendChild(new Text(Sentence));

            var root = new Element("div");
            root.AppendChild(paragraph);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = ContainerWidth },
                [paragraph] = new CssComputed
                {
                    Display = "flex",
                    Width = ContainerWidth,
                    WhiteSpace = "nowrap"
                }
            };

            foreach (var descendant in root.Descendants())
            {
                if (!styles.ContainsKey(descendant))
                {
                    styles[descendant] = new CssComputed { WhiteSpace = "nowrap" };
                }
            }

            var boxes = LayoutTestHelper.LayoutTree(root, styles, ContainerWidth, 600);
            float nowrapHeight = boxes[paragraph].ContentBox.Height;
            float singleLine = LayoutParagraphHeight("flex", "short");

            Assert.Equal(singleLine, nowrapHeight, 1);
        }
    }
}
