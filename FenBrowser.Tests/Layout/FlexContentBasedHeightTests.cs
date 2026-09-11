using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// `height: fit-content` asks a box to size to its contents. The cascade parks
    /// it in HeightExpression, where EvaluateCssExpression resolves it against the
    /// containing block — the right shape for a width, but for a height it handed a
    /// shrink-to-fit box the whole containing block instead.
    ///
    /// github.com's masthead is exactly this: a `position: fixed` header with only
    /// `top` set, whose nav is a `height: fit-content` flex box. The nav claimed the
    /// full viewport height, the auto-height header grew to match, and the logo and
    /// the sign-in buttons were centred half way down the page instead of sitting in
    /// a 72px bar.
    /// </summary>
    public class FlexContentBasedHeightTests
    {
        private const float ViewportWidth = 1000f;
        private const float ViewportHeight = 800f;

        private static CssComputed ContentBasedHeightStyle()
        {
            // Mirror the cascade: the keyword lands in both the raw map and the
            // expression slot.
            var style = new CssComputed { Display = "flex", HeightExpression = "fit-content" };
            style.Map["height"] = "fit-content";
            style.Map["display"] = "flex";
            return style;
        }

        [Fact]
        public void FitContentHeightFlexBox_SizesToContent_NotToTheContainingBlock()
        {
            var nav = new Element("div");
            var item = new Element("div");
            item.AppendChild(new Text("Sign in"));
            nav.AppendChild(item);

            var header = new Element("header");
            header.AppendChild(nav);

            var root = new Element("div");
            root.AppendChild(header);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block" },
                // A fixed header with `top` but no `bottom`: height stays auto, so it
                // takes whatever its content asks for.
                [header] = new CssComputed { Display = "block", Position = "fixed", Top = 0 },
                [nav] = ContentBasedHeightStyle(),
                [item] = new CssComputed { Display = "block", Height = 32 }
            };

            foreach (var descendant in root.Descendants())
            {
                if (!styles.ContainsKey(descendant))
                {
                    styles[descendant] = new CssComputed();
                }
            }

            var boxes = LayoutTestHelper.LayoutTree(root, styles, ViewportWidth, ViewportHeight);

            float navHeight = boxes[nav].ContentBox.Height;
            Assert.True(
                navHeight < ViewportHeight / 2f,
                $"height:fit-content must size to content, but the nav took {navHeight} of a {ViewportHeight} viewport.");

            float headerHeight = boxes[header].ContentBox.Height;
            Assert.True(
                headerHeight < ViewportHeight / 2f,
                $"The auto-height fixed header follows its content, but it grew to {headerHeight}.");
        }
    }
}
