using System;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Evaluates the CSS <c>contain</c> property (CSS Containment Level 2).
    /// Parses the token list and expands the <c>strict</c>/<c>content</c>
    /// keywords into the individual containment kinds so every enforcement
    /// point (formatting-context resolution, paint clipping, intrinsic sizing)
    /// can query the same flags.
    /// </summary>
    public static class ContainmentEvaluator
    {
        [Flags]
        private enum Flags
        {
            None = 0,
            Layout = 1 << 0,
            Paint = 1 << 1,
            Size = 1 << 2,
            Style = 1 << 3,
            InlineSize = 1 << 4,
            BlockSize = 1 << 5
        }

        /// <summary>
        /// Parses a raw <c>contain</c> value into flags, expanding strict/content.
        /// Invalid tokens are ignored per spec (unknown keywords are dropped).
        /// </summary>
        private static Flags Parse(string contain)
        {
            Flags flags = Flags.None;
            if (string.IsNullOrWhiteSpace(contain))
            {
                return flags;
            }

            var tokens = contain.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawToken in tokens)
            {
                var token = rawToken.Trim().ToLowerInvariant();
                switch (token)
                {
                    case "strict":
                        flags |= Flags.Layout | Flags.Paint | Flags.Size | Flags.Style;
                        break;
                    case "content":
                        flags |= Flags.Layout | Flags.Paint | Flags.Style;
                        break;
                    case "layout":
                        flags |= Flags.Layout;
                        break;
                    case "paint":
                        flags |= Flags.Paint;
                        break;
                    case "size":
                        flags |= Flags.Size;
                        break;
                    case "style":
                        flags |= Flags.Style;
                        break;
                    case "inline-size":
                        flags |= Flags.InlineSize;
                        break;
                    case "block-size":
                        flags |= Flags.BlockSize;
                        break;
                }
            }

            return flags;
        }

        /// <summary>
        /// Layout containment: the element establishes an independent formatting
        /// context; its contents cannot affect layout outside the element.
        /// </summary>
        public static bool HasLayoutContainment(CssComputed style)
        {
            return style != null && (Parse(style.Contain) & Flags.Layout) != 0;
        }

        /// <summary>
        /// Paint containment: the element's descendants are clipped to its border
        /// box (combined with the normal overflow clip, like overflow:hidden).
        /// </summary>
        public static bool HasPaintContainment(CssComputed style)
        {
            return style != null && (Parse(style.Contain) & Flags.Paint) != 0;
        }

        /// <summary>
        /// Size containment: the element's size is computed without considering
        /// its contents; auto sizes resolve to zero in the contained axes.
        /// </summary>
        public static bool HasSizeContainment(CssComputed style)
        {
            return style != null && (Parse(style.Contain) & Flags.Size) != 0;
        }

        /// <summary>
        /// Inline-size containment: auto width is resolved without content.
        /// </summary>
        public static bool HasInlineSizeContainment(CssComputed style)
        {
            return style != null && (Parse(style.Contain) & (Flags.Size | Flags.InlineSize)) != 0;
        }

        /// <summary>
        /// Block-size containment: auto height is resolved without content.
        /// </summary>
        public static bool HasBlockSizeContainment(CssComputed style)
        {
            return style != null && (Parse(style.Contain) & (Flags.Size | Flags.BlockSize)) != 0;
        }

        /// <summary>
        /// Style containment: counter and quote scoping inside the element.
        /// </summary>
        public static bool HasStyleContainment(CssComputed style)
        {
            return style != null && (Parse(style.Contain) & Flags.Style) != 0;
        }
    }
}
