// HTML Living Standard §4.9 Tabular data: the DOM interfaces of table,
// tbody/thead/tfoot, tr and td/th elements, expressed over the generic
// Element tree.
using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// HTMLTableElement, HTMLTableSectionElement, HTMLTableRowElement and
    /// HTMLTableCellElement algorithms (HTML §4.9.1, §4.9.5-4.9.9, §4.9.11).
    /// </summary>
    public static class HtmlTableDom
    {
        private static bool Is(Element el, string localName) =>
            el != null &&
            string.Equals(el.NamespaceUri, Namespaces.Html, StringComparison.Ordinal) &&
            string.Equals(el.LocalName, localName, StringComparison.OrdinalIgnoreCase);

        public static bool IsTable(Element el) => Is(el, "table");
        public static bool IsSection(Element el) => Is(el, "tbody") || Is(el, "thead") || Is(el, "tfoot");
        public static bool IsRow(Element el) => Is(el, "tr");
        public static bool IsCell(Element el) => Is(el, "td") || Is(el, "th");

        private static Element FirstChildElement(Element parent, string localName)
        {
            for (var child = parent.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && Is(el, localName))
                    return el;
            }

            return null;
        }

        // --- HTMLTableElement ---

        /// <summary>dom-table-caption: the first caption child.</summary>
        public static Element GetCaption(Element table) => FirstChildElement(table, "caption");

        /// <summary>dom-table-caption setter: remove the current caption, insert the new one first.</summary>
        public static void SetCaption(Element table, Element caption)
        {
            if (caption != null && !Is(caption, "caption"))
                throw new DomException("HierarchyRequestError", "The new caption must be a caption element");

            var existing = GetCaption(table);
            if (existing != null)
                table.RemoveChild(existing);

            if (caption != null)
                table.InsertBefore(caption, table.FirstChild);
        }

        public static Element CreateCaption(Element table)
        {
            var existing = GetCaption(table);
            if (existing != null)
                return existing;

            var caption = table.OwnerDocument.CreateElement("caption");
            table.InsertBefore(caption, table.FirstChild);
            return caption;
        }

        public static void DeleteCaption(Element table)
        {
            var existing = GetCaption(table);
            if (existing != null)
                table.RemoveChild(existing);
        }

        public static Element GetTHead(Element table) => FirstChildElement(table, "thead");

        public static Element GetTFoot(Element table) => FirstChildElement(table, "tfoot");

        /// <summary>
        /// dom-table-thead setter: the new thead goes immediately before the first
        /// child that is neither caption nor colgroup.
        /// </summary>
        public static void SetTHead(Element table, Element thead)
        {
            if (thead != null && !Is(thead, "thead"))
                throw new DomException("HierarchyRequestError", "The new tHead must be a thead element");

            var existing = GetTHead(table);
            if (existing != null)
                table.RemoveChild(existing);

            if (thead != null)
                table.InsertBefore(thead, FirstChildNotCaptionOrColgroup(table));
        }

        public static Element CreateTHead(Element table)
        {
            var existing = GetTHead(table);
            if (existing != null)
                return existing;

            var thead = table.OwnerDocument.CreateElement("thead");
            table.InsertBefore(thead, FirstChildNotCaptionOrColgroup(table));
            return thead;
        }

        public static void DeleteTHead(Element table)
        {
            var existing = GetTHead(table);
            if (existing != null)
                table.RemoveChild(existing);
        }

        /// <summary>dom-table-tfoot setter: the new tfoot is appended.</summary>
        public static void SetTFoot(Element table, Element tfoot)
        {
            if (tfoot != null && !Is(tfoot, "tfoot"))
                throw new DomException("HierarchyRequestError", "The new tFoot must be a tfoot element");

            var existing = GetTFoot(table);
            if (existing != null)
                table.RemoveChild(existing);

            if (tfoot != null)
                table.AppendChild(tfoot);
        }

        public static Element CreateTFoot(Element table)
        {
            var existing = GetTFoot(table);
            if (existing != null)
                return existing;

            var tfoot = table.OwnerDocument.CreateElement("tfoot");
            table.AppendChild(tfoot);
            return tfoot;
        }

        public static void DeleteTFoot(Element table)
        {
            var existing = GetTFoot(table);
            if (existing != null)
                table.RemoveChild(existing);
        }

        // dom-table-thead setter / createTHead: "immediately before the first
        // element in the table that is neither a caption nor a colgroup" -
        // elements only, so text between children is skipped over.
        private static Node FirstChildNotCaptionOrColgroup(Element table)
        {
            for (var child = table.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && !Is(el, "caption") && !Is(el, "colgroup"))
                    return el;
            }

            return null;
        }

        /// <summary>dom-table-tbodies: the tbody children, in tree order.</summary>
        public static HTMLCollection GetTBodies(Element table) =>
            new FilteredHTMLCollection(table, el => el.ParentNode == table && Is(el, "tbody"), nameMatches: false);

        /// <summary>
        /// dom-table-rows: tr children of the thead(s), then tr children of the
        /// table and of tbody children in tree order, then tr children of tfoot(s).
        /// </summary>
        public static List<Element> GetRows(Element table)
        {
            var rows = new List<Element>();
            for (var child = table.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && Is(el, "thead"))
                    AddRowChildren(el, rows);
            }

            for (var child = table.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is not Element el)
                    continue;
                if (IsRow(el))
                    rows.Add(el);
                else if (Is(el, "tbody"))
                    AddRowChildren(el, rows);
            }

            for (var child = table.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && Is(el, "tfoot"))
                    AddRowChildren(el, rows);
            }

            return rows;
        }

        private static void AddRowChildren(Element section, List<Element> rows)
        {
            for (var child = section.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && IsRow(el))
                    rows.Add(el);
            }
        }

        /// <summary>
        /// dom-table-insertrow. Index -1 or rows.length appends; an existing
        /// last tbody takes the row when the table has no rows, otherwise a new
        /// tbody is appended to hold it.
        /// </summary>
        public static Element InsertRow(Element table, int index)
        {
            var rows = GetRows(table);
            if (index < -1 || index > rows.Count)
                throw new DomException("IndexSizeError", "The row index is out of range");

            var document = table.OwnerDocument;
            var tr = document.CreateElement("tr");
            if (rows.Count == 0)
            {
                Element lastTBody = null;
                for (var child = table.FirstChild; child != null; child = child.NextSibling)
                {
                    if (child is Element el && Is(el, "tbody"))
                        lastTBody = el;
                }

                if (lastTBody == null)
                {
                    lastTBody = document.CreateElement("tbody");
                    table.AppendChild(lastTBody);
                }

                lastTBody.AppendChild(tr);
            }
            else if (index == -1 || index == rows.Count)
            {
                var last = rows[rows.Count - 1];
                ((ContainerNode)last.ParentNode).AppendChild(tr);
            }
            else
            {
                var reference = rows[index];
                ((ContainerNode)reference.ParentNode).InsertBefore(tr, reference);
            }

            return tr;
        }

        /// <summary>dom-table-deleterow: -1 removes the last row; nothing when empty.</summary>
        public static void DeleteRow(Element table, int index)
        {
            var rows = GetRows(table);
            if (index == -1)
            {
                if (rows.Count == 0)
                    return;
                index = rows.Count - 1;
            }

            if (index < 0 || index >= rows.Count)
                throw new DomException("IndexSizeError", "The row index is out of range");

            var row = rows[index];
            ((ContainerNode)row.ParentNode).RemoveChild(row);
        }

        // --- HTMLTableSectionElement ---

        public static List<Element> GetSectionRows(Element section)
        {
            var rows = new List<Element>();
            AddRowChildren(section, rows);
            return rows;
        }

        /// <summary>dom-tbody-insertrow.</summary>
        public static Element InsertSectionRow(Element section, int index)
        {
            var rows = GetSectionRows(section);
            if (index < -1 || index > rows.Count)
                throw new DomException("IndexSizeError", "The row index is out of range");

            var tr = section.OwnerDocument.CreateElement("tr");
            if (index == -1 || index == rows.Count)
                section.AppendChild(tr);
            else
                section.InsertBefore(tr, rows[index]);
            return tr;
        }

        /// <summary>dom-tbody-deleterow.</summary>
        public static void DeleteSectionRow(Element section, int index)
        {
            var rows = GetSectionRows(section);
            if (index == -1)
            {
                if (rows.Count == 0)
                    return;
                index = rows.Count - 1;
            }

            if (index < 0 || index >= rows.Count)
                throw new DomException("IndexSizeError", "The row index is out of range");

            section.RemoveChild(rows[index]);
        }

        // --- HTMLTableRowElement ---

        /// <summary>dom-tr-rowindex: the row's index in its table's rows, or -1 when not in a table.</summary>
        public static int GetRowIndex(Element row)
        {
            var parent = row.ParentElement;
            var table = parent != null && IsTable(parent) ? parent : parent?.ParentElement;
            if (table == null || !IsTable(table) || (parent != table && !IsSection(parent)))
                return -1;

            return GetRows(table).IndexOf(row);
        }

        /// <summary>dom-tr-sectionrowindex: the row's index among its parent's tr children.</summary>
        public static int GetSectionRowIndex(Element row)
        {
            var parent = row.ParentElement;
            if (parent == null || (!IsTable(parent) && !IsSection(parent)))
                return -1;

            var index = 0;
            for (var child = parent.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is not Element el || !IsRow(el))
                    continue;
                if (el == row)
                    return index;
                index++;
            }

            return -1;
        }

        public static List<Element> GetCells(Element row)
        {
            var cells = new List<Element>();
            for (var child = row.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is Element el && IsCell(el))
                    cells.Add(el);
            }

            return cells;
        }

        /// <summary>dom-tr-insertcell: a td at the index, -1 or cells.length appends.</summary>
        public static Element InsertCell(Element row, int index)
        {
            var cells = GetCells(row);
            if (index < -1 || index > cells.Count)
                throw new DomException("IndexSizeError", "The cell index is out of range");

            var td = row.OwnerDocument.CreateElement("td");
            if (index == -1 || index == cells.Count)
                row.AppendChild(td);
            else
                row.InsertBefore(td, cells[index]);
            return td;
        }

        /// <summary>dom-tr-deletecell.</summary>
        public static void DeleteCell(Element row, int index)
        {
            var cells = GetCells(row);
            if (index == -1)
            {
                if (cells.Count == 0)
                    return;
                index = cells.Count - 1;
            }

            if (index < 0 || index >= cells.Count)
                throw new DomException("IndexSizeError", "The cell index is out of range");

            row.RemoveChild(cells[index]);
        }

        // --- HTMLTableCellElement ---

        /// <summary>dom-tdth-cellindex: the cell's index among its row's cells, or -1.</summary>
        public static int GetCellIndex(Element cell)
        {
            var row = cell.ParentElement;
            if (row == null || !IsRow(row))
                return -1;

            return GetCells(row).IndexOf(cell);
        }
    }
}
