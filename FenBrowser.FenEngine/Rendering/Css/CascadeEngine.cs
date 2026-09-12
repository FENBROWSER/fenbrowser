// SpecRef: CSS Cascading and Inheritance Level 4, Cascade order
// CapabilityId: CSS-CASCADE-ORDER-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;

namespace FenBrowser.FenEngine.Rendering
{
public class CascadeEngine
{
private readonly StyleSet _styleSet;
private readonly bool _logCascade;

// PERF: Multi-level indexing for fast rule lookup
private Dictionary<string, List<CssStyleRule>> _idIndex; // #id rules
private Dictionary<string, List<CssStyleRule>> _classIndex; // .class rules
private Dictionary<string, List<CssStyleRule>> _attributeIndex; // [attribute] rules
private Dictionary<string, List<CssStyleRule>> _tagIndex; // tag rules
private List<CssStyleRule> _universalRules; // * and attribute-only rules
// Rules whose key segment names a pseudo-element, bucketed by that pseudo-element.
// A ::before pass can only ever match rules in the "before" bucket, so this keeps a
// pseudo sweep proportional to the rules that name it rather than to the whole sheet.
private Dictionary<string, List<CssStyleRule>> _pseudoIndex;
private bool _indexed = false;
[ThreadStatic] private static HashSet<CssStyleRule> _processedRules; // Track duplicates
internal static long TCollect, TSort, TApply, NMatches, NElems, TCacheHit, NCacheHit, NMain, NPseudo, TPseudoCollect;

// PERF: Style cache to avoid recomputing styles for unchanged elements
// ParallelCascadeScheduler fans body's subtrees across threads and shares one
// CascadeEngine between them, so this is written from several at once. A plain
// Dictionary is not safe for that: concurrent writes drop entries, and an
// element whose computed style went missing renders with its defaults - a row
// that lost `display:flex` laid out as a block, differing run to run. The
// inline-style cache below already took a lock for the same reason; this one
// was left unguarded.
private readonly ConcurrentDictionary<Node, CssComputed> _styleCache = new ConcurrentDictionary<Node, CssComputed>();

// Inline style text is commonly repeated by generated markup. Parsing it once per
// element dominated sampled cascade time, so keep a small engine-owned FIFO cache.
// Exact ordinal text is the key; values are read-only-by-contract arrays owned by this engine.
private const int InlineStyleCacheCapacity = 256;
private readonly object _inlineStyleCacheSync = new object();
private readonly Dictionary<string, CssDeclaration[]> _inlineStyleCache = new Dictionary<string, CssDeclaration[]>(StringComparer.Ordinal);
private readonly Queue<string> _inlineStyleCacheOrder = new Queue<string>(InlineStyleCacheCapacity);
private int _inlineStyleCacheHits;
private int _inlineStyleCacheMisses;
private int _inlineStyleCacheEvictions;


        // PERF: Track which pseudo-elements have any rules to skip cascade for unused ones
        private HashSet<string> _pseudoElementsWithRules;
        // Compatibility list for legacy single-colon pseudo-elements that may be
        // parsed as pseudo-classes by some selector paths.
        private static readonly HashSet<string> LegacyPseudoElementNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "before",
            "after",
            "first-line",
            "first-letter",
            "placeholder",
            "selection"
        };
        private static readonly HashSet<string> SupportedDisplayKeywords = new(StringComparer.Ordinal)
        {
            "none",
            "contents",
            "block",
            "inline",
            "inline-block",
            "flow-root",
            "list-item",
            "flex",
            "inline-flex",
            "grid",
            "inline-grid",
            "table",
            "inline-table",
            "table-row-group",
            "table-header-group",
            "table-footer-group",
            "table-row",
            "table-cell",
            "table-caption"
        };

        public CascadeEngine(StyleSet styleSet)
        {
            _styleSet = styleSet ?? new StyleSet();
            _logCascade = FenBrowser.Core.Logging.DebugConfig.LogCssCascade;
            if (_logCascade)
            {
                FenBrowser.Core.EngineLogCompat.Info($"[DEBUG-CASCADE] Created Engine with {_styleSet.Count} sheets", FenBrowser.Core.Logging.LogCategory.CSS);
            }
        }
        
        /// <summary>
        /// Returns true if any CSS rule targets the specified pseudo-element.
        /// Use this to skip ComputeCascadedValues calls for pseudo-elements with no rules.
        /// </summary>
        public bool HasPseudoRules(string pseudoElement)
        {
            EnsureIndex();
            return _pseudoElementsWithRules?.Contains(pseudoElement.ToLowerInvariant()) ?? false;
        }

        private void EnsureIndex()
        {
            if (_indexed) return;
            _indexed = true;
            _idIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _classIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _attributeIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _tagIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            _universalRules = new List<CssStyleRule>();
            _pseudoIndex = new Dictionary<string, List<CssStyleRule>>(StringComparer.OrdinalIgnoreCase);
            
            _pseudoElementsWithRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            for (int i = 0; i < _styleSet.Count; i++)
            {
                var sheet = _styleSet.Sheets[i];
                var origin = _styleSet.Origins[i];
                var sourceOrder = _styleSet.SourceOrders[i];
                IndexRules(sheet.Rules, origin, sourceOrder);
            }
        }
        
        private void IndexRules(IEnumerable<CssRule> rules, CssOrigin defaultOrigin, int sourceOrder)
        {
            foreach (var rule in rules)
            {
                rule.StylesheetSourceOrder = sourceOrder;
                if (rule.Origin == CssOrigin.UserAgent && defaultOrigin != CssOrigin.UserAgent)
                {
                    rule.Origin = defaultOrigin;
                }

                if (rule is CssStyleRule styleRule)
                {
                    IndexStyleRule(styleRule);
                }
                else if (rule is CssMediaRule mediaRule)
                {
                    IndexRules(mediaRule.Rules, defaultOrigin, sourceOrder);
                }
            }
        }
        
        private void IndexStyleRule(CssStyleRule styleRule)
        {
            if (styleRule.Selector?.Chains?.Count > 0)
            {
                // PERF: Track pseudo-elements used by this rule
                foreach (var chain in styleRule.Selector.Chains)
                {
                    if (chain.Segments == null) continue;
                    foreach (var seg in chain.Segments)
                    {
                        AddIndexedPseudoElementNames(seg);
                    }

                    // Index based on this chain's key segment (Rightmost)
                    if (chain.Segments.Count > 0)
                    {
                        var keySeg = chain.Segments[chain.Segments.Count - 1];
                        IndexKeySegment(keySeg, styleRule);
                        IndexKeySegmentPseudoElements(keySeg, styleRule);
                    }
                    else
                    {
                        _universalRules.Add(styleRule);
                    }
                }
            }
            else
            {
                _universalRules.Add(styleRule);
            }
        }

        private void AddIndexedPseudoElementNames(SelectorSegment seg)
        {
            if (seg == null)
            {
                return;
            }

            if (seg.PseudoElements != null)
            {
                foreach (var pe in seg.PseudoElements)
                {
                    if (TryNormalizePseudoElementName(pe?.Name, out var normalized))
                    {
                        _pseudoElementsWithRules.Add(normalized);
                    }
                }
            }

            // Some selector parsing paths still emit legacy pseudo-elements (:before/:after/etc.)
            // under PseudoClasses. Track those names too so pseudo cascade remains reachable.
            if (seg.PseudoClasses != null)
            {
                foreach (var pc in seg.PseudoClasses)
                {
                    if (TryNormalizeLegacyPseudoElementName(pc?.Name, out var normalized))
                    {
                        _pseudoElementsWithRules.Add(normalized);
                    }
                }
            }
        }

        private static bool TryNormalizePseudoElementName(string rawName, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return false;
            }

            normalized = rawName.Trim().TrimStart(':').ToLowerInvariant();
            return normalized.Length > 0;
        }

        private static bool TryNormalizeLegacyPseudoElementName(string rawName, out string normalized)
        {
            normalized = null;
            if (!TryNormalizePseudoElementName(rawName, out var candidate))
            {
                return false;
            }

            if (!LegacyPseudoElementNames.Contains(candidate))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }

        // Fast pseudo-classification: returns true if the rule's last segment actually
        // names a pseudo-element (either via PseudoElements or via a legacy single-colon
        // pseudo-class). Allocation-free; bails out at the first match.
        private static bool RuleHasAnyPseudoElement(SelectorSegment lastSeg, bool hasPe, bool hasPc)
        {
            if (hasPe)
            {
                foreach (var pe in lastSeg.PseudoElements)
                {
                    if (TryNormalizePseudoElementName(pe?.Name, out _)) return true;
                }
            }
            if (hasPc)
            {
                foreach (var pc in lastSeg.PseudoClasses)
                {
                    if (TryNormalizeLegacyPseudoElementName(pc?.Name, out _)) return true;
                }
            }
            return false;
        }

        private static bool RuleMatchesPseudo(SelectorSegment lastSeg, bool hasPe, bool hasPc, string requested)
        {
            if (hasPe)
            {
                foreach (var pe in lastSeg.PseudoElements)
                {
                    if (TryNormalizePseudoElementName(pe?.Name, out var n) && n == requested) return true;
                }
            }
            if (hasPc)
            {
                foreach (var pc in lastSeg.PseudoClasses)
                {
                    if (TryNormalizeLegacyPseudoElementName(pc?.Name, out var n) && n == requested) return true;
                }
            }
            return false;
        }

        // Mirrors RuleMatchesPseudo: only the key segment's pseudo-elements (including
        // the legacy single-colon spellings) can satisfy a pseudo-element request.
        private void IndexKeySegmentPseudoElements(SelectorSegment keySeg, CssStyleRule styleRule)
        {
            if (keySeg == null)
            {
                return;
            }

            if (keySeg.PseudoElements != null)
            {
                foreach (var pe in keySeg.PseudoElements)
                {
                    if (TryNormalizePseudoElementName(pe?.Name, out var normalized))
                    {
                        AddToPseudoIndex(normalized, styleRule);
                    }
                }
            }

            if (keySeg.PseudoClasses != null)
            {
                foreach (var pc in keySeg.PseudoClasses)
                {
                    if (TryNormalizeLegacyPseudoElementName(pc?.Name, out var normalized))
                    {
                        AddToPseudoIndex(normalized, styleRule);
                    }
                }
            }
        }

        private void AddToPseudoIndex(string pseudoName, CssStyleRule styleRule)
        {
            ref List<CssStyleRule> list = ref CollectionsMarshal.GetValueRefOrAddDefault(_pseudoIndex, pseudoName, out bool exists);
            if (!exists)
            {
                list = new List<CssStyleRule>();
            }

            // A rule reaches this bucket once per chain naming the pseudo-element; the
            // last-entry check keeps the common repeat out without a set per bucket.
            if (list.Count == 0 || !ReferenceEquals(list[list.Count - 1], styleRule))
            {
                list.Add(styleRule);
            }
        }

        private void IndexKeySegment(SelectorSegment keySeg, CssStyleRule styleRule)
        {
            if (keySeg == null)
            {
                _universalRules.Add(styleRule);
                return;
            }

            // Priority: ID > Class > Attribute > Tag > Universal
            // Index by the most specific key available
            if (!string.IsNullOrEmpty(keySeg.Id))
            {
                AddToIndex(_idIndex, keySeg.Id, styleRule);
            }
            else if (keySeg.Classes != null && keySeg.Classes.Count > 0)
            {
                // Index by first class (most rules have 1-2 classes)
                AddToIndex(_classIndex, keySeg.Classes[0], styleRule);
            }
            else if (keySeg.Attributes != null &&
                     keySeg.Attributes.Count > 0 &&
                     !string.IsNullOrEmpty(keySeg.Attributes[0]?.Name))
            {
                AddToIndex(_attributeIndex, keySeg.Attributes[0].Name, styleRule);
            }
            else if (!string.IsNullOrEmpty(keySeg.TagName) && keySeg.TagName != "*")
            {
                // _tagIndex owns case-insensitive matching; preserve the parsed key so
                // index construction does not allocate one normalized string per rule.
                AddToIndex(_tagIndex, keySeg.TagName, styleRule);
                // DEBUG: Log div rules
                if (FenBrowser.Core.Logging.DebugConfig.LogCssCascade &&
                    string.Equals(keySeg.TagName, "DIV", StringComparison.OrdinalIgnoreCase))
                {
                    var props = string.Join(", ", styleRule.Declarations.Select(d => d.Property));
                    FenBrowser.Core.EngineLogCompat.Info($"[CASCADE-INDEX] Indexed DIV rule: {styleRule.Selector?.Raw} -> [{props}]", LogCategory.CSS);
                }
            }
            else
            {
                _universalRules.Add(styleRule);
            }
        }
        
        internal static void AddToIndex(Dictionary<string, List<CssStyleRule>> index, string key, CssStyleRule rule)
        {
            // Index construction is engine-owned and synchronous. Keep the entry ref
            // only for this insertion so a new key needs one hash/probe, not two.
            ref List<CssStyleRule> list = ref CollectionsMarshal.GetValueRefOrAddDefault(index, key, out bool exists);
            if (!exists)
            {
                list = new List<CssStyleRule>();
            }
            list.Add(rule);
        }

        internal int GetAttributeCandidateCount(string attributeName)
        {
            EnsureIndex();
            return !string.IsNullOrEmpty(attributeName) &&
                   _attributeIndex.TryGetValue(attributeName, out var rules)
                ? rules.Count
                : 0;
        }

        internal int UniversalCandidateCount
        {
            get
            {
                EnsureIndex();
                return _universalRules.Count;
            }
        }

public Dictionary<string, CssDeclaration> ComputeCascadedValues(Element element, string pseudoElement = null, FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
{
	// PERF: Check style cache first to avoid recomputation.
	// Skip cache if the element has a pending Style dirty-flag (e.g. :hover state
	// changed via ElementStateManager). Without this, :hover/:focus/:active rules
	// never contribute because the stale non-hover result keeps being returned.
	if (element != null && !element.StyleDirty && _styleCache.TryGetValue(element, out var cachedStyle))
	{
	if (pseudoElement == null)
	{
	long __tc = System.Diagnostics.Stopwatch.GetTimestamp();
	var __r = ConvertComputedMapToDeclarations(cachedStyle.Map);
	System.Threading.Interlocked.Add(ref TCacheHit, System.Diagnostics.Stopwatch.GetTimestamp() - __tc);
	System.Threading.Interlocked.Increment(ref NCacheHit);
	return __r;
	}
	// For pseudo-elements, still need to recompute
	}
	// If the element is StyleDirty, discard any stale cache entry so the
	// recomputed result replaces it below.
	if (element != null && element.StyleDirty)
	{
	_styleCache.TryRemove(element, out _);
	}

EnsureIndex();
var results = new List<MatchedDeclaration>();
if (_processedRules == null) _processedRules = new HashSet<CssStyleRule>();
_processedRules.Clear();

// 1. Gather all declarations from matching rules (priority order: ID > Classes > Tag > Universal)
long __t0 = System.Diagnostics.Stopwatch.GetTimestamp();
CollectMatches(element, results, pseudoElement);
CollectPresentationalHintMatches(element, results, pseudoElement);
CollectInlineStyleMatches(element, results, pseudoElement);
long __dt = System.Diagnostics.Stopwatch.GetTimestamp() - __t0;
System.Threading.Interlocked.Add(ref TCollect, __dt);
if (pseudoElement == null) System.Threading.Interlocked.Increment(ref NMain);
else { System.Threading.Interlocked.Increment(ref NPseudo); System.Threading.Interlocked.Add(ref TPseudoCollect, __dt); }
System.Threading.Interlocked.Increment(ref NElems);
System.Threading.Interlocked.Add(ref NMatches, results.Count);

            /*
            if (_logCascade && results.Count > 0)
            {
                var uniqueSources = results.Select(r => new { r.SelectorText, Spec = r.Specificity, r.Origin }).Distinct().OrderBy(x => x.Spec).ToList();
                var msg = new System.Text.StringBuilder();
                msg.AppendLine($"[CASCADE] Element <{element.TagName}> matched {uniqueSources.Count} rules:");
                foreach(var src in uniqueSources)
                {
                    msg.AppendLine($"   - [{src.Origin}] {src.SelectorText} :: {src.Spec}");
                }
                global::FenBrowser.Core.EngineLogCompat.Log(msg.ToString().TrimEnd(), global::FenBrowser.Core.Logging.LogCategory.Cascade);
            }
            */

            // 2. Sort declarations
            deadline?.Check();
            long __t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            results.Sort();
            System.Threading.Interlocked.Add(ref TSort, System.Diagnostics.Stopwatch.GetTimestamp() - __t1);
            deadline?.Check();

            if (_logCascade && results.Count > 0)
            {
                LogCascadeWinners(element, pseudoElement, results);
            }

// 3. Apply the cascade declaration-by-declaration so shorthand expansion
// participates in origin/specificity/order resolution. Expanding after
// selecting winners per declared property is incorrect because a higher-
// priority shorthand (for example author `background`) must override a
// lower-priority longhand (for example UA `background-color`).
// CSS custom properties are case-sensitive and must not be merged by
// case-insensitive dictionary keys.
var computed = new Dictionary<string, CssDeclaration>(StringComparer.Ordinal);
long __t2 = System.Diagnostics.Stopwatch.GetTimestamp();
foreach (var match in results)
{
deadline?.Check();
ApplyDeclaration(computed, match.Declaration);
}
System.Threading.Interlocked.Add(ref TApply, System.Diagnostics.Stopwatch.GetTimestamp() - __t2);

// PERF: Store result in cache for future reuse
if (element != null && string.IsNullOrEmpty(pseudoElement))
{
var cssComputed = new CssComputed();
foreach (var kvp in computed)
{
    cssComputed.Map[kvp.Key] = kvp.Value?.Value ?? string.Empty;
}
_styleCache[element] = cssComputed;
}

return computed;
        }

        private static Dictionary<string, CssDeclaration> ConvertComputedMapToDeclarations(Dictionary<string, string> map)
        {
            var declarations = new Dictionary<string, CssDeclaration>(StringComparer.Ordinal);
            if (map == null)
            {
                return declarations;
            }

            foreach (var kvp in map)
            {
                declarations[kvp.Key] = new CssDeclaration
                {
                    Property = kvp.Key,
                    Value = kvp.Value ?? string.Empty,
                    IsImportant = false
                };
            }

            return declarations;
        }

        public InlineStyleCacheStatistics GetInlineStyleCacheStatistics()
        {
            lock (_inlineStyleCacheSync)
            {
                return new InlineStyleCacheStatistics(
                    _inlineStyleCacheHits,
                    _inlineStyleCacheMisses,
                    _inlineStyleCacheEvictions,
                    _inlineStyleCache.Count,
                    InlineStyleCacheCapacity);
            }
        }

        // HTML 15.3 presentational hints: legacy attributes that map into the
        // cascade as style. They are emitted at User origin, which is exactly the
        // precedence the cascade gives them - above the UA sheet, below any
        // author rule, so a stylesheet always wins.
        //
        // Hacker News is built out of these: <table width="85%" bgcolor="#f6f6ef">
        // for the page body and <td bgcolor="#ff6600"> for the orange bar. With
        // none of them mapped the page rendered unstyled and left-aligned.
        private void CollectPresentationalHintMatches(
            Element element,
            List<MatchedDeclaration> results,
            string pseudoElement)
        {
            if (element == null || results == null || !string.IsNullOrWhiteSpace(pseudoElement))
            {
                return;
            }

            var tag = element.TagName?.ToUpperInvariant();
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            var hintIndex = 0;

            void Emit(string property, string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                results.Add(new MatchedDeclaration
                {
                    Declaration = new CssDeclaration
                    {
                        Property = property,
                        Value = value,
                        IsImportant = false,
                    },
                    Key = new CascadeKey(
                        CssOrigin.User,
                        false,
                        0,
                        0,
                        0,
                        0,
                        0,
                        int.MaxValue,
                        int.MaxValue - 2,
                        hintIndex++),
                    SelectorText = "[presentational-hint]",
                });
            }

            // bgcolor -> background-color, on the elements HTML allows it on.
            if (tag is "BODY" or "TABLE" or "THEAD" or "TBODY" or "TFOOT" or "TR" or "TD" or "TH")
            {
                Emit("background-color", ParseLegacyColor(element.GetAttribute("bgcolor")));
            }

            if (tag == "BODY")
            {
                Emit("color", ParseLegacyColor(element.GetAttribute("text")));
            }

            // width/height -> the corresponding property. A bare number is
            // pixels; a trailing % stays a percentage.
            if (tag is "TABLE" or "TD" or "TH" or "COL" or "COLGROUP" or "IMG" or
                "HR" or "IFRAME" or "OBJECT" or "VIDEO" or "CANVAS" or "EMBED")
            {
                Emit("width", ParseLegacyLength(element.GetAttribute("width")));
                Emit("height", ParseLegacyLength(element.GetAttribute("height")));
            }

            // <table align=center|left|right> is a float/margin hint, not
            // text-align: center gives the table auto inline margins.
            if (tag == "TABLE")
            {
                var align = element.GetAttribute("align")?.Trim().ToLowerInvariant();
                if (align == "center")
                {
                    Emit("margin-left", "auto");
                    Emit("margin-right", "auto");
                }
                else if (align is "left" or "right")
                {
                    Emit("float", align);
                }
            }
        }

        // HTML "rules for parsing a legacy colour value", reduced to the forms
        // that actually appear: a named colour, #rgb, or #rrggbb with or without
        // the hash. Anything else is dropped rather than guessed at.
        private static string ParseLegacyColor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var value = raw.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                var digits = value.Substring(1);
                return (digits.Length == 3 || digits.Length == 6) && IsHex(digits) ? value : null;
            }

            if ((value.Length == 3 || value.Length == 6) && IsHex(value))
            {
                return "#" + value;
            }

            // A colour keyword: let the normal colour parser judge it.
            return System.Linq.Enumerable.All(value, c => char.IsLetter(c)) ? value : null;

            static bool IsHex(string s) =>
                System.Linq.Enumerable.All(s, c => System.Uri.IsHexDigit(c));
        }

        private static string ParseLegacyLength(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var value = raw.Trim();
            if (value.EndsWith("%", StringComparison.Ordinal))
            {
                var number = value.Substring(0, value.Length - 1).Trim();
                return double.TryParse(number, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct)
                    ? pct.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"
                    : null;
            }

            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var px)
                ? px.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px"
                : null;
        }

        private void CollectInlineStyleMatches(Element element, List<MatchedDeclaration> results, string pseudoElement)
        {
            if (element == null || results == null || !string.IsNullOrWhiteSpace(pseudoElement))
            {
                return;
            }

            var inlineStyleText = element.GetAttribute("style");
            if (string.IsNullOrWhiteSpace(inlineStyleText))
            {
                return;
            }

            var inlineDeclarations = GetInlineStyleDeclarations(inlineStyleText);
            if (inlineDeclarations.Count == 0)
            {
                return;
            }

            for (var declarationIndex = 0; declarationIndex < inlineDeclarations.Count; declarationIndex++)
            {
                var declaration = inlineDeclarations[declarationIndex];
                if (declaration == null || string.IsNullOrWhiteSpace(declaration.Property))
                {
                    continue;
                }

                var key = new CascadeKey(
                    CssOrigin.Author,
                    declaration.IsImportant,
                    ushort.MaxValue,
                    0,
                    0,
                    0,
                    0,
                    int.MaxValue,
                    int.MaxValue - 1,
                    declarationIndex);

                results.Add(new MatchedDeclaration
                {
                    Declaration = declaration,
                    Key = key,
                    SelectorText = "[style]"
                });
            }
        }

        private IReadOnlyList<CssDeclaration> GetInlineStyleDeclarations(string styleAttribute)
        {
            lock (_inlineStyleCacheSync)
            {
                if (_inlineStyleCache.TryGetValue(styleAttribute, out var cached))
                {
                    _inlineStyleCacheHits++;
                    return cached;
                }

                _inlineStyleCacheMisses++;
                var parsed = ParseInlineStyleDeclarations(styleAttribute);
                if (_inlineStyleCache.Count >= InlineStyleCacheCapacity)
                {
                    string oldest = _inlineStyleCacheOrder.Dequeue();
                    _inlineStyleCache.Remove(oldest);
                    _inlineStyleCacheEvictions++;
                }

                _inlineStyleCache.Add(styleAttribute, parsed);
                _inlineStyleCacheOrder.Enqueue(styleAttribute);
                return parsed;
            }
        }

        private static CssDeclaration[] ParseInlineStyleDeclarations(string styleAttribute)
        {
            if (string.IsNullOrWhiteSpace(styleAttribute))
            {
                return Array.Empty<CssDeclaration>();
            }

            try
            {
                var tokenizer = new CssTokenizer("*{" + styleAttribute + "}");
                var parser = new CssSyntaxParser(tokenizer);
                var stylesheet = parser.ParseStylesheet();
                CssStyleRule firstStyleRule = null;
                if (stylesheet?.Rules != null)
                {
                    for (var ruleIndex = 0; ruleIndex < stylesheet.Rules.Count; ruleIndex++)
                    {
                        if (stylesheet.Rules[ruleIndex] is CssStyleRule styleRule)
                        {
                            firstStyleRule = styleRule;
                            break;
                        }
                    }
                }
                if (firstStyleRule == null || firstStyleRule.Declarations.Count == 0)
                {
                    return Array.Empty<CssDeclaration>();
                }

                return firstStyleRule.Declarations.ToArray();
            }
            catch
            {
                return Array.Empty<CssDeclaration>();
            }
        }

        private void LogCascadeWinners(Element element, string pseudoElement, List<MatchedDeclaration> results)
        {
            if (element == null || results == null || results.Count == 0)
            {
                return;
            }

            var winners = new Dictionary<string, MatchedDeclaration>(StringComparer.Ordinal);
            foreach (var match in results)
            {
                var property = match?.Declaration?.Property;
                if (string.IsNullOrWhiteSpace(property))
                {
                    continue;
                }

                winners[property] = match;
            }

            if (winners.Count == 0)
            {
                return;
            }

            string elementLabel = BuildElementLabel(element, pseudoElement);
            var msg = new System.Text.StringBuilder();
            msg.AppendLine($"[CASCADE-WINNERS] {elementLabel}");

            foreach (var entry in winners.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var winner = entry.Value;
                msg.Append("  ");
                msg.Append(entry.Key);
                msg.Append(" = ");
                msg.Append(winner.Declaration.Value);
                msg.Append(" | key=");
                msg.Append(winner.Key.ToString());
                msg.Append($",selector={winner.SelectorText}");
                msg.AppendLine();
            }

            FenBrowser.Core.EngineLogCompat.Info(msg.ToString().TrimEnd(), LogCategory.CSS);
        }

        private static string BuildElementLabel(Element element, string pseudoElement)
        {
            var parts = new List<string>();
            string tag = string.IsNullOrWhiteSpace(element.TagName) ? "unknown" : element.TagName.ToLowerInvariant();
            parts.Add(tag);

            if (!string.IsNullOrWhiteSpace(element.Id))
            {
                parts.Add("#" + element.Id);
            }

            foreach (var cls in element.ClassList)
            {
                if (!string.IsNullOrWhiteSpace(cls))
                {
                    parts.Add("." + cls);
                }
            }

            if (!string.IsNullOrWhiteSpace(pseudoElement))
            {
                parts.Add("::" + pseudoElement.TrimStart(':'));
            }

            return string.Join(string.Empty, parts);
        }

        private void CollectMatches(Element element, List<MatchedDeclaration> results, string pseudoElement)
        {
            // The element's root is fixed for the whole rule sweep, while GetRootNode walks
            // the full ancestor chain. Resolving it per candidate rule cost ~16 parent hops
            // per check (153M hops on github.com); resolve it once per element instead.
            var elementShadowRoot = element.GetRootNode() as ShadowRoot;

            // A pseudo-element pass can only match rules whose key segment names that
            // pseudo-element, so walk that bucket instead of every id/class/tag/universal
            // candidate. TryMatchRule still re-verifies, so this only removes rules that
            // could not have matched.
            if (!string.IsNullOrWhiteSpace(pseudoElement))
            {
                var requestedPseudo = pseudoElement.Trim().TrimStart(':').ToLowerInvariant();
                if (_pseudoIndex.TryGetValue(requestedPseudo, out var pseudoRules))
                {
                    foreach (var rule in pseudoRules)
                    {
                        if (_processedRules.Add(rule))
                            TryMatchRule(element, rule, results, pseudoElement, elementShadowRoot);
                    }
                }

                return;
            }

            // Get attributes via properties
            string elemId = element.Id;
            string elemClass = element.GetAttribute("class");
            
            // 1. Check ID-specific rules (highest priority index)
            if (!string.IsNullOrEmpty(elemId) && _idIndex.TryGetValue(elemId, out var idRules))
            {
                foreach (var rule in idRules)
                {
                    if (_processedRules.Add(rule))
                        TryMatchRule(element, rule, results, pseudoElement, elementShadowRoot);
                }
            }
            
            // 2. Check class-specific rules
            if (!string.IsNullOrEmpty(elemClass))
            {
                var classes = element.ClassList; // Use ClassList for splitting
                foreach (var cls in classes)
                {
                    if (_classIndex.TryGetValue(cls, out var classRules))
                    {
                        foreach (var rule in classRules)
                        {
                            if (_processedRules.Add(rule))
                                TryMatchRule(element, rule, results, pseudoElement, elementShadowRoot);
                        }
                    }
                }
            }

            // 3. Check attribute-specific rules. Attribute-only selectors used to
            // fall into the universal bucket and were evaluated for every element.
            if (element.HasAttributes())
            {
                var attributes = element.Attributes;
                for (var attributeIndex = 0; attributeIndex < attributes.Length; attributeIndex++)
                {
                    var attribute = attributes[attributeIndex];
                    string attributeName = attribute?.LocalName ?? attribute?.Name;
                    if (!string.IsNullOrEmpty(attributeName) &&
                        _attributeIndex.TryGetValue(attributeName, out var attributeRules))
                    {
                        foreach (var rule in attributeRules)
                        {
                            if (_processedRules.Add(rule))
                                TryMatchRule(element, rule, results, pseudoElement, elementShadowRoot);
                        }
                    }
                }
            }

            // 4. Check tag-specific rules (_tagIndex is OrdinalIgnoreCase)
            string tag = element.TagName;
            if (!string.IsNullOrEmpty(tag) && _tagIndex.TryGetValue(tag, out var tagRules))
            {
                foreach (var rule in tagRules)
                {
                    if (_processedRules.Add(rule))
                        TryMatchRule(element, rule, results, pseudoElement, elementShadowRoot);
                }
            }
            
            // 5. Always check universal rules
            foreach (var rule in _universalRules)
            {
                if (_processedRules.Add(rule))
                    TryMatchRule(element, rule, results, pseudoElement, elementShadowRoot);
            }
        }
        
        private void TryMatchRule(Element element, CssStyleRule styleRule, List<MatchedDeclaration> results, string pseudoElement, ShadowRoot elementShadowRoot)
        {
            if (styleRule.ShadowScopeRoot != null)
            {
                if (!ReferenceEquals(styleRule.ShadowScopeRoot, elementShadowRoot))
                {
                    return;
                }
            }
            else if (elementShadowRoot != null && styleRule.Origin != CssOrigin.UserAgent)
            {
                return;
            }

            // 1. Check Scope if applicable
            int scopeProximity = 0;
            if (!string.IsNullOrEmpty(styleRule.ScopeSelector))
            {
                // Find closest ancestor matching the scope selector
                var current = element.ParentElement;
                int dist = 1;
                bool scopeMatched = false;
                while (current != null)
                {
                    if (SelectorMatcher.Matches(current, styleRule.ScopeSelector))
                    {
                        scopeProximity = dist;
                        scopeMatched = true;
                        break;
                    }
                    current = current.ParentElement;
                    dist++;
                }
                
                if (!scopeMatched) return; // Not inside the required scope
            }

            // 2. Match the actual selector in the requested element/pseudo context.
            // A selector list can contain multiple branches that match the same
            // originating element but target different generated boxes. Selecting
            // by specificity before filtering that context can apply declarations
            // from the wrong branch.
            var matchedChain = GetMatchingChainForContext(element, styleRule.Selector, pseudoElement);
            if (matchedChain == null)
            {
                return;
            }

            for (int declarationIndex = 0; declarationIndex < styleRule.Declarations.Count; declarationIndex++)
            {
                var decl = styleRule.Declarations[declarationIndex];
                var key = new CascadeKey(
                    styleRule.Origin,
                    decl.IsImportant,
                    (ushort)matchedChain.Specificity.A,
                    (ushort)matchedChain.Specificity.B,
                    (ushort)matchedChain.Specificity.C,
                    styleRule.LayerOrder,
                    scopeProximity,
                    styleRule.StylesheetSourceOrder,
                    styleRule.Order,
                    declarationIndex
                );

                results.Add(new MatchedDeclaration
                {
                    Declaration = decl,
                    Key = key,
                    SelectorText = styleRule.Selector?.Raw ?? styleRule.Selector?.ToString() ?? string.Empty
                });
            }
        }

        private static SelectorChain GetMatchingChainForContext(
            Element element,
            CssSelector selector,
            string pseudoElement)
        {
            if (element == null || selector == null)
            {
                return null;
            }

            if (selector.Chains == null || selector.Chains.Count == 0)
            {
                selector.Chains = SelectorMatcher.ParseSelectorList(selector.Raw);
            }

            string requestedPseudo = string.IsNullOrWhiteSpace(pseudoElement)
                ? null
                : pseudoElement.Trim().TrimStart(':').ToLowerInvariant();
            SelectorChain best = null;

            foreach (var chain in selector.Chains)
            {
                if (!SelectorMatcher.MatchesChain(element, chain))
                {
                    continue;
                }

                var segments = chain.Segments;
                var lastSegment = segments.Count > 0 ? segments[segments.Count - 1] : null;
                bool hasPseudoElements = lastSegment?.PseudoElements?.Count > 0;
                bool hasPseudoClasses = lastSegment?.PseudoClasses?.Count > 0;
                bool contextMatches = requestedPseudo == null
                    ? !RuleHasAnyPseudoElement(lastSegment, hasPseudoElements, hasPseudoClasses)
                    : RuleMatchesPseudo(lastSegment, hasPseudoElements, hasPseudoClasses, requestedPseudo);

                if (contextMatches &&
                    (best == null || chain.Specificity.CompareTo(best.Specificity) > 0))
                {
                    best = chain;
                }
            }

            return best;
        }

        private static void ApplyDeclaration(Dictionary<string, CssDeclaration> computed, CssDeclaration declaration)
        {
            if (declaration == null || string.IsNullOrEmpty(declaration.Property))
            {
                return;
            }

            var property = NormalizePropertyKey(declaration.Property);
            var value = declaration.Value?.Trim() ?? string.Empty;
            if (string.Equals(property, "display", StringComparison.Ordinal))
            {
                // Defer display validation when value resolution is pending.
                // var() and CSS-wide keywords are resolved during computed-style
                // resolution in CssLoader.ResolveStyle.
                bool needsComputedValueResolution =
                    value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    value.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);

                if (!needsComputedValueResolution && !TryNormalizeDisplayValue(value, out value))
                {
                    // Invalid/unsupported display values must be ignored so they
                    // cannot override an earlier valid declaration in the cascade.
                    return;
                }
            }

            if (!IsValidDeclarationValue(property, value))
            {
                return;
            }

            SetComputedDeclaration(computed, property, value, declaration);

            switch (property)
            {
                case "margin":
                    ApplyBoxShorthand(computed, declaration, value, "margin-top", "margin-right", "margin-bottom", "margin-left");
                    break;
                case "padding":
                    ApplyBoxShorthand(computed, declaration, value, "padding-top", "padding-right", "padding-bottom", "padding-left");
                    break;
                case "border":
                    ApplyBorderShorthand(computed, declaration, value, null);
                    break;
                case "border-top":
                case "border-right":
                case "border-bottom":
                case "border-left":
                    ApplyBorderShorthand(computed, declaration, value, property);
                    break;
                case "border-style":
                    ApplyBorderSidesShorthand(computed, declaration, value, "style");
                    break;
                case "border-width":
                    ApplyBorderSidesShorthand(computed, declaration, value, "width");
                    break;
                case "border-color":
                    ApplyBorderSidesShorthand(computed, declaration, value, "color");
                    break;
                case "background":
                    ApplyBackgroundShorthand(computed, declaration, value);
                    break;
                case "flex-flow":
                    ApplyFlexFlowShorthand(computed, declaration, value);
                    break;
                case "overflow":
                    ApplyOverflowShorthand(computed, declaration, value);
                    break;
                case "outline":
                    ApplyOutlineShorthand(computed, declaration, value);
                    break;
                case "list-style":
                    ApplyListStyleShorthand(computed, declaration, value);
                    break;
                case "gap":
                    ApplyGapShorthand(computed, declaration, value);
                    break;
                case "border-radius":
                    ApplyBorderRadiusShorthand(computed, declaration, value);
                    break;
                case "inset":
                    ApplyInsetShorthand(computed, declaration, value);
                    break;
                case "transition":
                    ApplyTransitionShorthand(computed, declaration, value);
                    break;
                case "font":
                    ApplyFontShorthand(computed, declaration, value);
                    break;
            }
        }

        // CSS Display Level 3 two-keyword values → single-keyword equivalents.
        // Spec: https://drafts.csswg.org/css-display/#typedef-display-outside
        private static readonly Dictionary<string, string> TwoKeywordDisplayMap = new(StringComparer.Ordinal)
        {
            ["block flow"] = "block",
            ["block flow-root"] = "flow-root",
            ["inline flow"] = "inline",
            ["inline flow-root"] = "inline-block",
            ["block flex"] = "flex",
            ["inline flex"] = "inline-flex",
            ["block grid"] = "grid",
            ["inline grid"] = "inline-grid",
            ["block table"] = "table",
            ["inline table"] = "inline-table",
        };

        private static bool TryNormalizeDisplayValue(string rawValue, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            string value = rawValue.Trim().ToLowerInvariant();
            switch (value)
            {
                case "-webkit-box":
                case "-webkit-flex":
                case "-ms-flexbox":
                    normalized = "flex";
                    return true;
                case "-webkit-inline-box":
                case "-webkit-inline-flex":
                case "-ms-inline-flexbox":
                    normalized = "inline-flex";
                    return true;
            }

            if (SupportedDisplayKeywords.Contains(value))
            {
                normalized = value;
                return true;
            }

            // CSS Display Level 3: two-keyword syntax (e.g. "inline flex" → "inline-flex")
            if (TwoKeywordDisplayMap.TryGetValue(value, out var mapped))
            {
                normalized = mapped;
                return true;
            }

            return false;
        }

        private static string NormalizePropertyKey(string property)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                return string.Empty;
            }

            // Custom properties are case-sensitive by spec.
            if (property.StartsWith("--", StringComparison.Ordinal))
            {
                return property;
            }

            return property.ToLowerInvariant();
        }

        private static bool IsValidDeclarationValue(string property, string value)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            switch (property.ToLowerInvariant())
            {
                case "color":
                case "background-color":
                case "border-top-color":
                case "border-right-color":
                case "border-bottom-color":
                case "border-left-color":
                case "outline-color":
                case "column-rule-color":
                case "text-decoration-color":
                case "caret-color":
                case "accent-color":
                    return IsValidColorValue(value);
                case "width":
                case "height":
                case "min-width":
                case "min-height":
                case "max-width":
                case "max-height":
                case "inline-size":
                case "block-size":
                case "min-inline-size":
                case "min-block-size":
                case "max-inline-size":
                case "max-block-size":
                    return IsValidSizingValue(value);
                case "background":
                    return IsValidBackgroundShorthand(value);
                default:
                    return true;
            }
        }

        private static bool IsValidColorValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "inherit":
                case "initial":
                case "unset":
                case "revert":
                case "revert-layer":
                case "currentcolor":
                    return true;
            }

            // Color custom-property references are valid at parse/cascade time and
            // resolved during computed-style resolution.
            if (trimmed.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return CssParser.ParseColor(trimmed).HasValue;
        }

        private static bool IsValidSizingValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "auto":
                case "inherit":
                case "initial":
                case "unset":
                case "revert":
                case "revert-layer":
                case "min-content":
                case "max-content":
                case "fit-content":
                    return true;
            }

            if (IsCssLength(trimmed))
            {
                return true;
            }

            return trimmed.Contains("(", StringComparison.Ordinal);
        }

        private static bool IsValidBackgroundShorthand(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int colorCount = 0;
            foreach (var part in SplitCssValue(trimmed))
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                if (part.Equals("/", StringComparison.Ordinal) ||
                    IsBackgroundRepeat(part) ||
                    IsBackgroundPosition(part) ||
                    part.Equals("fixed", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("contain", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("gradient(", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CssParser.ParseColor(part).HasValue)
                {
                    colorCount++;
                    if (colorCount > 1)
                    {
                        return false;
                    }
                    continue;
                }
            }

            return true;
        }

        private static void SetComputedDeclaration(
            Dictionary<string, CssDeclaration> computed,
            string property,
            string value,
            CssDeclaration source)
        {
            // Keep the entry reference local to this update. This performs one
            // hash/probe for both new properties and later cascade overwrites.
            ref CssDeclaration existing = ref CollectionsMarshal.GetValueRefOrAddDefault(
                computed,
                property,
                out bool exists);
            if (exists)
            {
                existing.Value = value;
                existing.IsImportant = source?.IsImportant ?? false;
                return;
            }

            existing = new CssDeclaration
            {
                Property = property,
                Value = value,
                IsImportant = source?.IsImportant ?? false
            };
        }

        private static void SetExpanded(Dictionary<string, CssDeclaration> computed, string property, string value, CssDeclaration source)
        {
            SetComputedDeclaration(computed, property, value, source);
        }

        private static void ApplyBoxShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value,
            string top, string right, string bottom, string left)
        {
            if (string.IsNullOrEmpty(value)) return;

            var parts = SplitCssValue(value);
            if (parts.Length == 0) return;

            string vTop, vRight, vBottom, vLeft;
            switch (parts.Length)
            {
                case 1:
                    vTop = vRight = vBottom = vLeft = parts[0];
                    break;
                case 2:
                    vTop = vBottom = parts[0];
                    vRight = vLeft = parts[1];
                    break;
                case 3:
                    vTop = parts[0];
                    vRight = vLeft = parts[1];
                    vBottom = parts[2];
                    break;
                default:
                    vTop = parts[0];
                    vRight = parts[1];
                    vBottom = parts[2];
                    vLeft = parts[3];
                    break;
            }

            SetExpanded(computed, top, vTop, source);
            SetExpanded(computed, right, vRight, source);
            SetExpanded(computed, bottom, vBottom, source);
            SetExpanded(computed, left, vLeft, source);
        }

        private static void ApplyBorderShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value, string sideProperty)
        {
            if (string.IsNullOrEmpty(value)) return;

            if (value == "none" || value == "0")
            {
                if (string.IsNullOrEmpty(sideProperty))
                {
                    SetExpanded(computed, "border-top-width", "0", source);
                    SetExpanded(computed, "border-right-width", "0", source);
                    SetExpanded(computed, "border-bottom-width", "0", source);
                    SetExpanded(computed, "border-left-width", "0", source);
                    SetExpanded(computed, "border-top-style", "none", source);
                    SetExpanded(computed, "border-right-style", "none", source);
                    SetExpanded(computed, "border-bottom-style", "none", source);
                    SetExpanded(computed, "border-left-style", "none", source);
                }
                else
                {
                    SetExpanded(computed, sideProperty + "-width", "0", source);
                    SetExpanded(computed, sideProperty + "-style", "none", source);
                }
                return;
            }

            var parts = SplitCssValue(value);
            string width = null, style = null, color = null;
            foreach (var part in parts)
            {
                if (IsBorderStyle(part)) style = part;
                else if (IsCssLength(part)) width = part;
                else color = part;
            }

            // CSS Backgrounds 3 §3.4: a shorthand resets every longhand it covers, so
            // `border: solid` is a medium currentcolor border and `border-bottom: red
            // solid` is 3px — an omitted component takes its initial value, not an
            // earlier declaration's.
            width ??= "medium";
            style ??= "none";
            color ??= "currentcolor";

            if (string.IsNullOrEmpty(sideProperty))
            {
                SetExpanded(computed, "border-top-width", width, source);
                SetExpanded(computed, "border-right-width", width, source);
                SetExpanded(computed, "border-bottom-width", width, source);
                SetExpanded(computed, "border-left-width", width, source);
                SetExpanded(computed, "border-top-style", style, source);
                SetExpanded(computed, "border-right-style", style, source);
                SetExpanded(computed, "border-bottom-style", style, source);
                SetExpanded(computed, "border-left-style", style, source);
                SetExpanded(computed, "border-top-color", color, source);
                SetExpanded(computed, "border-right-color", color, source);
                SetExpanded(computed, "border-bottom-color", color, source);
                SetExpanded(computed, "border-left-color", color, source);
            }
            else
            {
                SetExpanded(computed, sideProperty + "-width", width, source);
                SetExpanded(computed, sideProperty + "-style", style, source);
                SetExpanded(computed, sideProperty + "-color", color, source);
            }
        }

        /// <summary>
        /// `border-style` / `border-width` / `border-color` are four-value box
        /// shorthands over the per-side longhands; expanding them in declaration
        /// order lets `border: solid 1em; border-style: none solid` zero the top and
        /// bottom sides (CSS 2.1 §8.5.3, Acid2's smile) instead of the earlier
        /// `border` expansion winning.
        /// </summary>
        private static void ApplyBorderSidesShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value, string suffix)
        {
            ApplyBoxShorthand(computed, source, value,
                "border-top-" + suffix, "border-right-" + suffix, "border-bottom-" + suffix, "border-left-" + suffix);
        }

        private static void ApplyBackgroundShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            ExpandSingleLayerBackgroundShorthand(
                value,
                (property, shorthandValue) => SetExpanded(computed, property, shorthandValue, source),
                setDefaults: true);
        }

        private static void ApplyFlexFlowShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            foreach (var part in SplitCssValue(value))
            {
                if (part == "wrap" || part == "nowrap" || part == "wrap-reverse")
                    SetExpanded(computed, "flex-wrap", part, source);
                else
                    SetExpanded(computed, "flex-direction", part, source);
            }
        }

        private static void ApplyOverflowShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            var parts = SplitCssValue(value);
            if (parts.Length == 1)
            {
                SetExpanded(computed, "overflow-x", parts[0], source);
                SetExpanded(computed, "overflow-y", parts[0], source);
            }
            else if (parts.Length >= 2)
            {
                SetExpanded(computed, "overflow-x", parts[0], source);
                SetExpanded(computed, "overflow-y", parts[1], source);
            }
        }

        private static void ApplyOutlineShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            foreach (var part in SplitCssValue(value))
            {
                if (IsBorderStyle(part)) SetExpanded(computed, "outline-style", part, source);
                else if (IsCssLength(part)) SetExpanded(computed, "outline-width", part, source);
                else SetExpanded(computed, "outline-color", part, source);
            }
        }

        private static void ApplyListStyleShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            string raw = value?.Trim() ?? string.Empty;
            if (CssWideKeywords.Contains(raw))
            {
                SetExpanded(computed, "list-style-type", raw, source);
                SetExpanded(computed, "list-style-position", raw, source);
                SetExpanded(computed, "list-style-image", raw, source);
                return;
            }

            string type = null;
            string position = null;
            string image = null;
            foreach (var part in SplitCssValue(value))
            {
                string keyword = part.ToLowerInvariant();
                if (keyword == "inside" || keyword == "outside")
                    position = keyword;
                else if (keyword.StartsWith("url(", StringComparison.Ordinal))
                    image = part;
                else if (keyword == "none")
                {
                    if (type == null) type = "none";
                    else image = "none";
                }
                else
                    type = keyword;
            }

            SetExpanded(computed, "list-style-type", type ?? "disc", source);
            SetExpanded(computed, "list-style-position", position ?? "outside", source);
            SetExpanded(computed, "list-style-image", image ?? "none", source);
        }

        private static void ApplyGapShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            var parts = SplitCssValue(value);
            if (parts.Length == 1)
            {
                SetExpanded(computed, "row-gap", parts[0], source);
                SetExpanded(computed, "column-gap", parts[0], source);
            }
            else if (parts.Length >= 2)
            {
                SetExpanded(computed, "row-gap", parts[0], source);
                SetExpanded(computed, "column-gap", parts[1], source);
            }
        }

        private static void ApplyBorderRadiusShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            var slashIdx = value.IndexOf('/');
            var mainPart = slashIdx >= 0 ? value.Substring(0, slashIdx).Trim() : value;
            var parts = SplitCssValue(mainPart);
            if (parts.Length == 0) return;

            string tl, tr, br, bl;
            switch (parts.Length)
            {
                case 1: tl = tr = br = bl = parts[0]; break;
                case 2: tl = br = parts[0]; tr = bl = parts[1]; break;
                case 3: tl = parts[0]; tr = bl = parts[1]; br = parts[2]; break;
                default: tl = parts[0]; tr = parts[1]; br = parts[2]; bl = parts[3]; break;
            }

            SetExpanded(computed, "border-top-left-radius", tl, source);
            SetExpanded(computed, "border-top-right-radius", tr, source);
            SetExpanded(computed, "border-bottom-right-radius", br, source);
            SetExpanded(computed, "border-bottom-left-radius", bl, source);
        }

        private static void ApplyInsetShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            var parts = SplitCssValue(value);
            if (parts.Length == 0) return;

            string vTop, vRight, vBottom, vLeft;
            switch (parts.Length)
            {
                case 1: vTop = vRight = vBottom = vLeft = parts[0]; break;
                case 2: vTop = vBottom = parts[0]; vRight = vLeft = parts[1]; break;
                case 3: vTop = parts[0]; vRight = vLeft = parts[1]; vBottom = parts[2]; break;
                default: vTop = parts[0]; vRight = parts[1]; vBottom = parts[2]; vLeft = parts[3]; break;
            }

            SetExpanded(computed, "top", vTop, source);
            SetExpanded(computed, "right", vRight, source);
            SetExpanded(computed, "bottom", vBottom, source);
            SetExpanded(computed, "left", vLeft, source);
        }

        private static void ApplyTransitionShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ExpandTransitionShorthandCore(value, (property, shorthandValue) => SetExpanded(computed, property, shorthandValue, source));
        }

        private static readonly HashSet<string> FontStyleKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "normal", "italic", "oblique"
        };

        private static readonly HashSet<string> FontVariantKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "normal", "small-caps"
        };

        private static readonly HashSet<string> FontWeightKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "normal", "bold", "bolder", "lighter",
            "100", "200", "300", "400", "500", "600", "700", "800", "900"
        };

        private static readonly HashSet<string> FontStretchKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "normal",
            "ultra-condensed", "extra-condensed", "condensed", "semi-condensed",
            "semi-expanded", "expanded", "extra-expanded", "ultra-expanded"
        };

        private static readonly HashSet<string> FontSizeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "xx-small", "x-small", "small", "medium", "large", "x-large", "xx-large",
            "xxx-large", "smaller", "larger"
        };

        private static readonly HashSet<string> CssWideKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "inherit", "initial", "unset", "revert", "revert-layer"
        };

        private static readonly HashSet<string> SystemFontKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "caption", "icon", "menu", "message-box", "small-caption", "status-bar"
        };

        private static void ApplyFontShorthand(Dictionary<string, CssDeclaration> computed, CssDeclaration source, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string raw = value.Trim();
            string lowerRaw = raw.ToLowerInvariant();

            if (CssWideKeywords.Contains(lowerRaw))
            {
                SetExpanded(computed, "font-style", raw, source);
                SetExpanded(computed, "font-variant", raw, source);
                SetExpanded(computed, "font-weight", raw, source);
                SetExpanded(computed, "font-stretch", raw, source);
                SetExpanded(computed, "font-size", raw, source);
                SetExpanded(computed, "line-height", raw, source);
                SetExpanded(computed, "font-family", raw, source);
                return;
            }

            if (SystemFontKeywords.Contains(lowerRaw))
            {
                // System font keywords are valid but intentionally left as raw shorthand
                // for now because full platform mapping is not implemented yet.
                return;
            }

            var parts = SplitCssValue(raw);
            if (parts.Length == 0)
            {
                return;
            }

            string fontStyle = null;
            string fontVariant = null;
            string fontWeight = null;
            string fontStretch = null;
            string fontSize = null;
            string lineHeight = null;
            int sizeIndex = -1;
            int familyStartIndex = -1;

            for (int i = 0; i < parts.Length; i++)
            {
                var token = parts[i];
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (TryParseFontSizeToken(token, out var parsedSize, out var parsedLineHeight))
                {
                    fontSize = parsedSize;
                    lineHeight = parsedLineHeight;
                    sizeIndex = i;
                    familyStartIndex = i + 1;

                    if (lineHeight == null &&
                        familyStartIndex < parts.Length &&
                        string.Equals(parts[familyStartIndex], "/", StringComparison.Ordinal))
                    {
                        if (familyStartIndex + 1 < parts.Length)
                        {
                            lineHeight = parts[familyStartIndex + 1];
                            familyStartIndex += 2;
                        }
                        else
                        {
                            return;
                        }
                    }

                    break;
                }
            }

            if (sizeIndex < 0 || string.IsNullOrWhiteSpace(fontSize) || familyStartIndex >= parts.Length)
            {
                return;
            }

            for (int i = 0; i < sizeIndex; i++)
            {
                var token = parts[i];
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (fontStyle == null && FontStyleKeywords.Contains(token))
                {
                    fontStyle = token;
                    continue;
                }

                if (fontVariant == null && FontVariantKeywords.Contains(token))
                {
                    fontVariant = token;
                    continue;
                }

                if (fontWeight == null && FontWeightKeywords.Contains(token))
                {
                    fontWeight = token;
                    continue;
                }

                if (fontStretch == null && FontStretchKeywords.Contains(token))
                {
                    fontStretch = token;
                    continue;
                }
            }

            string fontFamily = string.Join(" ", parts.Skip(familyStartIndex)).Trim();
            if (string.IsNullOrWhiteSpace(fontFamily))
            {
                return;
            }

            // CSS Fonts: omitted longhands in font shorthand reset to initial values.
            SetExpanded(computed, "font-style", fontStyle ?? "normal", source);
            SetExpanded(computed, "font-variant", fontVariant ?? "normal", source);
            SetExpanded(computed, "font-weight", fontWeight ?? "normal", source);
            SetExpanded(computed, "font-stretch", fontStretch ?? "normal", source);
            SetExpanded(computed, "font-size", fontSize, source);
            SetExpanded(computed, "line-height", lineHeight ?? "normal", source);
            SetExpanded(computed, "font-family", fontFamily, source);
        }

        private static bool TryParseFontSizeToken(string token, out string fontSize, out string lineHeight)
        {
            fontSize = null;
            lineHeight = null;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var trimmed = token.Trim();
            int slash = trimmed.IndexOf('/');
            if (slash >= 0)
            {
                var left = trimmed.Substring(0, slash).Trim();
                var right = trimmed.Substring(slash + 1).Trim();
                if (!IsFontSizeToken(left))
                {
                    return false;
                }

                fontSize = left;
                if (!string.IsNullOrEmpty(right))
                {
                    lineHeight = right;
                }

                return true;
            }

            if (!IsFontSizeToken(trimmed))
            {
                return false;
            }

            fontSize = trimmed;
            return true;
        }

        private static bool IsFontSizeToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var normalized = token.Trim().ToLowerInvariant();
            if (FontSizeKeywords.Contains(normalized))
            {
                return true;
            }

            if (normalized == "0")
            {
                return true;
            }

            if (normalized.StartsWith("var(", StringComparison.Ordinal) ||
                normalized.StartsWith("calc(", StringComparison.Ordinal) ||
                normalized.StartsWith("min(", StringComparison.Ordinal) ||
                normalized.StartsWith("max(", StringComparison.Ordinal) ||
                normalized.StartsWith("clamp(", StringComparison.Ordinal))
            {
                return true;
            }

            return IsCssLength(normalized);
        }

        /// <summary>
        /// Expand CSS shorthand properties into their longhand equivalents.
        /// Only sets longhands that weren't explicitly declared with higher specificity.
        /// </summary>
        private static void ExpandShorthands(Dictionary<string, CssDeclaration> computed)
        {
            // Process each shorthand â†’ longhand expansion
            ExpandBoxShorthand(computed, "margin", "margin-top", "margin-right", "margin-bottom", "margin-left");
            ExpandBoxShorthand(computed, "padding", "padding-top", "padding-right", "padding-bottom", "padding-left");
            ExpandBorderShorthand(computed);
            ExpandBackgroundShorthand(computed);
            ExpandFlexFlowShorthand(computed);
            ExpandOverflowShorthand(computed);
            ExpandOutlineShorthand(computed);
            ExpandListStyleShorthand(computed);
            ExpandGapShorthand(computed);
            ExpandBorderRadiusShorthand(computed);
            ExpandInsetShorthand(computed);
            ExpandTransitionShorthand(computed);
        }

        /// <summary>
        /// Expand box model shorthands (margin, padding) using 1-4 value syntax.
        /// margin: 10px â†’ all four sides 10px
        /// margin: 10px 20px â†’ top/bottom 10px, left/right 20px
        /// margin: 10px 20px 30px â†’ top 10px, left/right 20px, bottom 30px
        /// margin: 10px 20px 30px 40px â†’ top right bottom left
        /// </summary>
        private static void ExpandBoxShorthand(Dictionary<string, CssDeclaration> computed,
            string shorthand, string top, string right, string bottom, string left)
        {
            if (!computed.TryGetValue(shorthand, out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            var parts = SplitCssValue(value);
            string vTop, vRight, vBottom, vLeft;

            switch (parts.Length)
            {
                case 1:
                    vTop = vRight = vBottom = vLeft = parts[0];
                    break;
                case 2:
                    vTop = vBottom = parts[0];
                    vRight = vLeft = parts[1];
                    break;
                case 3:
                    vTop = parts[0];
                    vRight = vLeft = parts[1];
                    vBottom = parts[2];
                    break;
                default: // 4+
                    vTop = parts[0];
                    vRight = parts[1];
                    vBottom = parts[2];
                    vLeft = parts[3];
                    break;
            }

            SetIfNotExplicit(computed, top, vTop, decl);
            SetIfNotExplicit(computed, right, vRight, decl);
            SetIfNotExplicit(computed, bottom, vBottom, decl);
            SetIfNotExplicit(computed, left, vLeft, decl);
        }

        /// <summary>
        /// Expand border shorthand: border: 1px solid black â†’ border-width, border-style, border-color
        /// </summary>
        private static void ExpandBorderShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("border", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;
            if (value == "none" || value == "0")
            {
                SetIfNotExplicit(computed, "border-top-width", "0", decl);
                SetIfNotExplicit(computed, "border-right-width", "0", decl);
                SetIfNotExplicit(computed, "border-bottom-width", "0", decl);
                SetIfNotExplicit(computed, "border-left-width", "0", decl);
                SetIfNotExplicit(computed, "border-top-style", "none", decl);
                SetIfNotExplicit(computed, "border-right-style", "none", decl);
                SetIfNotExplicit(computed, "border-bottom-style", "none", decl);
                SetIfNotExplicit(computed, "border-left-style", "none", decl);
                return;
            }

            var parts = SplitCssValue(value);
            string width = null, style = null, color = null;

            foreach (var p in parts)
            {
                if (IsBorderStyle(p)) style = p;
                else if (IsCssLength(p)) width = p;
                else color = p;
            }

            if (width != null)
            {
                SetIfNotExplicit(computed, "border-top-width", width, decl);
                SetIfNotExplicit(computed, "border-right-width", width, decl);
                SetIfNotExplicit(computed, "border-bottom-width", width, decl);
                SetIfNotExplicit(computed, "border-left-width", width, decl);
            }
            if (style != null)
            {
                SetIfNotExplicit(computed, "border-top-style", style, decl);
                SetIfNotExplicit(computed, "border-right-style", style, decl);
                SetIfNotExplicit(computed, "border-bottom-style", style, decl);
                SetIfNotExplicit(computed, "border-left-style", style, decl);
            }
            if (color != null)
            {
                SetIfNotExplicit(computed, "border-top-color", color, decl);
                SetIfNotExplicit(computed, "border-right-color", color, decl);
                SetIfNotExplicit(computed, "border-bottom-color", color, decl);
                SetIfNotExplicit(computed, "border-left-color", color, decl);
            }

            // Also expand border-top/right/bottom/left if present
            foreach (var side in new[] { "border-top", "border-right", "border-bottom", "border-left" })
            {
                if (!computed.TryGetValue(side, out var sideDecl)) continue;
                var sv = sideDecl.Value?.Trim();
                if (string.IsNullOrEmpty(sv)) continue;
                var sp = SplitCssValue(sv);
                string sw = null, ss = null, sc = null;
                foreach (var p in sp)
                {
                    if (IsBorderStyle(p)) ss = p;
                    else if (IsCssLength(p)) sw = p;
                    else sc = p;
                }
                if (sw != null) SetIfNotExplicit(computed, side + "-width", sw, sideDecl);
                if (ss != null) SetIfNotExplicit(computed, side + "-style", ss, sideDecl);
                if (sc != null) SetIfNotExplicit(computed, side + "-color", sc, sideDecl);
            }
        }

        private static void ExpandBackgroundShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("background", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            ExpandSingleLayerBackgroundShorthand(
                value,
                (property, shorthandValue) => SetIfNotExplicit(computed, property, shorthandValue, decl),
                setDefaults: true);
        }

        private static void ExpandSingleLayerBackgroundShorthand(
            string value,
            Action<string, string> assign,
            bool setDefaults)
        {
            if (string.IsNullOrWhiteSpace(value) || assign == null)
            {
                return;
            }

            if (value == "none" || value == "transparent")
            {
                assign("background-color", "transparent");
                assign("background-image", "none");
                return;
            }

            string color = null;
            string image = null;
            string repeat = null;
            string attachment = null;
            string origin = null;
            string clip = null;
            bool parsingSize = false;
            var positionTokens = new List<string>();
            var sizeTokens = new List<string>();

            foreach (var token in SplitCssValue(value))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (token == "/")
                {
                    parsingSize = true;
                    continue;
                }

                if (token.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                    token.Contains("gradient(", StringComparison.OrdinalIgnoreCase))
                {
                    ProcessToken(token);
                    continue;
                }

                var tokenParts = token.Split('/', 2);
                var head = tokenParts[0];
                var tail = tokenParts.Length == 2 ? tokenParts[1] : null;
                if (tokenParts.Length == 2)
                {
                    parsingSize = true;
                }

                void ProcessToken(string tokenValue)
                {
                    if (string.IsNullOrWhiteSpace(tokenValue))
                    {
                        return;
                    }

                    tokenValue = tokenValue.Trim();
                    if (tokenValue.EndsWith(",", StringComparison.Ordinal))
                    {
                        tokenValue = tokenValue.Substring(0, tokenValue.Length - 1).Trim();
                        if (tokenValue.Length == 0)
                        {
                            return;
                        }
                    }

                    if (tokenValue.StartsWith("url(", StringComparison.OrdinalIgnoreCase) ||
                        tokenValue.Contains("gradient(", StringComparison.OrdinalIgnoreCase))
                    {
                        if (image == null)
                        {
                            image = tokenValue;
                        }
                        return;
                    }

                    if (parsingSize && IsBackgroundSizeToken(tokenValue))
                    {
                        if (sizeTokens.Count < 2)
                        {
                            sizeTokens.Add(tokenValue);
                        }
                        return;
                    }

                    if (IsBackgroundRepeat(tokenValue))
                    {
                        repeat = tokenValue;
                        return;
                    }

                    if (tokenValue == "fixed" || tokenValue == "scroll" || tokenValue == "local")
                    {
                        attachment = tokenValue;
                        return;
                    }

                    if (IsBackgroundBoxToken(tokenValue))
                    {
                        if (origin == null)
                        {
                            origin = tokenValue;
                        }
                        else
                        {
                            clip = tokenValue;
                        }
                        return;
                    }

                    if (IsBackgroundPositionToken(tokenValue))
                    {
                        if (positionTokens.Count < 2)
                        {
                            positionTokens.Add(tokenValue);
                        }
                        return;
                    }

                    color = tokenValue;
                }

                ProcessToken(head);
                if (tokenParts.Length == 2)
                {
                    ProcessToken(tail);
                }

                if (parsingSize && sizeTokens.Count >= 2)
                {
                    parsingSize = false;
                }
            }

            if (color != null)
            {
                assign("background-color", color);
            }
            else if (setDefaults)
            {
                assign("background-color", "transparent");
            }

            if (image != null)
            {
                assign("background-image", image);
            }
            else if (setDefaults)
            {
                assign("background-image", "none");
            }

            if (repeat != null)
            {
                assign("background-repeat", repeat);
            }

            if (attachment != null)
            {
                assign("background-attachment", attachment);
            }

            if (positionTokens.Count > 0)
            {
                assign("background-position", string.Join(" ", positionTokens.Take(2)));
                assign("background-position-x", positionTokens[0]);
                assign("background-position-y", positionTokens.Count > 1 ? positionTokens[1] : "center");
            }

            if (sizeTokens.Count > 0)
            {
                assign("background-size", string.Join(" ", sizeTokens.Take(2)));
            }

            if (origin != null)
            {
                assign("background-origin", origin);
            }

            if (clip != null)
            {
                assign("background-clip", clip);
            }
            else if (origin != null)
            {
                // Single background-box token applies to both origin and clip.
                assign("background-clip", origin);
            }
        }

        private static void ExpandFlexFlowShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("flex-flow", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            foreach (var p in parts)
            {
                if (p == "wrap" || p == "nowrap" || p == "wrap-reverse")
                    SetIfNotExplicit(computed, "flex-wrap", p, decl);
                else
                    SetIfNotExplicit(computed, "flex-direction", p, decl);
            }
        }

        private static void ExpandOverflowShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("overflow", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            if (parts.Length == 1)
            {
                SetIfNotExplicit(computed, "overflow-x", parts[0], decl);
                SetIfNotExplicit(computed, "overflow-y", parts[0], decl);
            }
            else if (parts.Length >= 2)
            {
                SetIfNotExplicit(computed, "overflow-x", parts[0], decl);
                SetIfNotExplicit(computed, "overflow-y", parts[1], decl);
            }
        }

        private static void ExpandOutlineShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("outline", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            foreach (var p in parts)
            {
                if (IsBorderStyle(p)) SetIfNotExplicit(computed, "outline-style", p, decl);
                else if (IsCssLength(p)) SetIfNotExplicit(computed, "outline-width", p, decl);
                else SetIfNotExplicit(computed, "outline-color", p, decl);
            }
        }

        private static void ExpandListStyleShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("list-style", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            foreach (var p in parts)
            {
                if (p == "inside" || p == "outside")
                    SetIfNotExplicit(computed, "list-style-position", p, decl);
                else if (p.StartsWith("url("))
                    SetIfNotExplicit(computed, "list-style-image", p, decl);
                else
                    SetIfNotExplicit(computed, "list-style-type", p, decl);
            }
        }

        private static void ExpandGapShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("gap", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            if (parts.Length == 1)
            {
                SetIfNotExplicit(computed, "row-gap", parts[0], decl);
                SetIfNotExplicit(computed, "column-gap", parts[0], decl);
            }
            else if (parts.Length >= 2)
            {
                SetIfNotExplicit(computed, "row-gap", parts[0], decl);
                SetIfNotExplicit(computed, "column-gap", parts[1], decl);
            }
        }

        private static void ExpandBorderRadiusShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("border-radius", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;

            // Handle slash syntax for elliptical: "10px / 5px"
            // For simplicity, just handle the before-slash part
            var slashIdx = value.IndexOf('/');
            var mainPart = slashIdx >= 0 ? value.Substring(0, slashIdx).Trim() : value;
            var parts = SplitCssValue(mainPart);

            string tl, tr, br, bl;
            switch (parts.Length)
            {
                case 1: tl = tr = br = bl = parts[0]; break;
                case 2: tl = br = parts[0]; tr = bl = parts[1]; break;
                case 3: tl = parts[0]; tr = bl = parts[1]; br = parts[2]; break;
                default: tl = parts[0]; tr = parts[1]; br = parts[2]; bl = parts[3]; break;
            }

            SetIfNotExplicit(computed, "border-top-left-radius", tl, decl);
            SetIfNotExplicit(computed, "border-top-right-radius", tr, decl);
            SetIfNotExplicit(computed, "border-bottom-right-radius", br, decl);
            SetIfNotExplicit(computed, "border-bottom-left-radius", bl, decl);
        }

        private static void ExpandInsetShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("inset", out var decl)) return;
            var parts = SplitCssValue(decl.Value?.Trim() ?? "");
            string vTop, vRight, vBottom, vLeft;
            switch (parts.Length)
            {
                case 1: vTop = vRight = vBottom = vLeft = parts[0]; break;
                case 2: vTop = vBottom = parts[0]; vRight = vLeft = parts[1]; break;
                case 3: vTop = parts[0]; vRight = vLeft = parts[1]; vBottom = parts[2]; break;
                default: vTop = parts[0]; vRight = parts[1]; vBottom = parts[2]; vLeft = parts[3]; break;
            }
            SetIfNotExplicit(computed, "top", vTop, decl);
            SetIfNotExplicit(computed, "right", vRight, decl);
            SetIfNotExplicit(computed, "bottom", vBottom, decl);
            SetIfNotExplicit(computed, "left", vLeft, decl);
        }

        private static void ExpandTransitionShorthand(Dictionary<string, CssDeclaration> computed)
        {
            if (!computed.TryGetValue("transition", out var decl)) return;
            var value = decl.Value?.Trim();
            if (string.IsNullOrEmpty(value)) return;
            ExpandTransitionShorthandCore(value, (property, shorthandValue) => SetIfNotExplicit(computed, property, shorthandValue, decl));
        }

        private static void ExpandTransitionShorthandCore(string value, Action<string, string> assign)
        {
            if (string.IsNullOrWhiteSpace(value) || assign == null)
            {
                return;
            }

            var items = SplitTopLevel(value, ",");
            if (items.Count == 0)
            {
                return;
            }

            var properties = new List<string>(items.Count);
            var durations = new List<string>(items.Count);
            var timingFunctions = new List<string>(items.Count);
            var delays = new List<string>(items.Count);
            var behaviors = new List<string>(items.Count);

            foreach (var item in items)
            {
                ParseTransitionComponent(item, out var property, out var duration, out var timingFunction, out var delay, out var behavior);
                properties.Add(property ?? "all");
                durations.Add(duration ?? "0s");
                timingFunctions.Add(timingFunction ?? "ease");
                delays.Add(delay ?? "0s");
                behaviors.Add(behavior ?? "normal");
            }

            assign("transition-property", string.Join(", ", properties));
            assign("transition-duration", string.Join(", ", durations));
            assign("transition-timing-function", string.Join(", ", timingFunctions));
            assign("transition-delay", string.Join(", ", delays));

            if (behaviors.Any(b => !string.Equals(b, "normal", StringComparison.OrdinalIgnoreCase)))
            {
                assign("transition-behavior", string.Join(", ", behaviors));
            }
        }

        private static void ParseTransitionComponent(
            string component,
            out string property,
            out string duration,
            out string timingFunction,
            out string delay,
            out string behavior)
        {
            property = null;
            duration = null;
            timingFunction = null;
            delay = null;
            behavior = null;

            foreach (var token in SplitCssValue(component ?? string.Empty))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (IsTransitionTimeToken(token))
                {
                    if (duration == null) duration = token;
                    else if (delay == null) delay = token;
                    continue;
                }

                if (IsTransitionTimingFunctionToken(token))
                {
                    timingFunction = token;
                    continue;
                }

                if (IsTransitionBehaviorToken(token))
                {
                    behavior = token;
                    continue;
                }

                if (property == null)
                {
                    property = token;
                }
            }
        }

        /// <summary>
        /// Set a longhand property only if it wasn't explicitly declared (higher specificity wins).
        /// </summary>
        private static void SetIfNotExplicit(Dictionary<string, CssDeclaration> computed, string property, string value, CssDeclaration source)
        {
            if (computed.ContainsKey(property)) return; // Explicit longhand wins
            computed[property] = new CssDeclaration
            {
                Property = property,
                Value = value,
                IsImportant = source.IsImportant
            };
        }

        private static string[] SplitCssValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
            // Handle function parentheses (don't split inside them)
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if ((c == ' ' || c == '\t') && depth == 0)
                {
                    if (i > start)
                        parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < value.Length)
                parts.Add(value.Substring(start));
            return parts.ToArray();
        }

        private static List<string> SplitTopLevel(string value, string separator)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }

            int depth = 0;
            int start = 0;
            for (int i = 0; i <= value.Length - separator.Length;)
            {
                char c = value[i];
                if (c == '(')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (c == ')')
                {
                    if (depth > 0) depth--;
                    i++;
                    continue;
                }

                if (depth == 0 &&
                    value.AsSpan(i, separator.Length).Equals(separator.AsSpan(), StringComparison.Ordinal))
                {
                    var part = value.Substring(start, i - start).Trim();
                    if (part.Length > 0)
                    {
                        result.Add(part);
                    }

                    i += separator.Length;
                    start = i;
                    continue;
                }

                i++;
            }

            if (start < value.Length)
            {
                var tail = value.Substring(start).Trim();
                if (tail.Length > 0)
                {
                    result.Add(tail);
                }
            }

            return result;
        }

        private static bool IsBorderStyle(string value)
        {
            switch (value)
            {
                case "none": case "hidden": case "dotted": case "dashed": case "solid":
                case "double": case "groove": case "ridge": case "inset": case "outset":
                    return true;
                default: return false;
            }
        }

        private static bool IsCssLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value == "0") return true;
            if (value == "thin" || value == "medium" || value == "thick") return true;
            // Check if it ends with a unit
            return value.EndsWith("px") || value.EndsWith("em") || value.EndsWith("rem") ||
                   value.EndsWith("pt") || value.EndsWith("vh") || value.EndsWith("vw") ||
                   value.EndsWith("%") || value.EndsWith("ch") || value.EndsWith("ex") ||
                   value.EndsWith("cm") || value.EndsWith("mm") || value.EndsWith("in") ||
                   value.EndsWith("pc");
        }

        private static bool IsBackgroundRepeat(string value)
        {
            return value == "repeat" || value == "no-repeat" || value == "repeat-x" ||
                   value == "repeat-y" || value == "space" || value == "round";
        }

        private static bool IsBackgroundPosition(string value)
        {
            return value == "center" || value == "top" || value == "bottom" ||
                   value == "left" || value == "right";
        }

        private static bool IsBackgroundPositionToken(string value)
        {
            return IsBackgroundPosition(value) || IsCssLength(value);
        }

        private static bool IsBackgroundBoxToken(string value)
        {
            return value == "border-box" || value == "padding-box" || value == "content-box";
        }

        private static bool IsBackgroundSizeToken(string value)
        {
            return value == "auto" || value == "cover" || value == "contain" || IsCssLength(value);
        }

        private static bool IsTransitionTimeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var token = value.Trim().ToLowerInvariant();
            if (token.EndsWith("ms", StringComparison.Ordinal))
            {
                return double.TryParse(token.Substring(0, token.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            }

            if (token.EndsWith("s", StringComparison.Ordinal) && token.Length > 1)
            {
                return double.TryParse(token.Substring(0, token.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            }

            return false;
        }

        private static bool IsTransitionTimingFunctionToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var token = value.Trim().ToLowerInvariant();
            return token == "ease" ||
                   token == "linear" ||
                   token == "ease-in" ||
                   token == "ease-out" ||
                   token == "ease-in-out" ||
                   token == "step-start" ||
                   token == "step-end" ||
                   token.StartsWith("cubic-bezier(", StringComparison.Ordinal) ||
                   token.StartsWith("steps(", StringComparison.Ordinal) ||
                   token.StartsWith("linear(", StringComparison.Ordinal);
        }

        private static bool IsTransitionBehaviorToken(string value)
        {
            return string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "allow-discrete", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class MatchedDeclaration : IComparable<MatchedDeclaration>
    {
        public CssDeclaration Declaration { get; set; }
        public CascadeKey Key { get; set; }
        public string SelectorText { get; set; }

        public int CompareTo(MatchedDeclaration other)
        {
            return Key.CompareTo(other.Key);
        }
    }

    public readonly record struct InlineStyleCacheStatistics(
        int Hits,
        int Misses,
        int Evictions,
        int Entries,
        int Capacity);
}


