using System.Collections.Generic;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

/// <summary>
/// Scope boundary checks must recognize SVG names whose case is preserved by
/// the SVG tag adjustment (e.g. foreignObject), otherwise a malformed
/// foreign-content nesting lets scope walks continue past the boundary.
/// </summary>
public sealed class ScopeBoundaryCaseSensitivityTests
{
    [Fact]
    public void ButtonScopeWalkStopsAtCamelCaseForeignObject()
    {
        var builder = new HtmlTreeBuilder(string.Empty);
        var document = new Document();
        var html = document.CreateElement("html");
        var paragraph = document.CreateElement("p");
        var foreignObject = document.CreateElementNS(Namespaces.Svg, "foreignObject");
        var span = document.CreateElement("span");

        PushOpenElements(builder, html, paragraph, foreignObject, span);

        Assert.False(InvokeHasElementInButtonScope(builder, "p"),
            "foreignObject bounds the button scope, so <p> below it is not in scope.");
    }

    [Fact]
    public void ScopeWalkStopsAtCamelCaseForeignObject()
    {
        var builder = new HtmlTreeBuilder(string.Empty);
        var document = new Document();
        var html = document.CreateElement("html");
        var paragraph = document.CreateElement("p");
        var foreignObject = document.CreateElementNS(Namespaces.Svg, "foreignObject");
        var span = document.CreateElement("span");

        PushOpenElements(builder, html, paragraph, foreignObject, span);

        Assert.False(InvokeElementIsInScope(builder, paragraph),
            "foreignObject bounds the base scope, so <p> below it is not in scope.");
    }

    [Fact]
    public void LowerCaseForeignObjectStillBoundsScope()
    {
        var builder = new HtmlTreeBuilder(string.Empty);
        var document = new Document();
        var html = document.CreateElement("html");
        var paragraph = document.CreateElement("p");
        var foreignObject = document.CreateElementNS(Namespaces.Svg, "foreignobject");
        var span = document.CreateElement("span");

        PushOpenElements(builder, html, paragraph, foreignObject, span);

        Assert.False(InvokeHasElementInButtonScope(builder, "p"));
    }

    private static void PushOpenElements(HtmlTreeBuilder builder, params Element[] elements)
    {
        var field = typeof(HtmlTreeBuilder).GetField("_openElements", BindingFlags.NonPublic | BindingFlags.Instance);
        var stack = Assert.IsType<Stack<Element>>(field!.GetValue(builder));
        foreach (var element in elements)
        {
            stack.Push(element);
        }
    }

    private static bool InvokeHasElementInButtonScope(HtmlTreeBuilder builder, string tagName)
    {
        var method = typeof(HtmlTreeBuilder).GetMethod("HasElementInButtonScope", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(builder, new object[] { tagName }));
    }

    private static bool InvokeElementIsInScope(HtmlTreeBuilder builder, Element target)
    {
        var method = typeof(HtmlTreeBuilder).GetMethod("ElementIsInScope", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(builder, new object[] { target }));
    }
}
