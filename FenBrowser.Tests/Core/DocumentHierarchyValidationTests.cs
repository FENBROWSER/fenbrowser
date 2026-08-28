using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class DocumentHierarchyValidationTests
{
    [Fact]
    public void AppendChildRejectsFragmentWithMultipleDocumentElementsAtomically()
    {
        var document = new Document();
        var fragment = document.CreateDocumentFragment();
        var first = document.CreateElement("html");
        var second = document.CreateElement("body");
        fragment.AppendChild(first);
        fragment.AppendChild(second);

        var error = Assert.Throws<DomException>(() => document.AppendChild(fragment));

        Assert.Equal("HierarchyRequestError", error.Name);
        Assert.Null(document.FirstChild);
        Assert.Same(first, fragment.FirstChild);
        Assert.Same(second, fragment.LastChild);
    }

    [Fact]
    public void AppendChildRejectsFragmentWithTextAtomically()
    {
        var document = new Document();
        var fragment = document.CreateDocumentFragment();
        var text = document.CreateTextNode("not allowed at document level");
        fragment.AppendChild(text);

        var error = Assert.Throws<DomException>(() => document.AppendChild(fragment));

        Assert.Equal("HierarchyRequestError", error.Name);
        Assert.Null(document.FirstChild);
        Assert.Same(text, fragment.FirstChild);
    }

    [Fact]
    public void ReplaceChildRejectsInvalidFragmentWithoutRemovingCurrentRoot()
    {
        var document = new Document();
        var currentRoot = document.CreateElement("html");
        document.AppendChild(currentRoot);
        var fragment = document.CreateDocumentFragment();
        fragment.AppendChild(document.CreateElement("main"));
        fragment.AppendChild(document.CreateElement("aside"));

        var error = Assert.Throws<DomException>(() => document.ReplaceChild(fragment, currentRoot));

        Assert.Equal("HierarchyRequestError", error.Name);
        Assert.Same(currentRoot, document.DocumentElement);
        Assert.Same(document, currentRoot.ParentNode);
        Assert.NotNull(fragment.FirstChild);
        Assert.NotSame(fragment.FirstChild, fragment.LastChild);
    }

    [Fact]
    public void ReplaceChildAllowsOneDocumentElementToReplaceAnother()
    {
        var document = new Document();
        var oldRoot = document.CreateElement("html");
        var newRoot = document.CreateElement("svg");
        document.AppendChild(oldRoot);

        var removed = document.ReplaceChild(newRoot, oldRoot);

        Assert.Same(oldRoot, removed);
        Assert.Null(oldRoot.ParentNode);
        Assert.Same(newRoot, document.DocumentElement);
    }

    [Fact]
    public void InsertBeforeAllowsDoctypeBeforeDocumentElement()
    {
        var document = new Document();
        var doctype = document.CreateDocumentType("html");
        var root = document.CreateElement("html");
        document.AppendChild(root);

        document.InsertBefore(doctype, root);

        Assert.Same(doctype, document.FirstChild);
        Assert.Same(root, document.LastChild);
    }

    [Fact]
    public void AppendChildRejectsDoctypeAfterDocumentElement()
    {
        var document = new Document();
        var root = document.CreateElement("html");
        var doctype = document.CreateDocumentType("html");
        document.AppendChild(root);

        var error = Assert.Throws<DomException>(() => document.AppendChild(doctype));

        Assert.Equal("HierarchyRequestError", error.Name);
        Assert.Same(root, document.FirstChild);
        Assert.Null(doctype.ParentNode);
    }

    [Fact]
    public void ReinsertingExistingDocumentElementDoesNotCreateADuplicate()
    {
        var document = new Document();
        var root = document.CreateElement("html");
        document.AppendChild(root);

        Assert.Same(root, document.InsertBefore(root, root));
        Assert.Same(root, document.AppendChild(root));

        Assert.Same(root, document.FirstChild);
        Assert.Same(root, document.LastChild);
    }
}
