using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// CSS Transitions Engine - Manages smooth property animations between style changes.
    /// Tracks style changes and interpolates values over time based on transition-* properties.
    /// </summary>
    public class CssTransitionEngine
    {
        private readonly Dictionary<Element, Dictionary<string, ActiveTransition>> _activeTransitions = new();
        private readonly Dictionary<Element, CssComputed> _previousStyles = new();

        /// <summary>
        /// Represents an active transition for a single property
        /// </summary>
        public class ActiveTransition
        {
            public string Property { get; set; }
            public float StartValue { get; set; }
            public float EndValue { get; set; }
            public double DurationMs { get; set; }
            public double DelayMs { get; set; }
            public EasingFunction Easing { get; set; }
            public double StartTimeMs { get; set; }
            public float CurrentValue { get; set; }
            public bool IsComplete { get; set; }
        }

        public enum EasingFunction
        {
            Linear,
            Ease,
            EaseIn,
            EaseOut,
            EaseInOut,
            CubicBezier
        }

        /// <summary>
        /// Update transitions and get interpolated style values
        /// </summary>
        public void UpdateTransitions(Element element, CssComputed currentStyle, double currentTimeMs)
        {
            if (element == null || currentStyle == null) return;

            // Get or create transition state for this element
            if (!_activeTransitions.TryGetValue(element, out var transitions))
            {
                transitions = new Dictionary<string, ActiveTransition>();
                _activeTransitions[element] = transitions;
            }

            // Get previous style for comparison
            _previousStyles.TryGetValue(element, out var prevStyle);

            // Parse transition properties
            var transitionProps = ParseTransitionProperty(currentStyle.TransitionProperty);
            var durations = ParseTimings(currentStyle.TransitionDuration);
            var delays = ParseTimings(currentStyle.TransitionDelay);
            var easings = ParseEasings(currentStyle.TransitionTimingFunction);

            // Check for animatable property changes
            var animatableProps = new[] {
                "opacity", "width", "height", "left", "top", "right", "bottom",
                "margin-left", "margin-right", "margin-top", "margin-bottom",
                "padding-left", "padding-right", "padding-top", "padding-bottom",
                "font-size", "border-width", "border-radius"
            };

            foreach (var prop in animatableProps)
            {
                if (!ShouldTransition(transitionProps, prop)) continue;

                float? currentVal = GetNumericValue(currentStyle, prop);
                float? prevVal = prevStyle != null ? GetNumericValue(prevStyle, prop) : null;

                if (currentVal.HasValue && prevVal.HasValue && Math.Abs(currentVal.Value - prevVal.Value) > 0.01f)
                {
                    // Value changed - check if already transitioning
                    if (!transitions.TryGetValue(prop, out var existing) || existing.IsComplete)
                    {
                        // Start new transition
                        int idx = Array.IndexOf(transitionProps, prop) % Math.Max(1, transitionProps.Length);
                        transitions[prop] = new ActiveTransition
                        {
                            Property = prop,
                            StartValue = prevVal.Value,
                            EndValue = currentVal.Value,
                            DurationMs = idx < durations.Length ? durations[idx] : 300,
                            DelayMs = idx < delays.Length ? delays[idx] : 0,
                            Easing = idx < easings.Length ? easings[idx] : EasingFunction.Ease,
                            StartTimeMs = currentTimeMs,
                            CurrentValue = prevVal.Value,
                            IsComplete = false
                        };
                    }
                    else if (Math.Abs(existing.EndValue - currentVal.Value) > 0.01f)
                    {
                        // Target changed mid-transition - retarget smoothly
                        existing.StartValue = existing.CurrentValue;
                        existing.EndValue = currentVal.Value;
                        existing.StartTimeMs = currentTimeMs;
                    }
                }
            }

            // Update all active transitions
            var completed = new List<string>();
            foreach (var kvp in transitions)
            {
                var t = kvp.Value;
                double elapsed = currentTimeMs - t.StartTimeMs - t.DelayMs;
                
                if (elapsed < 0)
                {
                    t.CurrentValue = t.StartValue;
                }
                else if (elapsed >= t.DurationMs)
                {
                    t.CurrentValue = t.EndValue;
                    t.IsComplete = true;
                    completed.Add(kvp.Key);
                }
                else
                {
                    float progress = (float)(elapsed / t.DurationMs);
                    float easedProgress = ApplyEasing(progress, t.Easing);
                    t.CurrentValue = t.StartValue + (t.EndValue - t.StartValue) * easedProgress;
                }

                // Apply interpolated value to style
                SetNumericValue(currentStyle, t.Property, t.CurrentValue);
            }

            // Clean up completed transitions
            foreach (var key in completed)
            {
                transitions.Remove(key);
            }

            // Store current style for next frame comparison
            _previousStyles[element] = currentStyle.Clone();
        }

        /// <summary>
        /// Check if any transitions are currently active (for triggering repaints)
        /// </summary>
        public bool HasActiveTransitions(Element element)
        {
            if (!_activeTransitions.TryGetValue(element, out var transitions))
                return false;
            return transitions.Count > 0 && transitions.Values.Any(t => !t.IsComplete);
        }

        /// <summary>
        /// Get count of all active transitions across all elements
        /// </summary>
        public int ActiveTransitionCount => _activeTransitions.Values.Sum(d => d.Count(kvp => !kvp.Value.IsComplete));

        private bool ShouldTransition(string[] transitionProps, string prop)
        {
            if (transitionProps.Length == 0) return false;
            if (transitionProps.Contains("all")) return true;
            if (transitionProps.Contains("none")) return false;
            return transitionProps.Contains(prop);
        }

        private string[] ParseTransitionProperty(string value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
            return value.Split(',').Select(s => s.Trim().ToLowerInvariant()).ToArray();
        }

        private double[] ParseTimings(string value)
        {
            if (string.IsNullOrEmpty(value)) return new[] { 300.0 };
            var parts = value.Split(',');
            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = ParseDuration(parts[i].Trim());
            }
            return result;
        }

        private double ParseDuration(string s)
        {
            if (string.IsNullOrEmpty(s)) return 300;
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("ms"))
            {
                if (double.TryParse(s.Replace("ms", ""), out double ms))
                    return ms;
            }
            else if (s.EndsWith("s"))
            {
                if (double.TryParse(s.Replace("s", ""), out double sec))
                    return sec * 1000;
            }
            return 300;
        }

        private EasingFunction[] ParseEasings(string value)
        {
            if (string.IsNullOrEmpty(value)) return new[] { EasingFunction.Ease };
            var parts = value.Split(',');
            var result = new EasingFunction[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = ParseEasing(parts[i].Trim());
            }
            return result;
        }

        private EasingFunction ParseEasing(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "linear": return EasingFunction.Linear;
                case "ease": return EasingFunction.Ease;
                case "ease-in": return EasingFunction.EaseIn;
                case "ease-out": return EasingFunction.EaseOut;
                case "ease-in-out": return EasingFunction.EaseInOut;
                default: return EasingFunction.Ease;
            }
        }

        private float ApplyEasing(float t, EasingFunction easing)
        {
            switch (easing)
            {
                case EasingFunction.Linear:
                    return t;
                case EasingFunction.Ease:
                    // cubic-bezier(0.25, 0.1, 0.25, 1)
                    return CubicBezier(t, 0.25f, 0.1f, 0.25f, 1f);
                case EasingFunction.EaseIn:
                    // cubic-bezier(0.42, 0, 1, 1)
                    return CubicBezier(t, 0.42f, 0f, 1f, 1f);
                case EasingFunction.EaseOut:
                    // cubic-bezier(0, 0, 0.58, 1)
                    return CubicBezier(t, 0f, 0f, 0.58f, 1f);
                case EasingFunction.EaseInOut:
                    // cubic-bezier(0.42, 0, 0.58, 1)
                    return CubicBezier(t, 0.42f, 0f, 0.58f, 1f);
                default:
                    return t;
            }
        }

        private float CubicBezier(float t, float x1, float y1, float x2, float y2)
        {
            // Simplified cubic bezier approximation
            // For accurate results, should solve for t given x, but this is close enough
            float t2 = t * t;
            float t3 = t2 * t;
            float mt = 1 - t;
            float mt2 = mt * mt;
            float mt3 = mt2 * mt;
            
            // Calculate y value at parameter t
            return 3 * mt2 * t * y1 + 3 * mt * t2 * y2 + t3;
        }

        private float? GetNumericValue(CssComputed style, string prop)
        {
            switch (prop)
            {
                case "opacity": return (float?)style.Opacity;
                case "width": return (float?)style.Width;
                case "height": return (float?)style.Height;
                case "left": return (float?)style.Left;
                case "top": return (float?)style.Top;
                case "right": return (float?)style.Right;
                case "bottom": return (float?)style.Bottom;
                case "font-size": return (float?)style.FontSize;
                case "margin-left": return style.Margin != null ? (float?)style.Margin.Left : null;
                case "margin-right": return style.Margin != null ? (float?)style.Margin.Right : null;
                case "margin-top": return style.Margin != null ? (float?)style.Margin.Top : null;
                case "margin-bottom": return style.Margin != null ? (float?)style.Margin.Bottom : null;
                case "padding-left": return style.Padding != null ? (float?)style.Padding.Left : null;
                case "padding-right": return style.Padding != null ? (float?)style.Padding.Right : null;
                case "padding-top": return style.Padding != null ? (float?)style.Padding.Top : null;
                case "padding-bottom": return style.Padding != null ? (float?)style.Padding.Bottom : null;
                default: return null;
            }
        }

        private void SetNumericValue(CssComputed style, string prop, float value)
        {
            switch (prop)
            {
                case "opacity": style.Opacity = value; break;
                case "width": style.Width = value; break;
                case "height": style.Height = value; break;
                case "left":
                    style.Left = value;
                    style.LeftPercent = null;
                    break;
                case "top":
                    style.Top = value;
                    style.TopPercent = null;
                    break;
                case "right":
                    style.Right = value;
                    style.RightPercent = null;
                    break;
                case "bottom":
                    style.Bottom = value;
                    style.BottomPercent = null;
                    break;
                case "font-size": style.FontSize = value; break;
                case "margin-left":
                    var ml = style.Margin != null ? style.Margin : new Thickness();
                    style.Margin = new Thickness(value, ml.Top, ml.Right, ml.Bottom);
                    break;
                case "margin-right":
                    var mr = style.Margin != null ? style.Margin : new Thickness();
                    style.Margin = new Thickness(mr.Left, mr.Top, value, mr.Bottom);
                    break;
                case "margin-top":
                    var mt = style.Margin != null ? style.Margin : new Thickness();
                    style.Margin = new Thickness(mt.Left, value, mt.Right, mt.Bottom);
                    break;
                case "margin-bottom":
                    var mb = style.Margin != null ? style.Margin : new Thickness();
                    style.Margin = new Thickness(mb.Left, mb.Top, mb.Right, value);
                    break;
            }
        }

        /// <summary>
        /// Clear all transition state (e.g., on page navigation)
        /// </summary>
        public void Clear()
        {
            _activeTransitions.Clear();
            _previousStyles.Clear();
        }
    }
}

