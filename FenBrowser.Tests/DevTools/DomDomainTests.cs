using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using FenBrowser.DevTools.Domains.DTOs;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class DomDomainTests
{
    [Fact]
    public async Task GetDocumentAsync_AwaitsDispatcherAndBuildsDocumentSnapshot()
    {
        var registry = new NodeRegistry();
        var document = Document.CreateHtmlDocument("dispatcher");
        document.Body!.AppendChild(document.CreateElement("main"));
        var dispatched = false;
        var domain = new DomDomain(
            registry,
            () => document,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatched = true;
                return operation();
            });

        var responseTask = domain.HandleAsync("getDocument", new ProtocolRequest { Id = 1, Method = "DOM.getDocument" });

        Assert.False(responseTask.IsCompleted, "DOM.getDocument should await the owning-thread dispatcher.");

        var response = await responseTask;

        Assert.True(dispatched);
        Assert.True(response.IsSuccess);
        var result = Assert.IsType<GetDocumentResult>(response.Result);
        Assert.Equal("#document", result.Root.NodeName);
        Assert.Equal(2, result.Root.ChildNodeCount);
    }

    [Fact]
    public async Task SetAttributeValueAsync_AwaitsDispatcherBeforeMutatingElement()
    {
        var registry = new NodeRegistry();
        var element = new Element("div");
        var nodeId = registry.GetId(element);
        var dispatched = false;
        var domain = new DomDomain(
            registry,
            () => element,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatched = true;
                return operation();
            });

        using var paramsDoc = JsonDocument.Parse($"{{\"nodeId\":{nodeId},\"name\":\"data-test\",\"value\":\"value\"}}");
        var responseTask = domain.HandleAsync(
            "setAttributeValue",
            new ProtocolRequest
            {
                Id = 2,
                Method = "DOM.setAttributeValue",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.False(responseTask.IsCompleted, "DOM.setAttributeValue should await the owning-thread dispatcher.");
        Assert.Null(element.GetAttribute("data-test"));

        var response = await responseTask;

        Assert.True(dispatched);
        Assert.True(response.IsSuccess);
        Assert.Equal("value", element.GetAttribute("data-test"));
    }

    [Fact]
    public async Task SetOuterHtmlAsync_AwaitsDispatcherAndReplacesNodeWithParsedMarkup()
    {
        var registry = new NodeRegistry();
        var document = Document.CreateHtmlDocument("outer-html");
        var original = document.CreateElement("div");
        original.SetAttribute("id", "old");
        original.AppendChild(document.CreateTextNode("legacy"));
        document.Body!.AppendChild(original);

        var nodeId = registry.GetId(original);
        var dispatched = false;
        var domain = new DomDomain(
            registry,
            () => document,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatched = true;
                return operation();
            });

        using var paramsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            nodeId,
            outerHTML = "<section id=\"new\"><b>fresh</b></section><!--tail-->"
        }));

        var responseTask = domain.HandleAsync(
            "setOuterHTML",
            new ProtocolRequest
            {
                Id = 3,
                Method = "DOM.setOuterHTML",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.False(responseTask.IsCompleted, "DOM.setOuterHTML should await the owning-thread dispatcher.");
        Assert.Equal("old", ((Element)document.Body.FirstChild!).Id);

        var response = await responseTask;

        Assert.True(dispatched);
        Assert.True(response.IsSuccess);

        var replacement = Assert.IsType<Element>(document.Body.FirstChild);
        Assert.Equal("section", replacement.LocalName);
        Assert.Equal("new", replacement.Id);
        Assert.Equal("fresh", replacement.TextContent);

        var trailingComment = Assert.IsType<Comment>(replacement.NextSibling);
        Assert.Equal("tail", trailingComment.Data);
        Assert.Null(original.ParentNode);
    }

    [Fact]
    public async Task GetOuterHtmlAsync_AwaitsDispatcherAndReturnsExactMarkup()
    {
        var registry = new NodeRegistry();
        var document = Document.CreateHtmlDocument("markup");
        var host = document.CreateElement("section");
        host.SetAttribute("id", "hero");
        host.AppendChild(document.CreateTextNode("Hello "));
        var strong = document.CreateElement("strong");
        strong.AppendChild(document.CreateTextNode("Fen"));
        host.AppendChild(strong);
        host.AppendChild(document.CreateComment("marker"));
        document.Body!.AppendChild(host);

        var nodeId = registry.GetId(host);
        var dispatched = false;
        var domain = new DomDomain(
            registry,
            () => document,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatched = true;
                return operation();
            });

        using var paramsDoc = JsonDocument.Parse($"{{\"nodeId\":{nodeId}}}");
        var responseTask = domain.HandleAsync(
            "getOuterHTML",
            new ProtocolRequest
            {
                Id = 4,
                Method = "DOM.getOuterHTML",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.False(responseTask.IsCompleted, "DOM.getOuterHTML should await the owning-thread dispatcher.");

        var response = await responseTask;

        Assert.True(dispatched);
        Assert.True(response.IsSuccess);
        var result = Assert.IsType<GetOuterHtmlResult>(response.Result);
        Assert.Equal("<section id=\"hero\">Hello <strong>Fen</strong><!--marker--></section>", result.OuterHTML);
    }

    [Fact]
    public async Task QuerySelectorAndQuerySelectorAll_AwaitDispatcherAndReturnStableNodeIds()
    {
        var registry = new NodeRegistry();
        var document = Document.CreateHtmlDocument("selectors");
        var shell = document.CreateElement("div");
        shell.SetAttribute("id", "shell");
        document.Body!.AppendChild(shell);

        var first = document.CreateElement("article");
        first.SetAttribute("class", "card primary");
        shell.AppendChild(first);

        var second = document.CreateElement("article");
        second.SetAttribute("class", "card");
        shell.AppendChild(second);

        var rootId = registry.GetId(shell);
        var dispatchedCount = 0;
        var domain = new DomDomain(
            registry,
            () => document,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatchedCount++;
                return operation();
            });

        using var firstParams = JsonDocument.Parse($"{{\"nodeId\":{rootId},\"selector\":\"article.primary\"}}");
        var singleResponseTask = domain.HandleAsync(
            "querySelector",
            new ProtocolRequest
            {
                Id = 5,
                Method = "DOM.querySelector",
                Params = firstParams.RootElement.Clone()
            });

        Assert.False(singleResponseTask.IsCompleted, "DOM.querySelector should await the owning-thread dispatcher.");

        var singleResponse = await singleResponseTask;
        Assert.True(singleResponse.IsSuccess);
        var singleResult = Assert.IsType<QuerySelectorResult>(singleResponse.Result);
        Assert.Equal(registry.GetId(first), singleResult.NodeId);

        using var allParams = JsonDocument.Parse($"{{\"nodeId\":{rootId},\"selector\":\"article.card\"}}");
        var multiResponse = await domain.HandleAsync(
            "querySelectorAll",
            new ProtocolRequest
            {
                Id = 6,
                Method = "DOM.querySelectorAll",
                Params = allParams.RootElement.Clone()
            });

        Assert.True(multiResponse.IsSuccess);
        var allResult = Assert.IsType<QuerySelectorAllResult>(multiResponse.Result);
        Assert.Equal(new[] { registry.GetId(first), registry.GetId(second) }, allResult.NodeIds);
        Assert.Equal(2, dispatchedCount);
    }
}
