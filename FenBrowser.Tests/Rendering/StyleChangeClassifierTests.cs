using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    /// <summary>
    /// A restyle marks layout dirty only when it changed something boxes depend on;
    /// layout is a full document pass, so a class toggle that only recoloured an element
    /// (or matched no rule at all) cost a whole relayout at the next geometry read.
    /// </summary>
    public class StyleChangeClassifierTests
    {
        private static CssComputed Style(params (string Property, string Value)[] declarations)
        {
            var style = new CssComputed();
            foreach (var (property, value) in declarations)
            {
                style.Map[property] = value;
            }

            return style;
        }

        [Fact]
        public void IdenticalDeclarations_ChangeNothing()
        {
            Assert.Equal(StyleChange.None, StyleChangeClassifier.Classify(
                Style(("display", "block"), ("color", "red")),
                Style(("display", "block"), ("color", "red"))));
        }

        [Theory]
        [InlineData("color", "red", "blue")]
        [InlineData("background-color", "white", "black")]
        [InlineData("opacity", "1", "0.5")]
        [InlineData("visibility", "visible", "hidden")]
        [InlineData("box-shadow", "none", "0 0 2px black")]
        public void PaintOnlyChanges_NeedPaint(string property, string before, string after)
        {
            Assert.Equal(StyleChange.Paint, StyleChangeClassifier.Classify(
                Style(("display", "block"), (property, before)),
                Style(("display", "block"), (property, after))));
        }

        [Theory]
        [InlineData("display", "block", "none")]
        [InlineData("width", "10px", "20px")]
        [InlineData("font-size", "12px", "14px")]
        [InlineData("padding-left", "0px", "4px")]
        [InlineData("visibility", "visible", "collapse")]
        [InlineData("--gap", "4px", "8px")]
        public void GeometryChanges_NeedLayout(string property, string before, string after)
        {
            Assert.Equal(StyleChange.Layout, StyleChangeClassifier.Classify(
                Style((property, before)),
                Style((property, after))));
        }

        [Fact]
        public void AddedOrRemovedLayoutProperty_NeedsLayout()
        {
            Assert.Equal(StyleChange.Layout, StyleChangeClassifier.Classify(
                Style(("color", "red")),
                Style(("color", "red"), ("margin-top", "8px"))));
            Assert.Equal(StyleChange.Layout, StyleChangeClassifier.Classify(
                Style(("color", "red"), ("margin-top", "8px")),
                Style(("color", "red"))));
        }

        [Fact]
        public void GeneratedContentChange_NeedsLayout()
        {
            var before = Style(("display", "block"));
            before.Before = Style(("content", "\"a\""));
            var after = Style(("display", "block"));
            after.Before = Style(("content", "\"a longer label\""));

            Assert.Equal(StyleChange.Layout, StyleChangeClassifier.Classify(before, after));
        }

        [Fact]
        public void AttrContent_IsAlwaysTreatedAsLayout()
        {
            // attr() is read when boxes are built, so the attribute can change the text
            // while every declaration stays the same.
            var before = Style(("display", "block"));
            before.After = Style(("content", "attr(data-count)"));
            var after = Style(("display", "block"));
            after.After = Style(("content", "attr(data-count)"));

            Assert.Equal(StyleChange.Layout, StyleChangeClassifier.Classify(before, after));
        }

        [Fact]
        public void NewlyStyledElement_NeedsLayout()
        {
            Assert.Equal(StyleChange.Layout, StyleChangeClassifier.Classify(null, Style(("display", "block"))));
        }
    }
}
