namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Centralized parser safety policy shared by HTML and CSS entrypoints.
    /// Limits are defensive defaults intended to keep pathological inputs bounded.
    /// </summary>
    public sealed class ParserSecurityPolicy
    {
        private static readonly ParserSecurityPolicy DefaultInstance = new ParserSecurityPolicy();
        private int _htmlMaxTokenEmissions = 2_000_000;
        private int _htmlMaxOpenElementsDepth = 4096;
        private int _cssMaxRules = 200_000;
        private int _cssMaxDeclarationsPerBlock = 8192;

        public static ParserSecurityPolicy Default => DefaultInstance.Clone();

        public int HtmlMaxTokenEmissions
        {
            get => _htmlMaxTokenEmissions;
            set => _htmlMaxTokenEmissions = NormalizeLimit(value, 2_000_000);
        }

        public int HtmlMaxOpenElementsDepth
        {
            get => _htmlMaxOpenElementsDepth;
            set => _htmlMaxOpenElementsDepth = NormalizeLimit(value, 4096);
        }

        public int CssMaxRules
        {
            get => _cssMaxRules;
            set => _cssMaxRules = NormalizeLimit(value, 200_000);
        }

        public int CssMaxDeclarationsPerBlock
        {
            get => _cssMaxDeclarationsPerBlock;
            set => _cssMaxDeclarationsPerBlock = NormalizeLimit(value, 8192);
        }

        public ParserSecurityPolicy Clone()
        {
            return new ParserSecurityPolicy
            {
                HtmlMaxTokenEmissions = HtmlMaxTokenEmissions,
                HtmlMaxOpenElementsDepth = HtmlMaxOpenElementsDepth,
                CssMaxRules = CssMaxRules,
                CssMaxDeclarationsPerBlock = CssMaxDeclarationsPerBlock
            };
        }

        public override string ToString()
        {
            return $"HTML(tokens={HtmlMaxTokenEmissions}, openElements={HtmlMaxOpenElementsDepth}), CSS(rules={CssMaxRules}, declarations={CssMaxDeclarationsPerBlock})";
        }

        private static int NormalizeLimit(int value, int fallback)
        {
            return value > 0 ? value : fallback;
        }
    }
}
