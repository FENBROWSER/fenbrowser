// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2 - Production-grade DOM

using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// DOM Living Standard: ShadowRoot interface.
    /// https://dom.spec.whatwg.org/#interface-shadowroot
    ///
    /// Represents the shadow DOM attached to an element.
    /// </summary>
    public sealed class ShadowRoot : DocumentFragment
    {
        /// <summary>
        /// The shadow root mode (open or closed).
        /// https://dom.spec.whatwg.org/#dom-shadowroot-mode
        /// </summary>
        public ShadowRootMode Mode { get; }

        /// <summary>
        /// Whether the shadow root delegates focus.
        /// https://dom.spec.whatwg.org/#dom-shadowroot-delegatesfocus
        /// </summary>
        public bool DelegatesFocus { get; }

        /// <summary>
        /// The slot assignment mode.
        /// https://dom.spec.whatwg.org/#dom-shadowroot-slotassignment
        /// </summary>
        public SlotAssignmentMode SlotAssignment { get; }

        /// <summary>
        /// The host element this shadow root is attached to.
        /// https://dom.spec.whatwg.org/#dom-shadowroot-host
        /// </summary>
        public Element Host { get; }

        private Element _activeElement;

        /// <summary>
        /// Creates a new ShadowRoot attached to the given host element.
        /// </summary>
        internal ShadowRoot(Element host, ShadowRootMode mode,
            bool delegatesFocus = false, SlotAssignmentMode slotAssignment = SlotAssignmentMode.Named)
            : base(host._ownerDocument)
        {
            Host = host;
            Mode = mode;
            DelegatesFocus = delegatesFocus;
            SlotAssignment = slotAssignment;

            _flags |= NodeFlags.InShadowTree;
            _treeScope = new TreeScope(this);
        }

        // --- Slot Assignment ---

        private Dictionary<string, Element> _slotsByName;

        /// <summary>
        /// Returns the slot elements within this shadow root.
        /// </summary>
        public IEnumerable<Element> GetSlots()
        {
            foreach (var node in Descendants())
            {
                if (node is Element el && el.LocalName == "slot")
                    yield return el;
            }
        }

        /// <summary>
        /// Returns the slot with the given name.
        /// </summary>
        public Element GetSlotByName(string name)
        {
            name ??= "";

            // Rebuild cache if needed
            if (_slotsByName == null)
            {
                _slotsByName = new Dictionary<string, Element>(System.StringComparer.Ordinal);
                foreach (var slot in GetSlots())
                {
                    var slotName = slot.GetAttribute("name") ?? "";
                    if (!_slotsByName.ContainsKey(slotName))
                        _slotsByName[slotName] = slot;
                }
            }

            _slotsByName.TryGetValue(name, out var result);
            return result;
        }

        /// <summary>
        /// Invalidates the slot cache when children change.
        /// </summary>
        internal void InvalidateSlotCache()
        {
            _slotsByName = null;
        }

        // --- Stylesheets ---

        private List<object> _adoptedStylesheets;

        /// <summary>
        /// The adopted stylesheets for this shadow root.
        /// https://dom.spec.whatwg.org/#dom-documentorshadowroot-adoptedstylesheets
        /// </summary>
        public IReadOnlyList<object> AdoptedStyleSheets => _adoptedStylesheets ??= new List<object>();

        /// <summary>
        /// Sets the adopted stylesheets.
        /// </summary>
        public void SetAdoptedStyleSheets(IEnumerable<object> stylesheets)
        {
            _adoptedStylesheets ??= new List<object>();
            _adoptedStylesheets.Clear();
            if (stylesheets == null)
            {
                return;
            }

            foreach (var stylesheet in stylesheets)
            {
                if (stylesheet == null)
                    throw new ArgumentNullException(nameof(stylesheets), "Adopted stylesheets cannot contain null entries.");

                if (_adoptedStylesheets.Contains(stylesheet))
                    continue;

                _adoptedStylesheets.Add(stylesheet);
            }
        }

        // --- Active Element ---

        /// <summary>
        /// The currently focused element within this shadow tree.
        /// </summary>
        public Element ActiveElement
        {
            get => _activeElement;
            set
            {
                if (value == null)
                {
                    _activeElement = null;
                    return;
                }

                if (!IsDescendantOfShadowRoot(value))
                    throw new DomException(DomExceptionNames.NotFoundError,
                        "ActiveElement must belong to this shadow root.");

                _activeElement = value;
            }
        }

        // --- innerHTML ---

        /// <summary>
        /// Gets or sets the HTML content of this shadow root.
        /// </summary>
        public string InnerHTML
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                for (var child = FirstChild; child != null; child = child._nextSibling)
                {
                    if (child is Element el)
                        sb.Append(el.OuterHTML);
                    else if (child is Text t)
                        sb.Append(System.Net.WebUtility.HtmlEncode(t.Data));
                    else if (child is Comment c)
                        sb.Append("<!--").Append(c.Data).Append("-->");
                }
                return sb.ToString();
            }
            set
            {
                // Remove all children
                while (FirstChild != null)
                    RemoveChild(FirstChild);

                if (string.IsNullOrEmpty(value))
                    return;

                try
                {
                    var parsedFragment = Parsing.HtmlParser.ParseFragment(Host, value, options: null, out _);
                    while (parsedFragment.FirstChild != null)
                        AppendChild(parsedFragment.FirstChild);

                    MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
                }
                catch
                {
                    AppendChild(new Text(value, _ownerDocument));
                    MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
                }
            }
        }

        public override string ToString() => $"#shadow-root ({Mode})";

        internal void InvalidateStructureCaches()
        {
            InvalidateSlotCache();
            _treeScope?.InvalidateIdIndex();
        }

        private bool IsDescendantOfShadowRoot(Node node)
        {
            return ReferenceEquals(node?.GetRootNode(), this);
        }
    }
}

