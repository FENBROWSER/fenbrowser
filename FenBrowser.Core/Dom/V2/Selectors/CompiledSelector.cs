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
        private readonly long[] _chainBloomHints;

        internal CompiledSelector(List<SelectorChain> chains)
        {
            ArgumentNullException.ThrowIfNull(chains);
            _chains = chains.ToArray();
            _chainBloomHints = new long[_chains.Length];
            for (var i = 0; i < _chains.Length; i++)
            {
                _chainBloomHints[i] = _chains[i].ComputeBloomHint();
            }
        }

        /// <summary>
        /// Fast-path rejection using ancestor bloom filter.
        /// Returns false only when no selector-list branch can possibly match.
        /// </summary>
        public bool MayMatch(long ancestorFilter)
        {
            // Selector-list branches are alternatives. OR-ing every branch into one
            // combined hint incorrectly required an element to satisfy ancestor hints
            // from all branches at once (for example "#a, .b"), creating false
            // negatives for any caller that uses this fast path.
            for (var i = 0; i < _chainBloomHints.Length; i++)
            {
                var hint = _chainBloomHints[i];
                if (hint == 0 || (hint & ancestorFilter) == hint)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Tests if the element matches this selector.
        /// </summary>
        public bool Matches(Element element)
        {
            if (element == null) return false;

            for (var i = 0; i < _chains.Length; i++)
            {
                if (_chains[i].Matches(element))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the specificity of this selector.
        /// </summary>
        public Specificity GetSpecificity()
        {
            var max = new Specificity(0, 0, 0);
            for (var i = 0; i < _chains.Length; i++)
            {
                var spec = _chains[i].GetSpecificity();
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
        private readonly (CompoundSelector Compound, Combinator Combinator)[] _parts;

        internal SelectorChain(List<(List<SimpleSelector>, Combinator)> parts)
        {
            ArgumentNullException.ThrowIfNull(parts);
            _parts = new (CompoundSelector, Combinator)[parts.Count];
            for (var i = 0; i < parts.Count; i++)
            {
                _parts[i] = (new CompoundSelector(parts[i].Item1), parts[i].Item2);
            }
        }

        /// <summary>
        /// Computes a bloom filter hint for fast rejection.
        /// </summary>
        public long ComputeBloomHint()
        {
            long hint = 0;

            for (var i = 0; i < _parts.Length - 1; i++)
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
        /// Matching starts from the rightmost selector and uses explicit-stack
        /// backtracking for non-unique combinators.
        /// </summary>
        public bool Matches(Element element)
        {
            if (element == null || _parts.Length == 0)
                return false;

            // Greedily selecting the nearest matching descendant/general sibling is
            // incorrect when the selected candidate cannot satisfy the rest of the
            // chain but an earlier candidate can. Example: A + B ~ C. Use an explicit
            // DFS stack so all legal candidates can be tried without recursion/native
            // stack growth on long generated selector chains.
            var states = new Stack<MatchState>();
            states.Push(new MatchState(_parts.Length - 1, element));

            while (states.Count > 0)
            {
                var state = states.Pop();
                var partIndex = state.PartIndex;
                var current = state.Element;
                if (current == null || !_parts[partIndex].Compound.Matches(current))
                {
                    continue;
                }

                if (partIndex == 0)
                {
                    return true;
                }

                var previousPartIndex = partIndex - 1;
                var combinator = _parts[previousPartIndex].Combinator;
                PushCandidates(states, previousPartIndex, current, combinator);
            }

            return false;
        }

        private static void PushCandidates(
            Stack<MatchState> states,
            int previousPartIndex,
            Element current,
            Combinator combinator)
        {
            switch (combinator)
            {
                case Combinator.Child:
                {
                    var parent = current.ParentElement;
                    if (parent != null)
                    {
                        states.Push(new MatchState(previousPartIndex, parent));
                    }
                    break;
                }

                case Combinator.AdjacentSibling:
                {
                    var sibling = current.PreviousElementSibling;
                    if (sibling != null)
                    {
                        states.Push(new MatchState(previousPartIndex, sibling));
                    }
                    break;
                }

                case Combinator.Descendant:
                {
                    // Push farthest -> nearest so nearest is evaluated first while all
                    // ancestors remain available for backtracking.
                    var ancestors = new List<Element>();
                    for (var parent = current.ParentElement; parent != null; parent = parent.ParentElement)
                    {
                        ancestors.Add(parent);
                    }
                    for (var i = ancestors.Count - 1; i >= 0; i--)
                    {
                        states.Push(new MatchState(previousPartIndex, ancestors[i]));
                    }
                    break;
                }

                case Combinator.GeneralSibling:
                {
                    var siblings = new List<Element>();
                    for (var sibling = current.PreviousElementSibling;
                         sibling != null;
                         sibling = sibling.PreviousElementSibling)
                    {
                        siblings.Add(sibling);
                    }
                    for (var i = siblings.Count - 1; i >= 0; i--)
                    {
                        states.Push(new MatchState(previousPartIndex, siblings[i]));
                    }
                    break;
                }
            }
        }

        private readonly struct MatchState
        {
            public MatchState(int partIndex, Element element)
            {
                PartIndex = partIndex;
                Element = element;
            }

            public int PartIndex { get; }
            public Element Element { get; }
        }

        /// <summary>
        /// Gets the specificity of this chain.
        /// </summary>
        public Specificity GetSpecificity()
        {
            var a = 0;
            var b = 0;
            var c = 0;
            for (var i = 0; i < _parts.Length; i++)
            {
                var spec = _parts[i].Compound.GetSpecificity();
                a += spec.A;
                b += spec.B;
                c += spec.C;
            }
            return new Specificity(a, b, c);
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < _parts.Length; i++)
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
            ArgumentNullException.ThrowIfNull(selectors);
            _selectors = selectors.ToArray();
        }

        public long ComputeBloomHint()
        {
            long hint = 0;
            for (var i = 0; i < _selectors.Length; i++)
            {
                hint |= _selectors[i].ComputeBloomHint();
            }
            return hint;
        }

        public bool Matches(Element element)
        {
            for (var i = 0; i < _selectors.Length; i++)
            {
                if (!_selectors[i].Matches(element))
                    return false;
            }
            return true;
        }

        public Specificity GetSpecificity()
        {
            var a = 0;
            var b = 0;
            var c = 0;
            for (var i = 0; i < _selectors.Length; i++)
            {
                var spec = _selectors[i].GetSpecificity();
                a += spec.A;
                b += spec.B;
                c += spec.C;
            }
            return new Specificity(a, b, c);
        }

        public override string ToString()
        {
            return string.Join("", (IEnumerable<SimpleSelector>)_selectors);
        }
    }

    /// <summary>
    /// CSS specificity value (a, b, c).
    /// </summary>
    public readonly struct Specificity : IComparable<Specificity>
    {
        public readonly int A;
        public readonly int B;
        public readonly int C;

        public Specificity(int a, int b, int c)
        {
            A = a;
            B = b;
            C = c;
        }

        public int CompareTo(Specificity other)
        {
            var cmp = A.CompareTo(other.A);
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
