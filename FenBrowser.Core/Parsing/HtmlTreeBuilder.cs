using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace FenBrowser.Core.Parsing
{
    public sealed class HtmlParseBuildMetrics
    {
        public long TokenizingMs { get; set; }
        public long ParsingMs { get; set; }
        public int TokenCount { get; set; }
        public int TokenizingCheckpointCount { get; set; }
        public int ParsingCheckpointCount { get; set; }
        public int DocumentReadyTokenCount { get; set; }
        public bool UsedInterleavedBuild { get; set; }
        public int InterleavedTokenBatchSize { get; set; }
        public int InterleavedBatchCount { get; set; }
    }

    public enum HtmlParseBuildPhase
    {
        Tokenizing,
        Parsing
    }

    public sealed class HtmlParseCheckpoint
    {
        public HtmlParseCheckpoint(HtmlParseBuildPhase phase, int processedTokenCount, bool isFinal)
        {
            Phase = phase;
            ProcessedTokenCount = processedTokenCount;
            IsFinal = isFinal;
        }

        public HtmlParseBuildPhase Phase { get; }
        public int ProcessedTokenCount { get; }
        public bool IsFinal { get; }
    }

    /// <summary>
    /// A production-grade HTML5 Tree Builder.
    /// Implements insertion modes and tree construction rules.
    /// https://html.spec.whatwg.org/multipage/parsing.html#tree-construction
    /// </summary>
    public class HtmlTreeBuilder
    {
        private readonly HtmlTokenPool _pool;
        private readonly HtmlTokenizer _tokenizer;
        private readonly Document _document;
        private readonly DocumentFragment _fragment;
        private readonly Element _fragmentRoot;
        
        // Stack of Open Elements
        private readonly Stack<Element> _openElements = new Stack<Element>();
        
        // List of Active Formatting Elements (for Adoption Agency Algorithm)
        private readonly List<Element> _activeFormattingElements = new List<Element>();
        // Buffered table-interstitial character data. Must hold copied text, not
        // CharacterToken references: pooled tokens are recycled by the tokenizer's
        // ring buffer and would be silently overwritten before flush.
        private readonly StringBuilder _pendingTableText = new StringBuilder();
        
        // Current insertion mode
        private InsertionMode _insertionMode = InsertionMode.Initial;
        private InsertionMode _originalInsertionMode; // For "In Text" etc
        private bool _atStartOfInput = true;
        
        // Pointers
        private Element _headElement;
        private Element _formElement;
        
        public HtmlParseBuildMetrics LastBuildMetrics { get; private set; } = new HtmlParseBuildMetrics();
        public int ParseCheckpointTokenInterval { get; set; } = 256;
        public int InterleavedTokenBatchSize { get; set; }
        public Action<HtmlParseCheckpoint> ParseCheckpointCallback { get; set; }
        public Action<Document, HtmlParseCheckpoint> ParseDocumentCheckpointCallback { get; set; }
        public Action<StartTagToken> StartTagObserved { get; set; }
        public int MaxTokenizerEmissions { get; set; } = 2_000_000;
        public int MaxInputLengthChars { get; set; } = 8_000_000;
        public int MaxAttributesPerTag { get; set; } = 4096;
        public int MaxOpenElementsDepth { get; set; } = 4096;
        public bool ScriptingEnabled { get; set; } = true;
        public HtmlParsingOutcome LastParsingOutcome { get; private set; } = new HtmlParsingOutcome();
        private bool _openElementsDepthLimitLogged;
        private bool _openElementUnderflowLogged;
        private bool _fosterParenting;
        private bool _deferParsingCheckpointCallbacks;
        private readonly List<HtmlParseCheckpoint> _deferredParsingCheckpointCallbacks = new List<HtmlParseCheckpoint>();

        public HtmlTreeBuilder(string html)
        {
            _pool = new HtmlTokenPool();
            _tokenizer = new HtmlTokenizer(html, _pool);
            _document = new Document();
            // stack is initially empty? No, usually Document is root? 
            // Spec says stack of open elements is initially empty.
            // But usually we append to Document.
            // Actually, "Process Initial" handles this.
        }

        public HtmlTreeBuilder(TextReader reader)
        {
            _pool = new HtmlTokenPool();
            _tokenizer = new HtmlTokenizer(reader, _pool);
            _document = new Document();
        }

        public HtmlTreeBuilder(string html, Element contextElement)
        {
            if (contextElement == null)
                throw new ArgumentNullException(nameof(contextElement));

            _pool = new HtmlTokenPool();
            _tokenizer = new HtmlTokenizer(html, _pool);
            _document = contextElement.OwnerDocument ?? Document.CreateHtmlDocument();
            _fragment = _document.CreateDocumentFragment();
            _fragmentRoot = _document.CreateElementNS(
                contextElement.NamespaceUri ?? Namespaces.Html,
                contextElement.LocalName ?? "div");
            _fragment.AppendChild(_fragmentRoot);
            _openElements.Push(_fragmentRoot);
            ConfigureFragmentContext(contextElement);
        }

        public Document Build()
        {
            return BuildInternal(null);
        }

        public Document BuildWithPipelineStages(PipelineContext pipelineContext)
        {
            if (pipelineContext == null)
            {
                throw new ArgumentNullException(nameof(pipelineContext));
            }

            return BuildInternal(pipelineContext);
        }

        public DocumentFragment BuildFragment()
        {
            if (_fragment == null || _fragmentRoot == null)
                throw new InvalidOperationException("This tree builder was not created for fragment parsing.");

            BuildInternal(null);
            var parsedContents = _fragmentRoot is HtmlTemplateElement templateRoot
                ? (ContainerNode)templateRoot.Content
                : _fragmentRoot;
            while (parsedContents.FirstChild != null)
            {
                _fragment.AppendChild(parsedContents.FirstChild);
            }
            _fragment.RemoveChild(_fragmentRoot);
            return _fragment;
        }

        private Document BuildInternal(PipelineContext pipelineContext)
        {
            long tokenizingTicks = 0;
            long parsingTicks = 0;
            var tokenCount = 0;
            var tokenizingCheckpointCount = 0;
            var parsingCheckpointCount = 0;
            var documentReadyTokenCount = 0;
            var parsedTokenCount = 0;
            var interleavedBatchCount = 0;
            var tokenBatchSize = Math.Max(0, InterleavedTokenBatchSize);
            var useInterleavedBuild = tokenBatchSize > 0;
            var tokenBuffer = new List<HtmlToken>(useInterleavedBuild ? tokenBatchSize : 256);
            _deferParsingCheckpointCallbacks = !useInterleavedBuild;
            _deferredParsingCheckpointCallbacks.Clear();
            IDisposable frameScope = null;
            IDisposable tokenizingStageScope = null;
            IDisposable parsingStageScope = null;

            try
            {
                ApplyResilienceSettingsFromBrowserDefaults();
                LastParsingOutcome = new HtmlParsingOutcome
                {
                    OutcomeClass = HtmlParsingOutcomeClass.Success,
                    ReasonCode = HtmlParsingReasonCode.None
                };

                _tokenizer.MaxTokenEmissions = MaxTokenizerEmissions;
                _tokenizer.MaxInputLengthChars = MaxInputLengthChars;
                _tokenizer.MaxAttributesPerTag = MaxAttributesPerTag;

                if (pipelineContext != null)
                {
                    frameScope = pipelineContext.BeginScopedFrame();
                    tokenizingStageScope = pipelineContext.BeginScopedStage(PipelineStage.Tokenizing);
                }

                using (var enumerator = _tokenizer.Tokenize().GetEnumerator())
                {
                    while (true)
                    {
                        var tokenizeStart = Stopwatch.GetTimestamp();
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        tokenizingTicks += Stopwatch.GetTimestamp() - tokenizeStart;
                        var token = enumerator.Current;
                        tokenBuffer.Add(token);
                        tokenCount++;

                        if (ShouldEmitCheckpoint(tokenCount))
                        {
                            tokenizingCheckpointCount++;
                            EmitCheckpoint(HtmlParseBuildPhase.Tokenizing, tokenCount, isFinal: false);
                        }

                        if (useInterleavedBuild &&
                            (tokenBuffer.Count >= tokenBatchSize || RequiresImmediateTreeBuilderFeedback(token)))
                        {
                            tokenizingStageScope?.Dispose();
                            tokenizingStageScope = null;

                            if (pipelineContext != null)
                            {
                                parsingStageScope = pipelineContext.BeginScopedStage(PipelineStage.Parsing);
                            }

                            ProcessTokenBatch(
                                tokenBuffer,
                                ref parsingTicks,
                                ref parsedTokenCount,
                                ref parsingCheckpointCount,
                                ref documentReadyTokenCount);
                            interleavedBatchCount++;
                            tokenBuffer.Clear();

                            parsingStageScope?.Dispose();
                            parsingStageScope = null;

                            if (pipelineContext != null)
                            {
                                tokenizingStageScope = pipelineContext.BeginScopedStage(PipelineStage.Tokenizing);
                            }
                        }
                        else if (!useInterleavedBuild)
                        {
                            // The tokenizer depends on tree-builder feedback to enter RAWTEXT/RCDATA/ScriptData.
                            // Process tokens immediately in the non-interleaved path so inline scripts/styles
                            // cannot be tokenized ahead while the tokenizer is still in Data state.
                            if (pipelineContext != null)
                            {
                                tokenizingStageScope?.Dispose();
                                tokenizingStageScope = null;
                                parsingStageScope = pipelineContext.BeginScopedStage(PipelineStage.Parsing);
                            }

                            ProcessTokenBatch(
                                tokenBuffer,
                                ref parsingTicks,
                                ref parsedTokenCount,
                                ref parsingCheckpointCount,
                                ref documentReadyTokenCount);
                            tokenBuffer.Clear();

                            parsingStageScope?.Dispose();
                            parsingStageScope = null;

                            if (pipelineContext != null)
                            {
                                tokenizingStageScope = pipelineContext.BeginScopedStage(PipelineStage.Tokenizing);
                            }
                        }
                    }
                }

                tokenizingCheckpointCount++;
                EmitCheckpoint(HtmlParseBuildPhase.Tokenizing, tokenCount, isFinal: true);
                if (_deferParsingCheckpointCallbacks && _deferredParsingCheckpointCallbacks.Count > 0)
                {
                    foreach (var checkpoint in _deferredParsingCheckpointCallbacks)
                    {
                        EmitCheckpoint(checkpoint);
                    }

                    _deferredParsingCheckpointCallbacks.Clear();
                }
                tokenizingStageScope?.Dispose();
                tokenizingStageScope = null;

                if (pipelineContext != null)
                {
                    parsingStageScope = pipelineContext.BeginScopedStage(PipelineStage.Parsing);
                }

                if (tokenBuffer.Count > 0)
                {
                    ProcessTokenBatch(
                        tokenBuffer,
                        ref parsingTicks,
                        ref parsedTokenCount,
                        ref parsingCheckpointCount,
                        ref documentReadyTokenCount);
                    if (useInterleavedBuild)
                    {
                        interleavedBatchCount++;
                    }
                }

                parsingCheckpointCount++;
                var finalParsingCheckpoint = new HtmlParseCheckpoint(HtmlParseBuildPhase.Parsing, parsedTokenCount, isFinal: true);
                EmitCheckpoint(finalParsingCheckpoint);
                EmitDocumentCheckpoint(finalParsingCheckpoint);

                LastBuildMetrics = new HtmlParseBuildMetrics
                {
                    TokenizingMs = TicksToMilliseconds(tokenizingTicks),
                    ParsingMs = TicksToMilliseconds(parsingTicks),
                    TokenCount = tokenCount,
                    TokenizingCheckpointCount = tokenizingCheckpointCount,
                    ParsingCheckpointCount = parsingCheckpointCount,
                    DocumentReadyTokenCount = documentReadyTokenCount,
                    UsedInterleavedBuild = useInterleavedBuild,
                    InterleavedTokenBatchSize = tokenBatchSize,
                    InterleavedBatchCount = interleavedBatchCount
                };

                if (_tokenizer.LastReasonCode != HtmlParsingReasonCode.None)
                {
                    LastParsingOutcome = new HtmlParsingOutcome
                    {
                        OutcomeClass = HtmlParsingOutcomeClass.Degraded,
                        ReasonCode = _tokenizer.LastReasonCode,
                        Detail = _tokenizer.LastReasonDetail,
                        IsRetryable = _tokenizer.LastReasonCode == HtmlParsingReasonCode.TokenEmissionLimitExceeded
                    };
                }
                else if (_openElementsDepthLimitLogged)
                {
                    LastParsingOutcome = new HtmlParsingOutcome
                    {
                        OutcomeClass = HtmlParsingOutcomeClass.Degraded,
                        ReasonCode = HtmlParsingReasonCode.OpenElementsDepthLimitExceeded,
                        Detail = $"Open elements depth exceeded configured limit {MaxOpenElementsDepth}.",
                        IsRetryable = true
                    };
                }

                LastParsingOutcome.TokenizerErrors = _tokenizer.Errors.ToArray();
                LastParsingOutcome.TokenizerErrorCount = _tokenizer.ErrorCount;
                LastParsingOutcome.TokenizerErrorsTruncated = _tokenizer.ErrorsTruncated;

                return _document;
            }
            catch (Exception ex)
            {
                LastParsingOutcome = new HtmlParsingOutcome
                {
                    OutcomeClass = HtmlParsingOutcomeClass.Failed,
                    ReasonCode = HtmlParsingReasonCode.Exception,
                    Detail = ex.Message,
                    IsRetryable = false
                };
                throw;
            }
            finally
            {
                _pool?.ResetAll();
                parsingStageScope?.Dispose();
                tokenizingStageScope?.Dispose();
                frameScope?.Dispose();
                _deferParsingCheckpointCallbacks = false;
                _deferredParsingCheckpointCallbacks.Clear();
            }
        }

        private void ApplyResilienceSettingsFromBrowserDefaults()
        {
            var resilience = BrowserSettings.Instance?.Resilience;
            if (resilience == null)
            {
                return;
            }

            if (MaxTokenizerEmissions <= 0 || MaxTokenizerEmissions == 2_000_000)
            {
                MaxTokenizerEmissions = resilience.MaxHtmlTokenEmissions;
            }

            if (MaxInputLengthChars <= 0 || MaxInputLengthChars == 8_000_000)
            {
                MaxInputLengthChars = resilience.MaxHtmlInputChars;
            }

            if (MaxOpenElementsDepth <= 0 || MaxOpenElementsDepth == 4096)
            {
                MaxOpenElementsDepth = resilience.MaxOpenElementsDepth;
            }

            if (MaxAttributesPerTag <= 0 || MaxAttributesPerTag == 4096)
            {
                MaxAttributesPerTag = resilience.MaxHtmlAttributesPerElement;
            }
        }
        private void ProcessTokenBatch(
            List<HtmlToken> tokenBuffer,
            ref long parsingTicks,
            ref int parsedTokenCount,
            ref int parsingCheckpointCount,
            ref int documentReadyTokenCount)
        {
            if (tokenBuffer == null || tokenBuffer.Count == 0)
            {
                return;
            }

            foreach (var token in tokenBuffer)
            {
                var parseStart = Stopwatch.GetTimestamp();
                if (token is StartTagToken startTag)
                {
                    EmitStartTagObserved(startTag);
                }
                ProcessToken(token);
                EnforceOpenElementDepthLimit();
                parsingTicks += Stopwatch.GetTimestamp() - parseStart;
                parsedTokenCount++;
                if (documentReadyTokenCount == 0 && _document.DocumentElement != null)
                {
                    documentReadyTokenCount = parsedTokenCount;
                }

                if (ShouldEmitCheckpoint(parsedTokenCount))
                {
                    parsingCheckpointCount++;
                    var checkpoint = new HtmlParseCheckpoint(HtmlParseBuildPhase.Parsing, parsedTokenCount, isFinal: false);
                    if (_deferParsingCheckpointCallbacks)
                    {
                        _deferredParsingCheckpointCallbacks.Add(checkpoint);
                    }
                    else
                    {
                        EmitCheckpoint(checkpoint);
                    }
                    EmitDocumentCheckpoint(checkpoint);
                }
            }
        }

        private bool ShouldEmitCheckpoint(int processedTokenCount)
        {
            return ParseCheckpointTokenInterval > 0 &&
                processedTokenCount > 0 &&
                (processedTokenCount % ParseCheckpointTokenInterval) == 0;
        }

        private void EmitStartTagObserved(StartTagToken startTag)
        {
            var callback = StartTagObserved;
            if (callback == null)
                return;

            try
            {
                callback(startTag);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug(
                    $"[HTML] Preload token observer failed (type={ex.GetType().Name}).",
                    LogCategory.HtmlParsing);
            }
        }

        private void ConfigureFragmentContext(Element contextElement)
        {
            var name = contextElement.LocalName?.ToLowerInvariant() ?? "div";
            if (string.Equals(name, "template", StringComparison.OrdinalIgnoreCase))
            {
                // Fragment parsing: a template context seeds the template insertion mode stack
                // so ResetInsertionMode lands in InTemplate above it.
                _templateInsertionModes.Push(InsertionMode.InTemplate);
            }
            _tokenizer.LastStartTagName = name;
            _tokenizer.SetState(!string.Equals(contextElement.NamespaceUri, Namespaces.Html, StringComparison.Ordinal)
                ? HtmlTokenizer.TokenizerState.Data
                : name switch
            {
                "title" or "textarea" => HtmlTokenizer.TokenizerState.RcData,
                "style" or "xmp" or "iframe" or "noembed" or "noframes" => HtmlTokenizer.TokenizerState.RawText,
                "script" => HtmlTokenizer.TokenizerState.ScriptData,
                "plaintext" => HtmlTokenizer.TokenizerState.PlainText,
                _ => HtmlTokenizer.TokenizerState.Data
            });
            ResetInsertionMode();
        }

        private static bool RequiresImmediateTreeBuilderFeedback(HtmlToken token)
        {
            if (token is not StartTagToken startTag || string.IsNullOrWhiteSpace(startTag.TagName))
            {
                return false;
            }

            return startTag.TagName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("style", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("xmp", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("iframe", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("noembed", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("noframes", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("noscript", StringComparison.OrdinalIgnoreCase) ||
                startTag.TagName.Equals("plaintext", StringComparison.OrdinalIgnoreCase);
        }

        private void EmitCheckpoint(HtmlParseBuildPhase phase, int processedTokenCount, bool isFinal)
        {
            EmitCheckpoint(new HtmlParseCheckpoint(phase, processedTokenCount, isFinal));
        }

        private void EmitCheckpoint(HtmlParseCheckpoint checkpoint)
        {
            var callback = ParseCheckpointCallback;
            if (callback == null)
            {
                return;
            }

            try
            {
                callback(checkpoint);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[HTML] Parse checkpoint callback error: {ex.Message}", LogCategory.HtmlParsing);
            }
        }

        private void EmitDocumentCheckpoint(HtmlParseCheckpoint checkpoint)
        {
            var callback = ParseDocumentCheckpointCallback;
            if (callback == null)
            {
                return;
            }

            try
            {
                callback(_document, checkpoint);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[HTML] Parse document checkpoint callback error: {ex.Message}", LogCategory.HtmlParsing);
            }
        }

        private static long TicksToMilliseconds(long ticks)
        {
            if (ticks <= 0)
            {
                return 0;
            }

            return (long)((ticks * 1000.0) / Stopwatch.Frequency);
        }

        private enum InsertionMode
        {
            Initial,
            BeforeHtml,
            BeforeHead,
            InHead,
            InHeadNoscript,
            AfterHead,
            InBody,
            Text,
            InTable,
            InTableText,
            InCaption,
            InColumnGroup,
            InTableBody,
            InRow,
            InCell,
            InSelect,
            InSelectInTable,
            InTemplate,
            AfterBody,
            InFrameset,
            AfterFrameset,
            AfterAfterBody,
            AfterAfterFrameset
        }

        // Template Insertion Mode Stack
        private readonly Stack<InsertionMode> _templateInsertionModes = new Stack<InsertionMode>();

        private void ProcessToken(HtmlToken token)
        {
            // Simplified dispatch based on mode
            bool processed = false;

            // Loop for re-processing tokens (mode switching without consuming)
            while (!processed)
            {
                if (ShouldProcessUsingForeignContent(token))
                {
                    processed = HandleForeignContent(token);
                    if (processed)
                    {
                        continue;
                    }
                }

                switch (_insertionMode)
                {
                    case InsertionMode.Initial:
                        processed = HandleInitial(token);
                        break;
                    case InsertionMode.BeforeHtml:
                        processed = HandleBeforeHtml(token);
                        break;
                    case InsertionMode.BeforeHead:
                        processed = HandleBeforeHead(token);
                        break;
                    case InsertionMode.InHead:
                        processed = HandleInHead(token);
                        break;
                    case InsertionMode.InHeadNoscript:
                         processed = HandleInHeadNoscript(token);
                         break;
                    case InsertionMode.AfterHead:
                        processed = HandleAfterHead(token);
                        break;
                    case InsertionMode.InBody:
                        processed = HandleInBody(token);
                        break;
                    case InsertionMode.Text:
                        processed = HandleText(token);
                        break;
                    case InsertionMode.InTable:
                        processed = HandleInTable(token);
                        break;
                    case InsertionMode.InTableText:
                        processed = HandleInTableText(token);
                        break;
                    case InsertionMode.InTableBody:
                        processed = HandleInTableBody(token);
                        break;
                    case InsertionMode.InRow:
                        processed = HandleInRow(token);
                        break;
                    case InsertionMode.InCell:
                        processed = HandleInCell(token);
                        break;
                    case InsertionMode.InSelect:
                        processed = HandleInSelect(token);
                        break;
                    case InsertionMode.InSelectInTable:
                        processed = HandleInSelectInTable(token);
                        break;
                    case InsertionMode.InCaption:
                        processed = HandleInCaption(token);
                        break;
                    case InsertionMode.InColumnGroup:
                        processed = HandleInColumnGroup(token);
                        break;
                    case InsertionMode.InTemplate:
                        processed = HandleInTemplate(token);
                        break;
                    case InsertionMode.AfterBody:
                        processed = HandleAfterBody(token);
                        break;
                    case InsertionMode.InFrameset:
                        processed = HandleInFrameset(token);
                        break;
                    case InsertionMode.AfterFrameset:
                        processed = HandleAfterFrameset(token);
                        break;
                    case InsertionMode.AfterAfterBody:
                        processed = HandleAfterAfterBody(token);
                        break;
                    case InsertionMode.AfterAfterFrameset:
                        processed = HandleAfterAfterFrameset(token);
                        break;
                    default:
                        if (DebugConfig.LogHtmlParse)
                            FenBrowser.Core.EngineLogCompat.Warn($"[HTML] Unhandled Mode: {_insertionMode} for token {token.Type}", LogCategory.HtmlParsing);
                        processed = HandleInBody(token); // Fallback
                        break;
                }
            }
        }
        
        // --- Insertion Mode Handlers ---
        
        private bool HandleInitial(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                if (_atStartOfInput)
                {
                    _atStartOfInput = false;
                    if (_fragment == null &&
                        !string.IsNullOrEmpty(ct.Data) &&
                        ct.Data[0] == '\uFEFF')
                    {
                        ct.Data = ct.Data.Substring(1);
                        if (ct.Data.Length == 0)
                            return true;
                    }
                }

                if (string.IsNullOrWhiteSpace(ct.Data))
                    return true; // Ignore whitespace
            }
            else
            {
                _atStartOfInput = false;
            }
            
            if (token is CommentToken comment)
            {
                _document.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is DoctypeToken dt)
            {
                // Emit DOCTYPE
                var doctypeNode = new DocumentType(dt.Name, dt.PublicIdentifier, dt.SystemIdentifier);
                _document.AppendChild(doctypeNode);
                
                _document.Mode = DetermineQuirksMode(dt);
                SwitchTo(InsertionMode.BeforeHtml);
                return true;
            }
            
            // Anything else?
            // "If the document is not an iframe srcdoc document..." -> Parse error, set quarks mode.
            // Switch to BeforeHtml and Reprocess
            _document.Mode = QuirksMode.Quirks;
            SwitchTo(InsertionMode.BeforeHtml);
            return false; // Reprocess
        }

        private bool HandleInFrameset(HtmlToken token)
        {
            if (token is CharacterToken characters && string.IsNullOrWhiteSpace(characters.Data))
            {
                InsertCharacter(characters);
                return true;
            }

            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken)
            {
                return true;
            }

            if (token is StartTagToken startTag)
            {
                var name = startTag.TagName?.ToLowerInvariant();
                if (name == "html")
                {
                    return HandleInBody(token);
                }

                if (name == "frameset")
                {
                    InsertHtmlElement(startTag);
                    return true;
                }

                if (name == "frame")
                {
                    InsertHtmlElement(startTag);
                    SafePopOpenElement();
                    return true;
                }

                if (name == "noframes")
                {
                    return HandleInHead(token);
                }

                return true;
            }

            if (token is EndTagToken endTag &&
                string.Equals(endTag.TagName, "frameset", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(CurrentTag, "html", StringComparison.OrdinalIgnoreCase))
                {
                    SafePopOpenElement();
                }

                if (!string.Equals(CurrentTag, "frameset", StringComparison.OrdinalIgnoreCase))
                {
                    SwitchTo(InsertionMode.AfterFrameset);
                }
                return true;
            }

            return true;
        }

        private bool HandleAfterFrameset(HtmlToken token)
        {
            if (token is CharacterToken characters && string.IsNullOrWhiteSpace(characters.Data))
            {
                InsertCharacter(characters);
                return true;
            }

            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken)
            {
                return true;
            }

            if (token is StartTagToken startTag)
            {
                if (string.Equals(startTag.TagName, "html", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleInBody(token);
                }

                if (string.Equals(startTag.TagName, "noframes", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleInHead(token);
                }

                return true;
            }

            if (token is EndTagToken endTag &&
                string.Equals(endTag.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                SwitchTo(InsertionMode.AfterAfterFrameset);
                return true;
            }

            return true;
        }

        private bool HandleAfterAfterFrameset(HtmlToken token)
        {
            if (token is CommentToken comment)
            {
                _document.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken ||
                token is CharacterToken characters && string.IsNullOrWhiteSpace(characters.Data) ||
                token is StartTagToken htmlStart && string.Equals(htmlStart.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                return HandleInBody(token);
            }

            if (token is StartTagToken startTag &&
                string.Equals(startTag.TagName, "noframes", StringComparison.OrdinalIgnoreCase))
            {
                return HandleInHead(token);
            }

            return true;
        }

        private bool HandleInTemplate(HtmlToken token)
        {
            if (token is CharacterToken || token is CommentToken || token is DoctypeToken)
            {
                return HandleInBody(token);
            }
            
            if (token is StartTagToken st)
            {
                if (st.TagName == "base" || st.TagName == "basefont" || st.TagName == "bgsound" || st.TagName == "link" || st.TagName == "meta" || st.TagName == "noframes" || st.TagName == "script" || st.TagName == "style" || st.TagName == "template" || st.TagName == "title")
                {
                    return HandleInHead(token);
                }
                
                if (st.TagName == "caption" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead")
                {
                    SetCurrentTemplateInsertionMode(InsertionMode.InTable);
                    SwitchTo(InsertionMode.InTable);
                    return false; // Reprocess
                }

                if (st.TagName == "col")
                {
                    SetCurrentTemplateInsertionMode(InsertionMode.InColumnGroup);
                    SwitchTo(InsertionMode.InColumnGroup);
                    return false; // Reprocess
                }

                if (st.TagName == "tr")
                {
                    SetCurrentTemplateInsertionMode(InsertionMode.InTableBody);
                    SwitchTo(InsertionMode.InTableBody);
                    return false; // Reprocess
                }

                if (st.TagName == "td" || st.TagName == "th")
                {
                    SetCurrentTemplateInsertionMode(InsertionMode.InRow);
                    SwitchTo(InsertionMode.InRow);
                    return false; // Reprocess
                }

                SetCurrentTemplateInsertionMode(InsertionMode.InBody);
                SwitchTo(InsertionMode.InBody);
                return false; // Reprocess
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "template")
                {
                    return CloseTemplateElement();
                }
            }
            
            if (token is EofToken)
            {
                if (!InScope("template", new[] { "html" }))
                {
                    // Stop parsing
                     return true; 
                }
                // Error
                PopUntil("template");
                ClearActiveFormattingElementsMarker();
                if (_templateInsertionModes.Count > 0) _templateInsertionModes.Pop();
                ResetInsertionMode();
                return false; // Reprocess
            }
            
            return HandleInBody(token);
        }

        private bool HandleInHeadNoscript(HtmlToken token)
        {
             if (token is EndTagToken et && string.Equals(et.TagName, "noscript", StringComparison.OrdinalIgnoreCase))
             {
                 SafePopOpenElement();
                 SwitchTo(InsertionMode.InHead);
                 return true;
             }
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 return HandleInHead(token);
             }
             if (token is StartTagToken st)
             {
                 var tagLower = st.TagName?.ToLowerInvariant() ?? string.Empty;
                 if (tagLower == "basefont" || tagLower == "bgsound" || tagLower == "link" ||
                     tagLower == "meta" || tagLower == "noframes" || tagLower == "style")
                 {
                     return HandleInHead(token);
                 }
             }
             // Anything else -> Error, pop noscript, reprocess
             SafePopOpenElement();
             SwitchTo(InsertionMode.InHead);
             return false;
        }
        
        private bool HandleBeforeHtml(HtmlToken token)
        {
             if (token is DoctypeToken)
            {
                // Ignore (Parse error)
                return true;
            }
            
            if (token is CommentToken comment)
            {
                _document.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
                return true; // Ignore
                
            if (token is StartTagToken st && string.Equals(st.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                var html = CreateElement(st);
                _document.AppendChild(html);
                _openElements.Push(html);
                SwitchTo(InsertionMode.BeforeHead);
                return true;
            }
            
            // Anything else? Create <html> and reprocess
            var artificialHtml = new Element("html");
            _document.AppendChild(artificialHtml);
            _openElements.Push(artificialHtml);
            SwitchTo(InsertionMode.BeforeHead);
            return false; // Reprocess
        }
        
        private bool HandleBeforeHead(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
                return true; // Ignore
                
            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is DoctypeToken) return true; // Ignore
            
            if (token is StartTagToken st && string.Equals(st.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                return HandleInBody(token); // Process "in body" rules for html tag? Spec says: "Process the token using rules for In Body"
            }
            
            if (token is StartTagToken headTag && string.Equals(headTag.TagName, "head", StringComparison.OrdinalIgnoreCase))
            {
                var head = InsertHtmlElement(headTag);
                _headElement = head;
                SwitchTo(InsertionMode.InHead);
                return true;
            }
            
            // Anything else? Create <head> and reprocess
             var artificialHead = InsertHtmlElement(new StartTagToken() { TagName = "head" });
            _headElement = artificialHead;
            SwitchTo(InsertionMode.InHead);
            return false;
        }
        
        private bool HandleInHead(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 InsertCharacter(ct); // Valid in head if whitespace
                 return true;
             }
             
             if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is StartTagToken st)
            {
                if (string.Equals(st.TagName, "html", StringComparison.OrdinalIgnoreCase)) return HandleInBody(token);
                
                // FIX: Use case-insensitive comparison for tag names
                var tagLower = st.TagName?.ToLowerInvariant() ?? "";
                if (tagLower == "base" || tagLower == "basefont" || tagLower == "bgsound" || tagLower == "link")
                {
                    // DEBUG: Log LINK token attributes
                    if (tagLower == "link")
                    {
                        /* [PERF-REMOVED] */
                        if (st.HasAttributes)
                        {
                            // Debug logging removed for performance
                        }
                    }
                    InsertHtmlElement(st);
                    SafePopOpenElement(); // Immediately pop (void elements)
                    return true;
                }
                
                if (tagLower == "meta")
                {
                   InsertHtmlElement(st);
                   SafePopOpenElement();
                   ApplyMetaCharset(st);
                   return true;
                }
                
                if (tagLower == "title")
                {
                    InsertGenericRCDATAElement(st);
                    return true;
                }
                
                // NOSCRIPT, NOFRAMES, STYLE -> "Generic Raw Text Element"
                if (tagLower == "style" || tagLower == "noframes") // NoFrames is rawtext?
                {
                     InsertGenericRawTextElement(st);
                     return true;
                }
                 if (tagLower == "noscript")
                {
                    if (ScriptingEnabled)
                    {
                        InsertGenericRawTextElement(st);
                    }
                    else
                    {
                        InsertHtmlElement(st);
                        SwitchTo(InsertionMode.InHeadNoscript);
                    }
                    return true;
                }
                
                if (tagLower == "script")
                {
                    // Complex script handling
                     var script = InsertHtmlElement(st);
                     _tokenizer.SetState(HtmlTokenizer.TokenizerState.ScriptData); // Wait, tokenizer state is internal? 
                     // We need to access Tokenizer to change state.
                     _originalInsertionMode = _insertionMode;
                     // Switch to Text mode logic in TreeBuilder? 
                     // Spec says: switch tokenizer to script data state.
                     SwitchTo(InsertionMode.Text); 
                     
                     // We store the last start tag name for the tokenizer to use
                     _tokenizer.LastStartTagName = "script";
                     return true;
                }

                if (tagLower == "template")
                {
                    InsertHtmlElement(st);
                    _activeFormattingElements.Add(null); // Marker
                    _templateInsertionModes.Push(InsertionMode.InTemplate);
                    SwitchTo(InsertionMode.InTemplate);
                    return true;
                }
                
                if (tagLower == "head") return true; // Ignore
            }
            
            if (token is EndTagToken et)
            {
                var endTagLower = et.TagName?.ToLowerInvariant() ?? string.Empty;

                if (endTagLower == "template")
                {
                    return CloseTemplateElement();
                }

                if (endTagLower == "head")
                {
                    SafePopOpenElement(); // Pop head
                    SwitchTo(InsertionMode.AfterHead);
                    return true;
                }
                if (endTagLower == "body" || endTagLower == "html" || endTagLower == "br")
                {
                     // Act as if head closed
                     SafePopOpenElement();
                     SwitchTo(InsertionMode.AfterHead);
                     return false; // Reprocess
                }
                // Ignore other end tags
                return true; 
            }
            
            // Anything else? Pop head and reprocess
             SafePopOpenElement();
             SwitchTo(InsertionMode.AfterHead);
             return false;
        }
        
        private bool HandleAfterHead(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 InsertCharacter(ct);
                 return true;
             }
             
             if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }
            
            if (token is StartTagToken st)
            {
                var tagLower = st.TagName?.ToLowerInvariant() ?? string.Empty;

                if (tagLower == "html") return HandleInBody(token);
                if (tagLower == "body")
                {
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InBody);
                    return true;
                }
                
                if (tagLower == "frameset")
                {
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InFrameset);
                    return true;
                }
                
                // These head-content elements must be processed via HandleInHead to ensure
                // the tokenizer state is correctly switched (ScriptData/RawText/RcData).
                // Without this, the body of <script>/<style> gets parsed as regular HTML.
                if (tagLower == "base" || tagLower == "link" || tagLower == "meta" ||
                    tagLower == "script" || tagLower == "style" || tagLower == "title" ||
                    tagLower == "noframes" || tagLower == "noscript" ||
                    tagLower == "basefont" || tagLower == "bgsound" ||
                    tagLower == "template")
                {
                    // Spec says: "Process the token using the rules for the in head insertion mode."
                    return HandleInHead(token);
                }
                 
                 if (tagLower == "head") return true; // Ignore
            }
            
            // Anything else? Create <body> and reprocess
             InsertHtmlElement(new StartTagToken() { TagName = "body" });
             SwitchTo(InsertionMode.InBody);
             return false;
        }
        
        private bool HandleInBody(HtmlToken token)
        {
             if (token is CharacterToken ct)
             {
                 if (ct.Data == "\0") return true; // Ignore null
                 // Reconstruct active formatting elements per WHATWG Â§13.2.6.4.7
                 ReconstructActiveFormattingElements();
                 InsertCharacter(ct);
                 return true;
             }
             
              if (token is CommentToken comment)
            {
                InsertNodeAtAppropriatePlace(new Comment(comment.Data));
                return true;
            }
            
            if (token is StartTagToken st)
            {
                if (st.TagName == "html")
                {
                    // Parse error. Add attributes to html element if missing.
                    return true;
                }
                
                if (st.TagName == "base" || st.TagName == "link" || st.TagName == "meta" || st.TagName == "script" || st.TagName == "style" || st.TagName == "title" || st.TagName == "template")
                {
                    return HandleInHead(token);
                }
                
                if (st.TagName == "body")
                {
                    // Parse error.
                    return true;
                }
                
                if (st.TagName == "div" || st.TagName == "p" || st.TagName == "ul" || st.TagName == "ol" || st.TagName == "dl" || st.TagName == "blockquote" || 
                    st.TagName == "article" || st.TagName == "section" || st.TagName == "nav" || st.TagName == "header" || st.TagName == "footer" || st.TagName == "main" ||
                    st.TagName == "address" || st.TagName == "aside" || st.TagName == "center" || st.TagName == "details" || st.TagName == "dialog" || st.TagName == "dir" || 
                    st.TagName == "fieldset" || st.TagName == "figcaption" || st.TagName == "figure" || st.TagName == "hgroup" || st.TagName == "menu" || 
                    st.TagName == "summary" || st.TagName == "pre" || st.TagName == "listing")
                {
                    if (HasOpenParagraphElement()) 
                    {
                         ClosePElement();
                    }
                    if (st.TagName == "p")
                    {
                         // If st.TagName is p, we either closed it above or it's a new p
                         // Handled by the generic InsertHtmlElement below
                    }
                    InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "li")
                {
                    // HTML5 spec Ã‚Â§12.2.6.4.7: walk backwards through open elements
                    // looking for an open <li>. Pass through <div>, <address>, <p>
                    // (which are "special" but excluded from the stop condition).
                    // Stop at any other "special" element.
                    // Stack.ElementAt(0) = top (current node), increasing index goes toward bottom.
                    for (int idx = 0; idx < _openElements.Count; idx++)
                    {
                        var node = _openElements.ElementAt(idx);
                        string nodeName = node?.TagName?.ToLowerInvariant();
                        if (nodeName == "li")
                        {
                            GenerateImpliedEndTags("li");
                            PopUntil("li");
                            break;
                        }
                        // Stop at "special" elements, EXCEPT div, address, and p
                        if (nodeName != "div" && nodeName != "address" && nodeName != "p" && IsSpecialElement(nodeName))
                            break;
                    }
                    if (HasOpenParagraphElement()) ClosePElement();
                    InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "dd" || st.TagName == "dt")
                {
                     // HTML5 spec Ã‚Â§12.2.6.4.7: walk backwards like <li>
                     for (int idx = 0; idx < _openElements.Count; idx++)
                     {
                         var node = _openElements.ElementAt(idx);
                         string nodeName = node?.TagName?.ToLowerInvariant();
                         if (nodeName == "dd" || nodeName == "dt")
                         {
                             GenerateImpliedEndTags(nodeName);
                             PopUntil(nodeName);
                             break;
                         }
                         if (nodeName != "div" && nodeName != "address" && nodeName != "p" && IsSpecialElement(nodeName))
                             break;
                     }
                     if (HasOpenParagraphElement()) ClosePElement();
                     InsertHtmlElement(st);
                     return true;
                }

                if (st.TagName == "h1" || st.TagName == "h2" || st.TagName == "h3" || st.TagName == "h4" || st.TagName == "h5" || st.TagName == "h6")
                {
                    if (HasOpenParagraphElement()) ClosePElement();
                    InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "a")
                {
                    var activeAnchor = FindLastActiveFormattingElement("a");
                    if (activeAnchor != null)
                    {
                        RunAdoptionAgencyAlgorithm("a");
                        _activeFormattingElements.Remove(activeAnchor);
                        RemoveOpenElement(activeAnchor);
                    }
                    ReconstructActiveFormattingElements();
                    InsertHtmlElement(st);
                    PushActiveFormattingElement((Element)CurrentNode);
                    return true;
                }

                // HTML 13.2.6.4.7 "in body", start tag applet/marquee/object: these
                // push a marker, so formatting elements opened before them are not
                // reconstructed inside them or found by a later <a> - even once a
                // </table> has popped a foster-parented one off the stack.
                if (st.TagName == "applet" || st.TagName == "marquee" || st.TagName == "object")
                {
                    ReconstructActiveFormattingElements();
                    InsertHtmlElement(st);
                    _activeFormattingElements.Add(null); // Marker
                    return true;
                }
                
                if (st.TagName == "nobr" && HasElementInButtonScope("nobr"))
                {
                    // Misnested <nobr>: run the adoption agency before inserting the new one.
                    RunAdoptionAgencyAlgorithm("nobr");
                }

                if (st.TagName == "b" || st.TagName == "strong" || st.TagName == "em" || st.TagName == "i" || st.TagName == "u" || st.TagName == "s" || st.TagName == "small" || st.TagName == "code" ||
                    st.TagName == "nobr" || st.TagName == "big" || st.TagName == "font" || st.TagName == "tt" || st.TagName == "strike")
                {
                     // Reconstruct active formatting elements per spec
                     ReconstructActiveFormattingElements();
                     InsertHtmlElement(st);
                     PushActiveFormattingElement((Element)CurrentNode);
                     return true;
                }
                
                if (st.TagName == "textarea")
                {
                    InsertGenericRCDATAElement(st);
                    return true;
                }

                if (st.TagName == "select")
                {
                    InsertHtmlElement(st);
                    if (StackHas("table"))
                    {
                        SwitchTo(InsertionMode.InSelectInTable);
                    }
                    else
                    {
                        SwitchTo(InsertionMode.InSelect);
                    }
                    return true;
                }
                
                if (st.TagName == "xmp" || st.TagName == "iframe" || st.TagName == "noembed" || st.TagName == "noscript")
                {
                    InsertGenericRawTextElement(st);
                    return true;
                }

                if (st.TagName == "plaintext")
                {
                    if (HasOpenParagraphElement()) ClosePElement();
                    InsertHtmlElement(st);
                    _tokenizer.SetState(HtmlTokenizer.TokenizerState.PlainText);
                    return true;
                }

                if (st.TagName == "image")
                {
                    st.TagName = "img";
                }

                if (HtmlElementSemantics.IsVoid(st.TagName, DetermineElementNamespace(st)))
                {
                     // Void elements
                     if (st.TagName == "hr" && HasOpenParagraphElement()) ClosePElement();
                     
                     var el = InsertHtmlElement(st);
                     SafePopOpenElement(); // Immediately close
                     return true;
                }
                
                // Form
                if (st.TagName == "form")
                {
                    if (_formElement != null) return true; // Ignore nested forms
                    if (HasOpenParagraphElement()) ClosePElement();
                    _formElement = InsertHtmlElement(st);
                    return true;
                }
                
                if (st.TagName == "table")
                {
                    if (HasOpenParagraphElement()) ClosePElement();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InTable);
                    return true;
                }
                
                // (Consolidated into the block above for efficiency and correctness)

                // Any other start tag: reconstruct active formatting elements, then insert
                ReconstructActiveFormattingElements();
                var inserted = InsertHtmlElement(st);
                if (st.SelfClosing &&
                    !string.Equals(inserted.NamespaceUri, Namespaces.Html, StringComparison.Ordinal))
                {
                    SafePopOpenElement();
                }
                return true;
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "body")
                {
                    SwitchTo(InsertionMode.AfterBody);
                    return true;
                }
                if (et.TagName == "html")
                {
                     SwitchTo(InsertionMode.AfterBody);
                     return false; // Reprocess
                }
                
                if (et.TagName == "p")
                {
                    if (!StackHas("p"))
                    {
                        // Parse error: </p> without <p>. Create <p> and close it. (Implies <p></p>)
                        if (DebugConfig.LogHtmlParse)
                             EngineLogCompat.Log("[HTML] Recovered </p> without open <p> (Inserted empty paragraph)", LogCategory.HtmlParsing);
                        InsertHtmlElement(new StartTagToken() { TagName = "p" });
                    }
                    // Close p
                    PopUntil("p");
                    return true;
                }
                
                if (et.TagName == "div" || et.TagName == "ul" || et.TagName == "ol" || et.TagName == "li" || 
                    et.TagName == "nav" || et.TagName == "header" || et.TagName == "footer" || et.TagName == "section" || 
                    et.TagName == "article" || et.TagName == "main" || et.TagName == "aside" ||
                    et.TagName == "address" || et.TagName == "blockquote" || et.TagName == "center" || 
                    et.TagName == "details" || et.TagName == "dialog" || et.TagName == "dir" || 
                    et.TagName == "fieldset" || et.TagName == "figcaption" || et.TagName == "figure" || 
                    et.TagName == "hgroup" || et.TagName == "menu" || et.TagName == "summary" || 
                    et.TagName == "pre" || et.TagName == "listing" ||
                    et.TagName == "h1" || et.TagName == "h2" || et.TagName == "h3" || et.TagName == "h4" || et.TagName == "h5" || et.TagName == "h6")
                {
                     if (StackHas(et.TagName)) PopUntil(et.TagName);
                     return true;
                }
                
                if (et.TagName == "form")
                {
                     // Close form
                     // Spec is complex taking _formElement into account
                     if (_formElement != null) _formElement = null; // Simply nullify
                     if (StackHas("form")) PopUntil("form");
                     return true;
                }
                
                // HTML 13.2.6.4.7 "in body", end tag applet/marquee/object: close the
                // element and clear the formatting list back to its marker.
                if (et.TagName == "applet" || et.TagName == "marquee" || et.TagName == "object")
                {
                    if (!InScope(et.TagName, DefaultScopeBoundaries))
                    {
                        return true; // Parse error: ignore the token.
                    }

                    GenerateImpliedEndTags();
                    PopUntil(et.TagName);
                    ClearActiveFormattingElementsMarker();
                    return true;
                }

                // Formatting elements (Adoption Agency Algorithm)
                // WHATWG HTML spec Â§13.2.6.4.7
                if (IsFormattingElement(et.TagName))
                {
                    RunAdoptionAgencyAlgorithm(et.TagName);
                    return true;
                }
                
                // Scripts?
                if (et.TagName == "script")
                {
                    // Should be handled in Text mode
                    return true;
                }

                // Fallback for ordinary end tags that are not in the explicit lists above.
                // Without this, tags like </span>, </button>, </svg>, and </path> get ignored,
                // which corrupts stack structure and causes large layout/render regressions.
                if (StackHas(et.TagName))
                {
                    if (string.Equals(et.TagName, "form", StringComparison.OrdinalIgnoreCase))
                        _formElement = null;
                    PopUntil(et.TagName);
                }
                return true;
            }
            
            if (token is EofToken)
            {
                // Stop
                return true;
            }

            return true;
        }
        
        private bool HandleAfterBody(HtmlToken token)
        {
             if (token is CharacterToken ct && string.IsNullOrWhiteSpace(ct.Data))
             {
                 return HandleInBody(token); // Spec says process as if in body? No, spec says process in body for whitespace
             }
             if (token is CommentToken)
             {
                 // Append to html element
                 _document.DocumentElement?.AppendChild(new Comment(((CommentToken)token).Data));
                 return true;
             }
             
             if (token is EndTagToken et && et.TagName == "html")
             {
                 SwitchTo(InsertionMode.AfterAfterBody);
                 return true;
             }
             
             if (token is EofToken) return true;
             
             // Parse error -> switch to InBody and reprocess
             SwitchTo(InsertionMode.InBody);
             return false;
        }
        
        private bool HandleAfterAfterBody(HtmlToken token)
        {
             if (token is CommentToken)
             {
                 _document.AppendChild(new Comment(((CommentToken)token).Data));
                 return true;
             }
             if (token is EofToken) return true;
             
             SwitchTo(InsertionMode.InBody);
             return false;
        }
        
        // --- Table Insertion Modes ---

        private bool HandleInTable(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                _pendingTableText.Clear();
                _originalInsertionMode = _insertionMode;
                SwitchTo(InsertionMode.InTableText);
                return false; // Reprocess in InTableText.
            }

            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken) return true; // Ignore

            if (token is StartTagToken st)
            {
                if (st.TagName == "caption")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InCaption);
                    return true;
                }
                if (st.TagName == "colgroup")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InColumnGroup);
                    return true;
                }
                if (st.TagName == "col")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(new StartTagToken { TagName = "colgroup" });
                    SwitchTo(InsertionMode.InColumnGroup);
                    return false; // Reprocess col
                }
                if (st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(st);
                    SwitchTo(InsertionMode.InTableBody);
                    return true;
                }
                if (st.TagName == "td" || st.TagName == "th" || st.TagName == "tr")
                {
                    ClearStackBackToTableContext();
                    InsertHtmlElement(new StartTagToken { TagName = "tbody" });
                    SwitchTo(InsertionMode.InTableBody);
                    return false; // Reprocess
                }
                
                if (st.TagName == "table")
                {
                    // Parse error -> check scope closure
                    if (!InTableScope("table"))
                    {
                         // ignore
                         return true;
                    }
                    PopUntil("table");
                    // Reprocess "table" in ResetInsertionMode (Back to InBody probably?)
                    // Simplified: treat as end of table, then reprocess
                    // But spec says: "Act as if an end tag token with tag name 'table' had been seen, then... process the token in InBody"
                    // So we close current, then reprocess `st`
                    return HandleInTable(new EndTagToken { TagName = "table" }) ? HandleInBody(token) : false;
                }

                if (st.TagName == "style" || st.TagName == "script" || st.TagName == "template")
                {
                    return HandleInHead(token);
                }
                
                if (st.TagName == "input")
                {
                    // Special case: if hidden, append to table. Else foster parent.
                    var type = st.HasAttributes
                        ? st.Attributes.FirstOrDefault(a => a.Name.Equals("type", StringComparison.OrdinalIgnoreCase))?.Value
                        : null;
                    if (string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
                    {
                        InsertHtmlElement(st);
                        SafePopOpenElement();
                        return true;
                    }
                }
                
                if (st.TagName == "form")
                {
                    // Parse error
                    if (_formElement != null) return true; // Ignore
                    _formElement = InsertHtmlElement(st);
                    SafePopOpenElement(); // Immediately pop
                    return true;
                }
            }
            
            if (token is EndTagToken et)
            {
                if (et.TagName == "table")
                {
                    if (!InTableScope("table"))
                    {
                        // Error
                        return true;
                    }
                    PopUntil("table");
                    ResetInsertionMode();
                    return true;
                }
                
                if (et.TagName == "body" || et.TagName == "caption" || et.TagName == "col" || et.TagName == "colgroup" || et.TagName == "html" || et.TagName == "tbody" || et.TagName == "td" || et.TagName == "tfoot" || et.TagName == "th" || et.TagName == "thead" || et.TagName == "tr")
                {
                    // Parse error -> ignore
                    return true;
                }
            }
            
            if (token is EofToken)
            {
                return HandleInBody(token); // Propagate up
            }

            // --- Foster Parenting ---
            // "Enable foster parenting, process the token using the rules for the In Body insertion mode"
            return FosterParent(token);
        }

        private bool HandleInTableText(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                // Copy the data out immediately; the token instance is pool-owned and
                // may be recycled (ResetWith) before this buffer is flushed.
                _pendingTableText.Append(ct.Data);
                return true;
            }

            var pendingText = _pendingTableText.ToString();
            _pendingTableText.Clear();
            if (pendingText.Length == 0)
            {
                SwitchTo(_originalInsertionMode);
                return false; // Reprocess the non-character token.
            }

            var flushToken = new CharacterToken(pendingText);
            if (!string.IsNullOrWhiteSpace(pendingText))
            {
                FosterParent(flushToken);
            }
            else
            {
                InsertCharacter(flushToken);
            }

            SwitchTo(_originalInsertionMode);
            return false; // Reprocess the non-character token.
        }

        private bool HandleInTableBody(HtmlToken token)
        {
             if (token is StartTagToken st)
             {
                 if (st.TagName == "tr")
                 {
                     ClearStackBackToTableBodyContext();
                     InsertHtmlElement(st);
                     SwitchTo(InsertionMode.InRow);
                     return true;
                 }
                 if (st.TagName == "th" || st.TagName == "td")
                 {
                     ClearStackBackToTableBodyContext();
                     InsertHtmlElement(new StartTagToken { TagName = "tr" });
                     SwitchTo(InsertionMode.InRow);
                     return false; // Reprocess
                 }
                 if (st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead" || st.TagName == "table")
                 {
                     // Close body
                     if (!InTableScope("tbody") && !InTableScope("thead") && !InTableScope("tfoot"))
                     {
                         // Error
                         return true; 
                     }
                     ClearStackBackToTableBodyContext();
                     SafePopOpenElement(); // Pop body
                     SwitchTo(InsertionMode.InTable);
                     return false; // Reprocess
                 }
             }
             
             if (token is EndTagToken et)
             {
                 if (et.TagName == "tbody" || et.TagName == "tfoot" || et.TagName == "thead")
                 {
                     if (!InTableScope(et.TagName)) return true; // Error
                     ClearStackBackToTableBodyContext();
                     SafePopOpenElement();
                     SwitchTo(InsertionMode.InTable);
                     return true;
                 }
                 if (et.TagName == "table")
                 {
                      if (!InTableScope("tbody") && !InTableScope("thead") && !InTableScope("tfoot"))
                     {
                         // Error
                         return true; 
                     }
                     ClearStackBackToTableBodyContext();
                     SafePopOpenElement();
                     SwitchTo(InsertionMode.InTable);
                     return false; // Reprocess
                 }
             }
             
             return HandleInTable(token); // Anything else -> processed in InTable (which might foster parent)
        }

        private bool HandleInRow(HtmlToken token)
        {
             if (token is StartTagToken st)
             {
                 if (st.TagName == "th" || st.TagName == "td")
                 {
                     ClearStackBackToTableRowContext();
                     InsertHtmlElement(st);
                     SwitchTo(InsertionMode.InCell);
                     _activeFormattingElements.Add(null); // Marker
                     return true;
                 }
                 if (st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead" || st.TagName == "tr" || st.TagName == "table")
                 {
                     if (!InTableScope("tr")) return true; // Error
                     ClearStackBackToTableRowContext();
                     SafePopOpenElement(); // Pop tr
                     SwitchTo(InsertionMode.InTableBody);
                     return false; // Reprocess
                 }
             }
             
             if (token is EndTagToken et)
             {
                 if (et.TagName == "tr")
                 {
                     if (!InTableScope("tr")) return true; // Ignore
                     ClearStackBackToTableRowContext();
                     SafePopOpenElement(); // Pop tr
                     SwitchTo(InsertionMode.InTableBody);
                     return true;
                 }
                 if (et.TagName == "table")
                 {
                      if (!InTableScope("tr")) return true;
                      ClearStackBackToTableRowContext();
                      SafePopOpenElement(); // Pop tr
                      SwitchTo(InsertionMode.InTableBody);
                      return false; // Reprocess
                 }
                 if (et.TagName == "tbody" || et.TagName == "tfoot" || et.TagName == "thead")
                 {
                      if (!InTableScope(et.TagName)) return true; // Error
                      if (!InTableScope("tr")) return true; // Error
                      ClearStackBackToTableRowContext();
                      SafePopOpenElement(); // Pop tr
                      SwitchTo(InsertionMode.InTableBody);
                      return false; // Reprocess
                 }
             }

             return HandleInTable(token);
        }
        
        private bool HandleInCell(HtmlToken token)
        {
            if (token is StartTagToken st)
            {
                 if (st.TagName == "td" || st.TagName == "th" || st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "tfoot" || st.TagName == "thead" || st.TagName == "tr")
                 {
                     if (!InTableScope("td") && !InTableScope("th")) 
                     {
                         // Parse error: open cell not found (shouldn't happen in InCell mode unless stack corrupted or manipulated)
                         return true; 
                     }
                     CloseCell();
                     return false; // Reprocess
                 }
            }

            if (token is EndTagToken et)
            {
                if (et.TagName == "td" || et.TagName == "th")
                {
                    if (!InTableScope(et.TagName)) return true; // Ignore
                    GenerateImpliedEndTags();
                    if ((CurrentNode as Element)?.TagName != et.TagName)
                    {
                        // Parse error
                    }
                    PopUntil(et.TagName);
                    ClearActiveFormattingElementsMarker();
                    SwitchTo(InsertionMode.InRow);
                    return true;
                }
                if (et.TagName == "body" || et.TagName == "caption" || et.TagName == "col" || et.TagName == "colgroup" || et.TagName == "html")
                {
                    // Parse error -> ignore
                    return true;
                }
                if (et.TagName == "table" || et.TagName == "tbody" || et.TagName == "tfoot" || et.TagName == "thead" || et.TagName == "tr")
                {
                    if (!InTableScope(et.TagName)) return true; // Error
                    CloseCell();
                    return false; // Reprocess
                }
            }
            
            return HandleInBody(token);
        }

        private bool HandleInSelect(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                InsertCharacter(ct);
                return true;
            }

            if (token is CommentToken comment)
            {
                CurrentNode.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken)
            {
                return true;
            }

            if (token is StartTagToken st)
            {
                if (st.TagName == "html")
                {
                    return HandleInBody(token);
                }

                if (st.TagName == "option")
                {
                    if ((CurrentNode as Element)?.TagName == "OPTION")
                    {
                        SafePopOpenElement();
                    }

                    InsertHtmlElement(st);
                    return true;
                }

                if (st.TagName == "optgroup")
                {
                    if ((CurrentNode as Element)?.TagName == "OPTION")
                    {
                        SafePopOpenElement();
                    }

                    if ((CurrentNode as Element)?.TagName == "OPTGROUP")
                    {
                        SafePopOpenElement();
                    }

                    InsertHtmlElement(st);
                    return true;
                }

                if (st.TagName == "select")
                {
                    if (!StackHas("select"))
                    {
                        return true;
                    }

                    PopUntil("select");
                    ResetInsertionMode();
                    return true;
                }

                if (st.TagName == "input" || st.TagName == "keygen" || st.TagName == "textarea")
                {
                    if (!StackHas("select"))
                    {
                        return true;
                    }

                    PopUntil("select");
                    ResetInsertionMode();
                    return false; // Reprocess in new mode.
                }

                if (st.TagName == "script" || st.TagName == "template")
                {
                    return HandleInHead(token);
                }
            }

            if (token is EndTagToken et)
            {
                if (et.TagName == "optgroup")
                {
                    if ((CurrentNode as Element)?.TagName == "OPTION" &&
                        _openElements.Count > 1 &&
                        string.Equals(_openElements.ElementAt(1).TagName, "OPTGROUP", StringComparison.OrdinalIgnoreCase))
                    {
                        SafePopOpenElement(); // option
                    }

                    if ((CurrentNode as Element)?.TagName == "OPTGROUP")
                    {
                        SafePopOpenElement();
                    }

                    return true;
                }

                if (et.TagName == "option")
                {
                    if ((CurrentNode as Element)?.TagName == "OPTION")
                    {
                        SafePopOpenElement();
                    }

                    return true;
                }

                if (et.TagName == "select")
                {
                    if (!StackHas("select"))
                    {
                        return true;
                    }

                    PopUntil("select");
                    ResetInsertionMode();
                    return true;
                }

                if (et.TagName == "template")
                {
                    return HandleInHead(token);
                }
            }

            if (token is EofToken)
            {
                return true;
            }

            return true;
        }

        private bool HandleInSelectInTable(HtmlToken token)
        {
            if (token is StartTagToken st && IsSelectTableRelatedTag(st.TagName))
            {
                if (StackHas("select"))
                {
                    PopUntil("select");
                    ResetInsertionMode();
                }

                return false; // Reprocess token in new mode.
            }

            if (token is EndTagToken et && IsSelectTableRelatedTag(et.TagName))
            {
                if (StackHas("select"))
                {
                    PopUntil("select");
                    ResetInsertionMode();
                }

                return false; // Reprocess token in new mode.
            }

            return HandleInSelect(token);
        }

        private static bool IsSelectTableRelatedTag(string tagName)
        {
            return string.Equals(tagName, "caption", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "table", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "tbody", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "tfoot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "thead", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "tr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "td", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "th", StringComparison.OrdinalIgnoreCase);
        }
        
        private void CloseCell()
        {
            GenerateImpliedEndTags();
            if (CurrentTag != "td" && CurrentTag != "th")
            {
                 // Error
            }
            while (CurrentTag != "td" && CurrentTag != "th" && _openElements.Count > 0)
            {
                SafePopOpenElement();
            }
             if (_openElements.Count > 0) SafePopOpenElement();
             ClearActiveFormattingElementsMarker();
             SwitchTo(InsertionMode.InRow);
        }
        
        // --- Foster Parenting Logic ---
        private bool FosterParent(HtmlToken token)
        {
            if (CurrentNode is Element currentElement &&
                !IsCharacterFosterBoundaryElement(currentElement.TagName))
            {
                return HandleInBody(token);
            }

            var previous = _fosterParenting;
            _fosterParenting = true;
            try
            {
                return HandleInBody(token);
            }
            finally
            {
                _fosterParenting = previous;
            }
        }

        private static bool IsCharacterFosterBoundaryElement(string tagName)
        {
            return string.Equals(tagName, "table", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "tbody", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "tfoot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "thead", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "tr", StringComparison.OrdinalIgnoreCase);
        }

        private bool HandleInCaption(HtmlToken token)
        {
            if (token is EndTagToken et && et.TagName == "caption")
            {
                if (!InTableScope("caption")) return true; // Error
                GenerateImpliedEndTags();
                if ((CurrentNode as Element)?.TagName != "caption") { /* Parse error */ }
                PopUntil("caption");
                ClearActiveFormattingElementsMarker();
                SwitchTo(InsertionMode.InTable);
                return true;
            }
            if (token is StartTagToken st && (st.TagName == "caption" || st.TagName == "col" || st.TagName == "colgroup" || st.TagName == "tbody" || st.TagName == "td" || st.TagName == "tfoot" || st.TagName == "th" || st.TagName == "thead" || st.TagName == "tr"))
            {
                 if (!InTableScope("caption")) return true; // Error
                 GenerateImpliedEndTags();
                 PopUntil("caption");
                 ClearActiveFormattingElementsMarker();
                 SwitchTo(InsertionMode.InTable);
                 return false; // Reprocess
            }
            if (token is EndTagToken et2 && et2.TagName == "table")
            {
                 if (!InTableScope("caption")) return true; // Error
                 GenerateImpliedEndTags();
                 PopUntil("caption");
                 ClearActiveFormattingElementsMarker();
                 SwitchTo(InsertionMode.InTable);
                 return false; // Reprocess
            }
            return HandleInBody(token);
        }

        private bool HandleInColumnGroup(HtmlToken token)
        {
             if (token is CharacterToken ct && IsTableWhitespace(ct))
             {
                 InsertCharacter(ct);
                 return true;
             }
             if (token is CommentToken c)
             {
                 CurrentNode.AppendChild(new Comment(c.Data));
                 return true;
             }
             if (token is DoctypeToken) return true;
             if (token is StartTagToken st)
             {
                 if (st.TagName == "html") return HandleInBody(token);
                 if (st.TagName == "col")
                 {
                     InsertHtmlElement(st);
                     SafePopOpenElement(); // Col is void
                     // Attributes acknowledgment
                     return true;
                 }
                 if (st.TagName == "template") return HandleInHead(token);
             }
             if (token is EndTagToken et && et.TagName == "colgroup")
             {
                 if ((CurrentNode as Element)?.TagName != "colgroup") { /* Parse error */ }
                 SafePopOpenElement();
                 SwitchTo(InsertionMode.InTable);
                 return true;
             }
             if (token is EofToken) return HandleInBody(token);
             
             // Anything else: pop colgroup, reprocess
             if ((CurrentNode as Element)?.TagName != "colgroup") { /* Parse error */ }
             SafePopOpenElement();
             SwitchTo(InsertionMode.InTable);
             return false;
        }

        private bool IsTableWhitespace(CharacterToken ct)
        {
            // ASCII whitespace
            return string.IsNullOrWhiteSpace(ct.Data);
        }

        private bool InScope(string tagName, string[] scopeLimits)
        {
            foreach (var node in _openElements) // Iterates top to bottom? C# stack enumerates top-down (LIFO)
            {
                if (node is Element el)
                {
                    if (string.Equals(el.TagName, tagName, StringComparison.OrdinalIgnoreCase)) return true;
                    if (scopeLimits.Any(s => string.Equals(s, el.TagName, StringComparison.OrdinalIgnoreCase))) return false;
                }
            }
            return false;
        }
        
        // HTML 13.2.4.2 "has an element in scope": the default scope's boundaries.
        private static readonly string[] DefaultScopeBoundaries =
        {
            "applet", "caption", "html", "table", "td", "th", "marquee", "object", "template",
            "mi", "mo", "mn", "ms", "mtext", "annotation-xml", "foreignObject", "desc", "title",
        };

        private bool InTableScope(string tagName)
        {
            return InScope(tagName, new[] { "html", "table", "template" }); // Table scope limits
        }

        private bool CloseTemplateElement()
        {
            if (!InScope("template", new[] { "html" }))
                return true; // Parse error: ignore

            GenerateImpliedEndTags();
            PopUntil("template");
            ClearActiveFormattingElementsMarker();
            if (_templateInsertionModes.Count > 0)
                _templateInsertionModes.Pop();
            ResetInsertionMode();
            return true;
        }

        private void SetCurrentTemplateInsertionMode(InsertionMode mode)
        {
            if (_templateInsertionModes.Count > 0)
                _templateInsertionModes.Pop();
            _templateInsertionModes.Push(mode);
        }
        
        private void ClearStackBackToTableContext()
        {
            while (CurrentTag != "table" && CurrentTag != "template" && CurrentTag != "html" && _openElements.Count > 0)
            {
                SafePopOpenElement();
            }
        }
        
        private void ClearStackBackToTableBodyContext()
        {
            while (CurrentTag != "tbody" && CurrentTag != "tfoot" && CurrentTag != "thead" && CurrentTag != "template" && CurrentTag != "html" && _openElements.Count > 0)
            {
                SafePopOpenElement();
            }
        }
        
        private void ClearStackBackToTableRowContext()
        {
            while (CurrentTag != "tr" && CurrentTag != "template" && CurrentTag != "html" && _openElements.Count > 0)
            {
                SafePopOpenElement();
            }
        }
        
        private void ClearActiveFormattingElementsMarker()
        {
            while (_activeFormattingElements.Count > 0)
            {
                var entry = _activeFormattingElements[_activeFormattingElements.Count - 1];
                _activeFormattingElements.RemoveAt(_activeFormattingElements.Count - 1);
                if (entry == null) break;
            }
        }
        
        private string CurrentTag => (CurrentNode as Element)?.TagName?.ToLowerInvariant();

        private void IgnoreToken(HtmlToken token) { } // No-op

        private bool HandleText(HtmlToken token)
        {
            if (token is CharacterToken ct)
            {
                InsertCharacter(ct);
                return true;
            }
            
            if (token is EndTagToken et)
            {
                // If closing the element that put us in Text mode (CurrentNode), pop and switch back.
                // Note: The Tokenizer ensures we only get this EndTag if it matches the start tag (for RCDATA/RAWTEXT).
                // So we can blindly accept it.
                SafePopOpenElement();
                SwitchTo(_originalInsertionMode);
                return true;
            }
            
            // Per HTML spec: any other token in "text" mode Ã¢â€ â€™ pop the current node,
            // switch back to the original insertion mode, and reprocess.
            SafePopOpenElement();
            SwitchTo(_originalInsertionMode);
            return false; // Reprocess in original mode
        }

        // --- Helpers ---

        private void ResetInsertionMode()
        {
            var stackSnapshot = _openElements.ToArray(); // Top -> bottom
            for (int i = 0; i < stackSnapshot.Length; i++)
            {
                var el = stackSnapshot[i];
                if (el == null) continue;
                var tagName = el.TagName;
                
                if (tagName.Equals("select", StringComparison.OrdinalIgnoreCase))
                {
                    for (int j = i + 1; j < stackSnapshot.Length; j++)
                    {
                        var ancestorTag = stackSnapshot[j]?.TagName;
                        if (ancestorTag == null)
                        {
                            continue;
                        }

                        if (ancestorTag.Equals("template", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        if (ancestorTag.Equals("table", StringComparison.OrdinalIgnoreCase))
                        {
                            SwitchTo(InsertionMode.InSelectInTable);
                            return;
                        }
                    }

                    SwitchTo(InsertionMode.InSelect);
                    return;
                }
                if (tagName.Equals("td", StringComparison.OrdinalIgnoreCase) || tagName.Equals("th", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InCell); return; }
                if (tagName.Equals("tr", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InRow); return; }
                if (tagName.Equals("tbody", StringComparison.OrdinalIgnoreCase) || tagName.Equals("thead", StringComparison.OrdinalIgnoreCase) || tagName.Equals("tfoot", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InTableBody); return; }
                if (tagName.Equals("caption", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InCaption); return; }
                if (tagName.Equals("colgroup", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InColumnGroup); return; }
                if (tagName.Equals("table", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InTable); return; }
                if (tagName.Equals("template", StringComparison.OrdinalIgnoreCase)) { 
                     if (_templateInsertionModes.Count > 0)
                     {
                         SwitchTo(_templateInsertionModes.Peek());
                     }
                     else
                     {
                         SwitchTo(InsertionMode.InBody);
                     }
                     return;
                }
                if (tagName.Equals("head", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InHead); return; }
                if (tagName.Equals("body", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InBody); return; }
                if (tagName.Equals("frameset", StringComparison.OrdinalIgnoreCase)) { SwitchTo(InsertionMode.InFrameset); return; }
                if (tagName.Equals("html", StringComparison.OrdinalIgnoreCase))
                {
                    SwitchTo(_headElement == null ? InsertionMode.BeforeHead : InsertionMode.AfterHead);
                    return;
                }
            }
             SwitchTo(InsertionMode.InBody);
        }
        
        private ContainerNode CurrentNode => _openElements.Count > 0 ? (ContainerNode)_openElements.Peek() : (ContainerNode)_document;
        private ContainerNode CurrentInsertionTarget => CurrentNode is HtmlTemplateElement template
            ? template.Content
            : CurrentNode;

        private (ContainerNode Parent, Node Before) GetAppropriateInsertionLocation()
        {
            // Foster parenting only applies when the insertion target itself is a
            // table-family element (WHATWG "appropriate place for inserting a node").
            if (_fosterParenting && IsTableFamilyInsertionTarget(CurrentInsertionTarget))
            {
                return GetFosterParentInsertionLocation();
            }

            return (CurrentInsertionTarget, null);
        }

        private static bool IsTableFamilyInsertionTarget(Node target)
        {
            return target is Element element &&
                (string.Equals(element.LocalName, "table", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(element.LocalName, "tbody", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(element.LocalName, "tfoot", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(element.LocalName, "thead", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(element.LocalName, "tr", StringComparison.OrdinalIgnoreCase));
        }

        private void InsertNodeAtAppropriatePlace(Node node)
        {
            var (parent, before) = GetAppropriateInsertionLocation();
            if (before != null)
                parent.InsertBefore(node, before);
            else
                parent.AppendChild(node);
        }
        
        private void SwitchTo(InsertionMode mode)
        {
            _insertionMode = mode;
        }

        private Element SafePopOpenElement()
        {
            if (_openElements.Count == 0)
            {
                if (!_openElementUnderflowLogged)
                {
                    _openElementUnderflowLogged = true;
                    EngineLogCompat.Warn("[HtmlTreeBuilder] Open-elements stack underflow avoided during malformed token recovery.", LogCategory.HtmlParsing);
                }
                return null;
            }

            return _openElements.Pop();
        }

        private void EnforceOpenElementDepthLimit()
        {
            if (MaxOpenElementsDepth <= 0)
            {
                return;
            }

            if (_openElements.Count <= MaxOpenElementsDepth)
            {
                return;
            }

            if (!_openElementsDepthLimitLogged)
            {
                _openElementsDepthLimitLogged = true;
                EngineLogCompat.Warn($"[HtmlTreeBuilder] Open-elements depth exceeded limit ({MaxOpenElementsDepth}). Auto-closing overflow elements.", LogCategory.HtmlParsing);
            }

            while (_openElements.Count > MaxOpenElementsDepth)
            {
                var popped = SafePopOpenElement();
                if (popped != null)
                {
                    _activeFormattingElements.Remove(popped);
                }
            }
        }
        
        private Element CreateElement(StartTagToken token)
        {
            var namespaceUri = DetermineElementNamespace(token);
            var el = _document.CreateElementForParser(token.TagName, namespaceUri);
            if (token.SourceOffset >= 0)
            {
                el.SourceOffset = token.SourceOffset;
                el.SourceLine = token.SourceLine;
                el.SourceColumn = token.SourceColumn;
            }

            bool isForeignContent = !string.Equals(namespaceUri, Namespaces.Html, StringComparison.Ordinal);
            if (isForeignContent)
            {
                 AdjustForeignAttributes(token);
            }

            if (token.HasAttributes)
            {
                foreach (var attr in token.Attributes)
                {
                    el.SetAttributeUnsafe(attr.Name, attr.Value);
                }
            }
            return el;
        }

        private string DetermineElementNamespace(StartTagToken token)
        {
            var tagName = token.TagName;
            if (string.Equals(tagName, "svg", StringComparison.OrdinalIgnoreCase))
            {
                return Namespaces.Svg;
            }

            if (string.Equals(tagName, "math", StringComparison.OrdinalIgnoreCase))
            {
                return Namespaces.MathML;
            }

            var currentElement = CurrentNode as Element;
            if (currentElement == null)
            {
                return Namespaces.Html;
            }

            var currentNamespace = currentElement.NamespaceUri ?? Namespaces.Html;
            if (string.Equals(currentNamespace, Namespaces.Html, StringComparison.Ordinal))
            {
                return Namespaces.Html;
            }

            if (IsForeignContentBreakoutTag(tagName))
            {
                return Namespaces.Html;
            }
            if (IsHtmlIntegrationPoint(currentElement))
            {
                return Namespaces.Html;
            }

            if (IsMathMlTextIntegrationPoint(currentElement) &&
                !string.Equals(tagName, "mglyph", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tagName, "malignmark", StringComparison.OrdinalIgnoreCase))
            {
                return Namespaces.Html;
            }

            return currentNamespace;
        }

        private static bool IsForeignContentBreakoutTag(string tagName)
        {
            switch (tagName)
            {
                case "b": case "big": case "blockquote": case "body": case "br":
                case "center": case "code": case "dd": case "div": case "dl":
                case "dt": case "em": case "embed": case "h1": case "h2":
                case "h3": case "h4": case "h5": case "h6": case "head":
                case "hr": case "i": case "img": case "li": case "listing":
                case "menu": case "meta": case "nobr": case "ol": case "p":
                case "pre": case "ruby": case "s": case "small": case "span":
                case "strong": case "strike": case "sub": case "sup": case "table":
                case "tt": case "u": case "ul": case "var":
                    return true;
                default:
                    return false;
            }
        }
        private static bool IsMathMlTextIntegrationPoint(Element element)
        {
            if (!string.Equals(element.NamespaceUri, Namespaces.MathML, StringComparison.Ordinal))
            {
                return false;
            }

            var tag = element.LocalName;
            return tag == "mi" || tag == "mo" || tag == "mn" || tag == "ms" || tag == "mtext";
        }

        private static bool IsHtmlIntegrationPoint(Element element)
        {
            if (string.Equals(element.NamespaceUri, Namespaces.MathML, StringComparison.Ordinal) &&
                string.Equals(element.LocalName, "annotation-xml", StringComparison.OrdinalIgnoreCase))
            {
                var encoding = element.GetAttribute("encoding");
                if (string.Equals(encoding, "text/html", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(encoding, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (string.Equals(element.NamespaceUri, Namespaces.Svg, StringComparison.Ordinal))
            {
                var tag = element.LocalName;
                return string.Equals(tag, "foreignObject", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(tag, "desc", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(tag, "title", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private bool ShouldProcessUsingForeignContent(HtmlToken token)
        {
            if (token is EofToken || CurrentNode is not Element current ||
                string.Equals(current.NamespaceUri, Namespaces.Html, StringComparison.Ordinal))
            {
                return false;
            }

            if (token is CharacterToken)
            {
                return !IsMathMlTextIntegrationPoint(current) && !IsHtmlIntegrationPoint(current);
            }

            if (token is StartTagToken startTag)
            {
                if (IsMathMlTextIntegrationPoint(current) &&
                    !string.Equals(startTag.TagName, "mglyph", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(startTag.TagName, "malignmark", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(current.NamespaceUri, Namespaces.MathML, StringComparison.Ordinal) &&
                    string.Equals(current.LocalName, "annotation-xml", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(startTag.TagName, "svg", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !IsHtmlIntegrationPoint(current);
            }

            return true;
        }

        private bool HandleForeignContent(HtmlToken token)
        {
            if (token is CharacterToken characters)
            {
                InsertCharacter(characters);
                return true;
            }

            if (token is CommentToken comment)
            {
                CurrentInsertionTarget.AppendChild(new Comment(comment.Data));
                return true;
            }

            if (token is DoctypeToken)
            {
                return true;
            }

            if (token is StartTagToken startTag)
            {
                if (IsForeignContentBreakoutTag(startTag.TagName) ||
                    string.Equals(startTag.TagName, "font", StringComparison.OrdinalIgnoreCase) &&
                    startTag.Attributes.Any(attribute =>
                        attribute.Name.Equals("color", StringComparison.OrdinalIgnoreCase) ||
                        attribute.Name.Equals("face", StringComparison.OrdinalIgnoreCase) ||
                        attribute.Name.Equals("size", StringComparison.OrdinalIgnoreCase)))
                {
                    PopForeignContentToHtmlContext();
                    return false;
                }

                if (CurrentNode is Element current &&
                    string.Equals(current.NamespaceUri, Namespaces.Svg, StringComparison.Ordinal))
                {
                    AdjustSvgTagName(startTag);
                }
                var element = InsertHtmlElement(startTag);
                if (startTag.SelfClosing)
                {
                    SafePopOpenElement();
                }
                return true;
            }

            if (token is EndTagToken endTag)
            {
                foreach (var element in _openElements.ToArray())
                {
                    if (string.Equals(element.NamespaceUri, Namespaces.Html, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (string.Equals(element.LocalName, endTag.TagName, StringComparison.OrdinalIgnoreCase))
                    {
                        while (_openElements.Count > 0)
                        {
                            if (ReferenceEquals(SafePopOpenElement(), element))
                            {
                                break;
                            }
                        }
                        return true;
                    }

                }
                return true;
            }

            return false;
        }

        private void PopForeignContentToHtmlContext()
        {
            while (_openElements.Count > 0 && CurrentNode is Element current &&
                   !string.Equals(current.NamespaceUri, Namespaces.Html, StringComparison.Ordinal) &&
                   !IsMathMlTextIntegrationPoint(current) &&
                   !IsHtmlIntegrationPoint(current))
            {
                SafePopOpenElement();
            }
        }

        private static void AdjustSvgTagName(StartTagToken token)
        {
            if (_svgTagNameMap.TryGetValue(token.TagName, out var adjustedName))
            {
                token.TagName = adjustedName;
            }
        }

        private static readonly Dictionary<string, string> _svgTagNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["altglyph"] = "altGlyph",
            ["altglyphdef"] = "altGlyphDef",
            ["altglyphitem"] = "altGlyphItem",
            ["animatecolor"] = "animateColor",
            ["animatemotion"] = "animateMotion",
            ["animatetransform"] = "animateTransform",
            ["clippath"] = "clipPath",
            ["feblend"] = "feBlend",
            ["fecolormatrix"] = "feColorMatrix",
            ["fecomponenttransfer"] = "feComponentTransfer",
            ["fecomposite"] = "feComposite",
            ["feconvolvematrix"] = "feConvolveMatrix",
            ["fediffuselighting"] = "feDiffuseLighting",
            ["fedisplacementmap"] = "feDisplacementMap",
            ["fedistantlight"] = "feDistantLight",
            ["fedropshadow"] = "feDropShadow",
            ["feflood"] = "feFlood",
            ["fefunca"] = "feFuncA",
            ["fefuncb"] = "feFuncB",
            ["fefuncg"] = "feFuncG",
            ["fefuncr"] = "feFuncR",
            ["fegaussianblur"] = "feGaussianBlur",
            ["feimage"] = "feImage",
            ["femerge"] = "feMerge",
            ["femergenode"] = "feMergeNode",
            ["femorphology"] = "feMorphology",
            ["feoffset"] = "feOffset",
            ["fepointlight"] = "fePointLight",
            ["fespecularlighting"] = "feSpecularLighting",
            ["fespotlight"] = "feSpotLight",
            ["fetile"] = "feTile",
            ["feturbulence"] = "feTurbulence",
            ["foreignobject"] = "foreignObject",
            ["glyphref"] = "glyphRef",
            ["lineargradient"] = "linearGradient",
            ["radialgradient"] = "radialGradient",
            ["textpath"] = "textPath"
        };
        private bool IsSvgOrMathDescendant(Node node)
        {
             foreach (var el in _openElements)
             {
                 if (string.Equals(el.NamespaceUri, Namespaces.Svg, StringComparison.Ordinal) || string.Equals(el.NamespaceUri, Namespaces.MathML, StringComparison.Ordinal)) return true;
             }
             return false;
        }
        private void AdjustForeignAttributes(StartTagToken token)
        {
             if (!token.HasAttributes)
             {
                 return;
             }

             for (int i = 0; i < token.Attributes.Count; i++)
             {
                 var attr = token.Attributes[i];
                 var lower = attr.Name;
                 if (_foreignAttributeMap.TryGetValue(lower, out var fixedName))
                 {
                      token.Attributes[i] = new HtmlAttribute(fixedName, attr.Value);
                 }
             }
        }

        private static readonly Dictionary<string, string> _foreignAttributeMap = new Dictionary<string, string>
        {
            { "viewbox", "viewBox" },
            { "preserveaspectratio", "preserveAspectRatio" },
            { "gradientunits", "gradientUnits" },
            { "gradienttransform", "gradientTransform" },
            { "patternunits", "patternUnits" },
            { "patterntransform", "patternTransform" },
            { "maskunits", "maskUnits" },
            { "maskcontentunits", "maskContentUnits" },
            { "markerunits", "markerUnits" },
            { "markerwidth", "markerWidth" },
            { "markerheight", "markerHeight" },
            { "refx", "refX" },
            { "refy", "refY" },
            { "stop-color", "stop-color" }, // Keep as is
            { "stop-opacity", "stop-opacity" },
            { "lineargradient", "linearGradient" },
            { "radialgradient", "radialGradient" },
            { "clippath", "clipPath" },
            { "textlength", "textLength" },
            { "startoffset", "startOffset" },
            { "stddeviation", "stdDeviation" },
            { "basefrequency", "baseFrequency" },
            { "numoctaves", "numOctaves" },
            { "stitchtiles", "stitchTiles" },
            { "surfacescale", "surfaceScale" },
            { "specularconstant", "specularConstant" },
            { "specularexponent", "specularExponent" },
            { "targetx", "targetX" },
            { "targety", "targetY" },
            { "kernelmatrix", "kernelMatrix" },
            { "diffuseconstant", "diffuseConstant" },
            { "primitiveunits", "primitiveUnits" },
            { "filterunits", "filterUnits" },
            { "definitionurl", "definitionURL" },
            { "attributename", "attributeName" },
            { "attributetype", "attributeType" },
            { "calcmode", "calcMode" },
            { "keytimes", "keyTimes" },
            { "keysplines", "keySplines" }
            // Add more as needed
        };
        
        private Element InsertHtmlElement(StartTagToken token)
        {
            var el = CreateElement(token);
            InsertNodeAtAppropriatePlace(el);
            // EngineLogCompat.Debug($"[Parser] Pushing {el.TagName}_{el.GetHashCode()} to stack (Depth: {_openElements.Count})", LogCategory.HtmlParsing);
            _openElements.Push(el);

            return el;
        }
        
        private void InsertCharacter(CharacterToken token)
        {
            // Optimize: if current node's last child is text, append
            var (target, before) = GetAppropriateInsertionLocation();
            var last = before?.PreviousSibling ?? target.LastChild;
            if (last != null && UnsafeIsText(last))
            {
                last.NodeValue += token.Data;
            }
            else
            {
                var text = new Text(token.Data);
                if (before != null)
                    target.InsertBefore(text, before);
                else
                    target.AppendChild(text);
            }
        }

        private bool UnsafeIsText(Node e) => e.NodeType == NodeType.Text; // Avoid property implementation details

        private bool HasOpenParagraphElement()
        {
            return StackHas("p");
        }

        private void ClosePElement()
        {
            if (StackHas("p"))
            {
                if (DebugConfig.LogHtmlParse)
                    EngineLogCompat.Log("[HTML] Auto-closed <p> (implied end tag)", LogCategory.HtmlParsing);
                PopUntil("p");
            }
        }
        
        private void GenerateImpliedEndTags(string except = null)
        {
            while ((CurrentNode as Element)?.TagName != null)
            {
                 string currentTag = (CurrentNode as Element).TagName;
                 if (except != null && string.Equals(currentTag, except, StringComparison.OrdinalIgnoreCase)) break;
                 
                 if (IsImpliedEndTag(currentTag))
                 {
                    if (DebugConfig.LogHtmlParse)
                        EngineLogCompat.Log($"[HTML] Implied end tag for <{currentTag}>", LogCategory.HtmlParsing);
                    SafePopOpenElement();
                 }
                 else
                 {
                     break;
                 }
            }
        }
        
        private bool IsImpliedEndTag(string tag)
        {
             return string.Equals(tag, "dd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "dt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "li", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "optgroup", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "option", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "p", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "rtc", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFormattingElement(string tag)
        {
            switch (tag)
            {
                case "a": case "b": case "big": case "code": case "em": case "font":
                case "i": case "nobr": case "s": case "small": case "strike":
                case "strong": case "tt": case "u":
                    return true;
                default:
                    return false;
            }
        }

        private Element FindLastActiveFormattingElement(string localName)
        {
            for (var i = _activeFormattingElements.Count - 1; i >= 0; i--)
            {
                var entry = _activeFormattingElements[i];
                if (entry == null)
                {
                    break;
                }

                if (string.Equals(entry.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        private void PushActiveFormattingElement(Element element)
        {
            var matchingIndices = new List<int>(3);
            for (var i = _activeFormattingElements.Count - 1; i >= 0; i--)
            {
                var entry = _activeFormattingElements[i];
                if (entry == null)
                {
                    break;
                }

                if (FormattingElementsMatch(entry, element))
                {
                    matchingIndices.Add(i);
                }
            }

            if (matchingIndices.Count >= 3)
            {
                _activeFormattingElements.RemoveAt(matchingIndices[^1]);
            }

            _activeFormattingElements.Add(element);
        }

        private static bool FormattingElementsMatch(Element left, Element right)
        {
            if (!string.Equals(left.LocalName, right.LocalName, StringComparison.Ordinal) ||
                !string.Equals(left.NamespaceUri, right.NamespaceUri, StringComparison.Ordinal) ||
                left.Attributes.Length != right.Attributes.Length)
            {
                return false;
            }

            foreach (var attribute in left.Attributes)
            {
                var candidate = right.Attributes.GetNamedItemNS(attribute.NamespaceUri, attribute.LocalName);
                if (candidate == null ||
                    !string.Equals(candidate.Value, attribute.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void RemoveOpenElement(Element target)
        {
            if (!_openElements.Contains(target))
            {
                return;
            }

            var retained = _openElements.Where(element => !ReferenceEquals(element, target)).Reverse().ToArray();
            _openElements.Clear();
            foreach (var element in retained)
            {
                _openElements.Push(element);
            }
        }

        /// <summary>
        /// Reconstruct active formatting elements per WHATWG Â§13.2.4.3.
        /// Called before inserting character data and certain start tags.
        /// </summary>
        private void ReconstructActiveFormattingElements()
        {
            if (_activeFormattingElements.Count == 0) return;

            var last = _activeFormattingElements[_activeFormattingElements.Count - 1];
            // If last entry is a marker or is on the stack, nothing to do
            if (last == null) return;
            if (_openElements.Contains(last)) return;

            // Walk backward to find first entry on stack or marker
            int i = _activeFormattingElements.Count - 1;
            while (i > 0)
            {
                i--;
                var entry = _activeFormattingElements[i];
                if (entry == null || _openElements.Contains(entry))
                {
                    i++; // Step forward to first entry needing reconstruction
                    break;
                }
            }

            // Walk forward, creating elements for entries not on the stack
            for (; i < _activeFormattingElements.Count; i++)
            {
                var entry = _activeFormattingElements[i];
                if (entry == null) continue;

                // Create a new element with the same identity and parser metadata.
                var newElement = CloneElementForTreeConstruction(entry);
                InsertNodeAtAppropriatePlace(newElement);
                _openElements.Push(newElement);
                _activeFormattingElements[i] = newElement;
            }
        }

        /// <summary>
        /// Full Adoption Agency Algorithm per WHATWG HTML spec Â§13.2.6.4.7.
        /// Handles misnested formatting tags like &lt;b&gt;&lt;i&gt;&lt;/b&gt;&lt;/i&gt;.
        /// </summary>
        private void RunAdoptionAgencyAlgorithm(string subject)
        {
            // Step 1: If current node is an HTML element whose tag name is subject,
            // and it is NOT in the active formatting list, pop it and return.
            var currentEl = CurrentNode as Element;
            if (currentEl != null &&
                string.Equals(currentEl.LocalName, subject, StringComparison.OrdinalIgnoreCase) &&
                !_activeFormattingElements.Contains(currentEl))
            {
                SafePopOpenElement();
                return;
            }

            // Step 2: Outer loop (max 8 iterations)
            for (int outerLoop = 0; outerLoop < 8; outerLoop++)
            {
                // Step 3: Find formatting element â€” last in active formatting with subject tag
                Element formattingElement = null;
                int formattingIndex = -1;
                for (int i = _activeFormattingElements.Count - 1; i >= 0; i--)
                {
                    var entry = _activeFormattingElements[i];
                    if (entry == null) break; // Stop at marker
                    if (string.Equals(entry.LocalName, subject, StringComparison.OrdinalIgnoreCase))
                    {
                        formattingElement = entry;
                        formattingIndex = i;
                        break;
                    }
                }

                // Step 4: If no formatting element found, fall through to "any other end tag"
                if (formattingElement == null)
                {
                    if (StackHas(subject))
                    {
                        GenerateImpliedEndTags(subject);
                        PopUntil(subject);
                    }
                    return;
                }

                // Step 5: If formatting element is not on the stack, remove from active formatting, return
                if (!_openElements.Contains(formattingElement))
                {
                    _activeFormattingElements.RemoveAt(formattingIndex);
                    return;
                }

                // Step 6: If formatting element is not in scope, return (parse error)
                if (!ElementIsInScope(formattingElement))
                {
                    return;
                }

                // Step 7-8: Find furthest block
                // Walk the stack from formatting element toward the top, find first special element
                Element furthestBlock = null;
                int formattingStackIndex = -1;
                var stackArray = _openElements.ToArray(); // Index 0 = top of stack

                for (int i = stackArray.Length - 1; i >= 0; i--)
                {
                    if (stackArray[i] == formattingElement)
                    {
                        formattingStackIndex = i;
                        break;
                    }
                }

                if (formattingStackIndex < 0)
                {
                    _activeFormattingElements.RemoveAt(formattingIndex);
                    return;
                }

                // Search from formattingElement toward top of stack (decreasing index)
                for (int i = formattingStackIndex - 1; i >= 0; i--)
                {
                    if (IsSpecialElement(stackArray[i].LocalName))
                    {
                        furthestBlock = stackArray[i];
                        break;
                    }
                }

                // Step 9: If no furthest block, pop until formatting element, remove from active formatting
                if (furthestBlock == null)
                {
                    while (_openElements.Count > 0)
                    {
                        var popped = SafePopOpenElement();
                        if (popped == formattingElement) break;
                    }
                    _activeFormattingElements.Remove(formattingElement);
                    return;
                }

                // Step 10: Common ancestor is the element above formatting element on stack
                int commonAncestorIndex = formattingStackIndex + 1;
                Element commonAncestor = commonAncestorIndex < stackArray.Length ? stackArray[commonAncestorIndex] : null;
                if (commonAncestor == null) commonAncestor = _openElements.Last(); // html element

                // Step 11: Bookmark = formattingIndex in active formatting list
                int bookmark = formattingIndex;

                // Step 12: Inner loop
                Element node = furthestBlock;
                Element lastNode = furthestBlock;
                int innerLoopCounter = 0;

                // Walk from furthestBlock toward formattingElement on the stack
                int nodeStackIndex = -1;
                for (int i = 0; i < stackArray.Length; i++)
                {
                    if (stackArray[i] == furthestBlock)
                    {
                        nodeStackIndex = i;
                        break;
                    }
                }

                while (true)
                {
                    innerLoopCounter++;
                    // Move to the element one below node on the stack (toward formatting element)
                    nodeStackIndex++;
                    if (nodeStackIndex >= stackArray.Length) break;
                    node = stackArray[nodeStackIndex];

                    if (node == formattingElement) break;

                    // If inner loop counter > 3 and node is in active formatting, remove it
                    int nodeActiveIndex = _activeFormattingElements.IndexOf(node);
                    if (innerLoopCounter > 3 && nodeActiveIndex >= 0)
                    {
                        _activeFormattingElements.RemoveAt(nodeActiveIndex);
                        if (nodeActiveIndex < bookmark) bookmark--;
                        nodeActiveIndex = -1; // Removed
                    }

                    // If node is not in active formatting list, remove from stack and continue
                    if (nodeActiveIndex < 0)
                    {
                        // Remove from stack â€” rebuild stack without this node
                        var newStack = new Stack<Element>();
                        foreach (var el in _openElements.Reverse())
                        {
                            if (el != node) newStack.Push(el);
                        }
                        _openElements.Clear();
                        foreach (var el in newStack.Reverse())
                        {
                            _openElements.Push(el);
                        }
                        // Refresh stackArray
                        stackArray = _openElements.ToArray();
                        nodeStackIndex--; // Adjust since we removed an element
                        continue;
                    }

                    // Create replacement element
                    var replacement = CloneElementForTreeConstruction(node);

                    // Replace in active formatting list
                    _activeFormattingElements[nodeActiveIndex] = replacement;

                    // Replace on stack
                    var rebuildStack = new Stack<Element>();
                    foreach (var el in _openElements.Reverse())
                    {
                        rebuildStack.Push(el == node ? replacement : el);
                    }
                    _openElements.Clear();
                    foreach (var el in rebuildStack.Reverse())
                    {
                        _openElements.Push(el);
                    }
                    stackArray = _openElements.ToArray();

                    // If this is the first replacement, move the bookmark after it.
                    if (lastNode == furthestBlock)
                    {
                        bookmark = nodeActiveIndex + 1;
                    }

                    node = replacement;

                    // Reparent: detach lastNode from its parent and append to node
                    (lastNode.ParentNode as ContainerNode)?.RemoveChild(lastNode);
                    node.AppendChild(lastNode);
                    lastNode = node;
                }

                // Step 13: Insert lastNode at appropriate place
                // Detach lastNode from its parent
                (lastNode.ParentNode as ContainerNode)?.RemoveChild(lastNode);

                // If common ancestor is table/tbody/tfoot/thead/tr, foster parent
                string caTag = commonAncestor.LocalName;
                if (caTag == "table" || caTag == "tbody" || caTag == "tfoot" || caTag == "thead" || caTag == "tr")
                {
                    InsertAtFosterParent(lastNode);
                }
                else
                {
                    commonAncestor.AppendChild(lastNode);
                }

                // Step 14: Create new element for formatting element
                var newFormatting = CloneElementForTreeConstruction(formattingElement);

                // Step 15: Move children of furthest block to new formatting element
                while (furthestBlock.FirstChild != null)
                {
                    var child = furthestBlock.FirstChild;
                    furthestBlock.RemoveChild(child);
                    newFormatting.AppendChild(child);
                }

                // Step 16: Append new formatting element to furthest block
                furthestBlock.AppendChild(newFormatting);

                // Step 17: Remove old formatting element from active formatting list,
                // insert new one at bookmark position
                _activeFormattingElements.Remove(formattingElement);
                if (bookmark > _activeFormattingElements.Count) bookmark = _activeFormattingElements.Count;
                _activeFormattingElements.Insert(bookmark, newFormatting);

                // Step 18: Remove old formatting element from stack,
                // insert new one after furthest block
                var finalStack = new Stack<Element>();
                bool inserted = false;
                foreach (var el in _openElements.Reverse())
                {
                    if (el == formattingElement) continue; // Remove old
                    finalStack.Push(el);
                    if (el == furthestBlock && !inserted)
                    {
                        finalStack.Push(newFormatting); // Insert after furthest block
                        inserted = true;
                    }
                }
                _openElements.Clear();
                foreach (var el in finalStack.Reverse())
                {
                    _openElements.Push(el);
                }
            }
        }

        private Element CloneElementForTreeConstruction(Element source)
        {
            var owner = source.OwnerDocument ?? _document;
            var clone = owner.CreateElementForParser(source.LocalName, source.NamespaceUri ?? Namespaces.Html);
            clone.Prefix = source.Prefix;
            clone.SourceOffset = source.SourceOffset;
            clone.SourceLine = source.SourceLine;
            clone.SourceColumn = source.SourceColumn;
            foreach (var attribute in source.Attributes)
            {
                clone.SetAttributeUnsafe(attribute.Name, attribute.Value);
            }
            return clone;
        }

        private void InsertAtFosterParent(Node node)
        {
            var (parent, before) = GetFosterParentInsertionLocation();
            if (before != null)
                parent.InsertBefore(node, before);
            else
                parent.AppendChild(node);
        }

        private (ContainerNode Parent, Node Before) GetFosterParentInsertionLocation()
        {
            var stack = _openElements.ToArray();
            var tableIndex = Array.FindIndex(
                stack,
                element => string.Equals(element.LocalName, "table", StringComparison.OrdinalIgnoreCase));
            var templateIndex = Array.FindIndex(
                stack,
                element => string.Equals(element.LocalName, "template", StringComparison.OrdinalIgnoreCase));

            if (templateIndex >= 0 &&
                (tableIndex < 0 || templateIndex == tableIndex + 1))
            {
                var template = (HtmlTemplateElement)stack[templateIndex];
                return (template.Content, null);
            }

            if (tableIndex < 0)
            {
                var firstOpenElement = stack.LastOrDefault();
                return (firstOpenElement is HtmlTemplateElement rootTemplate
                    ? rootTemplate.Content
                    : firstOpenElement ?? CurrentInsertionTarget, null);
            }

            var table = stack[tableIndex];
            if (table.ParentNode is ContainerNode tableParent)
            {
                return (tableParent, table);
            }

            var previous = tableIndex + 1 < stack.Length ? stack[tableIndex + 1] : null;
            return (previous is HtmlTemplateElement previousTemplate
                ? previousTemplate.Content
                : previous ?? CurrentInsertionTarget, null);
        }

        /// <summary>
        /// Check if an element is "in scope" per WHATWG Â§13.2.4.2.
        /// </summary>
        private bool ElementIsInScope(Element target)
        {
            foreach (var el in _openElements)
            {
                if (el == target) return true;
                // SVG adjustment preserves camelCase (e.g. foreignObject), so
                // normalize before matching the lowercase boundary list.
                string tag = el.LocalName?.ToLowerInvariant();
                // Scope boundary elements
                if (tag == "applet" || tag == "caption" || tag == "html" ||
                    tag == "table" || tag == "td" || tag == "th" ||
                    tag == "marquee" || tag == "object" || tag == "template" ||
                    tag == "mi" || tag == "mo" || tag == "mn" || tag == "ms" ||
                    tag == "mtext" || tag == "annotation-xml" ||
                    tag == "foreignobject" || tag == "desc" || tag == "title")
                    return false;
            }
            return false;
        }

        /// <summary>
        /// HTML5 "special" category elements.  Used by the <li>/<dd>/<dt> start-tag
        /// loop to decide when to stop walking backwards through the open-elements stack.
        /// </summary>
        private static bool IsSpecialElement(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            switch (tag)
            {
                case "address": case "applet": case "area": case "article": case "aside":
                case "base": case "basefont": case "bgsound": case "blockquote": case "body":
                case "br": case "button": case "caption": case "center": case "col":
                case "colgroup": case "dd": case "details": case "dialog": case "dir":
                case "div": case "dl": case "dt": case "embed": case "fieldset":
                case "figcaption": case "figure": case "footer": case "form": case "frame":
                case "frameset": case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                case "head": case "header": case "hgroup": case "hr": case "html":
                case "iframe": case "img": case "input": case "keygen": case "li":
                case "link": case "listing": case "main": case "marquee": case "menu":
                case "meta": case "nav": case "noembed": case "noframes": case "noscript":
                case "object": case "ol": case "p": case "param": case "plaintext":
                case "pre": case "script": case "section": case "select": case "source":
                case "style": case "summary": case "table": case "tbody": case "td":
                case "template": case "textarea": case "tfoot": case "th": case "thead":
                case "title": case "tr": case "track": case "ul": case "wbr": case "xmp":
                    return true;
                default:
                    return false;
            }
        }
        
        private bool StackHas(string tagName)
        {
            foreach (var element in _openElements)
            {
                if (string.Equals(element.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool HasElementInButtonScope(string tagName)
        {
            foreach (var element in _openElements)
            {
                if (string.Equals(element.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                    return true;

                // SVG adjustment preserves camelCase (e.g. foreignObject), so
                // normalize before matching the lowercase boundary list.
                switch (element.LocalName?.ToLowerInvariant())
                {
                    case "applet":
                    case "caption":
                    case "html":
                    case "table":
                    case "td":
                    case "th":
                    case "marquee":
                    case "object":
                    case "template":
                    case "button":
                    case "mi":
                    case "mo":
                    case "mn":
                    case "ms":
                    case "mtext":
                    case "annotation-xml":
                    case "foreignobject":
                    case "desc":
                    case "title":
                        return false;
                }
            }

            return false;
        }
        
        private void PopUntil(string tagName)
        {
            var targetFound = StackHas(tagName);
            // EngineLogCompat.Debug($"[Parser] PopUntil({tagName}). Target in stack: {targetFound}. Current top: {(_openElements.Count > 0 ? _openElements.Peek().TagName : "NULL")}", LogCategory.HtmlParsing);

            if (targetFound)
            {
                while (_openElements.Count > 1)
                {
                    var popped = SafePopOpenElement();
                    if (string.Equals(popped.TagName, tagName, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }
        
        // RCDATA / RAWTEXT helpers
        private void InsertGenericRCDATAElement(StartTagToken token)
        {
            InsertHtmlElement(token);
            _tokenizer.SetState(HtmlTokenizer.TokenizerState.RcData);
            _tokenizer.LastStartTagName = token.TagName?.ToLowerInvariant();
            _originalInsertionMode = _insertionMode;
            SwitchTo(InsertionMode.Text);
        }
        
        private void InsertGenericRawTextElement(StartTagToken token)
        {
            InsertHtmlElement(token);
            _tokenizer.SetState(HtmlTokenizer.TokenizerState.RawText);
            _tokenizer.LastStartTagName = token.TagName?.ToLowerInvariant();
            _originalInsertionMode = _insertionMode;
            SwitchTo(InsertionMode.Text);
        }

        internal static QuirksMode DetermineQuirksMode(DoctypeToken dt)
        {
            if (dt == null) return QuirksMode.Quirks;
            if (dt.ForceQuirks) return QuirksMode.Quirks;

            var name = (dt.Name ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.Equals(name, "html", StringComparison.Ordinal))
                return QuirksMode.Quirks;

            var publicId = (dt.PublicIdentifier ?? string.Empty).Trim().ToLowerInvariant();
            var systemId = (dt.SystemIdentifier ?? string.Empty).Trim().ToLowerInvariant();

            if (publicId.StartsWith("+//silmaril//dtd html pro v0r11 19970101//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//advasoft ltd//dtd html 3.0 aswedit + extensions//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//as//dtd html 3.0 aswedit + extensions//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.0 level 1//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.0 level 2//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.0 strict level 1//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.0 strict level 2//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.0 strict//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 2.1e//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 3.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 3.2 final//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 3.2//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html 3//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html level 0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html level 1//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html level 2//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html level 3//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html strict level 0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html strict level 1//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html strict level 2//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html strict level 3//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html strict//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//ietf//dtd html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//metrius//dtd metrius presentational//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer 2.0 html strict//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer 2.0 html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer 2.0 tables//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer 3.0 html strict//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer 3.0 html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//microsoft//dtd internet explorer 3.0 tables//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//netscape comm. corp.//dtd html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//netscape comm. corp.//dtd strict html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//o'reilly and associates//dtd html 2.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//o'reilly and associates//dtd html extended 1.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//o'reilly and associates//dtd html extended relaxed 1.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//sq//dtd html 2.0 hotmetal + extensions//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//sun microsystems corp.//dtd hotjava html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//sun microsystems corp.//dtd hotjava strict html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 3 1995-03-24//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 3.2 draft//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 3.2 final//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 3.2//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 3.2s draft//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 4.0 frameset//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html 4.0 transitional//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html experimental 19960712//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd html experimental 970421//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3c//dtd w3 html//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//w3o//dtd w3 html 3.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//webtechs//dtd mozilla html 2.0//", StringComparison.Ordinal) ||
                publicId.StartsWith("-//webtechs//dtd mozilla html//", StringComparison.Ordinal))
            {
                return QuirksMode.Quirks;
            }

            if ((publicId.StartsWith("-//w3c//dtd xhtml 1.0 frameset//", StringComparison.Ordinal) ||
                 publicId.StartsWith("-//w3c//dtd xhtml 1.0 transitional//", StringComparison.Ordinal)) ||
                ((publicId.StartsWith("-//w3c//dtd html 4.01 frameset//", StringComparison.Ordinal) ||
                  publicId.StartsWith("-//w3c//dtd html 4.01 transitional//", StringComparison.Ordinal)) &&
                 string.IsNullOrEmpty(systemId)))
            {
                return QuirksMode.LimitedQuirks;
            }

            return QuirksMode.NoQuirks;
        }

        private void ApplyMetaCharset(StartTagToken st)
        {
            if (st == null || !st.HasAttributes) return;

            string charset = null;
            string httpEquiv = null;
            string content = null;

            foreach (var attr in st.Attributes)
            {
                var name = attr.Name?.Trim().ToLowerInvariant();
                if (name == "charset")
                    charset = attr.Value?.Trim();
                else if (name == "http-equiv")
                    httpEquiv = attr.Value?.Trim();
                else if (name == "content")
                    content = attr.Value;
            }

            if (string.IsNullOrWhiteSpace(charset) &&
                string.Equals(httpEquiv, "content-type", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(content))
            {
                var idx = content.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var eq = content.IndexOf('=', idx);
                    if (eq >= 0 && eq + 1 < content.Length)
                    {
                        charset = content[(eq + 1)..].Trim().Trim('"', '\'', ';');
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(charset))
                _document.CharacterSet = charset;
        }
    }
}





