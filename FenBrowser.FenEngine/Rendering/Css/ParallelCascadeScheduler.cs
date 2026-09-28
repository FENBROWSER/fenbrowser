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

            // :link / :visited consult the document's iframe URLs, which are cached per
            // document. Drop that cache here so this pass sees frames added or renavigated
            // since the last one; the DOM does not change while the pass runs.
            ElementStateManager.Instance.InvalidateFrameUrlCache();

            var result = new ConcurrentDictionary<Node, CssComputed>();
            var engine = new CascadeEngine(styleSet);

            // The document's @counter-style rules, for the markers and counter()
            // values this cascade's styles name.
            FenBrowser.FenEngine.Layout.CounterStyleRegistry.Rebuild(root.OwnerDocument, styleSet);

            // Force index build on the calling thread before any parallel work.
            // After this call all index fields are read-only.
            engine.HasPseudoRules("before");

            // Phase 1: Serial pass – process root (<html>) and structural nodes
            // (<head>, direct children of <html>) to establish inherited base values
            // and root font size.
            ComputeSingleNode(root, engine, result, log, deadline, root);

            // An incremental recascade is rooted at the element that went dirty, and that
            // can be a shadow host. Its shadow tree is part of its subtree; the loop below
            // walks light children only, so shadow content never got a computed style.
            var rootShadowRoot = root.GetAttachedShadowRoot();
            if (rootShadowRoot != null)
            {
                foreach (var child in rootShadowRoot.ChildNodes)
                {
                    if (child is Element shadowChild)
                    {
                        ComputeSingleNode(shadowChild, engine, result, log, deadline, root);
                        ProcessSubtreeSerial(shadowChild, engine, result, log, deadline, root);
                    }
                }
            }

            if (!ReferenceEquals(root, root.OwnerDocument?.DocumentElement))
            {
                var incrementalChildren = new List<Element>();
                foreach (var child in root.ChildNodes)
                {
                    if (child is Element element)
                    {
                        incrementalChildren.Add(element);
                    }
                }

                ProcessChildSubtrees(incrementalChildren, engine, result, log, deadline, root);
                inlineStyleCacheStatistics = engine.GetInlineStyleCacheStatistics();
                return new Dictionary<Node, CssComputed>(result);
            }

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

            // A shadow tree on <body> is styled before its light children, which may
            // be slotted into it and inherit from their slots.
            if (bodyElement.GetAttachedShadowRoot() is { } bodyShadowRoot)
            {
                foreach (var child in bodyShadowRoot.ChildNodes)
                {
                    if (child is Element shadowChild)
                    {
                        ComputeSingleNode(shadowChild, engine, result, log, deadline, root);
                        ProcessSubtreeSerial(shadowChild, engine, result, log, deadline, root);
                    }
                }
            }

            // Phase 2: Collect <body>'s direct children as parallel work items.
            var bodyChildren = new List<Element>();
            foreach (var child in bodyElement.ChildNodes)
            {
                if (child is Element el)
                    bodyChildren.Add(el);
            }

            ProcessChildSubtrees(bodyChildren, engine, result, log, deadline, root);

            inlineStyleCacheStatistics = engine.GetInlineStyleCacheStatistics();
            double __f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            FenBrowser.Core.EngineLogCompat.Log(FenBrowser.Core.Logging.LogCategory.Rendering, FenBrowser.Core.Logging.LogLevel.Info, $"[PERF-CASCADE] elemVisits={CascadeEngine.NElems} collectMs={CascadeEngine.TCollect*__f:F0} sortMs={CascadeEngine.TSort*__f:F0} applyMs={CascadeEngine.TApply*__f:F0} matchedDecls={CascadeEngine.NMatches} universalBucket={engine.UniversalCandidateCount} cacheHitMs={CascadeEngine.TCacheHit*__f:F0} cacheHits={CascadeEngine.NCacheHit} mainPass={CascadeEngine.NMain} pseudoPass={CascadeEngine.NPseudo} pseudoCollectMs={CascadeEngine.TPseudoCollect*__f:F0}");
            return new Dictionary<Node, CssComputed>(result);
        }

        private static void ProcessChildSubtrees(
            List<Element> children,
            CascadeEngine engine,
            ConcurrentDictionary<Node, CssComputed> result,
            Action<string> log,
            FenBrowser.Core.Deadlines.FrameDeadline deadline,
            Element docRoot)
        {
            if (children.Count < ParallelThreshold)
            {
                foreach (var child in children)
                {
                    ComputeSingleNode(child, engine, result, log, deadline, docRoot);
                    ProcessSubtreeSerial(child, engine, result, log, deadline, docRoot);
                }
                return;
            }

            Parallel.ForEach(
                children,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
                subtreeRoot =>
                {
                    ComputeSingleNode(subtreeRoot, engine, result, log, deadline, docRoot);
                    ProcessSubtreeSerial(subtreeRoot, engine, result, log, deadline, docRoot);
                });
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

        // Light children are pushed first so the whole shadow tree pops (and is
        // styled) before them: a slotted child inherits from its slot's style.
        private static void PushChildElements(Element parent, Stack<Element> stack)
        {
            var children = parent.ChildNodes;
            for (int i = children.Length - 1; i >= 0; i--)
            {
                if (children[i] is Element childEl)
                {
                    stack.Push(childEl);
                }
            }

            var shadowChildren = parent.GetAttachedShadowRoot()?.ChildNodes;
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
            else if (cascadeParent?.GetAttachedShadowRoot()?.GetAssignedSlotForRendering(n) is Element slot)
            {
                // CSS Scoping 1 §3.2: a slotted element inherits from its slot, its
                // parent in the flat tree (shadow trees are styled first, below).
                cascadeParent = slot;
            }

            // CSS Cascade 4 §7.2: inherited values come from the parent's computed
            // value. The root of an incremental recascade has a parent outside this
            // pass, whose style is the one the last cascade stored on it. Looking only
            // in this pass's results gave that root no parent at all: typing into
            // x.com's username field re-styles the <input> alone, and its inherited
            // white text reset to the initial black until a wider pass ran.
            if (cascadeParent != null && !result.TryGetValue(cascadeParent, out parentCss))
            {
                parentCss = cascadeParent.GetComputedStyle();
            }

            try
            {
                var mainProps = engine.ComputeCascadedValues(n, null);
                var css = CssLoader.ResolveStyle(n, parentCss, mainProps);

                // CSS Values 4 5.1.2: rem is the font size of the *document
                // element*. `root` here is only the root of this cascade, and an
                // incremental recascade is rooted at whichever subtree went dirty -
                // so any element at all could redefine the rem basis for the whole
                // page. On youtube.com the search field is `font-size: 1.6rem`, and
                // re-styling its subtree made 1.6rem mean 1.6x its own size: the
                // text grew by 1.6x on every click, without bound.
                if (css.FontSize.HasValue &&
                    css.FontSize.Value > 0 &&
                    double.IsFinite(css.FontSize.Value) &&
                    ReferenceEquals(n, n.OwnerDocument?.DocumentElement))
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
                n.SetComputedStyle(css);
            }
            catch (Exception)
            {
                var recovery = new CssComputed();
                result[n] = recovery;
                n.SetComputedStyle(recovery);
            }
        }

    }
}
