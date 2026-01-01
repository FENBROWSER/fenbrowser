using System.Collections.Generic;
using SkiaSharp;
using FenBrowser.FenEngine.Typography;

namespace FenBrowser.FenEngine.Rendering.Backends
{
    /// <summary>
    /// Headless (no-op) implementation of IRenderBackend for unit testing.
    /// 
    /// RULE 4 BENEFIT: Because we abstract the backend, we can test layout
    /// without needing a GPU, window, or any graphics context.
    /// </summary>
    public class HeadlessRenderBackend : IRenderBackend
    {
        /// <summary>
        /// Log of all render commands for verification in tests.
        /// </summary>
        public List<RenderCommand> CommandLog { get; } = new();
        
        /// <summary>
        /// Whether to log commands (disable for performance tests).
        /// </summary>
        public bool LogCommands { get; set; } = true;
        
        public void DrawRect(SKRect rect, SKColor color, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawRect", $"rect={rect}, color={color}, opacity={opacity}"));
        }
        
        public void DrawRectStroke(SKRect rect, SKColor color, float strokeWidth, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawRectStroke", $"rect={rect}, color={color}, strokeWidth={strokeWidth}"));
        }
        
        public void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKColor color, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawRoundRect", $"rect={rect}, radius=({radiusX}, {radiusY}), color={color}"));
        }
        
        public void DrawPath(SKPath path, SKColor color, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawPath", $"pathBounds={path.Bounds}, color={color}"));
        }
        
        public void DrawRect(SKRect rect, SKShader shader, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawRect", $"rect={rect}, shader=yes"));
        }

        public void DrawRoundRect(SKRect rect, float radiusX, float radiusY, SKShader shader, float opacity = 1f)
        {
             if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawRoundRect", $"rect={rect}, radius=({radiusX}, {radiusY}), shader=yes"));
        }
        
        public void DrawPath(SKPath path, SKShader shader, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawPath", $"bounds={path.Bounds}, shader=yes"));
        }

        public void DrawBorder(SKRect rect, BorderStyle border)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawBorder", $"rect={rect}"));
        }
        
        public void DrawGlyphRun(SKPoint origin, GlyphRun glyphs, SKColor color, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawGlyphRun", $"origin={origin}, text=\"{glyphs?.SourceText}\", color={color}"));
        }
        
        public void DrawText(string text, SKPoint origin, SKColor color, float fontSize, SKTypeface typeface, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawText", $"text=\"{text}\", origin={origin}, fontSize={fontSize}"));
        }
        
        public void DrawImage(SKImage image, SKRect destRect, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawImage", $"destRect={destRect}"));
        }
        
        public void DrawPicture(SKPicture picture, SKRect destRect, float opacity = 1f)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawPicture", $"destRect={destRect}"));
        }
        
        public void PushClip(SKRect clipRect)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("PushClip", $"clipRect={clipRect}"));
        }
        
        public void PushClip(SKRect clipRect, float radiusX, float radiusY)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("PushClip", $"rect={clipRect}, radius=({radiusX}, {radiusY})"));
        }
        
        public void PushClip(SKPath clipPath)
        {
            if (LogCommands)
                 CommandLog.Add(new RenderCommand("PushClip", $"pathBounds={clipPath.Bounds}"));
        }

        public void PopClip()
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("PopClip", ""));
        }
        
        public void PushLayer(float opacity)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("PushLayer", $"opacity={opacity}"));
        }
        
        public void PushTransform(SKMatrix transform)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("PushTransform", $"transform={transform}"));
        }
        
        public void PopLayer()
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("PopLayer", ""));
        }
        
        public void DrawShadow(SKPath path, float offsetX, float offsetY, float blurRadius, SKColor color)
        {
            if (LogCommands)
                 CommandLog.Add(new RenderCommand("DrawShadow", $"bounds={path.Bounds}, offset=({offsetX}, {offsetY}), blur={blurRadius}"));
        }
        
        public void DrawBoxShadow(SKRect rect, float offsetX, float offsetY, float blurRadius, float spreadRadius, SKColor color)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("DrawBoxShadow", $"rect={rect}, offset=({offsetX}, {offsetY}), blur={blurRadius}"));
        }
        
        public void Save()
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("Save", ""));
        }
        
        public void Restore()
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("Restore", ""));
        }
        
        public void Clear(SKColor color)
        {
            if (LogCommands)
                CommandLog.Add(new RenderCommand("Clear", $"color={color}"));
        }
        
        /// <summary>
        /// Clear the command log.
        /// </summary>
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
