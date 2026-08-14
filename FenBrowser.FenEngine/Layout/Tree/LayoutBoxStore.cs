using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Memory;

namespace FenBrowser.FenEngine.Layout.Tree
{
    /// <summary>
    /// Struct-of-Arrays (SoA) backing store for layout boxes.
    /// Tree child storage is allocated lazily per parent and reused across resets so
    /// building a wide sibling list is amortized O(1) per append rather than O(N)
    /// array-copy work for every child.
    /// </summary>
    public sealed class LayoutBoxStore : IDisposable
    {
        private const int DefaultCapacity = 4096;
        private static readonly IReadOnlyList<int> EmptyChildIds = Array.Empty<int>();

        private Node[] _sourceNodes;
        private CssComputed[] _styles;
        private BoxModel[] _geometries;
        private int[] _parentIds;
        private List<int>[] _childIds;
        private byte[] _boxTypes;
        private bool[] _isAnonymous;
        private LayoutBox[] _wrappers;

        private int _count;
        private int _generation = 1;

        public enum BoxType : byte
        {
            Block = 0,
            Inline = 1,
            Text = 2,
            AnonymousBlock = 3,
            ListItem = 4,
            ListMarker = 5
        }

        public LayoutBoxStore(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
            {
                capacity = DefaultCapacity;
            }

            AllocateArrays(capacity);
        }

        private void AllocateArrays(int capacity)
        {
            _sourceNodes = new Node[capacity];
            _styles = new CssComputed[capacity];
            _geometries = new BoxModel[capacity];
            _parentIds = new int[capacity];
            _childIds = new List<int>[capacity];
            _boxTypes = new byte[capacity];
            _isAnonymous = new bool[capacity];
            _wrappers = new LayoutBox[capacity];
        }

        private void EnsureCapacity()
        {
            if (_count < _sourceNodes.Length)
            {
                return;
            }

            int newCapacity = checked(_sourceNodes.Length * 2);
            Array.Resize(ref _sourceNodes, newCapacity);
            Array.Resize(ref _styles, newCapacity);
            Array.Resize(ref _geometries, newCapacity);
            Array.Resize(ref _parentIds, newCapacity);
            Array.Resize(ref _childIds, newCapacity);
            Array.Resize(ref _boxTypes, newCapacity);
            Array.Resize(ref _isAnonymous, newCapacity);
            Array.Resize(ref _wrappers, newCapacity);
        }

        public void Reset()
        {
            unchecked
            {
                _generation++;
                if (_generation == 0)
                {
                    _generation = 1;
                }
            }

            Array.Clear(_sourceNodes, 0, _count);
            Array.Clear(_styles, 0, _count);
            Array.Clear(_wrappers, 0, _count);

            // Keep allocated child buffers so repeated layout passes reuse their
            // capacity, but clear logical contents and parent links.
            for (int i = 0; i < _count; i++)
            {
                _childIds[i]?.Clear();
                _parentIds[i] = -1;
            }

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
            _childIds[id]?.Clear();
            _boxTypes[id] = (byte)type;
            _isAnonymous[id] = isAnonymous || source == null;

            return id;
        }

        public void AddChild(int parentId, int childId)
        {
            ValidateTreeMutation(parentId, childId);

            var oldParentId = _parentIds[childId];
            if (oldParentId == parentId)
            {
                if (IndexOfChild(parentId, childId) >= 0)
                {
                    return;
                }
            }
            else if (oldParentId >= 0 && oldParentId < _count)
            {
                RemoveChildId(oldParentId, childId, clearParent: false);
            }

            var children = _childIds[parentId] ??= new List<int>(4);
            children.Add(childId);
            _parentIds[childId] = parentId;
        }

        public void ClearChildren(int parentId)
        {
            if ((uint)parentId >= (uint)_count)
            {
                return;
            }

            var children = _childIds[parentId];
            if (children == null || children.Count == 0)
            {
                return;
            }

            for (int i = 0; i < children.Count; i++)
            {
                var childId = children[i];
                if ((uint)childId < (uint)_count && _parentIds[childId] == parentId)
                {
                    _parentIds[childId] = -1;
                }
            }

            children.Clear();
        }

        public void ReplaceChildren(int parentId, IReadOnlyList<int> newChildIds)
        {
            if ((uint)parentId >= (uint)_count)
            {
                return;
            }
            if (newChildIds == null)
            {
                throw new ArgumentNullException(nameof(newChildIds));
            }

            var unique = new HashSet<int>();
            for (int i = 0; i < newChildIds.Count; i++)
            {
                int childId = newChildIds[i];
                ValidateTreeMutation(parentId, childId);
                if (!unique.Add(childId))
                {
                    throw new InvalidOperationException("A layout box cannot appear more than once under the same parent.");
                }
            }

            var oldChildren = _childIds[parentId];
            if (oldChildren != null)
            {
                for (int i = 0; i < oldChildren.Count; i++)
                {
                    var oldChildId = oldChildren[i];
                    if (!unique.Contains(oldChildId) &&
                        (uint)oldChildId < (uint)_count &&
                        _parentIds[oldChildId] == parentId)
                    {
                        _parentIds[oldChildId] = -1;
                    }
                }
            }

            for (int i = 0; i < newChildIds.Count; i++)
            {
                var childId = newChildIds[i];
                var oldParentId = _parentIds[childId];
                if (oldParentId >= 0 && oldParentId != parentId && oldParentId < _count)
                {
                    RemoveChildId(oldParentId, childId, clearParent: false);
                }
                _parentIds[childId] = parentId;
            }

            var children = _childIds[parentId] ??= new List<int>(Math.Max(4, newChildIds.Count));
            children.Clear();
            if (children.Capacity < newChildIds.Count)
            {
                children.Capacity = newChildIds.Count;
            }
            for (int i = 0; i < newChildIds.Count; i++)
            {
                children.Add(newChildIds[i]);
            }
        }

        public Node GetSourceNode(int id) => _sourceNodes[id];
        public CssComputed GetStyle(int id) => _styles[id];
        public void SetStyle(int id, CssComputed style) => _styles[id] = style;

        public ref BoxModel GetGeometry(int id) => ref _geometries[id];
        public void SetGeometry(int id, BoxModel geometry) => _geometries[id] = geometry;

        public int GetParentId(int id) => _parentIds[id];

        public void SetParent(int id, int parentId)
        {
            if ((uint)id >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(id));
            }

            if (parentId < 0)
            {
                var oldParentId = _parentIds[id];
                if (oldParentId >= 0 && oldParentId < _count)
                {
                    RemoveChildId(oldParentId, id, clearParent: false);
                }
                _parentIds[id] = -1;
                return;
            }

            AddChild(parentId, id);
        }

        public IReadOnlyList<int> GetChildIds(int id) => _childIds[id] ?? EmptyChildIds;
        public BoxType GetBoxType(int id) => (BoxType)_boxTypes[id];
        public bool GetIsAnonymous(int id) => _isAnonymous[id];
        public int Count => _count;
        internal int Generation => _generation;

        internal void ValidateAccess(int id, int expectedGeneration)
        {
            if (expectedGeneration != _generation)
            {
                throw new InvalidOperationException("LayoutBox is stale: underlying LayoutBoxStore generation has advanced.");
            }

            if ((uint)id >= (uint)_count)
            {
                throw new InvalidOperationException("LayoutBox is stale: requested node id is outside the active layout tree.");
            }
        }

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
                BoxType.ListItem => new ListItemBox(this, id),
                BoxType.ListMarker => new ListMarkerBox(this, id),
                _ => throw new InvalidOperationException("Unknown BoxType")
            };
            _wrappers[id] = box;
            return box;
        }

        public IList<LayoutBox> GetChildrenList(int id) => new ChildrenListWrapper(this, id);

        public void Dispose() => Reset();

        private void ValidateTreeMutation(int parentId, int childId)
        {
            if ((uint)parentId >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(parentId));
            }
            if ((uint)childId >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(childId));
            }
            if (parentId == childId)
            {
                throw new InvalidOperationException("A layout box cannot be its own parent.");
            }

            var ancestorId = parentId;
            var hops = 0;
            while (ancestorId >= 0)
            {
                if (ancestorId == childId)
                {
                    throw new InvalidOperationException("Layout tree mutation would create a parent/child cycle.");
                }

                if ((uint)ancestorId >= (uint)_count || ++hops > _count)
                {
                    throw new InvalidOperationException("Layout tree contains an invalid or cyclic parent chain.");
                }
                ancestorId = _parentIds[ancestorId];
            }
        }

        private int IndexOfChild(int parentId, int childId)
        {
            var children = _childIds[parentId];
            return children?.IndexOf(childId) ?? -1;
        }

        private void RemoveChildId(int parentId, int childId, bool clearParent)
        {
            var index = IndexOfChild(parentId, childId);
            if (index < 0)
            {
                if (clearParent && (uint)childId < (uint)_count && _parentIds[childId] == parentId)
                {
                    _parentIds[childId] = -1;
                }
                return;
            }

            RemoveChildAt(parentId, index, clearParent);
        }

        private void RemoveChildAt(int parentId, int index, bool clearParent = true)
        {
            var children = _childIds[parentId];
            if (children == null || (uint)index >= (uint)children.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var removedChildId = children[index];
            children.RemoveAt(index);

            if (clearParent &&
                (uint)removedChildId < (uint)_count &&
                _parentIds[removedChildId] == parentId)
            {
                _parentIds[removedChildId] = -1;
            }
        }

        private void ReplaceChildAt(int parentId, int index, int childId)
        {
            ValidateTreeMutation(parentId, childId);
            var children = _childIds[parentId];
            if (children == null || (uint)index >= (uint)children.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var previousChildId = children[index];
            if (previousChildId == childId)
            {
                return;
            }
            if (IndexOfChild(parentId, childId) >= 0)
            {
                throw new InvalidOperationException("A layout box cannot appear more than once under the same parent.");
            }

            var oldParentId = _parentIds[childId];
            if (oldParentId >= 0 && oldParentId != parentId && oldParentId < _count)
            {
                RemoveChildId(oldParentId, childId, clearParent: false);
            }

            children[index] = childId;
            if ((uint)previousChildId < (uint)_count && _parentIds[previousChildId] == parentId)
            {
                _parentIds[previousChildId] = -1;
            }
            _parentIds[childId] = parentId;
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

            private List<int> ChildIds => _store._childIds[_parentId];

            public LayoutBox this[int index]
            {
                get
                {
                    var childIds = ChildIds;
                    if (childIds == null || (uint)index >= (uint)childIds.Count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }
                    return _store.GetWrapper(childIds[index]);
                }
                set
                {
                    ValidateItem(value);
                    _store.ReplaceChildAt(_parentId, index, value.StoreId);
                }
            }

            public int Count => ChildIds?.Count ?? 0;
            public bool IsReadOnly => false;

            public void Add(LayoutBox item)
            {
                ValidateItem(item);
                _store.AddChild(_parentId, item.StoreId);
            }

            public void Clear() => _store.ClearChildren(_parentId);

            public bool Contains(LayoutBox item)
            {
                if (item == null || !ReferenceEquals(item.Store, _store)) return false;
                return _store.IndexOfChild(_parentId, item.StoreId) >= 0;
            }

            public void CopyTo(LayoutBox[] array, int arrayIndex)
            {
                if (array == null) throw new ArgumentNullException(nameof(array));
                var childIds = ChildIds;
                var count = childIds?.Count ?? 0;
                if (arrayIndex < 0 || arrayIndex > array.Length - count)
                {
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));
                }
                for (int i = 0; i < count; i++)
                {
                    array[arrayIndex + i] = _store.GetWrapper(childIds[i]);
                }
            }

            public IEnumerator<LayoutBox> GetEnumerator()
            {
                var childIds = ChildIds;
                if (childIds == null)
                {
                    yield break;
                }

                for (int i = 0; i < childIds.Count; i++)
                {
                    yield return _store.GetWrapper(childIds[i]);
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public int IndexOf(LayoutBox item)
            {
                if (item == null || !ReferenceEquals(item.Store, _store)) return -1;
                return _store.IndexOfChild(_parentId, item.StoreId);
            }

            public void Insert(int index, LayoutBox item)
            {
                ValidateItem(item);
                var currentCount = Count;
                if ((uint)index > (uint)currentCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                if (_store.IndexOfChild(_parentId, item.StoreId) >= 0)
                {
                    throw new InvalidOperationException("A layout box cannot appear more than once under the same parent.");
                }

                _store.ValidateTreeMutation(_parentId, item.StoreId);
                var oldParentId = _store._parentIds[item.StoreId];
                if (oldParentId >= 0 && oldParentId != _parentId && oldParentId < _store._count)
                {
                    _store.RemoveChildId(oldParentId, item.StoreId, clearParent: false);
                }

                var children = _store._childIds[_parentId] ??= new List<int>(4);
                children.Insert(index, item.StoreId);
                _store._parentIds[item.StoreId] = _parentId;
            }

            public bool Remove(LayoutBox item)
            {
                int index = IndexOf(item);
                if (index < 0)
                {
                    return false;
                }

                RemoveAt(index);
                return true;
            }

            public void RemoveAt(int index) => _store.RemoveChildAt(_parentId, index);

            private void ValidateItem(LayoutBox item)
            {
                if (item == null)
                {
                    throw new ArgumentNullException(nameof(item));
                }
                if (!ReferenceEquals(item.Store, _store))
                {
                    throw new InvalidOperationException("Layout boxes from different stores cannot share a parent/child relationship.");
                }
            }
        }
    }
}
