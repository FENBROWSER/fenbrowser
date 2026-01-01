namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Interface for CSS parsing strategies.
    /// </summary>
    public interface ICssParser
    {
        /// <summary>
        /// Parse inline CSS style declarations (e.g., from a style attribute).
        /// </summary>
        /// <param name="styleValue">The raw CSS declarations string</param>
        /// <returns>Dictionary of property name to value</returns>
        System.Collections.Generic.Dictionary<string, string> ParseInlineStyle(string styleValue);
    }
}
