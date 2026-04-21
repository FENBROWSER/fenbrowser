using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class CanonicalHtmlParserEntrypointTests
    {
        [Fact]
        public void ParseFragment_ContextElement_Path_ProducesFragmentWithoutHtmlBodyWrappers()
        {
            var document = Document.CreateHtmlDocument();
            var context = document.CreateElement("div");

            var fragment = HtmlParser.ParseFragment(context, "<span id='x'>ok</span><!--tail-->", options: null, out var outcome);

            Assert.Equal(HtmlParsingOutcomeClass.Success, outcome.OutcomeClass);
            Assert.NotNull(fragment.FirstChild);
            Assert.Equal("span", (fragment.FirstChild as Element)?.LocalName);
            Assert.Equal("ok", (fragment.FirstChild as Element)?.TextContent);
            Assert.IsType<Comment>(fragment.FirstChild?.NextSibling);
            Assert.Null(fragment.Descendants().OfType<Element>().FirstOrDefault(e => e.LocalName == "html"));
            Assert.Null(fragment.Descendants().OfType<Element>().FirstOrDefault(e => e.LocalName == "body"));
        }

        [Fact]
        public void ParseDocument_ReportsDeterministicOutcome_WhenTokenLimitTriggers()
        {
            var html = "<!doctype html><html><body><div><span><b>hello</b></span></div></body></html>";
            var options = new HtmlParserOptions
            {
                SecurityPolicy = new ParserSecurityPolicy
                {
                    HtmlMaxTokenEmissions = 3,
                    HtmlMaxOpenElementsDepth = 128
                }
            };

            var _ = HtmlParser.ParseDocument(html, options, out var outcome);

            Assert.Equal(HtmlParsingOutcomeClass.Degraded, outcome.OutcomeClass);
            Assert.Equal(HtmlParsingReasonCode.TokenEmissionLimitExceeded, outcome.ReasonCode);
        }

        [Fact]
        public async Task ParseStream_AndStreamingHtmlParser_ParseAsync_UseCanonicalDocumentConstruction()
        {
            const string html = "<!doctype html><html><body><article><p>stream</p></article></body></html>";

            using var reader = new StringReader(html);
            var fromStream = HtmlParser.ParseStream(reader, options: null, out var streamOutcome);
            Assert.Equal(HtmlParsingOutcomeClass.Success, streamOutcome.OutcomeClass);

            using var streaming = new StreamingHtmlParser(html);
            var fromStreaming = await streaming.ParseAsync();

            Assert.Equal(fromStream.DocumentElement?.OuterHTML, fromStreaming.DocumentElement?.OuterHTML);
            Assert.Equal(fromStream.TextContent, fromStreaming.TextContent);
        }
    }
}
