using System;
using System.Text;
using FenBrowser.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class HtmlTreeBuilderStackSearchAllocationTests
{
    private readonly ITestOutputHelper _output;

    public HtmlTreeBuilderStackSearchAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void OrdinaryEndTags_AvoidStackSearchIteratorAllocations()
    {
        const int elementCount = 2_000;
        var source = new StringBuilder(elementCount * 11 + 32);
        source.Append("<!doctype html><html><body>");
        for (var index = 0; index < elementCount; index++)
        {
            source.Append("<div></div>");
        }
        source.Append("</body></html>");
        var html = source.ToString();

        GC.KeepAlive(HtmlParser.ParseDocument("<div></div>"));

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var document = HtmlParser.ParseDocument(html);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        _output.WriteLine($"Parsing {elementCount:N0} ordinary elements allocated {allocated:N0} B.");
        Assert.NotNull(document.Body);
        Assert.InRange(allocated, 1, 1_600_000);
    }
}
