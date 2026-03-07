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
}
