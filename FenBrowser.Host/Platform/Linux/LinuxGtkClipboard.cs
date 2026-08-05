using System;
using FenBrowser.Host.Platform;

namespace FenBrowser.Host.Platform.Linux;

/// <summary>
/// Linux clipboard implementation using GTK if available.
/// </summary>
internal sealed class LinuxGtkClipboard : IClipboard
{
    public string GetText()
    {
        // GTK clipboard would go here if we had GTK# dependency
        // For now, fall back to xclip/xsel
        return string.Empty;
    }

    public bool SetText(string text)
    {
        return false;
    }

    public bool HasText => false;
}