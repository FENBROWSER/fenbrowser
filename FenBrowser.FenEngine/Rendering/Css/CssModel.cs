using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public class CssStylesheet
    {
        public List<CssRule> Rules { get; } = new List<CssRule>();
    }

    public enum CssOrigin
    {
        UserAgent = 0,
        User = 1,
        Author = 2
    }

    public abstract class CssRule
    {
        public Uri BaseUri { get; set; }
        public CssOrigin Origin { get; set; }
        public int StylesheetSourceOrder { get; set; } // Global stylesheet loading order

        // Cascade Layer Support
        public string LayerName { get; set; }
        public int LayerOrder { get; set; } // Sequence in which @layer was defined
        
        // CSS Scope Support
        public string ScopeSelector { get; set; }
        public FenBrowser.Core.Dom.V2.Element ScopeRoot { get; set; }
        public int ScopeProximity { get; set; } // Number of generations between scope root and matched element
    }

    public class CssStyleRule : CssRule
    {
        public CssSelector Selector { get; set; }
        public List<CssDeclaration> Declarations { get; } = new List<CssDeclaration>();
        public int Order { get; set; } // For cascade sort
    }

    public class CssMediaRule : CssRule
    {
        public string Condition { get; set; }
        public List<CssRule> Rules { get; } = new List<CssRule>();
    }

    public class CssLayerRule : CssRule
    {
        public string Name { get; set; }
        public List<CssRule> Rules { get; } = new List<CssRule>();
    }

    public class CssScopeRule : CssRule
    {
        public string EndSelector { get; set; }
        public List<CssRule> Rules { get; } = new List<CssRule>();
    }

    public class CssFontFaceRule : CssRule
    {
        public List<CssDeclaration> Declarations { get; } = new List<CssDeclaration>();
    }

    public class CssDeclaration
    {
        public string Property { get; set; } // Normalized to lowercase
        public string Value { get; set; }    // Raw string value (for now)
        public bool IsImportant { get; set; }
    }

    public class CssSelector
    {
        public string Raw { get; set; }
        public Specificity Specificity { get; set; }
        public List<SelectorChain> Chains { get; set; } = new List<SelectorChain>();
    }

    public struct Specificity : IComparable<Specificity>
    {
        public int A; // ID
        public int B; // Class/Attribute/Pseudo
        public int C; // Element/Pseudo-element

        public int CompareTo(Specificity other)
        {
            if (A != other.A) return A.CompareTo(other.A);
            if (B != other.B) return B.CompareTo(other.B);
            return C.CompareTo(other.C);
        }

        public override string ToString() => $"({A},{B},{C})";
    }

    // --- Selector Types (Moved from SelectorMatcher.cs) ---

    public class SelectorChain : IComparable<SelectorChain>
    {
        public List<SelectorSegment> Segments { get; } = new List<SelectorSegment>();

        /// <summary>
        /// Calculate CSS Specificity per Selectors Level 4.
        /// - :where() has 0 specificity
        /// - :is(), :not(), :has() take the specificity of their most specific argument
        /// Reference: https://www.w3.org/TR/selectors-4/#specificity-rules
        /// </summary>
        public Specificity Specificity
        {
            get
            {
                int a = 0, b = 0, c = 0;
                foreach (var seg in Segments)
                {
                    // ID selectors (#id)
                    if (!string.IsNullOrEmpty(seg.Id)) a++;

                    // Class selectors (.class)
                    b += seg.Classes.Count;

                    // Attribute selectors ([attr])
                    b += seg.Attributes.Count;

                    // Pseudo-classes need special handling
                    foreach (var pseudo in seg.PseudoClasses)
                    {
                        var name = pseudo.Name?.ToLowerInvariant();

                        // :where() has 0 specificity (CSS Selectors Level 4)
                        if (name == "where")
                        {
                            // Don't add any specificity
                            continue;
                        }

                        // :is(), :not(), :has() take the specificity of their most specific argument
                        if (name == "is" || name == "not" || name == "has")
                        {
                            // Get the highest specificity from the arguments
                            var argSpec = GetHighestArgumentSpecificity(pseudo);
                            a += argSpec.A;
                            b += argSpec.B;
                            c += argSpec.C;
                            continue;
                        }

                        // :nth-child(), :nth-last-child() with selector argument take
                        // the specificity of the selector (Selectors Level 4)
                        // E.g., :nth-child(2 of .foo) has specificity of .foo
                        if ((name == "nth-child" || name == "nth-last-child") &&
                            !string.IsNullOrEmpty(pseudo.Args) &&
                            pseudo.Args.Contains(" of "))
                        {
                            b++; // The :nth-child itself counts as pseudo-class
                            var ofIdx = pseudo.Args.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
                            if (ofIdx > 0)
                            {
                                var selectorArg = pseudo.Args.Substring(ofIdx + 4).Trim();
                                var argSpec = GetHighestSpecificityFromSelector(selectorArg);
                                a += argSpec.A;
                                b += argSpec.B;
                                c += argSpec.C;
                            }
                            continue;
                        }

                        // Regular pseudo-classes count as (0, 1, 0)
                        b++;
                    }

                    // Type/Element selectors (div, span, etc.)
                    if (!string.IsNullOrEmpty(seg.TagName) && seg.TagName != "*") c++;

                    // Pseudo-elements (::before, ::after)
                    c += seg.PseudoElements.Count;
                }
                return new Specificity { A = a, B = b, C = c };
            }
        }

        /// <summary>
        /// Get the highest specificity from a pseudo-class's parsed arguments.
        /// </summary>
        private static Specificity GetHighestArgumentSpecificity(PseudoSelector pseudo)
        {
            var highest = new Specificity { A = 0, B = 0, C = 0 };

            // If we have pre-parsed arguments, use them
            if (pseudo.ParsedArgs != null && pseudo.ParsedArgs.Count > 0)
            {
                foreach (var chain in pseudo.ParsedArgs)
                {
                    var spec = chain.Specificity;
                    if (spec.CompareTo(highest) > 0)
                        highest = spec;
                }
            }
            else if (!string.IsNullOrEmpty(pseudo.Args))
            {
                // Parse the arguments as a selector list
                highest = GetHighestSpecificityFromSelector(pseudo.Args);
            }

            return highest;
        }

        /// <summary>
        /// Parse a selector string and return its highest specificity.
        /// </summary>
        private static Specificity GetHighestSpecificityFromSelector(string selector)
        {
            var highest = new Specificity { A = 0, B = 0, C = 0 };

            if (string.IsNullOrWhiteSpace(selector)) return highest;

            try
            {
                var chains = SelectorMatcher.ParseSelectorList(selector);
                foreach (var chain in chains)
                {
                    var spec = chain.Specificity;
                    if (spec.CompareTo(highest) > 0)
                        highest = spec;
                }
            }
            catch
            {
                // If parsing fails, return 0 specificity
            }

            return highest;
        }

        public int CompareTo(SelectorChain other)
        {
            return Specificity.CompareTo(other.Specificity);
        }
    }

    public class SelectorSegment
    {
        public string TagName { get; set; }
        public string Id { get; set; }
        public List<string> Classes { get; } = new List<string>();
        public List<AttributeSelector> Attributes { get; } = new List<AttributeSelector>();
        public List<PseudoSelector> PseudoClasses { get; } = new List<PseudoSelector>();
        public List<PseudoSelector> PseudoElements { get; } = new List<PseudoSelector>();
        public char Combinator { get; set; } = ' ';

        public bool IsEmpty => string.IsNullOrEmpty(TagName) && string.IsNullOrEmpty(Id) && 
                               Classes.Count == 0 && Attributes.Count == 0 && 
                               PseudoClasses.Count == 0 && PseudoElements.Count == 0;
    }

    public class PseudoSelector
    {
        public string Name { get; set; }
        public string Args { get; set; }
        public List<SelectorChain> ParsedArgs { get; set; } = new List<SelectorChain>();
    }

    public class AttributeSelector
    {
        public string Name { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }
        public bool CaseInsensitive { get; set; }
    }
}

