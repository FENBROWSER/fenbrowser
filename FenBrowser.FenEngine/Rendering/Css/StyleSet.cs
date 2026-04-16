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

        /// <summary>
        /// Inserts a parsed stylesheet into a specific global source order slot.
        /// This ensures asynchronous loads do not corrupt DOM-based ordering.
        /// </summary>
        public void AddSheet(CssStylesheet sheet, CssOrigin origin, int sourceOrder)
        {
            if (sheet == null) return;

            // Maintain sorted order by SourceOrder so cascade matching behaves correctly regardless of async fetch completion times.
            int index = _sourceOrders.BinarySearch(sourceOrder);
            if (index < 0) index = ~index;

            _sheets.Insert(index, sheet);
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

            if (sheet != null)
            {
                _sheets.Add(sheet);
                _sourceOrders.Add(0);
                _origins.Add(CssOrigin.Author); // Defaulting to author
            }
        }
    }
}
