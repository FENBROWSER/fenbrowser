using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Values of the <c>list-item</c> counter (CSS Lists 3 §4 "Automatic numbering"),
    /// the number a list item's marker shows.
    ///
    /// Each element, in tree order, resets, then increments, then sets the counter:
    /// <list type="bullet">
    /// <item>Reset: the author's <c>counter-reset</c> when it names <c>list-item</c>
    /// (plain or <c>reversed(list-item)</c>); otherwise HTML's defaults for
    /// <c>ol</c>/<c>ul</c>/<c>menu</c>, with <c>start</c> and <c>reversed</c> on
    /// <c>ol</c>. An author <c>counter-reset</c> that does not name
    /// <c>list-item</c> replaces those defaults.</item>
    /// <item>Increment: the author's <c>counter-increment</c> for <c>list-item</c>
    /// (every mention adds up); otherwise a list item increments by 1, or by -1
    /// when the counter is reversed.</item>
    /// <item>Set: the author's <c>counter-set</c> when present; otherwise an
    /// <c>li</c> in an <c>ol</c> with a <c>value</c> attribute sets it (HTML's
    /// presentational hint, which the author property overrides).</item>
    /// </list>
    /// A list container's reset covers its descendants, so an item after a nested
    /// list continues the outer numbering; a reset on any other element also covers
    /// its following siblings.
    ///
    /// Any other counter follows the same reset/increment/set order and scoping
    /// (CSS Lists 3 §4.5) without the HTML list defaults; <see cref="ComputeCounter"/>
    /// gives its values for counter() and counters() in generated content.
    /// </summary>
    internal sealed class ListItemCounters
    {
        private const string ListItem = "list-item";

        private sealed class Instance
        {
            public Node Owner;
            public int Value;
            public bool Reversed;
        }

        private readonly struct Reset
        {
            public Reset(int? value, bool reversed)
            {
                Value = value;
                Reversed = reversed;
            }

            public int? Value { get; }
            public bool Reversed { get; }
        }

        private readonly string _name;
        private readonly bool _isListItem;
        private readonly Func<Element, CssComputed> _styleOf;
        private readonly Func<Node, IEnumerable<Node>> _childrenOf;
        private readonly List<Instance> _instances = new();
        private readonly Dictionary<Element, int> _values = new();
        private readonly Dictionary<Element, int[]> _chains;

        private ListItemCounters(
            string name,
            Func<Element, CssComputed> styleOf,
            Func<Node, IEnumerable<Node>> childrenOf,
            bool recordChains)
        {
            _name = name;
            _childrenOf = childrenOf ?? (node => node.ChildNodes);
            _isListItem = string.Equals(name, ListItem, StringComparison.OrdinalIgnoreCase);
            _styleOf = styleOf;
            _chains = recordChains ? new Dictionary<Element, int[]>() : null;
        }

        /// <summary>The list-item value of every list item under <paramref name="root"/>.</summary>
        /// <remarks>
        /// Counters follow the flat tree (CSS Lists 3 §4.5 "inheriting counters"):
        /// <paramref name="childrenOf"/> gives a node's children in it - shadow root
        /// content for a host, assigned nodes for a slot. Null walks the DOM children.
        /// </remarks>
        public static Dictionary<Element, int> Compute(
            Node root,
            Func<Element, CssComputed> styleOf,
            Func<Node, IEnumerable<Node>> childrenOf = null)
        {
            var walker = new ListItemCounters(ListItem, styleOf, childrenOf, recordChains: false);
            if (root != null)
            {
                walker.Walk(root, root.ParentNode);
            }

            return walker._values;
        }

        /// <summary>
        /// The instances of counter <paramref name="name"/> in scope at every element
        /// and generated ::before/::after under <paramref name="root"/>, outermost
        /// first, after that element's own reset, increment and set. An element with
        /// no entry has no counter of that name in scope.
        /// </summary>
        public static Dictionary<Element, int[]> ComputeCounter(
            Node root,
            Func<Element, CssComputed> styleOf,
            string name,
            Func<Node, IEnumerable<Node>> childrenOf = null)
        {
            var walker = new ListItemCounters(name, styleOf, childrenOf, recordChains: true);
            if (root != null)
            {
                walker.Walk(root, root.ParentNode);
            }

            return walker._chains;
        }

        private void Walk(Node node, Node parent)
        {
            CssComputed style = null;
            if (node is Element element)
            {
                style = _styleOf(element);
                Apply(element, style, parent);

                // Generated ::before and ::after boxes take part in tree order - a
                // ::before that is a list item numbers before the element's children.
                if (GeneratedPseudo(element, style?.Before, "before") is { } before)
                {
                    Apply(before, style.Before, element);
                }
            }

            // An element with no box of its own (display: contents, which a <slot> has
            // by default) does not bound its children's counters: a counter one child
            // instantiates carries on to the next, as if they were siblings.
            var childParent = node is Element container && IsDisplayContents(container, style) ? parent : node;
            foreach (var child in _childrenOf(node))
            {
                Walk(child, childParent);
            }

            if (node is Element owner && GeneratedPseudo(owner, style?.After, "after") is { } after)
            {
                Apply(after, style.After, owner);
            }

            // Instances this node's children opened (their scope ends with this node's
            // subtree), and the one a list container opened for its own descendants.
            while (_instances.Count > 0 && ReferenceEquals(_instances[^1].Owner, node))
            {
                _instances.RemoveAt(_instances.Count - 1);
            }
        }

        // The pseudo-element the box tree generates for a ::before/::after whose
        // content is not none or normal; the same instance, created the same way.
        private static PseudoElement GeneratedPseudo(Element element, CssComputed pseudoStyle, string type)
        {
            var content = pseudoStyle?.Content;
            if (content == null ||
                string.Equals(content, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(content, "normal", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return pseudoStyle.PseudoElementInstance ??= new PseudoElement(element, type, pseudoStyle);
        }

        private void Apply(Element element, CssComputed style, Node parent)
        {
            if (TryGetReset(element, style, out var reset))
            {
                var owner = _isListItem && IsListContainer(element) ? (Node)element : parent;
                var instance = new Instance
                {
                    Owner = owner,
                    Reversed = reset.Reversed,
                    Value = reset.Value ?? (reset.Reversed ? ReversedStart(element, parent) : 0)
                };

                // A later sibling's reset replaces the earlier sibling's counter.
                if (_instances.Count > 0 && ReferenceEquals(_instances[^1].Owner, owner))
                {
                    _instances[^1] = instance;
                }
                else
                {
                    _instances.Add(instance);
                }
            }

            var reversed = _instances.Count > 0 && _instances[^1].Reversed;
            var increment = GetIncrement(element, style, reversed);
            var set = GetSet(element, style);
            if ((increment.HasValue || set.HasValue) && _instances.Count == 0)
            {
                // Using a counter nobody reset instantiates it on this element.
                _instances.Add(new Instance { Owner = parent, Value = 0 });
            }

            if (_instances.Count == 0)
            {
                return;
            }

            var current = _instances[^1];
            if (increment.HasValue)
            {
                current.Value = unchecked(current.Value + increment.Value);
            }

            if (set.HasValue)
            {
                current.Value = set.Value;
            }

            if (_chains != null)
            {
                var chain = new int[_instances.Count];
                for (var i = 0; i < chain.Length; i++)
                {
                    chain[i] = _instances[i].Value;
                }

                _chains[element] = chain;
            }
            else if (IsListItem(style))
            {
                _values[element] = current.Value;
            }
        }

        // CSS Lists 3 §4.4.2: the initial value of a reversed counter reset without
        // one. Over the elements this counter reaches, in tree order: add each
        // negated increment, or at the first counter-set add its value and stop; then
        // add the last non-zero negated increment once more.
        private int ReversedStart(Element resetElement, Node parent)
        {
            var scope = new List<Element>();
            CollectScope(resetElement, parent, scope);

            long num = 0;
            long lastNonZeroIncrementNegated = 0;
            foreach (var element in scope)
            {
                var style = _styleOf(element);
                long incrementNegated = -(long)(GetIncrement(element, style, reversed: true) ?? 0);
                if (incrementNegated != 0)
                {
                    lastNonZeroIncrementNegated = incrementNegated;
                }

                if (GetSet(element, style) is int set)
                {
                    num += set;
                    break;
                }

                num += incrementNegated;
            }

            return ClampToInt(num + lastNonZeroIncrementNegated);
        }

        private void CollectScope(Element resetElement, Node parent, List<Element> scope)
        {
            scope.Add(resetElement);
            CollectDescendants(resetElement, scope);
            if (_isListItem && IsListContainer(resetElement))
            {
                return;
            }

            if (parent == null)
            {
                return;
            }

            var afterReset = false;
            foreach (var sibling in _childrenOf(parent))
            {
                if (!afterReset)
                {
                    afterReset = ReferenceEquals(sibling, resetElement);
                    continue;
                }

                if (sibling is not Element element)
                {
                    continue;
                }

                if (TryGetReset(element, _styleOf(element), out _))
                {
                    return;
                }

                scope.Add(element);
                CollectDescendants(element, scope);
            }
        }

        private void CollectDescendants(Node parent, List<Element> scope)
        {
            foreach (var child in _childrenOf(parent))
            {
                if (child is not Element element)
                {
                    continue;
                }

                if (TryGetReset(element, _styleOf(element), out _))
                {
                    // A nested counter: a list container keeps it to its own subtree,
                    // any other element also hands it to its following siblings.
                    if (_isListItem && IsListContainer(element))
                    {
                        continue;
                    }

                    return;
                }

                scope.Add(element);
                CollectDescendants(element, scope);
            }
        }

        private bool TryGetReset(Element element, CssComputed style, out Reset reset)
        {
            reset = default;
            var authored = style?.CounterReset;
            if (!string.IsNullOrWhiteSpace(authored))
            {
                var found = false;
                foreach (var directive in ParseDirectives(authored))
                {
                    if (directive.Matches)
                    {
                        reset = new Reset(directive.Value, directive.Reversed);
                        found = true;
                    }
                }

                return found;
            }

            if (!_isListItem || !IsListContainer(element))
            {
                return false;
            }

            // HTML: ol, ul, menu { counter-reset: list-item }, with ol's start and
            // reversed attributes as presentational hints.
            var isOrdered = string.Equals(element.LocalName, "ol", StringComparison.OrdinalIgnoreCase);
            var reversed = isOrdered && element.HasAttribute("reversed");
            int? start = isOrdered && TryParseHtmlInteger(element.GetAttribute("start"), out var parsedStart)
                ? parsedStart
                : null;
            reset = reversed
                ? new Reset(start.HasValue ? ClampToInt((long)start.Value + 1) : null, reversed: true)
                : new Reset(start.HasValue ? ClampToInt((long)start.Value - 1) : 0, reversed: false);
            return true;
        }

        private int? GetIncrement(Element element, CssComputed style, bool reversed)
        {
            var authored = style?.CounterIncrement;
            if (!string.IsNullOrWhiteSpace(authored))
            {
                long total = 0;
                var named = false;
                foreach (var directive in ParseDirectives(authored))
                {
                    if (directive.Matches && !directive.Reversed)
                    {
                        total += directive.Value ?? 1;
                        named = true;
                    }
                }

                if (named)
                {
                    return ClampToInt(total);
                }
            }

            return _isListItem && IsListItem(style) ? (reversed ? -1 : 1) : null;
        }

        private int? GetSet(Element element, CssComputed style)
        {
            string authored = null;
            style?.Map?.TryGetValue("counter-set", out authored);
            if (!string.IsNullOrWhiteSpace(authored))
            {
                int? set = null;
                foreach (var directive in ParseDirectives(authored))
                {
                    if (directive.Matches && !directive.Reversed)
                    {
                        set = directive.Value ?? 0;
                    }
                }

                return set;
            }

            if (_isListItem &&
                string.Equals(element.LocalName, "li", StringComparison.OrdinalIgnoreCase) &&
                element.ParentElement is { } parent &&
                string.Equals(parent.LocalName, "ol", StringComparison.OrdinalIgnoreCase) &&
                TryParseHtmlInteger(element.GetAttribute("value"), out var value))
            {
                return value;
            }

            return null;
        }

        private static bool IsDisplayContents(Element element, CssComputed style) =>
            style?.Display != null
                ? string.Equals(style.Display.Trim(), "contents", StringComparison.OrdinalIgnoreCase)
                : string.Equals(element.LocalName, "slot", StringComparison.OrdinalIgnoreCase);

        private static bool IsListItem(CssComputed style) =>
            style?.Display != null &&
            style.Display.Contains("list-item", StringComparison.OrdinalIgnoreCase);

        private static bool IsListContainer(Element element) =>
            element.LocalName is { } name &&
            (string.Equals(name, "ol", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, "ul", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, "menu", StringComparison.OrdinalIgnoreCase));

        private readonly struct Directive
        {
            public Directive(bool matches, bool reversed, int? value)
            {
                Matches = matches;
                Reversed = reversed;
                Value = value;
            }

            public bool Matches { get; }
            public bool Reversed { get; }
            public int? Value { get; }
        }

        // "name [int]" or "reversed(name) [int]", repeated; "none" names nothing.
        // list-item matches in any case; author counter names are case-sensitive.
        private List<Directive> ParseDirectives(string value)
        {
            var directives = new List<Directive>();
            var tokens = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                var reversed = false;
                string name = token;
                if (token.StartsWith("reversed(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(")", StringComparison.Ordinal))
                {
                    reversed = true;
                    name = token.Substring("reversed(".Length, token.Length - "reversed(".Length - 1).Trim();
                }

                int? number = null;
                if (i + 1 < tokens.Length && int.TryParse(tokens[i + 1], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    number = parsed;
                    i++;
                }

                if (string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                directives.Add(new Directive(
                    _isListItem
                        ? string.Equals(name, ListItem, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(name, _name, StringComparison.Ordinal),
                    reversed,
                    number));
            }

            return directives;
        }

        // HTML "rules for parsing integers": optional leading whitespace and sign,
        // then digits; anything after the digits is ignored.
        internal static bool TryParseHtmlInteger(string input, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            var position = 0;
            while (position < input.Length && input[position] is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                position++;
            }

            var negative = false;
            if (position < input.Length && (input[position] == '-' || input[position] == '+'))
            {
                negative = input[position] == '-';
                position++;
            }

            if (position >= input.Length || !char.IsAsciiDigit(input[position]))
            {
                return false;
            }

            long result = 0;
            while (position < input.Length && char.IsAsciiDigit(input[position]))
            {
                result = Math.Min(result * 10 + (input[position] - '0'), (long)int.MaxValue + 1);
                position++;
            }

            value = ClampToInt(negative ? -result : result);
            return true;
        }

        private static int ClampToInt(long value) =>
            (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }
}
