using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class ResetInsertionModeTests
{
    [Theory]
    [InlineData("head", "InHead")]
    [InlineData("body", "InBody")]
    [InlineData("frameset", "InFrameset")]
    [InlineData("table", "InTable")]
    [InlineData("tbody", "InTableBody")]
    [InlineData("tr", "InRow")]
    [InlineData("td", "InCell")]
    [InlineData("caption", "InCaption")]
    [InlineData("colgroup", "InColumnGroup")]
    [InlineData("div", "InBody")]
    public void FragmentContextSelectsRequiredInsertionMode(string contextName, string expectedMode)
    {
        var document = Document.CreateHtmlDocument();
        var context = document.CreateElement(contextName);
        var builder = new HtmlTreeBuilder(string.Empty, context);

        Assert.Equal(expectedMode, GetInsertionMode(builder));
    }

    [Fact]
    public void HtmlContextWithoutHeadPointerSelectsBeforeHead()
    {
        var document = new Document();
        var context = document.CreateElement("html");
        var builder = new HtmlTreeBuilder(string.Empty, context);

        Assert.Equal("BeforeHead", GetInsertionMode(builder));
    }

    [Fact]
    public void HtmlContextWithHeadPointerSelectsAfterHead()
    {
        var document = new Document();
        var context = document.CreateElement("html");
        var builder = new HtmlTreeBuilder(string.Empty, context);
        var headField = typeof(HtmlTreeBuilder).GetField("_headElement", BindingFlags.NonPublic | BindingFlags.Instance);
        var resetMethod = typeof(HtmlTreeBuilder).GetMethod("ResetInsertionMode", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(headField);
        Assert.NotNull(resetMethod);
        headField.SetValue(builder, document.CreateElement("head"));
        resetMethod.Invoke(builder, null);

        Assert.Equal("AfterHead", GetInsertionMode(builder));
    }

    [Theory]
    [InlineData("select", "InSelect")]
    [InlineData("template", "InTemplate")]
    public void FragmentContextSelectsSpecializedInsertionMode(string contextName, string expectedMode)
    {
        var document = Document.CreateHtmlDocument();
        var context = document.CreateElement(contextName);
        var builder = new HtmlTreeBuilder(string.Empty, context);

        Assert.Equal(expectedMode, GetInsertionMode(builder));
    }

    [Fact]
    public void TemplateFragmentContextStartsInTemplateMode()
    {
        var document = Document.CreateHtmlDocument();
        var context = document.CreateElement("template");
        var builder = new HtmlTreeBuilder("<div>hi</div>", context);

        Assert.Equal("InTemplate", GetInsertionMode(builder));
    }

    [Fact]
    public void SelectFragmentContextSelectsInSelect()
    {
        var document = Document.CreateHtmlDocument();
        var select = document.CreateElement("select");

        var builder = new HtmlTreeBuilder("<option>x</option>", select);
        builder.BuildFragment();

        Assert.Equal("InSelect", GetInsertionMode(builder));
    }

    private static string GetInsertionMode(HtmlTreeBuilder builder)
    {
        var field = typeof(HtmlTreeBuilder).GetField("_insertionMode", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<string>(field.GetValue(builder)?.ToString());
    }
}
