using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Engine; // Phase protection

namespace FenBrowser.Core.Dom
{
    public enum NodeType
    {
        Element = 1,
        Attribute = 2,
        Text = 3,
        CDATA = 4,
        EntityReference = 5,
        Entity = 6,
        ProcessingInstruction = 7,
        Comment = 8,
        Document = 9,
        DocumentType = 10,
        DocumentFragment = 11,
        Notation = 12
    }

    /// <summary>
    /// Base class for all DOM nodes.
    /// Implements hierarchy management and EventTarget.
    /// </summary>
    public abstract class Node
    {
        public abstract NodeType NodeType { get; }
        public abstract string NodeName { get; }
        public virtual string NodeValue { get; set; }

        public Node Parent { get; internal set; }
        public List<Node> Children { get; private set; } = new List<Node>();
        public Document OwnerDocument { get; internal set; }

        // --- DOM Level 3 Events ---
        private Dictionary<string, List<EventListenerEntry>> _eventListeners;

        // --- MutationObserver integration ---
        [Obsolete("Use RegisterObserver/NotifyMutation instead")]
        public static Action<Node, string, string, string, List<Node>, List<Node>> OnMutation;

        private List<RegisteredObserver> _registeredObservers;

        internal class RegisteredObserver
        {
            public MutationObserver Observer { get; set; }
            public MutationObserverInit Options { get; set; }
        }

        public void RegisterObserver(MutationObserver observer, MutationObserverInit options)
        {
            if (observer == null) return;
            _registeredObservers ??= new List<RegisteredObserver>();
            // Check for existing registration and update options
            foreach (var reg in _registeredObservers)
            {
                if (ReferenceEquals(reg.Observer, observer))
                {
                    reg.Options = options;
                    return;
                }
            }
            _registeredObservers.Add(new RegisteredObserver { Observer = observer, Options = options });
        }

        public void UnregisterObserver(MutationObserver observer)
        {
            if (_registeredObservers == null || observer == null) return;
            _registeredObservers.RemoveAll(r => ReferenceEquals(r.Observer, observer));
        }

        protected void NotifyMutation(MutationRecord record)
        {
            if (record == null) return;
            
            // Legacy callback (for backward compat)
            OnMutation?.Invoke(record.Target, record.Type, record.AttributeName, record.OldValue, record.AddedNodes, record.RemovedNodes);

            // New observer dispatch
            NotifyObserversInternal(this, record, false);
        }

        private void NotifyObserversInternal(Node target, MutationRecord record, bool isSubtree)
        {
            if (_registeredObservers != null)
            {
                foreach (var reg in _registeredObservers)
                {
                    if (!ShouldNotify(reg.Options, record, isSubtree)) continue;
                    reg.Observer.EnqueueRecord(record);
                }
            }

            // Propagate up the tree for subtree observers
            if (Parent != null)
            {
                Parent.NotifyObserversInternal(target, record, true);
            }
        }

        private static bool ShouldNotify(MutationObserverInit opts, MutationRecord record, bool isSubtree)
        {
            if (isSubtree && !opts.Subtree) return false;
            switch (record.Type)
            {
                case "childList": return opts.ChildList;
                case "attributes":
                    if (!opts.Attributes) return false;
                    if (opts.AttributeFilter != null && opts.AttributeFilter.Length > 0)
                    {
                        bool found = false;
                        foreach (var f in opts.AttributeFilter)
                            if (string.Equals(f, record.AttributeName, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                        return found;
                    }
                    return true;
                case "characterData": return opts.CharacterData;
                default: return false;
            }
        }

        // ---- Backward Compatibility Aliases (for LiteElement migration) ----
        
        /// <summary>Alias for TagName (LiteElement compatibility) - returns NodeName for non-Element nodes</summary>
        public virtual string Tag => NodeName;
        
        /// <summary>Alias for Attributes (LiteElement compatibility) - returns null for non-Element nodes</summary>
        public virtual Dictionary<string, string> Attr => null;
        
        /// <summary>Text content property (LiteElement compatibility)</summary>
        public virtual string Text
        {
            get => NodeValue;
            set => NodeValue = value;
        }
        
        /// <summary>Check if this is a text node (LiteElement compatibility)</summary>
        public virtual bool IsText => NodeType == NodeType.Text;
        
        /// <summary>Get attribute value (LiteElement compatibility) - overridden in Element</summary>
        public virtual string GetAttribute(string name) => null;
        
        /// <summary>Set attribute value (LiteElement compatibility) - overridden in Element</summary>
        public virtual void SetAttribute(string name, string value) { }
        
        /// <summary>Check if has attribute (LiteElement compatibility) - overridden in Element</summary>
        public virtual bool HasAttribute(string name) => false;
        
        /// <summary>Remove attribute (LiteElement compatibility) - overridden in Element</summary>
        public virtual bool RemoveAttribute(string name) => false;
        
        /// <summary>Alias for AppendChild (LiteElement compatibility)</summary>
        public void Append(Node child) => AppendChild(child);

        // Hierarchy Methods

        // --- DOM Traversal Properties (DOM Living Standard §4.4) ---
        
        /// <summary>
        /// First child node (null if none).
        /// </summary>
        public Node FirstChild => Children.Count > 0 ? Children[0] : null;
        
        /// <summary>
        /// Last child node (null if none).
        /// </summary>
        public Node LastChild => Children.Count > 0 ? Children[Children.Count - 1] : null;
        
        /// <summary>
        /// Next sibling node (null if none or last child).
        /// </summary>
        public Node NextSibling
        {
            get
            {
                if (Parent == null) return null;
                var siblings = Parent.Children;
                var index = siblings.IndexOf(this);
                return (index >= 0 && index < siblings.Count - 1) ? siblings[index + 1] : null;
            }
        }
        
        /// <summary>
        /// Previous sibling node (null if none or first child).
        /// </summary>
        public Node PreviousSibling
        {
            get
            {
                if (Parent == null) return null;
                var siblings = Parent.Children;
                var index = siblings.IndexOf(this);
                return (index > 0) ? siblings[index - 1] : null;
            }
        }
        
        /// <summary>
        /// Parent as Element (null if parent is not an Element).
        /// </summary>
        public Element ParentElement => Parent as Element;
        
        /// <summary>
        /// Whether this node has child nodes.
        /// </summary>
        public bool HasChildNodes => Children.Count > 0;
        
        /// <summary>
        /// textContent per DOM Living Standard §4.4.3.
        /// Gets the text content of this node and its descendants.
        /// </summary>
        public virtual string TextContent
        {
            get
            {
                if (NodeType == NodeType.Text || NodeType == NodeType.Comment)
                    return NodeValue ?? "";
                
                // For elements/documents, concatenate all descendant text
                var sb = new System.Text.StringBuilder();
                CollectTextContent(this, sb);
                return sb.ToString();
            }
            set
            {
                // Remove all children and replace with a single text node
                while (Children.Count > 0)
                    Children[0].Remove();
                    
                if (!string.IsNullOrEmpty(value))
                    AppendChild(new Text(value));
            }
        }
        
        private static void CollectTextContent(Node node, System.Text.StringBuilder sb)
        {
            if (node.NodeType == NodeType.Text)
            {
                sb.Append(node.NodeValue);
                return;
            }
            
            foreach (var child in node.Children)
            {
                CollectTextContent(child, sb);
            }
        }


        public void AppendChild(Node child)
        {
            // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            if (child == null || child == this) return;
            // Prevent cycles
            for (var p = this; p != null; p = p.Parent) if (ReferenceEquals(p, child)) return;

            // Remove from old parent if attached
            child.Remove();

            child.Parent = this;
            child.OwnerDocument = this is Document doc ? doc : this.OwnerDocument;
            Children.Add(child);

            // Notify MutationObserver
            NotifyMutation(new MutationRecord
            {
                Type = "childList",
                Target = this,
                AddedNodes = new List<Node> { child }
            });
        }

        // --- Cloning ---
        
        /// <summary>
        /// Returns a duplicate of the node.
        /// </summary>
        /// <param name="deep">If true, recursively clone the subtree under the specified node; if false, clone only the node itself (and its attributes, if it is an Element).</param>
        public abstract Node CloneNode(bool deep);

        public void InsertBefore(Node newNode, Node referenceNode)
        {
            // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            if (newNode == null) return;
            if (referenceNode == null)
            {
                AppendChild(newNode);
                return;
            }

            if (!ReferenceEquals(referenceNode.Parent, this)) return;
            var idx = Children.IndexOf(referenceNode);
            if (idx < 0) return;

            newNode.Remove();
            newNode.Parent = this;
            newNode.OwnerDocument = this is Document doc ? doc : this.OwnerDocument;
            Children.Insert(idx, newNode);

            // Notify MutationObserver
            NotifyMutation(new MutationRecord
            {
                Type = "childList",
                Target = this,
                AddedNodes = new List<Node> { newNode }
            });
        }

        public void Remove()
        {
            // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            var p = Parent;
            if (p == null) return;
            p.Children.Remove(this);
            Parent = null;

            // Notify MutationObserver
            p.NotifyMutation(new MutationRecord
            {
                Type = "childList",
                Target = p,
                RemovedNodes = new List<Node> { this }
            });
        }

        public void RemoveAllChildren()
        {
             // Phase Guard
            EngineContext.Current.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);

            var removed = new List<Node>(Children);
            foreach (var child in Children)
                child.Parent = null;
            Children.Clear();
            
             // Notify MutationObserver
            if (removed.Count > 0)
                NotifyMutation(new MutationRecord
                {
                    Type = "childList",
                    Target = this,
                    RemovedNodes = removed
                });
        }

        public IEnumerable<Node> Descendants()
        {
            if (Children == null || Children.Count == 0) yield break;
            
            var stack = new Stack<Node>();
            // Push in reverse order to maintain original iteration order (first child processed first)
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                stack.Push(Children[i]);
            }
            
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                
                if (node.Children != null && node.Children.Count > 0)
                {
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(node.Children[i]);
                    }
                }
            }
        }
        
        public IEnumerable<Node> Ancestors()
        {
            var p = Parent;
            while (p != null) { yield return p; p = p.Parent; }
        }

         /// <summary>
        /// Add an event listener to this element (DOM Level 3 Events)
        /// </summary>
        public void AddEventListener(string type, object callback, bool capture = false, bool once = false, bool passive = false)
        {
            if (string.IsNullOrEmpty(type) || callback == null) return;

            if (_eventListeners == null)
                _eventListeners = new Dictionary<string, List<EventListenerEntry>>(StringComparer.OrdinalIgnoreCase);

            if (!_eventListeners.TryGetValue(type, out var list))
            {
                list = new List<EventListenerEntry>();
                _eventListeners[type] = list;
            }

            // Check for duplicate (same callback + same capture)
            foreach (var existing in list)
            {
                if (ReferenceEquals(existing.Callback, callback) && existing.Capture == capture)
                    return; // Already registered
            }

            list.Add(new EventListenerEntry
            {
                Callback = callback,
                Capture = capture,
                Once = once,
                Passive = passive
            });
        }

        /// <summary>
        /// Remove an event listener from this element (DOM Level 3 Events)
        /// </summary>
        public void RemoveEventListener(string type, object callback, bool capture = false)
        {
            if (string.IsNullOrEmpty(type) || callback == null || _eventListeners == null) return;

            if (!_eventListeners.TryGetValue(type, out var list)) return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i].Callback, callback) && list[i].Capture == capture)
                {
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        public List<EventListenerEntry> GetEventListeners(string type)
        {
             if (string.IsNullOrEmpty(type) || _eventListeners == null)
                return new List<EventListenerEntry>();

            return _eventListeners.TryGetValue(type, out var list) 
                ? new List<EventListenerEntry>(list) 
                : new List<EventListenerEntry>();
        }
    }

    public class EventListenerEntry
    {
        public object Callback { get; set; }
        public bool Capture { get; set; }
        public bool Once { get; set; }
        public bool Passive { get; set; }
    }
}
