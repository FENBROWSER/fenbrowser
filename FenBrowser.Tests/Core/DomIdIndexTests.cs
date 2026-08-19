using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class DomIdIndexTests
{
    [Fact]
    public void DuplicateIdMutation_UpdatesOnlyAffectedBucket()
    {
        var document = Document.CreateHtmlDocument();
        var first = document.CreateElement("div");
        var second = document.CreateElement("div");
        first.Id = "duplicate";
        second.Id = "duplicate";
        document.Body.AppendChild(first);
        document.Body.AppendChild(second);

        Assert.Same(first, document.GetElementById("duplicate"));
        Assert.Equal(1, document.IdIndexFullRebuildCount);

        first.Id = "other";
        Assert.Same(second, document.GetElementById("duplicate"));
        Assert.Same(first, document.GetElementById("other"));
        Assert.Equal(1, document.IdIndexFullRebuildCount);
    }
}
