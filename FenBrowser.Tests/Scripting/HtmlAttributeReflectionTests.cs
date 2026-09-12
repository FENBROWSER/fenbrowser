using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Js.Runtime;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §2.6.1 reflected IDL attributes: the table maps each IDL name to its
/// content attribute with the right type and element applicability.
/// </summary>
public class HtmlAttributeReflectionTests
{
    private static JsValue Read(Element element, string property)
    {
        Assert.True(HtmlAttributeReflection.TryGetEntry(element, property, out var entry));
        return HtmlAttributeReflection.Read(element, entry, raw => raw);
    }

    private static void Write(Element element, string property, JsValue value)
    {
        Assert.True(HtmlAttributeReflection.TryGetEntry(element, property, out var entry));
        HtmlAttributeReflection.Write(
            element, entry, value,
            v => v.Tag == JsValueTag.String ? v.AsString() : v.ToString() ?? string.Empty,
            v => v.Tag == JsValueTag.Boolean ? v.AsBoolean() : v.Tag != JsValueTag.Undefined && v.Tag != JsValueTag.Null,
            v => v.Tag == JsValueTag.Number ? v.AsNumber() : 0);
    }

    [Fact]
    public void BooleanReflectsPresence()
    {
        var input = new Element("input");
        Assert.False(Read(input, "disabled").AsBoolean());

        Write(input, "disabled", JsValue.FromBoolean(true));
        Assert.True(input.HasAttribute("disabled"));
        Assert.True(Read(input, "disabled").AsBoolean());

        Write(input, "disabled", JsValue.FromBoolean(false));
        Assert.False(input.HasAttribute("disabled"));
    }

    [Fact]
    public void StringReflectsContentAttributeWithEmptyDefault()
    {
        var label = new Element("label");
        Assert.Equal(string.Empty, Read(label, "htmlFor").AsString());

        Write(label, "htmlFor", JsValue.FromString("jars"));
        Assert.Equal("jars", label.GetAttribute("for"));
        Assert.Equal("jars", Read(label, "htmlFor").AsString());
    }

    [Fact]
    public void PositiveLongDefaultsAndClamps()
    {
        var td = new Element("td");
        Assert.Equal(1, Read(td, "colSpan").AsNumber());

        td.SetAttribute("colspan", " 3 ");
        Assert.Equal(3, Read(td, "colSpan").AsNumber());

        td.SetAttribute("colspan", "0");
        Assert.Equal(1, Read(td, "colSpan").AsNumber());
    }

    [Fact]
    public void MaxLengthIsMinusOneWhenAbsent()
    {
        var textarea = new Element("textarea");
        Assert.Equal(-1, Read(textarea, "maxLength").AsNumber());
    }

    [Fact]
    public void PropertyOnlyAppliesToItsElements()
    {
        Assert.False(HtmlAttributeReflection.TryGetEntry(new Element("div"), "disabled", out _));
        Assert.True(HtmlAttributeReflection.TryGetEntry(new Element("div"), "hidden", out _));
    }
}
