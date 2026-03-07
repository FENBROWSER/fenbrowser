using System;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains;
using Xunit;

namespace FenBrowser.Tests.DevTools;

public class CSSDomainTests
{
    [Fact]
    public async Task GetComputedStyleForNode_AwaitsDispatcherAndReturnsComputedStyles()
    {
        var registry = new NodeRegistry();
        var element = new Element("div");
        var nodeId = registry.GetId(element);
        var dispatched = false;
        var styles = new CssComputed
        {
            Display = "block",
            Position = "relative",
            Width = 320,
            Height = 180,
            Margin = new Thickness(1, 2, 3, 4),
            Padding = new Thickness(5, 6, 7, 8),
            BorderThickness = new Thickness(9, 10, 11, 12),
            FontFamilyName = "Test Sans",
            FontSize = 16
        };
        styles.Map["custom-prop"] = "custom-value";

        var domain = new CSSDomain(
            registry,
            _ => styles,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatched = true;
                return operation();
            });

        using var paramsDoc = JsonDocument.Parse($"{{\"nodeId\":{nodeId}}}");
        var responseTask = domain.HandleAsync(
            "getComputedStyleForNode",
            new ProtocolRequest
            {
                Id = 1,
                Method = "CSS.getComputedStyleForNode",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.False(responseTask.IsCompleted, "CSS.getComputedStyleForNode should await the owning-thread dispatcher.");

        var response = await responseTask;
        var json = ProtocolJson.Serialize(response);

        Assert.True(dispatched);
        Assert.True(response.IsSuccess);
        Assert.Contains("\"name\":\"display\"", json, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"block\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"custom-prop\"", json, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"custom-value\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetStyleTexts_AwaitsDispatcherAndTriggersRepaint()
    {
        var registry = new NodeRegistry();
        var element = new Element("div");
        var nodeId = registry.GetId(element);
        var repaintCount = 0;
        var dispatched = false;
        var domain = new CSSDomain(
            registry,
            _ => new CssComputed(),
            setInlineStyle: (node, propertyName, value) =>
            {
                if (node is Element el)
                {
                    el.SetAttribute("style", $"{propertyName}: {value}");
                }
            },
            triggerRepaint: () => repaintCount++,
            dispatchAsync: async operation =>
            {
                await Task.Delay(25);
                dispatched = true;
                return operation();
            });

        using var paramsDoc = JsonDocument.Parse($"{{\"edits\":[{{\"nodeId\":{nodeId},\"propertyName\":\"display\",\"value\":\"grid\"}}]}}");
        var responseTask = domain.HandleAsync(
            "setStyleTexts",
            new ProtocolRequest
            {
                Id = 2,
                Method = "CSS.setStyleTexts",
                Params = paramsDoc.RootElement.Clone()
            });

        Assert.False(responseTask.IsCompleted, "CSS.setStyleTexts should await the owning-thread dispatcher.");
        Assert.Null(element.GetAttribute("style"));

        var response = await responseTask;

        Assert.True(dispatched);
        Assert.True(response.IsSuccess);
        Assert.Equal("display: grid", element.GetAttribute("style"));
        Assert.Equal(1, repaintCount);
    }
}
