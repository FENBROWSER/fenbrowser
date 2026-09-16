using System;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §4.2.4 (link.as) and §4.13.2 (CORS settings attributes). React sets both on
/// every preload link it creates for a chunk, so a bundle split into a hundred chunks
/// assigns them a hundred times before the first render.
/// </summary>
public class LinkPreloadReflectionTests
{
    private static Element Link()
    {
        var document = new Document();
        return document.CreateElement("link");
    }

    private static JsValue Read(Element element, string property)
    {
        Assert.True(HtmlAttributeReflection.TryGetEntry(element, property, out var entry),
            $"'{property}' is not a reflected property of <{element.LocalName}>.");
        return HtmlAttributeReflection.Read(element, entry, raw => raw);
    }

    private static void Write(Element element, string property, JsValue value)
    {
        Assert.True(HtmlAttributeReflection.TryGetEntry(element, property, out var entry),
            $"'{property}' is not a reflected property of <{element.LocalName}>.");
        HtmlAttributeReflection.Write(
            element,
            entry,
            value,
            v => v.Tag == JsValueTag.String ? v.AsString() : v.ToString(),
            v => v.AsBoolean(),
            v => v.AsNumber());
    }

    [Fact]
    public void AsReflectsTheContentAttributeBothWays()
    {
        var link = Link();
        Assert.Equal(string.Empty, Read(link, "as").AsString());

        Write(link, "as", JsValue.FromString("script"));
        Assert.Equal("script", link.GetAttribute("as"));
        Assert.Equal("script", Read(link, "as").AsString());
    }

    [Fact]
    public void AsIsNotReflectedOnUnrelatedElements()
    {
        var div = new Document().CreateElement("div");
        Assert.False(HtmlAttributeReflection.TryGetEntry(div, "as", out _));
    }

    // The distinguishing part of a CORS settings attribute: absent is null, not "".
    [Fact]
    public void CrossOriginIsNullWhenTheAttributeIsAbsent()
    {
        Assert.Equal(JsValueTag.Null, Read(Link(), "crossOrigin").Tag);
    }

    [Theory]
    [InlineData("", "anonymous")]
    [InlineData("anonymous", "anonymous")]
    [InlineData("ANONYMOUS", "anonymous")]
    [InlineData("use-credentials", "use-credentials")]
    [InlineData("USE-CREDENTIALS", "use-credentials")]
    // The invalid value default is the Anonymous state, so an unknown keyword
    // reflects as "anonymous" rather than as itself.
    [InlineData("garbage", "anonymous")]
    public void CrossOriginReflectsTheEnumeratedStates(string attribute, string expected)
    {
        var link = Link();
        link.SetAttribute("crossorigin", attribute);
        Assert.Equal(expected, Read(link, "crossOrigin").AsString());
    }

    [Fact]
    public void SettingCrossOriginStoresTheValueVerbatim()
    {
        var link = Link();
        Write(link, "crossOrigin", JsValue.FromString("use-credentials"));
        Assert.Equal("use-credentials", link.GetAttribute("crossorigin"));
        Assert.Equal("use-credentials", Read(link, "crossOrigin").AsString());
    }

    [Fact]
    public void SettingCrossOriginToNullRemovesTheContentAttribute()
    {
        var link = Link();
        link.SetAttribute("crossorigin", "anonymous");

        Write(link, "crossOrigin", JsValue.Null);

        Assert.False(link.HasAttribute("crossorigin"));
        Assert.Equal(JsValueTag.Null, Read(link, "crossOrigin").Tag);
    }

    [Theory]
    [InlineData("img")]
    [InlineData("script")]
    [InlineData("audio")]
    [InlineData("video")]
    public void CrossOriginIsReflectedOnTheOtherElementsThatCarryIt(string tag)
    {
        var element = new Document().CreateElement(tag);
        Assert.True(HtmlAttributeReflection.TryGetEntry(element, "crossOrigin", out _));
    }
}
