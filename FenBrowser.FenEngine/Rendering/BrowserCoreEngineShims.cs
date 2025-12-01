using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace FenBrowser.FenEngine.Rendering
{
    // Minimal shims to provide missing UWP-like controls using Avalonia equivalents
    public class RichTextBlock : Control
    {
        // Emulated properties for compatibility
        public bool IsTextSelectionEnabled { get; set; }
        public int MaxLines { get; set; }
        public System.Collections.Generic.List<Control> Blocks { get; } = new System.Collections.Generic.List<Control>();
        public TextBlock CreateParagraph() { var p = new TextBlock(); Blocks.Add(p); return p; }
    }

    public class HyperlinkButton : Button
    {
        public Uri NavigateUri { get; set; }
    }

    public class RichEditBox : TextBox
    {
        // Placeholder for the UWP Document property
        public object Document { get; set; }
    }
}
