using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Result of a diff operation between two ImmutablePaintTrees.
    /// </summary>
    public sealed class PaintTreeDiff
    {
        public IReadOnlyList<PaintNodeBase> AddedNodes { get; init; } = new List<PaintNodeBase>();
        public IReadOnlyList<PaintNodeBase> RemovedNodes { get; init; } = new List<PaintNodeBase>();
        public IReadOnlyList<NodeChange> ModifiedNodes { get; init; } = new List<NodeChange>();

        public bool HasChanges => AddedNodes.Count > 0 || RemovedNodes.Count > 0 || ModifiedNodes.Count > 0;
    }

    public record NodeChange(PaintNodeBase OldNode, PaintNodeBase NewNode, ChangeType Type);

    public enum ChangeType
    {
        Geometry,   // Bounds/Transform changed
        Style,      // Color/Opacity/Radius changed
        Content,    // Text/Image changed
        Structure   // Children changed
    }
}
