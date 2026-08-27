using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class AdoptionAgencyLocalFixtureTests
{
    [Theory]
    [InlineData(
        "<a>1<p>2</a>3</p>",
        "<a>1</a><p><a>2</a>3</p>")]
    [InlineData(
        "<b>1<i>2<p>3</b>4",
        "<b>1<i>2</i></b><i><p><b>3</b>4</p></i>")]
    [InlineData(
        "<div><a><b><u><i><code><div></a>",
        "<div><a><b><u><i><code></code></i></u></b></a><u><i><code><div><a></a></div></code></i></u></div>")]
    public void MatchesLocalHtml5libAdoptionTrees(string markup, string expectedBodyHtml)
    {
        var document = HtmlParser.ParseDocument(markup);

        Assert.Equal(expectedBodyHtml, document.Body!.InnerHTML);
    }

    [Fact]
    public void ReplacementElementsRemainOwnedByTheParsedDocument()
    {
        var document = HtmlParser.ParseDocument("<div><a><b><u><i><code><div></a>");

        Assert.All(
            document.Body!.Descendants().OfType<Element>(),
            element =>
            {
                Assert.Same(document, element.OwnerDocument);
                Assert.True(element.SourceOffset >= 0, $"Replacement <{element.LocalName}> lost its source location.");
            });
    }
}
