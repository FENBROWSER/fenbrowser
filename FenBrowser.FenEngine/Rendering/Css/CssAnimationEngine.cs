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
        
        /// <summary>
        /// Event raised when animation frame updates require repaint
        /// </summary>
        public event Action<Element> OnAnimationFrame;
        
        /// <summary>
        /// Event raised when animation completes
        /// </summary>
        public event Action<Element, string> OnAnimationEnd;
        
        /// <summary>
        /// Event raised when transition completes
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
                    FenLogger.Debug($"[Animation] Keyframes not found: {currentName}", LogCategory.Layout);
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

                FenLogger.Debug($"[Animation] Starting: {currentName} on {element.TagName}, duration={animation.DurationMs}ms", LogCategory.Layout);

                // Add to active animations
                lock (_activeAnimations)
                {
                    if (!_activeAnimations.ContainsKey(element))
                        _activeAnimations[element] = new List<ActiveAnimation>();

                    // Remove existing animation with same name
                    _activeAnimations[element].RemoveAll(a => a.AnimationName == currentName);
                    _activeAnimations[element].Add(animation);
                }

                startedAny = true;
            }

            if (!startedAny && names.Count > 1)
            {
                FenLogger.Debug($"[Animation] No keyframes resolved from animation-name list: {animationName}", LogCategory.Layout);
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
        
        #endregion
        
        #region Animation Loop
        
        private void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _timer = new System.Threading.Timer(Tick, null, 0, FrameIntervalMs);
            FenLogger.Debug("[Animation] Engine started", LogCategory.Layout);
        }
        
        public void Stop()
        {
            _isRunning = false;
            _timer?.Dispose();
            _timer = null;
            FenLogger.Debug("[Animation] Engine stopped", LogCategory.Layout);
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
                    foreach (var anim in kvp.Value)
                    {
                        if (anim.PlayState == "paused") continue;
                        
                        // Calculate elapsed time
                        double elapsed = (now - anim.StartTime).TotalMilliseconds;
                        
                        // Handle delay
                        if (elapsed < anim.DelayMs)
                        {
                            // Apply backwards fill if applicable
                            if (anim.FillMode == "backwards" || anim.FillMode == "both")
                            {
                                ApplyKeyframeAt(anim, 0);
                                toNotify.Add(element);
                            }
                            continue;
                        }
                        
                        elapsed -= anim.DelayMs;
                        
                        // Calculate iteration and progress
                        double iterationProgress = anim.DurationMs > 0 ? elapsed / anim.DurationMs : 1;
                        int iteration = (int)Math.Floor(iterationProgress);
                        double progress = iterationProgress - iteration;
                        
                        // Check if complete
                        if (anim.IterationCount >= 0 && iteration >= anim.IterationCount)
                        {
                            anim.IsComplete = true;
                            
                            // Apply forwards fill if applicable
                            if (anim.FillMode == "forwards" || anim.FillMode == "both")
                            {
                                ApplyKeyframeAt(anim, 100);
                                toNotify.Add(element);
                            }
                            else
                            {
                                anim.ComputedProperties.Clear();
                            }
                            
                            toRemove.Add((element, anim));
                            OnAnimationEnd?.Invoke(element, anim.AnimationName);
                            continue;
                        }
                        
                        anim.CurrentIteration = iteration;
                        
                        // Handle direction
                        bool reverse = false;
                        switch (anim.Direction)
                        {
                            case "reverse":
                                reverse = true;
                                break;
                            case "alternate":
                                reverse = iteration % 2 == 1;
                                break;
                            case "alternate-reverse":
                                reverse = iteration % 2 == 0;
                                break;
                        }
                        
                        if (reverse)
                            progress = 1 - progress;
                        
                        // Apply easing
                        progress = ApplyEasing(progress, anim.TimingFunction);
                        
                        // Calculate animated values
                        double percentage = progress * 100;
                        ApplyKeyframeAt(anim, percentage);
                        toNotify.Add(element);
                    }
                }
                
                // Remove completed animations
                foreach (var (element, anim) in toRemove)
                {
                    if (_activeAnimations.TryGetValue(element, out var list))
                    {
                        list.Remove(anim);
                        if (list.Count == 0)
                            _activeAnimations.Remove(element);
                    }
                }
            }
            
            // Notify about frame updates
            foreach (var element in toNotify)
            {
                OnAnimationFrame?.Invoke(element);
            }
            
            // Stop engine if no more animations
            lock (_activeAnimations)
            {
                if (_activeAnimations.Count == 0)
                    Stop();
            }
        }
        
        #endregion
        
        #region Keyframe Interpolation
        
        private void ApplyKeyframeAt(ActiveAnimation anim, double percentage)
        {
            var frames = anim.Keyframes.Frames.OrderBy(f => f.Percentage).ToList();
            if (frames.Count == 0) return;
            
            // Find surrounding keyframes
            CssLoader.CssKeyframe fromFrame = null;
            CssLoader.CssKeyframe toFrame = null;
            
            foreach (var frame in frames)
            {
                if (frame.Percentage <= percentage)
                    fromFrame = frame;
                if (frame.Percentage >= percentage && toFrame == null)
                    toFrame = frame;
            }
            
            fromFrame ??= frames[0];
            toFrame ??= frames[frames.Count - 1];
            
            // Calculate interpolation factor
            double t = 0;
            if (fromFrame != toFrame && toFrame.Percentage != fromFrame.Percentage)
            {
                t = (percentage - fromFrame.Percentage) / (toFrame.Percentage - fromFrame.Percentage);
            }
            else if (fromFrame == toFrame)
            {
                t = 0; // At exact keyframe
            }
            
            // Interpolate properties
            anim.ComputedProperties.Clear();
            
            // Get all properties from both keyframes
            var allProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in fromFrame.Properties.Keys) allProps.Add(p);
            foreach (var p in toFrame.Properties.Keys) allProps.Add(p);
            
            foreach (var prop in allProps)
            {
                string fromValue = fromFrame.Properties.TryGetValue(prop, out var fv) ? fv : null;
                string toValue = toFrame.Properties.TryGetValue(prop, out var tv) ? tv : null;
                
                if (fromValue == null) fromValue = toValue;
                if (toValue == null) toValue = fromValue;
                
                string interpolated = InterpolateValue(prop, fromValue, toValue, t);
                if (interpolated != null)
                {
                    anim.ComputedProperties[prop] = interpolated;
                }
            }
        }
        
        /// <summary>
        /// Interpolate between two CSS values
        /// </summary>
        private string InterpolateValue(string property, string from, string to, double t)
        {
            if (from == null || to == null) return to ?? from;
            if (t <= 0) return from;
            if (t >= 1) return to;
            
            // Try numeric interpolation
            if (TryParseNumericValue(from, out double fromNum, out string fromUnit) &&
                TryParseNumericValue(to, out double toNum, out string toUnit) &&
                fromUnit == toUnit)
            {
                double interpolated = fromNum + (toNum - fromNum) * t;
                return $"{interpolated:F2}{fromUnit}";
            }
            
            // Try color interpolation
            if (TryParseColor(from, out var fromColor) && TryParseColor(to, out var toColor))
            {
                byte r = (byte)(fromColor.Red + (toColor.Red - fromColor.Red) * t);
                byte g = (byte)(fromColor.Green + (toColor.Green - fromColor.Green) * t);
                byte b = (byte)(fromColor.Blue + (toColor.Blue - fromColor.Blue) * t);
                byte a = (byte)(fromColor.Alpha + (toColor.Alpha - fromColor.Alpha) * t);
                return $"rgba({r},{g},{b},{a / 255.0:F2})";
            }
            
            // Try transform interpolation
            if (property.ToLower() == "transform")
            {
                return InterpolateTransform(from, to, t);
            }
            
            // Discrete (step) interpolation for non-numeric values
            return t < 0.5 ? from : to;
        }
        
        private string InterpolateTransform(string from, string to, double t)
        {
            // Parse and interpolate common transforms
            var fromParsed = ParseTransform(from);
            var toParsed = ParseTransform(to);
            
            var result = new List<string>();
            
            // Interpolate translate
            if (fromParsed.translateX != 0 || toParsed.translateX != 0 ||
                fromParsed.translateY != 0 || toParsed.translateY != 0)
            {
                double x = fromParsed.translateX + (toParsed.translateX - fromParsed.translateX) * t;
                double y = fromParsed.translateY + (toParsed.translateY - fromParsed.translateY) * t;
                result.Add($"translate({x:F2}px,{y:F2}px)");
            }
            
            // Interpolate scale
            if (fromParsed.scaleX != 1 || toParsed.scaleX != 1 ||
                fromParsed.scaleY != 1 || toParsed.scaleY != 1)
            {
                double sx = fromParsed.scaleX + (toParsed.scaleX - fromParsed.scaleX) * t;
                double sy = fromParsed.scaleY + (toParsed.scaleY - fromParsed.scaleY) * t;
                result.Add($"scale({sx:F3},{sy:F3})");
            }
            
            // Interpolate rotate
            if (fromParsed.rotate != 0 || toParsed.rotate != 0)
            {
                double r = fromParsed.rotate + (toParsed.rotate - fromParsed.rotate) * t;
                result.Add($"rotate({r:F2}deg)");
            }
            
            // Interpolate opacity (if part of transform)
            return result.Count > 0 ? string.Join(" ", result) : "none";
        }
        
        private (double translateX, double translateY, double scaleX, double scaleY, double rotate) ParseTransform(string value)
        {
            double tx = 0, ty = 0, sx = 1, sy = 1, r = 0;
            
            if (string.IsNullOrWhiteSpace(value) || value == "none")
                return (tx, ty, sx, sy, r);
            
            // Parse translate
            var translateMatch = System.Text.RegularExpressions.Regex.Match(value, @"translate\s*\(\s*([-\d.]+)(px|%)?\s*,?\s*([-\d.]+)?(px|%)?\s*\)");
            if (translateMatch.Success)
            {
                double.TryParse(translateMatch.Groups[1].Value, out tx);
                double.TryParse(translateMatch.Groups[3].Value, out ty);
            }
            
            var translateXMatch = System.Text.RegularExpressions.Regex.Match(value, @"translateX\s*\(\s*([-\d.]+)(px|%)?\s*\)");
            if (translateXMatch.Success)
            {
                double.TryParse(translateXMatch.Groups[1].Value, out tx);
            }
            
            var translateYMatch = System.Text.RegularExpressions.Regex.Match(value, @"translateY\s*\(\s*([-\d.]+)(px|%)?\s*\)");
            if (translateYMatch.Success)
            {
                double.TryParse(translateYMatch.Groups[1].Value, out ty);
            }
            
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
                if (rotateMatch.Groups[2].Value == "rad")
                    r = r * 180 / Math.PI;
            }
            
            return (tx, ty, sx, sy, r);
        }
        
        #endregion
        
        #region Easing Functions
        
        private double ApplyEasing(double t, string timingFunction)
        {
            if (string.IsNullOrEmpty(timingFunction))
                return t;
            
            switch (timingFunction.ToLower().Trim())
            {
                case "linear":
                    return t;
                case "ease":
                    return CubicBezier(t, 0.25, 0.1, 0.25, 1.0);
                case "ease-in":
                    return CubicBezier(t, 0.42, 0, 1.0, 1.0);
                case "ease-out":
                    return CubicBezier(t, 0, 0, 0.58, 1.0);
                case "ease-in-out":
                    return CubicBezier(t, 0.42, 0, 0.58, 1.0);
                default:
                    // Try parsing cubic-bezier(...)
                    var match = System.Text.RegularExpressions.Regex.Match(timingFunction, 
                        @"cubic-bezier\s*\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*\)");
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
        
        /// <summary>
        /// Evaluate cubic bezier curve at time t
        /// </summary>
        private double CubicBezier(double t, double p1x, double p1y, double p2x, double p2y)
        {
            // Newton-Raphson iteration to find x for given t
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
        
        private double BezierX(double t, double p1x, double p2x)
        {
            return 3 * (1 - t) * (1 - t) * t * p1x + 3 * (1 - t) * t * t * p2x + t * t * t;
        }
        
        private double BezierY(double t, double p1y, double p2y)
        {
            return 3 * (1 - t) * (1 - t) * t * p1y + 3 * (1 - t) * t * t * p2y + t * t * t;
        }
        
        private double BezierXDerivative(double t, double p1x, double p2x)
        {
            return 3 * (1 - t) * (1 - t) * p1x + 6 * (1 - t) * t * (p2x - p1x) + 3 * t * t * (1 - p2x);
        }
        
        #endregion
        
        #region Parsing Helpers
        
        private string GetAnimationProperty(CssComputed style, string property, int listIndex = 0)
        {
            // Try direct property
            if (style.Map.TryGetValue(property, out var value))
                return GetIndexedAnimationValue(value, listIndex);
            
            // Try shorthand 'animation'
            if (style.Map.TryGetValue("animation", out var shorthand))
            {
                // Parse animation shorthand: name duration timing-function delay iteration-count direction fill-mode play-state
                // This is a simplified parser - full implementation would handle multiple animations
                var parts = shorthand.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                
                switch (property)
                {
                    case "animation-name":
                        // First non-keyword is usually the name
                        foreach (var p in parts)
                        {
                            if (!IsDurationValue(p) && !IsTimingFunction(p) && !IsIterationCount(p) &&
                                !IsDirection(p) && !IsFillMode(p) && !IsPlayState(p))
                                return p;
                        }
                        break;
                    case "animation-duration":
                        foreach (var p in parts)
                        {
                            if (IsDurationValue(p)) return p;
                        }
                        break;
                    case "animation-timing-function":
                        foreach (var p in parts)
                        {
                            if (IsTimingFunction(p)) return p;
                        }
                        break;
                }
            }
            
            return null;
        }

        private static List<string> SplitAnimationList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
        }

        private static string GetIndexedAnimationValue(string raw, int listIndex)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            var parts = SplitAnimationList(raw);
            if (parts.Count == 0)
                return raw.Trim();

            int index = Math.Max(0, Math.Min(listIndex, parts.Count - 1));
            return parts[index];
        }
        
        private double ParseDuration(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 1000;
            
            value = value.Trim().ToLower();
            if (value.EndsWith("ms") && double.TryParse(value.Replace("ms", ""), out double ms))
                return ms;
            if (value.EndsWith("s") && double.TryParse(value.Replace("s", ""), out double s))
                return s * 1000;
            
            return 1000;
        }
        
        private int ParseIterationCount(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 1;
            value = value.Trim().ToLower();
            if (value == "infinite") return -1;
            if (int.TryParse(value, out int count)) return count;
            return 1;
        }
        
        private bool IsDurationValue(string value)
        {
            return value.EndsWith("s") || value.EndsWith("ms");
        }
        
        private bool IsTimingFunction(string value)
        {
            var v = value.ToLower();
            return v == "linear" || v == "ease" || v == "ease-in" || v == "ease-out" || 
                   v == "ease-in-out" || v.StartsWith("cubic-bezier");
        }
        
        private bool IsIterationCount(string value)
        {
            return value.ToLower() == "infinite" || int.TryParse(value, out _);
        }
        
        private bool IsDirection(string value)
        {
            var v = value.ToLower();
            return v == "normal" || v == "reverse" || v == "alternate" || v == "alternate-reverse";
        }
        
        private bool IsFillMode(string value)
        {
            var v = value.ToLower();
            return v == "none" || v == "forwards" || v == "backwards" || v == "both";
        }
        
        private bool IsPlayState(string value)
        {
            var v = value.ToLower();
            return v == "running" || v == "paused";
        }
        
        private bool TryParseNumericValue(string value, out double number, out string unit)
        {
            number = 0;
            unit = "";
            
            if (string.IsNullOrWhiteSpace(value)) return false;
            
            value = value.Trim().ToLower();
            
            // Extract unit
            string[] units = { "px", "%", "em", "rem", "vh", "vw", "deg", "rad", "s", "ms" };
            foreach (var u in units)
            {
                if (value.EndsWith(u))
                {
                    unit = u;
                    value = value.Substring(0, value.Length - u.Length);
                    break;
                }
            }
            
            return double.TryParse(value, out number);
        }
        
        private bool TryParseColor(string value, out SKColor color)
        {
            color = SKColors.Black;
            if (string.IsNullOrWhiteSpace(value)) return false;
            
            value = value.Trim().ToLower();
            
            // Try hex
            if (value.StartsWith("#"))
            {
                try
                {
                    color = SKColor.Parse(value);
                    return true;
                }
                catch { }
            }
            
            // Try rgb/rgba
            var rgbMatch = System.Text.RegularExpressions.Regex.Match(value, 
                @"rgba?\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+))?\s*\)");
            if (rgbMatch.Success)
            {
                byte.TryParse(rgbMatch.Groups[1].Value, out byte r);
                byte.TryParse(rgbMatch.Groups[2].Value, out byte g);
                byte.TryParse(rgbMatch.Groups[3].Value, out byte b);
                byte a = 255;
                if (!string.IsNullOrEmpty(rgbMatch.Groups[4].Value) && 
                    double.TryParse(rgbMatch.Groups[4].Value, out double alpha))
                {
                    a = (byte)(alpha * 255);
                }
                color = new SKColor(r, g, b, a);
                return true;
            }
            
            return false;
        }
        
        #endregion
    }
}




