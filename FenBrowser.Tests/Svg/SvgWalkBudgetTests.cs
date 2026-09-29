using System;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgWalkBudgetTests
    {
        [Fact]
        public void RenderDepthBudget_FailsClosedWithAnAdmissionReason()
        {
            // The render depth budget is independent of the parser's nesting budget,
            // so the document has to be admitted first to reach it. A browser draws
            // the whole subtree, so a frame with everything below the budget missing
            // is a frame a browser does not produce and must not be reported as one.
            using var result = new FenSvgRenderer().Render(
                NestedGroups(300),
                new SvgRenderLimits { MaxRecursionDepth = 400 });

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("render depth budget exceeded",
                    StringComparison.Ordinal));
            Assert.Contains("admission-budget", result.FallbackReasonCodes);
        }

        [Fact]
        public void RenderDepthWithinTheBudget_StillRenders()
        {
            // Control: the same shape under the budget is admitted and painted, so
            // the refusal above is the budget and not the document.
            using var result = new FenSvgRenderer().Render(
                NestedGroups(100),
                new SvgRenderLimits { MaxRecursionDepth = 400 });

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal(SKColors.Red, result.Bitmap.GetPixel(5, 5));
        }

        [Fact]
        public void UseCycle_FailsClosedWithAReferenceReason()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'><use id='a' href='#b'/>" +
                "<use id='b' href='#a'/></svg>");

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("use reference cycle", StringComparison.Ordinal));
            Assert.Contains("reference-resolution", result.FallbackReasonCodes);
        }

        [Theory]
        [InlineData("<rect id='n40' width='10' height='10' fill='red'/>")]
        [InlineData("<foreignObject id='n40' x='0' y='0' width='10' height='10'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'>x</div></foreignObject>")]
        public void UseDepthBudget_FailsClosedWithAnAdmissionReason(string leaf)
        {
            // Forty hops is past the default reference budget of 32, and the chain is
            // built from distinct ids so this is the depth budget rather than a cycle.
            // A browser instantiates all forty, so a frame holding only the ones
            // inside the budget is not a frame a browser produces. The foreignObject
            // case is the one that used to be lost without a reason code: the walk
            // stopped at the budget, never reached the leaf, and had nothing to name.
            var source = new StringBuilder("<svg width='10' height='10'>");
            for (int i = 0; i < 40; i++)
            {
                source.Append("<use id='n").Append(i).Append("' href='#n").Append(i + 1).Append("'/>");
            }
            source.Append(leaf).Append("</svg>");

            using var result = new FenSvgRenderer().Render(source.ToString());

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("use reference depth budget exceeded",
                    StringComparison.Ordinal));
            Assert.Contains("admission-budget", result.FallbackReasonCodes);
        }

        [Fact]
        public void DanglingUseReference_RendersNothingAndStillSucceeds()
        {
            // A reference that resolves to no element is a settled decision, not a
            // budget: the walk contributes nothing and the frame is the one a browser
            // paints.
            using var result = new FenSvgRenderer().Render(
                "<svg width='10' height='10'><use href='#missing'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Empty(result.FallbackReasonCodes);
        }

        [Fact]
        public void UseChainInsideTheReferenceBudget_StillReachesAndNamesItsForeignObject()
        {
            // Twenty hops is inside the default reference budget, so the walk
            // instantiates the whole chain and the foreignObject at the end of it is
            // reached and refused by its own decision. A chain that were instead cut
            // short by a budget now fails closed with the budget's reason, so no
            // foreignObject is lost without one; this covers the case the budgets do
            // not reach.
            var source = new StringBuilder("<svg width='20' height='20'>");
            for (int i = 0; i < 20; i++)
            {
                source.Append("<use id='h").Append(i).Append("' href='#h").Append(i + 1).Append("'/>");
            }
            source.Append("<foreignObject id='h20' x='0' y='0' width='10' height='10'>" +
                "<div xmlns='http://www.w3.org/1999/xhtml'>x</div></foreignObject></svg>");

            using var result = new FenSvgRenderer().Render(source.ToString());

            AssertFailsClosed(result);
            Assert.Contains(result.Warnings,
                warning => warning.Contains("foreignObject content", StringComparison.Ordinal));
        }

        private static string NestedGroups(int depth)
        {
            var source = new StringBuilder("<svg width='10' height='10'>");
            for (int i = 0; i < depth; i++) source.Append("<g>");
            source.Append("<rect width='10' height='10' fill='red'/>");
            for (int i = 0; i < depth; i++) source.Append("</g>");
            return source.Append("</svg>").ToString();
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.Equal(0f, result.Width);
            Assert.Equal(0f, result.Height);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Equal(SvgRendererBackend.FirstParty, result.Backend);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }
    }
}
