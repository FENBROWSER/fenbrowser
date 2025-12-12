using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// MutationObserver implementation - observes DOM changes
    /// </summary>
    public class MutationObserverWrapper
    {
        private readonly FenFunction _callback;
        private readonly List<(LiteElement target, MutationObserverOptions options)> _observed = 
            new List<(LiteElement, MutationObserverOptions)>();
        private readonly List<MutationRecordEntry> _pendingRecords = new List<MutationRecordEntry>();
        
        public MutationObserverWrapper(FenFunction callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }
        
        public void Observe(LiteElement target, MutationObserverOptions options)
        {
            if (target == null) return;
            _observed.Add((target, options ?? new MutationObserverOptions()));
        }
        
        public void Disconnect()
        {
            _observed.Clear();
            _pendingRecords.Clear();
        }
        
        public FenObject[] TakeRecords()
        {
            var records = new List<FenObject>();
            foreach (var rec in _pendingRecords)
            {
                records.Add(rec.ToFenObject());
            }
            _pendingRecords.Clear();
            return records.ToArray();
        }
        
        /// <summary>
        /// Record a mutation (called by DOM mutation methods)
        /// </summary>
        public void RecordMutation(LiteElement target, string type, string attributeName = null, 
            string oldValue = null, List<LiteElement> addedNodes = null, List<LiteElement> removedNodes = null)
        {
            foreach (var (observedTarget, options) in _observed)
            {
                bool matches = ReferenceEquals(observedTarget, target);
                if (!matches && options.Subtree)
                {
                    // Check if target is descendant of observed
                    for (var p = target?.Parent; p != null; p = p.Parent)
                    {
                        if (ReferenceEquals(p, observedTarget))
                        {
                            matches = true;
                            break;
                        }
                    }
                }
                
                if (!matches) continue;
                
                // Check if this mutation type is being observed
                if (type == "attributes" && !options.Attributes) continue;
                if (type == "childList" && !options.ChildList) continue;
                if (type == "characterData" && !options.CharacterData) continue;
                
                var record = new MutationRecordEntry
                {
                    Type = type,
                    Target = target,
                    AttributeName = attributeName,
                    OldValue = oldValue,
                    AddedNodes = addedNodes ?? new List<LiteElement>(),
                    RemovedNodes = removedNodes ?? new List<LiteElement>()
                };
                
                _pendingRecords.Add(record);
            }
        }
        
        public FenObject ToFenObject()
        {
            var obj = new FenObject();
            
            obj.Set("observe", FenValue.FromFunction(new FenFunction("observe", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0] is FenValue fv && fv.AsObject() is ElementWrapper ew)
                {
                    var options = new MutationObserverOptions();
                    if (args.Length >= 2 && args[1] is FenValue optFv && optFv.AsObject() is FenObject optObj)
                    {
                        var childListVal = optObj.Get("childList");
                        if (childListVal is FenValue clv) options.ChildList = clv.ToBoolean();
                        
                        var attrsVal = optObj.Get("attributes");
                        if (attrsVal is FenValue av) options.Attributes = av.ToBoolean();
                        
                        var charDataVal = optObj.Get("characterData");
                        if (charDataVal is FenValue cdv) options.CharacterData = cdv.ToBoolean();
                        
                        var subtreeVal = optObj.Get("subtree");
                        if (subtreeVal is FenValue sv) options.Subtree = sv.ToBoolean();
                    }
                    Observe(ew.Element, options);
                }
                return FenValue.Undefined;
            })));
            
            obj.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (args, thisVal) =>
            {
                Disconnect();
                return FenValue.Undefined;
            })));
            
            obj.Set("takeRecords", FenValue.FromFunction(new FenFunction("takeRecords", (args, thisVal) =>
            {
                var records = TakeRecords();
                var arr = new FenObject();
                arr.Set("length", FenValue.FromNumber(records.Length));
                for (int i = 0; i < records.Length; i++)
                {
                    arr.Set(i.ToString(), FenValue.FromObject(records[i]));
                }
                return FenValue.FromObject(arr);
            })));
            
            return obj;
        }
    }
    
    public class MutationObserverOptions
    {
        public bool ChildList { get; set; }
        public bool Attributes { get; set; }
        public bool CharacterData { get; set; }
        public bool Subtree { get; set; }
        public bool AttributeOldValue { get; set; }
        public bool CharacterDataOldValue { get; set; }
    }
    
    public class MutationRecordEntry
    {
        public string Type { get; set; }
        public LiteElement Target { get; set; }
        public string AttributeName { get; set; }
        public string OldValue { get; set; }
        public List<LiteElement> AddedNodes { get; set; } = new List<LiteElement>();
        public List<LiteElement> RemovedNodes { get; set; } = new List<LiteElement>();
        
        public FenObject ToFenObject()
        {
            var obj = new FenObject();
            obj.Set("type", FenValue.FromString(Type ?? ""));
            obj.Set("attributeName", AttributeName != null ? FenValue.FromString(AttributeName) : FenValue.Null);
            obj.Set("oldValue", OldValue != null ? FenValue.FromString(OldValue) : FenValue.Null);
            
            // AddedNodes as array-like
            var addedArr = new FenObject();
            addedArr.Set("length", FenValue.FromNumber(AddedNodes?.Count ?? 0));
            
            var removedArr = new FenObject();
            removedArr.Set("length", FenValue.FromNumber(RemovedNodes?.Count ?? 0));
            
            obj.Set("addedNodes", FenValue.FromObject(addedArr));
            obj.Set("removedNodes", FenValue.FromObject(removedArr));
            
            return obj;
        }
    }
}
