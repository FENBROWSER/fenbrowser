// =============================================================================
// ContainingBlockResolver.cs
// CSS 2.1 Containing Block Determination
// 
// SPEC REFERENCE: CSS 2.1 §10.1 - Definition of "containing block"
//                 https://www.w3.org/TR/CSS21/visudet.html#containing-block-details
// 
// CONTAINING BLOCK RULES:
//   1. Root element: Initial containing block (viewport)
//   2. Static/relative: Nearest block container ancestor
//   3. Absolute: Nearest positioned ancestor (or ICB if none)
//   4. Fixed: Viewport
// 
// STATUS: ✅ Fully Implemented
// =============================================================================

using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Represents a containing block with its dimensions and position.
    /// </summary>
    public struct ContainingBlock
    {
        public Node Node;
        public float Width;
        public float Height;
        public float X;
        public float Y;
        public bool IsInitial;
        public SKRect PaddingBox { get; set; }

        public SKRect ContentBox => new SKRect(X, Y, X + Width, Y + Height);

        public override string ToString()
        {
            var name = IsInitial ? "ICB" : ((Node as Element)?.TagName ?? "Node");
            return $"CB[{name} {Width}x{Height} at ({X},{Y})]";
        }
    }

    /// <summary>
    /// Resolves containing blocks for positioned elements per CSS 2.1 §10.1.
    /// </summary>
    public class ContainingBlockResolver
    {
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        private readonly IReadOnlyDictionary<Node, BoxModel> _layoutBoxes;
        private readonly float _viewportWidth;
        private readonly float _viewportHeight;
        private readonly Dictionary<Node, ContainingBlock> _cache = new Dictionary<Node, ContainingBlock>();

        public ContainingBlockResolver(
            IReadOnlyDictionary<Node, CssComputed> styles,
            IReadOnlyDictionary<Node, BoxModel> layoutBoxes,
            float viewportWidth,
            float viewportHeight)
        {
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _layoutBoxes = layoutBoxes ?? new Dictionary<Node, BoxModel>();
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
        }

        public ContainingBlock GetInitialContainingBlock()
        {
            return new ContainingBlock
            {
                Node = null,
                Width = _viewportWidth,
                Height = _viewportHeight,
                X = 0,
                Y = 0,
                IsInitial = true,
                PaddingBox = new SKRect(0, 0, _viewportWidth, _viewportHeight)
            };
        }

        public ContainingBlock Resolve(Element element)
        {
            if (element == null)
                return GetInitialContainingBlock();

            if (_cache.TryGetValue(element, out var cached))
                return cached;

            ContainingBlock result;
            var position = GetPosition(element);

            switch (position)
            {
                case "static":
                case "relative":
                    result = ResolveForStaticOrRelative(element);
                    break;
                case "absolute":
                    result = ResolveForAbsolute(element);
                    break;
                case "fixed":
                    result = GetInitialContainingBlock();
                    break;
                case "sticky":
                    result = ResolveForStaticOrRelative(element);
                    break;
                default:
                    result = ResolveForStaticOrRelative(element);
                    break;
            }

            _cache[element] = result;
            return result;
        }

        private ContainingBlock ResolveForStaticOrRelative(Element element)
        {
            var parent = element.Parent;
            while (parent != null)
            {
                if (parent is Element parentElement && IsBlockContainer(parentElement))
                    return BuildContainingBlock(parentElement, useContentBox: true);
                parent = parent.Parent;
            }
            return GetInitialContainingBlock();
        }

        private ContainingBlock ResolveForAbsolute(Element element)
        {
            var parent = element.Parent;
            while (parent != null)
            {
                if (parent is Element parentElement)
                {
                    var parentPosition = GetPosition(parentElement);
                    if (parentPosition != "static")
                        return BuildContainingBlock(parentElement, useContentBox: false);
                }
                parent = parent.Parent;
            }
            return GetInitialContainingBlock();
        }

        private ContainingBlock BuildContainingBlock(Element element, bool useContentBox)
        {
            var cb = new ContainingBlock { Node = element, IsInitial = false };

            if (_layoutBoxes.TryGetValue(element, out var boxModel))
            {
                if (useContentBox)
                {
                    // Static/Relative: CB is formed by the content edge of the ancestor
                    cb.X = boxModel.ContentBox.Left;
                    cb.Y = boxModel.ContentBox.Top;
                    cb.Width = boxModel.ContentBox.Width;
                    cb.Height = boxModel.ContentBox.Height;
                    cb.PaddingBox = boxModel.PaddingBox; 
                }
                else
                {
                    // Absolute: CB is formed by the padding edge of the ancestor
                    cb.X = boxModel.PaddingBox.Left;
                    cb.Y = boxModel.PaddingBox.Top;
                    cb.Width = boxModel.PaddingBox.Width;
                    cb.Height = boxModel.PaddingBox.Height;
                    cb.PaddingBox = boxModel.PaddingBox;
                }
            }
            else
            {
                cb.Width = _viewportWidth;
                cb.Height = _viewportHeight;
                cb.PaddingBox = new SKRect(0, 0, _viewportWidth, _viewportHeight);
            }

            return cb;
        }

        private bool IsBlockContainer(Element element)
        {
            if (!_styles.TryGetValue(element, out var style))
                return true;

            var display = style.Display?.ToLowerInvariant() ?? "block";
            return display == "block" || 
                   display == "list-item" ||
                   display == "table-cell" ||
                   display == "table-caption" ||
                   display == "flex" ||
                   display == "inline-flex" ||
                   display == "grid" ||
                   display == "inline-grid" ||
                   display == "flow-root";
        }

        private string GetPosition(Element element)
        {
            if (_styles.TryGetValue(element, out var style))
                return style.Position?.ToLowerInvariant() ?? "static";
            return "static";
        }

        public void ClearCache() => _cache.Clear();
    }
}
