namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Centralized parser safety policy shared by HTML and CSS entrypoints.
    /// Limits are defensive defaults intended to keep pathological inputs bounded.
    /// </summary>
    public sealed class ParserSecurityPolicy
    {
        private const int DefaultHtmlMaxTokenEmissions = 2_000_000;
        private const int DefaultHtmlMaxAttributesPerElement = 4096;
        private const int DefaultHtmlMaxOpenElementsDepth = 4096;
        private const int DefaultCssMaxRules = 200_000;
        private const int DefaultCssMaxDeclarationsPerBlock = 8192;

        // Configuration is allowed to relax defaults for conformance/diagnostics, but
        // this remains a security policy. Arbitrary int.MaxValue assignments must not
        // silently turn the guard into an effectively unbounded parser.
        private const int HardHtmlMaxTokenEmissions = 20_000_000;
        private const int HardHtmlMaxAttributesPerElement = 32_768;
        private const int HardHtmlMaxOpenElementsDepth = 32_768;
        private const int HardCssMaxRules = 2_000_000;
        private const int HardCssMaxDeclarationsPerBlock = 65_536;

        private static readonly ParserSecurityPolicy DefaultInstance = new ParserSecurityPolicy();
        private int _htmlMaxTokenEmissions = DefaultHtmlMaxTokenEmissions;
        private int _htmlMaxAttributesPerElement = DefaultHtmlMaxAttributesPerElement;
        private int _htmlMaxOpenElementsDepth = DefaultHtmlMaxOpenElementsDepth;
        private int _cssMaxRules = DefaultCssMaxRules;
        private int _cssMaxDeclarationsPerBlock = DefaultCssMaxDeclarationsPerBlock;

        public static ParserSecurityPolicy Default => DefaultInstance.Clone();

        public int HtmlMaxTokenEmissions
        {
            get => _htmlMaxTokenEmissions;
            set => _htmlMaxTokenEmissions = NormalizeLimit(value, DefaultHtmlMaxTokenEmissions, HardHtmlMaxTokenEmissions);
        }

        public int HtmlMaxOpenElementsDepth
        {
            get => _htmlMaxOpenElementsDepth;
            set => _htmlMaxOpenElementsDepth = NormalizeLimit(value, DefaultHtmlMaxOpenElementsDepth, HardHtmlMaxOpenElementsDepth);
        }

        public int HtmlMaxAttributesPerElement
        {
            get => _htmlMaxAttributesPerElement;
            set => _htmlMaxAttributesPerElement = NormalizeLimit(value, DefaultHtmlMaxAttributesPerElement, HardHtmlMaxAttributesPerElement);
        }

        public int CssMaxRules
        {
            get => _cssMaxRules;
            set => _cssMaxRules = NormalizeLimit(value, DefaultCssMaxRules, HardCssMaxRules);
        }

        public int CssMaxDeclarationsPerBlock
        {
            get => _cssMaxDeclarationsPerBlock;
            set => _cssMaxDeclarationsPerBlock = NormalizeLimit(value, DefaultCssMaxDeclarationsPerBlock, HardCssMaxDeclarationsPerBlock);
        }

        public ParserSecurityPolicy Clone()
        {
            return new ParserSecurityPolicy
            {
                HtmlMaxTokenEmissions = HtmlMaxTokenEmissions,
                HtmlMaxAttributesPerElement = HtmlMaxAttributesPerElement,
                HtmlMaxOpenElementsDepth = HtmlMaxOpenElementsDepth,
                CssMaxRules = CssMaxRules,
                CssMaxDeclarationsPerBlock = CssMaxDeclarationsPerBlock
            };
        }

        public override string ToString()
        {
            return $"HTML(tokens={HtmlMaxTokenEmissions}, attributesPerElement={HtmlMaxAttributesPerElement}, openElements={HtmlMaxOpenElementsDepth}), CSS(rules={CssMaxRules}, declarations={CssMaxDeclarationsPerBlock})";
        }

        private static int NormalizeLimit(int value, int fallback, int hardMaximum)
        {
            if (value <= 0)
                return fallback;

            return Math.Min(value, hardMaximum);
        }
    }
}
