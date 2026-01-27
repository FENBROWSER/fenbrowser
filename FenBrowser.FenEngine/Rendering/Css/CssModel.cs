using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom;

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
        
        // Cascade Layer Support
        public string LayerName { get; set; }
        public int LayerOrder { get; set; } // Sequence in which @layer was defined
        
        // CSS Scope Support
        public string ScopeSelector { get; set; }
        public FenBrowser.Core.Dom.Element ScopeRoot { get; set; }
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

        public Specificity Specificity
        {
            get
            {
                int a = 0, b = 0, c = 0;
                foreach (var seg in Segments)
                {
                    if (!string.IsNullOrEmpty(seg.Id)) a++;
                    b += seg.Classes.Count + seg.Attributes.Count + seg.PseudoClasses.Count;
                    if (!string.IsNullOrEmpty(seg.Tag) && seg.Tag != "*") c++;
                    c += seg.PseudoElements.Count;
                }
                return new Specificity { A = a, B = b, C = c };
            }
        }

        public int CompareTo(SelectorChain other)
        {
            return Specificity.CompareTo(other.Specificity);
        }
    }

    public class SelectorSegment
    {
        public string Tag { get; set; }
        public string Id { get; set; }
        public List<string> Classes { get; } = new List<string>();
        public List<AttributeSelector> Attributes { get; } = new List<AttributeSelector>();
        public List<PseudoSelector> PseudoClasses { get; } = new List<PseudoSelector>();
        public List<PseudoSelector> PseudoElements { get; } = new List<PseudoSelector>();
        public char Combinator { get; set; } = ' ';

        public bool IsEmpty => string.IsNullOrEmpty(Tag) && string.IsNullOrEmpty(Id) && 
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
