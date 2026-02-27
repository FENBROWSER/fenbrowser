namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Centralized parser safety policy shared by HTML and CSS entrypoints.
    /// Limits are defensive defaults intended to keep pathological inputs bounded.
    /// </summary>
    public sealed class ParserSecurityPolicy
    {
        public static ParserSecurityPolicy Default => new ParserSecurityPolicy();

        public int HtmlMaxTokenEmissions { get; set; } = 2_000_000;
        public int HtmlMaxOpenElementsDepth { get; set; } = 4096;
        public int CssMaxRules { get; set; } = 200000;
        public int CssMaxDeclarationsPerBlock { get; set; } = 8192;
    }
}
