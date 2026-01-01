using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A lightweight container for a sequence of rendering commands.
    /// Represents the "What to draw" separate from the "How/When to draw".
    /// </summary>
    public class DisplayList : IDisposable
    {
        private readonly List<RenderCommand> _commands;

        public IReadOnlyList<RenderCommand> Commands => _commands;

        public DisplayList(List<RenderCommand> commands)
        {
            _commands = commands ?? new List<RenderCommand>();
        }

        /// <summary>
        /// Replays the recorded commands onto the target SKCanvas.
        /// </summary>
        public void Render(SKCanvas canvas)
        {
            if (canvas == null) return;

            foreach (var cmd in _commands)
            {
                cmd.Execute(canvas);
            }
        }

        public void Dispose()
        {
            // Currently commands are just data, but if we hold bitmaps or shaders,
            // we might need to dispose them here if they are owned by the list.
            // For now, Resources are managed by ResourceManager/ImageLoader usually.
            _commands.Clear();
        }
    }
}
