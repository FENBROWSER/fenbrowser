namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Visitor pattern interface for PaintNode traversal.
    /// Allows clean separation of rendering logic per node type.
    /// </summary>
    public interface IPaintNodeVisitor
    {
        void Visit(BackgroundPaintNode node);
        void Visit(BorderPaintNode node);
        void Visit(TextPaintNode node);
        void Visit(ImagePaintNode node);
        void Visit(StackingContextPaintNode node);
        void Visit(OpacityGroupPaintNode node);
        void Visit(ClipPaintNode node);
        void Visit(BoxShadowPaintNode node);
    }
}
