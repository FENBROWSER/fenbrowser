using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FenBrowser.Core.Engine;

namespace FenBrowser.Core.Parsing
{
    public sealed class HtmlParserOptions
    {
        public Uri BaseUri { get; set; }
        public Network.ResourcePrefetcher Prefetcher { get; set; }
        public ParserSecurityPolicy SecurityPolicy { get; set; }
        public int? MaxInputLengthChars { get; set; }
        public PipelineContext PipelineContext { get; set; }
        public int? ParseCheckpointTokenInterval { get; set; }
        public int? InterleavedTokenBatchSize { get; set; }
        public Action<HtmlParseCheckpoint> ParseCheckpointCallback { get; set; }
        public Action<Document, HtmlParseCheckpoint> ParseDocumentCheckpointCallback { get; set; }
    }

    public sealed class HtmlParseDocumentResult
    {
        public Document Document { get; set; } = new Document();
        public HtmlParsingOutcome Outcome { get; set; } = new HtmlParsingOutcome();
        public HtmlParseBuildMetrics Metrics { get; set; } = new HtmlParseBuildMetrics();
    }

    public class HtmlParser : IHtmlParser
    {
        private readonly string _html;

        private readonly Uri _baseUri;
        private readonly Network.ResourcePrefetcher _prefetcher;
        private readonly ParserSecurityPolicy _securityPolicy;
        public HtmlParsingOutcome LastParsingOutcome { get; private set; } = new HtmlParsingOutcome();

        public HtmlParser(string html, Uri baseUri = null, Network.ResourcePrefetcher prefetcher = null, ParserSecurityPolicy securityPolicy = null)
        {
            _html = html;
            _baseUri = baseUri ?? new Uri("about:blank");
            _prefetcher = prefetcher;
            _securityPolicy = securityPolicy?.Clone() ?? ParserSecurityPolicy.Default;
        }

        public Document Parse()
        {
            return Parse(_html);
        }

        public Document Parse(string html)
        {
            var options = new HtmlParserOptions
            {
                BaseUri = _baseUri,
                Prefetcher = _prefetcher,
                SecurityPolicy = _securityPolicy
            };

            var doc = ParseDocument(html, options, out var outcome);
            LastParsingOutcome = CloneOutcome(outcome);
            return doc;
        }

        public static Document ParseDocument(string html, Uri baseUri, HtmlParserOptions options = null)
        {
            options ??= new HtmlParserOptions();
            options.BaseUri = baseUri;
            return ParseDocument(html, options, out _);
        }

        public static Document ParseDocument(string html, HtmlParserOptions options = null)
        {
            return ParseDocument(html, options, out _);
        }

        public static Document ParseDocument(string html, out HtmlParsingOutcome outcome)
        {
            return ParseDocument(html, options: null, out outcome);
        }

        public static HtmlParseDocumentResult ParseDocumentDetailed(string html, HtmlParserOptions options = null)
        {
            var document = ParseDocumentInternal(html, options, out var outcome, out var metrics);
            return new HtmlParseDocumentResult
            {
                Document = document,
                Outcome = CloneOutcome(outcome),
                Metrics = CloneMetrics(metrics)
            };
        }

        public static Document ParseDocument(string html, HtmlParserOptions options, out HtmlParsingOutcome outcome)
        {
            return ParseDocumentInternal(html, options, out outcome, out _);
        }

        public static DocumentFragment ParseFragment(Element contextElement, string markup, HtmlParserOptions options = null)
        {
            return ParseFragment(contextElement, markup, options, out _);
        }

        public static DocumentFragment ParseFragment(Element contextElement, string markup, HtmlParserOptions options, out HtmlParsingOutcome outcome)
        {
            var effectiveOptions = CloneOptions(options);
            effectiveOptions ??= new HtmlParserOptions();

            if (effectiveOptions.BaseUri == null)
            {
                var contextOwnerDocument = contextElement?.OwnerDocument;
                var candidateBase = contextOwnerDocument?.BaseURI ?? contextOwnerDocument?.URL;
                if (Uri.TryCreate(candidateBase, UriKind.Absolute, out var parsedBase))
                {
                    effectiveOptions.BaseUri = parsedBase;
                }
            }

            var ownerDocument = contextElement?.OwnerDocument ?? Document.CreateHtmlDocument();
            var fragment = ownerDocument.CreateDocumentFragment();
            if (string.IsNullOrEmpty(markup))
            {
                outcome = new HtmlParsingOutcome { OutcomeClass = HtmlParsingOutcomeClass.Success, ReasonCode = HtmlParsingReasonCode.None };
                return fragment;
            }

            var contextTag = IsValidContextTagName(contextElement?.LocalName) ? contextElement.LocalName : "div";
            var marker = "__fen_fragment_root__";
            var wrapped = $"<{contextTag} data-fen-fragment-root=\"{marker}\">{markup}</{contextTag}>";
            var document = ParseDocument(wrapped, effectiveOptions, out outcome);
            var source = document.Descendants()
                .OfType<Element>()
                .FirstOrDefault(e => string.Equals(e.GetAttribute("data-fen-fragment-root"), marker, StringComparison.Ordinal));

            if (source != null)
            {
                source.RemoveAttribute("data-fen-fragment-root");
                while (source.FirstChild != null)
                {
                    fragment.AppendChild(source.FirstChild);
                }
                return fragment;
            }

            var fallback = document.Body ?? document.DocumentElement as ContainerNode ?? document;
            while (fallback.FirstChild != null)
            {
                fragment.AppendChild(fallback.FirstChild);
            }
            return fragment;
        }

        public static Document ParseStream(TextReader reader, HtmlParserOptions options = null)
        {
            return ParseStream(reader, options, out _);
        }

        public static Document ParseStream(TextReader reader, HtmlParserOptions options, out HtmlParsingOutcome outcome)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var html = reader.ReadToEnd();
            return ParseDocument(html, options, out outcome);
        }

        private static Document ParseDocumentInternal(string html, HtmlParserOptions options, out HtmlParsingOutcome outcome, out HtmlParseBuildMetrics metrics)
        {
            options ??= new HtmlParserOptions();
            var parseInput = html ?? string.Empty;
            var safeBaseUri = options.BaseUri ?? new Uri("about:blank");
            var policy = options.SecurityPolicy?.Clone() ?? ParserSecurityPolicy.Default;

            if (options.Prefetcher != null)
            {
                var scanner = new PreloadScanner(parseInput, safeBaseUri, options.Prefetcher);
                scanner.ScanAsync();
            }

            var builder = new HtmlTreeBuilder(parseInput)
            {
                MaxTokenizerEmissions = policy.HtmlMaxTokenEmissions,
                MaxOpenElementsDepth = policy.HtmlMaxOpenElementsDepth
            };

            if (options.MaxInputLengthChars.HasValue && options.MaxInputLengthChars.Value > 0)
            {
                builder.MaxInputLengthChars = options.MaxInputLengthChars.Value;
            }

            if (options.ParseCheckpointTokenInterval.HasValue)
            {
                builder.ParseCheckpointTokenInterval = Math.Max(0, options.ParseCheckpointTokenInterval.Value);
            }

            if (options.InterleavedTokenBatchSize.HasValue)
            {
                builder.InterleavedTokenBatchSize = Math.Max(0, options.InterleavedTokenBatchSize.Value);
            }

            if (options.ParseCheckpointCallback != null)
            {
                builder.ParseCheckpointCallback = options.ParseCheckpointCallback;
            }

            if (options.ParseDocumentCheckpointCallback != null)
            {
                builder.ParseDocumentCheckpointCallback = options.ParseDocumentCheckpointCallback;
            }

            var document = options.PipelineContext != null
                ? builder.BuildWithPipelineStages(options.PipelineContext)
                : builder.Build();

            document.URL = safeBaseUri.AbsoluteUri;
            document.BaseURI = safeBaseUri.AbsoluteUri;

            outcome = CloneOutcome(builder.LastParsingOutcome);
            metrics = CloneMetrics(builder.LastBuildMetrics);
            return document;
        }

        private static HtmlParserOptions CloneOptions(HtmlParserOptions options)
        {
            if (options == null)
            {
                return null;
            }

            return new HtmlParserOptions
            {
                BaseUri = options.BaseUri,
                Prefetcher = options.Prefetcher,
                SecurityPolicy = options.SecurityPolicy?.Clone(),
                MaxInputLengthChars = options.MaxInputLengthChars,
                PipelineContext = options.PipelineContext,
                ParseCheckpointTokenInterval = options.ParseCheckpointTokenInterval,
                InterleavedTokenBatchSize = options.InterleavedTokenBatchSize,
                ParseCheckpointCallback = options.ParseCheckpointCallback,
                ParseDocumentCheckpointCallback = options.ParseDocumentCheckpointCallback
            };
        }

        public static bool IsVoid(string tag)
        {
            // Void elements from HTML5 spec
            // area, base, br, col, embed, hr, img, input, link, meta, source, track, wbr
            if (string.IsNullOrEmpty(tag)) return false;
            var t = tag.ToLowerInvariant();
            return t == "area" || t == "base" || t == "br" || t == "col" || t == "embed" ||
                   t == "hr" || t == "img" || t == "input" || t == "link" || t == "meta" ||
                   t == "source" || t == "track" || t == "wbr";
        }

        private static bool IsValidContextTagName(string localName)
        {
            if (string.IsNullOrWhiteSpace(localName))
            {
                return false;
            }

            foreach (var c in localName)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == ':' || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static HtmlParsingOutcome CloneOutcome(HtmlParsingOutcome outcome)
        {
            if (outcome == null)
            {
                return new HtmlParsingOutcome
                {
                    OutcomeClass = HtmlParsingOutcomeClass.Success,
                    ReasonCode = HtmlParsingReasonCode.None
                };
            }

            return new HtmlParsingOutcome
            {
                OutcomeClass = outcome.OutcomeClass,
                ReasonCode = outcome.ReasonCode,
                Detail = outcome.Detail,
                IsRetryable = outcome.IsRetryable
            };
        }

        private static HtmlParseBuildMetrics CloneMetrics(HtmlParseBuildMetrics metrics)
        {
            if (metrics == null)
            {
                return new HtmlParseBuildMetrics();
            }

            return new HtmlParseBuildMetrics
            {
                TokenizingMs = metrics.TokenizingMs,
                ParsingMs = metrics.ParsingMs,
                TokenCount = metrics.TokenCount,
                TokenizingCheckpointCount = metrics.TokenizingCheckpointCount,
                ParsingCheckpointCount = metrics.ParsingCheckpointCount,
                DocumentReadyTokenCount = metrics.DocumentReadyTokenCount,
                UsedInterleavedBuild = metrics.UsedInterleavedBuild,
                InterleavedTokenBatchSize = metrics.InterleavedTokenBatchSize,
                InterleavedBatchCount = metrics.InterleavedBatchCount
            };
        }
    }
}

