using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a stacking context in the CSS paint order.
    /// 
    /// Key CSS properties that create new stacking contexts:
    /// - position: absolute/relative/fixed with z-index != auto
    /// - opacity < 1
    /// - transform (any value except none)
    /// - filter (any value except none)
    /// - will-change (certain values)
    /// - contain: layout/paint/strict/content
    /// - isolation: isolate
    /// - mix-blend-mode (any value except normal)
    /// - clip-path (any value except none)
    /// - mask (any value except none)
    /// </summary>
    public class StackingContext
    {
        /// <summary>
        /// The DOM element that created this stacking context
        /// </summary>
        public LiteElement Element { get; set; }
        
        /// <summary>
        /// Z-index of this stacking context (0 for auto)
        /// </summary>
        public int ZIndex { get; set; }
        
        /// <summary>
        /// Whether this is the root stacking context (document root)
        /// </summary>
        public bool IsRoot { get; set; }
        
        /// <summary>
        /// Child stacking contexts (elements that create new contexts)
        /// </summary>
        public List<StackingContext> Children { get; } = new List<StackingContext>();
        
        /// <summary>
        /// Render commands that belong to this stacking context
        /// Commands are drawn within this context's isolation boundary
        /// </summary>
        public List<RenderCommand> Commands { get; } = new List<RenderCommand>();
        
        /// <summary>
        /// Non-positioned block-level descendants (paint between negative z-index and positioned)
        /// </summary>
        public List<RenderCommand> BlockCommands { get; } = new List<RenderCommand>();
        
        /// <summary>
        /// Float descendants
        /// </summary>
        public List<RenderCommand> FloatCommands { get; } = new List<RenderCommand>();
        
        /// <summary>
        /// Inline-level descendants
        /// </summary>
        public List<RenderCommand> InlineCommands { get; } = new List<RenderCommand>();
        
        /// <summary>
        /// Flattens this stacking context tree into correct paint order.
        /// 
        /// CSS 2.1 Appendix E - Painting Order:
        /// 1. Background and borders of the stacking context root
        /// 2. Child stacking contexts with negative z-index (in z-index order)
        /// 3. Block-level descendants in tree order
        /// 4. Float descendants in tree order
        /// 5. Inline-level descendants in tree order
        /// 6. Positioned descendants with z-index: auto (tree order)
        /// 7. Child stacking contexts with z-index >= 0 (in z-index order)
        /// </summary>
        public IEnumerable<RenderCommand> Flatten()
        {
            var result = new List<RenderCommand>();
            
            // 1. Background and borders of this context (Commands list)
            result.AddRange(Commands);
            
            // 2. Child contexts with negative z-index
            var negativeZChildren = Children.Where(c => c.ZIndex < 0).OrderBy(c => c.ZIndex);
            foreach (var child in negativeZChildren)
            {
                result.AddRange(child.Flatten());
            }
            
            // 3. Block-level descendants
            result.AddRange(BlockCommands);
            
            // 4. Float descendants
            result.AddRange(FloatCommands);
            
            // 5. Inline-level descendants
            result.AddRange(InlineCommands);
            
            // 6-7. Child contexts with z-index >= 0 (includes auto = 0)
            var positiveZChildren = Children.Where(c => c.ZIndex >= 0).OrderBy(c => c.ZIndex);
            foreach (var child in positiveZChildren)
            {
                result.AddRange(child.Flatten());
            }
            
            return result;
        }
        
        /// <summary>
        /// Add a command to the appropriate list based on element type
        /// </summary>
        public void AddCommand(RenderCommand command, CommandType type)
        {
            switch (type)
            {
                case CommandType.Background:
                    Commands.Add(command);
                    break;
                case CommandType.Block:
                    BlockCommands.Add(command);
                    break;
                case CommandType.Float:
                    FloatCommands.Add(command);
                    break;
                case CommandType.Inline:
                    InlineCommands.Add(command);
                    break;
            }
        }
    }
    
    /// <summary>
    /// Type of command for proper paint order placement
    /// </summary>
    public enum CommandType
    {
        Background,  // Background/border of element
        Block,       // Non-positioned block-level
        Float,       // Floated elements
        Inline       // Inline-level elements
    }
    
    /// <summary>
    /// Builder class for constructing the stacking context tree
    /// </summary>
    public class StackingContextBuilder
    {
        private readonly Dictionary<LiteElement, CssComputed> _styles;
        
        public StackingContextBuilder(Dictionary<LiteElement, CssComputed> styles)
        {
            _styles = styles;
        }
        
        /// <summary>
        /// Build the complete stacking context tree from the DOM root
        /// </summary>
        public StackingContext BuildTree(LiteElement root)
        {
            var rootContext = new StackingContext
            {
                Element = root,
                IsRoot = true,
                ZIndex = 0
            };
            
            BuildRecursive(root, rootContext);
            return rootContext;
        }
        
        /// <summary>
        /// Recursively build the stacking context tree
        /// </summary>
        private void BuildRecursive(LiteElement node, StackingContext parentContext)
        {
            if (node.Children == null) return;
            
            foreach (var child in node.Children)
            {
                CssComputed style = null;
                _styles?.TryGetValue(child, out style);
                
                if (CreatesStackingContext(style))
                {
                    // This element creates a new stacking context
                    var childContext = new StackingContext
                    {
                        Element = child,
                        ZIndex = style?.ZIndex ?? 0,
                        IsRoot = false
                    };
                    
                    parentContext.Children.Add(childContext);
                    BuildRecursive(child, childContext);
                }
                else
                {
                    // Element belongs to parent's stacking context
                    BuildRecursive(child, parentContext);
                }
            }
        }
        
        /// <summary>
        /// Determines if an element creates a new stacking context based on CSS specs.
        /// </summary>
        private bool CreatesStackingContext(CssComputed style)
        {
            if (style == null) return false;
            
            // Position with z-index
            bool isPositioned = style.Position != null && style.Position != "static";
            if (isPositioned && style.ZIndex.HasValue && style.ZIndex.Value != 0)
                return true;
            
            // Opacity < 1
            if (style.Opacity.HasValue && style.Opacity.Value < 1)
                return true;
            
            // Transform
            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none")
                return true;
            
            // Filter
            if (!string.IsNullOrEmpty(style.Filter) && style.Filter != "none")
                return true;
            
            // Will-change
            string willChange = style.Map?.TryGetValue("will-change", out var wc) == true ? wc : null;
            if (!string.IsNullOrEmpty(willChange) && 
                (willChange.Contains("opacity") || willChange.Contains("transform")))
                return true;
            
            // Contain
            string contain = style.Map?.TryGetValue("contain", out var cn) == true ? cn : null;
            if (!string.IsNullOrEmpty(contain) && contain != "none" && 
                (contain.Contains("layout") || contain.Contains("paint") || 
                 contain.Contains("strict") || contain.Contains("content")))
                return true;
            
            // Isolation
            string isolation = style.Map?.TryGetValue("isolation", out var iso) == true ? iso : null;
            if (isolation == "isolate")
                return true;
            
            // Mix-blend-mode
            string blendMode = style.Map?.TryGetValue("mix-blend-mode", out var mbm) == true ? mbm : null;
            if (!string.IsNullOrEmpty(blendMode) && blendMode != "normal")
                return true;
            
            // Clip-path
            if (!string.IsNullOrEmpty(style.ClipPath) && style.ClipPath != "none")
                return true;
            
            // Flex/Grid container items with z-index
            if ((style.Display == "flex" || style.Display == "grid" || 
                 style.Display == "inline-flex" || style.Display == "inline-grid") && 
                style.ZIndex.HasValue)
                return true;
            
            return false;
        }
    }
}
