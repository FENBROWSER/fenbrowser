using FenBrowser.Core.Dom.V2;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Registry for assigning stable integer IDs to DOM nodes.
/// Uses WeakReference to avoid preventing garbage collection.
/// </summary>
public interface INodeRegistry
{
    int GetId(Node node);
    Node? GetNode(int id);
    void Remove(int id);
    void Clear();
    bool IsRegistered(Node node);
}

/// <summary>
/// Implementation of INodeRegistry using WeakReference for GC-safety.
/// IDs are never reused during the registry lifetime, so a stale DevTools nodeId
/// cannot alias a different node after a document/navigation clear.
/// </summary>
public sealed class NodeRegistry : INodeRegistry
{
    private int _nextId = 1;
    private readonly object _lock = new();

    // Forward lookup: Node -> ID. ConditionalWeakTable doesn't prevent GC of keys.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Node, IdHolder> _nodeToId = new();

    // Reverse lookup: ID -> WeakReference<Node>.
    private readonly Dictionary<int, WeakReference<Node>> _idToNode = new();

    private sealed class IdHolder
    {
        public int Id { get; }
        public IdHolder(int id) => Id = id;
    }

    public int GetId(Node node)
    {
        if (node == null) throw new ArgumentNullException(nameof(node));

        lock (_lock)
        {
            if (_nodeToId.TryGetValue(node, out var holder))
                return holder.Id;

            if (_nextId <= 0 || _nextId == int.MaxValue)
            {
                throw new InvalidOperationException(
                    "DevTools node ID space is exhausted; IDs cannot be safely reused.");
            }

            var id = _nextId++;
            holder = new IdHolder(id);

            _nodeToId.Add(node, holder);
            _idToNode[id] = new WeakReference<Node>(node);
            return id;
        }
    }

    public Node? GetNode(int id)
    {
        lock (_lock)
        {
            if (_idToNode.TryGetValue(id, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var node))
                    return node;

                // Node was garbage collected, clean up the reverse entry.
                _idToNode.Remove(id);
            }

            return null;
        }
    }

    public void Remove(int id)
    {
        lock (_lock)
        {
            if (_idToNode.TryGetValue(id, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var node))
                    _nodeToId.Remove(node);
                _idToNode.Remove(id);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _nodeToId.Clear();
            _idToNode.Clear();
            // Do not reset _nextId. Reusing an old nodeId lets delayed/stale
            // DevTools commands accidentally target a node from a new document.
        }
    }

    public bool IsRegistered(Node node)
    {
        if (node == null) return false;

        lock (_lock)
        {
            return _nodeToId.TryGetValue(node, out _);
        }
    }

    /// <summary>
    /// Cleanup any stale WeakReferences (nodes that were GC'd).
    /// Call periodically or when memory pressure is detected.
    /// </summary>
    public void Cleanup()
    {
        lock (_lock)
        {
            var staleIds = new List<int>();
            foreach (var kvp in _idToNode)
            {
                if (!kvp.Value.TryGetTarget(out _))
                    staleIds.Add(kvp.Key);
            }

            foreach (var id in staleIds)
                _idToNode.Remove(id);
        }
    }
}
