using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// JS-API-compatible MutationObserver wrapper.
    /// Bridges the FenEngine JavaScript layer to the Core DOM MutationObserver.
    /// </summary>
    public class MutationObserverWrapper
    {
        public FenFunction Callback { get; }
        private readonly MutationObserver _coreObserver;
        private readonly List<MutationRecord> _pendingRecords = new List<MutationRecord>();

        public MutationObserverWrapper(FenFunction callback)
        {
            Callback = callback ?? throw new ArgumentNullException(nameof(callback));
            
            // The Core.Dom.MutationObserver expects Action<List<MutationRecord>, MutationObserver>
            _coreObserver = new MutationObserver((records, obs) =>
            {
                lock (_pendingRecords)
                {
                    _pendingRecords.AddRange(records);
                }
            });
        }

        public bool HasPendingRecords
        {
            get { lock (_pendingRecords) return _pendingRecords.Count > 0; }
        }

        public void Observe(Element target, MutationObserverOptions options)
        {
            if (target  == null) return;
            var init = new MutationObserverInit
            {
                ChildList = options?.ChildList ?? false,
                Attributes = options?.Attributes ?? false,
                CharacterData = options?.CharacterData ?? false,
                Subtree = options?.Subtree ?? false,
                AttributeOldValue = options?.AttributeOldValue ?? false,
                CharacterDataOldValue = options?.CharacterDataOldValue ?? false,
                AttributeFilter = options?.AttributeFilter?.ToArray()
            };
            _coreObserver.Observe(target, init);
        }

        public void Disconnect()
        {
            _coreObserver.Disconnect();
            lock (_pendingRecords) _pendingRecords.Clear();
        }

        public FenObject[] TakeRecords(IExecutionContext context)
        {
            List<MutationRecord> records;
            lock (_pendingRecords)
            {
                records = new List<MutationRecord>(_pendingRecords);
                _pendingRecords.Clear();
            }

            var fenRecords = new List<FenObject>();
            foreach (var rec in records)
            {
                fenRecords.Add(RecordToFenObject(rec, context));
            }
            return fenRecords.ToArray();
        }

        /// <summary>
        /// Record a mutation (called by legacy DOM mutation methods).
        /// Forwards to the core observer.
        /// </summary>
        public void RecordMutation(Node target, string type, string attributeName = null,
            string oldValue = null, List<Node> addedNodes = null, List<Node> removedNodes = null)
        {
            var record = new MutationRecord
            {
                Type = type,
                Target = target,
                AttributeName = attributeName,
                OldValue = oldValue,
                AddedNodes = addedNodes ?? new List<Node>(),
                RemovedNodes = removedNodes ?? new List<Node>()
            };
            
            lock (_pendingRecords)
            {
                _pendingRecords.Add(record);
            }
        }

        public FenObject ToFenObject(IExecutionContext context)
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

                        var attrOldVal = optObj.Get("attributeOldValue");
                        if (attrOldVal is FenValue aov) options.AttributeOldValue = aov.ToBoolean();

                        var charOldVal = optObj.Get("characterDataOldValue");
                        if (charOldVal is FenValue cov) options.CharacterDataOldValue = cov.ToBoolean();

                        var attrFilterVal = optObj.Get("attributeFilter");
                        if (attrFilterVal is FenValue afv && afv.IsObject)
                        {
                            options.AttributeFilter = new List<string>();
                            var lenVal = afv.AsObject().Get("length");
                            var len = lenVal.IsNumber ? lenVal.ToNumber() : 0;
                            for (int i = 0; i < len; i++)
                            {
                                var item = afv.AsObject().Get(i.ToString());
                                if (item != null) options.AttributeFilter.Add(item.ToString());
                            }
                        }

                        if ((options.AttributeOldValue || options.AttributeFilter != null) && !options.Attributes)
                            options.Attributes = true;
                        if (options.CharacterDataOldValue && !options.CharacterData)
                            options.CharacterData = true;
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
                var records = TakeRecords(context);
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

        private FenObject RecordToFenObject(MutationRecord rec, IExecutionContext context)
        {
            var obj = new FenObject();
            obj.Set("type", FenValue.FromString(rec.Type ?? ""));
            obj.Set("target", rec.Target != null ? FenValue.FromObject(new ElementWrapper(rec.Target as Element, context)) : FenValue.Null);
            obj.Set("attributeName", rec.AttributeName != null ? FenValue.FromString(rec.AttributeName) : FenValue.Null);
            obj.Set("oldValue", rec.OldValue != null ? FenValue.FromString(rec.OldValue) : FenValue.Null);

            var addedArr = new FenObject();
            addedArr.Set("length", FenValue.FromNumber(rec.AddedNodes?.Count ?? 0));
            if (rec.AddedNodes != null)
            {
                for (int i = 0; i < rec.AddedNodes.Count; i++)
                {
                    if (rec.AddedNodes[i] is Element el)
                        addedArr.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(el, context)));
                }
            }

            var removedArr = new FenObject();
            removedArr.Set("length", FenValue.FromNumber(rec.RemovedNodes?.Count ?? 0));
            if (rec.RemovedNodes != null)
            {
                for (int i = 0; i < rec.RemovedNodes.Count; i++)
                {
                    if (rec.RemovedNodes[i] is Element el)
                        removedArr.Set(i.ToString(), FenValue.FromObject(new ElementWrapper(el, context)));
                }
            }

            obj.Set("addedNodes", FenValue.FromObject(addedArr));
            obj.Set("removedNodes", FenValue.FromObject(removedArr));

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
        public List<string> AttributeFilter { get; set; }
    }
}
