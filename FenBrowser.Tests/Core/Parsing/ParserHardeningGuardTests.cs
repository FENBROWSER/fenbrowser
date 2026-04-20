using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class ParserHardeningGuardTests
    {
        [Fact]
        public void HtmlTokenizer_StopsAtConfiguredTokenEmissionLimit()
        {
            var tokenizer = new HtmlTokenizer(new string('a', 4096))
            {
                MaxTokenEmissions = 64
            };

            var tokens = tokenizer.Tokenize().ToList();

            Assert.NotEmpty(tokens);
            Assert.Equal(HtmlTokenType.EndOfFile, tokens[^1].Type);
            Assert.True(tokens.Count <= 65, $"Expected at most 65 tokens (64 + EOF), got {tokens.Count}.");
            Assert.Equal(HtmlParsingReasonCode.TokenEmissionLimitExceeded, tokenizer.LastReasonCode);
        }

        [Fact]
        public void HtmlTreeBuilder_ClampsOpenElementDepthForPathologicalNesting()
        {
            const int nesting = 1200;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><body>");
            for (int i = 0; i < nesting; i++) sb.Append("<div>");
            for (int i = 0; i < nesting; i++) sb.Append("</div>");
            sb.Append("</body></html>");

            var builder = new HtmlTreeBuilder(sb.ToString())
            {
                MaxOpenElementsDepth = 48
            };

            var doc = builder.Build();
            Assert.NotNull(doc.DocumentElement);

            int maxDepth = GetMaxDepth(doc.DocumentElement);
            Assert.True(maxDepth <= 64, $"Expected max element depth <= 64 after clamping, got {maxDepth}.");
        }

        [Fact]
        public void HtmlTokenizer_StopsWhenInputSizeExceedsLimit()
        {
            var tokenizer = new HtmlTokenizer(new string('x', 2048))
            {
                MaxInputLengthChars = 1024
            };

            var tokens = tokenizer.Tokenize().ToList();

            Assert.Single(tokens);
            Assert.Equal(HtmlTokenType.EndOfFile, tokens[0].Type);
            Assert.Equal(HtmlParsingReasonCode.InputSizeLimitExceeded, tokenizer.LastReasonCode);
        }

        [Fact]
        public void HtmlTreeBuilder_ReportsDegradedOutcome_WhenTokenizerLimitTriggers()
        {
            var builder = new HtmlTreeBuilder(new string('z', 2048))
            {
                MaxInputLengthChars = 1024
            };

            var doc = builder.Build();

            Assert.NotNull(doc);
            Assert.Equal(HtmlParsingOutcomeClass.Degraded, builder.LastParsingOutcome.OutcomeClass);
            Assert.Equal(HtmlParsingReasonCode.InputSizeLimitExceeded, builder.LastParsingOutcome.ReasonCode);
        }

        private static int GetMaxDepth(Element root)
        {
            int max = 0;
            var stack = new System.Collections.Generic.Stack<(Node node, int depth)>();
            stack.Push((root, 1));

            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (depth > max) max = depth;
                if (node.ChildNodes == null || node.ChildNodes.Length == 0) continue;

                for (int i = node.ChildNodes.Length - 1; i >= 0; i--)
                {
                    if (node.ChildNodes[i] is Element child)
                    {
                        stack.Push((child, depth + 1));
                    }
                }
            }

            return max;
        }
    }
}
