using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class CssAnchorPositioningTests
    {
        [Fact]
        public void SolveWithAnchorOverrides_UsesAnchorOffsetsAndAnchorSize()
        {
            var containingBlock = new ContainingBlock
            {
                X = 100,
                Y = 50,
                Width = 600,
                Height = 400
            };

            var anchor = new SKRect(250, 150, 390, 260); // width=140, height=110
            var style = new CssComputed
            {
                Position = "absolute",
                PositionAnchor = "--card",
                LeftAnchorExpression = "anchor(--card right)",
                TopAnchorExpression = "anchor(--card bottom)",
                WidthAnchorExpression = "anchor-size(--card width)",
                HeightAnchorExpression = "anchor-size(--card height)"
            };

            var result = AbsolutePositionSolver.SolveWithAnchorOverrides(style, containingBlock, anchor);

            Assert.Equal(290f, result.X);
            Assert.Equal(210f, result.Y);
            Assert.Equal(140f, result.Width);
            Assert.Equal(110f, result.Height);
        }

        [Fact]
        public void ResolveAnchorOffsets_RightAndBottom_AreRelativeToContainingBlockEnd()
        {
            var containingBlock = new ContainingBlock
            {
                X = 100,
                Y = 50,
                Width = 600,
                Height = 400
            };

            var anchor = new SKRect(250, 150, 390, 260);
            var style = new CssComputed
            {
                PositionAnchor = "--card",
                RightAnchorExpression = "anchor(--card left)",
                BottomAnchorExpression = "anchor(--card top)"
            };

            float? left = null;
            float? top = null;
            float? right = null;
            float? bottom = null;
            float? width = null;
            float? height = null;

            AbsolutePositionSolver.ResolveAnchorOffsets(
                style,
                anchor,
                containingBlock,
                ref left,
                ref top,
                ref right,
                ref bottom,
                ref width,
                ref height);

            Assert.Null(left);
            Assert.Null(top);
            Assert.Equal(450f, right.Value);
            Assert.Equal(300f, bottom.Value);
            Assert.Null(width);
            Assert.Null(height);
        }

        [Fact]
        public void SolveWithAnchorOverrides_WithoutResolvedAnchorName_DoesNotOverrideExplicitInsets()
        {
            var containingBlock = new ContainingBlock
            {
                X = 0,
                Y = 0,
                Width = 500,
                Height = 300
            };

            var anchor = new SKRect(100, 80, 240, 180);
            var style = new CssComputed
            {
                Position = "absolute",
                Left = 20,
                Top = 10,
                Width = 50,
                Height = 40,
                // No position-anchor set, so anchor(left) cannot resolve a named anchor.
                LeftAnchorExpression = "anchor(left)"
            };

            var result = AbsolutePositionSolver.SolveWithAnchorOverrides(style, containingBlock, anchor);

            Assert.Equal(20f, result.X);
            Assert.Equal(10f, result.Y);
            Assert.Equal(50f, result.Width);
            Assert.Equal(40f, result.Height);
        }
    }
}
