// SpecRef: CSS Cascading and Inheritance Level 4 – Parallel style computation
// CapabilityId: CSS-CASCADE-PARALLEL-01
// Determinism: strict (parent→child ordering preserved per subtree)
// FallbackPolicy: serial-degrade
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Schedules CSS cascade computation across multiple threads.
    ///
    /// Strategy:
    ///   1. Process <html> and its immediate structure (<head>, <body>) serially
    ///      to establish root font-size and base inherited values.
    ///   2. Fan out <body>'s children (the real content subtrees) across
    ///      Parallel.ForEach with bounded concurrency.
    ///   3. Within each subtree, walk depth-first (iterative stack) to preserve
    ///      parent→child inheritance order.
    ///
    /// Thread safety contract:
    ///   - CascadeEngine indexes are built once via EnsureIndex() before
    ///     parallel work begins (warm-up call in Cascade()).
    ///   - CascadeEngine._processedRules is [ThreadStatic], so each worker
    ///     thread gets its own dedup set.
    ///   - ConcurrentDictionary is used for the shared result map.
    ///   - Element.ComputedStyle writes are per-node and each node is visited
    ///     by exactly one thread.
    /// </summary>
    public static class ParallelCascadeScheduler
    {
        // Minimum child count before we bother going parallel.
        // Below this threshold serial is faster due to scheduling overhead.
        private const int ParallelThreshold = 4;

        public static Dictionary<Node, CssComputed> Cascade(
            Element root, 
            StyleSet styleSet, 
            Action<string> log, 
            FenBrowser.Core.Deadlines.FrameDeadline deadline)
        {
            return Cascade(root, styleSet, log, deadline, out _);
        }

        public static Dictionary<Node, CssComputed> Cascade(
            Element root,
            StyleSet styleSet,
            Action<string> log,
            FenBrowser.Core.Deadlines.FrameDeadline deadline,
            out InlineStyleCacheStatistics inlineStyleCacheStatistics)
        {
            if (root == null)
            {
                inlineStyleCacheStatistics = default;
                return new Dictionary<Node, CssComputed>();
            }

            var result = new ConcurrentDictionary<Node, CssComputed>();
            var engine = new CascadeEngine(styleSet);

            // Force index build on the calling thread before any parallel work.
            // After this call all index fields are read-only.
            engine.HasPseudoRules("before");

            // Phase 1: Serial pass – process root (<html>) and structural nodes
            // (<head>, direct children of <html>) to establish inherited base values
            // and root font size.
            ComputeSingleNode(root, engine, result, log, deadline, root);

            // Walk root's children serially (typically <head> and <body>).
            // For <body>, we defer its children to parallel phase.
            Element bodyElement = null;
            foreach (var child in root.ChildNodes)
            {
                if (child is Element el)
                {
                    ComputeSingleNode(el, engine, result, log, deadline, root);

                    if (string.Equals(el.TagName, "body", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(el.TagName, "BODY", StringComparison.Ordinal))
                    {
                        bodyElement = el;
                    }
                    else
                    {
                        // Non-body top-level elements (<head>) – process subtree serially.
                        ProcessSubtreeSerial(el, engine, result, log, deadline, root);
                    }
                }
            }

            if (bodyElement == null)
            {
                // No <body> found – degenerate document; everything was already processed.
                inlineStyleCacheStatistics = engine.GetInlineStyleCacheStatistics();
                return new Dictionary<Node, CssComputed>(result);
            }

            // Phase 2: Collect <body>'s direct children as parallel work items.
            var bodyChildren = new List<Element>();
            foreach (var child in bodyElement.ChildNodes)
            {
                if (child is Element el)
                    bodyChildren.Add(el);
            }

            if (bodyChildren.Count < ParallelThreshold)
            {
                // Small document – serial is faster.
                foreach (var child in bodyChildren)
                {
                    ComputeSingleNode(child, engine, result, log, deadline, root);
                    ProcessSubtreeSerial(child, engine, result, log, deadline, root);
                }
            }
            else
            {
                // Phase 3: Parallel fan-out across body's children.
                Parallel.ForEach(bodyChildren, 
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, 
                    subtreeRoot =>
                {
                    ComputeSingleNode(subtreeRoot, engine, result, log, deadline, root);
                    ProcessSubtreeSerial(subtreeRoot, engine, result, log, deadline, root);
                });
            }

            inlineStyleCacheStatistics = engine.GetInlineStyleCacheStatistics();
            return new Dictionary<Node, CssComputed>(result);
        }

        /// <summary>
        /// Iterative depth-first traversal of a subtree's descendants (excluding the root itself).
        /// Processes children in document order by pushing right-to-left onto the stack.
        /// </summary>
        private static void ProcessSubtreeSerial(
            Element subtreeRoot, 
            CascadeEngine engine, 
            ConcurrentDictionary<Node, CssComputed> result,
            Action<string> log, 
            FenBrowser.Core.Deadlines.FrameDeadline deadline,
            Element docRoot)
        {
            var stack = new Stack<Element>();
            PushChildElements(subtreeRoot, stack);

            while (stack.Count > 0)
            {
                var n = stack.Pop();
                ComputeSingleNode(n, engine, result, log, deadline, docRoot);
                PushChildElements(n, stack);
            }
        }

        private static void PushChildElements(Element parent, Stack<Element> stack)
        {
            var shadowChildren = parent.ShadowRoot?.ChildNodes;
            if (shadowChildren != null)
            {
                for (int i = shadowChildren.Length - 1; i >= 0; i--)
                {
                    if (shadowChildren[i] is Element childEl)
                    {
                        stack.Push(childEl);
                    }
                }
            }

            var children = parent.ChildNodes;
            for (int i = children.Length - 1; i >= 0; i--)
            {
                if (children[i] is Element childEl)
                {
                    stack.Push(childEl);
                }
            }
        }

        private static void ComputeSingleNode(
            Element n, 
            CascadeEngine engine, 
            ConcurrentDictionary<Node, CssComputed> result,
            Action<string> log, 
            FenBrowser.Core.Deadlines.FrameDeadline deadline,
            Element root)
        {
            deadline?.Check();
            if (n.IsText()) return;

            CssComputed parentCss = null;
            var cascadeParent = n.ParentElement;
            if (cascadeParent == null && n.ParentNode is ShadowRoot shadowRoot)
            {
                cascadeParent = shadowRoot.Host;
            }

            if (cascadeParent != null)
            {
                result.TryGetValue(cascadeParent, out parentCss);
            }

            try
            {
                var mainProps = engine.ComputeCascadedValues(n, null);
                var css = CssLoader.ResolveStyle(n, parentCss, mainProps);

                if (ReferenceEquals(n, root) && css.FontSize.HasValue && css.FontSize.Value > 0 && double.IsFinite(css.FontSize.Value))
                {
                    CssLoader.SetRootFontSize(css.FontSize.Value);
                }

                if (engine.HasPseudoRules("before")) CssLoader.ResolvePseudo(n, css, engine, "before", (c, s) => c.Before = s);
                if (engine.HasPseudoRules("after")) CssLoader.ResolvePseudo(n, css, engine, "after", (c, s) => c.After = s);
                if (engine.HasPseudoRules("marker")) CssLoader.ResolvePseudo(n, css, engine, "marker", (c, s) => c.Marker = s);
                if (engine.HasPseudoRules("placeholder")) CssLoader.ResolvePseudo(n, css, engine, "placeholder", (c, s) => c.Placeholder = s);
                if (engine.HasPseudoRules("selection")) CssLoader.ResolvePseudo(n, css, engine, "selection", (c, s) => c.Selection = s);
                if (engine.HasPseudoRules("first-line")) CssLoader.ResolvePseudo(n, css, engine, "first-line", (c, s) => c.FirstLine = s);
                if (engine.HasPseudoRules("first-letter")) CssLoader.ResolvePseudo(n, css, engine, "first-letter", (c, s) => c.FirstLetter = s);

                result[n] = css;
                FenBrowser.FenEngine.Layout.LayoutStyleResolver.NormalizeForLayout(css);
                n.ComputedStyle = css;
            }
            catch (Exception)
            {
                var recovery = new CssComputed();
                result[n] = recovery;
                n.ComputedStyle = recovery;
            }
        }

    }
}
