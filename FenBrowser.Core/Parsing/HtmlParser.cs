using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Parsing
{
    public sealed class HtmlParserOptions
    {
        public Uri BaseUri { get; set; }
        public Network.ResourcePrefetcher Prefetcher { get; set; }
        public Security.CspPolicy ContentSecurityPolicy { get; set; }
        public ParserSecurityPolicy SecurityPolicy { get; set; }
        public int? MaxInputLengthChars { get; set; }
        public PipelineContext PipelineContext { get; set; }
        public int? ParseCheckpointTokenInterval { get; set; }
        public int? InterleavedTokenBatchSize { get; set; }
        public Action<HtmlParseCheckpoint> ParseCheckpointCallback { get; set; }
        public Action<Document, HtmlParseCheckpoint> ParseDocumentCheckpointCallback { get; set; }
        public bool ScriptingEnabled { get; set; } = true;
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

            if (contextElement == null)
            {
                throw new ArgumentNullException(nameof(contextElement));
            }

            var ownerDocument = contextElement.OwnerDocument ?? Document.CreateHtmlDocument();
            var fragment = ownerDocument.CreateDocumentFragment();
            if (string.IsNullOrEmpty(markup))
            {
                outcome = new HtmlParsingOutcome { OutcomeClass = HtmlParsingOutcomeClass.Success, ReasonCode = HtmlParsingReasonCode.None };
                return fragment;
            }

            var builder = new HtmlTreeBuilder(markup, contextElement);
            ConfigureBuilder(builder, effectiveOptions, effectiveOptions.SecurityPolicy?.Clone() ?? ParserSecurityPolicy.Default);
            var parsedFragment = builder.BuildFragment();
            outcome = CloneOutcome(builder.LastParsingOutcome);
            return parsedFragment;
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

            options ??= new HtmlParserOptions();
            var safeBaseUri = options.BaseUri ?? new Uri("about:blank");
            var policy = options.SecurityPolicy?.Clone() ?? ParserSecurityPolicy.Default;
            EmitHtmlParsingStarted(null, safeBaseUri);

            try
            {
                var builder = new HtmlTreeBuilder(reader);
                ConfigureBuilder(builder, options, policy);
                if (options.Prefetcher != null)
                {
                    var preloadObserver = new PreloadScanner(null, safeBaseUri, options.Prefetcher, options.ContentSecurityPolicy);
                    builder.StartTagObserved = preloadObserver.ObserveStartTag;
                }
                var document = options.PipelineContext != null
                    ? builder.BuildWithPipelineStages(options.PipelineContext)
                    : builder.Build();

                document.URL = safeBaseUri.AbsoluteUri;
                document.BaseURI = safeBaseUri.AbsoluteUri;
                outcome = CloneOutcome(builder.LastParsingOutcome);
                EmitHtmlParsingCompleted(safeBaseUri, outcome, builder.LastBuildMetrics);
                return document;
            }
            catch (Exception ex)
            {
                EmitHtmlParsingFailed(safeBaseUri, ex);
                throw;
            }
        }

        private static Document ParseDocumentInternal(string html, HtmlParserOptions options, out HtmlParsingOutcome outcome, out HtmlParseBuildMetrics metrics)
        {
            options ??= new HtmlParserOptions();
            var parseInput = html ?? string.Empty;
            var safeBaseUri = options.BaseUri ?? new Uri("about:blank");
            var policy = options.SecurityPolicy?.Clone() ?? ParserSecurityPolicy.Default;
            EmitHtmlParsingStarted(parseInput, safeBaseUri);

            try
            {
                var builder = new HtmlTreeBuilder(parseInput);
                ConfigureBuilder(builder, options, policy);
                if (options.Prefetcher != null)
                {
                    var preloadObserver = new PreloadScanner(null, safeBaseUri, options.Prefetcher, options.ContentSecurityPolicy);
                    builder.StartTagObserved = preloadObserver.ObserveStartTag;
                }

                var document = options.PipelineContext != null
                    ? builder.BuildWithPipelineStages(options.PipelineContext)
                    : builder.Build();

                document.URL = safeBaseUri.AbsoluteUri;
                document.BaseURI = safeBaseUri.AbsoluteUri;

                outcome = CloneOutcome(builder.LastParsingOutcome);
                metrics = CloneMetrics(builder.LastBuildMetrics);
                EmitHtmlParsingCompleted(safeBaseUri, outcome, metrics);
                return document;
            }
            catch (Exception ex)
            {
                EmitHtmlParsingFailed(safeBaseUri, ex);
                throw;
            }
        }

        private static void ConfigureBuilder(
            HtmlTreeBuilder builder,
            HtmlParserOptions options,
            ParserSecurityPolicy policy)
        {
            builder.MaxTokenizerEmissions = policy.HtmlMaxTokenEmissions;
            builder.MaxAttributesPerTag = policy.HtmlMaxAttributesPerElement;
            builder.MaxOpenElementsDepth = policy.HtmlMaxOpenElementsDepth;
            builder.ScriptingEnabled = options.ScriptingEnabled;

            if (options.MaxInputLengthChars is > 0)
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
        }

        private static void EmitHtmlParsingStarted(string html, Uri baseUri)
        {
            try
            {
                EngineLog.Write(
                    LogSubsystem.Html,
                    LogSeverity.Info,
                    "HTMLParsingStarted",
                    LogMarker.None,
                    new EngineLogContext(
                        NavigationId: LogContext.CurrentCorrelationId,
                        Url: baseUri?.AbsoluteUri),
                    new Dictionary<string, object>
                    {
                        ["event"] = "HTMLParsingStarted",
                        ["url"] = baseUri?.AbsoluteUri,
                        ["inputLength"] = html?.Length ?? 0
                    });
            }
            catch
            {
                // Parsing must not fail because diagnostics failed.
            }
        }

        private static void EmitHtmlParsingCompleted(Uri baseUri, HtmlParsingOutcome outcome, HtmlParseBuildMetrics metrics)
        {
            try
            {
                EngineLog.Write(
                    LogSubsystem.Html,
                    LogSeverity.Info,
                    "HTMLParsingCompleted",
                    LogMarker.None,
                    new EngineLogContext(
                        NavigationId: LogContext.CurrentCorrelationId,
                        Url: baseUri?.AbsoluteUri),
                    new Dictionary<string, object>
                    {
                        ["event"] = "HTMLParsingCompleted",
                        ["url"] = baseUri?.AbsoluteUri,
                        ["outcomeClass"] = outcome?.OutcomeClass.ToString(),
                        ["reasonCode"] = outcome?.ReasonCode.ToString(),
                        ["tokenCount"] = Math.Max(0, metrics?.TokenCount ?? 0),
                        ["tokenizingMs"] = Math.Max(0, metrics?.TokenizingMs ?? 0),
                        ["parsingMs"] = Math.Max(0, metrics?.ParsingMs ?? 0),
                        ["documentReadyToken"] = Math.Max(0, metrics?.DocumentReadyTokenCount ?? 0)
                    });
            }
            catch
            {
                // Parsing must not fail because diagnostics failed.
            }
        }

        private static void EmitHtmlParsingFailed(Uri baseUri, Exception exception)
        {
            try
            {
                EngineLog.Write(
                    LogSubsystem.Html,
                    LogSeverity.Error,
                    "HTMLParsingFailed",
                    LogMarker.Unexpected,
                    new EngineLogContext(
                        NavigationId: LogContext.CurrentCorrelationId,
                        Url: baseUri?.AbsoluteUri),
                    new Dictionary<string, object>
                    {
                        ["event"] = "HTMLParsingFailed",
                        ["url"] = baseUri?.AbsoluteUri,
                        ["exception"] = exception.ToString()
                    });
            }
            catch
            {
                // Preserve original parser failure.
            }
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
                ParseDocumentCheckpointCallback = options.ParseDocumentCheckpointCallback,
                ScriptingEnabled = options.ScriptingEnabled
            };
        }

        public static bool IsVoid(string tag)
        {
            return HtmlElementSemantics.IsVoid(tag);
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
