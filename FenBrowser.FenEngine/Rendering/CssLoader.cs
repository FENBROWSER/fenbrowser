using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using FenBrowser.Core;
using System.Globalization;
namespace FenBrowser.FenEngine.Rendering
{
    public static class CssLoader
    {
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
            if (root == null)
                return new Dictionary<LiteElement, CssComputed>();

            var cssBlobs = new List<CssSource>(); // collected CSS texts with source ordering
            int sourceIndex = 0;

            // 0) UA stylesheet (very small normalize) � lowest precedence
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
                    // System.Diagnostics.Debug.WriteLine($"[CssLoader] Found style block: {text.Length} chars");
                    // System.Diagnostics.Debug.WriteLine($"[CssLoader] Style content: {text.Substring(0, Math.Min(text.Length, 100))}...");
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
            var extTasks = new List<Task>();
            var gate = new System.Threading.SemaphoreSlim(8); // Shared gate for all CSS fetches (links + imports)
            foreach (var link in linkNodes)
            {
                if (link.Attr == null) continue;
                string rel; if (!link.Attr.TryGetValue("rel", out rel)) continue; if (!ContainsToken(rel, "stylesheet")) continue;
                string href; if (!link.Attr.TryGetValue("href", out href) || string.IsNullOrWhiteSpace(href)) continue;
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
            if (extTasks.Count > 0) { try { await Task.WhenAll(extTasks).ConfigureAwait(false); } catch { /* Ignore fetch errors */ } }

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
            if (parseTasks.Count > 0) { try { await Task.WhenAll(parseTasks).ConfigureAwait(false); } catch { /* Ignore parse errors */ } }

            // 4.5) Resolve CSS variables
            ResolveVariables(allRules);

            // 5) Compute per-element cascaded styles
            var computed = CascadeIntoComputedStyles(root, allRules, log);

            return computed;
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

        private enum CssOrigin
        {
            Inline, External, Imported,
            UserAgent
        }

        private sealed class CssSource
        {
            public string CssText;
            public CssOrigin Origin;
            public int SourceOrder;
            public Uri BaseUri;
        }

        private sealed class CssRule
        {
            public List<SelectorChain> Selectors = new List<SelectorChain>(); // comma-separated selectors
            public Dictionary<string, CssDecl> Declarations = new Dictionary<string, CssDecl>(StringComparer.OrdinalIgnoreCase);
            public int SourceOrder;    // to break ties
            public Uri BaseUri;        // for url() resolving
        }

        private sealed class CssDecl
        {
            public string Name;       // canonicalized (lowercase)
            public string Value;      // raw value
            public bool Important;    // !important
            public int Specificity;   // computed from selector where used
        }

        /// <summary>Single selector fragment with combinators, e.g. "div.foo #bar > span"</summary>
        private sealed class SelectorChain
        {
            public List<SelectorSegment> Segments = new List<SelectorSegment>(); // left-to-right parsed
            public int Specificity; // computed from segments
        }

        private enum Combinator { Descendant, Child }

        private sealed class SelectorSegment
        {
            public string Tag;                    // e.g. "div"
            public string Id;                     // e.g. "main"
            public List<string> Classes;          // e.g. ["foo","bar"]
            public List<string> PseudoClasses;    // e.g. [":first-child"]
            public List<Tuple<string, string, string>> Attributes; // e.g. [("type", "=", "text")]
            public Combinator? Next;              // relation to the NEXT segment (left-to-right)
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

        // ===========================
        // Stage 2: Parsing rules
        // ===========================

        private static List<CssRule> ParseRules(string css, int sourceOrder, Uri baseForUrls, double? viewportWidth, Action<string> log)
        {
            var rules = new List<CssRule>();
            if (string.IsNullOrWhiteSpace(css)) return rules;

            var text = StripComments(css);

            // (Very) basic @media handling: keep simple "screen" blocks; ignore others.
            // We flatten recognized @media blocks by inlining their contents.
            text = FlattenBasicMedia(text, viewportWidth, log);

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

            // Extremely conservative: we only pass through blocks like @media screen and (min-width: Xpx)/(max-width: Xpx)
            // and we do a trivial check against viewportWidth when provided.
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

                    bool keep = true;
                    if (viewportWidth.HasValue)
                    {
                        // check simple min/max-width conditions if present
                        var mw = ExtractPx(header, "min-width");
                        var xw = ExtractPx(header, "max-width");
                        if (mw.HasValue && viewportWidth.Value < mw.Value) keep = false;
                        if (xw.HasValue && viewportWidth.Value > xw.Value) keep = false;
                    }

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
            // Split on commas at top level (not inside anything else � here we assume plain selectors).
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
                    if (seg.PseudoClasses == null) seg.PseudoClasses = new List<string>();
                    seg.PseudoClasses.Add(t.Substring(1));
                }
                else if (t.StartsWith("["))
                {
                    // Basic attribute parsing: [attr] or [attr=val]
                    // Tokenizer likely returns "[attr=val]" or "[attr" ...
                    // We assume simple "[content]" format from tokenizer for now
                    var content = t.TrimStart('[').TrimEnd(']');
                    var eq = content.IndexOf('=');
                    if (seg.Attributes == null) seg.Attributes = new List<Tuple<string, string, string>>();

                    if (eq < 0)
                    {
                        seg.Attributes.Add(Tuple.Create(content.Trim(), "", ""));
                    }
                    else
                    {
                        var name = content.Substring(0, eq).Trim();
                        var val = content.Substring(eq + 1).Trim().Trim('"', '\'');
                        seg.Attributes.Add(Tuple.Create(name, "=", val));
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
            // turns "div#main .x > span.y" into ["div","#main"," ",".x",">","span",".y"]
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
                else if (c == '>')
                {
                    flush(); r.Add(">");
                }
                else if (c == '.' || c == '#' || c == ':' || c == '[')
                {
                    flush(); sb.Append(c);
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
            foreach (var n in nodes)
            {
                if (n.IsText) continue;

                foreach (var rule in rules)
                {
                    foreach (var chain in rule.Selectors)
                    {
                        if (Matches(n, chain))
                        {
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
                                if (!perNode.TryGetValue(n, out list))
                                {
                                    list = new List<Tuple<CssDecl, SelectorChain, int>>();
                                    perNode[n] = list;
                                }
                                list.Add(Tuple.Create(d, chain, rule.SourceOrder));
                            }
                        }
                    }
                }
            }

            // Inline style beats author rules; include as highest priority �rule�
            foreach (var n in nodes)
            {
                if (n.IsText) continue;
                string style;
                if (n.Attr != null && n.Attr.TryGetValue("style", out style) && !string.IsNullOrWhiteSpace(style))
                {
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

                List<Tuple<CssDecl, SelectorChain, int>> items;
                if (!perNode.TryGetValue(n, out items) || items == null || items.Count == 0)
                    continue;

                // group by property name
                var byProp = items.GroupBy(t => t.Item1.Name, StringComparer.OrdinalIgnoreCase);
                var chosen = new Dictionary<string, CssDecl>(StringComparer.OrdinalIgnoreCase);

                foreach (var grp in byProp)
                {
                    var ordered = grp.OrderByDescending(c => c.Item1.Important)
                                     .ThenByDescending(c => c.Item1.Specificity)
                                     .ThenByDescending(c => c.Item3) // higher sourceOrder = later in document = wins
                                     .ToList();

                    chosen[grp.Key] = ordered[0].Item1;
                }

                CssComputed parentCss = null;
                if (n.Parent != null)
                    result.TryGetValue(n.Parent, out parentCss);

                var css = new CssComputed();
                if (parentCss != null && parentCss.CustomProperties != null)
                {
                    foreach (var kv in parentCss.CustomProperties)
                    {
                        css.CustomProperties[kv.Key] = kv.Value;
                        css.Map[kv.Key] = kv.Value;
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

                double posVal;
                if (TryPx(DictGet(css.Map, "left"), out posVal)) css.Left = posVal;
                if (TryPx(DictGet(css.Map, "top"), out posVal)) css.Top = posVal;
                if (TryPx(DictGet(css.Map, "right"), out posVal)) css.Right = posVal;
                if (TryPx(DictGet(css.Map, "bottom"), out posVal)) css.Bottom = posVal;

                // dimensions
                double sizeVal;
                if (TryPx(DictGet(css.Map, "width"), out sizeVal)) css.Width = sizeVal;
                else if (TryPercent(DictGet(css.Map, "width"), out sizeVal)) css.WidthPercent = sizeVal;

                if (TryPx(DictGet(css.Map, "height"), out sizeVal)) css.Height = sizeVal;
                else if (TryPercent(DictGet(css.Map, "height"), out sizeVal)) css.HeightPercent = sizeVal;
                if (TryPx(DictGet(css.Map, "min-width"), out sizeVal)) css.MinWidth = sizeVal;
                if (TryPx(DictGet(css.Map, "min-height"), out sizeVal)) css.MinHeight = sizeVal;
                if (TryPx(DictGet(css.Map, "max-width"), out sizeVal)) css.MaxWidth = sizeVal;
                if (TryPx(DictGet(css.Map, "max-height"), out sizeVal)) css.MaxHeight = sizeVal;

                // aspect-ratio
                var aspectRatioRaw = Safe(DictGet(css.Map, "aspect-ratio"));
                if (!string.IsNullOrEmpty(aspectRatioRaw) && !aspectRatioRaw.Contains("auto"))
                {
                    // Parse "16/9" or "1.777"
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

                // gaps (gap shorthand + explicit row/column overrides)
                double gapRow, gapCol;
                if (TryGapShorthand(DictGet(css.Map, "gap"), out gapRow, out gapCol))
                {
                    css.Gap = gapRow;
                    css.RowGap = gapRow;
                    css.ColumnGap = gapCol;
                }
                double gapExplicit;
                if (TryPx(DictGet(css.Map, "row-gap"), out gapExplicit)) css.RowGap = gapExplicit;
                if (TryPx(DictGet(css.Map, "column-gap"), out gapExplicit)) css.ColumnGap = gapExplicit;
                if (!css.RowGap.HasValue && css.Gap.HasValue) css.RowGap = css.Gap;
                if (!css.ColumnGap.HasValue)
                {
                    if (css.Gap.HasValue) css.ColumnGap = css.Gap;
                    else if (css.RowGap.HasValue) css.ColumnGap = css.RowGap;
                }

                // color
                var fgColor = TryColor(DictGet(css.Map, "color"));
                if (fgColor.HasValue) css.ForegroundColor = fgColor;

                // background-color / background
                var bgColor = TryColor(ExtractBackgroundColor(css.Map));
                if (bgColor.HasValue) css.BackgroundColor = bgColor;

                // font shorthand
                var fontShorthand = Safe(DictGet(css.Map, "font"));
                if (!string.IsNullOrEmpty(fontShorthand))
                {
                    ParseFontShorthand(fontShorthand, css);
                }

                // font-family (first concrete family)
                try
                {
                    var ffRaw = DictGet(css.Map, "font-family");
                    var resolved = SelectFontFamily(ffRaw);
                    if (!string.IsNullOrEmpty(resolved))
                        css.FontFamilyName = resolved;
                }
                catch { }

                // font-size
                double px;
                if (TryPx(DictGet(css.Map, "font-size"), out px)) css.FontSize = px;

                // font-weight (keywords and numeric 100..900)
                var fwRaw = Safe(DictGet(css.Map, "font-weight"));
                if (!string.IsNullOrEmpty(fwRaw))
                {
                    var fw = fwRaw.Trim().ToLowerInvariant();
                    if (fw == "normal") css.FontWeight = MakeFontWeight(400);
                    else if (fw == "bold") css.FontWeight = MakeFontWeight(700);
                    else
                    {
                        int numeric;
                        if (int.TryParse(fw, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                        {
                            css.FontWeight = MakeFontWeight(numeric);
                        }
                    }
                }

                // font-style
                var fsRaw = Safe(DictGet(css.Map, "font-style"));
                if (!string.IsNullOrEmpty(fsRaw))
                {
                    if (string.Equals(fsRaw, "italic", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fsRaw, "oblique", StringComparison.OrdinalIgnoreCase))
                        css.FontStyle = FontStyle.Italic;
                    else if (string.Equals(fsRaw, "normal", StringComparison.OrdinalIgnoreCase))
                        css.FontStyle = FontStyle.Normal;
                }

                // text-align
                var ta = Safe(DictGet(css.Map, "text-align"));
                if (ta == "center") css.TextAlign = TextAlignment.Center;
                else if (ta == "right") css.TextAlign = TextAlignment.Right;
                else if (ta == "justify") css.TextAlign = TextAlignment.Justify;

                // text-decoration
                css.TextDecoration = Safe(DictGet(css.Map, "text-decoration"));

                // Visual effects - opacity, text-shadow, box-shadow
                double opacityVal;
                if (TryDouble(DictGet(css.Map, "opacity"), out opacityVal))
                    css.Opacity = Math.Max(0.0, Math.Min(1.0, opacityVal));

                css.TextShadow = Safe(DictGet(css.Map, "text-shadow"));
                css.BoxShadow = Safe(DictGet(css.Map, "box-shadow"));

                // margins/padding/border (kept in Map for RendererStyles, but also set typed if your CssComputed supports them)
                Thickness th;
                if (TryThickness(DictGet(css.Map, "margin"), out th)) css.Margin = th;

                // Override individual margins
                double mVal;
                var m = css.Margin;
                // --- Margin overrides ---
                double mLeft = m.Left, mTop = m.Top, mRight = m.Right, mBottom = m.Bottom;
                if (TryPx(DictGet(css.Map, "margin-left"), out mVal)) mLeft = mVal;
                if (TryPx(DictGet(css.Map, "margin-top"), out mVal)) mTop = mVal;
                if (TryPx(DictGet(css.Map, "margin-right"), out mVal)) mRight = mVal;
                if (TryPx(DictGet(css.Map, "margin-bottom"), out mVal)) mBottom = mVal;
                css.Margin = new Thickness(mLeft, mTop, mRight, mBottom);

                if (TryThickness(DictGet(css.Map, "padding"), out th)) css.Padding = th;

                // Override individual paddings
                var p = css.Padding;
                // --- Padding overrides ---
                double pLeft = p.Left, pTop = p.Top, pRight = p.Right, pBottom = p.Bottom;
                if (TryPx(DictGet(css.Map, "padding-left"), out mVal)) pLeft = mVal;
                if (TryPx(DictGet(css.Map, "padding-top"), out mVal)) pTop = mVal;
                if (TryPx(DictGet(css.Map, "padding-right"), out mVal)) pRight = mVal;
                if (TryPx(DictGet(css.Map, "padding-bottom"), out mVal)) pBottom = mVal;
                // Invalid code removed: css is CssComputed, not a Control
                // if (css is Decorator dec) dec.Padding = new Thickness(pLeft, pTop, pRight, pBottom); else if (css is Border bor) bor.Padding = new Thickness(pLeft, pTop, pRight, pBottom);

                // border shorthand
                var borderColor = TryColor(ExtractBorderColor(css.Map));
                if (borderColor.HasValue) css.BorderBrushColor = borderColor;
                if (TryThickness(ExtractBorderThickness(css.Map), out th)) css.BorderThickness = th;
                CornerRadius cr;
                if (TryCornerRadius(DictGet(css.Map, "border-radius"), out cr)) css.BorderRadius = cr;

                // Flexbox
                // Display and position
                css.Display = Safe(DictGet(css.Map, "display"));
                css.Position = Safe(DictGet(css.Map, "position"));
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

                // New properties
                // line-height
                var lhRaw = Safe(DictGet(css.Map, "line-height"));
                if (!string.IsNullOrEmpty(lhRaw))
                {
                    double lh;
                    if (TryPx(lhRaw, out lh)) css.LineHeight = lh; // px value
                    else if (double.TryParse(lhRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out lh)) css.LineHeight = lh; // multiplier
                }

                css.VerticalAlign = Safe(DictGet(css.Map, "vertical-align"));
                css.WhiteSpace = Safe(DictGet(css.Map, "white-space"));
                css.TextOverflow = Safe(DictGet(css.Map, "text-overflow"));
                css.BoxSizing = Safe(DictGet(css.Map, "box-sizing"));
                css.Cursor = Safe(DictGet(css.Map, "cursor"));

                result[n] = css;
            }

            return result;
        }

        private static Avalonia.Media.FontWeight MakeFontWeight(int openTypeWeight)
        {
            // Clamp to valid range
            if (openTypeWeight < 1) openTypeWeight = 1;
            if (openTypeWeight > 999) openTypeWeight = 999;

            // Map OpenType weight to FontWeight enum
            if (openTypeWeight <= 150) return Avalonia.Media.FontWeight.Thin;
            if (openTypeWeight <= 250) return Avalonia.Media.FontWeight.ExtraLight;
            if (openTypeWeight <= 350) return Avalonia.Media.FontWeight.Light;
            if (openTypeWeight <= 450) return Avalonia.Media.FontWeight.Normal;
            if (openTypeWeight <= 550) return Avalonia.Media.FontWeight.Medium;
            if (openTypeWeight <= 650) return Avalonia.Media.FontWeight.SemiBold;
            if (openTypeWeight <= 750) return Avalonia.Media.FontWeight.Bold;
            if (openTypeWeight <= 850) return Avalonia.Media.FontWeight.ExtraBold;
            if (openTypeWeight <= 950) return Avalonia.Media.FontWeight.Black;
            return Avalonia.Media.FontWeight.ExtraBlack;
        }

        // ===========================
        // Matching
        // ===========================

        private static bool Matches(LiteElement n, SelectorChain chain)
        {
            if (n == null || chain == null || chain.Segments.Count == 0) return false;

            // We match from the last segment back to the first, walking up the DOM for ancestor/parent checks.
            int segIndex = chain.Segments.Count - 1;
            LiteElement cur = n;

            // Match the right-most segment first
            if (!MatchesSingle(cur, chain.Segments[segIndex])) return false;

            // Walk up the chain
            while (segIndex > 0)
            {
                // The combinator connecting (segIndex-1) -> (segIndex) is stored on (segIndex-1)
                var prevSeg = chain.Segments[segIndex - 1];
                var comb = prevSeg.Next;

                segIndex--; // Move to the previous segment (the one we want to find now)

                if (cur == null) return false;

                if (comb == Combinator.Child)
                {
                    cur = cur.Parent;
                    if (!MatchesSingle(cur, prevSeg)) return false;
                }
                else // Descendant
                {
                    // Find an ancestor that matches prevSeg
                    cur = FindAncestorMatching(cur.Parent, prevSeg);
                    if (cur == null) return false;
                }
            }

            return true;
        }

        private static LiteElement FindAncestorMatching(LiteElement start, SelectorSegment seg)
        {
            var cur = start;
            while (cur != null)
            {
                if (MatchesSingle(cur, seg)) return cur;
                cur = cur.Parent;
            }
            return null;
        }

        /// <summary>
        /// Parse an+b notation for :nth-child and :nth-of-type.
        /// Supports: "odd", "even", "3", "2n", "2n+1", "2n-1", "-n+3", etc.
        /// </summary>
        private static bool ParseNthExpression(string expr, out int a, out int b)
        {
            a = 0; b = 0;
            if (string.IsNullOrWhiteSpace(expr)) return false;

            var s = expr.Replace(" ", "").ToLowerInvariant();

            // Handle keywords
            if (s == "odd") { a = 2; b = 1; return true; }
            if (s == "even") { a = 2; b = 0; return true; }

            // Find 'n'
            int posN = s.IndexOf('n');
            if (posN < 0)
            {
                // Just a number (e.g., "3")
                int num;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
                {
                    a = 0;
                    b = num;
                    return true;
                }
                return false;
            }

            // Parse 'an' part
            var aPart = s.Substring(0, posN);
            if (aPart == "" || aPart == "+") a = 1;
            else if (aPart == "-") a = -1;
            else if (!int.TryParse(aPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out a))
                return false;

            // Parse '+b' or '-b' part
            var bPart = s.Substring(posN + 1);
            if (string.IsNullOrEmpty(bPart))
            {
                b = 0;
                return true;
            }

            if (!int.TryParse(bPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                return false;

            return true;
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

        private static bool MatchesSingle(LiteElement n, SelectorSegment seg)
        {
            if (n == null || seg == null) return false;
            if (n.IsText) return false;

            if (!string.IsNullOrEmpty(seg.Tag))
            {
                if (!string.Equals(n.Tag, seg.Tag, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrEmpty(seg.Id))
            {
                string id;
                if (n.Attr == null || !n.Attr.TryGetValue("id", out id) || !string.Equals(id ?? "", seg.Id, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (seg.Classes != null && seg.Classes.Count > 0)
            {
                string cls;
                if (n.Attr == null || !n.Attr.TryGetValue("class", out cls) || string.IsNullOrWhiteSpace(cls))
                    return false;

                var have = SplitTokens(cls);
                foreach (var c in seg.Classes)
                    if (!have.Contains(c, StringComparer.OrdinalIgnoreCase)) return false;
            }

            if (seg.Attributes != null)
            {
                foreach (var attr in seg.Attributes)
                {
                    string val;
                    if (!n.Attr.TryGetValue(attr.Item1, out val)) return false;

                    if (attr.Item2 == "=")
                    {
                        if (!string.Equals(val ?? "", attr.Item3, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    // Add other operators (~=, |=, ^=, $=, *=) if needed
                }
            }

            if (seg.PseudoClasses != null)
            {
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
                    else if (ps.StartsWith("nth-of-type(", StringComparison.OrdinalIgnoreCase))
                    {
                        var arg = ExtractPseudoArg(ps);
                        int a, b;
                        if (ParseNthExpression(arg, out a, out b))
                        {
                            int index = GetTypeIndex(n);
                            if (index == 0 || !MatchesNth(index, a, b)) return false;
                        }
                        else
                        {
                            return false; // Invalid nth-expression
                        }
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
                    css.FontStyle = FontStyle.Italic;
                }
                else if (p == "normal")
                {
                    css.FontStyle = FontStyle.Normal;
                    css.FontWeight = Avalonia.Media.FontWeight.Normal;
                }
                // Check weight
                else if (p == "bold")
                {
                    css.FontWeight = Avalonia.Media.FontWeight.Bold;
                }
                else if (p == "bolder" || p == "lighter")
                {
                    // simplified
                    css.FontWeight = p == "bolder" ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Light;
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
            if (n.IsText) return n.Text ?? "";
            var sb = new StringBuilder();
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

        private static bool TryDouble(string s, out double v)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private static bool TryPx(string s, out double px)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            var sl = s.ToLowerInvariant();

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

            // em (approximate 16px without parent context)
            if (sl.EndsWith("em"))
            {
                var num = s.Substring(0, s.Length - 2).Trim();
                double v;
                if (TryDouble(num, out v)) { px = v * 16.0; return true; }
                return false;
            }

            // raw number -> px
            {
                double v;
                if (TryDouble(sl, out v)) { px = v; return true; }
            }
            return false;
        }


        private static bool TryThickness(string s, out Thickness th)
        {
            th = new Thickness(0);
            if (string.IsNullOrWhiteSpace(s)) return false;

            var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double a, b, c, d;

            if (parts.Length == 1) { if (!TryPx(parts[0], out a)) a = 0; th = new Thickness(a); return true; }
            if (parts.Length == 2)
            {
                if (!TryPx(parts[0], out a)) a = 0;
                if (!TryPx(parts[1], out b)) b = 0;
                th = new Thickness(b, a, b, a); return true;
            }
            if (parts.Length == 3)
            {
                if (!TryPx(parts[0], out a)) a = 0;
                if (!TryPx(parts[1], out b)) b = 0;
                if (!TryPx(parts[2], out c)) c = 0;
                th = new Thickness(b, a, b, c); return true;
            }
            // 4+
            if (!TryPx(parts[0], out a)) a = 0;
            if (!TryPx(parts[1], out b)) b = 0;
            if (!TryPx(parts[2], out c)) c = 0;
            if (!TryPx(parts[3], out d)) d = 0;
            th = new Thickness(d, a, b, c); return true;
        }

        private static Avalonia.Media.Color FromHex(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 3) // #abc -> #aabbcc
            {
                var r = Convert.ToByte(new string(hex[0], 2), 16);
                var g = Convert.ToByte(new string(hex[1], 2), 16);
                var b = Convert.ToByte(new string(hex[2], 2), 16);
                return Avalonia.Media.Color.FromArgb(255, r, g, b);
            }
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Avalonia.Media.Color.FromArgb(255, r, g, b);
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Avalonia.Media.Color.FromArgb(a, r, g, b);
            }
            return Avalonia.Media.Colors.Black;
        }

        private static Avalonia.Media.Color? TryColor(string css)
        {
            try
            {
                var color = CssParser.ParseColor(css);
                if (color.HasValue)
                {
                    // Map Avalonia.Media.Color to Avalonia.Media.Color
                    var c = color.Value;
                    return Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
                }
                return null;
            }
            catch { return null; }
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
                        val.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return val;
                }
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
                // naive: pick first length
                var m = Regex.Match(v, @"([0-9]+)px");
                if (m.Success) return m.Groups[0].Value;
            }
            return null;
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
                        var newVal = Regex.Replace(val, @"var\((--[^)]+)\)", m =>
                        {
                            var name = m.Groups[1].Value.Trim();
                            string sub;
                            if (vars.TryGetValue(name, out sub)) return sub;
                            return m.Value;
                        });

                        if (newVal != val)
                        {
                            kvp.Value.Value = newVal;
                        }
                    }
                }
            }
        }
    }
}
