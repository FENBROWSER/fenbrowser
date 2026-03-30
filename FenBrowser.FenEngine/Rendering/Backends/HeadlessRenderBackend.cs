using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Backends
{
    /// <summary>
    /// Headless (no-op) implementation of IRenderBackend for unit testing.
    /// </summary>
    public class HeadlessRenderBackend : IRenderBackend
    {
        private int _saveDepth;

        public List<RenderCommand> CommandLog { get; } = new();
        public bool LogCommands { get; set; } = true;

        public int SaveDepth => _saveDepth;

        public void DrawRect(SKRect rect, SKColor color, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawRect", $"rect={rect}, color={color}, opacity={opacity}"));
        }

        public void DrawRectStroke(SKRect rect, SKColor color, float strokeWidth, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawRectStroke", $"rect={rect}, color={color}, strokeWidth={strokeWidth}"));
        }

        public void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKColor color, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawRoundRect", $"rect={rect}, radius=({radiusX}, {radiusY}), color={color}"));
        }

        public void DrawPath(SKPath path, SKColor color, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawPath", $"pathBounds={path.Bounds}, color={color}"));
        }

        public void DrawRect(SKRect rect, SKShader shader, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawRect", $"rect={rect}, shader=yes"));
        }

        public void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKShader shader, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawRoundRect", $"rect={rect}, radius=({radiusX}, {radiusY}), shader=yes"));
        }

        public void DrawPath(SKPath path, SKShader shader, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawPath", $"bounds={path.Bounds}, shader=yes"));
        }

        public void DrawBorder(SKRect rect, BorderStyle border)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawBorder", $"rect={rect}"));
        }

        public void DrawGlyphRun(SKPoint origin, GlyphRun glyphs, SKColor color, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawGlyphRun", $"origin={origin}, text=\"{glyphs?.SourceText}\", color={color}"));
        }

        public void DrawText(string text, SKPoint origin, SKColor color, float fontSize, SKTypeface typeface, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawText", $"text=\"{text}\", origin={origin}, fontSize={fontSize}"));
        }

        public void DrawImage(SKImage image, SKRect destRect, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawImage", $"destRect={destRect}"));
        }

        public void DrawImage(SKImage image, SKRect destRect, SKRect srcRect, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawImage", $"destRect={destRect}, srcRect={srcRect}"));
        }

        public void DrawPicture(SKPicture picture, SKRect destRect, float opacity = 1f)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawPicture", $"destRect={destRect}"));
        }

        public void PushClip(SKRect clipRect)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PushClip", $"clipRect={clipRect}"));
            _saveDepth++;
        }

        public void PushClip(SKRect clipRect, float radiusX, float radiusY)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PushClip", $"rect={clipRect}, radius=({radiusX}, {radiusY})"));
            _saveDepth++;
        }

        public void PushClip(SKPath clipPath)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PushClip", $"pathBounds={clipPath.Bounds}"));
            _saveDepth++;
        }

        public void PopClip()
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PopClip", ""));
            _saveDepth = Math.Max(0, _saveDepth - 1);
        }

        public void PushLayer(float opacity)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PushLayer", $"opacity={opacity}"));
            _saveDepth++;
        }

        public void PushTransform(SKMatrix transform)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PushTransform", $"transform={transform}"));
            _saveDepth++;
        }

        public void PopLayer()
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PopLayer", ""));
            _saveDepth = Math.Max(0, _saveDepth - 1);
        }

        public void ApplyMask(SKImage mask, SKRect bounds)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("ApplyMask", $"bounds={bounds}"));
        }

        public void PushFilter(SKImageFilter filter)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PushFilter", $"filter={(filter != null ? "yes" : "no")}"));
            _saveDepth++;
        }

        public void PopFilter()
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("PopFilter", ""));
            _saveDepth = Math.Max(0, _saveDepth - 1);
        }

        public void ApplyBackdropFilter(SKRect bounds, SKImageFilter filter)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("ApplyBackdropFilter", $"bounds={bounds}, filter={(filter != null ? "yes" : "no")}"));
        }

        public void DrawShadow(SKPath path, float offsetX, float offsetY, float blurRadius, SKColor color)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawShadow", $"bounds={path.Bounds}, offset=({offsetX}, {offsetY}), blur={blurRadius}"));
        }

        public void DrawBoxShadow(SKRect rect, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawBoxShadow", $"rect={rect}, offset=({offsetX}, {offsetY}), blur={blurRadius}, spread={spreadRadius}"));
        }

        public void DrawInsetBoxShadow(SKRect rect, SKPoint[] borderRadius, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("DrawInsetBoxShadow", $"rect={rect}, offset=({offsetX}, {offsetY}), blur={blurRadius}, spread={spreadRadius}"));
        }

        public void Save()
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("Save", ""));
            _saveDepth++;
        }

        public void Restore()
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("Restore", ""));
            _saveDepth = Math.Max(0, _saveDepth - 1);
        }

        public void RestoreToSaveDepth(int saveDepth)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("RestoreToSaveDepth", $"saveDepth={saveDepth}"));
            _saveDepth = Math.Max(0, saveDepth);
        }

        public void Clear(SKColor color)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("Clear", $"color={color}"));
        }

        public void ExecuteCustomPaint(Action<SKCanvas, SKRect> paintAction, SKRect bounds)
        {
            if (LogCommands) CommandLog.Add(new RenderCommand("ExecuteCustomPaint", $"bounds={bounds}"));
        }

        public void ClearLog()
        {
            CommandLog.Clear();
        }
    }

    /// <summary>
    /// A logged render command for test verification.
    /// </summary>
    public class RenderCommand
    {
        public string Name { get; }
        public string Args { get; }
        public long Timestamp { get; }

        public RenderCommand(string name, string args)
        {
            Name = name;
            Args = args;
            Timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public override string ToString() => $"{Name}({Args})";
    }
}
