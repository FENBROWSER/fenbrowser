using FenBrowser.DevTools.Core.Protocol;
using FenBrowser.DevTools.Domains.DTOs;
using System.Text.Json;
using Xunit;

namespace FenBrowser.Tests.Core;

public class DevToolsDomProtocolShapeTests
{
    [Fact]
    public void DomNodeDto_Attributes_SerializeAsCdpNameValueArray()
    {
        var node = new DomNodeDto
        {
            NodeId = 1,
            NodeType = 1,
            NodeName = "DIV",
            Attributes = new Dictionary<string, string>
            {
                ["id"] = "root",
                ["class"] = "hero"
            }
        };

        var json = ProtocolJson.Serialize(node);
        using var doc = JsonDocument.Parse(json);
        var attributes = doc.RootElement.GetProperty("attributes");

        Assert.Equal(JsonValueKind.Array, attributes.ValueKind);
        Assert.Equal("id", attributes[0].GetString());
        Assert.Equal("root", attributes[1].GetString());
        Assert.Equal("class", attributes[2].GetString());
        Assert.Equal("hero", attributes[3].GetString());
    }

    [Fact]
    public void DomNodeDto_Attributes_DeserializeCdpNameValueArrayToDictionary()
    {
        var node = ProtocolJson.Deserialize<DomNodeDto>(
            """{"nodeId":1,"nodeType":1,"nodeName":"DIV","attributes":["id","root","class","hero"],"childNodeCount":0}""");

        Assert.NotNull(node);
        Assert.Equal("root", node!.Attributes!["id"]);
        Assert.Equal("hero", node.Attributes["class"]);
    }
}
