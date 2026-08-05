using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Resolved track geometry that a parent grid passes to a subgrid item
    /// (a grid item whose <c>grid-template-columns/rows</c> is <c>subgrid</c>).
    /// The subgrid inherits the parent's track lines for the tracks it spans,
    /// so its own items align exactly with the parent grid's lines.
    /// </summary>
    public sealed class GridSubgridContext
    {
        /// <summary>
        /// True when the columns axis is a subgrid (inherits parent column tracks).
        /// </summary>
        public bool SubgridColumns { get; set; }

        /// <summary>
        /// True when the rows axis is a subgrid (inherits parent row tracks).
        /// </summary>
        public bool SubgridRows { get; set; }

        /// <summary>
        /// Parent grid line positions for the spanned column range, expressed
        /// relative to the subgrid item's content-box origin. Length is
        /// span+1; line i is the start of the i-th inherited column track.
        /// </summary>
        public float[] ColumnLines { get; set; }

        /// <summary>
        /// Parent grid line positions for the spanned row range, expressed
        /// relative to the subgrid item's content-box origin. Length is
        /// span+1; line i is the start of the i-th inherited row track.
        /// </summary>
        public float[] RowLines { get; set; }

        /// <summary>
        /// Parent line names that fall inside the spanned range, mapped to
        /// child-local 1-based line numbers (child line 1 == first spanned
        /// parent line). Merged with the subgrid template's own line names.
        /// </summary>
        public Dictionary<string, int> InheritedLineNames { get; set; }

        public bool HasColumnLines => ColumnLines != null && ColumnLines.Length > 1;
        public bool HasRowLines => RowLines != null && RowLines.Length > 1;

        public float ColumnSpanWidth
        {
            get
            {
                if (!HasColumnLines) return 0f;
                return ColumnLines[ColumnLines.Length - 1] - ColumnLines[0];
            }
        }

        public float RowSpanHeight
        {
            get
            {
                if (!HasRowLines) return 0f;
                return RowLines[RowLines.Length - 1] - RowLines[0];
            }
        }
    }
}
