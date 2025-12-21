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
// using FenBrowser.Core.Math; // Namespace moved to Core
namespace FenBrowser.FenEngine.Rendering
{
    public static class CssLoader
    {
        public class CssLoadResult
        {
            public Dictionary<LiteElement, CssComputed> Computed { get; set; } = new Dictionary<LiteElement, CssComputed>();
            public List<CssSource> Sources { get; set; } = new List<CssSource>();
        }

        // Debug file logging - DISABLE for production (sync file I/O causes severe lag)
        private const bool DEBUG_FILE_LOGGING = false;

        public class MatchedRule
        {
            public CssRule Rule;
            public CssSource Source;
        }

        public static List<MatchedRule> GetMatchedRules(LiteElement element, List<CssSource> sources)
        {
             var matched = new List<MatchedRule>();
             if (element == null || sources == null) return matched;
             
             // 1. Gather all rules from all sources
             // WARNING: Re-parsing is expensive. Better to parse once?
             // But sources only contains TEXT.
             // We need to re-parse.
             
             foreach(var source in sources)
             {
                 try
                 {
                     var rules = ParseRules(source.CssText, source.SourceOrder, source.BaseUri, null, null);
                     foreach(var rule in rules)
                     {
                         // Check each selector chain
                         foreach(var chain in rule.Selectors)
                         {
                             if (Matches(element, chain))
                             {
                                 matched.Add(new MatchedRule { Rule = rule, Source = source });
                                 break; // Rule matches via at least one selector
                             }
                         }
                     }
                 }
                 catch {}
             }
             
             // Sort by weight: SourceOrder ASC
             matched.Sort((a,b) => a.Rule.SourceOrder.CompareTo(b.Rule.SourceOrder));
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
        public static async Task<Dictionary<LiteElement, CssComputed>> ComputeAsync(
            LiteElement root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            Action<string> log = null)
        {
            var result = await ComputeWithResultAsync(root, baseUri, fetchExternalCssAsync, viewportWidth, log);
            return result.Computed;
        }

        public static async Task<CssLoadResult> ComputeWithResultAsync(
            LiteElement root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            Action<string> log = null)
        {
            if (root == null)
                return new CssLoadResult();

            var cssBlobs = new List<CssSource>(); // collected CSS texts with source ordering
            int sourceIndex = 0;

            // 0) UA stylesheet (very small normalize) ? lowest precedence
            try
            {
                var uaCss = @"
                    /* Basic UA defaults for readability on phone */
                    html,body{background:#fff;color:#222;margin:0;padding:0;}
                    body{font:16px/1.5 'Segoe UI',Roboto,Arial,sans-serif;}
                    a{color:#1a0dab;text-decoration:underline;}
                    /* Footer/nav polish: compact inline links with spacing */
                    nav a, footer a, .footer a, #footer a, .nav a, .menu a{ display:inline-block; margin:0 8px 6px 0; }
                    nav ul, footer ul, .footer ul, #footer ul, .nav ul, .menu ul, .uiList{ list-style:none; padding:0; margin:0.5em 0; }
                    nav li, footer li, .footer li, #footer li, .nav li, .menu li, .uiList li{ display:inline-block; margin:0 10px 6px 0; }
                    a:focus,a:hover{ text-decoration:underline; }
                    img{border:0;max-width:100%;height:auto;}
                    input,button,select,textarea{font:inherit;}
                    /* Block semantics */
                    article,aside,nav,section,header,footer,main,figure{display:block;}
                    center{display:flex;flex-direction:column;align-items:center;text-align:center;}
                    h1{font-size:2em;margin:0.67em 0;font-weight:600;}
                    h2{font-size:1.5em;margin:0.83em 0;font-weight:600;}
                    h3{font-size:1.17em;margin:1em 0;font-weight:600;}
                    h4{font-size:1em;margin:1.33em 0;font-weight:600;}
                    h5{font-size:0.83em;margin:1.67em 0;font-weight:600;}
                    h6{font-size:0.67em;margin:2.33em 0;font-weight:600;}
                    p{margin:1em 0;}
                    ul,ol{margin:1em 0;padding-left:2em;}
                    li{margin:0.25em 0;}
                    small{font-size:0.875em;}
                    strong,b{font-weight:600;}
                    em,i{font-style:italic;}
                    figure{margin:1em 0;}
                    figcaption{font-size:0.875em;color:#555;}
                    table{border-collapse:collapse;}
                    th{font-weight:600;text-align:left;}
                    td,th{padding:0.4em 0.6em;}
                ";
                cssBlobs.Add(new CssSource { CssText = uaCss, Origin = CssOrigin.UserAgent, SourceOrder = sourceIndex++, BaseUri = baseUri });
            }
            catch { /* Ignore UA style errors */ }

            // 1) Inline <style> tags first (DOM order)
            foreach (var n in root.Descendants().Where(n => !n.IsText && n.Tag == "style"))
            {
                var text = SafeGatherText(n);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try {
                    try {
                        var msg = $"[{DateTime.Now:HH:mm:ss}] [CssLoader] Found style block: {text.Length} chars. Content start: {text.Substring(0, Math.Min(text.Length, 50))}\r\n";
                        if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", msg);
                    } catch {}
                    } catch {}
                    
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
            var linkNodes = root.Descendants().Where(n => !n.IsText && n.Tag == "link").ToList();
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
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[LINK] Found link with rel='{rel}'\r\n"); } catch {}
                if (!ContainsToken(rel, "stylesheet")) continue;
                string href; if (!link.Attr.TryGetValue("href", out href) || string.IsNullOrWhiteSpace(href)) continue;
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[LINK] Loading stylesheet: {href}\r\n"); } catch {}
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
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[LINK] Fetched CSS len={css?.Length ?? -1} for {abs}\r\n"); } catch {}
                        if (!string.IsNullOrWhiteSpace(css))
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

            // 3) Expand @import (depth-bounded)
            var expanded = await ExpandImportsAsync(cssBlobs, fetchExternalCssAsync, viewportWidth, log, gate);

            // 4) Parse rules from all sources (parallel, bounded)
            var allRules = new List<CssRule>();
            var parseGate = new System.Threading.SemaphoreSlim(4);
            var parseTasks = new List<Task>();
            foreach (var blob in expanded)
            {
                await parseGate.WaitAsync();
                parseTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var parsed = ParseRules(blob.CssText, blob.SourceOrder, blob.BaseUri, viewportWidth, log);
                        lock (allRules) allRules.AddRange(parsed);
                    }
                    catch (Exception ex)
                    {
                        Log(log, "[CssLoader] Parse error: " + ex.Message);
                    }
                    finally { try { parseGate.Release(); } catch { /* Ignore release errors */ } }
                }));
            }
            if (parseTasks.Count > 0) { try { await Task.WhenAll(parseTasks); } catch { /* Ignore parse errors */ } }

            // Debug: Log parsed rules count
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CssLoader] Total CSS rules parsed: {allRules.Count}\r\n"); } catch {}

            // 4.5) Resolve CSS variables
            // 4.5) Resolve CSS variables & 5) Compute per-element cascaded styles
            // CRITICAL FIX: Offload heavy CSS matching/cascading to background thread to avoid freezing UI
            ResolveVariables(allRules);
            var computed = CascadeIntoComputedStyles(root, allRules, log);
            
            return new CssLoadResult
            {
                Computed = computed,
                Sources = cssBlobs
            };
        }

        /// <summary>
        /// Overload without viewport/log parameters.
        /// </summary>
        public static Task<Dictionary<LiteElement, CssComputed>> ComputeAsync(
            LiteElement root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync)
        {
            return ComputeAsync(root, baseUri, fetchExternalCssAsync, null, null);
        }

        // ===========================
        // Model & helpers
        // ===========================

        public enum CssOrigin
        {
            Inline, External, Imported,
            UserAgent
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

        /// <summary>Single selector fragment with combinators, e.g. "div.foo #bar > span"</summary>
        public class SelectorChain
        {
            public List<SelectorSegment> Segments = new List<SelectorSegment>(); // left-to-right parsed
            public int Specificity; // computed from segments
        }

        public enum Combinator { Descendant, Child, AdjacentSibling, GeneralSibling }

        public class SelectorSegment
        {
            public string Tag;                    // e.g. "div"
            public string Id;                     // e.g. "main"
            public List<string> Classes;          // e.g. ["foo","bar"]
            public List<string> PseudoClasses;    // e.g. [":first-child"]
            public string PseudoElement;          // e.g. "before", "after"
            public List<Tuple<string, string, string>> Attributes; // e.g. [("type", "=", "text")]
            public Combinator? Next;              // relation to the NEXT segment (left-to-right)
            public List<SelectorSegment> NotSelectors; // :not() selectors (elements must NOT match these)
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
            
            while (i < text.Length)
            {
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
                    i = nameStart;
                    continue;
                }
                
                string animName = text.Substring(nameStart, braceOpen - nameStart).Trim();
                
                // Find matching closing brace for the outer @keyframes block
                int braceClose = FindMatchingBrace(text, braceOpen);
                if (braceClose < 0)
                {
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
            while (i < body.Length)
            {
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
        private static string ExtractFontFace(string text, Action<string> log)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("@font-face", StringComparison.OrdinalIgnoreCase) < 0) return text;

            var result = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
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
                    FontRegistry.ParseAndRegister(fontFaceBody);
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

            while (i < text.Length)
            {
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

            while (i < text.Length)
            {
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
                "min-height", "max-height", "aspect-ratio", "object-fit", "cursor",
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
                    EngineCapabilities.LogUnsupportedCss(property, value, "CSS property not implemented");
                }
            }
            
            return isSupported;
        }

        /// <summary>
        /// Public method to check if an element matches a CSS selector string.
        /// Used by DOM API methods like matches() and closest().
        /// </summary>
        public static bool MatchesSelector(LiteElement element, string selectorString)
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

            while (i < text.Length)
            {
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

        private static List<CssRule> ParseRules(string css, int sourceOrder, Uri baseForUrls, double? viewportWidth, Action<string> log)
        {
            var rules = new List<CssRule>();
            if (string.IsNullOrWhiteSpace(css)) return rules;

            try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_raw_css.txt", "\n--- RAW CSS BLOCK ---\n" + css + "\n-------------------\n"); } catch {}
            var text = StripComments(css);
            
            // DEBUG: Log CSS after comment stripping
            // DEBUG: Log CSS after comment stripping
             try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\debug_full_css.txt", "\n--- NEW CSS BLOCK ---\n" + text + "\n-------------------\n"); } catch {}

            // (Very) basic @media handling: keep simple "screen" blocks; ignore others.
            // We flatten recognized @media blocks by inlining their contents.
            text = FlattenBasicMedia(text, viewportWidth, log);
            
            // Extract and parse @keyframes rules
            text = ExtractKeyframes(text, log);

            // Extract and register @font-face rules
            text = ExtractFontFace(text, log);

            // Handle @supports feature queries
            text = FlattenSupports(text, log);

            // Handle @layer cascade layers
            text = ExtractLayers(text, log);
            
            // Handle @container queries (container queries)
            text = FlattenContainerQueries(text, (float)(viewportWidth ?? 1024), log);

            // Split by braces into selector/declarations pairs.
            // This is a naive parser but resistant to most content without nested braces in values.
            int i = 0;
            while (i < text.Length)
            {
                // find '{'
                int open = text.IndexOf('{', i);
                if (open < 0) break;
                var selectorText = text.Substring(i, open - i).Trim();
                int close = FindMatchingBrace(text, open);
                if (close < 0) break;

                var declText = text.Substring(open + 1, close - open - 1);
                i = close + 1;

                if (string.IsNullOrWhiteSpace(selectorText) || string.IsNullOrWhiteSpace(declText))
                    continue;

                var chains = ParseSelectors(selectorText);
                if (chains.Count == 0) continue;
                
                // DEBUG: Log parsed selectors with attribute selectors
                if (selectorText.Contains("["))
                {
                    try { 
                        var chainStr = string.Join(", ", chains.Select(c => string.Join(" ", c.Segments.Select(seg => 
                            $"{seg.Tag ?? "*"}{(seg.Id != null ? "#"+seg.Id : "")}{(seg.Classes != null && seg.Classes.Count > 0 ? "."+string.Join(".", seg.Classes) : "")}{(seg.Attributes != null && seg.Attributes.Count > 0 ? "["+string.Join("][", seg.Attributes.Select(a => $"{a.Item1}{a.Item2}{a.Item3}"))+"]" : "")}"))));
                        if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[SELECTOR] {selectorText} => Chains: {chainStr}\r\n"); 
                    } catch {}
                }

                // DEBUG: Log raw declarations for .picture
                if (selectorText.Contains("picture"))
                {
                    try { if (DEBUG_FILE_LOGGING) DebugLog(@"C:\Users\udayk\Videos\FENBROWSER\css_debug.txt", $"[PARSER_RAW] Selectors: {selectorText} | Decls: {declText}\r\n"); } catch {}
                }

                var decls = ParseDeclarations(declText);

                var rule = new CssRule { Selectors = chains, SourceOrder = sourceOrder, BaseUri = baseForUrls };
                foreach (var d in decls)
                {
                    // last declaration wins inside the same block
                    rule.Declarations[d.Name] = d;
                }
                rules.Add(rule);
            }

            return rules;
        }

        private static string FlattenBasicMedia(string text, double? viewportWidth, Action<string> log)
{
    if (string.IsNullOrEmpty(text)) return "";
    if (text.IndexOf("@media", StringComparison.OrdinalIgnoreCase) < 0) return text;

    // Enhanced media query support: min/max-width, prefers-color-scheme, prefers-reduced-motion, orientation
    var sb = new StringBuilder();
    int i = 0;
    while (i < text.Length)
    {
        if (StartsWithAt(text, i, "@media"))
        {
            int open = text.IndexOf('{', i);
            if (open < 0) break;
            int close = FindMatchingBrace(text, open);
            if (close < 0) break;

            var header = text.Substring(i, open - i).ToLowerInvariant();
            var body = text.Substring(open + 1, close - open - 1);

            bool keep = EvaluateMediaQuery(header, viewportWidth);

            if (keep) sb.Append(body);
            i = close + 1;
        }
        else
        {
            sb.Append(text[i]);
            i++;
        }
    }
    return sb.ToString();
}

/// <summary>
/// Evaluate a media query condition string
/// </summary>
private static bool EvaluateMediaQuery(string header, double? viewportWidth)
{
    bool keep = true;
    
    // Check min/max-width
    if (viewportWidth.HasValue)
    {
        var mw = ExtractPx(header, "min-width");
        var xw = ExtractPx(header, "max-width");
        if (mw.HasValue && viewportWidth.Value < mw.Value) keep = false;
        if (xw.HasValue && viewportWidth.Value > xw.Value) keep = false;
    }
    
    // Check prefers-color-scheme
    if (header.Contains("prefers-color-scheme"))
    {
        string scheme = CssParser.MediaPrefersColorScheme ?? "light";
        bool isDark = string.Equals(scheme, "dark", StringComparison.OrdinalIgnoreCase);
        
        // Update CssParser.PrefersDarkMode to match
        CssParser.PrefersDarkMode = isDark;
        
        if (header.Contains("prefers-color-scheme: dark") || header.Contains("prefers-color-scheme:dark"))
        {
            if (!isDark) keep = false;
        }
        else if (header.Contains("prefers-color-scheme: light") || header.Contains("prefers-color-scheme:light"))
        {
            if (isDark) keep = false;
        }
    }
    
    // Check prefers-reduced-motion
    if (header.Contains("prefers-reduced-motion"))
    {
        bool reducedMotion = false; // Default to false (allow motion)
        
        if (header.Contains("prefers-reduced-motion: reduce") || header.Contains("prefers-reduced-motion:reduce"))
        {
            if (!reducedMotion) keep = false;
        }
        else if (header.Contains("prefers-reduced-motion: no-preference") || header.Contains("prefers-reduced-motion:no-preference"))
        {
            if (reducedMotion) keep = false;
        }
    }
    
    // Check orientation
    if (header.Contains("orientation"))
    {
        double w = viewportWidth ?? CssParser.MediaViewportWidth ?? 1920;
        double h = CssParser.MediaViewportHeight ?? 1080;
        bool isPortrait = h > w;
        
        if (header.Contains("orientation: portrait") || header.Contains("orientation:portrait"))
        {
            if (!isPortrait) keep = false;
        }
        else if (header.Contains("orientation: landscape") || header.Contains("orientation:landscape"))
        {
            if (isPortrait) keep = false;
        }
    }
    
    // Check screen/print media type
    if (header.Contains("print") && !header.Contains("screen"))
    {
        keep = false; // We're not printing
    }
    
    return keep;
}

        private static double? ExtractPx(string text, string prop)
        {
            var m = Regex.Match(text, prop + @"\s*:\s*(?<v>[0-9]+)px", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                double v;
                if (TryDouble(m.Groups["v"].Value, out v)) return v;
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

        private static List<SelectorChain> ParseSelectors(string selectorText)
        {
            // Split on commas at top level (not inside anything else ? here we assume plain selectors).
            var parts = selectorText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<SelectorChain>();
            foreach (var p in parts)
            {
                var chain = ParseSelectorChain(p.Trim());
                if (chain != null) list.Add(chain);
            }
            return list;
        }

        private static SelectorChain ParseSelectorChain(string s)
        {
            // Supports sequences separated by space (descendant) or '>' (child)
            // Each segment supports: tag (optional), #id, .class(.class2...)
            var chain = new SelectorChain();
            var tokens = TokenizeSelector(s);
            if (tokens.Count == 0) return null;

            var seg = new SelectorSegment { Classes = new List<string>() };
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == " ")
                {
                    seg.Next = Combinator.Descendant;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t == ">")
                {
                    seg.Next = Combinator.Child;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t == "+")
                {
                    seg.Next = Combinator.AdjacentSibling;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t == "~")
                {
                    seg.Next = Combinator.GeneralSibling;
                    chain.Segments.Add(seg);
                    seg = new SelectorSegment { Classes = new List<string>() };
                }
                else if (t.StartsWith("."))
                {
                    seg.Classes.Add(t.Substring(1));
                }
                else if (t.StartsWith("#"))
                {
                    seg.Id = t.Substring(1);
                }
                else if (t.StartsWith(":"))
                {
                    string lower = t.ToLowerInvariant();
                    if (lower.StartsWith("::"))
                    {
                        string pe = lower.Substring(2);
                        if (pe == "before" || pe == "after")
                        {
                            seg.PseudoElement = pe;
                        }
                        else
                        {
                            if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                            seg.PseudoClasses.Add(t.Substring(2));
                        }
                    }
                    else
                    {
                        string val = lower.Substring(1);
                        if (val == "before" || val == "after")
                        {
                            seg.PseudoElement = val;
                        }
                        else if (val.StartsWith("not("))
                        {
                            // Handle :not() selector
                            var notArg = ExtractPseudoArg(val);
                            if (!string.IsNullOrEmpty(notArg))
                            {
                                // Parse the inner selector
                                var notSeg = ParseSimpleSelector(notArg);
                                if (notSeg != null)
                                {
                                    if (seg.NotSelectors == null) seg.NotSelectors = new List<SelectorSegment>();
                                    seg.NotSelectors.Add(notSeg);
                                }
                            }
                        }
                        else
                        {
                            if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                            seg.PseudoClasses.Add(t.Substring(1));
                        }
                    }
                }
                else if (t.StartsWith("["))
                {
                    // Attribute selector parsing: [attr], [attr=val], [attr~=val], [attr|=val], [attr^=val], [attr$=val], [attr*=val]
                    var content = t.TrimStart('[').TrimEnd(']');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();

                    // Check for operator (in order of length to match longest first)
                    string[] operators = { "~=", "|=", "^=", "$=", "*=", "=" };
                    string foundOp = null;
                    int opIndex = -1;
                    
                    foreach (var op in operators)
                    {
                        int idx = content.IndexOf(op);
                        if (idx >= 0)
                        {
                            foundOp = op;
                            opIndex = idx;
                            break;
                        }
                    }

                    if (foundOp == null || opIndex < 0)
                    {
                        // Just [attr] - presence check
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, opIndex).Trim();
                        var val = content.Substring(opIndex + foundOp.Length).Trim().Trim('"', '\'');
                        // Handle CSS escape sequences: backslash followed by a character means the character itself
                        // e.g., "second\ two" becomes "second two"
                        val = System.Text.RegularExpressions.Regex.Replace(val, @"\\(.)", "$1");
                        seg.Attributes.Add(Tuple.Create(name, foundOp, val));
                    }
                }
                else
                {
                    seg.Tag = t;
                }
            }
            chain.Segments.Add(seg);

            // compute specificity: ids*100 + classes*10 + tags*1
            int ids = 0, cl = 0, tg = 0;
            foreach (var s2 in chain.Segments)
            {
                if (!string.IsNullOrEmpty(s2.Id)) ids++;
                if (!string.IsNullOrEmpty(s2.Tag)) tg++;
                if (s2.Classes != null) cl += s2.Classes.Count;
            }
            chain.Specificity = ids * 100 + cl * 10 + tg;

            return chain;
        }

        private static List<string> TokenizeSelector(string s)
        {
            // turns "div#main .x > span.y [attr=val]" into ["div","#main"," ",".x",">","span",".y"," ","[attr=val]"]
            var r = new List<string>();
            var sb = new StringBuilder();
            Action flush = () => { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Clear(); } };

            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    flush();
                    // coalesce spaces
                    if (r.Count == 0 || r[r.Count - 1] != " ") r.Add(" ");
                }
                else if (c == '>' || c == '+' || c == '~')
                {
                    // Check if ~ is part of ~= attribute selector (peek ahead)
                    if (c == '~' && i + 1 < s.Length && s[i + 1] == '=')
                    {
                        // This is part of an attribute selector inside [], should be handled there
                        sb.Append(c);
                    }
                    else
                    {
                        flush(); r.Add(c.ToString());
                    }
                }
                else if (c == '[')
                {
                    // Read entire attribute selector until closing ]
                    flush();
                    sb.Append(c);
                    i++;
                    int bracketDepth = 1;
                    while (i < s.Length && bracketDepth > 0)
                    {
                        char ac = s[i];
                        sb.Append(ac);
                        if (ac == '[') bracketDepth++;
                        else if (ac == ']') bracketDepth--;
                        i++;
                    }
                    i--; // Back up since outer loop will increment
                    flush();
                }
                else if (c == '.' || c == '#')
                {
                    flush(); sb.Append(c);
                }
                else if (c == ':')
                {
                    flush(); 
                    sb.Append(c);
                    // Handle double colon ::
                    if (i + 1 < s.Length && s[i + 1] == ':')
                    {
                        i++;
                        sb.Append(s[i]);
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            flush();
            // trim leading/trailing spaces tokens
            if (r.Count > 0 && r[0] == " ") r.RemoveAt(0);
            if (r.Count > 0 && r[r.Count - 1] == " ") r.RemoveAt(r.Count - 1);
            return r;
        }

        /// <summary>
        /// Parse a simple selector (for :not() argument) - supports tag, .class, #id, [attr]
        /// </summary>
        private static SelectorSegment ParseSimpleSelector(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            
            var seg = new SelectorSegment { Classes = new List<string>() };
            var tokens = TokenizeSelector(s);
            
            foreach (var t in tokens)
            {
                if (t == " " || t == ">" || t == "+" || t == "~") continue; // Skip combinators
                
                if (t.StartsWith("."))
                {
                    seg.Classes.Add(t.Substring(1));
                }
                else if (t.StartsWith("#"))
                {
                    seg.Id = t.Substring(1);
                }
                else if (t.StartsWith("["))
                {
                    // Attribute selector parsing
                    var content = t.TrimStart('[').TrimEnd(']');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();
                    
                    string[] operators = { "~=", "|=", "^=", "$=", "*=", "=" };
                    string foundOp = null;
                    int opIndex = -1;
                    
                    foreach (var op in operators)
                    {
                        int idx = content.IndexOf(op);
                        if (idx >= 0)
                        {
                            foundOp = op;
                            opIndex = idx;
                            break;
                        }
                    }
                    
                    if (foundOp == null || opIndex < 0)
                    {
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, opIndex).Trim();
                        var val = content.Substring(opIndex + foundOp.Length).Trim().Trim('"', '\'');
                        seg.Attributes.Add(Tuple.Create(name, foundOp, val));
                    }
                }
                else if (t.StartsWith(":"))
                {
                    // Pseudo-class inside :not() - add to pseudo-classes
                    string val = t.Substring(1).ToLowerInvariant();
                    if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                    seg.PseudoClasses.Add(val);
                }
                else if (!string.IsNullOrEmpty(t))
                {
                    seg.Tag = t;
                }
            }
            
            return seg;
        }

        private static List<CssDecl> ParseDeclarations(string declText)
        {
            var list = new List<CssDecl>();
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

                list.Add(new CssDecl { Name = name, Value = val, Important = important });
            }
            return list;
        }

        // ===========================
        // Stage 3: Cascade
        // ===========================

        private static Dictionary<LiteElement, CssComputed> CascadeIntoComputedStyles(LiteElement root, List<CssRule> rules, Action<string> log)
        {
            var result = new Dictionary<LiteElement, CssComputed>();
            if (root == null) return result;
            
            // Debug: Log root hash to compare with DocumentWrapper._root
            try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CssLoader] CascadeIntoComputedStyles: root_hash={root.GetHashCode()}\r\n"); } catch {}

            // Pre-flatten the DOM into a list to avoid repeated Descendants() enumerations
            var nodes = new List<LiteElement>();
            var stack = new Stack<LiteElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                nodes.Add(n);
                // Push children in reverse so natural order remains pre-order in 'nodes'
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    stack.Push(n.Children[i]);
            }

            // Prepare per-element candidate declarations+weights
            var perNode = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodeBefore = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodeAfter = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodeMarker = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodePlaceholder = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodeSelection = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodeFirstLine = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            var perNodeFirstLetter = new Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>>();
            int matchedNodes = 0;
            foreach (var n in nodes)
            {
                if (n.IsText) continue;

                bool nodeHasMatches = false;
                foreach (var rule in rules)
                {
                    foreach (var chain in rule.Selectors)
                    {
                        if (Matches(n, chain))
                        {
                            var lastSeg = chain.Segments[chain.Segments.Count - 1];
                            string pe = lastSeg.PseudoElement;
                            
                            Dictionary<LiteElement, List<Tuple<CssDecl, SelectorChain, int>>> targetDict = perNode;
                            if (pe == "before") targetDict = perNodeBefore;
                            else if (pe == "after") targetDict = perNodeAfter;
                            else if (pe == "marker") targetDict = perNodeMarker;
                            else if (pe == "placeholder") targetDict = perNodePlaceholder;
                            else if (pe == "selection") targetDict = perNodeSelection;
                            else if (pe == "first-line") targetDict = perNodeFirstLine;
                            else if (pe == "first-letter") targetDict = perNodeFirstLetter;
                            else if (!string.IsNullOrEmpty(pe)) continue; // Unknown pseudo-element

                            nodeHasMatches = true;
                            // weight tuple: important (1/0), specificity, sourceOrder
                            foreach (var decl in rule.Declarations.Values)
                            {
                                // NOTE: we assign specificity here; for a rule with multiple selectors we use the chain's specificity
                                var d = new CssDecl
                                {
                                    Name = decl.Name,
                                    Value = ResolveUrlIfNeeded(decl.Value, rule.BaseUri),
                                    Important = decl.Important,
                                    Specificity = chain.Specificity
                                };

                                List<Tuple<CssDecl, SelectorChain, int>> list;
                                if (!targetDict.TryGetValue(n, out list))
                                {
                                    list = new List<Tuple<CssDecl, SelectorChain, int>>();
                                    targetDict[n] = list;
                                }
                                list.Add(Tuple.Create(d, chain, rule.SourceOrder));
                            }
                        }
                    }
                }
                if (nodeHasMatches) matchedNodes++;
            }
            
            // Debug: Log how many nodes had CSS rule matches
            // Log removed
            
            // Debug: Log CSS properties for key Acid2 elements
            try {
                foreach (var n in nodes)
                {
                    if (n.IsText) continue;
                    string cls = "";
                    if (n.Attr != null) n.Attr.TryGetValue("class", out cls);
                    if (cls != null && (cls.Contains("picture") || cls.Contains("intro") || cls.Contains("eyes") || cls.Contains("nose") || cls.Contains("forehead")))
                    {
                        List<Tuple<CssDecl, SelectorChain, int>> items;
                        perNode.TryGetValue(n, out items);
                        int propCount = items != null ? items.Count : 0;
                        var propList = items != null ? string.Join(", ", items.Select(i => i.Item1.Name + ":" + i.Item1.Value).Take(10)) : "none";
                        // Log removed
                    }
                }
            } catch {}

            // Inline style beats author rules; include as highest priority ?rule?
            foreach (var n in nodes)
            {
                if (n.IsText) continue;
                string style;
                if (n.Attr != null && n.Attr.TryGetValue("style", out style) && !string.IsNullOrWhiteSpace(style))
                {
                    // Debug: Log inline style for body element
                    if (string.Equals(n.Tag, "body", StringComparison.OrdinalIgnoreCase))
                    {
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CssLoader] BODY inline style found: '{style}'\r\n"); } catch {}
                    }
                    
                    var decls = ParseDeclarations(style);
                    List<Tuple<CssDecl, SelectorChain, int>> list;
                    if (!perNode.TryGetValue(n, out list))
                    {
                        list = new List<Tuple<CssDecl, SelectorChain, int>>();
                        perNode[n] = list;
                    }
                    // inline specificity: max out (acts like 1000)
                    foreach (var d in decls)
                    {
                        d.Specificity = 1000;
                        list.Add(Tuple.Create(d, (SelectorChain)null, int.MaxValue));
                    }
                }
            }

            // Now resolve final property values with cascade ordering
            foreach (var n in nodes)
            {
                if (n == null || n.IsText) continue;

                CssComputed parentCss = null;
                if (n.Parent != null)
                    result.TryGetValue(n.Parent, out parentCss);

                List<Tuple<CssDecl, SelectorChain, int>> items;
                perNode.TryGetValue(n, out items);

                bool hasMain = items != null && items.Count > 0;
                bool hasBefore = perNodeBefore.ContainsKey(n) && perNodeBefore[n].Count > 0;
                bool hasAfter = perNodeAfter.ContainsKey(n) && perNodeAfter[n].Count > 0;
                bool hasMarker = perNodeMarker.ContainsKey(n) && perNodeMarker[n].Count > 0;
                bool hasPlaceholder = perNodePlaceholder.ContainsKey(n) && perNodePlaceholder[n].Count > 0;
                bool hasSelection = perNodeSelection.ContainsKey(n) && perNodeSelection[n].Count > 0;
                bool hasFirstLine = perNodeFirstLine.ContainsKey(n) && perNodeFirstLine[n].Count > 0;
                bool hasFirstLetter = perNodeFirstLetter.ContainsKey(n) && perNodeFirstLetter[n].Count > 0;

                if (!hasMain && !hasBefore && !hasAfter && !hasMarker && !hasPlaceholder && !hasSelection && !hasFirstLine && !hasFirstLetter) continue;

                var css = ResolveStyle(n, parentCss, items ?? new List<Tuple<CssDecl, SelectorChain, int>>());

                if (hasBefore)
                {
                    css.Before = ResolveStyle(n, css, perNodeBefore[n]);
                }

                if (hasAfter)
                {
                    css.After = ResolveStyle(n, css, perNodeAfter[n]);
                }
                
                if (hasMarker)
                {
                    css.Marker = ResolveStyle(n, css, perNodeMarker[n]);
                }
                
                if (hasPlaceholder) css.Placeholder = ResolveStyle(n, css, perNodePlaceholder[n]);
                if (hasSelection) css.Selection = ResolveStyle(n, css, perNodeSelection[n]);
                if (hasFirstLine) css.FirstLine = ResolveStyle(n, css, perNodeFirstLine[n]);
                if (hasFirstLetter) css.FirstLetter = ResolveStyle(n, css, perNodeFirstLetter[n]);

                result[n] = css;
            }

            // Debug: Summary statistics
            try { 
                int nodesWithBackground = result.Values.Count(c => c.Background != null || c.BackgroundColor.HasValue);
                int nodesWithColor = result.Values.Count(c => c.ForegroundColor.HasValue);
                int nodesWithDisplay = result.Values.Count(c => !string.IsNullOrEmpty(c.Display));
                System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                    $"[CssLoader] === CASCADE SUMMARY ===\r\n" +
                    $"[CssLoader] Total nodes styled: {result.Count}\r\n" +
                    $"[CssLoader] Nodes matched by CSS: {matchedNodes}\r\n" +
                    $"[CssLoader] Match rate: {(nodes.Count > 0 ? (100.0 * matchedNodes / nodes.Count).ToString("F1") : "0")}%\r\n" +
                    $"[CssLoader] Nodes with background: {nodesWithBackground}\r\n" +
                    $"[CssLoader] Nodes with color: {nodesWithColor}\r\n" +
                    $"[CssLoader] Nodes with display: {nodesWithDisplay}\r\n"); 
            } catch {}

            return result;
        }

        private static CssComputed ResolveStyle(LiteElement n, CssComputed parentCss, List<Tuple<CssDecl, SelectorChain, int>> items)
        {
            var css = new CssComputed();

            if (parentCss != null && parentCss.CustomProperties != null)
            {
                foreach (var kv in parentCss.CustomProperties)
                {
                    css.CustomProperties[kv.Key] = kv.Value;
                    css.Map[kv.Key] = kv.Value;
                }
            }

            if (items == null || items.Count == 0)
                return css;

            var byProp = items.GroupBy(t => t.Item1.Name, StringComparer.OrdinalIgnoreCase);
            var chosen = new Dictionary<string, CssDecl>(StringComparer.OrdinalIgnoreCase);

            foreach (var grp in byProp)
            {
                var ordered = grp.OrderByDescending(c => c.Item1.Important)
                                 .ThenByDescending(c => c.Item1.Specificity)
                                 .ThenByDescending(c => c.Item3)
                                 .ToList();

                chosen[grp.Key] = ordered[0].Item1;
                
                if (grp.Key.ToLowerInvariant() == "background" && ordered.Count > 1)
                {
                    string cls = "";
                    if (n != null && n.Attr != null) n.Attr.TryGetValue("class", out cls);
                    if (cls != null && cls.Contains("picture"))
                    {
                        try {
                            var orderedInfo = string.Join("; ", ordered.Select(o => $"[val={o.Item1.Value}, spec={o.Item1.Specificity}, srcOrder={o.Item3}]"));
                            // Log removed
                        } catch {}
                    }
                }
            }

            var rawCustom = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in chosen.Values)
            {
                if (IsCustomPropertyName(d.Name))
                    rawCustom[d.Name] = d.Value ?? string.Empty;
            }

            foreach (var key in rawCustom.Keys.ToList())
            {
                var resolvedCustom = ResolveCustomPropertyReferences(rawCustom[key], css, rawCustom, new HashSet<string>(StringComparer.Ordinal) { key });
                rawCustom[key] = resolvedCustom;
                css.CustomProperties[key] = resolvedCustom;
                css.Map[key] = resolvedCustom;
            }

            foreach (var d in chosen.Values)
            {
                if (IsCustomPropertyName(d.Name)) continue;
                var val = ResolveCustomPropertyReferences(d.Value, css, rawCustom, new HashSet<string>());
                css.Map[d.Name] = val;
            }

            // Populate core display/positioning properties from the map
            css.Display = Safe(DictGet(css.Map, "display"))?.ToLowerInvariant();
            css.Position = Safe(DictGet(css.Map, "position"))?.ToLowerInvariant();
            css.FlexDirection = Safe(DictGet(css.Map, "flex-direction"))?.ToLowerInvariant();
            css.FlexWrap = Safe(DictGet(css.Map, "flex-wrap"))?.ToLowerInvariant();
            css.JustifyContent = Safe(DictGet(css.Map, "justify-content"))?.ToLowerInvariant();
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
            css.GridRowStart = Safe(DictGet(css.Map, "grid-row-start"));
            css.GridRowEnd = Safe(DictGet(css.Map, "grid-row-end"));
            
            // Parse grid-column shorthand: grid-column: start / end
            string gridColumn = DictGet(css.Map, "grid-column");
            if (!string.IsNullOrWhiteSpace(gridColumn))
            {
                var parts = gridColumn.Split('/');
                if (parts.Length >= 1 && string.IsNullOrEmpty(css.GridColumnStart))
                    css.GridColumnStart = parts[0].Trim();
                if (parts.Length >= 2 && string.IsNullOrEmpty(css.GridColumnEnd))
                    css.GridColumnEnd = parts[1].Trim();
            }
            
            // Parse grid-row shorthand: grid-row: start / end
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
            
            // Overflow properties
            css.Overflow = Safe(DictGet(css.Map, "overflow"))?.ToLowerInvariant();
            css.OverflowX = Safe(DictGet(css.Map, "overflow-x"))?.ToLowerInvariant() ?? css.Overflow;
            css.OverflowY = Safe(DictGet(css.Map, "overflow-y"))?.ToLowerInvariant() ?? css.Overflow;
            
            // z-index
            double zVal;
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

            double emBase = 16.0;
            if (parentCss != null && parentCss.FontSize.HasValue) emBase = parentCss.FontSize.Value;
            
            double currentEmBase = emBase;
            double fsPx;
            if (TryPx(DictGet(css.Map, "font-size"), out fsPx, emBase)) 
            {
                css.FontSize = fsPx;
                currentEmBase = fsPx;
            }
            else if (parentCss != null && parentCss.FontSize.HasValue)
            {
                css.FontSize = parentCss.FontSize.Value;
                currentEmBase = parentCss.FontSize.Value;
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
            if (TryPx(wStr, out sizeVal, currentEmBase)) css.Width = sizeVal;
            else if (TryPercent(wStr, out sizeVal)) css.WidthPercent = sizeVal;
            else if (IsCssFunction(wStr)) css.WidthExpression = wStr;

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
            
            // DEBUG: Log sizing properties for important elements
            string tag = n?.Tag?.ToUpperInvariant() ?? "";
            string classAttr = n?.Attr != null && n.Attr.TryGetValue("class", out var debugCls) ? debugCls : "";
            if (tag == "FORM" || tag == "MAIN" || tag == "HEADER" || classAttr.Contains("container") || classAttr.Contains("wrapper"))
            {
                string rawMaxW = DictGet(css.Map, "max-width") ?? "null";
                string rawWidth = DictGet(css.Map, "width") ?? "null";
                string rawMargin = DictGet(css.Map, "margin") ?? "null";
                FenLogger.Debug($"[CSS] Sizing parsed: tag={tag} class='{classAttr}' " +
                    $"width={css.Width?.ToString() ?? "null"} widthPct={css.WidthPercent?.ToString() ?? "null"} " +
                    $"maxWidth={css.MaxWidth?.ToString() ?? "null"}(raw='{rawMaxW}') " +
                    $"margin='{rawMargin}'", LogCategory.CSS);
            }

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

            var fgColor = TryColor(DictGet(css.Map, "color"));
            if (fgColor.HasValue) css.ForegroundColor = fgColor;

            var bgRaw = ExtractBackgroundColor(css.Map);
            var bgColor = TryColor(bgRaw);
            if (bgColor.HasValue) 
            {
                css.BackgroundColor = bgColor;
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
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_gradient.txt", $"[CSS] Parsing gradient: {bgImage.Substring(0, Math.Min(100, bgImage.Length))}...\r\n"); } catch { }
                    
                    var grad = ParseGradient(bgImage);
                    if (grad != null) 
                    {
                        css.BackgroundImage = grad;
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_gradient.txt", $"[CSS] Gradient parsed successfully! Type={grad.GetType().Name}\r\n"); } catch { }
                    }
                    else
                    {
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log_gradient.txt", $"[CSS] Gradient parsing FAILED for: {bgImage.Substring(0, Math.Min(100, bgImage.Length))}...\r\n"); } catch { }
                    }
                }
            }

            var fontShorthand = Safe(DictGet(css.Map, "font"));
            if (!string.IsNullOrEmpty(fontShorthand))
            {
                ParseFontShorthand(fontShorthand, css);
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

            css.TextShadow = Safe(DictGet(css.Map, "text-shadow"));
            css.BoxShadow = Safe(DictGet(css.Map, "box-shadow"));
            css.MaskImage = Safe(DictGet(css.Map, "mask-image"));
            if (string.IsNullOrEmpty(css.MaskImage)) css.MaskImage = Safe(DictGet(css.Map, "-webkit-mask-image"));

            Thickness th;
            if (TryThickness(DictGet(css.Map, "margin"), out th, currentEmBase)) css.Margin = th;

            double mVal;
            var m = css.Margin;
            double mLeft = m.Left, mTop = m.Top, mRight = m.Right, mBottom = m.Bottom;
            if (TryPx(DictGet(css.Map, "margin-left"), out mVal, currentEmBase)) mLeft = mVal;
            if (TryPx(DictGet(css.Map, "margin-top"), out mVal, currentEmBase)) mTop = mVal;
            if (TryPx(DictGet(css.Map, "margin-right"), out mVal, currentEmBase)) mRight = mVal;
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
            //     try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[CSS-H2-MARGIN] margin='{rawMarginH2}' margin-top='{rawMarginTop}' computed=L:{mLeft},T:{mTop},R:{mRight},B:{mBottom}\r\n"); } catch {}
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
            CornerRadius cr;
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
            if (TryPx(DictGet(css.Map, "flex-basis"), out flexVal)) css.FlexBasis = flexVal;
            
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
                if (TryPx(lhRaw, out lh)) css.LineHeight = lh;
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
            
            // **CRITICAL FIX**: Assign display, position, and flexbox properties from Map
            // These were previously missing, causing flex container detection to fail
            css.Display = Safe(DictGet(css.Map, "display"));
            css.Position = Safe(DictGet(css.Map, "position"));
            css.Float = Safe(DictGet(css.Map, "float"));
            css.Overflow = Safe(DictGet(css.Map, "overflow"));
            
            // Flexbox properties - critical for Google.com layout
            css.FlexDirection = Safe(DictGet(css.Map, "flex-direction"));
            css.FlexWrap = Safe(DictGet(css.Map, "flex-wrap"));
            css.JustifyContent = Safe(DictGet(css.Map, "justify-content"));
            css.AlignItems = Safe(DictGet(css.Map, "align-items"));
            css.AlignContent = Safe(DictGet(css.Map, "align-content"));
            css.AlignSelf = Safe(DictGet(css.Map, "align-self"));
            
            // Flex item properties
            double flexPropVal;
            if (TryDouble(DictGet(css.Map, "flex-grow"), out flexPropVal)) css.FlexGrow = flexPropVal;
            if (TryDouble(DictGet(css.Map, "flex-shrink"), out flexPropVal)) css.FlexShrink = flexPropVal;
            if (TryPx(DictGet(css.Map, "flex-basis"), out flexPropVal)) css.FlexBasis = flexPropVal;
            if (int.TryParse(DictGet(css.Map, "order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flexOrderVal))
                css.Order = flexOrderVal;


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

            double gapVal2;
            if (TryPx(DictGet(css.Map, "gap"), out gapVal2))
            {
                css.Gap = gapVal2;
                css.RowGap = gapVal2;
                css.ColumnGap = gapVal2;
            }
            if (TryPx(DictGet(css.Map, "row-gap"), out gapVal2)) css.RowGap = gapVal2;
            if (TryPx(DictGet(css.Map, "column-gap"), out gapVal2)) css.ColumnGap = gapVal2;

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

        private static bool HasDebugText(LiteElement n)
        {
            if (n == null) return false;
            // Quick shallow check for debug text
            var stack = new System.Collections.Generic.Stack<LiteElement>();
            stack.Push(n);
            int count = 0;
            while (stack.Count > 0 && count < 50)
            {
                var cur = stack.Pop();
                if (cur.IsText && cur.Text != null && (cur.Text.Contains("Guides") || cur.Text.Contains("Detect my settings")))
                    return true;
                
                if (cur.Children != null)
                {
                    foreach (var c in cur.Children) stack.Push(c); // Depth-first
                }
                count++;
            }
            return false;
        }

        private static bool Matches(LiteElement n, SelectorChain chain)
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
            LiteElement cur = n;

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
                    cur = cur.Parent;
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
                    cur = parent.Children[idx - 1];
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
                        if (MatchesSingle(sib, chain.Segments[segIndex]))
                        {
                            cur = sib;
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

        private static SelectorSegment ParseSegment(List<string> tokens)
        {
            var seg = new SelectorSegment();
            foreach (var t in tokens)
            {
                if (t == " " || t == ">" || t == "+" || t == "~") continue; // Skip combinators
                if (t == "*") { seg.Tag = "*"; continue; }
                if (t.StartsWith("."))
                {
                    seg.Classes.Add(t.Substring(1));
                }
                else if (t.StartsWith("#"))
                {
                    seg.Id = t.Substring(1);
                }
                else if (t.StartsWith("["))
                {
                    // Attribute selector parsing (improved for escapes)
                    var content = t.TrimStart('[').TrimEnd(']');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();
                    string[] operators = { "~=", "|=", "^=", "$=", "*=", "=" };
                    string foundOp = null;
                    int opIndex = -1;
                    foreach (var op in operators)
                    {
                        int idx = content.IndexOf(op);
                        if (idx >= 0)
                        {
                            foundOp = op;
                            opIndex = idx;
                            break;
                        }
                    }
                    if (foundOp == null || opIndex < 0)
                    {
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, opIndex).Trim();
                        var val = content.Substring(opIndex + foundOp.Length).Trim().Trim('"', '\'');
                        // Unescape CSS escapes
                        val = Regex.Replace(val, @"\\(.)", "$1");
                        seg.Attributes.Add(Tuple.Create(name, foundOp, val));
                    }
                }
                else if (t.StartsWith(":"))
                {
                    // Pseudo-class or pseudo-element
                    string val = t.Substring(1).ToLowerInvariant();
                    if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                    seg.PseudoClasses.Add(val);
                }
                else if (!string.IsNullOrEmpty(t))
                {
                    seg.Tag = t;
                }
            }
            return seg;

        }

        private static LiteElement FindAncestorMatching(LiteElement n, SelectorSegment seg)
        {
            var p = n.Parent;
            while (p != null)
            {
                if (MatchesSingle(p, seg)) return p;
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
        private static int GetChildIndex(LiteElement n)
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
        private static int GetTypeIndex(LiteElement n)
        {
            if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag))
                return 0;

            int index = 0;
            foreach (var child in n.Parent.Children)
            {
                if (child.IsText) continue;
                if (string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
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
        private static int GetLastChildIndex(LiteElement n)
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
        private static int GetLastTypeIndex(LiteElement n)
        {
            if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag))
                return 0;

            var sameTypeElements = new System.Collections.Generic.List<LiteElement>();
            foreach (var child in n.Parent.Children)
            {
                if (!child.IsText && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
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
        private static string ExtractPseudoArg(string pseudoClass)
        {
            if (string.IsNullOrEmpty(pseudoClass)) return "";

            int start = pseudoClass.IndexOf('(');
            int end = pseudoClass.LastIndexOf(')');

            if (start >= 0 && end > start)
                return pseudoClass.Substring(start + 1, end - start - 1).Trim();

            return "";
        }

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

        private static bool MatchesSingle(LiteElement n, SelectorSegment seg)
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
                        LiteElement lastOfType = null;
                        foreach (var child in n.Parent.Children)
                        {
                            if (!child.IsText && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
                                lastOfType = child;
                        }
                        if (lastOfType != n) return false;
                    }
                    else if (string.Equals(ps, "only-of-type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n == null || n.Parent == null || n.Parent.Children == null || string.IsNullOrEmpty(n.Tag)) return false;

                        int typeCount = 0;
                        foreach (var child in n.Parent.Children)
                        {
                            if (!child.IsText && string.Equals(child.Tag, n.Tag, StringComparison.OrdinalIgnoreCase))
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
                            current = current.Parent;
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
        private static bool MatchesSingleBasic(LiteElement n, SelectorSegment seg)
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
            string fallback = comma >= 0 ? trimmed.Substring(comma + 1) : null;

            if (string.IsNullOrEmpty(name) || !name.StartsWith("--", StringComparison.Ordinal))
                return ResolveFallback(fallback, current, rawCurrent, seen);

            string resolved;
            string rawValue;
            if (rawCurrent != null && rawCurrent.TryGetValue(name, out rawValue))
            {
                if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                if (seen.Contains(name))
                    return ResolveFallback(fallback, current, rawCurrent, seen);

                seen.Add(name);
                resolved = ResolveCustomPropertyReferences(rawValue, current, rawCurrent, seen);
                seen.Remove(name);
                current.CustomProperties[name] = resolved;
                return resolved;
            }

            if (current != null && current.CustomProperties != null && current.CustomProperties.TryGetValue(name, out resolved))
                return resolved;

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
            if (!TryPx(parts[0], out first)) return false;
            row = first;
            column = first;

            if (parts.Count > 1)
            {
                double second;
                if (TryPx(parts[1], out second)) column = second;
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

        private static bool TryCornerRadius(string raw, out CornerRadius radius)
        {
            radius = new CornerRadius(0);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var main = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            var parts = main.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            double tl, tr, br, bl;
            switch (parts.Length)
            {
                case 1:
                    if (TryCornerComponent(parts[0], out tl))
                    {
                        radius = new CornerRadius(tl);
                        return true;
                    }
                    return false;
                case 2:
                    if (TryCornerComponent(parts[0], out tl) && TryCornerComponent(parts[1], out tr))
                    {
                        radius = new CornerRadius(tl, tr, tl, tr);
                        return true;
                    }
                    return false;
                case 3:
                    if (TryCornerComponent(parts[0], out tl) && TryCornerComponent(parts[1], out tr) && TryCornerComponent(parts[2], out br))
                    {
                        radius = new CornerRadius(tl, tr, br, tr);
                        return true;
                    }
                    return false;
                default:
                    if (TryCornerComponent(parts[0], out tl) &&
                        TryCornerComponent(parts[1], out tr) &&
                        TryCornerComponent(parts[2], out br) &&
                        TryCornerComponent(parts[3], out bl))
                    {
                        radius = new CornerRadius(tl, tr, br, bl);
                        return true;
                    }
                    return false;
            }
        }

        private static bool TryCornerComponent(string raw, out double value)
        {
            value = 0;
            if (TryPx(raw, out value)) return true;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            raw = raw.Trim();
            if (raw.EndsWith("%", StringComparison.Ordinal))
            {
                double pct;
                if (TryDouble(raw.TrimEnd('%'), out pct))
                {
                    value = Math.Max(0, pct);
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

        private static string SafeGatherText(LiteElement n)
        {
            if (n == null) return null;
            var sb = new StringBuilder();
            
            // Gather text from this node itself (e.g. #text, #cdata, or comments)
            if (!string.IsNullOrEmpty(n.Text)) sb.Append(n.Text);
            
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



        private static bool TryDouble(string s, out double v)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private static bool TryPx(string s, out double px, double emBase = 16.0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            var sl = s.ToLowerInvariant();

            // Handle calc() expressions
            if (sl.StartsWith("calc("))
            {
                return TryParseCalc(s, out px, emBase);
            }

            // px
            if (sl.EndsWith("px"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v; return true; }
                return false;
            }

            // rem (baseline 16px)
            if (sl.EndsWith("rem"))
            {
                var num = s.Substring(0, s.Length - 3).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * 16.0; return true; }
                return false;
            }

            // em (uses provided base, usually 16px if root or inherited)
            if (sl.EndsWith("em") && !sl.EndsWith("rem"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * emBase; return true; }
                return false;
            }

            // Viewport units - use CssParser.MediaViewportWidth/Height if available
            double vpWidth = CssParser.MediaViewportWidth ?? 1920.0;
            double vpHeight = CssParser.MediaViewportHeight ?? 1080.0;

            // vw (viewport width percentage)
            if (sl.EndsWith("vw"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * vpWidth / 100.0; return true; }
                return false;
            }

            // vh (viewport height percentage)
            if (sl.EndsWith("vh"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * vpHeight / 100.0; return true; }
                return false;
            }

            // vmin (smaller of vw or vh)
            if (sl.EndsWith("vmin"))
            {
                var num = s.Substring(0, s.Length - 4).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * Math.Min(vpWidth, vpHeight) / 100.0; return true; }
                return false;
            }

            // vmax (larger of vw or vh)
            if (sl.EndsWith("vmax"))
            {
                var num = s.Substring(0, s.Length - 4).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * Math.Max(vpWidth, vpHeight) / 100.0; return true; }
                return false;
            }

            // ch (width of '0' character, approximate as 0.5em)
            if (sl.EndsWith("ch"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * emBase * 0.5; return true; }
                return false;
            }

            // ex (x-height, approximate as 0.5em)
            if (sl.EndsWith("ex"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * emBase * 0.5; return true; }
                return false;
            }

            // pt (points: 1pt = 1/72 inch at 96 DPI = 96/72 = 1.333... px)
            if (sl.EndsWith("pt"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * (96.0 / 72.0); return true; }
                return false;
            }

            // pc (picas: 1pc = 12pt = 16px)
            if (sl.EndsWith("pc"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * 16.0; return true; }
                return false;
            }

            // in (inches: 1in = 96px at 96 DPI)
            if (sl.EndsWith("in"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * 96.0; return true; }
                return false;
            }

            // cm (centimeters: 1cm = 96/2.54 px)
            if (sl.EndsWith("cm"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * (96.0 / 2.54); return true; }
                return false;
            }

            // mm (millimeters: 1mm = 96/25.4 px)
            if (sl.EndsWith("mm"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * (96.0 / 25.4); return true; }
                return false;
            }

            // raw number -> px
            {
                double v;
                if (TryDouble(sl, out v)) { px = v; return true; }
            }
            return false;
        }

        /// <summary>
        /// Parse CSS calc() expressions like calc(100% - 40px), calc(100vh - 60px), etc.
        /// Supports: +, -, *, / operators and px, em, rem, %, vw, vh, vmin, vmax units
        /// </summary>
        private static bool TryParseCalc(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Extract content between calc( and )
            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("calc(") || !sl.EndsWith(")")) return false;
            
            var expr = s.Substring(5, s.Length - 6).Trim();
            if (string.IsNullOrWhiteSpace(expr)) return false;

            try
            {
                px = EvaluateCalcExpression(expr, emBase, percentBase);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse CSS min() function: min(value1, value2, ...)
        /// Returns the smallest of the provided values
        /// </summary>
        private static bool TryParseMin(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("min(") || !sl.EndsWith(")")) return false;
            
            var inner = s.Substring(4, s.Length - 5).Trim();
            if (string.IsNullOrWhiteSpace(inner)) return false;

            try
            {
                var args = SplitCssFunctionArgs(inner);
                if (args.Count == 0) return false;
                
                double minVal = double.MaxValue;
                foreach (var arg in args)
                {
                    double val = EvaluateCssValue(arg.Trim(), emBase, percentBase);
                    if (val < minVal) minVal = val;
                }
                px = minVal;
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse CSS max() function: max(value1, value2, ...)
        /// Returns the largest of the provided values
        /// </summary>
        private static bool TryParseMax(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("max(") || !sl.EndsWith(")")) return false;
            
            var inner = s.Substring(4, s.Length - 5).Trim();
            if (string.IsNullOrWhiteSpace(inner)) return false;

            try
            {
                var args = SplitCssFunctionArgs(inner);
                if (args.Count == 0) return false;
                
                double maxVal = double.MinValue;
                foreach (var arg in args)
                {
                    double val = EvaluateCssValue(arg.Trim(), emBase, percentBase);
                    if (val > maxVal) maxVal = val;
                }
                px = maxVal;
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse CSS clamp() function: clamp(min, preferred, max)
        /// Clamps the preferred value between min and max
        /// </summary>
        private static bool TryParseClamp(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("clamp(") || !sl.EndsWith(")")) return false;
            
            var inner = s.Substring(6, s.Length - 7).Trim();
            if (string.IsNullOrWhiteSpace(inner)) return false;

            try
            {
                var args = SplitCssFunctionArgs(inner);
                if (args.Count != 3) return false;
                
                double minVal = EvaluateCssValue(args[0].Trim(), emBase, percentBase);
                double preferred = EvaluateCssValue(args[1].Trim(), emBase, percentBase);
                double maxVal = EvaluateCssValue(args[2].Trim(), emBase, percentBase);
                
                // clamp(min, preferred, max) = max(min, min(preferred, max))
                px = Math.Max(minVal, Math.Min(preferred, maxVal));
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Split CSS function arguments, handling nested functions
        /// </summary>
        private static List<string> SplitCssFunctionArgs(string inner)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            
            foreach (char c in inner)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                
                if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            
            if (current.Length > 0)
                args.Add(current.ToString());
                
            return args;
        }
        
        /// <summary>
        /// Evaluate a CSS value that may contain functions like calc(), min(), max(), clamp(), env()
        /// </summary>
        private static double EvaluateCssValue(string value, double emBase = 16.0, double percentBase = 0)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            
            value = value.Trim();
            var lower = value.ToLowerInvariant();
            
            // Try CSS math functions
            if (lower.StartsWith("calc(") && TryParseCalc(value, out double calcVal, emBase, percentBase))
                return calcVal;
            if (lower.StartsWith("min(") && TryParseMin(value, out double minVal, emBase, percentBase))
                return minVal;
            if (lower.StartsWith("max(") && TryParseMax(value, out double maxVal, emBase, percentBase))
                return maxVal;
            if (lower.StartsWith("clamp(") && TryParseClamp(value, out double clampVal, emBase, percentBase))
                return clampVal;
            
            // env() function - environment variables
            if (lower.StartsWith("env(") && TryParseEnv(value, out double envVal))
                return envVal;
            
            // CSS Values Level 4 math functions
            if (lower.StartsWith("abs(") && TryParseMathFunc(value, "abs", out double absVal, emBase, percentBase))
                return Math.Abs(absVal);
            if (lower.StartsWith("sign(") && TryParseMathFunc(value, "sign", out double signVal, emBase, percentBase))
                return Math.Sign(signVal);
            if (lower.StartsWith("round(") && TryParseRound(value, out double roundVal, emBase, percentBase))
                return roundVal;
            if (lower.StartsWith("mod(") && TryParseMod(value, out double modVal, emBase, percentBase))
                return modVal;
            if (lower.StartsWith("rem(") && TryParseRem(value, out double remVal, emBase, percentBase))
                return remVal;
            if (lower.StartsWith("pow(") && TryParsePow(value, out double powVal, emBase, percentBase))
                return powVal;
            if (lower.StartsWith("sqrt(") && TryParseMathFunc(value, "sqrt", out double sqrtVal, emBase, percentBase))
                return Math.Sqrt(sqrtVal);
            if (lower.StartsWith("log(") && TryParseMathFunc(value, "log", out double logVal, emBase, percentBase))
                return Math.Log(logVal);
            if (lower.StartsWith("exp(") && TryParseMathFunc(value, "exp", out double expVal, emBase, percentBase))
                return Math.Exp(expVal);
            
            // CSS Trigonometric functions (input in degrees, output unitless)
            if (lower.StartsWith("sin(") && TryParseMathFunc(value, "sin", out double sinVal, emBase, percentBase))
                return Math.Sin(sinVal * Math.PI / 180.0);
            if (lower.StartsWith("cos(") && TryParseMathFunc(value, "cos", out double cosVal, emBase, percentBase))
                return Math.Cos(cosVal * Math.PI / 180.0);
            if (lower.StartsWith("tan(") && TryParseMathFunc(value, "tan", out double tanVal, emBase, percentBase))
                return Math.Tan(tanVal * Math.PI / 180.0);
            if (lower.StartsWith("asin(") && TryParseMathFunc(value, "asin", out double asinVal, emBase, percentBase))
                return Math.Asin(asinVal) * 180.0 / Math.PI; // Output in degrees
            if (lower.StartsWith("acos(") && TryParseMathFunc(value, "acos", out double acosVal, emBase, percentBase))
                return Math.Acos(acosVal) * 180.0 / Math.PI;
            if (lower.StartsWith("atan(") && TryParseMathFunc(value, "atan", out double atanVal, emBase, percentBase))
                return Math.Atan(atanVal) * 180.0 / Math.PI;
            if (lower.StartsWith("atan2(") && TryParseAtan2(value, out double atan2Val, emBase, percentBase))
                return atan2Val;
            if (lower.StartsWith("hypot(") && TryParseHypot(value, out double hypotVal, emBase, percentBase))
                return hypotVal;
            
            // Try simple value with units
            return ParseCalcValue(value, emBase, percentBase);
        }

/// <summary>
/// Parse a single-argument math function like abs(), sign(), sqrt(), etc.
/// </summary>
private static bool TryParseMathFunc(string value, string funcName, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    result = EvaluateCssValue(inner, emBase, percentBase);
    return true;
}

/// <summary>
/// Parse round() function: round(strategy?, value, interval?)
/// Strategies: nearest (default), up, down, to-zero
/// </summary>
private static bool TryParseRound(string value, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    var parts = SplitCssFunctionArgs(inner);
    
    if (parts.Count == 0) return false;
    
    string strategy = "nearest";
    double val, interval = 1;
    
    if (parts.Count >= 3)
    {
        strategy = parts[0].Trim().ToLowerInvariant();
        val = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
        interval = EvaluateCssValue(parts[2].Trim(), emBase, percentBase);
    }
    else if (parts.Count == 2)
    {
        val = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
        interval = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
    }
    else
    {
        val = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
    }
    
    if (interval == 0) interval = 1;
    
    switch (strategy)
    {
        case "up":
            result = Math.Ceiling(val / interval) * interval;
            break;
        case "down":
            result = Math.Floor(val / interval) * interval;
            break;
        case "to-zero":
            result = Math.Truncate(val / interval) * interval;
            break;
        default: // nearest
            result = Math.Round(val / interval) * interval;
            break;
    }
    return true;
}

/// <summary>
/// Parse mod() function: mod(dividend, divisor) - like % but always positive
/// </summary>
private static bool TryParseMod(string value, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    var parts = SplitCssFunctionArgs(inner);
    
    if (parts.Count < 2) return false;
    
    double dividend = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
    double divisor = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
    
    if (divisor == 0) return false;
    
    // CSS mod() uses floored division (always towards -infinity)
    result = dividend - divisor * Math.Floor(dividend / divisor);
    return true;
}

/// <summary>
/// Parse rem() function: rem(dividend, divisor) - like % with sign of dividend
/// </summary>
private static bool TryParseRem(string value, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    var parts = SplitCssFunctionArgs(inner);
    
    if (parts.Count < 2) return false;
    
    double dividend = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
    double divisor = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
    
    if (divisor == 0) return false;
    
    // CSS rem() uses truncated division (sign of dividend)
    result = dividend % divisor;
    return true;
}

/// <summary>
/// Parse pow() function: pow(base, exponent)
/// </summary>
private static bool TryParsePow(string value, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    var parts = SplitCssFunctionArgs(inner);
    
    if (parts.Count < 2) return false;
    
    double baseVal = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
    double exponent = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
    
    result = Math.Pow(baseVal, exponent);
    return true;
}

/// <summary>
/// Parse atan2() function: atan2(y, x) - returns angle in degrees
/// </summary>
private static bool TryParseAtan2(string value, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    var parts = SplitCssFunctionArgs(inner);
    
    if (parts.Count < 2) return false;
    
    double y = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
    double x = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
    
    result = Math.Atan2(y, x) * 180.0 / Math.PI; // Output in degrees
    return true;
}

/// <summary>
/// Parse hypot() function: hypot(value1, value2, ...) - hypotenuse of values
/// </summary>
private static bool TryParseHypot(string value, out double result, double emBase = 16.0, double percentBase = 0)
{
    result = 0;
    int open = value.IndexOf('(');
    int close = value.LastIndexOf(')');
    if (open < 0 || close <= open) return false;
    
    var inner = value.Substring(open + 1, close - open - 1).Trim();
    var parts = SplitCssFunctionArgs(inner);
    
    if (parts.Count == 0) return false;
    
    double sumOfSquares = 0;
    foreach (var part in parts)
    {
        double v = EvaluateCssValue(part.Trim(), emBase, percentBase);
        sumOfSquares += v * v;
    }
    
    result = Math.Sqrt(sumOfSquares);
    return true;
}

        /// <summary>
        /// Parse env() function for environment variables like safe-area-inset-*
        /// </summary>
        private static bool TryParseEnv(string value, out double result)
        {
            result = 0;
            
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            
            // Split by comma to get variable name and optional fallback
            var parts = SplitCssFunctionArgs(inner);
            if (parts.Count == 0) return false;
            
            string varName = parts[0].Trim().ToLowerInvariant();
            
            // Environment variable values (0 for now, could be made configurable)
            switch (varName)
            {
                case "safe-area-inset-top":
                case "safe-area-inset-bottom":
                case "safe-area-inset-left":
                case "safe-area-inset-right":
                    result = 0; // Default to 0 (no notch/safe area)
                    return true;
                    
                case "titlebar-area-x":
                case "titlebar-area-y":
                case "titlebar-area-width":
                case "titlebar-area-height":
                    result = 0; // PWA titlebar area
                    return true;
                    
                case "keyboard-inset-top":
                case "keyboard-inset-bottom":
                case "keyboard-inset-left":
                case "keyboard-inset-right":
                case "keyboard-inset-width":
                case "keyboard-inset-height":
                    result = 0; // Virtual keyboard dimensions
                    return true;
                    
                default:
                    // Unknown env variable - try fallback if provided
                    if (parts.Count > 1)
                    {
                        if (TryPx(parts[1].Trim(), out result))
                            return true;
                    }
                    return false;
            }
        }
        
        /// <summary>
        /// Evaluate a calc expression supporting +, -, *, / with proper operator precedence
        /// </summary>
        private static double EvaluateCalcExpression(string expr, double emBase, double percentBase)
        {
            // Tokenize the expression
            var tokens = TokenizeCalcExpression(expr);
            if (tokens.Count == 0) return 0;

            // Convert to postfix notation (Shunting-yard algorithm) and evaluate
            var output = new Stack<double>();
            var operators = new Stack<char>();

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                if (IsCalcOperator(token))
                {
                    char op = token[0];
                    while (operators.Count > 0 && ShouldPopOperator(operators.Peek(), op))
                    {
                        ApplyOperator(output, operators.Pop());
                    }
                    operators.Push(op);
                }
                else
                {
                    // It's a value with potential unit
                    double val = ParseCalcValue(token, emBase, percentBase);
                    output.Push(val);
                }
            }

            while (operators.Count > 0)
            {
                ApplyOperator(output, operators.Pop());
            }

            return output.Count > 0 ? output.Pop() : 0;
        }

        private static List<string> TokenizeCalcExpression(string expr)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];

                if (c == ' ' || c == '\t')
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                // Handle operators - they must be surrounded by spaces in calc(), but handle anyway
                if ((c == '+' || c == '-') && current.Length > 0 && !char.IsDigit(expr[Math.Max(0, i - 1)]))
                {
                    // This is an operator
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    tokens.Add(c.ToString());
                    continue;
                }
                else if ((c == '+' || c == '-') && current.Length == 0)
                {
                    // Could be a sign or operator - check if there's a previous token
                    if (tokens.Count > 0 && !IsCalcOperator(tokens[tokens.Count - 1]))
                    {
                        tokens.Add(c.ToString());
                        continue;
                    }
                    // Otherwise it's a sign, part of the number
                    current.Append(c);
                    continue;
                }
                else if (c == '*' || c == '/')
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    tokens.Add(c.ToString());
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }
            return tokens;
        }

        private static double ParseCalcValue(string token, double emBase, double percentBase)
        {
            token = token.Trim().ToLowerInvariant();
            double v;
            double vpWidth = CssParser.MediaViewportWidth ?? 1920.0;
            double vpHeight = CssParser.MediaViewportHeight ?? 1080.0;

            if (token.EndsWith("rem"))
            {
                if (TryDouble(token.Substring(0, token.Length - 3), out v)) return v * 16.0;
            }
            else if (token.EndsWith("em"))
            {
                if (TryDouble(token.Substring(0, token.Length - 2), out v)) return v * emBase;
            }
            else if (token.EndsWith("vw"))
            {
                if (TryDouble(token.Substring(0, token.Length - 2), out v)) return v * vpWidth / 100.0;
            }
            else if (token.EndsWith("vh"))
            {
                if (TryDouble(token.Substring(0, token.Length - 2), out v)) return v * vpHeight / 100.0;
            }
            else if (token.EndsWith("vmin"))
            {
                if (TryDouble(token.Substring(0, token.Length - 4), out v)) return v * Math.Min(vpWidth, vpHeight) / 100.0;
            }
            else if (token.EndsWith("vmax"))
            {
                if (TryDouble(token.Substring(0, token.Length - 4), out v)) return v * Math.Max(vpWidth, vpHeight) / 100.0;
            }
            else if (token.EndsWith("%"))
            {
                if (TryDouble(token.Substring(0, token.Length - 1), out v)) return v * percentBase / 100.0;
            }
            else
            {
                // Raw number (treated as px)
                if (TryDouble(token, out v)) return v;
            }
            return 0;
        }

        private static bool IsCalcOperator(string token)
        {
            return token == "+" || token == "-" || token == "*" || token == "/";
        }

        private static bool ShouldPopOperator(char stackTop, char newOp)
        {
             int p1 = GetPriority(stackTop);
             int p2 = GetPriority(newOp);
             return p1 >= p2;
        }

        private static int GetPriority(char op)
        {
            if (op == '*' || op == '/') return 2;
            if (op == '+' || op == '-') return 1;
            return 0;
        }

        private static void ApplyOperator(Stack<double> output, char op)
        {
            if (output.Count < 2) return;
            double right = output.Pop();
            double left = output.Pop();
            double res = 0;
            switch(op)
            {
                case '+': res = left + right; break;
                case '-': res = left - right; break;
                case '*': res = left * right; break;
                case '/': res = right != 0 ? left / right : 0; break;
            }
            output.Push(res);
        }


        private static bool TryThickness(string s, out Thickness th, double emBase = 16.0)
        {
            th = new Thickness(0);
            if (string.IsNullOrWhiteSpace(s)) return false;

            var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double a, b, c, d;

            if (parts.Length == 1) { if (!TryPx(parts[0], out a, emBase)) a = 0; th = new Thickness(a); return true; }
            if (parts.Length == 2)
            {
                if (!TryPx(parts[0], out a, emBase)) a = 0;
                if (!TryPx(parts[1], out b, emBase)) b = 0;
                th = new Thickness(b, a, b, a); return true;
            }
            if (parts.Length == 3)
            {
                if (!TryPx(parts[0], out a, emBase)) a = 0;
                if (!TryPx(parts[1], out b, emBase)) b = 0;
                if (!TryPx(parts[2], out c, emBase)) c = 0;
                th = new Thickness(b, a, b, c); return true;
            }
            // 4+
            if (!TryPx(parts[0], out a, emBase)) a = 0;
            if (!TryPx(parts[1], out b, emBase)) b = 0;
            if (!TryPx(parts[2], out c, emBase)) c = 0;
            if (!TryPx(parts[3], out d, emBase)) d = 0;
            th = new Thickness(d, a, b, c); return true;
        }

        private static SKColor FromHex(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 3) // #abc -> #aabbcc
            {
                var r = Convert.ToByte(new string(hex[0], 2), 16);
                var g = Convert.ToByte(new string(hex[1], 2), 16);
                var b = Convert.ToByte(new string(hex[2], 2), 16);
                return new SKColor(r, g, b, 255);
            }
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return new SKColor(r, g, b, 255);
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return new SKColor(r, g, b, a);
            }
            return SKColors.Black;
        }

        private static SKColor? TryColor(string css)
        {
            return CssParser.ParseColor(css);
        }



        private static string ExtractBackgroundColor(Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("background-color", out v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (map.TryGetValue("background", out v) && !string.IsNullOrWhiteSpace(v))
            {
                // Find all potential color matches
                // Matches hex, rgb/rgba, or named colors (letters only)
                var matches = Regex.Matches(v, @"(#[0-9a-fA-F]{3,8}|rgba?\([^)]+\)|[a-zA-Z]+)");
                foreach (Match m in matches)
                {
                    var val = m.Value;
                    // Ignore common keywords that might look like colors but aren't
                    if (val.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("repeat", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("fixed", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("center", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("top", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("bottom", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("right", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return val;
                }
            }
            return null;
        }

        private static string ExtractBorderColor(Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("border-color", out v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (map.TryGetValue("border", out v) && !string.IsNullOrWhiteSpace(v))
            {
                // Find all potential color matches
                var matches = Regex.Matches(v, @"(#[0-9a-fA-F]{3,8}|rgba?\([^)]+\)|[a-zA-Z]+)");
                foreach (Match m in matches)
                {
                    var val = m.Value;
                    if (IsBorderStyle(val)) continue;
                    // Ignore width keywords
                    if (val.Equals("thin", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
                        val.Equals("thick", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith("em", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return val;
                }
                // If no color found but border is present, default to current color (black usually)
                // But we return null here and let the renderer decide or default to black if border width > 0
                return null; 
            }
            return null;
        }

        private static bool IsBorderStyle(string s)
        {
            s = s.ToLowerInvariant();
            return s == "none" || s == "hidden" || s == "dotted" || s == "dashed" ||
                   s == "solid" || s == "double" || s == "groove" || s == "ridge" ||
                   s == "inset" || s == "outset";
        }

        private static bool TryPercent(string s, out double pct)
        {
            pct = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.EndsWith("%"))
            {
                var num = s.Substring(0, s.Length - 1).Trim();
                return TryDouble(num, out pct);
            }
            return false;
        }

        private static string ExtractBorderThickness(Dictionary<string, string> map)
        {
            string v;
            if (map.TryGetValue("border-width", out v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (map.TryGetValue("border", out v) && !string.IsNullOrWhiteSpace(v))
            {
                // Check for keywords first
                if (v.IndexOf("thin", StringComparison.OrdinalIgnoreCase) >= 0) return "1px";
                if (v.IndexOf("medium", StringComparison.OrdinalIgnoreCase) >= 0) return "3px";
                if (v.IndexOf("thick", StringComparison.OrdinalIgnoreCase) >= 0) return "5px";

                // naive: pick first length
                var m = Regex.Match(v, @"([0-9.]+)(px|em|rem)");
                if (m.Success) return m.Groups[0].Value;
            }
            return null;
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

        private static void ResolveVariables(List<CssRule> rules)
        {
            if (rules == null) return;
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);

            // 1. Collect global variables from :root
            foreach (var rule in rules)
            {
                if (rule.Selectors == null) continue;
                foreach (var sel in rule.Selectors)
                {
                    bool isRoot = false;
                    if (sel.Segments != null && sel.Segments.Count == 1)
                    {
                        var seg = sel.Segments[0];
                        if (string.Equals(seg.Tag, ":root", StringComparison.OrdinalIgnoreCase)) isRoot = true;
                    }

                    if (isRoot && rule.Declarations != null)
                    {
                        foreach (var kvp in rule.Declarations)
                        {
                            if (kvp.Key.StartsWith("--"))
                            {
                                vars[kvp.Key] = kvp.Value.Value;
                            }
                        }
                    }
                }
            }

            if (vars.Count == 0) return;

            // 2. Substitute in all rules
            foreach (var rule in rules)
            {
                if (rule.Declarations == null) continue;
                foreach (var kvp in rule.Declarations.ToList())
                {
                    var val = kvp.Value.Value;
                    if (val != null && val.IndexOf("var(--", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Manual parsing to handle nested parenthesis and fallbacks
                        var sb = new StringBuilder();
                        int lastPos = 0;
                        int i = 0;
                        while ((i = val.IndexOf("var(--", i, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            sb.Append(val.Substring(lastPos, i - lastPos));
                            
                            // Find closing parenthesis accounting for nesting
                            int open = i + 3; // pointing to '(' of var(
                            int balance = 0;
                            int j = open + 1;
                            bool foundClose = false;
                            
                            for (; j < val.Length; j++)
                            {
                                if (val[j] == '(') balance++;
                                else if (val[j] == ')')
                                {
                                    if (balance == 0) { foundClose = true; break; }
                                    balance--;
                                }
                            }
                            
                            if (foundClose)
                            {
                                string content = val.Substring(open + 1, j - open - 1);
                                string varName = content;
                                string fallback = null;
                                
                                int comma = content.IndexOf(',');
                                if (comma > 0)
                                {
                                    varName = content.Substring(0, comma).Trim();
                                    fallback = content.Substring(comma + 1).Trim();
                                }
                                else
                                {
                                    varName = varName.Trim();
                                }
                                
                                string resolved = null;
                                if (vars.TryGetValue(varName, out var sub)) resolved = sub;
                                else if (fallback != null) resolved = fallback;
                                
                                sb.Append(resolved ?? $"var({content})");
                                
                                i = j + 1;
                                lastPos = i;
                            }
                            else
                            {
                                // No matching close, skip
                                sb.Append(val.Substring(i, 4)); // var(
                                i += 4;
                                lastPos = i;
                            }
                        }
                        sb.Append(val.Substring(lastPos));
                        var newVal = sb.ToString();

                        if (newVal != val)
                        {
                            kvp.Value.Value = newVal;
                        }
                    }
                }
            }
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
        try { lock (_logLock) { System.IO.File.AppendAllText(filename, message); } } catch { }
    }

    /// <summary>
    /// Matches a SelectorChain against an element (for compound :not() support)
    /// </summary>
    private static bool MatchesSelectorChain(SelectorChain chain, LiteElement n)
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

    private static bool MatchesSelectorList(LiteElement n, string selectorList)
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

    private static bool MatchesHas(LiteElement n, string selectorList)
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

        var queue = new Queue<LiteElement>();
        if (n.Children != null)
        {
            foreach(var c in n.Children) queue.Enqueue(c);
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
                foreach (var c in curr.Children) queue.Enqueue(c);
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
    private static string ResolveAttr(string value, LiteElement n)
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
}
}
