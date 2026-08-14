using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Lightweight speculative scanner that discovers resource URLs before the main
    /// tree builder reaches them. It is intentionally not a second HTML parser, but it
    /// does preserve the lexical boundaries needed to avoid fetching tag-looking text
    /// from comments and raw-text element bodies.
    /// </summary>
    public sealed class PreloadScanner
    {
        private static readonly HashSet<string> RawTextLikeElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "title", "textarea", "xmp", "iframe", "noembed", "noframes", "plaintext"
        };

        private readonly string _html;
        private readonly Uri _baseUri;
        private readonly ResourcePrefetcher _prefetcher;

        public PreloadScanner(string html, Uri baseUri, ResourcePrefetcher prefetcher)
        {
            _html = html;
            _baseUri = baseUri;
            _prefetcher = prefetcher;
        }

        public Task ScanAsync()
        {
            if (string.IsNullOrEmpty(_html) || _baseUri == null)
            {
                return Task.CompletedTask;
            }

            var tasks = new List<Task>();
            var currentBaseUri = _baseUri;
            var acceptedBaseElement = false;
            var cursor = 0;

            try
            {
                while (cursor < _html.Length)
                {
                    var tagStart = _html.IndexOf('<', cursor);
                    if (tagStart < 0)
                    {
                        break;
                    }

                    if (StartsWithAt(tagStart, "<!--"))
                    {
                        var commentEnd = _html.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                        cursor = commentEnd < 0 ? _html.Length : commentEnd + 3;
                        continue;
                    }

                    if (tagStart + 1 >= _html.Length ||
                        _html[tagStart + 1] is '/' or '!' or '?')
                    {
                        cursor = SkipMarkup(tagStart + 1);
                        continue;
                    }

                    if (!TryReadStartTag(
                            tagStart,
                            out var tagName,
                            out var attributes,
                            out var nextCursor,
                            out var selfClosing))
                    {
                        cursor = tagStart + 1;
                        continue;
                    }

                    cursor = nextCursor;

                    if (string.Equals(tagName, "base", StringComparison.OrdinalIgnoreCase) &&
                        !acceptedBaseElement &&
                        TryGetAttribute(attributes, "href", out var baseHref) &&
                        TryResolveUrl(_baseUri, baseHref, out var resolvedBase))
                    {
                        // HTML uses the first applicable <base href> for subsequent
                        // relative URL resolution. Later base elements must not rewrite
                        // already-discovered resource URLs.
                        currentBaseUri = resolvedBase;
                        acceptedBaseElement = true;
                    }
                    else if (string.Equals(tagName, "link", StringComparison.OrdinalIgnoreCase))
                    {
                        QueueLink(attributes, currentBaseUri, tasks);
                    }
                    else if (string.Equals(tagName, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        QueueScript(attributes, currentBaseUri, tasks);
                    }
                    else if (string.Equals(tagName, "img", StringComparison.OrdinalIgnoreCase))
                    {
                        QueueImage(attributes, currentBaseUri, tasks);
                    }

                    if (!selfClosing && RawTextLikeElements.Contains(tagName))
                    {
                        if (string.Equals(tagName, "plaintext", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        cursor = SkipRawTextBody(tagName, cursor);
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug(
                    $"[PreloadScanner] Lexical scan failed (type={ex.GetType().Name}).",
                    LogCategory.HtmlParsing);
            }

            return tasks.Count > 0 ? Task.WhenAll(tasks) : Task.CompletedTask;
        }

        private void QueueLink(
            IReadOnlyDictionary<string, string> attributes,
            Uri baseUri,
            List<Task> tasks)
        {
            if (!TryGetAttribute(attributes, "rel", out var relValue) ||
                !TryGetAttribute(attributes, "href", out var href) ||
                !TryResolveUrl(baseUri, href, out var url))
            {
                return;
            }

            var relTokens = TokenizeRel(relValue);
            ResourceHint? hint = null;
            var asType = ParseAsType(GetAttributeOrNull(attributes, "as"));
            var discoveryType = string.Empty;
            var eventName = string.Empty;

            if (relTokens.Contains("preload") || relTokens.Contains("modulepreload"))
            {
                hint = ResourceHint.Preload;
                if (relTokens.Contains("modulepreload") && asType == PreloadAs.Unknown)
                {
                    asType = PreloadAs.Script;
                }
                discoveryType = asType == PreloadAs.Style ? "preload-style" :
                    asType == PreloadAs.Script ? "preload-script" : "preload";
                eventName = asType == PreloadAs.Style ? "StylesheetDiscovered" :
                    asType == PreloadAs.Script ? "ScriptDiscovered" : "ResourceDiscovered";
            }
            else if (relTokens.Contains("stylesheet"))
            {
                hint = ResourceHint.Preload;
                asType = PreloadAs.Style;
                discoveryType = "stylesheet";
                eventName = "StylesheetDiscovered";
            }
            else if (relTokens.Contains("prefetch"))
            {
                hint = ResourceHint.Prefetch;
                discoveryType = "prefetch";
                eventName = "ResourceDiscovered";
            }
            else if (relTokens.Contains("preconnect"))
            {
                hint = ResourceHint.Preconnect;
            }
            else if (relTokens.Contains("dns-prefetch"))
            {
                hint = ResourceHint.DnsPrefetch;
            }
            else if (relTokens.Contains("prerender"))
            {
                hint = ResourceHint.Prerender;
            }

            if (!hint.HasValue)
            {
                return;
            }

            if (!string.IsNullOrEmpty(eventName))
            {
                EmitResourceDiscovered(eventName, url, discoveryType);
            }

            if (_prefetcher != null)
            {
                tasks.Add(_prefetcher.QueueHintAsync(
                    url,
                    hint.Value,
                    asType,
                    GetAttributeOrNull(attributes, "crossorigin"),
                    GetAttributeOrNull(attributes, "type")));
            }
        }

        private void QueueScript(
            IReadOnlyDictionary<string, string> attributes,
            Uri baseUri,
            List<Task> tasks)
        {
            if (!TryGetAttribute(attributes, "src", out var source) ||
                !TryResolveUrl(baseUri, source, out var url))
            {
                return;
            }

            EmitResourceDiscovered("ScriptDiscovered", url, "script-src");
            if (_prefetcher != null)
            {
                tasks.Add(_prefetcher.QueueHintAsync(
                    url,
                    ResourceHint.Preload,
                    PreloadAs.Script,
                    GetAttributeOrNull(attributes, "crossorigin"),
                    GetAttributeOrNull(attributes, "type")));
            }
        }

        private void QueueImage(
            IReadOnlyDictionary<string, string> attributes,
            Uri baseUri,
            List<Task> tasks)
        {
            if (_prefetcher == null ||
                !TryGetAttribute(attributes, "src", out var source) ||
                !TryResolveUrl(baseUri, source, out var url))
            {
                return;
            }

            tasks.Add(_prefetcher.QueueHintAsync(
                url,
                ResourceHint.Preload,
                PreloadAs.Image,
                GetAttributeOrNull(attributes, "crossorigin"),
                GetAttributeOrNull(attributes, "type")));
        }

        private bool TryReadStartTag(
            int start,
            out string tagName,
            out Dictionary<string, string> attributes,
            out int nextCursor,
            out bool selfClosing)
        {
            tagName = null;
            attributes = null;
            nextCursor = start + 1;
            selfClosing = false;

            var i = start + 1;
            if (i >= _html.Length || !IsTagNameChar(_html[i]))
            {
                return false;
            }

            var nameStart = i;
            while (i < _html.Length && IsTagNameChar(_html[i])) i++;
            tagName = _html.Substring(nameStart, i - nameStart).ToLowerInvariant();
            attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (i < _html.Length)
            {
                SkipAsciiWhitespace(ref i);
                if (i >= _html.Length)
                {
                    return false;
                }

                if (_html[i] == '>')
                {
                    nextCursor = i + 1;
                    return true;
                }

                if (_html[i] == '/')
                {
                    var slash = i++;
                    SkipAsciiWhitespace(ref i);
                    if (i < _html.Length && _html[i] == '>')
                    {
                        selfClosing = true;
                        nextCursor = i + 1;
                        return true;
                    }
                    i = slash + 1;
                    continue;
                }

                var attrNameStart = i;
                while (i < _html.Length && IsAttributeNameChar(_html[i])) i++;
                if (i == attrNameStart)
                {
                    // Malformed byte in a tag: make forward progress without treating
                    // the rest of the document as an attribute value.
                    i++;
                    continue;
                }

                var attrName = _html.Substring(attrNameStart, i - attrNameStart).ToLowerInvariant();
                SkipAsciiWhitespace(ref i);
                var attrValue = string.Empty;

                if (i < _html.Length && _html[i] == '=')
                {
                    i++;
                    SkipAsciiWhitespace(ref i);
                    if (i >= _html.Length)
                    {
                        return false;
                    }

                    if (_html[i] is '\'' or '"')
                    {
                        var quote = _html[i++];
                        var valueStart = i;
                        while (i < _html.Length && _html[i] != quote) i++;
                        if (i >= _html.Length)
                        {
                            return false;
                        }
                        attrValue = _html.Substring(valueStart, i - valueStart);
                        i++;
                    }
                    else
                    {
                        var valueStart = i;
                        while (i < _html.Length &&
                               !IsAsciiWhitespace(_html[i]) &&
                               _html[i] != '>')
                        {
                            i++;
                        }
                        attrValue = _html.Substring(valueStart, i - valueStart);
                    }
                }

                // HTML keeps the first duplicate attribute on a token. Matching that
                // behavior prevents a later duplicate href/src from steering only the
                // speculative scanner to a different resource.
                if (!attributes.ContainsKey(attrName))
                {
                    attributes[attrName] = WebUtility.HtmlDecode(attrValue) ?? string.Empty;
                }
            }

            return false;
        }

        private int SkipRawTextBody(string tagName, int cursor)
        {
            var closeNeedle = "</" + tagName;
            var closeStart = _html.IndexOf(closeNeedle, cursor, StringComparison.OrdinalIgnoreCase);
            if (closeStart < 0)
            {
                return _html.Length;
            }

            var closeEnd = _html.IndexOf('>', closeStart + closeNeedle.Length);
            return closeEnd < 0 ? _html.Length : closeEnd + 1;
        }

        private int SkipMarkup(int cursor)
        {
            var quote = '\0';
            while (cursor < _html.Length)
            {
                var c = _html[cursor++];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    break;
                }
            }

            return cursor;
        }

        private bool StartsWithAt(int index, string value)
        {
            if (index < 0 || value == null || index > _html.Length - value.Length)
            {
                return false;
            }

            return _html.AsSpan(index, value.Length).SequenceEqual(value.AsSpan());
        }

        private static bool TryResolveUrl(Uri baseUri, string rawValue, out Uri resolved)
        {
            resolved = null;
            if (baseUri == null || string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            var value = rawValue.Trim();
            if (!Uri.TryCreate(baseUri, value, out var candidate) || !candidate.IsAbsoluteUri)
            {
                return false;
            }

            if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolved = candidate;
            return true;
        }

        private static HashSet<string> TokenizeRel(string rel)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rel))
            {
                return tokens;
            }

            foreach (var token in rel.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (trimmed.Length > 0) tokens.Add(trimmed);
            }

            return tokens;
        }

        private static PreloadAs ParseAsType(string asValue)
        {
            if (string.IsNullOrWhiteSpace(asValue)) return PreloadAs.Unknown;

            return asValue.Trim().ToLowerInvariant() switch
            {
                "script" => PreloadAs.Script,
                "style" => PreloadAs.Style,
                "image" => PreloadAs.Image,
                "font" => PreloadAs.Font,
                "fetch" => PreloadAs.Fetch,
                "document" => PreloadAs.Document,
                "audio" => PreloadAs.Audio,
                "video" => PreloadAs.Video,
                "track" => PreloadAs.Track,
                "worker" => PreloadAs.Worker,
                _ => PreloadAs.Unknown
            };
        }

        private static bool TryGetAttribute(
            IReadOnlyDictionary<string, string> attributes,
            string name,
            out string value)
        {
            value = null;
            return attributes != null &&
                   attributes.TryGetValue(name, out value) &&
                   !string.IsNullOrWhiteSpace(value);
        }

        private static string GetAttributeOrNull(
            IReadOnlyDictionary<string, string> attributes,
            string name)
        {
            return attributes != null && attributes.TryGetValue(name, out var value)
                ? value
                : null;
        }

        private static bool IsTagNameChar(char c)
        {
            return char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_';
        }

        private static bool IsAttributeNameChar(char c)
        {
            return !IsAsciiWhitespace(c) && c is not '=' and not '>' and not '/' and not '<' and not '\'' and not '"';
        }

        private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f';

        private void SkipAsciiWhitespace(ref int index)
        {
            while (index < _html.Length && IsAsciiWhitespace(_html[index])) index++;
        }

        private static void EmitResourceDiscovered(string eventName, Uri url, string discoveryType)
        {
            try
            {
                var safeUrl = GetSafeResourceUrlForLog(url);
                EngineLog.Write(
                    LogSubsystem.Fetch,
                    LogSeverity.Info,
                    eventName,
                    LogMarker.None,
                    new EngineLogContext(
                        NavigationId: LogContext.CurrentCorrelationId,
                        ResourceUrl: safeUrl),
                    new Dictionary<string, object>
                    {
                        ["event"] = eventName,
                        ["traceCategory"] = "ResourceLoader",
                        ["resourceUrl"] = safeUrl,
                        ["resourcePathLength"] = url?.AbsolutePath?.Length ?? 0,
                        ["discoveryType"] = discoveryType ?? string.Empty
                    });
            }
            catch
            {
                // Discovery tracing must not affect parsing or prefetching.
            }
        }

        private static string GetSafeResourceUrlForLog(Uri url)
        {
            if (url == null || !url.IsAbsoluteUri)
            {
                return string.Empty;
            }

            try
            {
                return url.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                return url.Scheme + ":";
            }
        }
    }
}
