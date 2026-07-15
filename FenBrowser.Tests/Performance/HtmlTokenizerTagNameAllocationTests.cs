using System;
using System.Text;
using FenBrowser.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class HtmlTokenizerTagNameAllocationTests
{
    private const string TagName = "ordinarycustomtagnamewithlength";
    private readonly ITestOutputHelper _output;

    public HtmlTokenizerTagNameAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void OrdinaryTagNames_HaveBoundedTokenizerAllocations()
    {
        const int elementCount = 2_000;
        var source = new StringBuilder(elementCount * ((TagName.Length * 2) + 5));
        for (var index = 0; index < elementCount; index++)
        {
            source.Append('<').Append(TagName).Append("></").Append(TagName).Append('>');
        }

        string html = source.ToString();
        var pool = new HtmlTokenPool();
        ConsumeTags(html, pool, out _);
        pool.ResetAll();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int tagCount = ConsumeTags(html, pool, out bool allNamesMatch);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        _output.WriteLine($"Tokenizing {tagCount:N0} ordinary tags allocated {allocated:N0} B.");
        Assert.Equal(elementCount * 2, tagCount);
        Assert.True(allNamesMatch);
        Assert.InRange(allocated, 1, 380_000);
    }

    private static int ConsumeTags(string html, HtmlTokenPool pool, out bool allNamesMatch)
    {
        int tagCount = 0;
        allNamesMatch = true;
        foreach (var token in new HtmlTokenizer(html, pool).Tokenize())
        {
            if (token is not TagToken tag)
            {
                continue;
            }

            tagCount++;
            allNamesMatch &= string.Equals(tag.TagName, TagName, StringComparison.Ordinal);
        }

        return tagCount;
    }
}
