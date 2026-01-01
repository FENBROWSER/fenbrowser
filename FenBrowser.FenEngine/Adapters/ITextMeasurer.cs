namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// Text measurement interface. Wraps RichTextKit or any other text library.
    /// 
    /// RULE 5: If RichTextKit disappears, we only replace this implementation.
    /// The rest of the browser doesn't know anything changed.
    /// </summary>
    public interface ITextMeasurer
    {
        /// <summary>
        /// Measure the width of text.
        /// </summary>
        float MeasureWidth(string text, string fontFamily, float fontSize, int fontWeight = 400);
        
        /// <summary>
        /// Get line height for a font configuration.
        /// </summary>
        float GetLineHeight(string fontFamily, float fontSize, int fontWeight = 400);
    }
}
