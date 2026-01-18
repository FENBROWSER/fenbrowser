using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.IO;
using System.Globalization;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Css; // Direct using, will resolve ambiguity manually or by deleting inner classes
using NewCss = FenBrowser.FenEngine.Rendering.Css;
// using FenBrowser.Core.Math; // Namespace moved to Core
namespace FenBrowser.FenEngine.Rendering
{
    public static partial class CssLoader
    {
        public class CssLoadResult
        {
            public Dictionary<Node, CssComputed> Computed { get; set; } = new Dictionary<Node, CssComputed>();
            public List<CssSource> Sources { get; set; } = new List<CssSource>();
        }

        // Debug file logging - ENABLE temporarily to diagnose CSS loading
        private const bool DEBUG_FILE_LOGGING = false;

        public class MatchedRule
        {
            public NewCss.CssRule Rule;
            public CssSource Source;
            public NewCss.Specificity Specificity;
        }

        // -------------------------------------------------------------------------
        // CSS PERFORMANCE CACHES
        // -------------------------------------------------------------------------
        private static readonly Dictionary<string, List<NewCss.CssRule>> _parsedRulesCache = new Dictionary<string, List<NewCss.CssRule>>();
        private static readonly Dictionary<(Element, SelectorChain), bool> _matchCache = new Dictionary<(Element, SelectorChain), bool>();
        private static readonly Dictionary<Element, List<NewCss.CssRule>> _elementMatchedRulesCache = new Dictionary<Element, List<NewCss.CssRule>>();
        private static readonly Dictionary<Element, CssComputed> _elementStyleCache = new Dictionary<Element, CssComputed>();
        
        // CSS Custom Properties (CSS Variables) storage - keyed by property name (e.g., "--primary-color")
        private static readonly Dictionary<string, string> _customProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static double _rootFontSize = 16.0;

        public static void ClearCaches()
        {
            lock (_parsedRulesCache) _parsedRulesCache.Clear();
            lock (_matchCache) _matchCache.Clear();
            lock (_elementMatchedRulesCache) _elementMatchedRulesCache.Clear();
            lock (_elementStyleCache) _elementStyleCache.Clear();
            lock (_customProperties) _customProperties.Clear();
            _rootFontSize = 16.0;
            _keyframes.Clear();
            FontRegistry.Clear();
        }
        // -------------------------------------------------------------------------

        public static List<MatchedRule> GetMatchedRules(Element element, List<CssSource> sources)
        {
             var matched = new List<MatchedRule>();
             if (element == null || sources == null) return matched;
             
             foreach(var source in sources)
             {
                 try
                 {
                     List<NewCss.CssRule> rules;
                     lock (_parsedRulesCache)
                     {
                         if (!_parsedRulesCache.TryGetValue(source.CssText, out rules))
                         {
                             rules = ParseRules(source.CssText, source.SourceOrder, source.BaseUri, null, null, MapToNewCssOrigin(source.Origin));
                             _parsedRulesCache[source.CssText] = rules;
                         }
                     }

                     foreach(var rule in rules)
                     {
                         if (rule is NewCss.CssStyleRule styleRule)
                         {
                             // Specificity calculation
                             var spec = SelectorMatcher.GetMatchingSpecificity(element, styleRule.Selector);
                             if (spec.HasValue)
                             {
                                 matched.Add(new MatchedRule { Rule = rule, Source = source, Specificity = spec.Value });
                             }
                         }
                         // TODO: Support media rules nesting for DevTools inspection
                     }
                 }
                 catch (Exception parseEx)
                 {
                     /* [PERF-REMOVED] */
                 }
             }
             
             // Sort by Origin -> Specificity -> Order
             matched.Sort((a,b) => {
                 var ruleA = a.Rule as NewCss.CssStyleRule;
                 var ruleB = b.Rule as NewCss.CssStyleRule;
                 if (ruleA == null) return -1; 
                 if (ruleB == null) return 1;

                 // 1. Origin (Ascending: UserAgent < User < Author)
                 int originComp = ruleA.Origin.CompareTo(ruleB.Origin);
                 if (originComp != 0) return originComp;

                 // 2. Specificity (Ascending: lower specificity first, higher later)
                 int specComp = a.Specificity.CompareTo(b.Specificity);
                 if (specComp != 0) return specComp;

                 // 3. Source Order (Ascending)
                 return ruleA.Order.CompareTo(ruleB.Order);
             });
             return matched;
        }

        // ===========================
        // Public API
        // ===========================

        /// <summary>
        /// Main entry point: fetches external styles, parses all CSS, and computes styles for the document.
        /// </summary>
        /// <param name="root">Document root (html or the parsed tree root).</param>
        /// <param name="baseUri">Base URI for resolving &lt;link&gt;, @import, url(...).</param>
        /// <param name="fetchExternalCssAsync">
        /// Delegate to fetch external CSS text for a given absolute URL (cookie-aware if needed).
        /// If null, external styles are skipped gracefully.
        /// </param>
        /// <param name="viewportWidth">Optional viewport width for simple media checks.</param>
        /// <param name="log">Optional logger for warnings/notes.</param>
        public static async Task<Dictionary<Node, CssComputed>> ComputeAsync(
            Element root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            double? viewportHeight = null,
            Action<string> log = null)
        {
            var result = await ComputeWithResultAsync(root, baseUri, fetchExternalCssAsync, viewportWidth, viewportHeight, log);
            return result.Computed;
        }

        public static async Task<CssLoadResult> ComputeWithResultAsync(
            Element root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            double? viewportHeight = null,
            Action<string> log = null)
        {
            // This MUST be done even if using cached rules.
            CssParser.MediaViewportWidth = viewportWidth;
            CssParser.MediaViewportHeight = viewportHeight;

            /* [PERF-REMOVED] */

            if (root == null)
                return new CssLoadResult();

            var cssBlobs = new List<CssSource>(); // collected CSS texts with source ordering
            int sourceIndex = 0;
            var _cssStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 0) UA stylesheet - load from external file (lowest precedence)
            try
            {
                string uaCss = null;
                
                try
                {
                    // PRIORITY 1: Development path (hardcoded)
                    var devPaths = new[] {
                        @"c:\Users\udayk\Videos\FENBROWSER\FenBrowser.FenEngine\Assets\ua.css",
                        Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\..\FenBrowser.FenEngine\Assets\ua.css"), 
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Credits\ua.css"), // unlikely
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"ua.css")
                    };
                    
                    bool loaded = false;
                    foreach (var path in devPaths) {
                        var p = Path.GetFullPath(path);
                        if (System.IO.File.Exists(p)) {
                             uaCss = System.IO.File.ReadAllText(p);
                             System.Diagnostics.Debug.WriteLine($"[CssLoader] Loaded UA stylesheet from: {p}");
                             Console.WriteLine($"[CssLoader] Loaded UA stylesheet from: {p}");
                             loaded = true;
                             break;
                        } else {
                             Console.WriteLine($"[CssLoader] UA File NOT FOUND at: {p}");
                        }
                    }
                    
                    if (!loaded)
                    {
                        // Fallback: try assembly path
                        var assemblyDir = Path.GetDirectoryName(typeof(CssLoader).Assembly.Location);
                        var uaCssPath = Path.Combine(assemblyDir, "Assets", "ua.css");
                        if (File.Exists(uaCssPath))
                        {
                            uaCss = File.ReadAllText(uaCssPath);
                            Console.WriteLine($"[CssLoader] Loaded UA stylesheet from assembly: {uaCssPath}");
                        }
                        else
                        {
                             Console.WriteLine($"[CssLoader] UA File NOT FOUND at assembly: {uaCssPath}");
                        }
                    }
                }
                catch (Exception readEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[CssLoader] Failed to read UA file: {readEx.Message}. Using fallback.");
                    Console.WriteLine($"[CssLoader] Failed to read UA file: {readEx.Message}");
                    /* [PERF-REMOVED] */
                }
                
                // Fallback: minimal inline defaults
                if (string.IsNullOrEmpty(uaCss))
                {
                    uaCss = @"
                        html,body{background:#fff;color:#000;margin:0;padding:0;}
                        head,style,script,title,meta,link{display:none;}
                        body{font:16px/1.5 'Segoe UI',Arial,sans-serif;margin:8px;}
                        h1{font-size:2em;font-weight:bold;margin:0.67em 0;}
                        h2{font-size:1.5em;font-weight:bold;margin:0.83em 0;}
                        h3{font-size:1.17em;font-weight:bold;margin:1em 0;}
                        p{margin:1em 0;}
                        div,article,section,header,footer,nav,main{display:block;}
                        strong,b{font-weight:bold;}
                        em,i{font-style:italic;}
                        a{color:#0000EE;text-decoration:underline;}
                    ";
                    /* [PERF-REMOVED] */
                }
                else
                {
                     /* [PERF-REMOVED] */
                }
                
                cssBlobs.Add(new CssSource { CssText = uaCss, Origin = CssOrigin.UserAgent, SourceOrder = sourceIndex++, BaseUri = baseUri });
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"[CssLoader] UA stylesheet error: {ex.Message}");
                /* [PERF-REMOVED] */
            }

            // 1) Inline <style> tags first (DOM order)
            const int MAX_INLINE_CSS_SIZE = 300_000; // 300KB per inline style
            foreach (var n in root.Descendants().OfType<Element>().Where(n => !n.IsText && string.Equals(n.Tag, "style", StringComparison.OrdinalIgnoreCase)))
            {
                var text = SafeGatherText(n);
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= MAX_INLINE_CSS_SIZE)
                {
                    // Debug logging disabled for performance
                    
                    cssBlobs.Add(new CssSource
                    {
                        CssText = text,
                        Origin = CssOrigin.Inline,
                        SourceOrder = sourceIndex++,
                        BaseUri = baseUri
                    });
                }
                else
                {
                    // System.Diagnostics.Debug.WriteLine("[CssLoader] Found empty style block");
                }
            }

            // 2) External <link rel="stylesheet"> (DOM order)
            // Fetch in parallel with a small concurrency limit to avoid blocking UI
            
            // DEBUG: Dump DOM structure to understand why LINK elements are not found
            if (DEBUG_FILE_LOGGING)
            {
                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[DOM-CHECK] Root tag='{root.Tag}' Children={root.Children?.Count ?? 0}\r\n");
                foreach (var child in root.Children ?? new List<Node>())
                {
                    DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[DOM-CHECK]   Child tag='{child.Tag}' (IsText={child.IsText}) Children={child.Children?.Count ?? 0}\r\n");
                    foreach (var child2 in child.Children ?? new List<Node>())
                    {
                        string tag2 = child2.Tag?.ToUpperInvariant() ?? "";
                        if (!child2.IsText && (tag2 == "HEAD" || tag2 == "LINK" || tag2 == "META"))
                        {
                            DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[DOM-CHECK]     Child2 tag='{child2.Tag}' Children={child2.Children?.Count ?? 0}\r\n");
                            // Dump HEAD children
                            if (tag2 == "HEAD")
                            {
                                foreach (var headChild in child2.Children ?? new List<Node>())
                                {
                                    DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[DOM-CHECK]       HEAD-Child tag='{headChild.Tag}'\r\n");
                                }
                            }
                        }
                    }
                }
            }
            
            // FIX: Cast to Element to access actual Attributes property (Node.Attr returns null)
            var linkNodes = root.Descendants().OfType<Element>()
                .Where(n => string.Equals(n.Tag, "link", StringComparison.OrdinalIgnoreCase)).ToList();
            if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[LINK] Found {linkNodes.Count} link elements in DOM root\r\n");


            var extTasks = new List<Task>();
            var gate = new System.Threading.SemaphoreSlim(8); // Shared gate for all CSS fetches (links + imports)
            foreach (var link in linkNodes)
            {
                if (link.Attr == null) { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", "[LINK] Link has no attributes\r\n"); continue; }
                string rel;
                if (!link.Attr.TryGetValue("rel", out rel)) 
                {
                     if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", "[LINK] Link has no rel attribute\r\n");
                     continue; 
                }
                /* [PERF-REMOVED] */
                if (!ContainsToken(rel, "stylesheet")) continue;
                string href; if (!link.Attr.TryGetValue("href", out href) || string.IsNullOrWhiteSpace(href)) continue;
                /* [PERF-REMOVED] */
                // Respect media attribute (screen/all)
                string media;
                if (link.Attr.TryGetValue("media", out media) && !string.IsNullOrWhiteSpace(media))
                {
                    var m = media.ToLowerInvariant();
                    if (!(m.Contains("all") || m.Contains("screen"))) continue;
                }
                var abs = ResolveUri(baseUri, href);
                if (abs == null || fetchExternalCssAsync == null) continue;

                await gate.WaitAsync();
                var order = sourceIndex++;
                var t = Task.Run(async () =>
                {
                    try
                    {
                        var css = await fetchExternalCssAsync(abs).ConfigureAwait(false);
                        /* [PERF-REMOVED] */
                        // Limit CSS size to prevent crashes on massive stylesheets (GitHub, etc.)
                        const int MAX_CSS_SIZE = 500_000; // 500KB per stylesheet
                        if (!string.IsNullOrWhiteSpace(css) && css.Length <= MAX_CSS_SIZE)
                        {
                            lock (cssBlobs)
                            {
                                cssBlobs.Add(new CssSource
                                {
                                    CssText = css,
                                    Origin = CssOrigin.External,
                                    SourceOrder = order,
                                    BaseUri = abs
                                });
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(css))
                        {
                            Log(log, $"[CssLoader] Skipped large CSS ({css.Length} bytes) from: {abs}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(log, "[CssLoader] Failed to fetch CSS: " + abs + " :: " + ex.Message);
                    }
                    finally { try { gate.Release(); } catch { /* Ignore release errors */ } }
                });
                extTasks.Add(t);
            }
            if (extTasks.Count > 0) { try { await Task.WhenAll(extTasks); } catch { /* Ignore fetch errors */ } }
            FenLogger.Debug($"[PERF-CSS] External CSS Fetch: {_cssStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

            // 3) Expand @import (depth-bounded)
            var expanded = await ExpandImportsAsync(cssBlobs, fetchExternalCssAsync, viewportWidth, log, gate);
            FenLogger.Debug($"[PERF-CSS] @import Expansion: {_cssStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);

            // 4) Parse rules from all sources (parallel, bounded)
            var allRules = new List<NewCss.CssRule>();
            var parseGate = new System.Threading.SemaphoreSlim(4);
            var parseTasks = new List<Task>();
            foreach (var blob in expanded)
            {
                await parseGate.WaitAsync();
                parseTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        List<NewCss.CssRule> parsed;
                        lock (_parsedRulesCache)
                        {
                            // Pre-process for @font-face
                            string processedCss = ExtractFontFace(blob.CssText, blob.BaseUri, log);

                            if (!_parsedRulesCache.TryGetValue(processedCss, out parsed))
                            {
                                parsed = ParseRules(processedCss, blob.SourceOrder, blob.BaseUri, viewportWidth, log, MapToNewCssOrigin(blob.Origin));
                                _parsedRulesCache[processedCss] = parsed;
                            }
                        }
                        lock (allRules) allRules.AddRange(parsed);
                    }
                    catch (Exception ex)
                    {
                        Log(log, "[CssLoader] Parse error: " + ex.Message);
                        /* [PERF-REMOVED] */
                    }
                    finally { try { parseGate.Release(); } catch { /* Ignore release errors */ } }
                }));
            }
            if (parseTasks.Count > 0) { try { await Task.WhenAll(parseTasks); } catch { /* Ignore parse errors */ } }
            FenLogger.Debug($"[PERF-CSS] Rule Parsing: {_cssStopwatch.ElapsedMilliseconds}ms (Rules: {allRules.Count})", LogCategory.Rendering);

            // Debug: Log parsed rules count
            /* [PERF-REMOVED] */

            // 4.5) Resolve CSS variables
            // 4.5) Resolve CSS variables & 5) Compute per-element cascaded styles
            // CRITICAL FIX: Offload heavy CSS matching/cascading to background thread to avoid freezing UI
            ResolveVariables(allRules);
            FenLogger.Debug($"[PERF-CSS] Variable Resolution: {_cssStopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
            var computed = CascadeIntoComputedStyles(root, allRules, log);
            FenLogger.Debug($"[PERF-CSS] Cascade Matching: {_cssStopwatch.ElapsedMilliseconds}ms (Elements: {computed.Count})", LogCategory.Rendering);
            
            return new CssLoadResult
            {
                Computed = computed,
                Sources = cssBlobs
            };
        }

        /// <summary>
        /// Overload without viewport/log parameters.
        /// </summary>
        public static Task<Dictionary<Node, CssComputed>> ComputeAsync(
            Element root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync)
        {
            return ComputeAsync(root, baseUri, fetchExternalCssAsync, null, null, null);
        }

        // ===========================
        // CSS Custom Properties (Variables)
        // ===========================
        
        /// <summary>
        /// Extract CSS custom properties (--name: value) from :root and html rules
        /// and store them in the global _customProperties dictionary for var() resolution.
        /// </summary>
        private static void ResolveVariables(List<NewCss.CssRule> rules)
        {
            if (rules == null) return;
            
            lock (_customProperties)
            {
                _customProperties.Clear();
                _rootFontSize = 16.0;
                int count = 0;
                
                foreach (var rule in rules)
                {
                    if (!(rule is NewCss.CssStyleRule styleRule)) continue;

                    bool isRootRule = false;
                    foreach (var chain in styleRule.Selector.Chains)
                    {
                        if (chain.Segments.Count > 0)
                        {
                            var lastSeg = chain.Segments[chain.Segments.Count - 1];
                            string tag = lastSeg.Tag?.ToLowerInvariant() ?? "";
                            if (tag == ":root" || tag == "html" || tag == "body" || tag == "*" || lastSeg.PseudoClasses?.Any(pc => pc.name == "root" || pc.name == ":root") == true)
                            {
                                isRootRule = true;
                                break;
                            }
                            // Check pseudo-classes if tag is empty/implied
                            if (string.IsNullOrEmpty(tag) && lastSeg.PseudoClasses != null)
                            {
                                foreach (var pc in lastSeg.PseudoClasses)
                                {
                                    if (pc.name.ToLowerInvariant().Contains("root"))
                                    {
                                        isRootRule = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    
                    foreach (var decl in styleRule.Declarations)
                    {
                        string propName = decl.Property;
                        if (string.IsNullOrEmpty(propName)) continue;

                        if (propName.StartsWith("--"))
                        {
                            string value = decl.Value?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(value))
                            {
                                if (isRootRule || !_customProperties.ContainsKey(propName))
                                {
                                    _customProperties[propName] = value;
                                    count++;
                                }
                            }
                        }
                        
                        if (isRootRule && propName.Equals("font-size", StringComparison.OrdinalIgnoreCase))
                        {
                            double fs;
                            if (TryPx(decl.Value, out fs, percentBase: 16.0))
                            {
                                _rootFontSize = fs;
                                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR] Captured Root Font Size: {_rootFontSize}px from '{decl.Value}'\r\n");
                            }
                        }
                    }
                }
                
                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR] Final Root Font Size: {_rootFontSize}px\r\n");
                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR] Extracted {count} global variables.\r\n");
            }
        }

        // ===========================
        // Model & helpers
        // ===========================

        public enum CssOrigin
        {
            Inline, External, Imported,
            UserAgent
        }
        
        /// <summary>
        /// Maps CssLoader.CssOrigin to NewCss.CssOrigin correctly.
        /// Inline, External, Imported are all Author-level styles.
        /// Only UserAgent maps to UserAgent.
        /// </summary>
        private static NewCss.CssOrigin MapToNewCssOrigin(CssOrigin origin)
        {
            return origin == CssOrigin.UserAgent 
                ? NewCss.CssOrigin.UserAgent 
                : NewCss.CssOrigin.Author;
        }

        // ===========================
        // CSS Animations (@keyframes)
        // ===========================
        
        /// <summary>
        /// Storage for parsed @keyframes animations (keyed by animation name)
        /// </summary>
        private static readonly Dictionary<string, CssKeyframes> _keyframes = new Dictionary<string, CssKeyframes>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Represents a single @keyframes animation
        /// </summary>
        public class CssKeyframes
        {
            public string Name { get; set; }
            public List<CssKeyframe> Frames { get; set; } = new List<CssKeyframe>();
        }
        
        /// <summary>
        /// Represents a single keyframe in an animation (e.g., "0%", "50%", "100%", "from", "to")
        /// </summary>
        public class CssKeyframe
        {
            public double Percentage { get; set; } // 0-100
            public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Get a keyframes animation by name
        /// </summary>
        public static CssKeyframes GetKeyframes(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _keyframes.TryGetValue(name, out var kf);
            return kf;
        }
        public class CssSource
        {
            public string CssText;
            public CssOrigin Origin;
            public int SourceOrder;
            public Uri BaseUri;
        }

        public class CssRule
        {
            public List<SelectorChain> Selectors = new List<SelectorChain>(); // comma-separated selectors
            public Dictionary<string, CssDecl> Declarations = new Dictionary<string, CssDecl>(StringComparer.OrdinalIgnoreCase);
            public int SourceOrder;    // to break ties
            public Uri BaseUri;        // for url() resolving
        }

        public class CssDecl
        {
            public string Name;       // canonicalized (lowercase)
            public string Value;      // raw value
            public bool Important;    // !important
            public int Specificity;   // computed from selector where used
        }



        // ===========================
        // Stage 1: Import expansion
        // ===========================

        private static async Task<List<CssSource>> ExpandImportsAsync(
            List<CssSource> sources,
            Func<Uri, Task<string>> fetchExternal,
            double? viewportWidth,
            Action<string> log,
            System.Threading.SemaphoreSlim gate)
        {
            var output = new List<CssSource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guard = new RecursionGuard();

            foreach (var s in sources)
            {
                await ExpandOneAsync(s, fetchExternal, seen, output, viewportWidth, log, guard, gate);

            }
            return output;
        }

        // Keeps track of @import recursion without needing ref/out on async methods
        private sealed class RecursionGuard
        {
            public int Count;
        }

        private static async Task ExpandOneAsync(
            CssSource source,
            Func<Uri, Task<string>> fetchExternal,
            HashSet<string> seenUrls,
            List<CssSource> output,
            double? viewportWidth,
            Action<string> log,
            RecursionGuard guard,
            System.Threading.SemaphoreSlim gate)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.CssText))
                return;

            // Hard cap to avoid runaway recursion
            if ((guard != null ? guard.Count : 0) > 2048)
            {
                Log(log, "[CssLoader] Import expansion limit reached.");
                return;
            }

            string text = source.CssText;
            text = StripComments(text);

            var imports = new List<string>();
            var sb = new StringBuilder();

            int idx = 0;
            while (idx < text.Length)
            {
                if (StartsWithAt(text, idx, "@import"))
                {
                    int semi = text.IndexOf(';', idx);
                    if (semi < 0) { break; }
                    var importLine = text.Substring(idx, semi - idx + 1);
                    idx = semi + 1;

                    var url = ExtractImportUrl(importLine);
                    if (!string.IsNullOrWhiteSpace(url))
                        imports.Add(url);
                }
                else
                {
                    sb.Append(text[idx]);
                    idx++;
                }
            }

            // Resolve + fetch imports (parallel, bounded)
            // var gate = new System.Threading.SemaphoreSlim(4); // REMOVED: use shared gate
            var tasks = new List<System.Threading.Tasks.Task>();
            foreach (var imp in imports)
            {
                var abs = ResolveUri(source.BaseUri, imp);
                if (abs == null) continue;
                var key = abs.AbsoluteUri;
                if (seenUrls.Contains(key)) continue; // cycle
                seenUrls.Add(key);
                if (fetchExternal == null) continue;

                await gate.WaitAsync().ConfigureAwait(false);
                tasks.Add(System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var css = await fetchExternal(abs).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(css))
                        {
                            if (guard != null) guard.Count++;
                            await ExpandOneAsync(
                                new CssSource
                                {
                                    CssText = css,
                                    Origin = CssOrigin.Imported,
                                    SourceOrder = source.SourceOrder,
                                    BaseUri = abs
                                },
                                fetchExternal,
                                seenUrls,
                                output,
                                viewportWidth,
                                log,
                                guard,
                                gate).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) { Log(log, "[CssLoader] @import fetch failed: " + abs + " :: " + ex.Message); }
                    finally { try { gate.Release(); } catch { /* Ignore release errors */ } }
                }));
            }
            if (tasks.Count > 0) { try { await System.Threading.Tasks.Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* Ignore task errors */ } }

            // Keep the remainder (with @imports stripped)
            output.Add(new CssSource
            {
                CssText = sb.ToString(),
                Origin = source.Origin,
                SourceOrder = source.SourceOrder,
                BaseUri = source.BaseUri
            });
        }

        private static bool StartsWithAt(string s, int idx, string token)
        {
            if (idx + token.Length > s.Length) return false;
            return string.Compare(s, idx, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>
        /// Extract and parse @keyframes rules from CSS text, storing them for animation use
        /// </summary>
        private static string ExtractKeyframes(string text, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            var result = new StringBuilder();
            int i = 0;
            
            int loopCount = 0;
            while (i < text.Length)
            {
                if (loopCount++ > 100000)
                {
                    log?.Invoke("[CSS] Aborting ExtractKeyframes loop: Iteration limit reached");
                    result.Append(text.Substring(i));
                    break;
                }

                // Look for @keyframes or @-webkit-keyframes
                int kfPos = -1;
                int prefixLen = 0;
                
                int pos1 = text.IndexOf("@keyframes", i, StringComparison.OrdinalIgnoreCase);
                int pos2 = text.IndexOf("@-webkit-keyframes", i, StringComparison.OrdinalIgnoreCase);
                
                if (pos1 >= 0 && (pos2 < 0 || pos1 < pos2))
                {
                    kfPos = pos1;
                    prefixLen = "@keyframes".Length;
                }
                else if (pos2 >= 0)
                {
                    kfPos = pos2;
                    prefixLen = "@-webkit-keyframes".Length;
                }
                
                if (kfPos < 0)
                {
                    // No more keyframes, append rest
                    result.Append(text.Substring(i));
                    break;
                }
                
                // Append text before @keyframes
                result.Append(text.Substring(i, kfPos - i));
                
                // Find the animation name (after @keyframes and before {)
                int nameStart = kfPos + prefixLen;
                int braceOpen = text.IndexOf('{', nameStart);
                if (braceOpen < 0)
                {
                    // Safe recovery: advance past this @keyframes token
                    i = nameStart + 1;
                    continue;
                }
                
                string animName = text.Substring(nameStart, braceOpen - nameStart).Trim();
                
                // Find matching closing brace for the outer @keyframes block
                int braceClose = FindMatchingBrace(text, braceOpen);
                if (braceClose < 0)
                {
                    // Safe recovery: advance past the open brace
                    i = braceOpen + 1;
                    continue;
                }
                
                // Extract keyframes content
                string keyframesBody = text.Substring(braceOpen + 1, braceClose - braceOpen - 1);
                
                // Parse keyframe stops
                var keyframes = new CssKeyframes { Name = animName };
                ParseKeyframeStops(keyframesBody, keyframes);
                
                // Store in dictionary
                _keyframes[animName] = keyframes;
                log?.Invoke($"[CSS] Parsed @keyframes: {animName} with {keyframes.Frames.Count} stops");
                
                i = braceClose + 1;
            }
            
            return result.ToString();
        }
        
        /// <summary>
        /// Parse individual keyframe stops from the body of @keyframes
        /// </summary>
        private static void ParseKeyframeStops(string body, CssKeyframes keyframes)
        {
            int i = 0;
            int loopCount = 0;
            while (i < body.Length)
            {
                if (loopCount++ > 100000) { break; }
                // Find next {
                int open = body.IndexOf('{', i);
                if (open < 0) break;
                
                string percentageText = body.Substring(i, open - i).Trim();
                
                int close = FindMatchingBrace(body, open);
                if (close < 0) break;
                
                string propsText = body.Substring(open + 1, close - open - 1);
                i = close + 1;
                
                // Parse percentages (can be comma-separated like "0%, 100%")
                var percentages = new List<double>();
                foreach (var pct in percentageText.Split(','))
                {
                    string p = pct.Trim().ToLowerInvariant();
                    if (p == "from") percentages.Add(0);
                    else if (p == "to") percentages.Add(100);
                    else if (p.EndsWith("%"))
                    {
                        if (double.TryParse(p.TrimEnd('%'), out double val))
                            percentages.Add(val);
                    }
                }
                
                // Parse properties
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var decl in propsText.Split(';'))
                {
                    var colonIdx = decl.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var propName = decl.Substring(0, colonIdx).Trim().ToLowerInvariant();
                        var propValue = decl.Substring(colonIdx + 1).Trim();
                        if (!string.IsNullOrEmpty(propName))
                            props[propName] = propValue;
                    }
                }
                
                // Create keyframe for each percentage
                foreach (var pct in percentages)
                {
                    keyframes.Frames.Add(new CssKeyframe
                    {
                        Percentage = pct,
                        Properties = new Dictionary<string, string>(props, StringComparer.OrdinalIgnoreCase)
                    });
                }
            }
            
            // Sort by percentage
            keyframes.Frames.Sort((a, b) => a.Percentage.CompareTo(b.Percentage));
        }

        private static string ExtractImportUrl(string importLine)
        {
            // Handles: @import "x.css";  @import url('x.css');
            var m = Regex.Match(importLine, @"@import\s+(url\((['""]?)(?<u>[^)'""]+)\2\)|(['""])(?<u2>[^'""]+)\4)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var u = !string.IsNullOrEmpty(m.Groups["u"].Value) ? m.Groups["u"].Value : m.Groups["u2"].Value;
                return u.Trim();
            }
            return null;
        }

        /// <summary>
        /// Extract and register @font-face rules from CSS text
        /// </summary>
        private static string ExtractFontFace(string text, Uri baseUri, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("@font-face", StringComparison.OrdinalIgnoreCase) < 0) return text;

            var result = new StringBuilder();
            int i = 0;

            int loopCount = 0;
            while (i < text.Length)
            {
                if (loopCount++ > 100000) break;
                int ffPos = text.IndexOf("@font-face", i, StringComparison.OrdinalIgnoreCase);
                if (ffPos < 0)
                {
                    result.Append(text.Substring(i));
                    break;
                }

                // Append text before @font-face
                result.Append(text.Substring(i, ffPos - i));

                // Find the opening brace
                int braceOpen = text.IndexOf('{', ffPos);
                if (braceOpen < 0)
                {
                    i = ffPos + 10;
                    continue;
                }

                // Find matching closing brace
                int braceClose = FindMatchingBrace(text, braceOpen);
                if (braceClose < 0)
                {
                    i = braceOpen + 1;
                    continue;
                }

                // Extract and parse the @font-face block
                string fontFaceBody = text.Substring(braceOpen + 1, braceClose - braceOpen - 1);
                try
                {
                    FontRegistry.ParseAndRegister(fontFaceBody, baseUri);
                    log?.Invoke($"[CSS] Registered @font-face");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[CSS] Error parsing @font-face: {ex.Message}");
                }

                i = braceClose + 1;
            }

            return result.ToString();
        }

        /// <summary>
        /// Handle @supports feature queries - include content if feature is supported
        /// </summary>
        private static string FlattenSupports(string text, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("@supports", StringComparison.OrdinalIgnoreCase) < 0) return text;

            var result = new StringBuilder();
            int i = 0;

            int loopCount = 0;
            while (i < text.Length)
            {
                if (loopCount++ > 100000) break;
                int supPos = text.IndexOf("@supports", i, StringComparison.OrdinalIgnoreCase);
                if (supPos < 0)
                {
                    result.Append(text.Substring(i));
                    break;
                }

                result.Append(text.Substring(i, supPos - i));

                int braceOpen = text.IndexOf('{', supPos);
                if (braceOpen < 0) { i = supPos + 9; continue; }

                int braceClose = FindMatchingBrace(text, braceOpen);
                if (braceClose < 0) { i = braceOpen + 1; continue; }

                // Parse the condition (between @supports and {)
                string condition = text.Substring(supPos + 9, braceOpen - supPos - 9).Trim();
                string body = text.Substring(braceOpen + 1, braceClose - braceOpen - 1);

                // Check if condition is supported
                if (IsSupportsConditionMet(condition))
                {
                    result.Append(body);
                    log?.Invoke($"[CSS] @supports condition met: {condition}");
                }
                else
                {
                    log?.Invoke($"[CSS] @supports condition NOT met: {condition}");
                }

                i = braceClose + 1;
            }

            return result.ToString();
        }

        /// <summary>
        /// Check if a @supports condition is met by this browser
        /// </summary>
        private static bool IsSupportsConditionMet(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition)) return false;
            condition = condition.Trim().ToLowerInvariant();

            // Handle not()
            if (condition.StartsWith("not"))
            {
                var inner = ExtractPseudoArg(condition.Substring(3));
                return !IsSupportsConditionMet(inner);
            }

            // Handle or/and
            if (condition.Contains(" or "))
            {
                var parts = condition.Split(new[] { " or " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                    if (IsSupportsConditionMet(part.Trim())) return true;
                return false;
            }

            if (condition.Contains(" and "))
            {
                var parts = condition.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                    if (!IsSupportsConditionMet(part.Trim())) return false;
                return true;
            }

            // Check for property:value pair in parentheses
            var match = Regex.Match(condition, @"\(\s*([a-z-]+)\s*:\s*([^)]+)\s*\)");
            if (match.Success)
            {
                var prop = match.Groups[1].Value.Trim();
                // We support most standard CSS properties
                return IsSupportedProperty(prop);
            }

            return false;
        }
        
        /// <summary>
        /// Handle @container queries - conditional styles based on container size
        /// For now, we use viewport width as the container size (simplified implementation)
        /// </summary>
        private static string FlattenContainerQueries(string text, float viewportWidth, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("@container", StringComparison.OrdinalIgnoreCase) < 0) return text;

            var result = new StringBuilder();
            int i = 0;

            int loopCount = 0;
            while (i < text.Length)
            {
                if (loopCount++ > 100000) break;
                int contPos = text.IndexOf("@container", i, StringComparison.OrdinalIgnoreCase);
                if (contPos < 0)
                {
                    result.Append(text.Substring(i));
                    break;
                }

                result.Append(text.Substring(i, contPos - i));

                int braceOpen = text.IndexOf('{', contPos);
                if (braceOpen < 0) { i = contPos + 10; continue; }

                int braceClose = FindMatchingBrace(text, braceOpen);
                if (braceClose < 0) { i = braceOpen + 1; continue; }

                // Parse the condition (between @container and {)
                string condition = text.Substring(contPos + 10, braceOpen - contPos - 10).Trim();
                string body = text.Substring(braceOpen + 1, braceClose - braceOpen - 1);

                // Check if condition is met using viewport as container size
                if (IsContainerConditionMet(condition, viewportWidth))
                {
                    result.Append(body);
                    log?.Invoke($"[CSS] @container condition met: {condition}");
                }
                else
                {
                    log?.Invoke($"[CSS] @container condition NOT met: {condition}");
                }

                i = braceClose + 1;
            }

            return result.ToString();
        }
        
        /// <summary>
        /// Evaluate a @container condition against container (viewport) width
        /// </summary>
        private static bool IsContainerConditionMet(string condition, float containerWidth)
        {
            if (string.IsNullOrWhiteSpace(condition)) return true;
            condition = condition.Trim().ToLowerInvariant();
            
            // Skip container name if present (e.g., "container-name (min-width: 400px)")
            int parenIndex = condition.IndexOf('(');
            if (parenIndex > 0 && !condition.StartsWith("("))
            {
                condition = condition.Substring(parenIndex);
            }
            
            // Handle min-width: 400px
            var minMatch = Regex.Match(condition, @"\(\s*min-width\s*:\s*([\d.]+)(px|em|rem|%)?\s*\)");
            if (minMatch.Success)
            {
                float minWidth = float.Parse(minMatch.Groups[1].Value);
                string unit = minMatch.Groups[2].Value;
                if (unit == "em" || unit == "rem") minWidth *= 16; // Approximate
                return containerWidth >= minWidth;
            }
            
            // Handle max-width: 400px
            var maxMatch = Regex.Match(condition, @"\(\s*max-width\s*:\s*([\d.]+)(px|em|rem|%)?\s*\)");
            if (maxMatch.Success)
            {
                float maxWidth = float.Parse(maxMatch.Groups[1].Value);
                string unit = maxMatch.Groups[2].Value;
                if (unit == "em" || unit == "rem") maxWidth *= 16;
                return containerWidth <= maxWidth;
            }
            
            // Handle width: 400px (exact)
            var widthMatch = Regex.Match(condition, @"\(\s*width\s*:\s*([\d.]+)(px|em|rem|%)?\s*\)");
            if (widthMatch.Success)
            {
                float width = float.Parse(widthMatch.Groups[1].Value);
                string unit = widthMatch.Groups[2].Value;
                if (unit == "em" || unit == "rem") width *= 16;
                return Math.Abs(containerWidth - width) < 1;
            }
            
            // Default: include content if no recognizable condition
            return true;
        }

        /// <summary>
        /// Check if a CSS property is supported and log unsupported ones
        /// </summary>
        private static bool IsSupportedProperty(string property, string value = null)
        {
            // List of supported CSS properties
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "display", "position", "width", "height", "margin", "padding", "border",
                "background", "background-color", "background-image", "color", "font",
                "font-family", "font-size", "font-weight", "font-style", "text-align",
                "text-decoration", "flex", "flex-direction", "flex-wrap", "justify-content",
                "align-items", "grid", "grid-template-columns", "gap", "transform",
                "opacity", "overflow", "z-index", "box-shadow", "border-radius",
                "transition", "filter", "animation", "min-width", "max-width",
                "min-height", "max-height", "aspect-ratio", "object-fit", "cursor", "box-sizing",
                // Shorthand properties
                "margin-top", "margin-right", "margin-bottom", "margin-left",
                "padding-top", "padding-right", "padding-bottom", "padding-left",
                "border-top", "border-right", "border-bottom", "border-left",
                "border-width", "border-style", "border-color",
                "top", "right", "bottom", "left",
                "flex-grow", "flex-shrink", "flex-basis", "align-self",
                "order", "grid-template-rows", "grid-column", "grid-row",
                "line-height", "letter-spacing", "word-spacing", "text-transform",
                "white-space", "overflow-x", "overflow-y", "visibility",
                "vertical-align", "float", "clear",
                "content", "counter-reset", "counter-increment", "counter-set",
                "filter", "backdrop-filter", "clip-path",
                // Grid Layout (from gap report)
                "grid-template-areas", "grid-area", "grid-auto-flow", 
                "grid-auto-columns", "grid-auto-rows", "place-items", "place-content",
                "row-gap", "column-gap",
                // Flexbox (from gap report)
                "align-content",
                // Visual Effects (from gap report)
                "mix-blend-mode", "isolation", "mask", "mask-image",
                // Typography (from gap report)
                "font-variant", "font-stretch", "text-orientation", "writing-mode",
                "hyphens", "word-break", "text-indent", "text-overflow",
                // Bidirectional text (Phase 8)
                "direction", "unicode-bidi",
                // Logical Properties (from gap report)
                "margin-block", "margin-block-start", "margin-block-end",
                "margin-inline", "margin-inline-start", "margin-inline-end",
                "padding-block", "padding-block-start", "padding-block-end",
                "padding-inline", "padding-inline-start", "padding-inline-end",
                "inset", "inset-block", "inset-inline", "block-size", "inline-size",
                // Interactivity (from gap report)
                "pointer-events", "touch-action", "user-select", "resize",
                // Scroll Control (from gap report)
                "overscroll-behavior", "scroll-behavior", "scroll-margin", "scroll-padding",
                // Animation (from gap report)
                "animation-name", "animation-duration", "animation-timing-function",
                "animation-delay", "animation-iteration-count", "animation-direction",
                "animation-fill-mode", "animation-play-state",
                // Background enhancements
                "background-position", "background-size", "background-repeat",
                "background-attachment", "background-origin", "background-clip",
                // Border enhancements
                "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
                "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
                "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
                "border-top-left-radius", "border-top-right-radius",
                "border-bottom-left-radius", "border-bottom-right-radius",
                // Outline
                "outline", "outline-width", "outline-style", "outline-color", "outline-offset",
                // List styles
                "list-style", "list-style-type", "list-style-position", "list-style-image",
                // Table
                "border-collapse", "border-spacing", "table-layout", "caption-side", "empty-cells",
                // Modern CSS properties (new)
                "accent-color", "caret-color", "color-scheme", "contain", "appearance",
                "image-rendering", "rendering-intent", "image-orientation",
                // 3D Transforms
                "transform-origin", "transform-style", "backface-visibility", "perspective", "perspective-origin",
                // Multi-column layout
                "columns", "column-count", "column-width", "column-gap", "column-rule",
                "column-rule-width", "column-rule-style", "column-rule-color", "column-span",
                // Text decoration
                "text-decoration-line", "text-decoration-style", "text-decoration-color", "text-decoration-thickness",
                "text-underline-offset", "text-emphasis", "text-emphasis-style", "text-emphasis-color",
                // Tab and line
                "tab-size", "text-rendering",
                // Transitions
                "transition-property", "transition-duration", "transition-timing-function", "transition-delay",
                // Will-change and containment
                "will-change", "contain-intrinsic-size",
                // Print/page
                "page-break-before", "page-break-after", "page-break-inside", "orphans", "widows",
                // Object fit
                "object-position",
                // Additional flex/grid
                "place-self", "justify-items", "justify-self",
                // Shapes
                "shape-outside", "shape-margin", "shape-image-threshold"
            };
            
            bool isSupported = supported.Contains(property);
            
            // Log unsupported properties for debugging (tracks first encounter only)
            if (!isSupported && !string.IsNullOrEmpty(property))
            {
                // Skip vendor prefixes and CSS variables - they're expected to be unsupported
                if (!property.StartsWith("-") && !property.StartsWith("--"))
                {
                    if (FenBrowser.Core.Logging.DebugConfig.LogCssParse)
                        global::FenBrowser.Core.FenLogger.Log($"[CSS] Ignored Property: '{property}' (Value: '{value ?? ""}')", LogCategory.CssParsing);
                        
                    EngineCapabilities.LogUnsupportedCss(property, value, "CSS property not implemented");
                }
            }
            
            return isSupported;
        }

        /// <summary>
        /// Public method to check if an element matches a CSS selector string.
        /// Used by DOM API methods like matches() and closest().
        /// </summary>
        public static bool MatchesSelector(Element element, string selectorString)
        {
            if (element == null || string.IsNullOrWhiteSpace(selectorString))
                return false;
            
            try
            {
                // Parse the selector string into a SelectorChain
                var chain = ParseSelectorChain(selectorString.Trim());
                if (chain == null || chain.Segments == null || chain.Segments.Count == 0)
                    return false;
                
                // Check if the element matches
                return Matches(element, chain);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Handle @layer cascade layers - flatten for now with layer tracking
        /// </summary>
        private static string ExtractLayers(string text, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("@layer", StringComparison.OrdinalIgnoreCase) < 0) return text;

            var result = new StringBuilder();
            int i = 0;

            int loopCount = 0;
            while (i < text.Length)
            {
                if (loopCount++ > 100000) break;
                int layerPos = text.IndexOf("@layer", i, StringComparison.OrdinalIgnoreCase);
                if (layerPos < 0)
                {
                    result.Append(text.Substring(i));
                    break;
                }

                result.Append(text.Substring(i, layerPos - i));

                // Check if it's a layer declaration (e.g., @layer theme, base;) or layer block
                int braceOpen = text.IndexOf('{', layerPos);
                int semicolon = text.IndexOf(';', layerPos);

                if (semicolon >= 0 && (braceOpen < 0 || semicolon < braceOpen))
                {
                    // Layer declaration only (no block), skip it
                    i = semicolon + 1;
                    continue;
                }

                if (braceOpen < 0) { i = layerPos + 6; continue; }

                int braceClose = FindMatchingBrace(text, braceOpen);
                if (braceClose < 0) { i = braceOpen + 1; continue; }

                // Extract layer name (if any)
                string header = text.Substring(layerPos + 6, braceOpen - layerPos - 6).Trim();
                string body = text.Substring(braceOpen + 1, braceClose - braceOpen - 1);

                // Include the layer content (flatten it)
                result.Append(body);
                log?.Invoke($"[CSS] Flattened @layer: {(string.IsNullOrEmpty(header) ? "(anonymous)" : header)}");

                i = braceClose + 1;
            }

            return result.ToString();
        }

        // ===========================
        // Stage 2: Parsing rules
        // ===========================

        private static List<NewCss.CssRule> ParseRules(string css, int sourceOrder, Uri baseForUrls, double? viewportWidth, Action<string> log, NewCss.CssOrigin origin = NewCss.CssOrigin.Author)
        {
            var rules = new List<NewCss.CssRule>();
            if (string.IsNullOrWhiteSpace(css)) return rules;

            try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_raw_css.txt", "\n--- RAW CSS BLOCK ---\n" + css + "\n-------------------\n"); } catch {}
            var text = StripComments(css);
            
             try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_full_css.txt", "\n--- NEW CSS BLOCK ---\n" + text + "\n-------------------\n"); } catch {}

            // FlattenBasicMedia REMOVED: Now handled by proper parsing in CssSyntaxParser + Recursive processing below
            // text = FlattenBasicMedia(text, viewportWidth, log);
            
            // Extract non-standard/unimplemented blocks to avoid parser errors
            text = ExtractKeyframes(text, log);
            text = ExtractFontFace(text, baseForUrls, log);
            text = FlattenSupports(text, log);
            text = ExtractLayers(text, log);
            text = FlattenContainerQueries(text, (float)(viewportWidth ?? 1024), log);

             try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_full_css.txt", "\n--- PROCESSED CSS BLOCK ---\n" + text + "\n-------------------\n"); } catch {}

            // New Pipeline: Tokenize -> Parse
            var tokenizer = new CssTokenizer(text);
            var parser = new CssSyntaxParser(tokenizer);
            var sheet = parser.ParseStylesheet();

            int ruleIndexInsideSheet = 0;
            
            // Recursive helper to flatten media rules into the main list
            void ProcessRuleList(IEnumerable<NewCss.CssRule> inputRules)
            {
                foreach (var rule in inputRules)
                {
                    if (rule is NewCss.CssMediaRule mediaRule)
                    {
                        // Evaluate condition
                        bool matches = EvaluateMediaQuery(mediaRule.Condition, viewportWidth);
                        if (matches)
                        {
                            // Flatten inner rules into the main stream
                            ProcessRuleList(mediaRule.Rules);
                        }
                    }
                    else
                    {
                        rule.BaseUri = baseForUrls;
                        rule.Origin = origin; 
                        if (rule is NewCss.CssStyleRule styleRule)
                        {
                            styleRule.Order = (sourceOrder * 10000) + ruleIndexInsideSheet++;
                        }
                        rules.Add(rule);
                    }
                }
            }
            
            ProcessRuleList(sheet.Rules);
            
            try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CssLoader] Parsed {rules.Count} rules from block of length {text.Length}\r\n"); } catch {}

            return rules;
        }

        private static string FlattenBasicMedia(string text, double? viewportWidth, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.IndexOf("@media", StringComparison.OrdinalIgnoreCase) < 0) return text;

            var sb = new StringBuilder();
            int i = 0;
            int loopCount = 0;
            while (i < text.Length)
            {
                if (loopCount++ > 100000) break;
                int mediaPos = text.IndexOf("@media", i, StringComparison.OrdinalIgnoreCase);
                if (mediaPos < 0)
                {
                    sb.Append(text.Substring(i));
                    break;
                }

                sb.Append(text.Substring(i, mediaPos - i));

                int open = text.IndexOf('{', mediaPos);
                if (open < 0) { i = mediaPos + 6; continue; }
                
                int close = FindMatchingBrace(text, open);
                if (close < 0) { i = open + 1; continue; }

                var header = text.Substring(mediaPos, open - mediaPos).ToLowerInvariant();
                var body = text.Substring(open + 1, close - open - 1);

                bool keep = EvaluateMediaQuery(header, viewportWidth);
                if (keep) sb.Append(body);
                
                i = close + 1;
            }

            return sb.ToString();
        }

/// <summary>
/// Evaluate a media query condition string
/// </summary>
private static bool EvaluateMediaQuery(string header, double? viewportWidth)
{
    if (string.IsNullOrWhiteSpace(header)) return true;
    if (header.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;

    // Support comma-separated OR queries
    if (header.Contains(","))
    {
        var parts = header.Split(',');
        foreach (var p in parts)
        {
            if (EvaluateMediaQueryInternal(p.Trim(), viewportWidth)) return true;
        }
        return false;
    }

    return EvaluateMediaQueryInternal(header.Trim(), viewportWidth);
}

private static bool EvaluateMediaQueryInternal(string query, double? viewportWidth)
{
    bool conditionMatches = true;
    bool isNot = false;

    if (query.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
    {
        isNot = true;
        query = query.Substring(4).Trim();
    }

    // Check media types
    if (query.Contains("print", StringComparison.OrdinalIgnoreCase) && !query.Contains("screen", StringComparison.OrdinalIgnoreCase))
    {
        conditionMatches = false; // We're not printing
    }

    var mw = ExtractPx(query, "min-width");
    var xw = ExtractPx(query, "max-width");
    var mh = ExtractPx(query, "min-height");
    var xh = ExtractPx(query, "max-height");
    var vpW = viewportWidth ?? CssParser.MediaViewportWidth ?? 1920;
    var vpH = CssParser.MediaViewportHeight ?? 1080;

    if (mw.HasValue && vpW < mw.Value) conditionMatches = false;
    if (xw.HasValue && vpW > xw.Value) conditionMatches = false;
    if (mh.HasValue && vpH < mh.Value) conditionMatches = false;
    if (xh.HasValue && vpH > xh.Value) conditionMatches = false;

    // Check orientation
    if (query.Contains("orientation", StringComparison.OrdinalIgnoreCase))
    {
        bool isPortrait = vpH > vpW;
        if (query.Contains("portrait", StringComparison.OrdinalIgnoreCase))
        {
            if (!isPortrait) conditionMatches = false;
        }
        else if (query.Contains("landscape", StringComparison.OrdinalIgnoreCase))
        {
            if (isPortrait) conditionMatches = false;
        }
    }

    // Check prefers-color-scheme
    if (query.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase))
    {
        string scheme = CssParser.MediaPrefersColorScheme ?? "light";
        bool isDark = string.Equals(scheme, "dark", StringComparison.OrdinalIgnoreCase);
        
        if (query.Contains("dark", StringComparison.OrdinalIgnoreCase))
        {
            if (!isDark) conditionMatches = false;
        }
        else if (query.Contains("light", StringComparison.OrdinalIgnoreCase))
        {
            if (isDark) conditionMatches = false;
        }
    }

    bool result = isNot ? !conditionMatches : conditionMatches;
    
    // Diagnostic logging disabled for performance

    return result;
}

private static double? ExtractPx(string text, string prop)
{
    // Support decimal values and optional units (default px)
    // Matches prop: 123.45px or prop: 123.45
    // Using named groups to avoid index confusion
    var m = Regex.Match(text, prop + @"\s*:\s*(?<v>[0-9]*\.?[0-9]+)\s*(?<u>px|em|rem|vw)?", RegexOptions.IgnoreCase);
    if (m.Success)
    {
        if (double.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            string unit = m.Groups["u"].Value.ToLowerInvariant();
            if (unit == "em" || unit == "rem") v *= 16; 
            else if (unit == "vw") v = (v / 100.0) * (CssParser.MediaViewportWidth ?? 1920);
            return v;
        }
    }
    return null;
}

        private static int FindMatchingBrace(string s, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }





        private static List<NewCss.CssDeclaration> ParseDeclarations(string declText)
        {
            var list = new List<NewCss.CssDeclaration>();
            // naive split by ';', then split by ':'
            var parts = declText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var kv = p.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;

                var name = kv[0].Trim().ToLowerInvariant();
                var valRaw = kv[1].Trim();

                bool important = false;
                var val = valRaw;
                var idx = valRaw.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    important = true;
                    val = valRaw.Substring(0, idx).Trim();
                }

                list.Add(new NewCss.CssDeclaration { Property = name, Value = val, IsImportant = important });
            }
            return list;
        }

        // ===========================
        // Stage 3: Cascade
        // ===========================

        private static Dictionary<Node, CssComputed> CascadeIntoComputedStyles(Element root, List<NewCss.CssRule> rules, Action<string> log)
        {
            var result = new Dictionary<Node, CssComputed>();
            if (root == null) return result;
            
            // Create CascadeEngine with provided rules (Author + User)
            var sheet = new NewCss.CssStylesheet();
            sheet.Rules.AddRange(rules);
            var engine = new CascadeEngine(sheet);

            // Pre-flatten the DOM into a list
            var nodes = new List<Element>();
            var stack = new Stack<Element>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                nodes.Add(n);
                for (int i = n.Children.Count - 1; i >= 0; i--)
                {
                    if (n.Children[i] is Element childEl)
                        stack.Push(childEl);
                }
            }

            foreach (var n in nodes)
            {
                if (n.IsText) continue;

                CssComputed parentCss = null;
                if (n.Parent != null)
                    result.TryGetValue(n.Parent, out parentCss);

                try
                {
                    // 1. Compute Main Styles
                    var mainProps = engine.ComputeCascadedValues(n, null);
                    MergeInlineStyle(n, mainProps);
                    var css = ResolveStyle(n, parentCss, mainProps);

                    // 2. Compute Pseudo-Element Styles
                    ResolvePseudo(n, css, engine, "before", (c, s) => c.Before = s);
                    ResolvePseudo(n, css, engine, "after", (c, s) => c.After = s);
                    ResolvePseudo(n, css, engine, "marker", (c, s) => c.Marker = s);
                    ResolvePseudo(n, css, engine, "placeholder", (c, s) => c.Placeholder = s);
                    ResolvePseudo(n, css, engine, "selection", (c, s) => c.Selection = s);
                    ResolvePseudo(n, css, engine, "first-line", (c, s) => c.FirstLine = s);
                    ResolvePseudo(n, css, engine, "first-letter", (c, s) => c.FirstLetter = s);

                    result[n] = css;
                }
                catch (Exception resolveEx)
                {
                    var msg = $"[CssLoader] CRASH in ResolveStyle (or pseudo) for Node <{n.Tag} id='{n.Id}'>: {resolveEx}";
                    Log(log, msg);
                    result[n] = new CssComputed(); // Recovery
                }
            }
            
            return result;
        }

        private static void ResolvePseudo(Element n, CssComputed parent, CascadeEngine engine, string pseudo, Action<CssComputed, CssComputed> setProp)
        {
            var props = engine.ComputeCascadedValues(n, pseudo);
            if (props.Count > 0)
            {
                // Pseudo-elements inherit from their originating element (parent)
                var resolved = ResolveStyle(n, parent, props);
                setProp(parent, resolved);
            }
        }

        private static void MergeInlineStyle(Element n, Dictionary<string, NewCss.CssDeclaration> props)
        {
            if (n.Attr != null && n.Attr.TryGetValue("style", out var style) && !string.IsNullOrWhiteSpace(style))
            {
                var decls = ParseDeclarations(style);
                foreach (var d in decls)
                {
                    // Inline style overrides author rules.
                    props[d.Property] = d; 
                }
            }
        }

        private static CssComputed ResolveStyle(Element n, CssComputed parentCss, Dictionary<string, NewCss.CssDeclaration> cascadedProperties)
        {
            // DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", "[D-BUG] ResolveStyle\r\n");

            var css = new CssComputed();
            string tag = n?.Tag?.ToUpperInvariant() ?? "";

            try
            {
                if (parentCss != null && parentCss.CustomProperties != null)
                {
                    foreach (var kv in parentCss.CustomProperties)
                    {
                        css.CustomProperties[kv.Key] = kv.Value;
                        css.Map[kv.Key] = kv.Value;
                    }
                }

                if (cascadedProperties != null && cascadedProperties.Count > 0)
                {
                    // Extract custom properties first
                    var rawCustom = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach(var kv in cascadedProperties)
                    {
                         if (kv.Key.StartsWith("--"))
                            rawCustom[kv.Key] = kv.Value.Value ?? "";
                    }
                    
                    // Resolve variables in custom properties
                    foreach (var key in rawCustom.Keys.ToList())
                    {
                        var resolvedCustom = ResolveCustomPropertyReferences(rawCustom[key], css, rawCustom, new HashSet<string>(StringComparer.Ordinal) { key });
                        rawCustom[key] = resolvedCustom;
                        css.CustomProperties[key] = resolvedCustom;
                        css.Map[key] = resolvedCustom;
                    }

                    // Resolve standard properties
                    foreach (var kv in cascadedProperties)
                    {
                        if (kv.Key.StartsWith("--")) continue;
                        var val = ResolveCustomPropertyReferences(kv.Value.Value, css, rawCustom, new HashSet<string>());
                        
                        // Handle CSS-wide keywords: inherit, initial, unset
                        var lowerVal = val?.ToLowerInvariant()?.Trim();
                        if (lowerVal == "inherit")
                        {
                            // Use parent's computed value
                            if (parentCss != null && parentCss.Map.TryGetValue(kv.Key, out var parentVal))
                                val = parentVal;
                            else
                                val = CssComputed.GetInitialValue(kv.Key) ?? val;
                        }
                        else if (lowerVal == "initial")
                        {
                            // Use spec-defined initial value
                            val = CssComputed.GetInitialValue(kv.Key) ?? val;
                        }
                        else if (lowerVal == "unset")
                        {
                            // inherit for inherited properties, initial for non-inherited
                            if (CssComputed.IsInheritedProperty(kv.Key))
                            {
                                if (parentCss != null && parentCss.Map.TryGetValue(kv.Key, out var parentVal))
                                    val = parentVal;
                                else
                                    val = CssComputed.GetInitialValue(kv.Key) ?? val;
                            }
                            else
                            {
                                val = CssComputed.GetInitialValue(kv.Key) ?? val;
                            }
                        }
                        
                        css.Map[kv.Key] = val;
                    }
                }

                // Populate core display/positioning properties from the map
                css.Display = Safe(DictGet(css.Map, "display"))?.ToLowerInvariant();
                css.Position = Safe(DictGet(css.Map, "position"))?.ToLowerInvariant();
                css.Visibility = Safe(DictGet(css.Map, "visibility"))?.ToLowerInvariant(); // Add Visibility
                css.FlexDirection = Safe(DictGet(css.Map, "flex-direction"))?.ToLowerInvariant();
                css.FlexWrap = Safe(DictGet(css.Map, "flex-wrap"))?.ToLowerInvariant();
                css.JustifyContent = Safe(DictGet(css.Map, "justify-content"))?.ToLowerInvariant();
                css.JustifyItems = Safe(DictGet(css.Map, "justify-items"))?.ToLowerInvariant();
                css.JustifySelf = Safe(DictGet(css.Map, "justify-self"))?.ToLowerInvariant();
                css.AlignItems = Safe(DictGet(css.Map, "align-items"))?.ToLowerInvariant();
                css.AlignContent = Safe(DictGet(css.Map, "align-content"))?.ToLowerInvariant();
                css.AlignSelf = Safe(DictGet(css.Map, "align-self"))?.ToLowerInvariant();
                
                // Grid Properties
                css.GridTemplateColumns = Safe(DictGet(css.Map, "grid-template-columns"));
                css.GridTemplateRows = Safe(DictGet(css.Map, "grid-template-rows"));
                css.GridTemplateAreas = Safe(DictGet(css.Map, "grid-template-areas"));
                
                // Grid Placement
                css.GridArea = Safe(DictGet(css.Map, "grid-area"));
                css.GridColumnStart = Safe(DictGet(css.Map, "grid-column-start"));
                css.GridColumnEnd = Safe(DictGet(css.Map, "grid-column-end"));
                
                // Gaps (row-gap, column-gap, gap, grid-gap shorthand)
                var rawRowGap = Safe(DictGet(css.Map, "row-gap")) ?? Safe(DictGet(css.Map, "grid-row-gap"));
                var rawColGap = Safe(DictGet(css.Map, "column-gap")) ?? Safe(DictGet(css.Map, "grid-column-gap"));
                var rawGap = Safe(DictGet(css.Map, "gap")) ?? Safe(DictGet(css.Map, "grid-gap"));

                if (!string.IsNullOrEmpty(rawGap)) 
                {
                    var parts = rawGap.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1) rawRowGap = parts[0];
                    if (parts.Length >= 2) rawColGap = parts[1];
                    else if (parts.Length == 1) rawColGap = parts[0];
                }

                css.RowGap = ParseGapValue(rawRowGap);
                css.ColumnGap = ParseGapValue(rawColGap);
                css.GridRowStart = Safe(DictGet(css.Map, "grid-row-start"));
                css.GridRowEnd = Safe(DictGet(css.Map, "grid-row-end"));
                
                // Parse grid-column shorthand
                string gridColumn = DictGet(css.Map, "grid-column");
                if (!string.IsNullOrWhiteSpace(gridColumn))
                {
                    var parts = gridColumn.Split('/');
                    if (parts.Length >= 1 && string.IsNullOrEmpty(css.GridColumnStart))
                        css.GridColumnStart = parts[0].Trim();
                    if (parts.Length >= 2 && string.IsNullOrEmpty(css.GridColumnEnd))
                        css.GridColumnEnd = parts[1].Trim();
                }
                
                // Parse grid-row shorthand
                string gridRow = DictGet(css.Map, "grid-row");
                if (!string.IsNullOrWhiteSpace(gridRow))
                {
                    var parts = gridRow.Split('/');
                    if (parts.Length >= 1 && string.IsNullOrEmpty(css.GridRowStart))
                        css.GridRowStart = parts[0].Trim();
                    if (parts.Length >= 2 && string.IsNullOrEmpty(css.GridRowEnd))
                        css.GridRowEnd = parts[1].Trim();
                }
                
                // Grid Auto Flow & Implicit Tracks
                css.GridAutoFlow = Safe(DictGet(css.Map, "grid-auto-flow"))?.ToLowerInvariant();
                css.GridAutoColumns = Safe(DictGet(css.Map, "grid-auto-columns"));
                css.GridAutoRows = Safe(DictGet(css.Map, "grid-auto-rows"));
                
                css.ColumnSpan = Safe(DictGet(css.Map, "column-span"));
                css.ColumnRuleStyle = Safe(DictGet(css.Map, "column-rule-style"));
                css.ColumnRuleWidth = Safe(DictGet(css.Map, "column-rule-width"));
                css.ColumnRuleColor = Safe(DictGet(css.Map, "column-rule-color"));
                
                // Overflow properties
                css.Overflow = Safe(DictGet(css.Map, "overflow"))?.ToLowerInvariant();
                css.OverflowX = Safe(DictGet(css.Map, "overflow-x"))?.ToLowerInvariant() ?? css.Overflow;
                css.OverflowY = Safe(DictGet(css.Map, "overflow-y"))?.ToLowerInvariant() ?? css.Overflow;

                // ACID2 FIX: Visibility property - critical for hiding elements
                css.Visibility = Safe(DictGet(css.Map, "visibility"))?.ToLowerInvariant();

                // Box Model
                css.BoxSizing = Safe(DictGet(css.Map, "box-sizing"))?.ToLowerInvariant();
            }
            catch (Exception ex)
            {
                 /* [PERF-REMOVED] */
            }
            
            // z-index logic continues below...

            double zVal; // Declare zVal here, outside the try-catch block
            if (TryDouble(DictGet(css.Map, "z-index"), out zVal)) css.ZIndex = (int)zVal;
            
            // Background properties
            css.BackgroundClip = Safe(DictGet(css.Map, "background-clip"))?.ToLowerInvariant();
            css.BackgroundOrigin = Safe(DictGet(css.Map, "background-origin"))?.ToLowerInvariant();
            css.BackgroundRepeat = Safe(DictGet(css.Map, "background-repeat"))?.ToLowerInvariant();
            css.BackgroundSize = Safe(DictGet(css.Map, "background-size"))?.ToLowerInvariant();
            css.BackgroundPosition = Safe(DictGet(css.Map, "background-position"))?.ToLowerInvariant();

            double cssFlexVal;
            if (TryDouble(DictGet(css.Map, "flex-grow"), out cssFlexVal)) css.FlexGrow = cssFlexVal;
            if (TryDouble(DictGet(css.Map, "flex-shrink"), out cssFlexVal)) css.FlexShrink = cssFlexVal;
            if (TryDouble(DictGet(css.Map, "order"), out cssFlexVal)) css.Order = (int)cssFlexVal;
            
            // Handle 'flex' shorthand: flex: [grow] [shrink] [basis]
            string flexShorthand = DictGet(css.Map, "flex");
            if (!string.IsNullOrEmpty(flexShorthand))
            {
                double fg, fs, fb;
                if (TryFlexShorthand(flexShorthand, out fg, out fs, out fb))
                {
                    css.FlexGrow = fg;
                    css.FlexShrink = fs;
                    if (!double.IsNaN(fb)) css.FlexBasis = fb;
                }
            }
            
            // Interaction
            css.PointerEvents = Safe(DictGet(css.Map, "pointer-events"))?.ToLowerInvariant();

            double emBase = 16.0;
            if (parentCss != null && parentCss.FontSize.HasValue) emBase = parentCss.FontSize.Value;
            
            // Parse CSS 'font' shorthand: font: [style] [weight] size[/line-height] family
            // Example: font: 400 16px/1.5 Arial
            string fontShorthand = DictGet(css.Map, "font");
            if (!string.IsNullOrWhiteSpace(fontShorthand) && !fontShorthand.StartsWith("var("))
            {
                ParseFontShorthand(fontShorthand, css, emBase);
            }
            
            // Explicit font-family overrides shorthand
            string fontFamilyRaw = DictGet(css.Map, "font-family");
            if (!string.IsNullOrWhiteSpace(fontFamilyRaw))
            {
                 var fontParts = fontFamilyRaw.Split(',');
                 foreach(var fp in fontParts) 
                 {
                     var clean = fp.Trim().Trim('"', '\'');
                     if (!string.IsNullOrEmpty(clean)) 
                     {
                         css.FontFamilyName = clean;
                         break; 
                     }
                 }
            }
            // INHERITANCE: Font family
            if (string.IsNullOrEmpty(css.FontFamilyName) && parentCss != null)
            {
                css.FontFamilyName = parentCss.FontFamilyName;
            }
            
            double currentEmBase = emBase;
            double fsPx;
            string rawFontSize = DictGet(css.Map, "font-size");
            if (TryPx(rawFontSize, out fsPx, emBase, percentBase: emBase)) 
            {
                css.FontSize = fsPx;
                currentEmBase = fsPx;
                // DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[FONT-TRACE] Element={n.Tag} fs={rawFontSize} -> {fsPx}px\r\n");
            }
            else if (parentCss != null && parentCss.FontSize.HasValue)
            {
                css.FontSize = parentCss.FontSize.Value;
                currentEmBase = parentCss.FontSize.Value;
            }
            else {
                css.FontSize = 16.0;
                currentEmBase = 16.0;
            }
            
            // Multi-column properties (moved here to have currentEmBase available)
            css.ColumnCount = Safe(DictGet(css.Map, "column-count"));
            if (int.TryParse(css.ColumnCount, out int cCount)) css.ColumnCountInt = cCount;
            
            css.ColumnWidth = Safe(DictGet(css.Map, "column-width"));
            if (TryPx(css.ColumnWidth, out double cWidth, currentEmBase)) css.ColumnWidthFloat = cWidth;
            
            css.Columns = Safe(DictGet(css.Map, "columns"));
            if (!string.IsNullOrEmpty(css.Columns))
            {
                // Shorthand: [width] [count] (order-independent)
                var colParts = css.Columns.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var cp in colParts)
                {
                    if (int.TryParse(cp, out int c)) css.ColumnCountInt = c;
                    else if (TryPx(cp, out double w, currentEmBase)) css.ColumnWidthFloat = w;
                }
            }
            
            css.ColumnGapValue = Safe(DictGet(css.Map, "column-gap"));
            if (TryPx(css.ColumnGapValue, out double colGapVal, currentEmBase)) css.ColumnGap = colGapVal;
            
            if (css.FontSize < 8) {
                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[FONT-WARN] Tiny Font! Element={n.Tag} fs={rawFontSize ?? "null"} resolved={css.FontSize}px\r\n");
            }

            double posVal;
            if (TryPx(DictGet(css.Map, "left"), out posVal, currentEmBase)) css.Left = posVal;
            else if (TryPercent(DictGet(css.Map, "left"), out posVal)) css.LeftPercent = posVal;

            if (TryPx(DictGet(css.Map, "top"), out posVal, currentEmBase)) css.Top = posVal;
            else if (TryPercent(DictGet(css.Map, "top"), out posVal)) css.TopPercent = posVal;

            if (TryPx(DictGet(css.Map, "right"), out posVal, currentEmBase)) css.Right = posVal;
            else if (TryPercent(DictGet(css.Map, "right"), out posVal)) css.RightPercent = posVal;

            if (TryPx(DictGet(css.Map, "bottom"), out posVal, currentEmBase)) css.Bottom = posVal;
            else if (TryPercent(DictGet(css.Map, "bottom"), out posVal)) css.BottomPercent = posVal;
            
            // inset property: shorthand for top, right, bottom, left
            Thickness insetTh;
            if (TryThickness(DictGet(css.Map, "inset"), out insetTh, currentEmBase))
            {
                css.Top = insetTh.Top;
                css.Right = insetTh.Right;
                css.Bottom = insetTh.Bottom;
                css.Left = insetTh.Left;
            }
            // inset-block: shorthand for top, bottom (in horizontal-tb)
            if (TryThickness(DictGet(css.Map, "inset-block"), out insetTh, currentEmBase))
            {
                css.Top = insetTh.Top;
                css.Bottom = insetTh.Bottom;
            }
            // inset-inline: shorthand for left, right (in horizontal-tb)
            if (TryThickness(DictGet(css.Map, "inset-inline"), out insetTh, currentEmBase))
            {
                css.Left = insetTh.Left;
                css.Right = insetTh.Right;
            }

            double sizeVal;
            


            string wStr = DictGet(css.Map, "width");
            if (tag == "BODY") {
            }
            if (TryPx(wStr, out sizeVal, currentEmBase)) {
                css.Width = sizeVal;
            }
            else if (TryPercent(wStr, out sizeVal)) {
                css.WidthPercent = sizeVal;
            }
            else if (IsCssFunction(wStr)) {
                css.WidthExpression = wStr;
            }

            string hStr = DictGet(css.Map, "height");
            if (TryPx(hStr, out sizeVal, currentEmBase)) css.Height = sizeVal;
            else if (TryPercent(hStr, out sizeVal)) css.HeightPercent = sizeVal;
            else if (IsCssFunction(hStr)) css.HeightExpression = hStr;

            string minWStr = DictGet(css.Map, "min-width");
            if (TryPx(minWStr, out sizeVal, currentEmBase)) css.MinWidth = sizeVal;
            else if (TryPercent(minWStr, out sizeVal)) css.MinWidthExpression = sizeVal + "%";
            else if (IsCssFunction(minWStr)) css.MinWidthExpression = minWStr;
            
            string minHStr = DictGet(css.Map, "min-height");
            if (TryPx(minHStr, out sizeVal, currentEmBase)) css.MinHeight = sizeVal;
            else if (TryPercent(minHStr, out sizeVal)) css.MinHeightExpression = sizeVal + "%";
            else if (IsCssFunction(minHStr)) css.MinHeightExpression = minHStr;
            
            string maxWStr = DictGet(css.Map, "max-width");
            if (TryPx(maxWStr, out sizeVal, currentEmBase)) css.MaxWidth = sizeVal;
            else if (TryPercent(maxWStr, out sizeVal)) css.MaxWidthExpression = sizeVal + "%";
            else if (IsCssFunction(maxWStr)) css.MaxWidthExpression = maxWStr;

            string maxHStr = DictGet(css.Map, "max-height");
            if (TryPx(maxHStr, out sizeVal, currentEmBase)) css.MaxHeight = sizeVal;
            else if (TryPercent(maxHStr, out sizeVal)) css.MaxHeightExpression = sizeVal + "%";
            else if (IsCssFunction(maxHStr)) css.MaxHeightExpression = maxHStr;
            
            // Logical properties: inline-size, block-size
            string isVal = DictGet(css.Map, "inline-size");
            if (TryPx(isVal, out sizeVal, currentEmBase)) css.Width = sizeVal;
            else if (TryPercent(isVal, out sizeVal)) css.WidthPercent = sizeVal;
            else if (IsCssFunction(isVal)) css.WidthExpression = isVal;
            
            string bsVal = DictGet(css.Map, "block-size");
            if (TryPx(bsVal, out sizeVal, currentEmBase)) css.Height = sizeVal;
            else if (TryPercent(bsVal, out sizeVal)) css.HeightPercent = sizeVal;
            else if (IsCssFunction(bsVal)) css.HeightExpression = bsVal;
            
            if (TryPx(DictGet(css.Map, "min-inline-size"), out sizeVal, currentEmBase)) css.MinWidth = sizeVal;
            if (TryPx(DictGet(css.Map, "min-block-size"), out sizeVal, currentEmBase)) css.MinHeight = sizeVal;
            if (TryPx(DictGet(css.Map, "max-inline-size"), out sizeVal, currentEmBase)) css.MaxWidth = sizeVal;
            if (TryPx(DictGet(css.Map, "max-block-size"), out sizeVal, currentEmBase)) css.MaxHeight = sizeVal;
            

            var aspectRatioRaw = Safe(DictGet(css.Map, "aspect-ratio"));
            if (!string.IsNullOrEmpty(aspectRatioRaw) && !aspectRatioRaw.Contains("auto"))
            {
                if (aspectRatioRaw.Contains("/"))
                {
                    var parts = aspectRatioRaw.Split('/');
                    if (parts.Length == 2)
                    {
                        double w, h;
                        if (TryDouble(parts[0].Trim(), out w) && TryDouble(parts[1].Trim(), out h) && h > 0)
                            css.AspectRatio = w / h;
                    }
                }
                else
                {
                    double ratio;
                    if (TryDouble(aspectRatioRaw, out ratio) && ratio > 0)
                        css.AspectRatio = ratio;
                }
            }

            double gapRow, gapCol;
            if (TryGapShorthand(DictGet(css.Map, "gap"), out gapRow, out gapCol))
            {
                css.Gap = gapRow;
                css.RowGap = gapRow;
                css.ColumnGap = gapCol;
            }
            double gapExplicit;
            if (TryPx(DictGet(css.Map, "row-gap"), out gapExplicit, currentEmBase)) css.RowGap = gapExplicit;
            if (TryPx(DictGet(css.Map, "column-gap"), out gapExplicit, currentEmBase)) css.ColumnGap = gapExplicit;
            if (!css.RowGap.HasValue && css.Gap.HasValue) css.RowGap = css.Gap;
            if (!css.ColumnGap.HasValue)
            {
                if (css.Gap.HasValue) css.ColumnGap = css.Gap;
                else if (css.RowGap.HasValue) css.ColumnGap = css.RowGap;
            }

            // INHERITANCE: Visibility
            // Visibility is an inherited property. If not specified, take from parent.
            if (string.IsNullOrEmpty(css.Visibility))
            {
                 if (parentCss != null && !string.IsNullOrEmpty(parentCss.Visibility))
                 {
                     css.Visibility = parentCss.Visibility;
                 }
                 else
                 {
                     css.Visibility = "visible";
                 }
            }

            var fgColor = TryColor(DictGet(css.Map, "color"));
            if (fgColor.HasValue) 
                css.ForegroundColor = fgColor;
            else if (parentCss != null && parentCss.ForegroundColor.HasValue)
                css.ForegroundColor = parentCss.ForegroundColor; // Inherit color from parent

            // [FIX] Explicitly handle background-color first (highest priority)
            var explicitBgColor = TryColor(DictGet(css.Map, "background-color"));
            if (explicitBgColor.HasValue)
            {
                css.BackgroundColor = explicitBgColor;
            }
            else
            {
                // Fallback to 'background' shorthand
                var bgShorthand = DictGet(css.Map, "background");
                if (!string.IsNullOrWhiteSpace(bgShorthand))
                {
                    // Try parsing whole string as color first
                    var shColor = TryColor(bgShorthand);
                    if (shColor.HasValue) 
                    {
                        css.BackgroundColor = shColor;
                    }
                    else
                    {
                        // TODO: Extract color from complex shorthand (e.g. "url(...) no-repeat red")
                        // For now simple extraction: check commonly used color names or hex codes in the string
                        var tokens = SplitTokens(bgShorthand);
                        foreach (var t in tokens)
                        {
                            var c = TryColor(t);
                            if (c.HasValue) 
                            { 
                                css.BackgroundColor = c; 
                                break; 
                            }
                        }
                    }
                }
            }

            string bgImage = DictGet(css.Map, "background-image");
            if (string.IsNullOrWhiteSpace(bgImage)) bgImage = DictGet(css.Map, "background");

            if (!string.IsNullOrWhiteSpace(bgImage))
            {
                // Check for url() - store directly for image rendering
                bool containsUrl = bgImage.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0;
                bool containsGradient = bgImage.Contains("gradient");
                
                if (containsUrl && !containsGradient)
                {
                    // Store the url() value directly for ImageLoader to process
                    css.BackgroundImage = bgImage;
                    FenLogger.Debug($"[CSS] BackgroundImage URL stored: {bgImage.Substring(0, Math.Min(80, bgImage.Length))}...", LogCategory.CSS);
                }
                else if (containsGradient)
                {
                    // Parse gradient

                    
                    var grad = ParseGradient(bgImage);
                    if (grad != null) 
                    {
                        css.BackgroundImage = grad;

                    }
                    else
                    {

                    }
                }
            }

            try
            {
                var ffRaw = DictGet(css.Map, "font-family");
                var resolved = SelectFontFamily(ffRaw);
                if (!string.IsNullOrEmpty(resolved))
                    css.FontFamilyName = resolved;
            }
            catch { }

            var fwRaw = Safe(DictGet(css.Map, "font-weight"));
            if (!string.IsNullOrEmpty(fwRaw))
            {
                var fw = fwRaw.Trim().ToLowerInvariant();
                if (fw == "normal") css.FontWeight = MakeFontWeight(400);
                else if (fw == "bold") css.FontWeight = MakeFontWeight(700);
                else if (fw == "bolder") css.FontWeight = MakeFontWeight(700);
                else if (fw == "lighter") css.FontWeight = MakeFontWeight(300);
                else
                {
                    int numeric;
                    if (int.TryParse(fw, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                    {
                        css.FontWeight = MakeFontWeight(numeric);
                    }
                }
            }

            var fsRaw = Safe(DictGet(css.Map, "font-style"));
            if (!string.IsNullOrEmpty(fsRaw))
            {
                if (string.Equals(fsRaw, "italic", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fsRaw, "oblique", StringComparison.OrdinalIgnoreCase))
                    css.FontStyle = SKFontStyleSlant.Italic;
                else if (string.Equals(fsRaw, "normal", StringComparison.OrdinalIgnoreCase))
                    css.FontStyle = SKFontStyleSlant.Upright;
            }

            var ta = Safe(DictGet(css.Map, "text-align"));
            if (ta == "center") css.TextAlign = SKTextAlign.Center;
            else if (ta == "right") css.TextAlign = SKTextAlign.Right;
            else if (ta == "justify") css.TextAlign = SKTextAlign.Left; // Skia doesn't support justify natively

            css.TextDecoration = Safe(DictGet(css.Map, "text-decoration"));

            double opacityVal;
            if (TryDouble(DictGet(css.Map, "opacity"), out opacityVal))
                css.Opacity = Math.Max(0.0, Math.Min(1.0, opacityVal));

            // Interactivity
            css.PointerEvents = Safe(DictGet(css.Map, "pointer-events"));
            css.Cursor = Safe(DictGet(css.Map, "cursor"));
            css.UserSelect = Safe(DictGet(css.Map, "user-select"));

            css.TextShadow = Safe(DictGet(css.Map, "text-shadow"));
            css.BoxShadow = Safe(DictGet(css.Map, "box-shadow"));
            css.MaskImage = Safe(DictGet(css.Map, "mask-image"));
            if (string.IsNullOrEmpty(css.MaskImage)) css.MaskImage = Safe(DictGet(css.Map, "-webkit-mask-image"));

            Thickness th;
            if (TryThickness(DictGet(css.Map, "margin"), out th, currentEmBase)) css.Margin = th;
            
            // Detect margin: auto for centering (horizontal and vertical for absolute pos)
            string marginRaw = DictGet(css.Map, "margin")?.Trim().ToLowerInvariant() ?? "";
            string marginLeftRaw = DictGet(css.Map, "margin-left")?.Trim().ToLowerInvariant() ?? "";
            string marginRightRaw = DictGet(css.Map, "margin-right")?.Trim().ToLowerInvariant() ?? "";
            string marginTopRaw = DictGet(css.Map, "margin-top")?.Trim().ToLowerInvariant() ?? "";
            string marginBottomRaw = DictGet(css.Map, "margin-bottom")?.Trim().ToLowerInvariant() ?? "";
            
            // Parse margin shorthand for auto: "auto", "0 auto", "0 auto 0 auto", etc.
            if (!string.IsNullOrEmpty(marginRaw))
            {
                var parts = marginRaw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1 && parts[0] == "auto")
                {
                    // margin: auto - all sides auto
                    css.MarginLeftAuto = true;
                    css.MarginRightAuto = true;
                    css.MarginTopAuto = true;
                    css.MarginBottomAuto = true;
                }
                else if (parts.Length == 2)
                {
                    // margin: V H
                    if (parts[0] == "auto") { css.MarginTopAuto = true; css.MarginBottomAuto = true; }
                    if (parts[1] == "auto") { css.MarginLeftAuto = true; css.MarginRightAuto = true; }
                }
                else if (parts.Length == 3)
                {
                    // margin: T H B
                    if (parts[0] == "auto") css.MarginTopAuto = true;
                    if (parts[1] == "auto") { css.MarginLeftAuto = true; css.MarginRightAuto = true; }
                    if (parts[2] == "auto") css.MarginBottomAuto = true;
                }
                else if (parts.Length == 4)
                {
                    // margin: T R B L
                    if (parts[0] == "auto") css.MarginTopAuto = true;
                    if (parts[1] == "auto") css.MarginRightAuto = true;
                    if (parts[2] == "auto") css.MarginBottomAuto = true;
                    if (parts[3] == "auto") css.MarginLeftAuto = true;
                }
            }
            
            // Individual overrides
            if (marginLeftRaw == "auto") css.MarginLeftAuto = true;
            if (marginRightRaw == "auto") css.MarginRightAuto = true;
            if (marginTopRaw == "auto") css.MarginTopAuto = true;
            if (marginBottomRaw == "auto") css.MarginBottomAuto = true;

            // DEBUG: Log margin auto detection for DIV elements
            if (tag == "DIV" && (!string.IsNullOrEmpty(marginRaw) || !string.IsNullOrEmpty(marginLeftRaw) || !string.IsNullOrEmpty(marginRightRaw)))
            {
                /* [PERF-REMOVED] */
            }

            double mVal;
            var m = css.Margin;
            double mLeft = m.Left, mTop = m.Top, mRight = m.Right, mBottom = m.Bottom;
            if (!css.MarginLeftAuto && TryPx(DictGet(css.Map, "margin-left"), out mVal, currentEmBase)) mLeft = mVal;
            if (TryPx(DictGet(css.Map, "margin-top"), out mVal, currentEmBase)) mTop = mVal;
            if (!css.MarginRightAuto && TryPx(DictGet(css.Map, "margin-right"), out mVal, currentEmBase)) mRight = mVal;
            if (TryPx(DictGet(css.Map, "margin-bottom"), out mVal, currentEmBase)) mBottom = mVal;
            
            // Logical Properties: margin-block/margin-inline (horizontal-tb writing mode)
            // margin-block-start -> top, margin-block-end -> bottom
            // margin-inline-start -> left, margin-inline-end -> right
            if (TryThickness(DictGet(css.Map, "margin-block"), out th, currentEmBase))
            {
                mTop = th.Top; mBottom = th.Bottom;
            }
            if (TryPx(DictGet(css.Map, "margin-block-start"), out mVal, currentEmBase)) mTop = mVal;
            if (TryPx(DictGet(css.Map, "margin-block-end"), out mVal, currentEmBase)) mBottom = mVal;
            if (TryThickness(DictGet(css.Map, "margin-inline"), out th, currentEmBase))
            {
                mLeft = th.Left; mRight = th.Right;
            }
            if (TryPx(DictGet(css.Map, "margin-inline-start"), out mVal, currentEmBase)) mLeft = mVal;
            if (TryPx(DictGet(css.Map, "margin-inline-end"), out mVal, currentEmBase)) mRight = mVal;
            
            css.Margin = new Thickness(mLeft, mTop, mRight, mBottom);
            
            // DEBUG: Log H2 margin to trace 2400px margin-top bug (DISABLED - causes performance issues)
            // if (tag == "H2")
            // {
            //     string rawMarginH2 = DictGet(css.Map, "margin") ?? "(none)";
            //     string rawMarginTop = DictGet(css.Map, "margin-top") ?? "(none)";
            //     /* [PERF-REMOVED] */
            // }

            if (TryThickness(DictGet(css.Map, "padding"), out th, currentEmBase)) css.Padding = th;

            var p = css.Padding;
            double pLeft = p.Left, pTop = p.Top, pRight = p.Right, pBottom = p.Bottom;
            if (TryPx(DictGet(css.Map, "padding-left"), out mVal, currentEmBase)) pLeft = mVal;
            if (TryPx(DictGet(css.Map, "padding-top"), out mVal, currentEmBase)) pTop = mVal;
            if (TryPx(DictGet(css.Map, "padding-right"), out mVal, currentEmBase)) pRight = mVal;
            if (TryPx(DictGet(css.Map, "padding-bottom"), out mVal, currentEmBase)) pBottom = mVal;
            
            // Logical Properties: padding-block/padding-inline (horizontal-tb writing mode)
            if (TryThickness(DictGet(css.Map, "padding-block"), out th, currentEmBase))
            {
                pTop = th.Top; pBottom = th.Bottom;
            }
            if (TryPx(DictGet(css.Map, "padding-block-start"), out mVal, currentEmBase)) pTop = mVal;
            if (TryPx(DictGet(css.Map, "padding-block-end"), out mVal, currentEmBase)) pBottom = mVal;
            if (TryThickness(DictGet(css.Map, "padding-inline"), out th, currentEmBase))
            {
                pLeft = th.Left; pRight = th.Right;
            }
            if (TryPx(DictGet(css.Map, "padding-inline-start"), out mVal, currentEmBase)) pLeft = mVal;
            if (TryPx(DictGet(css.Map, "padding-inline-end"), out mVal, currentEmBase)) pRight = mVal;
            
            css.Padding = new Thickness(pLeft, pTop, pRight, pBottom);
            
            var borderColor = TryColor(ExtractBorderColor(css.Map));
            if (borderColor.HasValue) css.BorderBrushColor = borderColor;

            if (TryThickness(ExtractBorderThickness(css.Map), out th, currentEmBase)) css.BorderThickness = th;
            CssCornerRadius cr;
            if (TryCornerRadius(DictGet(css.Map, "border-radius"), out cr)) css.BorderRadius = cr;
            
            // Parse border-style shorthand
            var borderStyleValue = Safe(DictGet(css.Map, "border-style"));
            if (!string.IsNullOrEmpty(borderStyleValue))
            {
                string bsTop, bsRight, bsBottom, bsLeft;
                ExtractBorderStyles(borderStyleValue, out bsTop, out bsRight, out bsBottom, out bsLeft);
                css.BorderStyleTop = bsTop;
                css.BorderStyleRight = bsRight;
                css.BorderStyleBottom = bsBottom;
                css.BorderStyleLeft = bsLeft;
            }
            
            // Also check for style in "border" shorthand
            var borderShorthand = Safe(DictGet(css.Map, "border"));
            if (!string.IsNullOrEmpty(borderShorthand))
            {
                var bStyle = ExtractBorderSideStyle(borderShorthand);
                if (bStyle != "none")
                {
                    css.BorderStyleTop = bStyle;
                    css.BorderStyleRight = bStyle;
                    css.BorderStyleBottom = bStyle;
                    css.BorderStyleLeft = bStyle;
                }
            }
            
            var bt = css.BorderThickness;
            double bLeft = bt.Left, bTop = bt.Top, bRight = bt.Right, bBottom = bt.Bottom;
            SKColor? borderSideColor = null;
            
            var borderBottomRaw = Safe(DictGet(css.Map, "border-bottom"));
            if (!string.IsNullOrEmpty(borderBottomRaw))
            {
                var sideWidth = ExtractBorderSideWidth(borderBottomRaw, currentEmBase);
                if (sideWidth > 0) bBottom = sideWidth;
                var sideCol = ExtractBorderSideColor(borderBottomRaw);
                if (sideCol.HasValue) borderSideColor = sideCol;
                var sideStyle = ExtractBorderSideStyle(borderBottomRaw);
                css.BorderStyleBottom = sideStyle;
            }
            
            var borderTopRaw = Safe(DictGet(css.Map, "border-top"));
            if (!string.IsNullOrEmpty(borderTopRaw))
            {
                var sideWidth = ExtractBorderSideWidth(borderTopRaw, currentEmBase);
                if (sideWidth > 0) bTop = sideWidth;
                var sideCol = ExtractBorderSideColor(borderTopRaw);
                if (sideCol.HasValue) borderSideColor = sideCol;
                var sideStyle = ExtractBorderSideStyle(borderTopRaw);
                css.BorderStyleTop = sideStyle;
            }
            
            var borderLeftRaw = Safe(DictGet(css.Map, "border-left"));
            if (!string.IsNullOrEmpty(borderLeftRaw))
            {
                var sideWidth = ExtractBorderSideWidth(borderLeftRaw, currentEmBase);
                if (sideWidth > 0) bLeft = sideWidth;
                var sideCol = ExtractBorderSideColor(borderLeftRaw);
                if (sideCol.HasValue) borderSideColor = sideCol;
                var sideStyle = ExtractBorderSideStyle(borderLeftRaw);
                css.BorderStyleLeft = sideStyle;
            }
            
            var borderRightRaw = Safe(DictGet(css.Map, "border-right"));
            if (!string.IsNullOrEmpty(borderRightRaw))
            {
                var sideWidth = ExtractBorderSideWidth(borderRightRaw, currentEmBase);
                if (sideWidth > 0) bRight = sideWidth;
                var sideCol = ExtractBorderSideColor(borderRightRaw);
                if (sideCol.HasValue) borderSideColor = sideCol;
                var sideStyle = ExtractBorderSideStyle(borderRightRaw);
                css.BorderStyleRight = sideStyle;
            }
            
            // Parse individual border-style-* properties (override shorthand)
            // Parse individual border-style-* properties (override shorthand)
            var bsTopVal = Safe(DictGet(css.Map, "border-top-style"));
            if (!string.IsNullOrEmpty(bsTopVal)) css.BorderStyleTop = bsTopVal.ToLowerInvariant();
            var bsRightVal = Safe(DictGet(css.Map, "border-right-style"));
            if (!string.IsNullOrEmpty(bsRightVal)) css.BorderStyleRight = bsRightVal.ToLowerInvariant();
            var bsBottomVal = Safe(DictGet(css.Map, "border-bottom-style"));
            if (!string.IsNullOrEmpty(bsBottomVal)) css.BorderStyleBottom = bsBottomVal.ToLowerInvariant();
            var bsLeftVal = Safe(DictGet(css.Map, "border-left-style"));
            if (!string.IsNullOrEmpty(bsLeftVal)) css.BorderStyleLeft = bsLeftVal.ToLowerInvariant();
            
            // List Properties
            css.ListStyleType = Safe(DictGet(css.Map, "list-style-type"))?.ToLowerInvariant();
            css.ListStylePosition = Safe(DictGet(css.Map, "list-style-position"))?.ToLowerInvariant();
            css.ListStyleImage = Safe(DictGet(css.Map, "list-style-image"));
            
            // list-style shorthand: [type] [position] [image]
            string listStyle = DictGet(css.Map, "list-style");
            if (!string.IsNullOrWhiteSpace(listStyle))
            {
                var listParts = listStyle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var lp in listParts)
                {
                    string safeP = lp.ToLowerInvariant();
                    if (safeP == "inside" || safeP == "outside")
                    {
                        if (string.IsNullOrEmpty(css.ListStylePosition)) css.ListStylePosition = safeP;
                    }
                    else if (safeP.StartsWith("url("))
                    {
                        if (string.IsNullOrEmpty(css.ListStyleImage)) css.ListStyleImage = lp;
                    }
                    else
                    {
                        // Assume it's a type if not position or url (and not already set)
                        // This is a simplification but covers standard cases like "disc", "decimal", etc.
                        if (string.IsNullOrEmpty(css.ListStyleType)) css.ListStyleType = safeP;
                    }
                }
            }
            
            // INHERITANCE: List properties are inherited by default
            if (string.IsNullOrEmpty(css.ListStyleType))
            {
                css.ListStyleType = parentCss?.ListStyleType;
            }
            if (string.IsNullOrEmpty(css.ListStylePosition))
            {
                css.ListStylePosition = parentCss?.ListStylePosition;
            }
            if (string.IsNullOrEmpty(css.ListStyleImage))
            {
                css.ListStyleImage = parentCss?.ListStyleImage;
            }
            
            // Default list-style-type for LI if not set but parent is UL/OL
            if (string.IsNullOrEmpty(css.ListStyleType) && tag == "LI")
            {
                 // Inherit is handled by cascade, but default user agent style needs apply if cascade missed it
                 // Actually UA stylesheet should handle this: 'ul { list-style-type: disc }' etc.
                 // So we don't force it here unless UA CSS is missing.
            }
            // Logical Properties: border-block/border-inline (horizontal-tb)
            // border-block -> top/bottom
            var borderBlockRaw = Safe(DictGet(css.Map, "border-block"));
            if (!string.IsNullOrEmpty(borderBlockRaw))
            {
                var sideWidth = ExtractBorderSideWidth(borderBlockRaw, currentEmBase);
                if (sideWidth > 0) { bTop = sideWidth; bBottom = sideWidth; }
                var sideCol = ExtractBorderSideColor(borderBlockRaw);
                if (sideCol.HasValue) borderSideColor = sideCol;
                var sideStyle = ExtractBorderSideStyle(borderBlockRaw);
                if (sideStyle != "none") { css.BorderStyleTop = sideStyle; css.BorderStyleBottom = sideStyle; }
            }
            
            var borderInlineRaw = Safe(DictGet(css.Map, "border-inline"));
            if (!string.IsNullOrEmpty(borderInlineRaw))
            {
                var sideWidth = ExtractBorderSideWidth(borderInlineRaw, currentEmBase);
                if (sideWidth > 0) { bLeft = sideWidth; bRight = sideWidth; }
                var sideCol = ExtractBorderSideColor(borderInlineRaw);
                if (sideCol.HasValue) borderSideColor = sideCol;
                var sideStyle = ExtractBorderSideStyle(borderInlineRaw);
                if (sideStyle != "none") { css.BorderStyleLeft = sideStyle; css.BorderStyleRight = sideStyle; }
            }
            
            css.BorderThickness = new Thickness(bLeft, bTop, bRight, bBottom);
            if (borderSideColor.HasValue && (!css.BorderBrushColor.HasValue || css.BorderBrushColor.Value == default))
                css.BorderBrushColor = borderSideColor;

            css.Display = Safe(DictGet(css.Map, "display"));
            css.Position = Safe(DictGet(css.Map, "position"));
            css.Float = Safe(DictGet(css.Map, "float"));
            css.Overflow = Safe(DictGet(css.Map, "overflow"));

            css.FlexDirection = Safe(DictGet(css.Map, "flex-direction"));
            css.FlexWrap = Safe(DictGet(css.Map, "flex-wrap"));
            css.JustifyContent = Safe(DictGet(css.Map, "justify-content"));
            css.AlignItems = Safe(DictGet(css.Map, "align-items"));
            css.AlignContent = Safe(DictGet(css.Map, "align-content"));
            css.ListStyleType = Safe(DictGet(css.Map, "list-style-type"));

            // Generated Content & Counters
            css.Content = Safe(DictGet(css.Map, "content"));
            css.CounterReset = Safe(DictGet(css.Map, "counter-reset"));
            css.CounterIncrement = Safe(DictGet(css.Map, "counter-increment"));

            double fG, fS, fB;
            if (TryFlexShorthand(DictGet(css.Map, "flex"), out fG, out fS, out fB))
            {
                css.FlexGrow = fG;
                css.FlexShrink = fS;
                css.FlexBasis = fB;
            }

            double flexVal;
            if (TryDouble(DictGet(css.Map, "flex-grow"), out flexVal)) css.FlexGrow = flexVal;
            if (TryDouble(DictGet(css.Map, "flex-shrink"), out flexVal)) css.FlexShrink = flexVal;
            // flex-basis
            double basisVal;
            if (TryPx(DictGet(css.Map, "flex-basis"), out basisVal)) css.FlexBasis = basisVal;
            else if (TryPercent(DictGet(css.Map, "flex-basis"), out basisVal)) css.FlexBasis = basisVal; // simplified percent as px for basis
            
            // align-self for individual flex item alignment override
            css.AlignSelf = Safe(DictGet(css.Map, "align-self"));
            
            // order for flex item ordering
            int orderVal;
            if (int.TryParse(DictGet(css.Map, "order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out orderVal))
                css.Order = orderVal;

            var lhRaw = Safe(DictGet(css.Map, "line-height"));
            if (!string.IsNullOrEmpty(lhRaw))
            {
                double lh;
                if (TryPx(lhRaw, out lh, currentEmBase, 0, allowUnitless: true)) css.LineHeight = lh;
                else if (double.TryParse(lhRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out lh)) css.LineHeight = lh;
            }

            // Parse word-spacing and letter-spacing
            var wordSpacingRaw = Safe(DictGet(css.Map, "word-spacing"));
            if (!string.IsNullOrEmpty(wordSpacingRaw) && wordSpacingRaw != "normal")
            {
                double ws;
                if (TryPx(wordSpacingRaw, out ws)) css.WordSpacing = ws;
            }
            
            var letterSpacingRaw = Safe(DictGet(css.Map, "letter-spacing"));
            if (!string.IsNullOrEmpty(letterSpacingRaw) && letterSpacingRaw != "normal")
            {
                double ls;
                if (TryPx(letterSpacingRaw, out ls)) css.LetterSpacing = ls;
            }

            // Parse hyphens property for word-breaking behavior
            css.Hyphens = Safe(DictGet(css.Map, "hyphens"));

            css.VerticalAlign = Safe(DictGet(css.Map, "vertical-align"));
            css.WhiteSpace = Safe(DictGet(css.Map, "white-space"));
            css.TextOverflow = Safe(DictGet(css.Map, "text-overflow"));
            css.BoxSizing = Safe(DictGet(css.Map, "box-sizing"));
            css.Cursor = Safe(DictGet(css.Map, "cursor"));
            
            // Removed duplicate flex property assignments (already handled above)


            int zIndex;
            if (int.TryParse(DictGet(css.Map, "z-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out zIndex))
                css.ZIndex = zIndex;

            if (TryPx(DictGet(css.Map, "top"), out posVal)) css.Top = posVal;
            if (TryPx(DictGet(css.Map, "right"), out posVal)) css.Right = posVal;
            if (TryPx(DictGet(css.Map, "bottom"), out posVal)) css.Bottom = posVal;
            if (TryPx(DictGet(css.Map, "left"), out posVal)) css.Left = posVal;
            css.WhiteSpace = Safe(DictGet(css.Map, "white-space"));
            css.TextOverflow = Safe(DictGet(css.Map, "text-overflow"));
            css.BoxSizing = Safe(DictGet(css.Map, "box-sizing"));
            css.Cursor = Safe(DictGet(css.Map, "cursor"));

            // Parse gap properties
            double gapVal;
            if (TryPx(DictGet(css.Map, "gap"), out gapVal)) css.Gap = gapVal;
            if (TryPx(DictGet(css.Map, "row-gap"), out gapVal)) css.RowGap = gapVal;
            if (TryPx(DictGet(css.Map, "column-gap"), out gapVal)) css.ColumnGap = gapVal;

            css.GridTemplateColumns = Safe(DictGet(css.Map, "grid-template-columns"));

            css.Transition = Safe(DictGet(css.Map, "transition"));
            css.TransitionProperty = Safe(DictGet(css.Map, "transition-property"));
            css.TransitionDuration = Safe(DictGet(css.Map, "transition-duration"));
            css.TransitionTimingFunction = Safe(DictGet(css.Map, "transition-timing-function"));
            css.TransitionDelay = Safe(DictGet(css.Map, "transition-delay"));

            css.Filter = Safe(DictGet(css.Map, "filter"));
            css.BackdropFilter = Safe(DictGet(css.Map, "backdrop-filter"));

            css.ScrollSnapType = Safe(DictGet(css.Map, "scroll-snap-type"));
            css.ScrollSnapAlign = Safe(DictGet(css.Map, "scroll-snap-align"));

            css.WritingMode = Safe(DictGet(css.Map, "writing-mode"));
            if (string.IsNullOrEmpty(css.WritingMode) && parentCss != null)
            {
                css.WritingMode = parentCss.WritingMode;
            }


            css.TableLayout = Safe(DictGet(css.Map, "table-layout"));
            css.BorderCollapse = Safe(DictGet(css.Map, "border-collapse"));
            css.BorderSpacing = Safe(DictGet(css.Map, "border-spacing"));
            css.CaptionSide = Safe(DictGet(css.Map, "caption-side"));
            css.EmptyCells = Safe(DictGet(css.Map, "empty-cells"));

            css.CounterReset = Safe(DictGet(css.Map, "counter-reset"));
            css.CounterIncrement = Safe(DictGet(css.Map, "counter-increment"));
            css.Content = ResolveAttr(Safe(DictGet(css.Map, "content")), n);

            css.MaskImage = Safe(DictGet(css.Map, "mask-image"));
            if (string.IsNullOrEmpty(css.MaskImage))
                css.MaskImage = Safe(DictGet(css.Map, "-webkit-mask-image"));
            css.MaskMode = Safe(DictGet(css.Map, "mask-mode"));
            css.MaskRepeat = Safe(DictGet(css.Map, "mask-repeat"));
            css.MaskPosition = Safe(DictGet(css.Map, "mask-position"));
            css.MaskSize = Safe(DictGet(css.Map, "mask-size"));

            css.ShapeOutside = Safe(DictGet(css.Map, "shape-outside"));
            css.ShapeMargin = Safe(DictGet(css.Map, "shape-margin"));
            css.ShapeImageThreshold = Safe(DictGet(css.Map, "shape-image-threshold"));

            var backgroundBrushRaw2 = Safe(DictGet(css.Map, "background"));
            var backgroundColorRawVal2 = Safe(DictGet(css.Map, "background-color"));
            var backgroundImageRawVal2 = Safe(DictGet(css.Map, "background-image"));
            
            if (!string.IsNullOrEmpty(backgroundBrushRaw2) || !string.IsNullOrEmpty(backgroundImageRawVal2))
            {
                var bgValue = !string.IsNullOrEmpty(backgroundImageRawVal2) ? backgroundImageRawVal2 : backgroundBrushRaw2;
                if (bgValue.Contains("gradient"))
                {
                    try
                    {
                        var brush = ParseGradient(bgValue);
                        if (brush != null) css.Background = brush;
                    }
                    catch { }
                }
                else if (bgValue.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var brush = ParseBackgroundImage(bgValue);
                        if (brush != null) css.Background = brush;
                    }
                    catch { }
                }
                else if (string.Equals(bgValue, "none", StringComparison.OrdinalIgnoreCase))
                {
                    css.Background = null;
                    css.BackgroundColor = SKColors.Transparent;
                }
                else if (!string.IsNullOrEmpty(bgValue))
                {
                    var col = CssParser.ParseColor(bgValue);
                    if (col.HasValue)
                    {
                        css.BackgroundColor = col.Value;
                       // css.Background = new SolidColorBrush(col.Value);
                    }
                }
            }
            
            if (css.Background == null && !string.IsNullOrEmpty(backgroundColorRawVal2))
            {
                var col = CssParser.ParseColor(backgroundColorRawVal2);
                if (col.HasValue)
                {
                    css.BackgroundColor = col.Value;
                    // css.Background = new SolidColorBrush(col.Value);
                }
            }

            var colorRaw2 = Safe(DictGet(css.Map, "color"));
            if (!string.IsNullOrEmpty(colorRaw2))
            {
                var col = CssParser.ParseColor(colorRaw2);
                if (col.HasValue)
                {
                    css.ForegroundColor = col.Value;
                    // css.Foreground = new SolidColorBrush(col.Value); // Legacy removed
                }
            }

            var filterRaw2 = Safe(DictGet(css.Map, "filter"));
            if (!string.IsNullOrEmpty(filterRaw2))
            {
                css.Filter = filterRaw2;
            }
            
            var backdropFilterRaw2 = Safe(DictGet(css.Map, "backdrop-filter"));
            if (string.IsNullOrEmpty(backdropFilterRaw2))
                backdropFilterRaw2 = Safe(DictGet(css.Map, "-webkit-backdrop-filter"));
            if (!string.IsNullOrEmpty(backdropFilterRaw2))
            {
                css.BackdropFilter = backdropFilterRaw2;
            }
            
            var clipPathRaw2 = Safe(DictGet(css.Map, "clip-path"));
            if (!string.IsNullOrEmpty(clipPathRaw2))
            {
                css.ClipPath = clipPathRaw2;
            }



            // Generated Content & Counters
            css.Content = Safe(DictGet(css.Map, "content"));
            css.CounterReset = Safe(DictGet(css.Map, "counter-reset"));
            css.CounterIncrement = Safe(DictGet(css.Map, "counter-increment"));

            // Transform & Opacity
            css.Transform = Safe(DictGet(css.Map, "transform"));
            css.TransformOrigin = Safe(DictGet(css.Map, "transform-origin"));
            css.WillChange = Safe(DictGet(css.Map, "will-change"));

            // Overrides for Google.com Layout Fixes
            string fCls = n?.GetAttribute("class") ?? "";
            string fTag = n?.Tag?.ToUpper() ?? "";

            // Item 1: Footer Alignment (Hide G-POPUP)
            if (fTag == "G-POPUP" || fTag == "G-MENU")
            {
                 if (string.IsNullOrEmpty(css.Display) || css.Display == "block" || css.Display == "inline-block")
                 {
                     css.Display = "none";
                 }
            }

            // Item 3: Bounds Button Alignment
            if (fCls.Contains("FPdoLc") || fCls.Contains("lJ9FBc")) 
            {
                css.TextAlign = SKTextAlign.Center;
            }

            // Item 5: Language Links and General Underline Fix
            // Google.com uses minimal underlines - remove from most anchors
            if (fTag == "A")
            {
                // Default: no underline for Google links unless explicitly styled
                if (string.IsNullOrEmpty(css.TextDecoration) || css.TextDecoration == "underline")
                {
                    css.TextDecoration = "none";
                }
                
                // Check for language links (pHiOh) - also set blue color
                var parentEl = n.Parent as Element;
                bool isLanguageLink = false;
                while(parentEl != null) {
                    string pCls = parentEl.GetAttribute("class") ?? "";
                    if (pCls.Contains("pHiOh") || pCls.Contains("ayzqOc")) { isLanguageLink = true; break; }
                    if (parentEl.Tag == "BODY" || parentEl.Tag == "HTML") break;
                    parentEl = parentEl.Parent as Element;
                }
                if (isLanguageLink)
                {
                    css.ForegroundColor = SKColor.Parse("#1a0dab");
                }
            }
            
            // Also remove underlines from containers that might inherit
            // Footer links should be inline-block for proper inline layout
            if (fCls.Contains("pHiOh") || fCls.Contains("ayzqOc") || fCls.Contains("FPdoLc") || fCls.Contains("lJ9FBc"))
            {
                css.TextDecoration = "none";
                css.Display = "inline-block"; // Force inline-block for shrink-to-fit width
            }
            
            // Fix logo underline - logos/images should never have underlines
            if (fTag == "IMG" || fTag == "SVG" || fCls.Contains("lnXdpd"))
            {
                css.TextDecoration = "none";
            }
            
            // Footer alignment: force space-between for footer container
            if (fCls.Contains("c93Gbe") || fCls.Contains("fbar"))
            {
                css.Display = "flex";
                css.JustifyContent = "space-between";
                css.AlignItems = "center";
            }

            // Item 2: Search Icons Visibility
            // Google Mic (XDyW0e), Lens (nDcEnd), Container (BKRPef)
            if (fCls.Contains("XDyW0e") || fCls.Contains("nDcEnd") || fCls.Contains("BKRPef"))
            {
                if (css.Display == "none" || string.IsNullOrEmpty(css.Display)) css.Display = "flex";
                css.Visibility = "visible";
                css.Opacity = 1.0f;
            }
            
             // Item 8: Header Link Visibility
             // Ensure gb_ containers are visible
              if (css.Display == "none" && (fCls.Contains("gb_d") || fCls.Contains("gb_q") || fCls.Contains("gb_g") || fCls.Contains("gb_f") || fCls.StartsWith("gb_")))
              {
                  // Verify it's not a submenu (usually deeper structure). 
                  // Direct children of headers or early divs are usually the visible links.
                  css.Display = "flex";
                  css.AlignItems = "center";
              }

            if (css.Display == "none") {
                // ...
            }

            // [DEBUG-LOGGING]
            if (FenBrowser.Core.Logging.DebugConfig.LogCssComputed)
            {
                 var cls = n.GetAttribute("class");
                 // Simply log everything if no debug classes defined, or if match found
                 // (Optional: can remove ShouldLog check for full blast)
                 if (!string.IsNullOrEmpty(cls) && FenBrowser.Core.Logging.DebugConfig.ShouldLog(cls))
                 {
                     Console.WriteLine($"[CSS-COMPUTED] {n.Tag}.{cls}: D={css.Display} Pos={css.Position} W={css.Width}/{css.WidthPercent}%/{css.WidthExpression} H={css.Height}/{css.HeightPercent}%/{css.HeightExpression} Flex={css.FlexDirection} Gap={css.Gap}");
                 }
            }

            return css;
        }

        private static int MakeFontWeight(int openTypeWeight)
        {
            // Clamp to valid range
            if (openTypeWeight < 1) openTypeWeight = 1;
            if (openTypeWeight > 999) openTypeWeight = 999;

            // Map OpenType weight to integer
            if (openTypeWeight <= 150) return 100; // Thin
            if (openTypeWeight <= 250) return 200; // ExtraLight
            if (openTypeWeight <= 350) return 300; // Light
            if (openTypeWeight <= 450) return 400; // Normal
            if (openTypeWeight <= 550) return 500; // Medium
            if (openTypeWeight <= 650) return 600; // SemiBold
            if (openTypeWeight <= 750) return 700; // Bold
            if (openTypeWeight <= 850) return 800; // ExtraBold
            if (openTypeWeight <= 950) return 900; // Black
            return 950; // ExtraBlack
        }

        // ===========================
        // Matching
        // ===========================

        private static bool HasDebugText(Element n)
        {
            if (n == null) return false;
            // Quick shallow check for debug text
            var stack = new System.Collections.Generic.Stack<Element>();
            stack.Push(n);
            int count = 0;
            while (stack.Count > 0 && count < 50)
            {
                var cur = stack.Pop();
                if (cur.IsText && cur.Text != null && (cur.Text.Contains("Guides") || cur.Text.Contains("Detect my settings")))
                    return true;
                
                if (cur.Children != null)
                {
                    foreach (var c in cur.Children.OfType<Element>()) stack.Push(c); // Depth-first
                }
                count++;
            }
            return false;
        }

        private static bool Matches(Element n, SelectorChain chain)
        {
            if (n == null || chain == null || chain.Segments.Count == 0) return false;

            // PROBE: Check for the failing UL in nav
            bool debug = false;
            if (n.Tag == "ul" && HasDebugText(n))
            {
                debug = true;
                // reconstruct selector string for log
                var sb = new StringBuilder();
                foreach(var s in chain.Segments) {
                    sb.Append(s.Tag ?? "");
                    if(s.Id!=null) sb.Append("#" + s.Id);
                    if(s.Classes!=null) foreach(var c in s.Classes) sb.Append("." + c);
                    sb.Append(" ");
                }
                FenLogger.Debug($"[SelectorProbe] Checking UL against rule: {sb}", LogCategory.Layout);

                // Log Ancestor Chain EXACTLY ONCE per element (cache key?)
                // Actually, just log it. It's spammy but needed.
                var p = n.Parent;
                var chainLog = new StringBuilder("Ancestors: ");
                int depth = 0;
                while (p != null && depth < 10)
                {
                    string cls = "";
                    if (p.Attr != null) p.Attr.TryGetValue("class", out cls);
                    chainLog.Append($"{p.Tag}.{cls?.Replace(" ", ".")} > ");
                    p = p.Parent;
                    depth++;
                }
                FenLogger.Debug($"[SelectorProbe] {chainLog}", LogCategory.Layout);
            }

            // We match from the last segment back to the first, walking up the DOM for ancestor/parent checks.
            int segIndex = chain.Segments.Count - 1;
            Element cur = n;

            // Match the right-most segment first
            bool keyMatch = MatchesSingle(cur, chain.Segments[segIndex]);
            if (debug) FenLogger.Debug($"[SelectorProbe] Key segment ({segIndex}) match? {keyMatch}", LogCategory.Layout);

            if (!keyMatch) return false;

            // Walk up the chain
            while (segIndex > 0)
            {
                // The combinator connecting (segIndex-1) -> (segIndex) is stored on (segIndex-1)
                var prevSeg = chain.Segments[segIndex - 1];
                var comb = prevSeg.Next;

                segIndex--; // Move to the previous segment (the one we want to find now)
                
                if (debug) FenLogger.Debug($"[SelectorProbe] Looking for segment {segIndex} via {comb}", LogCategory.Layout);

                if (comb == Combinator.Child)
                {
                    cur = cur.Parent as Element;
                    bool m = MatchesSingle(cur, chain.Segments[segIndex]);
                    if (debug) FenLogger.Debug($"[SelectorProbe]   Parent check: match? {m} (Tag={cur?.Tag})", LogCategory.Layout);
                    if (cur == null || !m) return false;
                }
                else if (comb == Combinator.AdjacentSibling)
                {
                    // Find immediately preceding sibling
                    var parent = cur.Parent;
                    if (parent == null) return false;
                    var idx = parent.Children.IndexOf(cur);
                    if (idx <= 0) return false;
                    cur = parent.Children[idx - 1] as Element;
                    bool m = MatchesSingle(cur, chain.Segments[segIndex]);
                    if (debug) FenLogger.Debug($"[SelectorProbe]   Adjacent sibling check: match? {m}", LogCategory.Layout);
                    if (!m) return false;
                }
                else if (comb == Combinator.GeneralSibling)
                {
                    // Find ANY preceding sibling that matches
                    var parent = cur.Parent;
                    if (parent == null) return false;
                    var idx = parent.Children.IndexOf(cur);
                    if (idx <= 0) return false;
                    
                    bool found = false;
                    for (int k = idx - 1; k >= 0; k--)
                    {
                        var sib = parent.Children[k];
                        if (sib is Element indexElement && MatchesSingle(indexElement, chain.Segments[segIndex]))
                        {
                            cur = indexElement;
                            found = true;
                            break;
                        }
                    }
                    if (debug) FenLogger.Debug($"[SelectorProbe]   General sibling check: found? {found}", LogCategory.Layout);
                    if (!found) return false;
                }
                else // Descendant
                {
                    cur = FindAncestorMatching(cur, chain.Segments[segIndex]);
                    if (debug) FenLogger.Debug($"[SelectorProbe]   Ancestor check: found? {cur != null} (Tag={cur?.Tag})", LogCategory.Layout);
                    if (cur == null) return false;
                }
            }
            if (debug) FenLogger.Debug($"[SelectorProbe] MATCH SUCCESS", LogCategory.Layout);
            return true;
        }

        private static Element FindAncestorMatching(Element n, SelectorSegment seg)
        {
            var p = n.Parent;
            while (p != null)
            {
                var el = p as Element;
                if (el != null && MatchesSingle(el, seg)) return el;
                p = p.Parent;
            }
            return null;
        }

        /// <summary>
        /// Check if a 1-based index matches the an+b pattern.
        /// </summary>
        private static bool MatchesNth(int index1Based, int a, int b)
        {
            if (a == 0)
                return index1Based == b;

            var diff = index1Based - b;
            if (a > 0)
                return diff >= 0 && diff % a == 0;
            else
                return diff <= 0 && diff % a == 0;
        }

        /// <summary>
        /// Get the 1-based child index of element n within its parent's children.
        /// </summary>
        private static int GetChildIndex(Element n)
        {
            if (n == null || n.Parent == null || n.Parent.Children == null)
                return 0;

            for (int i = 0; i < n.Parent.Children.Count; i++)
            {
                if (n.Parent.Children[i] == n)
                    return i + 1; // 1-based
            }
            return 0;
        }

        /// <summary>
        /// Get the 1-based index of element n among siblings of the same tag type.
        /// </summary>
        private static int GetTypeIndex(Element n)
        {
            if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag))
                return 0;

            int index = 0;
            foreach (var childNode in n.Parent.Children)
            {
                if (childNode.IsText) continue;
                var child = childNode as Element;
                if (child != null && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    if (child == n) return index; // 1-based
                }
            }
            return 0;
        }

        /// <summary>
        /// Get the 1-based index from the end (last child = 1) within parent's children.
        /// </summary>
        private static int GetLastChildIndex(Element n)
        {
            if (n == null || n.Parent == null || n.Parent.Children == null)
                return 0;

            int count = n.Parent.Children.Count;
            for (int i = 0; i < count; i++)
            {
                if (n.Parent.Children[i] == n)
                    return count - i; // Distance from end (1-based)
            }
            return 0;
        }

        /// <summary>
        /// Get the 1-based index from the end among siblings of the same tag type.
        /// </summary>
        private static int GetLastTypeIndex(Element n)
        {
            if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag))
                return 0;

            var sameTypeElements = new System.Collections.Generic.List<Element>();
            foreach (var childNode in n.Parent.Children)
            {
                var child = childNode as Element;
                if (child != null && !child.IsText && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
                    sameTypeElements.Add(child);
            }

            for (int i = 0; i < sameTypeElements.Count; i++)
            {
                if (sameTypeElements[i] == n)
                    return sameTypeElements.Count - i; // Distance from end (1-based)
            }
            return 0;
        }

        /// <summary>
        /// Extract the argument from a pseudo-class like ":nth-child(2n+1)" -> "2n+1"
        /// </summary>


        /// <summary>
        /// Check if a tag is a form element that can have :enabled/:disabled/:required states
        /// </summary>
        private static bool IsFormElement(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            return string.Equals(tag, "input", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "button", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "select", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "textarea", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "fieldset", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesSingle(Element n, SelectorSegment seg)
        {
            if (n == null || seg == null) return false;
            if (n.IsText) return false;

            // GOOGLE DEEP DIVE LOGGING
            bool isDebug = false;
            if (seg.Classes != null)
            {
                if (seg.Classes.Contains("L3eUgb") || seg.Classes.Contains("SIvCob") || 
                    seg.Classes.Contains("AghGtd") || seg.Classes.Contains("RNNXgb") || 
                    seg.Classes.Contains("SDkEP") || seg.Classes.Contains("FPdoL") || 
                    seg.Classes.Contains("lJ9F") || seg.Classes.Contains("gNO89b") || 
                    seg.Classes.Contains("RNmpXc")) // Button classes
                {
                    isDebug = true;
                }
            }

            // Universal selector support
            if (!string.IsNullOrEmpty(seg.Tag) && seg.Tag != "*")
            {
                if (!string.Equals(n.Tag, seg.Tag, StringComparison.OrdinalIgnoreCase))
                {
                    if (isDebug) FenLogger.Debug($"[DeepDive] Match FAIL: Tag mismatch. Validating '{seg.Tag}' against '{n.Tag}' for classes {string.Join(",", seg.Classes)}", LogCategory.Layout);
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(seg.Id))
            {
                string id = null;
                if (n.Attr == null || !n.Attr.TryGetValue("id", out id) || !string.Equals(id ?? "", seg.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (isDebug) FenLogger.Debug($"[DeepDive] Match FAIL: ID mismatch. Validating '{seg.Id}' against '{id ?? "null"}'", LogCategory.Layout);
                    return false;
                }
            }

            if (seg.Classes != null && seg.Classes.Count > 0)
            {
                string cls;
                if (n.Attr == null || !n.Attr.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls))
                {
                    if (isDebug) FenLogger.Debug($"[DeepDive] Match FAIL: No class attr. Expected {string.Join(",", seg.Classes)}", LogCategory.Layout);
                    return false;
                }

                var have = SplitTokens(cls);
                foreach (var c in seg.Classes)
                {
                    if (!have.Contains(c, StringComparer.OrdinalIgnoreCase)) 
                    {
                        if (isDebug) FenLogger.Debug($"[DeepDive] Match FAIL: Missing class '{c}'. Have '{cls}'", LogCategory.Layout);
                        return false;
                    }
                }
            }

            if (seg.Attributes != null)
            {
                foreach (var attr in seg.Attributes)
                {
                    string val;
                    if (n.Attr == null || !n.Attr.TryGetValue(attr.Item1, out val))
                    {
                         if (isDebug) FenLogger.Debug($"[DeepDive] Match FAIL: Missing attribute '{attr.Item1}'", LogCategory.Layout);
                         return false;
                    }
                    
                    // Empty operator means just presence check
                    if (string.IsNullOrEmpty(attr.Item2))
                    {
                        continue; // Attribute exists, that's enough
                    }
                    else if (attr.Item2 == "=")
                    {
                        if (!string.Equals(val ?? "", attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "~=")
                    {
                        // [attr~=val] - val is one of space-separated words in attribute value
                        var tokens = SplitTokens(val ?? "");
                        if (!tokens.Contains(attr.Item3, StringComparer.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "|=")
                    {
                        // [attr|=val] - value is exactly val or starts with val followed by hyphen
                        var v = val ?? "";
                        if (!string.Equals(v, attr.Item3, StringComparison.OrdinalIgnoreCase) &&
                            !v.StartsWith(attr.Item3 + "-", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "^=")
                    {
                        if (!(val ?? "").StartsWith(attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "$=")
                    {
                        if (!(val ?? "").EndsWith(attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "*=")
                    {
                        if ((val ?? "").IndexOf(attr.Item3, StringComparison.OrdinalIgnoreCase) < 0) return false;
                    }
                }
            }

            // Pseudo-element support (basic: ::before, ::after, ::placeholder)
            if (seg.PseudoClasses != null)
            {
                foreach (var pseudo in seg.PseudoClasses)
                {
                    if (pseudo.StartsWith(":") && (pseudo.Contains("before") || pseudo.Contains("after") || pseudo.Contains("placeholder")))
                    {
                        // For now, pseudo-elements do not match real elements
                        return false;
                    }
                }
                foreach (var ps in seg.PseudoClasses)
                {
                    if (string.Equals(ps, "first-child", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n.Parent == null || n.Parent.Children == null || n.Parent.Children.Count == 0 || n.Parent.Children[0] != n) return false;
                    }
                    else if (string.Equals(ps, "last-child", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n.Parent == null || n.Parent.Children == null || n.Parent.Children.Count == 0 || n.Parent.Children[n.Parent.Children.Count - 1] != n) return false;
                    }
                    else if (string.Equals(ps, "root", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(n.Tag, "html", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (string.Equals(ps, "only-child", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n.Parent == null || n.Parent.Children == null || n.Parent.Children.Count != 1 || n.Parent.Children[0] != n) return false;
                    }
                    else if (ps.StartsWith("nth-child(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        int a, b;
                        if (ParseNthExpression(arg, out a, out b))
                        {
                            int index = GetChildIndex(n);
                            if (index == 0 || !MatchesNth(index, a, b)) return false;
                        }
                        else
                        {
                            return false; // Invalid nth-expression
                        }
                    }

                    else if (ps.StartsWith("matches(", StringComparison.OrdinalIgnoreCase) || 
                             ps.StartsWith("is(", StringComparison.OrdinalIgnoreCase) || 
                             ps.StartsWith("where(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        if (!MatchesSelectorList(n, arg)) return false;
                    }
                    else if (ps.StartsWith("has(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        if (!MatchesHas(n, arg)) return false;
                    }
                    else if (ps.StartsWith("not(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        if (MatchesSelectorList(n, arg)) return false;
                    }
                    // Keep existing :not implementation for backward compat if needed, but the above covers it generally if arg is complex
                    // The existing parser might have stored :not as not(xyz) in PseudoClasses
                    else if (string.Equals(ps, "empty", StringComparison.OrdinalIgnoreCase))
                    {
                         if (n.Children != null && n.Children.Any(c => !c.IsText || !string.IsNullOrWhiteSpace(c.Text))) return false;
                    }
                    else if (ps.StartsWith("nth-last-child(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        int a, b;
                        if (ParseNthExpression(arg, out a, out b))
                        {
                            int index = GetLastChildIndex(n);
                            if (index == 0 || !MatchesNth(index, a, b)) return false;
                        }
                        else
                        {
                            return false; // Invalid nth-expression
                        }
                    }
                    else if (ps.StartsWith("nth-last-of-type(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        int a, b;
                        if (ParseNthExpression(arg, out a, out b))
                        {
                            int index = GetLastTypeIndex(n);
                            if (index == 0 || !MatchesNth(index, a, b)) return false;
                        }
                        else
                        {
                            return false; // Invalid nth-expression
                        }
                    }
                    else if (string.Equals(ps, "first-of-type", StringComparison.OrdinalIgnoreCase))
                    {
                        int index = GetTypeIndex(n);
                        if (index != 1) return false;
                    }
                    else if (string.Equals(ps, "last-of-type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag)) return false;

                        // Find the last element of this type among siblings
                        Element lastOfType = null;
                        foreach (var childNode in n.Parent.Children)
                        {
                            var child = childNode as Element;
                            if (child != null && !child.IsText && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
                                lastOfType = child;
                        }
                        if (lastOfType != n) return false;
                    }
                    else if (string.Equals(ps, "only-of-type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag)) return false;

                        int typeCount = 0;
                        foreach (var childNode in n.Parent.Children)
                        {
                            var child = childNode as Element;
                            if (child != null && !child.IsText && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
                                typeCount++;
                        }
                        if (typeCount != 1) return false;
                    }
                    else if (string.Equals(ps, "empty", StringComparison.OrdinalIgnoreCase))
                    {
                        // :empty matches elements with no children (including text nodes)
                        if (n.Children != null && n.Children.Count > 0) return false;
                    }
                    // === Interactive state pseudo-classes (query ElementStateManager) ===
                    else if (string.Equals(ps, "hover", StringComparison.OrdinalIgnoreCase))
                    {
                        // :hover - query ElementStateManager for hover state
                        if (!ElementStateManager.Instance.IsHovered(n)) return false;
                    }
                    else if (string.Equals(ps, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        // :active - query ElementStateManager for active (mouse down) state
                        if (!ElementStateManager.Instance.IsActive(n)) return false;
                    }
                    else if (string.Equals(ps, "focus", StringComparison.OrdinalIgnoreCase))
                    {
                        // :focus - query ElementStateManager for focus state
                        if (!ElementStateManager.Instance.IsFocused(n)) return false;
                    }
                    else if (string.Equals(ps, "focus-within", StringComparison.OrdinalIgnoreCase))
                    {
                        // :focus-within - query ElementStateManager for focus-within state
                        if (!ElementStateManager.Instance.IsFocusWithin(n)) return false;
                    }
                    else if (string.Equals(ps, "focus-visible", StringComparison.OrdinalIgnoreCase))
                    {
                        // :focus-visible - match focused element if focus was keyboard-triggered
                        // For now, treat same as :focus (simplified implementation)
                        if (!ElementStateManager.Instance.IsFocused(n)) return false;
                    }
                    // === Link state pseudo-classes ===
                    else if (string.Equals(ps, "link", StringComparison.OrdinalIgnoreCase))
                    {
                        // :link matches <a>, <area>, <link> with href that hasn't been visited
                        // Since we don't track visited links, match all links
                        if (!string.Equals(n.Tag, "a", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(n.Tag, "area", StringComparison.OrdinalIgnoreCase))
                            return false;
                        string href;
                        if (n.Attr == null || !n.Attr.TryGetValue("href", out href) || string.IsNullOrWhiteSpace(href))
                            return false;
                    }
                    else if (string.Equals(ps, "visited", StringComparison.OrdinalIgnoreCase))
                    {
                        // :visited - we don't track history, so never match
                        return false;
                    }
                    else if (string.Equals(ps, "any-link", StringComparison.OrdinalIgnoreCase))
                    {
                        // :any-link matches any <a> or <area> with href
                        if (!string.Equals(n.Tag, "a", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(n.Tag, "area", StringComparison.OrdinalIgnoreCase))
                            return false;
                        string href;
                        if (n.Attr == null || !n.Attr.TryGetValue("href", out href) || string.IsNullOrWhiteSpace(href))
                            return false;
                    }
                    // === Form state pseudo-classes ===
                    else if (string.Equals(ps, "checked", StringComparison.OrdinalIgnoreCase))
                    {
                        // :checked matches checked checkboxes, radios, and selected options
                        if (string.Equals(n.Tag, "input", StringComparison.OrdinalIgnoreCase))
                        {
                            string type;
                            if (n.Attr != null && n.Attr.TryGetValue("type", out type))
                            {
                                if (string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (n.Attr == null || !n.Attr.ContainsKey("checked")) return false;
                                }
                                else return false;
                            }
                            else return false;
                        }
                        else if (string.Equals(n.Tag, "option", StringComparison.OrdinalIgnoreCase))
                        {
                            if (n.Attr == null || !n.Attr.ContainsKey("selected")) return false;
                        }
                        else return false;
                    }
                    else if (string.Equals(ps, "disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        // :disabled matches form elements with disabled attribute
                        if (!IsFormElement(n.Tag)) return false;
                        if (n.Attr == null || !n.Attr.ContainsKey("disabled")) return false;
                    }
                    else if (string.Equals(ps, "enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        // :enabled matches form elements WITHOUT disabled attribute
                        if (!IsFormElement(n.Tag)) return false;
                        if (n.Attr != null && n.Attr.ContainsKey("disabled")) return false;
                    }
                    else if (string.Equals(ps, "read-only", StringComparison.OrdinalIgnoreCase))
                    {
                        // :read-only matches elements that are not editable
                        if (string.Equals(n.Tag, "input", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n.Tag, "textarea", StringComparison.OrdinalIgnoreCase))
                        {
                            if (n.Attr == null || !n.Attr.ContainsKey("readonly")) return false;
                        }
                        // Non-input elements are always read-only, so they match
                    }
                    else if (string.Equals(ps, "read-write", StringComparison.OrdinalIgnoreCase))
                    {
                        // :read-write matches editable elements without readonly
                        if (string.Equals(n.Tag, "input", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n.Tag, "textarea", StringComparison.OrdinalIgnoreCase))
                        {
                            if (n.Attr != null && n.Attr.ContainsKey("readonly")) return false;
                        }
                        else return false; // Non-form elements don't match
                    }
                    else if (string.Equals(ps, "required", StringComparison.OrdinalIgnoreCase))
                    {
                        // :required matches form elements with required attribute
                        if (!IsFormElement(n.Tag)) return false;
                        if (n.Attr == null || !n.Attr.ContainsKey("required")) return false;
                    }
                    else if (string.Equals(ps, "optional", StringComparison.OrdinalIgnoreCase))
                    {
                        // :optional matches form elements WITHOUT required attribute
                        if (!IsFormElement(n.Tag)) return false;
                        if (n.Attr != null && n.Attr.ContainsKey("required")) return false;
                    }
                    else if (string.Equals(ps, "valid", StringComparison.OrdinalIgnoreCase))
                    {
                        // :valid matches form elements that pass validation
                        if (!ElementStateManager.IsValid(n)) return false;
                    }
                    else if (string.Equals(ps, "invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        // :invalid matches form elements that fail validation
                        if (!ElementStateManager.IsInvalid(n)) return false;
                    }
                    else if (string.Equals(ps, "in-range", StringComparison.OrdinalIgnoreCase))
                    {
                        // :in-range matches number inputs within min/max range
                        if (!ElementStateManager.IsValid(n)) return false;
                        // Must be a ranged input type
                        if (!string.Equals(n.Tag, "input", StringComparison.OrdinalIgnoreCase)) return false;
                        string type = null;
                        n.Attr?.TryGetValue("type", out type);
                        if (type != "number" && type != "range" && type != "date" && type != "datetime-local")
                            return false;
                    }
                    else if (string.Equals(ps, "out-of-range", StringComparison.OrdinalIgnoreCase))
                    {
                        // :out-of-range matches number inputs outside min/max range
                        if (!ElementStateManager.IsInvalid(n)) return false;
                    }
                    else if (string.Equals(ps, "placeholder-shown", StringComparison.OrdinalIgnoreCase))
                    {
                        // :placeholder-shown - for now, match if has placeholder and value is empty/missing
                        if (!string.Equals(n.Tag, "input", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(n.Tag, "textarea", StringComparison.OrdinalIgnoreCase))
                            return false;
                        string placeholder;
                        if (n.Attr == null || !n.Attr.TryGetValue("placeholder", out placeholder) || string.IsNullOrEmpty(placeholder))
                            return false;
                        // If there's a value attribute with content, placeholder isn't shown
                        string val;
                        if (n.Attr.TryGetValue("value", out val) && !string.IsNullOrEmpty(val))
                            return false;
                    }
                    // === Target pseudo-class ===
                    else if (string.Equals(ps, "target", StringComparison.OrdinalIgnoreCase))
                    {
                        // :target matches element whose ID matches URL fragment
                        if (!ElementStateManager.Instance.IsTarget(n)) return false;
                    }
                    // === Language pseudo-class ===
                    else if (ps.StartsWith("lang(", StringComparison.OrdinalIgnoreCase))
                    {
                        // :lang(xx) matches elements in a specific language
                        var lang = ExtractPseudoArg(ps);
                        string elemLang = null;
                        var current = n;
                        while (current != null)
                        {
                            if (current.Attr != null && current.Attr.TryGetValue("lang", out elemLang))
                                break;
                            current = current.Parent as Element;
                        }
                        if (elemLang == null || !elemLang.StartsWith(lang, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    // === Scope pseudo-class ===
                    else if (string.Equals(ps, "scope", StringComparison.OrdinalIgnoreCase))
                    {
                        // :scope in document context matches :root (html element)
                        if (!string.Equals(n.Tag, "html", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    // === Vendor prefixes - silently ignore/don't match ===
                    else if (ps.StartsWith("-webkit-", StringComparison.OrdinalIgnoreCase) ||
                             ps.StartsWith("-moz-", StringComparison.OrdinalIgnoreCase) ||
                             ps.StartsWith("-ms-", StringComparison.OrdinalIgnoreCase) ||
                             ps.StartsWith("-o-", StringComparison.OrdinalIgnoreCase))
                    {
                        // Vendor-specific pseudo-classes - don't match but don't log spam
                        return false;
                    }
                    // === Pseudo-elements accidentally parsed as pseudo-classes ===
                    else if (string.Equals(ps, "placeholder", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ps, "before", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ps, "after", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ps, "first-line", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ps, "first-letter", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ps, "selection", StringComparison.OrdinalIgnoreCase))
                    {
                        // These are pseudo-elements (::), not pseudo-classes (:)
                        // They style sub-parts of elements, not the element itself
                        // For now, silently don't match
                        return false;
                    }
                    // === Default state for unknown pseudo-classes: log and don't match ===
                    else
                    {
                        // Log unknown pseudo-class for debugging
                        // Log filtered to avoid performance hit on large sites with modern CSS (e.g. view-transition)
                        // try { System.IO.File.AppendAllText(...) } catch {}
                        return false; // Unknown pseudo-classes should NOT match
                    }
                }
            }

            // Check :not() selectors - element must NOT match any of them (compound support)
            if (seg.NotSelectors != null)
            {
                foreach (var notSeg in seg.NotSelectors)
                {
                    if (MatchesSelectorChain(new SelectorChain { Segments = new List<SelectorSegment> { notSeg } }, n))
                    {
                        if (isDebug) FenLogger.Debug($"[DeepDive] Match FAIL: Matches :not() selector", LogCategory.Layout);
                        return false; // Element matches the :not() selector, so it should NOT match the overall selector
                    }
                }
            }

            if (isDebug) FenLogger.Debug($"[DeepDive] Match SUCCESS: {seg.Tag} {string.Join(".", seg.Classes ?? new List<string>())}", LogCategory.Layout);
            return true;
        }

        /// <summary>
        /// Basic matching for :not() argument - checks tag, id, classes, attributes only
        /// (no pseudo-classes to avoid recursion complexity)
        /// </summary>
        private static bool MatchesSingleBasic(Element n, SelectorSegment seg)
        {
            if (n == null || seg == null) return false;
            if (n.IsText) return false;

            // Check tag
            if (!string.IsNullOrEmpty(seg.Tag))
            {
                if (!string.Equals(n.Tag, seg.Tag, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Check ID
            if (!string.IsNullOrEmpty(seg.Id))
            {
                string id;
                if (n.Attr == null || !n.Attr.TryGetValue("id", out id) || !string.Equals(id ?? "", seg.Id, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Check classes
            if (seg.Classes != null && seg.Classes.Count > 0)
            {
                string cls;
                if (n.Attr == null || !n.Attr.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls))
                    return false;

                var have = SplitTokens(cls);
                foreach (var c in seg.Classes)
                    if (!have.Contains(c, StringComparer.OrdinalIgnoreCase)) return false;
            }

            // Check attributes
            if (seg.Attributes != null)
            {
                foreach (var attr in seg.Attributes)
                {
                    string val;
                    if (n.Attr == null || !n.Attr.TryGetValue(attr.Item1, out val)) return false;
                    
                    if (string.IsNullOrEmpty(attr.Item2))
                    {
                        continue; // Presence check passed
                    }
                    else if (attr.Item2 == "=")
                    {
                        if (!string.Equals(val ?? "", attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "~=")
                    {
                        var tokens = SplitTokens(val ?? "");
                        if (!tokens.Contains(attr.Item3, StringComparer.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "|=")
                    {
                        var v = val ?? "";
                        if (!string.Equals(v, attr.Item3, StringComparison.OrdinalIgnoreCase) &&
                            !v.StartsWith(attr.Item3 + "-", StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "^=")
                    {
                        if (!(val ?? "").StartsWith(attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "$=")
                    {
                        if (!(val ?? "").EndsWith(attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else if (attr.Item2 == "*=")
                    {
                        if ((val ?? "").IndexOf(attr.Item3, StringComparison.OrdinalIgnoreCase) < 0) return false;
                    }
                }
            }

            return true;
        }

        // ===========================
        // Utility helpers
        // ===========================

        private static void ParseFontShorthand(string font, CssComputed css)
        {
            // Syntax: [ <font-style> || <font-variant> || <font-weight> || <font-stretch> ]? <font-size> [ / <line-height> ]? <font-family>
            // Example: italic bold 12px/30px Georgia, serif
            
            var parts = SplitCssValues(font);
            if (parts.Count == 0) return;

            int index = 0;
            
            // 1. Parse optional style/weight/variant
            // We loop until we find a size (digit or known size keyword)
            while (index < parts.Count)
            {
                var p = parts[index].ToLowerInvariant();
                
                // Check if it's a size
                if (char.IsDigit(p[0]) || p.StartsWith(".") || IsFontSizeKeyword(p))
                {
                    break;
                }

                // Check style
                if (p == "italic" || p == "oblique")
                {
                    css.FontStyle = SKFontStyleSlant.Italic;
                }
                else if (p == "normal")
                {
                    css.FontStyle = SKFontStyleSlant.Upright;
                    css.FontWeight = 400;
                }
                // Check weight
                else if (p == "bold")
                {
                    css.FontWeight = 700;
                }
                else if (p == "bolder" || p == "lighter")
                {
                    // simplified
                    css.FontWeight = p == "bolder" ? 700 : 300;
                }
                else if (int.TryParse(p, out int w))
                {
                    css.FontWeight = MakeFontWeight(w);
                }
                // Ignore variant/stretch for now
                
                index++;
            }

            if (index >= parts.Count) return;

            // 2. Parse font-size and optional line-height
            var sizePart = parts[index];
            index++;

            string fontSizeStr = sizePart;
            string lineHeightStr = null;

            int slash = sizePart.IndexOf('/');
            if (slash >= 0)
            {
                fontSizeStr = sizePart.Substring(0, slash);
                lineHeightStr = sizePart.Substring(slash + 1);
            }

            double fs;
            if (TryPx(fontSizeStr, out fs)) css.FontSize = fs;
            else if (IsFontSizeKeyword(fontSizeStr)) css.FontSize = ParseFontSizeKeyword(fontSizeStr);

            if (!string.IsNullOrEmpty(lineHeightStr))
            {
                double lh;
                if (TryPx(lineHeightStr, out lh)) css.LineHeight = lh;
                else if (double.TryParse(lineHeightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out lh)) css.LineHeight = lh;
            }

            // 3. Parse font-family (rest of the string)
            if (index < parts.Count)
            {
                var sb = new StringBuilder();
                for (int i = index; i < parts.Count; i++)
                {
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append(parts[i]);
                }
                var family = sb.ToString();
                
                // Resolve family
                var resolved = SelectFontFamily(family);
                if (!string.IsNullOrEmpty(resolved))
                    css.FontFamilyName = resolved;
            }
        }

        private static bool IsFontSizeKeyword(string s)
        {
            return s == "xx-small" || s == "x-small" || s == "small" || s == "medium" || s == "large" || s == "x-large" || s == "xx-large" || s == "smaller" || s == "larger";
        }

        private static double ParseFontSizeKeyword(string s)
        {
            // Base 16px
            switch (s)
            {
                case "xx-small": return 9;
                case "x-small": return 10;
                case "small": return 13;
                case "medium": return 16;
                case "large": return 18;
                case "x-large": return 24;
                case "xx-large": return 32;
                default: return 16;
            }
        }

        private static bool IsCustomPropertyName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.StartsWith("--", StringComparison.Ordinal);
        }

        private static string ResolveCustomPropertyReferences(string value, CssComputed current, Dictionary<string, string> rawCurrent, HashSet<string> seen)
        {
            if (value != null && value.Contains("var(")) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[D-BUG] ResolveCustomPropertyReferences value='{value}'\r\n");
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            if (value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0) return value;

            if (seen == null)
                seen = new HashSet<string>(StringComparer.Ordinal);

            var sb = new StringBuilder(value.Length);
            int idx = 0;
            while (idx < value.Length)
            {
                var pos = value.IndexOf("var(", idx, StringComparison.OrdinalIgnoreCase);
                if (pos < 0)
                {
                    sb.Append(value.Substring(idx));
                    break;
                }

                sb.Append(value.Substring(idx, pos - idx));
                int argsStart = pos + 4;
                int depth = 1;
                int i = argsStart;
                while (i < value.Length && depth > 0)
                {
                    char ch = value[i];
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    if (depth == 0) break;
                    i++;
                }

                if (depth != 0)
                {
                    sb.Append(value.Substring(pos));
                    break;
                }

                var inner = value.Substring(argsStart, i - argsStart);
                var resolved = EvaluateVarExpression(inner, current, rawCurrent, seen);
                sb.Append(resolved);
                idx = i + 1;
            }

            return sb.ToString();
        }

        private static string EvaluateVarExpression(string rawArgs, CssComputed current, Dictionary<string, string> rawCurrent, HashSet<string> seen)
        {
            var trimmed = (rawArgs ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;

            int comma = FindTopLevelComma(trimmed);
            string name = comma >= 0 ? trimmed.Substring(0, comma).Trim() : trimmed;
            DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[D-BUG] EvaluateVarExpression name='{name}'\r\n");
            string fallback = comma >= 0 ? trimmed.Substring(comma + 1) : null;

            if (string.IsNullOrEmpty(name) || !name.StartsWith("--", StringComparison.Ordinal))
                return ResolveFallback(fallback, current, rawCurrent, seen);

            string resolved;
            string rawValue;
            if (rawCurrent != null && rawCurrent.TryGetValue(name, out rawValue))
            {
                if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                if (seen.Contains(name))
                {
                    DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-LOOP] name='{name}' already in seen set\r\n");
                    return ResolveFallback(fallback, current, rawCurrent, seen);
                }

                seen.Add(name);
                resolved = ResolveCustomPropertyReferences(rawValue, current, rawCurrent, seen);
                seen.Remove(name);
                current.CustomProperties[name] = resolved;
                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-LOCAL] name='{name}' resolved to='{resolved}'\r\n");
                return resolved;
            }

            if (current != null && current.CustomProperties != null && current.CustomProperties.TryGetValue(name, out resolved))
            {
                DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-INHERITED] name='{name}' found value='{resolved}'\r\n");
                return resolved;
            }

            // GLOBAL FALLBACK (My addition for :root/global variables)
            lock (_customProperties)
            {
                if (_customProperties.TryGetValue(name, out resolved))
                {
                    DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-GLOBAL] name='{name}' resolved to='{resolved}'\r\n");
                    
                    // Recursive resolution for global properties (e.g., --brand-font: var(--main-font))
                    if (resolved != null && resolved.Contains("var("))
                    {
                        if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                        if (!seen.Contains(name))
                        {
                             seen.Add(name);
                             DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-RECURSE] Recursing for {name} value='{resolved}'\r\n");
                             var recursiveResolved = ResolveCustomPropertyReferences(resolved, current, rawCurrent, seen);
                             seen.Remove(name);
                             DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-RECURSE] Result for {name} is '{recursiveResolved}'\r\n");
                             return recursiveResolved;
                        }
                    }
                    return resolved;
                }
                else
                {
                    DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-VAR-MISS] name='{name}' (Global Dict Count: {_customProperties.Count})\r\n");
                }
            }

            return ResolveFallback(fallback, current, rawCurrent, seen);
        }

        private static string ResolveFallback(string fallback, CssComputed current, Dictionary<string, string> rawCurrent, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(fallback)) return string.Empty;
            return ResolveCustomPropertyReferences(fallback, current, rawCurrent, seen);
        }

        private static int FindTopLevelComma(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return -1;
            int depth = 0;
            bool inString = false;
            char stringChar = '\0';

            for (int i = 0; i < raw.Length; i++)
            {
                var ch = raw[i];
                if (inString)
                {
                    if (ch == stringChar) inString = false;
                    continue;
                }

                if (ch == '\"' || ch == '\'')
                {
                    inString = true; stringChar = ch; continue;
                }
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
                if (ch == ',' && depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitCssValues(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            char stringChar = '\0';

            foreach (var ch in raw)
            {
                if (inString)
                {
                    sb.Append(ch);
                    if (ch == stringChar) inString = false;
                    continue;
                }

                if (ch == '\"' || ch == '\'')
                {
                    inString = true; stringChar = ch; sb.Append(ch); continue;
                }

                if (ch == '(') { depth++; sb.Append(ch); continue; }
                if (ch == ')') { depth = Math.Max(0, depth - 1); sb.Append(ch); continue; }

                if (char.IsWhiteSpace(ch) && depth == 0)
                {
                    if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                }
                else
                {
                    sb.Append(ch);
                }
            }

            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }





        private static bool TryGapShorthand(string raw, out double row, out double column)
        {
            row = column = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            
            var parts = SplitCssValues(raw);
            if (parts.Count == 0) return false;

            double first;
            string p0 = parts[0].Trim();
            if (!TryPx(p0, out first)) 
            {
                // Fallback: Manually strip px
                if (p0.EndsWith("px", StringComparison.OrdinalIgnoreCase) && 
                    double.TryParse(p0.Substring(0, p0.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out first))
                {
                    // success
                }
                else if (double.TryParse(p0, NumberStyles.Float, CultureInfo.InvariantCulture, out first))
                {
                    // success (unitless)
                }
                else return false;
            }
            row = first;
            column = first;

            if (parts.Count > 1)
            {
                double second;
                string p1 = parts[1].Trim();
                if (TryPx(p1, out second)) 
                {
                    column = second;
                }
                else if (p1.EndsWith("px", StringComparison.OrdinalIgnoreCase) && 
                         double.TryParse(p1.Substring(0, p1.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out second))
                {
                    column = second;
                }
                else if (double.TryParse(p1, NumberStyles.Float, CultureInfo.InvariantCulture, out second))
                {
                    column = second;
                }
            }

            return true;
        }





        private static bool TryFlexShorthand(string raw, out double grow, out double shrink, out double basis)
        {
            grow = 0; shrink = 1; basis = double.NaN; // Default initial values
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var parts = SplitCssValues(raw);
            if (parts.Count == 0) return false;

            // Handle keywords
            if (parts.Count == 1)
            {
                var p = parts[0].ToLowerInvariant();
                if (p == "none") { grow = 0; shrink = 0; basis = double.NaN; return true; }
                if (p == "auto") { grow = 1; shrink = 1; basis = double.NaN; return true; }
                if (p == "initial") { grow = 0; shrink = 1; basis = double.NaN; return true; }
            }

            // Helper to check if string is a length
            Func<string, bool> isLength = (s) =>
            {
                s = s.ToLowerInvariant();
                return s.EndsWith("px") || s.EndsWith("%") || s.EndsWith("em") || s.EndsWith("rem") || s == "auto" || s == "content";
            };

            // Parse parts
            if (parts.Count == 1)
            {
                // <number> (grow) OR <length> (basis)
                double val;
                if (isLength(parts[0]))
                {
                    if (TryPx(parts[0], out val) || parts[0].ToLowerInvariant() == "auto")
                    {
                        grow = 1; shrink = 1; basis = (parts[0].ToLowerInvariant() == "auto" ? double.NaN : val);
                    }
                    else if (TryPercent(parts[0], out val))
                    {
                         // Treat percentage basis as Auto (NaN) for now but ensure Grow=1 is set
                         grow = 1; shrink = 1; basis = double.NaN;
                    }
                }
                else if (TryDouble(parts[0], out val))
                {
                    grow = val; shrink = 1; basis = 0;
                }
            }
            else if (parts.Count == 2)
            {
                // first is grow
                double val1;
                if (TryDouble(parts[0], out val1)) grow = val1;

                // second: <number> (shrink) OR <length> (basis)
                double val2;
                if (isLength(parts[1]))
                {
                    shrink = 1;
                    if (TryPx(parts[1], out val2) || parts[1].ToLowerInvariant() == "auto")
                        basis = (parts[1].ToLowerInvariant() == "auto" ? double.NaN : val2);
                }
                else if (TryDouble(parts[1], out val2))
                {
                    shrink = val2; basis = 0;
                }
            }
            else if (parts.Count >= 3)
            {
                // grow shrink basis
                double v;
                if (TryDouble(parts[0], out v)) grow = v;
                if (TryDouble(parts[1], out v)) shrink = v;
                if (TryPx(parts[2], out v) || parts[2].ToLowerInvariant() == "auto")
                    basis = (parts[2].ToLowerInvariant() == "auto" ? double.NaN : v);
            }

            return true;
        }

        private static string StripComments(string css)
        {
            return Regex.Replace(css ?? "", @"/\*[\s\S]*?\*/", ""); // remove /* ... */
        }

        private static IEnumerable<string> SplitTokens(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            var parts = s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                yield return parts[i].Trim();
        }

        private static bool ContainsToken(string list, string token)
        {
            foreach (var t in SplitTokens(list))
                if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Safe(string s) { return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }

        private static bool TryCornerRadius(string raw, out CssCornerRadius radius)
        {
            radius = new CssCornerRadius(new CssLength(0));
            if (string.IsNullOrWhiteSpace(raw)) return false;
            
            /* [PERF-REMOVED] */

            // Remove / part for now (elliptical corners not fully supported in simple model)
            var main = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            
            // Robust splitting by whitespace
            var parts = main.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            CssLength tl, tr, br, bl;
            if (parts.Length == 1)
            {
                if (TryCornerComponent(parts[0], out var val))
                {
                    radius = new CssCornerRadius(val);
                    return true;
                }
            }
            else if (parts.Length == 2)
            {
                if (TryCornerComponent(parts[0], out tl) && TryCornerComponent(parts[1], out tr))
                {
                    radius = new CssCornerRadius(tl, tr, tl, tr); // top-left=bottom-right, top-right=bottom-left
                    return true;
                }
            }
            else if (parts.Length == 3)
            {
                if (TryCornerComponent(parts[0], out tl) && TryCornerComponent(parts[1], out tr) && TryCornerComponent(parts[2], out br))
                {
                    radius = new CssCornerRadius(tl, tr, br, tr); // top-left, top-right=bottom-left, bottom-right
                    return true;
                }
            }
            else if (parts.Length >= 4)
            {
                if (TryCornerComponent(parts[0], out tl) &&
                    TryCornerComponent(parts[1], out tr) &&
                    TryCornerComponent(parts[2], out br) &&
                    TryCornerComponent(parts[3], out bl))
                {
                    radius = new CssCornerRadius(tl, tr, br, bl);
                    return true;
                }
            }
            return false;
        }

        private static bool TryCornerComponent(string raw, out CssLength value)
        {
            value = new CssLength(0);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            double val;
            // Try standard parser
            if (TryPx(raw, out val)) 
            {
                value = new CssLength((float)val);
                return true;
            }
            
            // Fallback: Manually strip px
            var trimmed = raw.Trim();
            if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                var numPart = trimmed.Substring(0, trimmed.Length - 2).Trim();
                if (double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                {
                    value = new CssLength((float)val);
                    return true;
                }
            }
            
            // Fallback: Unitless (quirks)
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            {
                value = new CssLength((float)val);
                return true;
            }

            if (trimmed.EndsWith("%", StringComparison.Ordinal))
            {
                double pct;
                if (TryDouble(trimmed.TrimEnd('%'), out pct))
                {
                    // Store as percentage
                    value = new CssLength((float)Math.Max(0, pct), true);
                    return true;
                }
            }
            return false;
        }

        private static string SelectFontFamily(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var candidate = part.Trim().Trim('\"', '\'');
                if (candidate.Length == 0) continue;
                if (IsGenericFamily(candidate)) continue;
                return candidate;
            }

            if (parts.Length == 0) return null;
            var fallback = parts[0].Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(fallback)) return null;
            return MapGenericFamily(fallback) ?? fallback;
        }

        private static bool IsGenericFamily(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            switch (name.Trim().ToLowerInvariant())
            {
                case "sans-serif":
                case "serif":
                case "monospace":
                case "cursive":
                case "fantasy":
                case "system-ui":
                case "ui-sans-serif":
                case "ui-serif":
                case "ui-monospace":
                case "ui-rounded":
                    return true;
                default:
                    return false;
            }
        }

        private static string MapGenericFamily(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            switch (name.Trim().ToLowerInvariant())
            {
                case "sans-serif":
                case "system-ui":
                case "ui-sans-serif":
                    return "Segoe UI";
                case "serif":
                case "ui-serif":
                    return "Times New Roman";
                case "monospace":
                case "ui-monospace":
                    return "Consolas";
                case "cursive":
                    return "Comic Sans MS";
                case "fantasy":
                case "ui-rounded":
                    return "Segoe UI";
                default:
                    return null;
            }
        }

        private static string SafeGatherText(Node n)
        {
            if (n == null) return null;
            var sb = new StringBuilder();
            
            // Gather text from this node itself (e.g. #text, #cdata, or comments)
            if (n is Element e && !string.IsNullOrEmpty(e.Text)) sb.Append(e.Text);
            else if (n.IsText && !string.IsNullOrEmpty(n.Text)) sb.Append(n.Text);
            
            // Recurse children (if any)
            foreach (var ch in n.Children)
            {
                sb.Append(SafeGatherText(ch));
            }
            return sb.ToString();
        }

        private static string DictGet(IDictionary<string, string> map, string key)
        {
            if (map == null || key == null) return null;
            string v;
            return map.TryGetValue(key, out v) ? v : null;
        }

        private static Uri ResolveUri(Uri baseUri, string href)
        {
            if (string.IsNullOrWhiteSpace(href)) return null;
            href = href.Trim();

            if (href.StartsWith("//"))
            {
                try { return new Uri((baseUri != null ? baseUri.Scheme : "https") + ":" + href); } catch { return null; }
            }

            Uri abs;
            if (Uri.TryCreate(href, UriKind.Absolute, out abs)) return abs;
            if (baseUri != null && Uri.TryCreate(baseUri, href, out abs)) return abs;
            return null;
        }

        private static string ResolveUrlIfNeeded(string value, Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var m = Regex.Match(value, @"url\(['""]?(?<u>[^)'""]+)['""]?\)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var u = m.Groups["u"].Value.Trim();
                var abs = ResolveUri(baseUri, u);
                if (abs != null)
                    return "url(" + abs.AbsoluteUri + ")";
            }
            return value;
        }

        // ---- CSS value parsing used for typed properties ----

        private static bool IsCssFunction(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().ToLowerInvariant();
            return s.StartsWith("calc(") || 
                   s.StartsWith("min(") || 
                   s.StartsWith("max(") || 
                   s.StartsWith("clamp(") || 
                   s.StartsWith("env(") || 
                   s.StartsWith("var(");
        }

        /// <summary>
        /// Parse CSS 'font' shorthand property.
        /// Format: [font-style] [font-variant] [font-weight] font-size[/line-height] font-family
        /// Example: font: italic 400 16px/1.5 Arial, sans-serif
        /// Only font-size and font-family are required.
        /// </summary>
        private static void ParseFontShorthand(string value, CssComputed css, double emBase)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            
            // Skip system font keywords
            var lower = value.Trim().ToLowerInvariant();
            if (lower == "caption" || lower == "icon" || lower == "menu" || 
                lower == "message-box" || lower == "small-caption" || lower == "status-bar" ||
                lower == "inherit" || lower == "initial" || lower == "unset")
            {
                return;
            }
            
            try
            {
                // Split by spaces, but be careful of font-family names with spaces
                var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;
                
                // Font-size is the first part that looks like a size (has unit or is a size keyword)
                // Font-weight is typically 100-900 or normal/bold/lighter/bolder
                double parsedSize = 0;
                bool foundSize = false;
                
                foreach (var part in parts)
                {
                    var p = part.Trim().ToLowerInvariant();
                    
                    // Skip font-weight numeric values (100-900)
                    if (p == "100" || p == "200" || p == "300" || p == "400" || 
                        p == "500" || p == "600" || p == "700" || p == "800" || p == "900")
                    {
                        continue;
                    }
                    
                    // Skip font-style
                    if (p == "normal" || p == "italic" || p == "oblique") continue;
                    
                    // Skip font-variant
                    if (p == "small-caps") continue;
                    
                    // Skip font-weight keywords
                    if (p == "bold" || p == "bolder" || p == "lighter") continue;
                    
                    // Handle size/line-height (e.g., "16px/1.5")
                    string sizeStr = p;
                    if (p.Contains("/"))
                    {
                        var slashParts = p.Split('/');
                        sizeStr = slashParts[0];
                        // Could parse line-height from slashParts[1] here
                    }
                    
                    // Try to parse as font-size
                    if (TryPx(sizeStr, out parsedSize, emBase, percentBase: emBase))
                    {
                        foundSize = true;
                        break; // Font-size found, rest is font-family
                    }
                }
                
                if (foundSize && parsedSize > 0)
                {
                    // Only set if font-size isn't already explicitly set
                    if (!css.FontSize.HasValue)
                    {
                        css.FontSize = parsedSize;
                        css.Map["font-size"] = parsedSize.ToString("0.##") + "px";
                    }
                }
            }
            catch
            {
                // Ignore parsing errors for font shorthand
            }
        }



        /// <summary>
        /// Extract width from a border-side shorthand (e.g., "2px solid #eee")
        /// </summary>
        private static double ExtractBorderSideWidth(string borderSide, double emBase = 16.0)
        {
            if (string.IsNullOrWhiteSpace(borderSide)) return 0;
            var parts = borderSide.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                double px;
                if (TryPx(part, out px, emBase) && px > 0)
                    return px;
            }
            return 0;
        }

        /// <summary>
        /// Extract color from a border-side shorthand (e.g., "2px solid #eee")
        /// </summary>
        private static SKColor? ExtractBorderSideColor(string borderSide)
        {
            if (string.IsNullOrWhiteSpace(borderSide)) return null;
            var parts = borderSide.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                // Skip width values
                double px;
                if (TryPx(part, out px)) continue;
                // Skip style keywords
                var lower = part.ToLowerInvariant();
                if (lower == "none" || lower == "hidden" || lower == "dotted" || lower == "dashed" ||
                    lower == "solid" || lower == "double" || lower == "groove" || lower == "ridge" ||
                    lower == "inset" || lower == "outset") continue;
                // Try as color
                var col = TryColor(part);
                if (col.HasValue) return col;
            }
            return null;
        }

        // ... existing methods ...

        private static string ParseBackgroundImage(string value)
        {
            // Just return the raw value if it looks like a URL
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                 // Simplify: return the whole string or just the url part? 
                 // For now, return the whole property value so the renderer can parse it.
                 return value;
            }
            return null;
        }

        private static string ParseGradient(string css)
        {
            if (string.IsNullOrWhiteSpace(css)) return null;
            css = css.Trim();

            if (css.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase) ||
                css.StartsWith("radial-gradient(", StringComparison.OrdinalIgnoreCase) ||
                css.StartsWith("conic-gradient(", StringComparison.OrdinalIgnoreCase) ||
                css.StartsWith("repeating-linear-gradient(", StringComparison.OrdinalIgnoreCase) ||
                css.StartsWith("repeating-radial-gradient(", StringComparison.OrdinalIgnoreCase))
            {
                return css;
            }

            return null;
        }

        /// <summary>
        /// Extract border style from a border shorthand (e.g., "2px solid #eee")
        /// Returns the style keyword: none, hidden, dotted, dashed, solid, double, groove, ridge, inset, outset
        /// </summary>
        private static string ExtractBorderSideStyle(string borderSide)
        {
            if (string.IsNullOrWhiteSpace(borderSide)) return "none";
            var parts = borderSide.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var lower = part.ToLowerInvariant();
                if (IsBorderStyle(lower)) return lower;
            }
            // If border has width but no style specified, default to solid
            foreach (var part in parts)
            {
                double px;
                if (TryPx(part, out px) && px > 0) return "solid";
            }
            return "none";
        }

        /// <summary>
        /// Extract border style from border-style shorthand (1-4 values)
        /// </summary>
        private static void ExtractBorderStyles(string value, out string top, out string right, out string bottom, out string left)
        {
            top = right = bottom = left = "none";
            if (string.IsNullOrWhiteSpace(value)) return;
            var parts = value.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            
            // CSS shorthand: 1 value = all, 2 values = top/bottom + left/right, 3 values = top + left/right + bottom, 4 = each
            if (parts.Length == 1)
            {
                top = right = bottom = left = parts[0].ToLowerInvariant();
            }
            else if (parts.Length == 2)
            {
                top = bottom = parts[0].ToLowerInvariant();
                left = right = parts[1].ToLowerInvariant();
            }
            else if (parts.Length == 3)
            {
                top = parts[0].ToLowerInvariant();
                left = right = parts[1].ToLowerInvariant();
                bottom = parts[2].ToLowerInvariant();
            }
            else
            {
                top = parts[0].ToLowerInvariant();
                right = parts[1].ToLowerInvariant();
                bottom = parts[2].ToLowerInvariant();
                left = parts[3].ToLowerInvariant();
            }
        }

        private static void Log(Action<string> log, string msg)
        {
            try { if (log != null) log(msg); } catch { }
        }



        private static List<string> SplitByComma(string content)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(content)) return list;
            
            int depth = 0;
            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '(') depth++;
                else if (content[i] == ')') depth--;
                else if (content[i] == ',' && depth == 0)
                {
                    list.Add(content.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < content.Length) list.Add(content.Substring(start));
            return list;
        }

    private static readonly object _logLock = new object();
    private static void DebugLog(string filename, string message)
    {
        // Disabled for performance
    }

    /// <summary>
    /// Matches a SelectorChain against an element (for compound :not() support)
    /// </summary>
    private static bool MatchesSelectorChain(SelectorChain chain, Element n)
    {
        return Matches(n, chain);
    }

    /// <summary>
    /// Parses an nth-expression (e.g. '2n+1', 'odd', 'even') into a and b for an+b
    /// </summary>
    private static bool ParseNthExpression(string expr, out int a, out int b)
    {
        a = 0; b = 0;
        expr = expr.Trim().ToLowerInvariant();
        if (expr == "odd") { a = 2; b = 1; return true; }
        if (expr == "even") { a = 2; b = 0; return true; }
        var match = System.Text.RegularExpressions.Regex.Match(expr, @"^([+-]?\d*)n([+-]?\d+)?$");
        if (match.Success)
        {
            var aStr = match.Groups[1].Value;
            var bStr = match.Groups[2].Value;
            a = (aStr == "" || aStr == "+") ? 1 : (aStr == "-" ? -1 : int.Parse(aStr));
            b = bStr == "" ? 0 : int.Parse(bStr);
            return true;
        }
        // Just a number
        if (int.TryParse(expr, out b)) { a = 0; return true; }
        return false;
    }

    // ==========================================
    // Selector Helpers for :is, :where, :has
    // ==========================================

    private static bool MatchesSelectorList(Element n, string selectorList)
    {
        if (string.IsNullOrWhiteSpace(selectorList)) return false;
        
        var parts = SplitByComma(selectorList);
        foreach (var part in parts)
        {
            var chain = ParseSelectorChain(part.Trim());
            if (chain != null && Matches(n, chain)) return true;
        }
        return false;
    }

    private static bool MatchesHas(Element n, string selectorList)
    {
        if (string.IsNullOrWhiteSpace(selectorList)) return false;
        
        var parts = SplitByComma(selectorList);
        var chains = new List<SelectorChain>();
        foreach (var p in parts)
        {
            var c = ParseSelectorChain(p.Trim());
            if (c != null) chains.Add(c);
        }
        
        if (chains.Count == 0) return false;

        var queue = new Queue<Element>();
        if (n.Children != null)
        {
            foreach(var c in n.Children.OfType<Element>()) queue.Enqueue(c);
        }
        
        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            foreach (var chain in chains)
            {
                if (Matches(curr, chain)) return true;
            }
            
            if (curr.Children != null)
            {
                foreach (var c in curr.Children.OfType<Element>()) queue.Enqueue(c);
            }
        }
        return false;
    }
    
    private static bool IsIdentChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '*' || c >= 128;
    }

    /// <summary>
    /// Resolve attr() function in CSS value.
    /// </summary>
    private static string ResolveAttr(string value, Element n)
    {
        if (string.IsNullOrEmpty(value) || n == null || !value.Contains("attr(")) return value;
        
        // Replaces attr(name) with attribute value
        return System.Text.RegularExpressions.Regex.Replace(value, @"attr\s*\(\s*([a-zA-Z0-9-]+)\s*\)", m => 
        {
            string attrName = m.Groups[1].Value.Trim();
            string attrVal = "";
            if (n.Attr != null) n.Attr.TryGetValue(attrName, out attrVal);
            return attrVal ?? "";
        });
    }

    private static double? ParseGapValue(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        val = val.Trim();
        if (val.EndsWith("px", StringComparison.OrdinalIgnoreCase)) 
        {
             if (double.TryParse(val.Substring(0, val.Length - 2), out double px)) return px;
        }
        if (val.All(char.IsDigit)) 
        {
             if (double.TryParse(val, out double d)) return d;
        }
        return 0; 
    }
}
}


