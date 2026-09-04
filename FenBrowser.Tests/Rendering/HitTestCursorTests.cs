using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// The cursor a hit test reports for page content. CSS UI 3 §7.1: a link is
/// pointer, an editable field is text, everything else default - and an explicit
/// CSS cursor on the element or an ancestor wins over all of it.
/// </summary>
public class HitTestCursorTests
{
    private static RenderContext ContextFor(Element element) => new RenderContext
    {
        PaintTreeRoots = new List<PaintNodeBase>
        {
            new BackgroundPaintNode
            {
                SourceNode = element,
                Bounds = new SKRect(0, 0, 100, 100),
                Color = SKColors.White,
            },
        },
    };

    private static CursorType CursorAt(Element element)
    {
        Assert.True(HitTester.HitTest(ContextFor(element), 10, 10, out var result));
        return result.Cursor;
    }

    [Fact]
    public void LinkWithHrefReportsPointer()
    {
        var link = new Element("a");
        link.SetAttribute("href", "https://example.test/");

        Assert.Equal(CursorType.Pointer, CursorAt(link));
    }

    [Fact]
    public void AnchorWithoutHrefIsNotAPointer()
    {
        // A bare <a> is not a link, so it gets no pointer affordance.
        Assert.NotEqual(CursorType.Pointer, CursorAt(new Element("a")));
    }

    [Fact]
    public void ButtonReportsPointer()
    {
        Assert.Equal(CursorType.Pointer, CursorAt(new Element("button")));
    }

    [Fact]
    public void TextInputReportsTextCursor()
    {
        var input = new Element("input");
        input.SetAttribute("type", "text");

        Assert.Equal(CursorType.Text, CursorAt(input));
    }

    [Fact]
    public void CheckboxInputKeepsThePointerAffordance()
    {
        // Only text-entry inputs are I-beams; a checkbox stays clickable.
        var input = new Element("input");
        input.SetAttribute("type", "checkbox");

        Assert.Equal(CursorType.Pointer, CursorAt(input));
    }

    [Fact]
    public void TextAreaReportsTextCursor()
    {
        Assert.Equal(CursorType.Text, CursorAt(new Element("textarea")));
    }

    [Fact]
    public void InputWithoutTypeDefaultsToTextCursor()
    {
        Assert.Equal(CursorType.Text, CursorAt(new Element("input")));
    }

    [Fact]
    public void PlainDivReportsDefault()
    {
        Assert.Equal(CursorType.Default, CursorAt(new Element("div")));
    }

    [Fact]
    public void ContentEditableReportsTextCursor()
    {
        var div = new Element("div");
        div.SetAttribute("contenteditable", "true");

        Assert.Equal(CursorType.Text, CursorAt(div));
    }
}
