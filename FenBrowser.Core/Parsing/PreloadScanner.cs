using System;
using System.Collections.Generic;
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
        private readonly string _html;
        private readonly Uri _baseUri;
        private readonly ResourcePrefetcher _prefetcher;
        private Uri _observedBaseUri;
        private bool _observedBaseElement;

        public PreloadScanner(string html, Uri baseUri, ResourcePrefetcher prefetcher)
        {
            _html = html;
            _baseUri = baseUri;
            _prefetcher = prefetcher;
            _observedBaseUri = baseUri;
        }

        public void ObserveStartTag(StartTagToken token)
        {
            if (token == null || _baseUri == null || string.IsNullOrEmpty(token.TagName))
                return;

            if (token.TagName.Equals("base", StringComparison.OrdinalIgnoreCase) &&
                !_observedBaseElement &&
                TryResolveUrl(_baseUri, GetAttributeOrNull(token, "href"), out var resolvedBase))
            {
                _observedBaseUri = resolvedBase;
                _observedBaseElement = true;
                return;
            }

            if (token.TagName.Equals("link", StringComparison.OrdinalIgnoreCase))
                QueueLinkCore(token, _observedBaseUri);
            else if (token.TagName.Equals("script", StringComparison.OrdinalIgnoreCase))
                QueueScriptCore(token, _observedBaseUri);
            else if (token.TagName.Equals("img", StringComparison.OrdinalIgnoreCase))
                QueueImageCore(token, _observedBaseUri);
        }

        public Task ScanAsync()
        {
            if (string.IsNullOrEmpty(_html) || _baseUri == null)
            {
                return Task.CompletedTask;
            }

            try
            {
                _observedBaseUri = _baseUri;
                _observedBaseElement = false;
                var tokenizer = new HtmlTokenizer(_html);
                foreach (var token in tokenizer.Tokenize())
                {
                    if (token is not StartTagToken startTag)
                    {
                        continue;
                    }

                    ObserveStartTag(startTag);
                    var name = startTag.TagName;
                    tokenizer.LastStartTagName = name;
                    if (name.Equals("script", StringComparison.OrdinalIgnoreCase))
                    {
                        tokenizer.SetState(HtmlTokenizer.TokenizerState.ScriptData);
                    }
                    else if (name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("textarea", StringComparison.OrdinalIgnoreCase))
                    {
                        tokenizer.SetState(HtmlTokenizer.TokenizerState.RcData);
                    }
                    else if (name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("xmp", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("iframe", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("noembed", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("noframes", StringComparison.OrdinalIgnoreCase))
                    {
                        tokenizer.SetState(HtmlTokenizer.TokenizerState.RawText);
                    }
                    else if (name.Equals("plaintext", StringComparison.OrdinalIgnoreCase))
                    {
                        tokenizer.SetState(HtmlTokenizer.TokenizerState.PlainText);
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug(
                    $"[PreloadScanner] Lexical scan failed (type={ex.GetType().Name}).",
                    LogCategory.HtmlParsing);
            }

            return Task.CompletedTask;
        }

        private void QueueLinkCore(StartTagToken token, Uri baseUri)
        {
            var relValue = GetAttributeOrNull(token, "rel");
            var href = GetAttributeOrNull(token, "href");
            if (string.IsNullOrWhiteSpace(relValue) ||
                !TryResolveUrl(baseUri, href, out var url))
            {
                return;
            }

            var relTokens = TokenizeRel(relValue);
            ResourceHint? hint = null;
            var asType = ParseAsType(GetAttributeOrNull(token, "as"));
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
                _ = _prefetcher.QueueHintAsync(
                    url,
                    hint.Value,
                    asType,
                    GetAttributeOrNull(token, "crossorigin"),
                    GetAttributeOrNull(token, "type"));
            }
        }

        private void QueueScriptCore(StartTagToken token, Uri baseUri)
        {
            if (!TryResolveUrl(baseUri, GetAttributeOrNull(token, "src"), out var url))
            {
                return;
            }

            EmitResourceDiscovered("ScriptDiscovered", url, "script-src");
            if (_prefetcher != null)
            {
                _ = _prefetcher.QueueHintAsync(
                    url,
                    ResourceHint.Preload,
                    PreloadAs.Script,
                    GetAttributeOrNull(token, "crossorigin"),
                    GetAttributeOrNull(token, "type"));
            }
        }

        private void QueueImageCore(StartTagToken token, Uri baseUri)
        {
            if (_prefetcher == null ||
                !TryResolveUrl(baseUri, GetAttributeOrNull(token, "src"), out var url))
            {
                return;
            }

            _ = _prefetcher.QueueHintAsync(
                url,
                ResourceHint.Preload,
                PreloadAs.Image,
                GetAttributeOrNull(token, "crossorigin"),
                GetAttributeOrNull(token, "type"));
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

        private static string GetAttributeOrNull(StartTagToken token, string name)
        {
            if (token?.HasAttributes != true)
                return null;

            foreach (var attribute in token.Attributes)
            {
                if (attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return attribute.Value;
            }

            return null;
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
