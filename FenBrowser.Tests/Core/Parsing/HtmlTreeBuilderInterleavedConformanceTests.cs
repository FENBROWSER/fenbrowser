using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTreeBuilderInterleavedConformanceTests
    {
        public static IEnumerable<object[]> PathologicalMarkupCases()
        {
            yield return new object[]
            {
                "<!doctype html><html><body><table><tr><td>A<p>B<div>C</table><p>D</body></html>"
            };

            yield return new object[]
            {
                "<!doctype html><html><head><title>x</title><style>.a{color:red}</style></head><body><p>one<script>var a=1<2;</script><p>two</body></html>"
            };

            yield return new object[]
            {
                "<!doctype html><html><body><svg viewBox='0 0 100 100'><g><path d='M0 0 L10 10'/><rect width='10' height='10'></rect></g></svg><p>end</p></body></html>"
            };
        }

        [Theory]
        [MemberData(nameof(PathologicalMarkupCases))]
        public void Build_InterleavedMode_MatchesLegacyParserOutput(string html)
        {
            var baselineBuilder = new HtmlTreeBuilder(html)
            {
                ParseCheckpointTokenInterval = 4,
                InterleavedTokenBatchSize = 0
            };
            var interleavedBuilder = new HtmlTreeBuilder(html)
            {
                ParseCheckpointTokenInterval = 4,
                InterleavedTokenBatchSize = 7
            };

            var baselineDocument = baselineBuilder.Build();
            var interleavedDocument = interleavedBuilder.Build();

            Assert.NotNull(baselineDocument?.DocumentElement);
            Assert.NotNull(interleavedDocument?.DocumentElement);
            Assert.True(interleavedBuilder.LastBuildMetrics.UsedInterleavedBuild);
            Assert.True(interleavedBuilder.LastBuildMetrics.InterleavedBatchCount > 0);
            Assert.True(baselineDocument.IsEqualNode(interleavedDocument));
            Assert.Equal(baselineDocument.DocumentElement.OuterHTML, interleavedDocument.DocumentElement.OuterHTML);
            Assert.Equal(baselineDocument.TextContent, interleavedDocument.TextContent);
        }

        [Fact]
        public void Build_InterleavedMode_LargeDocumentPreservesOutputAcrossManyBatches()
        {
            var htmlBuilder = new StringBuilder();
            htmlBuilder.Append("<!doctype html><html><body><ul>");
            for (var i = 0; i < 1200; i++)
            {
                htmlBuilder.Append("<li data-i='").Append(i).Append("'>item-").Append(i).Append("</li>");
            }

            htmlBuilder.Append("</ul><table><tr><td>x<td>y</tr></table></body></html>");
            var html = htmlBuilder.ToString();

            var baselineBuilder = new HtmlTreeBuilder(html)
            {
                ParseCheckpointTokenInterval = 16,
                InterleavedTokenBatchSize = 0
            };
            var interleavedBuilder = new HtmlTreeBuilder(html)
            {
                ParseCheckpointTokenInterval = 16,
                InterleavedTokenBatchSize = 64
            };

            var baselineDocument = baselineBuilder.Build();
            var interleavedDocument = interleavedBuilder.Build();

            Assert.NotNull(baselineDocument?.DocumentElement);
            Assert.NotNull(interleavedDocument?.DocumentElement);
            Assert.True(interleavedBuilder.LastBuildMetrics.UsedInterleavedBuild);
            Assert.True(interleavedBuilder.LastBuildMetrics.InterleavedBatchCount > 1);
            Assert.True(baselineDocument.IsEqualNode(interleavedDocument));
            Assert.Equal(baselineDocument.DocumentElement.OuterHTML, interleavedDocument.DocumentElement.OuterHTML);
        }
    }
}
