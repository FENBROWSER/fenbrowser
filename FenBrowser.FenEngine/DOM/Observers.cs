using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// ResizeObserver implementation - observes element size changes
    /// </summary>
    public class ResizeObserverWrapper
    {
        private readonly FenFunction _callback;
        private readonly List<Element> _observedElements = new List<Element>();
        private readonly Dictionary<Element, (float width, float height)> _lastSizes = new Dictionary<Element, (float, float)>();
        
        public ResizeObserverWrapper(FenFunction callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }
        
        public void Observe(Element target)
        {
            if (target  == null) return;
            if (!_observedElements.Contains(target))
            {
                _observedElements.Add(target);
                _lastSizes[target] = (0, 0);
            }
        }
        
        public void Unobserve(Element target)
        {
            if (target != null)
            {
                _observedElements.Remove(target);
                _lastSizes.Remove(target);
            }
        }
        
        public void Disconnect()
        {
            _observedElements.Clear();
            _lastSizes.Clear();
        }
        
        private FenObject CreateArrayFromEntries(List<FenObject> entries)
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(entries.Count));
            for (int i = 0; i < entries.Count; i++)
            {
                arr.Set(i.ToString(), FenValue.FromObject(entries[i]));
            }
            return arr;
        }
        
        public void CheckForChanges(Dictionary<Element, (float width, float height)> currentSizes, IExecutionContext context)
        {
            if (_observedElements.Count == 0) return;
            
            var entries = new List<FenObject>();
            
            foreach (var element in _observedElements)
            {
                if (currentSizes.TryGetValue(element, out var size))
                {
                    if (_lastSizes.TryGetValue(element, out var lastSize))
                    {
                        if (Math.Abs(size.width - lastSize.width) > 0.1 || Math.Abs(size.height - lastSize.height) > 0.1)
                        {
                            var entry = new FenObject();
                            entry.Set("contentRect", CreateDOMRect(0, 0, size.width, size.height));
                            entries.Add(entry);
                            _lastSizes[element] = size;
                        }
                    }
                    else
                    {
                        _lastSizes[element] = size;
                    }
                }
            }
            
            if (entries.Count > 0)
            {
                try
                {
                    var arrObj = CreateArrayFromEntries(entries);
                    _callback.Invoke(new FenValue[] { FenValue.FromObject(arrObj) }, context);
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[ResizeObserver] Callback error: {ex.Message}", LogCategory.JavaScript);
                }
            }
        }
        
        private FenValue CreateDOMRect(float x, float y, float width, float height)
        {
            var rect = new FenObject();
            rect.Set("x", FenValue.FromNumber(x));
            rect.Set("y", FenValue.FromNumber(y));
            rect.Set("width", FenValue.FromNumber(width));
            rect.Set("height", FenValue.FromNumber(height));
            return FenValue.FromObject(rect);
        }
        
        public FenObject ToFenObject()
        {
            var obj = new FenObject();
            
            obj.Set("observe", FenValue.FromFunction(new FenFunction("observe", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0] is FenValue fv && fv.AsObject() is ElementWrapper ew)
                {
                    Observe(ew.Element);
                }
                return FenValue.Undefined;
            })));
            
            obj.Set("unobserve", FenValue.FromFunction(new FenFunction("unobserve", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0] is FenValue fv && fv.AsObject() is ElementWrapper ew)
                {
                    Unobserve(ew.Element);
                }
                return FenValue.Undefined;
            })));
            
            obj.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (args, thisVal) =>
            {
                Disconnect();
                return FenValue.Undefined;
            })));
            
            return obj;
        }
    }
    
    /// <summary>
    /// IntersectionObserver implementation - observes element visibility in viewport
    /// </summary>
    public class IntersectionObserverWrapper
    {
        private readonly FenFunction _callback;
        private readonly List<Element> _observedElements = new List<Element>();
        private readonly Dictionary<Element, bool> _lastIntersecting = new Dictionary<Element, bool>();
        
        public IntersectionObserverWrapper(FenFunction callback, FenObject options = null)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }
        
        public void Observe(Element target)
        {
            if (target  == null) return;
            if (!_observedElements.Contains(target))
            {
                _observedElements.Add(target);
                _lastIntersecting[target] = false;
            }
        }
        
        public void Unobserve(Element target)
        {
            if (target != null)
            {
                _observedElements.Remove(target);
                _lastIntersecting.Remove(target);
            }
        }
        
        public void Disconnect()
        {
            _observedElements.Clear();
            _lastIntersecting.Clear();
        }
        
        private FenObject CreateArrayFromEntries(List<FenObject> entries)
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(entries.Count));
            for (int i = 0; i < entries.Count; i++)
            {
                arr.Set(i.ToString(), FenValue.FromObject(entries[i]));
            }
            return arr;
        }
        
        public void CheckForChanges(Dictionary<Element, (float x, float y, float width, float height)> elementRects, 
                                    float viewportWidth, float viewportHeight, float scrollY, IExecutionContext context)
        {
            if (_observedElements.Count == 0) return;
            
            var entries = new List<FenObject>();
            
            foreach (var element in _observedElements)
            {
                if (elementRects.TryGetValue(element, out var rect))
                {
                    float visibleTop = scrollY;
                    float visibleBottom = scrollY + viewportHeight;
                    
                    bool isIntersecting = rect.y + rect.height > visibleTop && rect.y < visibleBottom;
                    
                    if (_lastIntersecting.TryGetValue(element, out var lastIntersecting) && lastIntersecting != isIntersecting)
                    {
                        var entry = new FenObject();
                        entry.Set("isIntersecting", FenValue.FromBoolean(isIntersecting));
                        entry.Set("intersectionRatio", FenValue.FromNumber(isIntersecting ? 1.0 : 0.0));
                        entries.Add(entry);
                        
                        _lastIntersecting[element] = isIntersecting;
                    }
                }
            }
            
            if (entries.Count > 0)
            {
                try
                {
                    var arrObj = CreateArrayFromEntries(entries);
                    _callback.Invoke(new FenValue[] { FenValue.FromObject(arrObj) }, context);
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[IntersectionObserver] Callback error: {ex.Message}", LogCategory.JavaScript);
                }
            }
        }
        
        public FenObject ToFenObject()
        {
            var obj = new FenObject();
            
            obj.Set("observe", FenValue.FromFunction(new FenFunction("observe", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0] is FenValue fv && fv.AsObject() is ElementWrapper ew)
                {
                    Observe(ew.Element);
                }
                return FenValue.Undefined;
            })));
            
            obj.Set("unobserve", FenValue.FromFunction(new FenFunction("unobserve", (args, thisVal) =>
            {
                if (args.Length >= 1 && args[0] is FenValue fv && fv.AsObject() is ElementWrapper ew)
                {
                    Unobserve(ew.Element);
                }
                return FenValue.Undefined;
            })));
            
            obj.Set("disconnect", FenValue.FromFunction(new FenFunction("disconnect", (args, thisVal) =>
            {
                Disconnect();
                return FenValue.Undefined;
            })));
            
            return obj;
        }
    }
}

