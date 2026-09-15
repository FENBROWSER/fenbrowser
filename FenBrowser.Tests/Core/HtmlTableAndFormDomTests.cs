using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>HTML §4.9 table DOM and §4.10 form-associated element algorithms.</summary>
public class HtmlTableAndFormDomTests
{
    [Fact]
    public void TableAccessorsCreateAndDeleteSections()
    {
        var doc = HtmlParser.ParseDocument("<table id='t'></table>");
        var table = doc.GetElementById("t");

        Assert.Null(HtmlTableDom.GetCaption(table));
        var caption = HtmlTableDom.CreateCaption(table);
        var thead = HtmlTableDom.CreateTHead(table);
        var tfoot = HtmlTableDom.CreateTFoot(table);
        Assert.Same(caption, HtmlTableDom.GetCaption(table));
        Assert.Same(thead, HtmlTableDom.GetTHead(table));
        Assert.Same(tfoot, HtmlTableDom.GetTFoot(table));
        Assert.Same(caption, table.FirstChild);
        Assert.Same(thead, caption.NextSibling);
        Assert.Same(caption, HtmlTableDom.CreateCaption(table));
        Assert.Equal(0, HtmlTableDom.GetTBodies(table).Length);

        HtmlTableDom.DeleteCaption(table);
        HtmlTableDom.DeleteTHead(table);
        HtmlTableDom.DeleteTFoot(table);
        Assert.False(table.HasChildNodes);
    }

    [Fact]
    public void RowsAreOrderedHeadBodyFootAndInsertRowCreatesATBody()
    {
        var doc = HtmlParser.ParseDocument(
            "<table id='t'><tfoot><tr id='f'></tr></tfoot><tbody><tr id='b'></tr></tbody><thead><tr id='h'></tr></thead></table>");
        var table = doc.GetElementById("t");

        var rows = HtmlTableDom.GetRows(table);
        Assert.Equal(new[] { "h", "b", "f" }, rows.ConvertAll(r => r.Id));
        Assert.Equal(1, HtmlTableDom.GetRowIndex(doc.GetElementById("b")));
        Assert.Equal(0, HtmlTableDom.GetSectionRowIndex(doc.GetElementById("b")));

        var empty = doc.CreateElement("table");
        var tr = HtmlTableDom.InsertRow(empty, -1);
        Assert.Equal("tbody", tr.ParentElement.LocalName);
        Assert.Equal(1, HtmlTableDom.GetTBodies(empty).Length);
        Assert.Throws<DomException>(() => HtmlTableDom.InsertRow(empty, 5));
        HtmlTableDom.DeleteRow(empty, -1);
        Assert.Empty(HtmlTableDom.GetRows(empty));
    }

    [Fact]
    public void CellsAndCellIndex()
    {
        var doc = HtmlParser.ParseDocument("<table><tr id='r'><td></td><th id='c'></th></tr></table>");
        var row = doc.GetElementById("r");
        Assert.Equal(2, HtmlTableDom.GetCells(row).Count);
        Assert.Equal(1, HtmlTableDom.GetCellIndex(doc.GetElementById("c")));
        var td = HtmlTableDom.InsertCell(row, 0);
        Assert.Same(td, row.FirstChild);
        HtmlTableDom.DeleteCell(row, 0);
        Assert.Equal(2, HtmlTableDom.GetCells(row).Count);
    }

    [Fact]
    public void FormListedElementsFollowFormOwnership()
    {
        var doc = HtmlParser.ParseDocument(
            "<form id='f'><input name='a'><input type='image' name='img'><select name='s'></select></form>" +
            "<input name='outside' form='f'><textarea name='free'></textarea>");
        var form = doc.GetElementById("f");
        var listed = HtmlFormDom.ListedElements(form);
        Assert.Equal(new[] { "a", "s", "outside" }, listed.ConvertAll(e => e.GetAttribute("name")));
        Assert.Same(form, HtmlFormDom.FormOwner(listed[0]));
        Assert.Null(HtmlFormDom.FormOwner(doc.Body.LastChild as Element));
    }

    [Fact]
    public void SelectAddInsertsBeforeOptionOrIndex()
    {
        var doc = HtmlParser.ParseDocument("<select id='s'><option id='a'></option><optgroup><option id='b'></option></optgroup></select>");
        var select = doc.GetElementById("s");
        Assert.Equal(2, HtmlFormDom.Options(select).Count);

        var added = doc.CreateElement("option");
        HtmlFormDom.Add(select, added, null, 1);
        Assert.Same(added, doc.GetElementById("b").PreviousSibling);
        Assert.Equal(1, HtmlFormDom.OptionIndex(added));

        var appended = doc.CreateElement("option");
        HtmlFormDom.Add(select, appended, null, null);
        Assert.Same(appended, select.LastChild);
        Assert.Throws<DomException>(() => HtmlFormDom.Add(select, doc.CreateElement("option"), doc.CreateElement("option"), null));

        HtmlFormDom.Remove(select, 0);
        Assert.Null(doc.GetElementById("a"));
    }
}
