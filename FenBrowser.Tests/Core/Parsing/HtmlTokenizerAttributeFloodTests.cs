using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerAttributeFloodTests
{
    private readonly ITestOutputHelper _output;

    public HtmlTokenizerAttributeFloodTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void UniqueAttributeFlood_PreservesValuesWithIndexedLookup()
    {
        const int smallerAttributeCount = 8_000;
        const int largerAttributeCount = 16_000;
        string smallerInput = BuildStartTag(smallerAttributeCount);
        string largerInput = BuildStartTag(largerAttributeCount);

        Tokenize(BuildStartTag(32), 64, out _, out _);

        var smaller = Measure(smallerInput, largerAttributeCount + 1);
        var larger = Measure(largerInput, largerAttributeCount + 1);

        _output.WriteLine(
            $"{smallerAttributeCount:N0} attributes: {smaller.Elapsed.TotalMilliseconds:N2} ms, " +
            $"{smaller.AllocatedBytes:N0} B allocated.");
        _output.WriteLine(
            $"{largerAttributeCount:N0} attributes: {larger.Elapsed.TotalMilliseconds:N2} ms, " +
            $"{larger.AllocatedBytes:N0} B allocated.");

        Assert.Equal(smallerAttributeCount, smaller.AttributeCount);
        Assert.Equal(largerAttributeCount, larger.AttributeCount);
        Assert.Equal(HtmlParsingReasonCode.None, smaller.ReasonCode);
        Assert.Equal(HtmlParsingReasonCode.None, larger.ReasonCode);
    }

    [Fact]
    public void AttributeFlood_StopsRetainingOverflowAndReportsDegradedOutcome()
    {
        const int attributeCount = 16_000;
        const int attributeLimit = 128;
        string input = BuildStartTag(attributeCount);

        Tokenize(BuildStartTag(32), attributeLimit, out _, out _);
        var measurement = Measure(input, attributeLimit);

        _output.WriteLine(
            $"{attributeCount:N0} syntactic attributes with a {attributeLimit:N0}-attribute cap: " +
            $"{measurement.Elapsed.TotalMilliseconds:N2} ms, {measurement.AllocatedBytes:N0} B allocated.");

        Assert.Equal(attributeLimit, measurement.AttributeCount);
        Assert.Equal($"v{attributeLimit - 1}", measurement.FinalAttributeValue);
        Assert.Equal(HtmlParsingReasonCode.AttributeLimitExceeded, measurement.ReasonCode);
        Assert.InRange(measurement.AllocatedBytes, 1, 500_000);
    }

    [Fact]
    public void HtmlParser_PropagatesAttributeLimitAndKeepsDocumentUsable()
    {
        const int attributeLimit = 32;
        var parser = new HtmlParser(
            $"<!doctype html><body>{BuildStartTag(512)}</div><p id=tail>after</p></body>",
            securityPolicy: new ParserSecurityPolicy
            {
                HtmlMaxAttributesPerElement = attributeLimit
            });

        Document document = parser.Parse();
        var div = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.TagName, "DIV", StringComparison.OrdinalIgnoreCase));
        var tail = Assert.Single(
            document.Descendants().OfType<Element>(),
            element => string.Equals(element.GetAttribute("id"), "tail", StringComparison.Ordinal));

        Assert.Equal(attributeLimit, div.Attributes.Length);
        Assert.Equal($"v{attributeLimit - 1}", div.GetAttribute($"a{attributeLimit - 1}"));
        Assert.Null(div.GetAttribute($"a{attributeLimit}"));
        Assert.Equal("after", tail.TextContent);
        Assert.Equal(HtmlParsingOutcomeClass.Degraded, parser.LastParsingOutcome.OutcomeClass);
        Assert.Equal(HtmlParsingReasonCode.AttributeLimitExceeded, parser.LastParsingOutcome.ReasonCode);
    }

    [Fact]
    public void AttributeIndex_PreservesFirstCaseInsensitiveDuplicateValue()
    {
        var tokenizer = new HtmlTokenizer("<div Data-Key=first data-key=second other=third>")
        {
            MaxAttributesPerTag = 16
        };

        var startTag = Assert.Single(tokenizer.Tokenize().OfType<StartTagToken>());

        Assert.Equal(2, startTag.Attributes.Count);
        Assert.Equal("data-key", startTag.Attributes[0].Name);
        Assert.Equal("first", startTag.Attributes[0].Value);
        Assert.Equal("other", startTag.Attributes[1].Name);
        Assert.Equal("third", startTag.Attributes[1].Value);
        Assert.Equal(HtmlParsingReasonCode.None, tokenizer.LastReasonCode);
    }

    private static Measurement Measure(string input, int maxAttributesPerTag)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        int attributeCount = Tokenize(
            input,
            maxAttributesPerTag,
            out var finalAttributeValue,
            out var reasonCode);
        stopwatch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        return new Measurement(
            attributeCount,
            finalAttributeValue,
            reasonCode,
            stopwatch.Elapsed,
            allocatedBytes);
    }

    private static int Tokenize(
        string input,
        int maxAttributesPerTag,
        out string finalAttributeValue,
        out HtmlParsingReasonCode reasonCode)
    {
        var tokenizer = new HtmlTokenizer(input)
        {
            MaxAttributesPerTag = maxAttributesPerTag
        };
        var startTag = tokenizer.Tokenize().OfType<StartTagToken>().Single();

        finalAttributeValue = startTag.Attributes[^1].Value;
        reasonCode = tokenizer.LastReasonCode;
        return startTag.Attributes.Count;
    }

    private static string BuildStartTag(int attributeCount)
    {
        var input = new StringBuilder(attributeCount * 16);
        input.Append("<div");
        for (int index = 0; index < attributeCount; index++)
        {
            input.Append(" a").Append(index).Append("=v").Append(index);
        }
        input.Append('>');
        return input.ToString();
    }

    private readonly record struct Measurement(
        int AttributeCount,
        string FinalAttributeValue,
        HtmlParsingReasonCode ReasonCode,
        TimeSpan Elapsed,
        long AllocatedBytes);
}
