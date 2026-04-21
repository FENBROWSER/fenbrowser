using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// CSS Animation Engine - executes @keyframes animations at runtime.
    /// Manages animation state, timing, interpolation, and triggers repaints.
    /// </summary>
    public class CssAnimationEngine
    {
        #region Singleton
        private static CssAnimationEngine _instance;
        private static readonly object _lock = new object();
        
        public static CssAnimationEngine Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new CssAnimationEngine();
                    }
                }
                return _instance;
            }
        }
        
        public static void Reset()
        {
            lock (_lock)
            {
                _instance?.Stop();
                _instance = null;
            }
        }
        #endregion
        
        #region Animation State
        
        /// <summary>
        /// Represents an active animation instance
        /// </summary>
        public class ActiveAnimation
        {
            public Element Element { get; set; }
            public string AnimationName { get; set; }
            public CssLoader.CssKeyframes Keyframes { get; set; }
            
            // Timing
            public double DurationMs { get; set; } = 1000;
            public double DelayMs { get; set; } = 0;
            public int IterationCount { get; set; } = 1; // -1 = infinite
            public string Direction { get; set; } = "normal"; // normal, reverse, alternate, alternate-reverse
            public string FillMode { get; set; } = "none"; // none, forwards, backwards, both
            public string TimingFunction { get; set; } = "ease";
            public string PlayState { get; set; } = "running"; // running, paused
            
            // State
            public DateTime StartTime { get; set; }
            public DateTime? PauseTime { get; set; }
            public double ElapsedBeforePause { get; set; }
            public int CurrentIteration { get; set; } = 0;
            public bool IsComplete { get; set; } = false;
            
            // Computed values (per frame)
            public Dictionary<string, string> ComputedProperties { get; set; } = new();
        }
        
        /// <summary>
        /// Represents an active CSS transition
        /// </summary>
        public class ActiveTransition
        {
            public Element Element { get; set; }
            public string Property { get; set; }
            public string FromValue { get; set; }
            public string ToValue { get; set; }
            public double DurationMs { get; set; } = 300;
            public double DelayMs { get; set; } = 0;
            public string TimingFunction { get; set; } = "ease";
            public DateTime StartTime { get; set; }
            public bool IsComplete { get; set; }
        }
        
        private readonly Dictionary<Element, List<ActiveAnimation>> _activeAnimations = new();
        private readonly Dictionary<Element, List<ActiveTransition>> _activeTransitions = new();
        private readonly Dictionary<Element, Dictionary<string, string>> _previousValues = new();
        private bool _isRunning = false;
        private System.Threading.Timer _timer;
        private const int FrameIntervalMs = 16; // ~60fps
        private static readonly HashSet<string> _layoutAffectingProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "width", "height", "min-width", "min-height", "max-width", "max-height",
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
            "top", "right", "bottom", "left",
            "font-size", "line-height", "letter-spacing", "word-spacing",
            "border-width", "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
            "flex", "flex-basis", "flex-grow", "flex-shrink",
            "grid-template-columns", "grid-template-rows", "grid-auto-columns", "grid-auto-rows"
        };
        /// <summary>
        /// Event raised when an animation completes.
        /// </summary>
        public event Action<Element, string> OnAnimationEnd;

        /// <summary>
        /// Event raised on animation ticks for elements with updated animated values.
        /// </summary>
        public event Action<Element> OnAnimationFrame;

        /// <summary>
        /// Event raised when transition completes.
        /// </summary>
        public event Action<Element, string> OnTransitionEnd;
        
        #endregion
        
        #region Transition API
        
        /// <summary>
        /// Check for property changes and start transitions
        /// </summary>
        public void CheckTransitions(Element element, CssComputed newStyle)
        {
            if (element == null || newStyle?.Map == null) return;
            
            // Get transition properties
            string transitionProperty = GetTransitionProperty(newStyle, "transition-property") ?? "all";
            double transitionDuration = ParseDuration(GetTransitionProperty(newStyle, "transition-duration") ?? "0s");
            double transitionDelay = ParseDuration(GetTransitionProperty(newStyle, "transition-delay") ?? "0s");
            string transitionTiming = GetTransitionProperty(newStyle, "transition-timing-function") ?? "ease";
            
            if (transitionDuration <= 0) return;
            
            // Get previous values for this element
            if (!_previousValues.TryGetValue(element, out var prevValues))
            {
                prevValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _previousValues[element] = prevValues;
            }
            
            // Properties that can be transitioned
            var transitionableProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "opacity", "color", "background-color", "border-color",
                "width", "height", "min-width", "min-height", "max-width", "max-height",
                "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
                "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
                "top", "right", "bottom", "left",
                "font-size", "line-height", "letter-spacing", "word-spacing",
                "border-width", "border-radius",
                "transform", "filter", "box-shadow"
            };
            
            // Get list of properties to transition
            var propsToCheck = transitionProperty == "all" 
                ? transitionableProps 
                : transitionProperty.Split(',').Select(p => p.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            // Check each property for changes
            foreach (var prop in propsToCheck)
            {
                if (!transitionableProps.Contains(prop)) continue;
                
                string newValue = null;
                newStyle.Map?.TryGetValue(prop, out newValue);
                prevValues.TryGetValue(prop, out var oldValue);
                
                // If value changed and we have both values, start transition
                if (newValue != null && oldValue != null && newValue != oldValue)
                {
                    StartTransition(element, prop, oldValue, newValue, transitionDuration, transitionDelay, transitionTiming);
                }
                
                // Update previous value
                if (newValue != null)
                    prevValues[prop] = newValue;
            }
        }
        
        /// <summary>
        /// Start a transition for a specific property
        /// </summary>
        private void StartTransition(Element element, string property, string from, string to,
            double durationMs, double delayMs, string timingFunction)
        {
            var transition = new ActiveTransition
            {
                Element = element,
                Property = property,
                FromValue = from,
                ToValue = to,
                DurationMs = durationMs,
                DelayMs = delayMs,
                TimingFunction = timingFunction,
                StartTime = DateTime.UtcNow
            };
            
            lock (_activeTransitions)
            {
                if (!_activeTransitions.ContainsKey(element))
                    _activeTransitions[element] = new List<ActiveTransition>();
                
                // Remove any existing transition for this property
                _activeTransitions[element].RemoveAll(t => t.Property == property);
                _activeTransitions[element].Add(transition);
            }
            
            if (!_isRunning)
                Start();
        }
        
        /// <summary>
        /// Get transitioned property values for an element
        /// </summary>
        public Dictionary<string, string> GetTransitionedProperties(Element element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            lock (_activeTransitions)
            {
                if (_activeTransitions.TryGetValue(element, out var list))
                {
                    var now = DateTime.UtcNow;
                    foreach (var trans in list)
                    {
                        if (trans.IsComplete) continue;
                        
                        double elapsed = (now - trans.StartTime).TotalMilliseconds;
                        if (elapsed < trans.DelayMs)
                        {
                            result[trans.Property] = trans.FromValue;
                            continue;
                        }
                        
                        elapsed -= trans.DelayMs;
                        double progress = trans.DurationMs > 0 ? Math.Min(elapsed / trans.DurationMs, 1.0) : 1.0;
                        progress = ApplyEasing(progress, trans.TimingFunction);
                        
                        string interpolated = InterpolateValue(trans.Property, trans.FromValue, trans.ToValue, progress);
                        if (interpolated != null)
                            result[trans.Property] = interpolated;
                    }
                }
            }
            
            return result;
        }
        
        private string GetTransitionProperty(CssComputed style, string property)
        {
            if (style.Map?.TryGetValue(property, out var value) == true)
                return value;
            
            // Try shorthand
            if (style.Map?.TryGetValue("transition", out var shorthand) == true)
            {
                var parts = shorthand.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                switch (property)
                {
                    case "transition-property":
                        foreach (var p in parts)
                        {
                            if (!IsDurationValue(p) && !IsTimingFunction(p))
                                return p;
                        }
                        break;
                    case "transition-duration":
                        foreach (var p in parts)
                        {
                            if (IsDurationValue(p)) return p;
                        }
                        break;
                    case "transition-timing-function":
                        foreach (var p in parts)
                        {
                            if (IsTimingFunction(p)) return p;
                        }
                        break;
                }
            }
            
            return null;
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Start an animation on an element
        /// </summary>
        public void StartAnimation(Element element, CssComputed style)
        {
            if (element == null || style?.Map == null) return;
            
            // Parse animation properties
            string animationName = GetAnimationProperty(style, "animation-name");
            if (string.IsNullOrWhiteSpace(animationName) || animationName == "none") return;

            var names = SplitAnimationList(animationName);
            bool startedAny = false;

            for (int i = 0; i < names.Count; i++)
            {
                string currentName = names[i];
                if (string.IsNullOrWhiteSpace(currentName) || currentName.Equals("none", StringComparison.OrdinalIgnoreCase))
                    continue;

                var keyframes = CssLoader.GetKeyframes(currentName);
                if (keyframes == null)
                {
                    EngineLogCompat.Debug($"[Animation] Keyframes not found: {currentName}", LogCategory.Layout);
                    continue;
                }

                // Create animation instance
                var animation = new ActiveAnimation
                {
                    Element = element,
                    AnimationName = currentName,
                    Keyframes = keyframes,
                    DurationMs = ParseDuration(GetAnimationProperty(style, "animation-duration", i)),
                    DelayMs = ParseDuration(GetAnimationProperty(style, "animation-delay", i)),
                    IterationCount = ParseIterationCount(GetAnimationProperty(style, "animation-iteration-count", i)),
                    Direction = GetAnimationProperty(style, "animation-direction", i) ?? "normal",
                    FillMode = GetAnimationProperty(style, "animation-fill-mode", i) ?? "none",
                    TimingFunction = GetAnimationProperty(style, "animation-timing-function", i) ?? "ease",
                    PlayState = GetAnimationProperty(style, "animation-play-state", i) ?? "running",
                    StartTime = DateTime.UtcNow
                };

                EngineLogCompat.Debug($"[Animation] Starting: {currentName} on {element.TagName}, duration={animation.DurationMs}ms", LogCategory.Layout);

                // Add to active animations
                lock (_activeAnimations)
                {
                    if (!_activeAnimations.ContainsKey(element))
                        _activeAnimations[element] = new List<ActiveAnimation>();

                    var existing = _activeAnimations[element].FirstOrDefault(a =>
                        a.AnimationName == currentName &&
                        !a.IsComplete);

                    if (existing != null &&
                        existing.Keyframes == keyframes &&
                        Math.Abs(existing.DurationMs - animation.DurationMs) < 0.01 &&
                        Math.Abs(existing.DelayMs - animation.DelayMs) < 0.01 &&
                        existing.IterationCount == animation.IterationCount &&
                        string.Equals(existing.Direction, animation.Direction, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.FillMode, animation.FillMode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.TimingFunction, animation.TimingFunction, StringComparison.OrdinalIgnoreCase))
                    {
                        startedAny = true;
                        continue;
                    }

                    // Remove existing animation with same name when the definition changed.
                    _activeAnimations[element].RemoveAll(a => a.AnimationName == currentName);
                    _activeAnimations[element].Add(animation);
                }

                startedAny = true;
            }

            if (!startedAny && names.Count > 1)
            {
                EngineLogCompat.Debug($"[Animation] No keyframes resolved from animation-name list: {animationName}", LogCategory.Layout);
            }
            
            // Start engine if not running
            if (startedAny && !_isRunning)
                Start();
        }
        
        /// <summary>
        /// Stop all animations on an element
        /// </summary>
        public void StopAnimations(Element element)
        {
            lock (_activeAnimations)
            {
                _activeAnimations.Remove(element);
            }
        }
        
        /// <summary>
        /// Stop a specific animation on an element
        /// </summary>
        public void StopAnimation(Element element, string animationName)
        {
            lock (_activeAnimations)
            {
                if (_activeAnimations.TryGetValue(element, out var list))
                {
                    list.RemoveAll(a => a.AnimationName == animationName);
                    if (list.Count == 0)
                        _activeAnimations.Remove(element);
                }
            }
        }
        
        /// <summary>
        /// Pause/resume animations on an element
        /// </summary>
        public void SetPlayState(Element element, string state)
        {
            lock (_activeAnimations)
            {
                if (_activeAnimations.TryGetValue(element, out var list))
                {
                    foreach (var anim in list)
                    {
                        if (state == "paused" && anim.PlayState != "paused")
                        {
                            anim.PauseTime = DateTime.UtcNow;
                            anim.ElapsedBeforePause = (anim.PauseTime.Value - anim.StartTime).TotalMilliseconds;
                        }
                        else if (state == "running" && anim.PlayState == "paused")
                        {
                            // Adjust start time to account for pause
                            anim.StartTime = DateTime.UtcNow.AddMilliseconds(-anim.ElapsedBeforePause);
                            anim.PauseTime = null;
                        }
                        anim.PlayState = state;
                    }
                }
            }
        }
        
        
        /// <summary>
        /// Get computed animation properties for an element at current time
        /// </summary>
        public Dictionary<string, string> GetAnimatedProperties(Element element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            lock (_activeAnimations)
            {
                if (_activeAnimations.TryGetValue(element, out var list))
                {
                    foreach (var anim in list)
                    {
                        foreach (var kvp in anim.ComputedProperties)
                        {
                            result[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Check if element has active animations
        /// </summary>
        public bool HasActiveAnimations(Element element)
        {
            lock (_activeAnimations)
            {
                return _activeAnimations.ContainsKey(element) && _activeAnimations[element].Count > 0;
            }
        }

        /// <summary>
        /// Gets all elements that are currently being animated or transitioned.
        /// PERF: use this to avoid O(N) iteration in the renderer.
        /// </summary>
        public HashSet<Element> GetAllActiveAnimationElements()
        {
            var result = new HashSet<Element>();
            lock (_activeAnimations)
            {
                foreach (var el in _activeAnimations.Keys) result.Add(el);
            }
            lock (_activeTransitions)
            {
                foreach (var el in _activeTransitions.Keys) result.Add(el);
            }
            return result;
        }

        public static InvalidationKind DetermineInvalidationKind(IEnumerable<string> properties)
        {
            if (properties == null)
            {
                return InvalidationKind.None;
            }

            var invalidation = InvalidationKind.None;
            foreach (var property in properties)
            {
                invalidation |= ClassifyPropertyInvalidation(property);
                if ((invalidation & InvalidationKind.Layout) != 0)
                {
                    break;
                }
            }

            return invalidation == InvalidationKind.None
                ? InvalidationKind.Paint
                : invalidation;
        }

        public static InvalidationKind ClassifyPropertyInvalidation(string property)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                return InvalidationKind.None;
            }

            return _layoutAffectingProperties.Contains(property.Trim())
                ? InvalidationKind.Layout | InvalidationKind.Paint
                : InvalidationKind.Paint;
        }

        #endregion
        
        #region Animation Loop
        
        private void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _timer = new System.Threading.Timer(Tick, null, 0, FrameIntervalMs);
            EngineLogCompat.Debug("[Animation] Engine started", LogCategory.Layout);
        }
        
        public void Stop()
        {
            _isRunning = false;
            _timer?.Dispose();
            _timer = null;
            EngineLogCompat.Debug("[Animation] Engine stopped", LogCategory.Layout);
        }
        
        private void Tick(object state)
        {
            if (!_isRunning) return;
            
            var now = DateTime.UtcNow;
            var toRemove = new List<(Element element, ActiveAnimation anim)>();
            var toNotify = new HashSet<Element>();
            
            lock (_activeAnimations)
            {
                foreach (var kvp in _activeAnimations)
                {
                    var element = kvp.Key;
                    var style = element.GetComputedStyle();
                    if (style != null) style.AnimationOverlay?.Clear();

                    foreach (var anim in kvp.Value)
                    {
                        if (anim.PlayState == "paused") continue;
                        double elapsed = (now - anim.StartTime).TotalMilliseconds;
                        
                        if (elapsed < anim.DelayMs)
                        {
                            if (anim.FillMode == "backwards" || anim.FillMode == "both")
                            {
                                ApplyKeyframeAt(anim, 0);
                                ApplyToOverlay(element, anim.ComputedProperties);
                                toNotify.Add(element);
                                element.MarkDirty(DetermineInvalidationKind(anim.ComputedProperties.Keys));
                            }
                            continue;
                        }
                        
                        elapsed -= anim.DelayMs;
                        double iterationProgress = anim.DurationMs > 0 ? elapsed / anim.DurationMs : 1;
                        int iteration = (int)Math.Floor(iterationProgress);
                        double progress = iterationProgress - iteration;
                        
                        if (anim.IterationCount >= 0 && iteration >= anim.IterationCount)
                        {
                            anim.IsComplete = true;
                            if (anim.FillMode == "forwards" || anim.FillMode == "both")
                            {
                                ApplyKeyframeAt(anim, 100);
                                ApplyToOverlay(element, anim.ComputedProperties);
                                toNotify.Add(element);
                                element.MarkDirty(DetermineInvalidationKind(anim.ComputedProperties.Keys));
                            }
                            toRemove.Add((element, anim));
                            OnAnimationEnd?.Invoke(element, anim.AnimationName);
                            continue;
                        }
                        
                        anim.CurrentIteration = iteration;
                        bool reverse = false;
                        switch (anim.Direction)
                        {
                            case "reverse": reverse = true; break;
                            case "alternate": reverse = iteration % 2 == 1; break;
                            case "alternate-reverse": reverse = iteration % 2 == 0; break;
                        }
                        
                        if (reverse) progress = 1 - progress;
                        progress = ApplyEasing(progress, anim.TimingFunction);
                        ApplyKeyframeAt(anim, progress * 100);
                        
                        ApplyToOverlay(element, anim.ComputedProperties);
                        element.MarkDirty(DetermineInvalidationKind(anim.ComputedProperties.Keys));
                        toNotify.Add(element);
                    }
                }
                
                foreach (var (element, anim) in toRemove)
                {
                    if (_activeAnimations.TryGetValue(element, out var list))
                    {
                        list.Remove(anim);
                        if (list.Count == 0) _activeAnimations.Remove(element);
                    }
                }
            }

            lock (_activeTransitions)
            {
                foreach (var kvp in _activeTransitions)
                {
                    var element = kvp.Key;
                    bool elementDirty = false;
                    var invalidation = InvalidationKind.None;

                    foreach (var trans in kvp.Value)
                    {
                        if (trans.IsComplete) continue;
                        double elapsed = (now - trans.StartTime).TotalMilliseconds;
                        if (elapsed < trans.DelayMs) continue;
                        elapsed -= trans.DelayMs;
                        double progress = trans.DurationMs > 0 ? Math.Min(elapsed / trans.DurationMs, 1.0) : 1.0;
                        progress = ApplyEasing(progress, trans.TimingFunction);

                        string interpolated = InterpolateValue(trans.Property, trans.FromValue, trans.ToValue, progress);
                        if (interpolated != null)
                        {
                            var style = element.GetComputedStyle();
                            if (style != null)
                            {
                                style.AnimationOverlay ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                style.AnimationOverlay[trans.Property] = interpolated;
                                elementDirty = true;
                                invalidation |= ClassifyPropertyInvalidation(trans.Property);
                            }
                        }
                        if (progress >= 1.0)
                        {
                            trans.IsComplete = true;
                            OnTransitionEnd?.Invoke(element, trans.Property);
                        }
                    }

                    if (elementDirty)
                    {
                        element.MarkDirty(invalidation);
                        toNotify.Add(element);
                    }
                }
                
                foreach (var el in _activeTransitions.Keys.ToList())
                {
                    if (_activeTransitions[el].All(t => t.IsComplete))
                        _activeTransitions.Remove(el);
                }
            }
            
            foreach (var element in toNotify)
            {
                OnAnimationFrame?.Invoke(element);
            }

            if (toNotify.Count == 0 && _activeAnimations.Count == 0 && _activeTransitions.Count == 0)
                Stop();
        }

        private void ApplyToOverlay(Element element, Dictionary<string, string> properties)
        {
            var style = element.GetComputedStyle();
            if (style == null) return;
            style.AnimationOverlay ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in properties) style.AnimationOverlay[kvp.Key] = kvp.Value;
        }

        private void ApplyKeyframeAt(ActiveAnimation anim, double progress)
        {
            if (anim.Keyframes == null || anim.Keyframes.Frames.Count == 0) return;

            var frames = anim.Keyframes.Frames.OrderBy(f => f.Percentage).ToList();
            double normalizedProgress = progress * 100.0;
            
            // Find bounding keyframes
            CssLoader.CssKeyframe lower = frames.LastOrDefault(f => f.Percentage <= normalizedProgress);
            CssLoader.CssKeyframe upper = frames.FirstOrDefault(f => f.Percentage > normalizedProgress);

            if (lower == null) lower = frames.First();
            if (upper == null)
            {
                // We are at or past the last keyframe
                foreach (var kvp in lower.Properties) anim.ComputedProperties[kvp.Key] = kvp.Value;
                return;
            }

            double frameRange = upper.Percentage - lower.Percentage;
            double frameProgress = frameRange > 0 ? (normalizedProgress - lower.Percentage) / frameRange : 1.0;

            // Interpolate all properties present in either keyframe
            var allProps = lower.Properties.Keys.Union(upper.Properties.Keys).Distinct();
            foreach (var prop in allProps)
            {
                lower.Properties.TryGetValue(prop, out var fromVal);
                upper.Properties.TryGetValue(prop, out var toVal);

                if (fromVal != null && toVal != null)
                {
                    anim.ComputedProperties[prop] = InterpolateValue(prop, fromVal, toVal, frameProgress);
                }
                else if (fromVal != null)
                {
                    anim.ComputedProperties[prop] = fromVal;
                }
                else if (toVal != null)
                {
                    anim.ComputedProperties[prop] = toVal;
                }
            }
        }

        private string InterpolateValue(string property, string from, string to, double progress)
        {
            if (progress <= 0) return from;
            if (progress >= 1) return to;

            // Handle Transforms
            if (property == "transform")
            {
                var f = ParseTransform(from);
                var t = ParseTransform(to);
                
                double tx = f.Item1 + (t.Item1 - f.Item1) * progress;
                double ty = f.Item2 + (t.Item2 - f.Item2) * progress;
                double sx = f.Item3 + (t.Item3 - f.Item3) * progress;
                double sy = f.Item4 + (t.Item4 - f.Item4) * progress;
                double r = f.Item5 + (t.Item5 - f.Item5) * progress;

                return $"translate({tx}px, {ty}px) scale({sx}, {sy}) rotate({r}deg)";
            }

            // Handle Numeric values (Opacity, etc)
            if (TryParseNumericValue(from, out double fNum, out string fUnit) && 
                TryParseNumericValue(to, out double tNum, out string tUnit))
            {
                double val = fNum + (tNum - fNum) * progress;
                return $"{val}{fUnit}";
            }

            // Handle Colors
            if (TryParseColor(from, out SKColor fCol) && TryParseColor(to, out SKColor tCol))
            {
                byte r = (byte)(fCol.Red + (tCol.Red - fCol.Red) * progress);
                byte g = (byte)(fCol.Green + (tCol.Green - fCol.Green) * progress);
                byte b = (byte)(fCol.Blue + (tCol.Blue - fCol.Blue) * progress);
                byte a = (byte)(fCol.Alpha + (tCol.Alpha - fCol.Alpha) * progress);
                return $"rgba({r},{g},{b},{a})";
            }

            // Default to discrete swap at 50%
            return progress < 0.5 ? from : to;
        }

        private (double, double, double, double, double) ParseTransform(string value)
        {
            double tx = 0, ty = 0, sx = 1, sy = 1, r = 0;
            if (string.IsNullOrEmpty(value) || value == "none") return (tx, ty, sx, sy, r);

            // Parse translate
            var translateMatch = System.Text.RegularExpressions.Regex.Match(value, @"translate\s*\(\s*([-\d.]+)(px|%)?\s*,?\s*([-\d.]+)?(px|%)?\s*\)");
            if (translateMatch.Success)
            {
                double.TryParse(translateMatch.Groups[1].Value, out tx);
                if (!string.IsNullOrEmpty(translateMatch.Groups[3].Value))
                    double.TryParse(translateMatch.Groups[3].Value, out ty);
            }
            
            var translateXMatch = System.Text.RegularExpressions.Regex.Match(value, @"translateX\s*\(\s*([-\d.]+)(px|%)?\s*\)");
            if (translateXMatch.Success) double.TryParse(translateXMatch.Groups[1].Value, out tx);
            
            var translateYMatch = System.Text.RegularExpressions.Regex.Match(value, @"translateY\s*\(\s*([-\d.]+)(px|%)?\s*\)");
            if (translateYMatch.Success) double.TryParse(translateYMatch.Groups[1].Value, out ty);
            
            // Parse scale
            var scaleMatch = System.Text.RegularExpressions.Regex.Match(value, @"scale\s*\(\s*([-\d.]+)\s*,?\s*([-\d.]+)?\s*\)");
            if (scaleMatch.Success)
            {
                double.TryParse(scaleMatch.Groups[1].Value, out sx);
                if (!string.IsNullOrEmpty(scaleMatch.Groups[2].Value))
                    double.TryParse(scaleMatch.Groups[2].Value, out sy);
                else
                    sy = sx;
            }
            
            // Parse rotate
            var rotateMatch = System.Text.RegularExpressions.Regex.Match(value, @"rotate\s*\(\s*([-\d.]+)(deg|rad)?\s*\)");
            if (rotateMatch.Success)
            {
                double.TryParse(rotateMatch.Groups[1].Value, out r);
                if (rotateMatch.Groups[2].Value == "rad") r = r * 180 / Math.PI;
            }
            
            return (tx, ty, sx, sy, r);
        }
        
        #endregion
        
        #region Easing Functions
        
        private double ApplyEasing(double t, string timingFunction)
        {
            if (string.IsNullOrEmpty(timingFunction)) return t;
            
            switch (timingFunction.ToLower().Trim())
            {
                case "linear": return t;
                case "ease": return CubicBezier(t, 0.25, 0.1, 0.25, 1.0);
                case "ease-in": return CubicBezier(t, 0.42, 0, 1.0, 1.0);
                case "ease-out": return CubicBezier(t, 0, 0, 0.58, 1.0);
                case "ease-in-out": return CubicBezier(t, 0.42, 0, 0.58, 1.0);
                default:
                    var match = System.Text.RegularExpressions.Regex.Match(timingFunction, @"cubic-bezier\s*\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*\)");
                    if (match.Success)
                    {
                        double.TryParse(match.Groups[1].Value, out double p1x);
                        double.TryParse(match.Groups[2].Value, out double p1y);
                        double.TryParse(match.Groups[3].Value, out double p2x);
                        double.TryParse(match.Groups[4].Value, out double p2y);
                        return CubicBezier(t, p1x, p1y, p2x, p2y);
                    }
                    return t;
            }
        }
        
        private double CubicBezier(double t, double p1x, double p1y, double p2x, double p2y)
        {
            double x = t;
            for (int i = 0; i < 8; i++)
            {
                double currentX = BezierX(x, p1x, p2x);
                double currentSlope = BezierXDerivative(x, p1x, p2x);
                if (Math.Abs(currentSlope) < 1e-6) break;
                x -= (currentX - t) / currentSlope;
            }
            return BezierY(x, p1y, p2y);
        }
        
        private double BezierX(double t, double p1x, double p2x) => 3 * (1 - t) * (1 - t) * t * p1x + 3 * (1 - t) * t * t * p2x + t * t * t;
        private double BezierY(double t, double p1y, double p2y) => 3 * (1 - t) * (1 - t) * t * p1y + 3 * (1 - t) * t * t * p2y + t * t * t;
        private double BezierXDerivative(double t, double p1x, double p2x) => 3 * (1 - t) * (1 - t) * p1x + 6 * (1 - t) * t * (p2x - p1x) + 3 * t * t * (1 - p2x);
        
        #endregion
        
        #region Parsing Helpers
        
        private string GetAnimationProperty(CssComputed style, string property, int listIndex = 0)
        {
            if (style.Map.TryGetValue(property, out var value)) return GetIndexedAnimationValue(value, listIndex);
            
            if (style.Map.TryGetValue("animation", out var shorthand))
            {
                var parts = shorthand.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                switch (property)
                {
                    case "animation-name":
                        foreach (var p in parts) if (!IsDurationValue(p) && !IsTimingFunction(p) && !IsIterationCount(p) && !IsDirection(p) && !IsFillMode(p) && !IsPlayState(p)) return p;
                        break;
                    case "animation-duration":
                        foreach (var p in parts) if (IsDurationValue(p)) return p;
                        break;
                    case "animation-timing-function":
                        foreach (var p in parts) if (IsTimingFunction(p)) return p;
                        break;
                }
            }
            return null;
        }

        private static List<string> SplitAnimationList(string raw) => string.IsNullOrWhiteSpace(raw) ? new List<string>() : raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        private static string GetIndexedAnimationValue(string raw, int listIndex)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            var parts = SplitAnimationList(raw);
            if (parts.Count == 0) return raw.Trim();
            return parts[Math.Max(0, Math.Min(listIndex, parts.Count - 1))];
        }
        
        private double ParseDuration(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 1000;
            value = value.Trim().ToLower();
            if (value.EndsWith("ms") && double.TryParse(value.Replace("ms", ""), out double ms)) return ms;
            if (value.EndsWith("s") && double.TryParse(value.Replace("s", ""), out double s)) return s * 1000;
            return 1000;
        }
        
        private int ParseIterationCount(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 1;
            value = value.Trim().ToLower();
            if (value == "infinite") return -1;
            return int.TryParse(value, out int count) ? count : 1;
        }
        
        private bool IsDurationValue(string value) => value.EndsWith("s") || value.EndsWith("ms");
        private bool IsTimingFunction(string value) { var v = value.ToLower(); return v == "linear" || v == "ease" || v == "ease-in" || v == "ease-out" || v == "ease-in-out" || v.StartsWith("cubic-bezier"); }
        private bool IsIterationCount(string value) => value.ToLower() == "infinite" || int.TryParse(value, out _);
        private bool IsDirection(string value) { var v = value.ToLower(); return v == "normal" || v == "reverse" || v == "alternate" || v == "alternate-reverse"; }
        private bool IsFillMode(string value) { var v = value.ToLower(); return v == "none" || v == "forwards" || v == "backwards" || v == "both"; }
        private bool IsPlayState(string value) { var v = value.ToLower(); return v == "running" || v == "paused"; }
        
        private bool TryParseNumericValue(string value, out double number, out string unit)
        {
            number = 0; unit = "";
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim().ToLower();
            string[] units = { "px", "%", "em", "rem", "vh", "vw", "deg", "rad", "s", "ms" };
            foreach (var u in units) if (value.EndsWith(u)) { unit = u; value = value.Substring(0, value.Length - u.Length); break; }
            return double.TryParse(value, out number);
        }
        
        private bool TryParseColor(string value, out SKColor color)
        {
            color = SKColors.Black;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim().ToLower();
            if (value.StartsWith("#"))
            {
                try { color = SKColor.Parse(value); return true; }
                catch { return false; }
            }
            var rgbMatch = System.Text.RegularExpressions.Regex.Match(value, @"rgba?\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+))?\s*\)");
            if (rgbMatch.Success)
            {
                byte.TryParse(rgbMatch.Groups[1].Value, out byte r);
                byte.TryParse(rgbMatch.Groups[2].Value, out byte g);
                byte.TryParse(rgbMatch.Groups[3].Value, out byte b);
                byte a = 255;
                if (!string.IsNullOrEmpty(rgbMatch.Groups[4].Value) && double.TryParse(rgbMatch.Groups[4].Value, out double alpha)) a = (byte)(alpha * 255);
                color = new SKColor(r, g, b, a);
                return true;
            }
            return false;
        }
        
        #endregion
    }
}
