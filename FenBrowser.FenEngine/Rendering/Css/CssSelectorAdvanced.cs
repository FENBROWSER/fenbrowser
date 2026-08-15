// SpecRef: Selectors Level 4 functional pseudo-classes (:is/:where/:not/:has)
// CapabilityId: CSS-SELECTOR-ADVANCED-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using System;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Compatibility facade for callers that historically used the separate
    /// CssSelectorAdvanced matcher. All selector parsing and matching is now owned by
    /// the canonical <see cref="SelectorMatcher"/> implementation so selector semantics
    /// cannot diverge between call paths.
    /// </summary>
    public static class CssSelectorAdvanced
    {
        public static bool Matches(Element element, string selector, Element root = null)
        {
            if (element == null || string.IsNullOrWhiteSpace(selector))
                return false;

            // root was never an actual scoping boundary in the legacy implementation;
            // retain it only for source compatibility rather than inventing new behavior.
            _ = root;

            var chains = SelectorMatcher.ParseSelectorList(selector);
            for (int i = 0; i < chains.Count; i++)
            {
                if (SelectorMatcher.MatchesChain(element, chains[i]))
                    return true;
            }

            return false;
        }

        public static bool BasicMatch(Element element, string selector)
        {
            return Matches(element, selector);
        }
    }
}