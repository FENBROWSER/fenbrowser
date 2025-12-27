using FenBrowser.Core.Dom;
using FenBrowser.DevTools.Core;
using FenBrowser.DevTools.Domains.DTOs;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using System.Linq;

namespace FenBrowser.DevTools.Instrumentation;

/// <summary>
/// Bridges raw DOM mutations to the DevTools protocol events.
/// </summary>
public class DomInstrumenter
{
    private readonly DevToolsServer _server;
    private readonly INodeRegistry _registry;
    private readonly object _lock = new();
    private readonly List<Action> _queue = new();
    private bool _flushScheduled = false;

    public DomInstrumenter(DevToolsServer server)
    {
        _server = server;
        _registry = server.Registry;

        // Hook into engine mutations
        Node.OnMutation += HandleMutation;
    }

    private void HandleMutation(Node target, string type, string attrName, string attrNamespace, List<Node> addedNodes, List<Node> removedNodes)
    {
        // Only process if the target node is already registered (observed by DevTools)
        if (!_registry.IsRegistered(target)) return;

        int targetId = _registry.GetId(target);

        switch (type)
        {
            case "childList":
                if (addedNodes != null)
                {
                    foreach (var node in addedNodes)
                    {
                        var eventParams = new ChildNodeInsertedEvent
                        {
                            ParentNodeId = targetId,
                            PreviousNodeId = GetPreviousNodeId(target, node),
                            Node = BuildNodeDto(node)
                        };
                        Enqueue(() => _server.BroadcastDomEvent("childNodeInserted", eventParams));
                    }
                }

                if (removedNodes != null)
                {
                    foreach (var node in removedNodes)
                    {
                        var eventParams = new ChildNodeRemovedEvent
                        {
                            ParentNodeId = targetId,
                            NodeId = _registry.GetId(node)
                        };
                        Enqueue(() => _server.BroadcastDomEvent("childNodeRemoved", eventParams));
                    }
                }
                break;

            case "attributes":
                if (target is Element element)
                {
                    var val = element.GetAttribute(attrName);
                    if (val != null)
                    {
                        var eventParams = new AttributeModifiedEvent
                        {
                            NodeId = targetId,
                            Name = attrName,
                            Value = val
                        };
                        Enqueue(() => _server.BroadcastDomEvent("attributeModified", eventParams));
                    }
                    else
                    {
                        var eventParams = new AttributeRemovedEvent
                        {
                            NodeId = targetId,
                            Name = attrName
                        };
                        Enqueue(() => _server.BroadcastDomEvent("attributeRemoved", eventParams));
                    }
                }
                break;

            case "characterData":
                var charEventParams = new CharacterDataModifiedEvent
                {
                    NodeId = targetId,
                    CharacterData = target.NodeValue ?? ""
                };
                Enqueue(() => _server.BroadcastDomEvent("characterDataModified", charEventParams));
                break;
        }
    }

    private void Enqueue(Action action)
    {
        lock (_lock)
        {
            _queue.Add(action);
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                EventLoopCoordinator.Instance.EnqueueMicrotask(FlushMutations);
            }
        }
    }

    private void FlushMutations()
    {
        List<Action> toFlush;
        lock (_lock)
        {
            toFlush = _queue.ToList();
            _queue.Clear();
            _flushScheduled = false;
        }

        foreach (var action in toFlush)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[DomInstrumenter] Error flushing mutation: {ex.Message}", LogCategory.General);
            }
        }
    }

    private int GetPreviousNodeId(Node parent, Node node)
    {
        int idx = parent.Children.IndexOf(node);
        if (idx <= 0) return 0;
        return _registry.GetId(parent.Children[idx - 1]);
    }

    private DomNodeDto BuildNodeDto(Node node)
    {
        // Helper to build a DTO for a newly inserted node.
        // We usually only send the immediate node for insertions, 
        // and the UI can request children later if needed.
        
        int nodeId = _registry.GetId(node);
        int? parentId = node.Parent != null ? (int?)_registry.GetId(node.Parent) : null;

        int nodeType = node switch
        {
            FenBrowser.Core.Dom.Document => 9,
            FenBrowser.Core.Dom.Text => 3,
            FenBrowser.Core.Dom.Element => 1,
            _ => 0
        };

        string nodeName = node switch
        {
            Document => "#document",
            Text => "#text",
            Element el => el.TagName?.ToUpper() ?? "UNKNOWN",
            _ => node.GetType().Name
        };

        Dictionary<string, string>? attributes = null;
        if (node is Element e && e.Attributes.Count > 0)
        {
            attributes = new Dictionary<string, string>(e.Attributes);
        }

        return new DomNodeDto
        {
            NodeId = nodeId,
            ParentId = parentId,
            NodeType = nodeType,
            NodeName = nodeName,
            NodeValue = node is Text t ? t.Data : null,
            Attributes = attributes,
            ChildNodeCount = node.Children.Count
        };
    }
}
