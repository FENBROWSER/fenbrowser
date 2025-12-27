using FenBrowser.Core.Dom;

namespace FenBrowser.DevTools.Core;

/// <summary>
/// Registry for assigning stable integer IDs to DOM nodes.
/// Uses WeakReference to avoid preventing garbage collection.
/// </summary>
public interface INodeRegistry
{
    /// <summary>
    /// Get or assign an ID for a node.
    /// If the node already has an ID, returns it.
    /// If not, assigns a new unique ID.
    /// </summary>
    int GetId(Node node);
    
    /// <summary>
    /// Retrieve a node by its ID.
    /// Returns null if the node has been garbage collected.
    /// </summary>
    Node? GetNode(int id);
    
    /// <summary>
    /// Remove a node's registration (when node is destroyed).
    /// </summary>
    void Remove(int id);
    
    /// <summary>
    /// Clear all registrations.
    /// </summary>
    void Clear();
    
    /// <summary>
    /// Check if a node is registered.
    /// </summary>
    bool IsRegistered(Node node);
}

/// <summary>
/// Implementation of INodeRegistry using WeakReference for GC-safety.
/// </summary>
public sealed class NodeRegistry : INodeRegistry
{
    private int _nextId = 1;
    private readonly object _lock = new();
    
    // Forward lookup: Node -> ID
    // Uses ConditionalWeakTable which doesn't prevent GC of keys
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Node, IdHolder> _nodeToId = new();
    
    // Reverse lookup: ID -> WeakReference<Node>
    private readonly Dictionary<int, WeakReference<Node>> _idToNode = new();
    
    // Helper class to store ID as a reference type (required for ConditionalWeakTable)
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
            // Check if already registered
            if (_nodeToId.TryGetValue(node, out var holder))
            {
                return holder.Id;
            }
            
            // Assign new ID
            int id = _nextId++;
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
                {
                    return node;
                }
                
                // Node was garbage collected, clean up
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
                {
                    _nodeToId.Remove(node);
                }
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
            _nextId = 1;
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
                {
                    staleIds.Add(kvp.Key);
                }
            }
            
            foreach (var id in staleIds)
            {
                _idToNode.Remove(id);
            }
        }
    }
}
