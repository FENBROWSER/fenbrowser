using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Logging;
using FenBrowser.Core;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Feature 2/3: Container Queries layout support
    /// Enables @container responsive layouts based on container size
    /// </summary>
    public class ContainerQueryResolver
    {
        private static ContainerQueryResolver _instance;
        public static ContainerQueryResolver Instance => _instance ??= new ContainerQueryResolver();
        
        /// <summary>
        /// Resolve container queries during layout
        /// @container (min-width: 400px) { .child { flex-direction: row; } }
        /// </summary>
        public void ApplyContainerQueries(LayoutBox container, LayoutBox child)
        {
            if (container.ComputedStyle?.ContainerQueries == null || !container.ComputedStyle.ContainerQueries.Any())
                return;
            
            var containerWidth = container.Geometry.ContentBox.Width;
            var containerHeight = container.Geometry.ContentBox.Height;
            
            // Evaluate each container query
            foreach (var cq in container.ComputedStyle.ContainerQueries)
            {
                if (MatchesCondition(cq, containerWidth, containerHeight))
                {
                    ApplyQueryStyles(child, cq.Rules);
                    EngineLogCompat.Debug($"[ContainerQuery] Applied {cq.Selector} @ {containerWidth:F0}px", LogCategory.Layout);
                }
            }
        }
        
        private bool MatchesCondition(ContainerQuery query, float width, float height)
        {
            foreach (var condition in query.Conditions)
            {
                bool matches = condition.Feature switch
                {
                    "min-width" => width >= ParseLength(condition.Value),
                    "max-width" => width <= ParseLength(condition.Value),
                    "min-height" => height >= ParseLength(condition.Value),
                    "max-height" => height <= ParseLength(condition.Value),
                    _ => false
                };
                
                if (!matches && !condition.IsNot) return false;
                if (matches && condition.IsNot) return false;
            }
            return true;
        }
        
        private void ApplyQueryStyles(LayoutBox element, Dictionary<string, string> rules)
        {
            foreach (var rule in rules)
            {
                // Apply style overrides based on container size
                switch (rule.Key.ToLowerInvariant())
                {
                    case "flex-direction":
                        element.ComputedStyle.FlexDirection = rule.Value;
                        break;
                    case "display":
                        element.ComputedStyle.Display = rule.Value;
                        break;
                }
            }
        }
        
        private float ParseLength(string value)
        {
            if (float.TryParse(value.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float px))
                return px;
            
            // Calculate relative units like em, rem
            return 0f;
        }
    }
}