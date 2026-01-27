using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FenBrowser.Core.Network;

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
            return Task.Run(() =>
            {
                if (string.IsNullOrEmpty(_html) || _prefetcher == null) return;

                // Simple regex-based scanning for speed. 
                // A full tokenizer would be more accurate but slower.
                // We scan for <link>, <script>, <img> tags.
                
                // Matches <link ... href="..." ... >
                ScanLinks();

                // Matches <script ... src="..." ... >
                ScanScripts();

                // Matches <img ... src="..." ... >
                ScanImages();
            });
        }

        private void ScanLinks()
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
                            
                            ResourceHint hint = ResourceHint.Preload; // Default to preload-ish behavior? Or just fetch?
                            PreloadAs asType = PreloadAs.Fetch;

                            if (relMatch.Success)
                            {
                                var rel = relMatch.Groups[1].Value.ToLowerInvariant();
                                if (rel == "stylesheet") 
                                {
                                    asType = PreloadAs.Style; 
                                }
                                else if (rel == "preload")
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
                                }
                                else
                                {
                                    // Ignore verification/icons for now
                                    continue; 
                                }
                            }
                            else
                            {
                                // No rel? links usually need rel.
                                continue;
                            }

                            _prefetcher.QueueHintAsync(url, hint, asType).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch { /* Ignore regex errors in speculative scan */ }
        }

        private void ScanScripts()
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
                            _prefetcher.QueueHintAsync(url, ResourceHint.Preload, PreloadAs.Script).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch { }
        }

        private void ScanImages()
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
                            _prefetcher.QueueHintAsync(url, ResourceHint.Preload, PreloadAs.Image).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
