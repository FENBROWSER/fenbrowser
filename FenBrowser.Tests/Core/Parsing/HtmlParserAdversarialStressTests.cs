using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlParserAdversarialStressTests
{
    private readonly ITestOutputHelper _output;

    public HtmlParserAdversarialStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("a\0b", "ab")]
    [InlineData("\0ab", "ab")]
    [InlineData("ab\0", "ab")]
    [InlineData("a\0\0b", "ab")]
    public void EmbeddedNullInBody_IsIgnoredWithoutDroppingAdjacentText(
        string sourceText,
        string expectedText)
    {
        Document document = HtmlParser.ParseDocument($"<!doctype html><body><div>{sourceText}</div>");
        var div = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expectedText, div.TextContent);
        Assert.DoesNotContain('\0', div.TextContent);
    }

    [Theory]
    [InlineData("&\0auml;", "&auml;")]
    [InlineData("&a\0uml;", "&auml;")]
    [InlineData("&au\0ml;", "&auml;")]
    [InlineData("&aum\0l;", "&auml;")]
    [InlineData("&auml\0;", "\u00E4;")]
    [InlineData("&notin\0;", "\u00ACin;")]
    public void NullInsideTextNamedReference_MatchesLocalWpt(
        string sourceText,
        string expectedText)
    {
        Document document = HtmlParser.ParseDocument($"<!doctype html><body><div>{sourceText}</div>");
        var div = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "DIV", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expectedText, div.TextContent);
        Assert.DoesNotContain('\0', div.TextContent);
    }

    [Fact]
    public void NullAfterMarkupDeclarationHyphen_IsReplacedInCommentData()
    {
        Document document = HtmlParser.ParseDocument("<!doctype html><body><div><!-\0></div>");
        var div = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "DIV", StringComparison.OrdinalIgnoreCase));
        Comment comment = Assert.IsType<Comment>(div.FirstChild);

        Assert.Equal("-\uFFFD", comment.Data);
    }

    [Fact]
    public void NullInStandardComment_IsReplacedWithoutChangingAdjacentData()
    {
        CommentToken comment = Assert.Single(
            new HtmlTokenizer("<!--a\0b-->").Tokenize().OfType<CommentToken>());

        Assert.Equal("a\uFFFDb", comment.Data);
    }

    [Fact]
    public void ProcessingInstructionLikeMarkup_PreservesQuestionMarkInBogusComment()
    {
        CommentToken comment = Assert.Single(
            new HtmlTokenizer("<?target data>").Tokenize().OfType<CommentToken>());

        Assert.Equal("?target data", comment.Data);
    }

    [Theory]
    [InlineData("</\0>", "\uFFFD")]
    [InlineData("<?\0>", "?\uFFFD")]
    [InlineData("<!--a-\0b-->", "a-\uFFFDb")]
    [InlineData("<!--a--\0b-->", "a--\uFFFDb")]
    public void CommentNullRecovery_ReconsumesThroughBoundaryStates(
        string html,
        string expectedData)
    {
        CommentToken comment = Assert.Single(
            new HtmlTokenizer(html).Tokenize().OfType<CommentToken>());

        Assert.Equal(expectedData, comment.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveComments_ClearTokenizerOwnedBufferAfterEmission(bool useTokenPool)
    {
        var tokenPool = useTokenPool ? new HtmlTokenPool() : null;
        var tokenizer = tokenPool is null
            ? new HtmlTokenizer("<!--one--><!two><!--three-->")
            : new HtmlTokenizer("<!--one--><!two><!--three-->", tokenPool);
        string[] comments = tokenizer
            .Tokenize()
            .OfType<CommentToken>()
            .Select(comment => comment.Data)
            .ToArray();

        Assert.Equal(new[] { "one", "two", "three" }, comments);
    }

    [Theory]
    [InlineData("<!--", "")]
    [InlineData("<!--unterminated", "unterminated")]
    [InlineData("<!bogus", "bogus")]
    [InlineData("<?target", "?target")]
    [InlineData("</$name", "$name")]
    public void EofCommentRecovery_EmitsExactlyOneCommentThenEof(
        string html,
        string expectedData)
    {
        HtmlToken[] tokens = new HtmlTokenizer(html).Tokenize().ToArray();

        Assert.Equal(2, tokens.Length);
        CommentToken comment = Assert.IsType<CommentToken>(tokens[0]);
        Assert.Equal(expectedData, comment.Data);
        Assert.IsType<EofToken>(tokens[1]);
    }

    [Fact]
    public void EofCommentRecovery_ResetsReusedPooledCommentToken()
    {
        var tokenPool = new HtmlTokenPool();
        CommentToken firstComment = Assert.Single(
            new HtmlTokenizer("<!--stale", tokenPool).Tokenize().OfType<CommentToken>());

        Assert.Equal("stale", firstComment.Data);
        tokenPool.ResetAll();

        HtmlToken[] tokens = new HtmlTokenizer("<!--", tokenPool).Tokenize().ToArray();
        Assert.Equal(2, tokens.Length);
        CommentToken reusedComment = Assert.IsAssignableFrom<CommentToken>(tokens[0]);
        Assert.Same(firstComment, reusedComment);
        Assert.Equal(string.Empty, reusedComment.Data);
        Assert.IsType<EofToken>(tokens[1]);
    }

    [Fact]
    public void LongComment_AccumulatesWithinLinearAllocationBudget()
    {
        const int commentLength = 12_000;
        _ = new HtmlTokenizer("<!--warmup-->").Tokenize().ToArray();
        string input = $"<!--{new string('x', commentLength)}-->";

        long before = GC.GetAllocatedBytesForCurrentThread();
        CommentToken comment = Assert.Single(
            new HtmlTokenizer(input).Tokenize().OfType<CommentToken>());
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"{commentLength:N0}-character comment allocated {allocatedBytes:N0} bytes.");

        Assert.Equal(commentLength, comment.Data.Length);
        Assert.All(comment.Data, character => Assert.Equal('x', character));
        Assert.True(
            allocatedBytes < 1_000_000,
            $"Expected linear comment accumulation below 1,000,000 bytes; allocated {allocatedBytes:N0} bytes.");
    }

    [Fact]
    public void DataState_EmitsNullAsAnIndependentRecoveryToken()
    {
        string[] characterTokens = new HtmlTokenizer("a\0b")
            .Tokenize()
            .OfType<CharacterToken>()
            .Select(token => token.Data)
            .ToArray();

        Assert.Equal(new[] { "a", "\0", "b" }, characterTokens);
    }

    [Theory]
    [InlineData("title='a\0b'")]
    [InlineData("title=\"a\0b\"")]
    [InlineData("title=a\0b")]
    public void NullInAttributeValue_IsReplacedInEveryQuotingMode(string attributeSyntax)
    {
        Assert.Equal("a\uFFFDb", ParseSpanTitle(attributeSyntax));
    }

    [Theory]
    [InlineData("\0ab", "\uFFFDab")]
    [InlineData("ab\0", "ab\uFFFD")]
    [InlineData("a\0\0b", "a\uFFFD\uFFFDb")]
    public void NullAtAttributeValueBoundaries_IsReplacedPerCodePoint(
        string sourceValue,
        string expectedValue)
    {
        Assert.Equal(expectedValue, ParseSpanTitle($"title='{sourceValue}'"));
    }

    [Theory]
    [InlineData("&\0auml;", "&\uFFFDauml;")]
    [InlineData("&a\0uml;", "&a\uFFFDuml;")]
    [InlineData("&au\0ml;", "&au\uFFFDml;")]
    [InlineData("&aum\0l;", "&aum\uFFFDl;")]
    [InlineData("&auml\0;", "\u00E4\uFFFD;")]
    [InlineData("&notin\0;", "&notin\uFFFD;")]
    public void NullInsideNamedReference_MatchesLocalWpt(string sourceValue, string expectedValue)
    {
        Assert.Equal(expectedValue, ParseSpanTitle($"title='{sourceValue}'"));
    }

    [Theory]
    [InlineData("&notin ", "&notin ")]
    [InlineData("&notin;", "\u2209")]
    public void NamedReference_RequiresSemicolonUnlessLegacyAllowed(
        string sourceValue,
        string expectedValue)
    {
        Assert.Equal(expectedValue, ParseSpanTitle($"title='{sourceValue}'"));
    }

    [Fact]
    public void NullInIgnoredDuplicateAttribute_DoesNotDisruptFollowingMarkup()
    {
        Document document = HtmlParser.ParseDocument(
            "<!doctype html><body><span title='first' title='a\0b'>content</span><p id='tail'>tail</p>");
        Element span = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "SPAN", StringComparison.OrdinalIgnoreCase));
        Element paragraph = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.Id, "tail", StringComparison.Ordinal));

        Assert.Equal("first", span.GetAttribute("title"));
        Assert.Equal("content", span.TextContent);
        Assert.Equal("tail", paragraph.TextContent);
    }

    [Fact]
    public void MalformedRecoveryCorpus_PreservesDomOwnershipAndLinks()
    {
        var corpus = new (string Name, string Html)[]
        {
            ("orphan end tags", "</div></span></p>tail"),
            ("formatting adoption", "<p><b><i>one</b>two</i>three"),
            ("table foster parenting", "<table><b><tr><td>x</b>y</table>z"),
            ("select recovery", "<select><option>one<table><tr><td>x</select>tail"),
            ("broken comments", "<!----!><!-x><!bogus><div>tail"),
            ("missing raw text end", "<style>.a{content:'</script>'}<div>tail"),
            ("repeated recovery", BuildRecoveryStorm(500))
        };

        foreach (var (name, html) in corpus)
        {
            ParseAndValidate(html, name);
        }
    }

    [Fact]
    public void DeepNesting_IsBoundedWithoutCorruptingDomLinks()
    {
        const int nesting = 20_000;
        const int depthLimit = 128;
        var html = new StringBuilder((nesting * 11) + 64);
        html.Append("<!doctype html><body>");
        for (int index = 0; index < nesting; index++)
        {
            html.Append("<div>");
        }
        html.Append("payload");
        for (int index = 0; index < nesting; index++)
        {
            html.Append("</div>");
        }

        Document document = HtmlParser.ParseDocument(
            html.ToString(),
            new HtmlParserOptions
            {
                SecurityPolicy = CreateStressPolicy(depthLimit)
            },
            out HtmlParsingOutcome outcome);
        DomMetrics metrics = ValidateDom(document, "deep nesting");

        Assert.Equal(HtmlParsingOutcomeClass.Degraded, outcome.OutcomeClass);
        Assert.Equal(HtmlParsingReasonCode.OpenElementsDepthLimitExceeded, outcome.ReasonCode);
        Assert.True(metrics.NodeCount >= nesting, $"Expected all source elements to be represented; got {metrics.NodeCount} nodes.");
        Assert.True(
            metrics.MaxDepth <= depthLimit + 2,
            $"Expected bounded DOM depth <= {depthLimit + 2}, got {metrics.MaxDepth}.");
    }

    [Fact]
    public void InvalidUtf16Corpus_DoesNotBreakDomInvariants()
    {
        string[] invalidSequences =
        {
            "\uD800",
            "\uDC00",
            "x\uD800y\uDC00z",
            "\uD800\uD800\uDC00\uDC00",
            "\0\uD800\0\uDC00"
        };

        for (int index = 0; index < invalidSequences.Length; index++)
        {
            string value = invalidSequences[index];
            ParseAndValidate(
                $"<!doctype html><body><div data-v='{value}'><!--{value}-->{value}</div>",
                $"invalid UTF-16 case {index}");
        }
    }

    [Fact]
    public void SeededMutationCorpus_PreservesDomInvariants()
    {
        const int caseCount = 500;
        var random = new Random(0x5EED_262);
        for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
        {
            string html = BuildMutatedHtml(random, caseIndex);
            try
            {
                ParseAndValidate(html, $"mutation case {caseIndex}");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Mutation case {caseIndex} failed. Input code units: {FormatCodeUnits(html)}",
                    exception);
            }
        }
    }

    private static void ParseAndValidate(string html, string caseName)
    {
        Document document = HtmlParser.ParseDocument(
            html,
            new HtmlParserOptions
            {
                SecurityPolicy = CreateStressPolicy(maxDepth: 256)
            },
            out HtmlParsingOutcome outcome);

        Assert.NotEqual(HtmlParsingOutcomeClass.Failed, outcome.OutcomeClass);
        Assert.NotNull(document.DocumentElement);
        ValidateDom(document, caseName);
    }

    private static string ParseSpanTitle(string attributeSyntax)
    {
        Document document = HtmlParser.ParseDocument($"<!doctype html><body><span {attributeSyntax}></span>");
        var span = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "SPAN", StringComparison.OrdinalIgnoreCase));
        return span.GetAttribute("title");
    }

    private static ParserSecurityPolicy CreateStressPolicy(int maxDepth)
    {
        return new ParserSecurityPolicy
        {
            HtmlMaxTokenEmissions = 200_000,
            HtmlMaxAttributesPerElement = 512,
            HtmlMaxOpenElementsDepth = maxDepth
        };
    }

    private static DomMetrics ValidateDom(Document document, string caseName)
    {
        Assert.NotNull(document);
        Assert.Null(document.ParentNode);
        Assert.Same(document, document.OwnerDocument);

        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(Node Node, int Depth)>();
        pending.Push((document, 0));
        int maxDepth = 0;

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            Assert.True(seen.Add(node), $"{caseName}: node was reachable more than once or formed a cycle.");
            Assert.Same(document, node.OwnerDocument);
            maxDepth = Math.Max(maxDepth, depth);

            NodeList children = node.ChildNodes;
            for (int index = 0; index < children.Length; index++)
            {
                Node child = children[index];
                Assert.NotNull(child);
                Assert.Same(node, child.ParentNode);

                if (index == 0)
                {
                    Assert.Null(child.PreviousSibling);
                }
                else
                {
                    Assert.Same(children[index - 1], child.PreviousSibling);
                }

                if (index == children.Length - 1)
                {
                    Assert.Null(child.NextSibling);
                }
                else
                {
                    Assert.Same(children[index + 1], child.NextSibling);
                }

                pending.Push((child, depth + 1));
            }
        }

        return new DomMetrics(seen.Count, maxDepth);
    }

    private static string BuildRecoveryStorm(int repetitions)
    {
        var html = new StringBuilder(repetitions * 64);
        for (int index = 0; index < repetitions; index++)
        {
            html.Append("</p><li><table><tr><td><b><i>x</table></select>");
        }
        return html.ToString();
    }

    private static string BuildMutatedHtml(Random random, int caseIndex)
    {
        const string punctuation = "<>/&=\"'!-[]{}() \t\r\n";
        var html = new StringBuilder(640);
        html.Append("<!doctype html><body data-case=").Append(caseIndex).Append('>');

        for (int index = 0; index < 512; index++)
        {
            int choice = random.Next(100);
            if (choice < 55)
            {
                html.Append((char)('a' + random.Next(26)));
            }
            else if (choice < 82)
            {
                html.Append(punctuation[random.Next(punctuation.Length)]);
            }
            else if (choice < 90)
            {
                html.Append('\0');
            }
            else if (choice < 95)
            {
                html.Append((char)random.Next(0xD800, 0xDC00));
            }
            else
            {
                html.Append((char)random.Next(0xDC00, 0xE000));
            }
        }

        html.Append("</body>");
        return html.ToString();
    }

    private static string FormatCodeUnits(string value)
    {
        return string.Join(" ", value.Select(character => $"{(int)character:X4}"));
    }

    private readonly record struct DomMetrics(int NodeCount, int MaxDepth);
}
