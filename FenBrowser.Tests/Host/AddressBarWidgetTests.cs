using System.Reflection;
using FenBrowser.Host.Input;
using FenBrowser.Host.Widgets;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host;

public class AddressBarWidgetTests
{
    [Fact]
    public void TextSetter_ResetsHorizontalScroll_WhenUrlChangesProgrammatically()
    {
        var widget = new AddressBarWidget();
        var scrollField = typeof(AddressBarWidget).GetField("_scrollOffset", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(scrollField);

        widget.Text = "https://www.google.com/search?q=fenbrowser+address+bar+regression";
        scrollField!.SetValue(widget, 184f);

        widget.Text = "fen://newtab/";

        Assert.Equal(0f, (float)scrollField.GetValue(widget)!);
    }

    [Fact]
    public void RequestFocus_InvalidatesAddressBarImmediately()
    {
        var widget = new AddressBarWidget();
        widget.Arrange(new SKRect(0, 0, 320, 32));
        widget.Text = "fen://newtab/";
        widget.ClearDirtyRect();

        InputManager.Instance.RequestFocus(widget);

        Assert.NotNull(widget.DirtyRect);

        InputManager.Instance.ClearFocus();
    }

    [Fact]
    public void ChildInvalidate_PropagatesGlobalBoundsWithoutParentOffset()
    {
        var toolbar = new ToolbarWidget();
        toolbar.Measure(new SKSize(800, 48));
        toolbar.Arrange(new SKRect(0, 30, 800, 78));

        var addressBar = toolbar.AddressBar;
        var addressBarBounds = addressBar.Bounds;

        toolbar.ClearDirtyRect();
        addressBar.ClearDirtyRect();

        addressBar.Invalidate();

        Assert.NotNull(toolbar.DirtyRect);
        Assert.Equal(addressBarBounds.Left, toolbar.DirtyRect!.Value.Left);
        Assert.Equal(addressBarBounds.Top, toolbar.DirtyRect!.Value.Top);
        Assert.Equal(addressBarBounds.Right, toolbar.DirtyRect!.Value.Right);
        Assert.Equal(addressBarBounds.Bottom, toolbar.DirtyRect!.Value.Bottom);
    }
}