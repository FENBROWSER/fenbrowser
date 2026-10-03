using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public class StyleSet
    {
        private readonly List<CssStylesheet> _sheets = new List<CssStylesheet>();
        private readonly List<int> _sourceOrders = new List<int>();
        private readonly List<CssOrigin> _origins = new List<CssOrigin>();

        public int Count => _sheets.Count;

        public IReadOnlyList<CssStylesheet> Sheets => _sheets;
        public IReadOnlyList<CssOrigin> Origins => _origins;
        public IReadOnlyList<int> SourceOrders => _sourceOrders;

        // Bumped on every change to the sheet list. A document's StyleSet is reused
        // across cascades while its stylesheets are unchanged, so the rule index the
        // cascade builds from it is kept here and rebuilt only when this moves on.
        private int _version;
        private object _ruleIndex;
        private int _ruleIndexVersion = -1;

        internal object GetCachedRuleIndex()
        {
            lock (_sheets)
            {
                return _ruleIndexVersion == _version ? _ruleIndex : null;
            }
        }

        internal void SetCachedRuleIndex(object index)
        {
            lock (_sheets)
            {
                _ruleIndex = index;
                _ruleIndexVersion = _version;
            }
        }

        /// <summary>
        /// Inserts a parsed stylesheet into a specific global source order slot.
        /// This ensures asynchronous loads do not corrupt DOM-based ordering.
        /// </summary>
        public void AddSheet(CssStylesheet sheet, CssOrigin origin, int sourceOrder)
        {
            if (sheet == null) return;

            // Maintain sorted order by SourceOrder so cascade matching behaves correctly regardless of async fetch completion times.
            int index = _sourceOrders.BinarySearch(sourceOrder);
            if (index < 0)
            {
                index = ~index;
            }
            else
            {
                // Preserve deterministic insertion order for duplicate source slots.
                while (index < _sourceOrders.Count && _sourceOrders[index] <= sourceOrder)
                {
                    index++;
                }
            }

            _sheets.Insert(index, sheet);
            _version++;
            _sourceOrders.Insert(index, sourceOrder);
            _origins.Insert(index, origin);
        }

        /// <summary>
        /// Replaces the entire content. Useful for setting up single-sheet scenarios
        /// while still passing a StyleSet to the CascadeEngine.
        /// </summary>
        public void SetSingleSheet(CssStylesheet sheet)
        {
            _sheets.Clear();
            _sourceOrders.Clear();
            _origins.Clear();
            _version++;

            if (sheet != null)
            {
                _sheets.Add(sheet);
                _sourceOrders.Add(0);
                _origins.Add(CssOrigin.Author); // Defaulting to author
            }
        }
    }
}
