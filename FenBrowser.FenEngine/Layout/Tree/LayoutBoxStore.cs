using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Memory;

namespace FenBrowser.FenEngine.Layout.Tree
{
    /// <summary>
    /// Struct-of-Arrays (SoA) backing store for layout boxes.
    /// Dramatically reduces LOH allocations by storing tree state in parallel arrays
    /// instead of thousands of individual heap-allocated objects.
    /// </summary>
    public sealed class LayoutBoxStore : IDisposable
    {
        private const int DefaultCapacity = 4096;

        // SoA parallel arrays
        private Node[] _sourceNodes;
        private CssComputed[] _styles;
        private BoxModel[] _geometries;
        private int[] _parentIds;
        private int[][] _childIds;
        private byte[] _boxTypes; // 0=Block, 1=Inline, 2=Text, 3=AnonymousBlock
        private bool[] _isAnonymous;

        private int _count;

        public enum BoxType : byte
        {
            Block = 0,
            Inline = 1,
            Text = 2,
            AnonymousBlock = 3
        }

        private LayoutBox[] _wrappers;

        public LayoutBoxStore(int capacity = DefaultCapacity)
        {
            AllocateArrays(capacity);
        }

        private void AllocateArrays(int capacity)
        {
            // Allocate from standard heap for references, but these grow gracefully
            // and are reused across frames via pooling, avoiding LOH churn per-frame.
            _sourceNodes = new Node[capacity];
            _styles = new CssComputed[capacity];
            _geometries = new BoxModel[capacity];
            _parentIds = new int[capacity];
            _childIds = new int[capacity][];
            _boxTypes = new byte[capacity];
            _isAnonymous = new bool[capacity];
            _wrappers = new LayoutBox[capacity];
        }

        private void EnsureCapacity()
        {
            if (_count >= _sourceNodes.Length)
            {
                int newCapacity = _sourceNodes.Length * 2;
                Array.Resize(ref _sourceNodes, newCapacity);
                Array.Resize(ref _styles, newCapacity);
                Array.Resize(ref _geometries, newCapacity);
                Array.Resize(ref _parentIds, newCapacity);
                Array.Resize(ref _childIds, newCapacity);
                Array.Resize(ref _boxTypes, newCapacity);
                Array.Resize(ref _isAnonymous, newCapacity);
                Array.Resize(ref _wrappers, newCapacity);
            }
        }

        public void Reset()
        {
            // Clear reference arrays to prevent memory leaks
            Array.Clear(_sourceNodes, 0, _count);
            Array.Clear(_styles, 0, _count);
            Array.Clear(_childIds, 0, _count);
            Array.Clear(_wrappers, 0, _count);
            _count = 0;
        }

        public int CreateBox(Node source, CssComputed style, BoxType type, bool isAnonymous = false)
        {
            EnsureCapacity();
            int id = _count++;

            _sourceNodes[id] = source;
            _styles[id] = style;
            
            if (_geometries[id] == null)
            {
                _geometries[id] = new BoxModel();
            }
            else
            {
                // Reset state for pooled instance
                var g = _geometries[id];
                g.MarginBox = default;
                g.BorderBox = default;
                g.PaddingBox = default;
                g.ContentBox = default;
                g.LogicalContentBox = default;
                g.Margin = new Thickness();
                g.Border = new Thickness();
                g.Padding = new Thickness();
                g.Baseline = 0;
                g.LineHeight = 0;
                g.Ascent = 0;
                g.Descent = 0;
                g.Transform = default;
                g.Lines = null;
            }

            _parentIds[id] = -1;
            _childIds[id] = Array.Empty<int>();
            _boxTypes[id] = (byte)type;
            _isAnonymous[id] = isAnonymous || source == null;

            return id;
        }

        public void AddChild(int parentId, int childId)
        {
            if (parentId < 0 || parentId >= _count) return;
            if (childId < 0 || childId >= _count) return;

            var currentChildren = _childIds[parentId];
            int currentLength = currentChildren.Length;

            // Small arrays won't hit LOH
            var newChildren = new int[currentLength + 1];
            if (currentLength > 0)
            {
                Array.Copy(currentChildren, newChildren, currentLength);
            }
            newChildren[currentLength] = childId;
            _childIds[parentId] = newChildren;

            _parentIds[childId] = parentId;
        }

        public void ClearChildren(int parentId)
        {
            if (parentId >= 0 && parentId < _count)
            {
                _childIds[parentId] = Array.Empty<int>();
            }
        }

        public void ReplaceChildren(int parentId, IReadOnlyList<int> newChildIds)
        {
            if (parentId < 0 || parentId >= _count) return;

            var newArray = new int[newChildIds.Count];
            for (int i = 0; i < newChildIds.Count; i++)
            {
                int childId = newChildIds[i];
                newArray[i] = childId;
                if (childId >= 0 && childId < _count)
                {
                    _parentIds[childId] = parentId;
                }
            }
            _childIds[parentId] = newArray;
        }

        public Node GetSourceNode(int id) => _sourceNodes[id];
        public CssComputed GetStyle(int id) => _styles[id];
        public void SetStyle(int id, CssComputed style) => _styles[id] = style;
        
        public ref BoxModel GetGeometry(int id) => ref _geometries[id];
        public void SetGeometry(int id, BoxModel geometry) => _geometries[id] = geometry;
        
        public int GetParentId(int id) => _parentIds[id];
        public void SetParent(int id, int parentId) => _parentIds[id] = parentId;
        
        public IReadOnlyList<int> GetChildIds(int id) => _childIds[id];
        public BoxType GetBoxType(int id) => (BoxType)_boxTypes[id];
        public bool GetIsAnonymous(int id) => _isAnonymous[id];
        public int Count => _count;

        public LayoutBox GetWrapper(int id)
        {
            if (id < 0 || id >= _count) return null;
            if (_wrappers[id] != null) return _wrappers[id];

            var type = (BoxType)_boxTypes[id];
            LayoutBox box = type switch
            {
                BoxType.Block => new BlockBox(this, id),
                BoxType.Inline => new InlineBox(this, id),
                BoxType.Text => new TextLayoutBox(this, id),
                BoxType.AnonymousBlock => new AnonymousBlockBox(this, id),
                _ => throw new InvalidOperationException("Unknown BoxType")
            };
            _wrappers[id] = box;
            return box;
        }

        public IList<LayoutBox> GetChildrenList(int id)
        {
            return new ChildrenListWrapper(this, id);
        }

        public void Dispose()
        {
            Reset();
        }

        private sealed class ChildrenListWrapper : IList<LayoutBox>
        {
            private readonly LayoutBoxStore _store;
            private readonly int _parentId;

            public ChildrenListWrapper(LayoutBoxStore store, int parentId)
            {
                _store = store;
                _parentId = parentId;
            }

            public LayoutBox this[int index] 
            { 
                get => _store.GetWrapper(_store._childIds[_parentId][index]); 
                set => throw new NotSupportedException("Use AddChild or ClearChildren instead"); 
            }

            public int Count => _store._childIds[_parentId].Length;
            public bool IsReadOnly => false;

            public void Add(LayoutBox item) => _store.AddChild(_parentId, item.StoreId);
            public void Clear() => _store.ClearChildren(_parentId);
            
            public bool Contains(LayoutBox item)
            {
                foreach (int childId in _store._childIds[_parentId])
                    if (childId == item.StoreId) return true;
                return false;
            }

            public void CopyTo(LayoutBox[] array, int arrayIndex)
            {
                var childIds = _store._childIds[_parentId];
                for (int i = 0; i < childIds.Length; i++)
                    array[arrayIndex + i] = _store.GetWrapper(childIds[i]);
            }

            public IEnumerator<LayoutBox> GetEnumerator()
            {
                var childIds = _store._childIds[_parentId];
                for (int i = 0; i < childIds.Length; i++)
                    yield return _store.GetWrapper(childIds[i]);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public int IndexOf(LayoutBox item)
            {
                var childIds = _store._childIds[_parentId];
                for (int i = 0; i < childIds.Length; i++)
                    if (childIds[i] == item.StoreId) return i;
                return -1;
            }

            public void Insert(int index, LayoutBox item)
            {
                var current = _store._childIds[_parentId];
                var newArray = new int[current.Length + 1];
                if (index > 0) Array.Copy(current, newArray, index);
                newArray[index] = item.StoreId;
                if (index < current.Length) Array.Copy(current, index, newArray, index + 1, current.Length - index);
                _store._childIds[_parentId] = newArray;
                _store._parentIds[item.StoreId] = _parentId;
            }

            public bool Remove(LayoutBox item)
            {
                int index = IndexOf(item);
                if (index >= 0) { RemoveAt(index); return true; }
                return false;
            }

            public void RemoveAt(int index)
            {
                var current = _store._childIds[_parentId];
                var newArray = new int[current.Length - 1];
                if (index > 0) Array.Copy(current, newArray, index);
                if (index < current.Length - 1) Array.Copy(current, index + 1, newArray, index, current.Length - index - 1);
                _store._childIds[_parentId] = newArray;
            }
        }
    }
}
