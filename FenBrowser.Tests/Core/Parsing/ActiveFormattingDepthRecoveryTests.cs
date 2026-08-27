using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class ActiveFormattingDepthRecoveryTests
{
    [Fact]
    public void DepthLimitDoesNotReconstructFormattingElementsItAutoClosed()
    {
        const int maxOpenDepth = 16;
        var markup = new StringBuilder("<!doctype html><html><body>");
        for (var index = 0; index < 100; index++)
        {
            markup.Append("<b data-depth=").Append(index).Append('>');
        }
        markup.Append("text</body></html>");

        var document = new HtmlTreeBuilder(markup.ToString())
        {
            MaxOpenElementsDepth = maxOpenDepth
        }.Build();

        Assert.True(
            GetMaximumElementDepth(document.DocumentElement) <= maxOpenDepth + 1,
            "Formatting entries removed by the safety depth limit must not be reconstructed on later tokens.");
        Assert.Equal("text", document.Body!.TextContent);
    }

    private static int GetMaximumElementDepth(Element root)
    {
        var maximum = 0;
        var pending = new Stack<(Element Element, int Depth)>();
        pending.Push((root, 1));

        while (pending.Count > 0)
        {
            var (element, depth) = pending.Pop();
            maximum = Math.Max(maximum, depth);
            foreach (var child in element.ChildNodes.OfType<Element>())
            {
                pending.Push((child, depth + 1));
            }
        }

        return maximum;
    }
}
