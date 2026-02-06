// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Selectors - Compiled Selector

using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2.Selectors
{
    /// <summary>
    /// A compiled CSS selector for fast matching.
    /// Pre-computes bloom filter hints for fast rejection.
    /// </summary>
    public sealed class CompiledSelector
    {
        private readonly SelectorChain[] _chains;
        private readonly long _bloomHint;

        internal CompiledSelector(List<SelectorChain> chains)
        {
            _chains = chains.ToArray();

            // Compute bloom filter hint from all chains
            long hint = 0;
            foreach (var chain in _chains)
            {
                hint |= chain.ComputeBloomHint();
            }
            _bloomHint = hint;
        }

        /// <summary>
        /// Fast-path rejection using ancestor bloom filter.
        /// Returns false if the selector definitely won't match.
        /// </summary>
        public bool MayMatch(long ancestorFilter)
        {
            // If all required bits are in the ancestor filter, matching is possible
            return (_bloomHint & ancestorFilter) == _bloomHint ||
                   _bloomHint == 0; // Universal selectors have no hint
        }

        /// <summary>
        /// Tests if the element matches this selector.
        /// </summary>
        public bool Matches(Element element)
        {
            if (element == null) return false;

            foreach (var chain in _chains)
            {
                if (chain.Matches(element))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the specificity of this selector.
        /// </summary>
        public Specificity GetSpecificity()
        {
            // Return the highest specificity among all chains
            var max = new Specificity(0, 0, 0);
            foreach (var chain in _chains)
            {
                var spec = chain.GetSpecificity();
                if (spec.CompareTo(max) > 0)
                    max = spec;
            }
            return max;
        }

        public override string ToString()
        {
            return string.Join(", ", (IEnumerable<SelectorChain>)_chains);
        }
    }

    /// <summary>
    /// A single selector chain (compound selectors connected by combinators).
    /// Example: "div.foo > span.bar" is one chain.
    /// </summary>
    public sealed class SelectorChain
    {
        // Each item: (compound selector, combinator to next)
        private readonly (CompoundSelector Compound, Combinator Combinator)[] _parts;

        internal SelectorChain(List<(List<SimpleSelector>, Combinator)> parts)
        {
            _parts = new (CompoundSelector, Combinator)[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                _parts[i] = (new CompoundSelector(parts[i].Item1), parts[i].Item2);
            }
        }

        /// <summary>
        /// Computes a bloom filter hint for fast rejection.
        /// </summary>
        public long ComputeBloomHint()
        {
            // Only the rightmost compound selector needs to match
            // But ancestor requirements from descendant/child combinators should be hinted
            long hint = 0;

            // Collect hints from all ancestor-requiring selectors
            for (int i = 0; i < _parts.Length - 1; i++)
            {
                var combinator = _parts[i].Combinator;
                if (combinator == Combinator.Descendant || combinator == Combinator.Child)
                {
                    hint |= _parts[i].Compound.ComputeBloomHint();
                }
            }

            return hint;
        }

        /// <summary>
        /// Tests if the element matches this chain.
        /// Matching starts from the rightmost selector and works left.
        /// </summary>
        public bool Matches(Element element)
        {
            if (element == null || _parts.Length == 0)
                return false;

            // Start from the rightmost compound selector
            int partIndex = _parts.Length - 1;
            var currentElement = element;

            while (partIndex >= 0)
            {
                var (compound, combinator) = _parts[partIndex];

                if (!compound.Matches(currentElement))
                    return false;

                partIndex--;
                if (partIndex < 0)
                    return true; // All parts matched

                // Navigate based on previous combinator
                var prevCombinator = _parts[partIndex].Combinator;
                currentElement = FindMatchingAncestorOrSibling(
                    currentElement, _parts[partIndex].Compound, prevCombinator);

                if (currentElement == null)
                    return false;
            }

            return true;
        }

        private static Element FindMatchingAncestorOrSibling(
            Element element, CompoundSelector compound, Combinator combinator)
        {
            switch (combinator)
            {
                case Combinator.Descendant:
                    // Any ancestor
                    for (var parent = element.ParentElement; parent != null; parent = parent.ParentElement)
                    {
                        if (compound.Matches(parent))
                            return parent;
                    }
                    return null;

                case Combinator.Child:
                    // Direct parent only
                    var directParent = element.ParentElement;
                    return directParent != null && compound.Matches(directParent) ? directParent : null;

                case Combinator.AdjacentSibling:
                    // Immediately preceding sibling
                    var prevSibling = element.PreviousElementSibling;
                    return prevSibling != null && compound.Matches(prevSibling) ? prevSibling : null;

                case Combinator.GeneralSibling:
                    // Any preceding sibling
                    for (var sibling = element.PreviousElementSibling; sibling != null;
                         sibling = sibling.PreviousElementSibling)
                    {
                        if (compound.Matches(sibling))
                            return sibling;
                    }
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets the specificity of this chain.
        /// </summary>
        public Specificity GetSpecificity()
        {
            int a = 0, b = 0, c = 0;
            foreach (var (compound, _) in _parts)
            {
                var spec = compound.GetSpecificity();
                a += spec.A;
                b += spec.B;
                c += spec.C;
            }
            return new Specificity(a, b, c);
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _parts.Length; i++)
            {
                if (i > 0)
                {
                    switch (_parts[i - 1].Combinator)
                    {
                        case Combinator.Descendant: sb.Append(' '); break;
                        case Combinator.Child: sb.Append(" > "); break;
                        case Combinator.AdjacentSibling: sb.Append(" + "); break;
                        case Combinator.GeneralSibling: sb.Append(" ~ "); break;
                    }
                }
                sb.Append(_parts[i].Compound);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A compound selector (one or more simple selectors without combinators).
    /// Example: "div.foo#bar" is one compound selector.
    /// </summary>
    public sealed class CompoundSelector
    {
        private readonly SimpleSelector[] _selectors;

        internal CompoundSelector(List<SimpleSelector> selectors)
        {
            _selectors = selectors.ToArray();
        }

        /// <summary>
        /// Computes a bloom filter hint.
        /// </summary>
        public long ComputeBloomHint()
        {
            long hint = 0;
            foreach (var selector in _selectors)
            {
                hint |= selector.ComputeBloomHint();
            }
            return hint;
        }

        /// <summary>
        /// Tests if the element matches all simple selectors.
        /// </summary>
        public bool Matches(Element element)
        {
            foreach (var selector in _selectors)
            {
                if (!selector.Matches(element))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the specificity.
        /// </summary>
        public Specificity GetSpecificity()
        {
            int a = 0, b = 0, c = 0;
            foreach (var selector in _selectors)
            {
                var spec = selector.GetSpecificity();
                a += spec.A;
                b += spec.B;
                c += spec.C;
            }
            return new Specificity(a, b, c);
        }

        public override string ToString()
        {
            return string.Join("", (System.Collections.Generic.IEnumerable<SimpleSelector>)_selectors);
        }
    }

    /// <summary>
    /// CSS specificity value (a, b, c).
    /// </summary>
    public readonly struct Specificity : IComparable<Specificity>
    {
        public readonly int A; // ID selectors
        public readonly int B; // Class, attribute, pseudo-class selectors
        public readonly int C; // Type, pseudo-element selectors

        public Specificity(int a, int b, int c)
        {
            A = a;
            B = b;
            C = c;
        }

        public int CompareTo(Specificity other)
        {
            int cmp = A.CompareTo(other.A);
            if (cmp != 0) return cmp;
            cmp = B.CompareTo(other.B);
            if (cmp != 0) return cmp;
            return C.CompareTo(other.C);
        }

        public override string ToString() => $"({A},{B},{C})";

        public static bool operator >(Specificity left, Specificity right) => left.CompareTo(right) > 0;
        public static bool operator <(Specificity left, Specificity right) => left.CompareTo(right) < 0;
        public static bool operator >=(Specificity left, Specificity right) => left.CompareTo(right) >= 0;
        public static bool operator <=(Specificity left, Specificity right) => left.CompareTo(right) <= 0;
    }
}
