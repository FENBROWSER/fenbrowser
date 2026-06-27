using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// speculative token scanner that identifies resource URLs 
    /// (scripts, styles, images) in the HTML stream before the main parser/tree builder reaches them.
    /// </summary>
    public class PreloadScanner
    {
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
            if (string.IsNullOrEmpty(_html))
            {
                return Task.CompletedTask;
            }

            // Simple regex-based scanning for speed.
            // A full tokenizer would be more accurate but slower.
            // We scan for <link>, <script>, <img> tags.

            var tasks = new List<Task>();

            // Matches <link ... href="..." ... >
            ScanLinks(tasks);

            // Matches <script ... src="..." ... >
            ScanScripts(tasks);

            // Matches <img ... src="..." ... >
            ScanImages(tasks);

            return tasks.Count > 0 ? Task.WhenAll(tasks) : Task.CompletedTask;
        }

        private void ScanLinks(List<Task> tasks)
        {
            try 
            {
                var matches = Regex.Matches(_html, @"<link\s+[^>]*href=[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var urlStr = match.Groups[1].Value;
                        if (Uri.TryCreate(_baseUri, urlStr, out var url))
                        {
                            // Basic heuristic: assume stylesheet if not specified, but really we should parse 'rel'
                            // For a simple scanner, we can try to extract rel too.
                            var fullTag = match.Value;
                            var relMatch = Regex.Match(fullTag, @"rel=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                            
                            ResourceHint hint = ResourceHint.Preload;
                            PreloadAs asType = PreloadAs.Fetch;

                            if (relMatch.Success)
                            {
                                var rel = relMatch.Groups[1].Value;
                                var relTokens = TokenizeRel(rel);

                                if (relTokens.Contains("stylesheet")) 
                                {
                                    asType = PreloadAs.Style; 
                                    EmitResourceDiscovered("StylesheetDiscovered", url, fullTag, "stylesheet");
                                }

                                if (relTokens.Contains("preload"))
                                {
                                    hint = ResourceHint.Preload;
                                    // extract 'as'
                                    var asMatch = Regex.Match(fullTag, @"as=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                                    if (asMatch.Success)
                                    {
                                        var asStr = asMatch.Groups[1].Value.ToLowerInvariant();
                                        if (asStr == "style") asType = PreloadAs.Style;
                                        else if (asStr == "script") asType = PreloadAs.Script;
                                        else if (asStr == "image") asType = PreloadAs.Image;
                                    }

                                    if (asType == PreloadAs.Style)
                                    {
                                        EmitResourceDiscovered("StylesheetDiscovered", url, fullTag, "preload-style");
                                    }
                                    else if (asType == PreloadAs.Script)
                                    {
                                        EmitResourceDiscovered("ScriptDiscovered", url, fullTag, "preload-script");
                                    }
                                }

                                if (!relTokens.Contains("stylesheet") && !relTokens.Contains("preload"))
                                {
                                    // Ignore non-fetching links (icons, verification, alternates, etc.).
                                    continue; 
                                }
                            }
                            else
                            {
                                // No rel? links usually need rel.
                                continue;
                            }

                            if (_prefetcher != null)
                            {
                                tasks.Add(_prefetcher.QueueHintAsync(url, hint, asType));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[PreloadScanner] Link scan failed: {ex.Message}", LogCategory.HtmlParsing);
            }
        }

        private void ScanScripts(List<Task> tasks)
        {
            try 
            {
                var matches = Regex.Matches(_html, @"<script\s+[^>]*src=[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var urlStr = match.Groups[1].Value;
                        if (Uri.TryCreate(_baseUri, urlStr, out var url))
                        {
                            EmitResourceDiscovered("ScriptDiscovered", url, match.Value, "script-src");
                            if (_prefetcher != null)
                            {
                                tasks.Add(_prefetcher.QueueHintAsync(url, ResourceHint.Preload, PreloadAs.Script));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[PreloadScanner] Script scan failed: {ex.Message}", LogCategory.HtmlParsing);
            }
        }

        private void ScanImages(List<Task> tasks)
        {
            try
            {
                var matches = Regex.Matches(_html, @"<img\s+[^>]*src=[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var urlStr = match.Groups[1].Value;
                        if (Uri.TryCreate(_baseUri, urlStr, out var url))
                        {
                            if (_prefetcher != null)
                            {
                                tasks.Add(_prefetcher.QueueHintAsync(url, ResourceHint.Preload, PreloadAs.Image));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[PreloadScanner] Image scan failed: {ex.Message}", LogCategory.HtmlParsing);
            }
        }

        private static HashSet<string> TokenizeRel(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return rel.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(static t => t.Trim())
                .Where(static t => t.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static void EmitResourceDiscovered(string eventName, Uri url, string tagSource, string discoveryType)
        {
            try
            {
                EngineLog.Write(
                    LogSubsystem.Fetch,
                    LogSeverity.Info,
                    eventName,
                    LogMarker.None,
                    new EngineLogContext(
                        NavigationId: LogContext.CurrentCorrelationId,
                        ResourceUrl: url?.AbsoluteUri),
                    new Dictionary<string, object>
                    {
                        ["event"] = eventName,
                        ["traceCategory"] = "ResourceLoader",
                        ["resourceUrl"] = url?.AbsoluteUri,
                        ["discoveryType"] = discoveryType,
                        ["tagSample"] = Truncate(tagSource, 240)
                    });
            }
            catch
            {
                // Discovery tracing must not affect parsing or prefetching.
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }
    }
}
