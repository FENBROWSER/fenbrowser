using System;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering;

/// <summary>
/// Builds owned SKPath instances through the non-obsolete SkiaSharp path builder API.
/// </summary>
internal static class PathBuilderHelper
{
    public static SKPath Build(Action<SKPathBuilder> configure)
    {
        using var builder = new SKPathBuilder();
        configure(builder);
        return builder.Detach();
    }
}
