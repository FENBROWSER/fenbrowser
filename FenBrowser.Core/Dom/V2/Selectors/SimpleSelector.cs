// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Selectors - Simple Selectors

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2.Selectors
{
    /// <summary>
    /// Base class for all simple selectors.
    /// </summary>
    public abstract class SimpleSelector
    {
        /// <summary>
        /// Tests if the element matches this selector.
        /// </summary>
        public abstract bool Matches(Element element);

        /// <summary>
        /// Computes a bloom filter hint for ancestor matching optimization.
        /// </summary>
        public virtual long ComputeBloomHint() => 0;

        /// <summary>
        /// Gets the specificity contribution of this selector.
        /// </summary>
        public abstract Specificity GetSpecificity();
    }

    /// <summary>
    /// Type selector (element name): div, span, etc.
    /// </summary>
    public sealed class TypeSelector : SimpleSelector
    {
        private readonly string _tagName; // Uppercase

        public TypeSelector(string tagName)
        {
            _tagName = tagName?.ToUpperInvariant() ?? "*";
        }

        public override bool Matches(Element element)
        {
            return element.TagName == _tagName;
        }

        public override long ComputeBloomHint() => BloomFilter.Hash(_tagName);
        public override Specificity GetSpecificity() => new Specificity(0, 0, 1);
        public override string ToString() => _tagName.ToLowerInvariant();
    }

    /// <summary>
    /// Universal selector: *
    /// </summary>
    public sealed class UniversalSelector : SimpleSelector
    {
        public override bool Matches(Element element) => true;
        public override Specificity GetSpecificity() => new Specificity(0, 0, 0);
        public override string ToString() => "*";
    }

    /// <summary>
    /// ID selector: #id
    /// </summary>
    public sealed class IdSelector : SimpleSelector
    {
        private readonly string _id;

        public IdSelector(string id)
        {
            _id = id ?? throw new ArgumentNullException(nameof(id));
        }

        public override bool Matches(Element element)
        {
            return element.Id == _id;
        }

        public override long ComputeBloomHint() => BloomFilter.Hash("#" + _id);
        public override Specificity GetSpecificity() => new Specificity(1, 0, 0);
        public override string ToString() => "#" + _id;
    }

    /// <summary>
    /// Class selector: .class
    /// </summary>
    public sealed class ClassSelector : SimpleSelector
    {
        private readonly string _className;

        public ClassSelector(string className)
        {
            _className = className ?? throw new ArgumentNullException(nameof(className));
        }

        public override bool Matches(Element element)
        {
            return element.ClassList.Contains(_className);
        }

        public override long ComputeBloomHint() => BloomFilter.Hash("." + _className);
        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);
        public override string ToString() => "." + _className;
    }

    /// <summary>
    /// Attribute selector: [attr], [attr=value], [attr^=value], etc.
    /// </summary>
    public sealed class AttributeSelector : SimpleSelector
    {
        private readonly string _attrName;
        private readonly string _value;
        private readonly AttributeMatchType _matchType;
        private readonly bool _caseInsensitive;

        public AttributeSelector(string attrName, string value, AttributeMatchType matchType, bool caseInsensitive)
        {
            _attrName = attrName ?? throw new ArgumentNullException(nameof(attrName));
            _value = value;
            _matchType = matchType;
            _caseInsensitive = caseInsensitive;
        }

        public override bool Matches(Element element)
        {
            var attrValue = element.GetAttribute(_attrName);

            if (_matchType == AttributeMatchType.Exists)
                return attrValue != null;

            if (attrValue == null)
                return false;

            var comparison = _caseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return _matchType switch
            {
                AttributeMatchType.Equals => string.Equals(attrValue, _value, comparison),
                AttributeMatchType.Includes => ContainsWord(attrValue, _value, comparison),
                AttributeMatchType.DashMatch => attrValue.Equals(_value, comparison) ||
                                                attrValue.StartsWith(_value + "-", comparison),
                AttributeMatchType.Prefix => attrValue.StartsWith(_value, comparison),
                AttributeMatchType.Suffix => attrValue.EndsWith(_value, comparison),
                AttributeMatchType.Substring => attrValue.IndexOf(_value, comparison) >= 0,
                _ => false
            };
        }

        private static bool ContainsWord(string haystack, string needle, StringComparison comparison)
        {
            var words = haystack.Split(new[] { ' ', '\t', '\r', '\n', '\f' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word.Equals(needle, comparison))
                    return true;
            }
            return false;
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);

        public override string ToString()
        {
            if (_matchType == AttributeMatchType.Exists)
                return $"[{_attrName}]";

            var op = _matchType switch
            {
                AttributeMatchType.Equals => "=",
                AttributeMatchType.Includes => "~=",
                AttributeMatchType.DashMatch => "|=",
                AttributeMatchType.Prefix => "^=",
                AttributeMatchType.Suffix => "$=",
                AttributeMatchType.Substring => "*=",
                _ => "="
            };

            var flags = _caseInsensitive ? " i" : "";
            return $"[{_attrName}{op}\"{_value}\"{flags}]";
        }
    }

    /// <summary>
    /// Pseudo-element selector: ::before, ::after, ::slotted(), ::part(), etc.
    /// </summary>
    public sealed class PseudoElementSelector : SimpleSelector
    {
        private readonly string _name;
        private readonly string _arg;

        public PseudoElementSelector(string name, string arg = null)
        {
            _name = name?.ToLowerInvariant() ?? throw new ArgumentNullException(nameof(name));
            _arg = arg;
        }

        public override bool Matches(Element element)
        {
            return _name switch
            {
                // ::slotted(selector) — matches elements distributed into a slot
                // Basic: element is a direct child of a shadow host (in light DOM)
                "slotted" => element.ParentElement?.ShadowRoot != null,

                // ::part(name) — matches elements with matching part= attribute
                "part" => MatchesPart(element),

                // ::before, ::after, ::marker etc. — handled by renderer, not element matching
                _ => false
            };
        }

        private bool MatchesPart(Element element)
        {
            if (string.IsNullOrEmpty(_arg)) return false;
            var partAttr = element.GetAttribute("part");
            if (string.IsNullOrEmpty(partAttr)) return false;
            var targetPart = _arg.Trim();
            foreach (var p in partAttr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(p, targetPart, StringComparison.Ordinal)) return true;
            return false;
        }

        public override Specificity GetSpecificity() => new Specificity(0, 0, 1);
        public override string ToString() => "::" + _name + (string.IsNullOrEmpty(_arg) ? "" : $"({_arg})");
    }

    /// <summary>
    /// :host selector — matches the shadow host element from within a shadow root stylesheet.
    /// Also matches when used as :host(selector) with an argument.
    /// </summary>
    public sealed class HostSelector : SimpleSelector
    {
        /// <summary>
        /// Optional inner selector argument from :host(selector). Null means bare :host.
        /// </summary>
        private readonly string _arg;

        public HostSelector(string arg = null)
        {
            _arg = string.IsNullOrWhiteSpace(arg) ? null : arg.Trim();
        }

        public override bool Matches(Element element)
        {
            // Basic: element is a shadow host (has an attached shadow root)
            if (element.ShadowRoot == null) return false;
            // :host with argument — element must also match the inner selector
            // We skip deep matching here to avoid a Core→Engine circular dep.
            // :host() with a non-empty arg is treated as always matching the host when arg is unparsed.
            return true;
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);
        public override string ToString() => string.IsNullOrEmpty(_arg) ? ":host" : $":host({_arg})";
    }

    /// <summary>
    /// State-based pseudo-class selector: :hover, :focus, :checked, etc.
    /// </summary>
    public sealed class StatePseudoClassSelector : SimpleSelector
    {
        private readonly string _name;

        /// <summary>
        /// Pluggable state provider wired by FenEngine at startup.
        /// Signature: (element, pseudoClassName) → bool
        /// Handles dynamic states: hover, focus, active, focus-within, focus-visible, target, valid, invalid.
        /// Core registers a no-op by default; FenEngine replaces it with ElementStateManager.
        /// </summary>
        public static Func<Element, string, bool> StateProvider { get; set; } = (_, _) => false;

        public StatePseudoClassSelector(string name)
        {
            _name = name?.ToLowerInvariant() ?? throw new ArgumentNullException(nameof(name));
        }

        public override bool Matches(Element element)
        {
            return _name switch
            {
                "root" => element.ParentElement == null &&
                          element.OwnerDocument?.DocumentElement == element,
                "empty" => !element.HasChildNodes ||
                           (element.ChildNodes.Length == 1 && element.FirstChild is Text t &&
                            string.IsNullOrWhiteSpace(t.Data)),
                "first-child" => element.PreviousElementSibling == null,
                "last-child" => element.NextElementSibling == null,
                "only-child" => element.PreviousElementSibling == null &&
                                element.NextElementSibling == null,
                "enabled" => SupportsEnabledDisabledPseudoClass(element) && !HasAttribute(element, "disabled"),
                "disabled" => SupportsEnabledDisabledPseudoClass(element) && HasAttribute(element, "disabled"),
                "checked" => IsCheckedFormControl(element),
                "required" => HasAttribute(element, "required"),
                "optional" => !HasAttribute(element, "required"),
                "read-only" => HasAttribute(element, "readonly"),
                "read-write" => !HasAttribute(element, "readonly"),
                "link" => element.LocalName == "a" && element.HasAttribute("href"),
                // Dynamic states delegated to ElementStateManager (wired by FenEngine)
                "hover" or "active" or "focus" or "focus-visible" or "focus-within" or
                "target" or "valid" or "invalid" or "in-range" or
                "out-of-range" or "indeterminate" => StateProvider(element, _name),
                // :defined — true when element is a known/registered custom element or a built-in element
                "defined" => !element.TagName.Contains('-') ||
                             StateProvider(element, "defined"),
                // :visited — intentionally not tracked for privacy
                "visited" => StateProvider(element, "visited"),
                _ => false
            };
        }

        private static bool HasAttribute(Element el, string name)
        {
            return el.HasAttribute(name);
        }

        private static bool SupportsEnabledDisabledPseudoClass(Element element)
        {
            var tagName = element?.TagName;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }

            return string.Equals(tagName, "INPUT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "BUTTON", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "SELECT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "TEXTAREA", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tagName, "FIELDSET", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCheckedFormControl(Element element)
        {
            var tagName = element?.TagName;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }

            if (string.Equals(tagName, "OPTION", StringComparison.OrdinalIgnoreCase))
            {
                return HasAttribute(element, "selected");
            }

            if (!string.Equals(tagName, "INPUT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var type = element.GetAttribute("type");
            if (!string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return HasAttribute(element, "checked");
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);
        public override string ToString() => ":" + _name;
    }

    /// <summary>
    /// :root pseudo-class
    /// </summary>
    public sealed class RootSelector : SimpleSelector
    {
        public override bool Matches(Element element)
        {
            return element.OwnerDocument?.DocumentElement == element;
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);
        public override string ToString() => ":root";
    }

    /// <summary>
    /// :empty pseudo-class
    /// </summary>
    public sealed class EmptySelector : SimpleSelector
    {
        public override bool Matches(Element element)
        {
            for (var child = element.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element) return false;
                if (child is Text t && !string.IsNullOrEmpty(t.Data)) return false;
            }
            return true;
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);
        public override string ToString() => ":empty";
    }

    /// <summary>
    /// :not() negation pseudo-class
    /// </summary>
    public sealed class NegationSelector : SimpleSelector
    {
        private readonly CompiledSelector _inner;

        public NegationSelector(CompiledSelector inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool Matches(Element element)
        {
            return !_inner.Matches(element);
        }

        public override Specificity GetSpecificity() => _inner.GetSpecificity();
        public override string ToString() => $":not({_inner})";
    }

    /// <summary>
    /// :is() / :where() pseudo-class
    /// </summary>
    public sealed class IsWhereSelector : SimpleSelector
    {
        private readonly string _name;
        private readonly CompiledSelector _inner;

        public IsWhereSelector(string name, CompiledSelector inner)
        {
            _name = name?.ToLowerInvariant() ?? "is";
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool Matches(Element element)
        {
            return _inner.Matches(element);
        }

        public override Specificity GetSpecificity()
        {
            // :where() has zero specificity
            if (_name == "where")
                return new Specificity(0, 0, 0);
            return _inner.GetSpecificity();
        }

        public override string ToString() => $":{_name}({_inner})";
    }

    /// <summary>
    /// :has() relational pseudo-class
    /// </summary>
    public sealed class HasSelector : SimpleSelector
    {
        private readonly CompiledSelector _inner;

        public HasSelector(CompiledSelector inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool Matches(Element element)
        {
            // Check if any descendant matches
            foreach (var node in element.Descendants())
            {
                if (node is Element el && _inner.Matches(el))
                    return true;
            }
            return false;
        }

        public override Specificity GetSpecificity() => _inner.GetSpecificity();
        public override string ToString() => $":has({_inner})";
    }

    /// <summary>
    /// :nth-child() pseudo-class
    /// </summary>
    public sealed class NthChildSelector : SimpleSelector
    {
        private readonly int _a;
        private readonly int _b;
        private readonly bool _fromEnd;
        private readonly CompiledSelector _ofSelector;
        private readonly string _ofSelectorText;

        public NthChildSelector(string formula, bool fromEnd)
        {
            _fromEnd = fromEnd;
            ParseFormulaAndOfSelector(formula, out var nthFormula, out _ofSelectorText);
            ParseFormula(nthFormula, out _a, out _b);
            if (!string.IsNullOrWhiteSpace(_ofSelectorText))
            {
                _ofSelector = SelectorParser.Parse(_ofSelectorText);
            }
        }

        private static void ParseFormula(string formula, out int a, out int b)
        {
            formula = formula?.Trim().ToLowerInvariant() ?? "";

            if (formula == "odd")
            {
                a = 2; b = 1;
                return;
            }
            if (formula == "even")
            {
                a = 2; b = 0;
                return;
            }

            // Parse an+b
            int nIndex = formula.IndexOf('n');
            if (nIndex < 0)
            {
                a = 0;
                b = int.TryParse(formula, out int val) ? val : 1;
                return;
            }

            var aStr = formula.Substring(0, nIndex).Trim();
            a = aStr switch
            {
                "" or "+" => 1,
                "-" => -1,
                _ => int.TryParse(aStr, out int val) ? val : 0
            };

            var bPart = formula.Substring(nIndex + 1).Trim();
            if (string.IsNullOrEmpty(bPart))
            {
                b = 0;
            }
            else
            {
                b = int.TryParse(bPart.Replace("+", "").Replace(" ", ""), out int val) ? val : 0;
            }
        }

        private static void ParseFormulaAndOfSelector(string input, out string formula, out string ofSelector)
        {
            formula = input?.Trim() ?? "";
            ofSelector = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int depth = 0;

            for (int i = 0; i < input.Length - 1; i++)
            {
                char c = input[i];
                if (c == '\\' && i + 1 < input.Length)
                {
                    i++;
                    continue;
                }

                if (!inDoubleQuote && c == '\'')
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (!inSingleQuote && c == '"')
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                {
                    continue;
                }

                if (c == '(' || c == '[')
                {
                    depth++;
                    continue;
                }

                if (c == ')' || c == ']')
                {
                    if (depth > 0) depth--;
                    continue;
                }

                if (depth == 0 && IsOfKeyword(input, i))
                {
                    formula = input.Substring(0, i).Trim();
                    ofSelector = input.Substring(i + 2).Trim();
                    return;
                }
            }
        }

        private static bool IsOfKeyword(string input, int index)
        {
            if (index < 0 || index + 2 > input.Length)
                return false;
            if ((input[index] != 'o' && input[index] != 'O') ||
                (input[index + 1] != 'f' && input[index + 1] != 'F'))
            {
                return false;
            }

            bool beforeOk = index == 0 || char.IsWhiteSpace(input[index - 1]);
            bool afterOk = index + 2 >= input.Length || char.IsWhiteSpace(input[index + 2]);
            return beforeOk && afterOk;
        }

        public override bool Matches(Element element)
        {
            int index = GetIndex(element);
            if (index < 1) return false;

            // Check if index matches an+b
            if (_a == 0)
                return index == _b;

            // (index - b) must be divisible by a and result must be non-negative
            int diff = index - _b;
            if (_a > 0)
                return diff >= 0 && diff % _a == 0;
            else
                return diff <= 0 && diff % _a == 0;
        }

        private int GetIndex(Element element)
        {
            var parent = element.ParentElement;
            if (parent == null) return 0;

            if (_ofSelector != null)
            {
                var matches = new List<Element>();
                for (var sibling = parent.FirstElementChild; sibling != null; sibling = sibling.NextElementSibling)
                {
                    if (_ofSelector.Matches(sibling))
                    {
                        matches.Add(sibling);
                    }
                }

                int position = matches.IndexOf(element);
                if (position < 0)
                {
                    return 0;
                }

                return _fromEnd ? matches.Count - position : position + 1;
            }

            int index = 0;
            if (_fromEnd)
            {
                for (var sibling = element; sibling != null; sibling = sibling.NextElementSibling)
                    index++;
            }
            else
            {
                for (var sibling = element; sibling != null; sibling = sibling.PreviousElementSibling)
                    index++;
            }
            return index;
        }

        public override Specificity GetSpecificity()
        {
            var baseSpecificity = new Specificity(0, 1, 0);
            if (_ofSelector == null)
            {
                return baseSpecificity;
            }

            var arg = _ofSelector.GetSpecificity();
            return new Specificity(
                baseSpecificity.A + arg.A,
                baseSpecificity.B + arg.B,
                baseSpecificity.C + arg.C);
        }

        public override string ToString()
        {
            var name = _fromEnd ? ":nth-last-child" : ":nth-child";
            string formula;
            if (_a == 0) formula = _b.ToString();
            else if (_a == 2 && _b == 1) formula = "odd";
            else if (_a == 2 && _b == 0) formula = "even";
            else formula = $"{_a}n+{_b}";

            if (string.IsNullOrWhiteSpace(_ofSelectorText))
            {
                return $"{name}({formula})";
            }

            return $"{name}({formula} of {_ofSelectorText})";
        }
    }

    /// <summary>
    /// :nth-of-type() pseudo-class
    /// </summary>
    public sealed class NthOfTypeSelector : SimpleSelector
    {
        private readonly int _a;
        private readonly int _b;
        private readonly bool _fromEnd;

        public NthOfTypeSelector(string formula, bool fromEnd)
        {
            _fromEnd = fromEnd;
            ParseFormula(formula, out _a, out _b);
        }

        private static void ParseFormula(string formula, out int a, out int b)
        {
            // Same parsing as NthChildSelector
            formula = formula?.Trim().ToLowerInvariant() ?? "";

            if (formula == "odd") { a = 2; b = 1; return; }
            if (formula == "even") { a = 2; b = 0; return; }

            int nIndex = formula.IndexOf('n');
            if (nIndex < 0)
            {
                a = 0;
                b = int.TryParse(formula, out int val) ? val : 1;
                return;
            }

            var aStr = formula.Substring(0, nIndex).Trim();
            a = aStr switch { "" or "+" => 1, "-" => -1, _ => int.TryParse(aStr, out int valA) ? valA : 0 };

            var bPart = formula.Substring(nIndex + 1).Trim();
            b = string.IsNullOrEmpty(bPart) ? 0 :
                int.TryParse(bPart.Replace("+", "").Replace(" ", ""), out int valB) ? valB : 0;
        }

        public override bool Matches(Element element)
        {
            int index = GetTypeIndex(element);
            if (index < 1) return false;

            if (_a == 0) return index == _b;

            int diff = index - _b;
            if (_a > 0) return diff >= 0 && diff % _a == 0;
            return diff <= 0 && diff % _a == 0;
        }

        private int GetTypeIndex(Element element)
        {
            var parent = element.ParentElement;
            if (parent == null) return 0;

            var tagName = element.TagName;
            int index = 0;

            if (_fromEnd)
            {
                for (var sibling = element; sibling != null; sibling = sibling.NextElementSibling)
                {
                    if (sibling.TagName == tagName)
                        index++;
                }
            }
            else
            {
                for (var sibling = element; sibling != null; sibling = sibling.PreviousElementSibling)
                {
                    if (sibling.TagName == tagName)
                        index++;
                }
            }
            return index;
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);

        public override string ToString()
        {
            var name = _fromEnd ? ":nth-last-of-type" : ":nth-of-type";
            if (_a == 0) return $"{name}({_b})";
            return $"{name}({_a}n+{_b})";
        }
    }

    /// <summary>
    /// :only-child / :only-of-type pseudo-class
    /// </summary>
    public sealed class OnlyChildSelector : SimpleSelector
    {
        private readonly bool _ofType;

        public OnlyChildSelector(bool ofType)
        {
            _ofType = ofType;
        }

        public override bool Matches(Element element)
        {
            if (_ofType)
            {
                var tagName = element.TagName;
                for (var sibling = element.PreviousElementSibling; sibling != null;
                     sibling = sibling.PreviousElementSibling)
                {
                    if (sibling.TagName == tagName) return false;
                }
                for (var sibling = element.NextElementSibling; sibling != null;
                     sibling = sibling.NextElementSibling)
                {
                    if (sibling.TagName == tagName) return false;
                }
                return true;
            }
            else
            {
                return element.PreviousElementSibling == null &&
                       element.NextElementSibling == null;
            }
        }

        public override Specificity GetSpecificity() => new Specificity(0, 1, 0);
        public override string ToString() => _ofType ? ":only-of-type" : ":only-child";
    }

    /// <summary>
    /// Bloom filter utility for selector optimization.
    /// </summary>
    internal static class BloomFilter
    {
        public static long Hash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;

            // FNV-1a hash
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++)
                h = (h ^ s[i]) * 16777619;

            // Map to 2 bits in 64-bit field
            int b1 = (int)(h % 64);
            int b2 = (int)((h >> 6) % 64);
            return (1L << b1) | (1L << b2);
        }
    }
}
