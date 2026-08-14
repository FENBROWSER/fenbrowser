using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Core
{
    /// <summary>
    /// Incremental HTML parsing surface used for progressive browser parsing.
    /// The full-document Parse/ParseAsync paths delegate to the canonical HtmlParser;
    /// the incremental path preserves chunk state and handles HTML text/raw-text
    /// boundaries without corrupting script/style source.
    /// </summary>
    public class StreamingHtmlParser : IDisposable
    {
        private readonly TextReader _reader;
        private readonly StringBuilder _buffer;
        private readonly bool _ownsReader;
        private int _bufferPos;
        private bool _disposed;

        private const int ChunkSize = 8192;
        private const int MaxBufferSize = 1024 * 1024;

#pragma warning disable CS0067
        public event Action<Element> OnElementParsed;
#pragma warning restore CS0067
        public event Action<string> OnTextParsed;
        public event Action<Document> OnDocumentComplete;

        public StreamingHtmlParser(TextReader reader, bool ownsReader = false)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _buffer = new StringBuilder(ChunkSize);
            _ownsReader = ownsReader;
        }

        public StreamingHtmlParser(Stream stream, Encoding encoding = null)
            : this(new StreamReader(stream, encoding ?? Encoding.UTF8, true, ChunkSize, leaveOpen: false), true)
        {
        }

        public StreamingHtmlParser(string html)
            : this(new StringReader(html ?? string.Empty), true)
        {
        }

        public async Task<Document> ParseAsync(CancellationToken ct = default)
        {
            try
            {
                var content = await _reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var doc = HtmlParser.ParseDocument(content, out _);
                OnDocumentComplete?.Invoke(doc);
                EngineLogCompat.Debug(
                    $"[StreamingHtmlParser] Parsed document with {CountElements(doc)} elements",
                    LogCategory.HtmlParsing);
                return doc;
            }
            catch (OperationCanceledException)
            {
                EngineLogCompat.Debug("[StreamingHtmlParser] Parsing cancelled", LogCategory.HtmlParsing);
                throw;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error(
                    $"[StreamingHtmlParser] Error during parsing: {ex.Message}",
                    LogCategory.HtmlParsing);
                throw new InvalidOperationException("Streaming HTML parsing failed.", ex);
            }
        }

        public Document Parse()
        {
            var content = _reader.ReadToEnd();
            using var reader = new StringReader(content);
            return HtmlParser.ParseStream(reader, options: null, out _);
        }

        public async Task ParseIncrementallyAsync(
            Action<Document> onProgress = null,
            CancellationToken ct = default)
        {
            var document = new Document();
            var state = new IncrementalParseState(document);
            var chunk = new char[ChunkSize];

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await ReadChunkAsync(chunk, ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                _buffer.Append(chunk, 0, read);
                ParseBufferedContent(state, isFinalChunk: false);
                TrimBuffer();

                if (_buffer.Length > MaxBufferSize)
                {
                    throw new InvalidOperationException(
                        $"Streaming parser buffer exceeded {MaxBufferSize} characters while waiting for a complete token.");
                }

                onProgress?.Invoke(document);
            }

            ParseBufferedContent(state, isFinalChunk: true);
            state.Finish();
            TrimBuffer();

            onProgress?.Invoke(document);
            OnDocumentComplete?.Invoke(document);
        }

        public void FeedData(string data)
        {
            if (!string.IsNullOrEmpty(data))
                _buffer.Append(data);
        }

        public int BufferPosition => _bufferPos;
        public int BufferLength => _buffer.Length;

        private ValueTask<int> ReadChunkAsync(char[] buffer, CancellationToken ct)
        {
            return _reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        }

        private void ParseBufferedContent(IncrementalParseState state, bool isFinalChunk)
        {
            while (_bufferPos < _buffer.Length)
            {
                if (state.TryGetRawTextTagName(out var rawTextTagName))
                {
                    if (!ParseRawTextContent(state, rawTextTagName, isFinalChunk))
                        break;
                    continue;
                }

                var c = _buffer[_bufferPos];
                if (c == '<')
                {
                    var tagEnd = FindTagEnd(_bufferPos);
                    if (tagEnd < 0)
                        break;

                    var tagContent = _buffer.ToString(_bufferPos, tagEnd - _bufferPos + 1);
                    state.ProcessToken(tagContent);
                    _bufferPos = tagEnd + 1;
                }
                else
                {
                    var textEnd = _bufferPos;
                    while (textEnd < _buffer.Length && _buffer[textEnd] != '<')
                        textEnd++;

                    if (textEnd > _bufferPos)
                    {
                        var text = _buffer.ToString(_bufferPos, textEnd - _bufferPos);
                        state.ProcessText(text, decodeCharacterReferences: true);
                        OnTextParsed?.Invoke(text);
                        _bufferPos = textEnd;
                    }
                }
            }
        }

        private bool ParseRawTextContent(
            IncrementalParseState state,
            string rawTextTagName,
            bool isFinalChunk)
        {
            var availableTextLength = _buffer.Length - _bufferPos;
            if (availableTextLength <= 0)
                return false;

            if (string.Equals(rawTextTagName, "plaintext", StringComparison.OrdinalIgnoreCase))
            {
                var text = _buffer.ToString(_bufferPos, availableTextLength);
                state.ProcessText(text, decodeCharacterReferences: false);
                OnTextParsed?.Invoke(text);
                _bufferPos += availableTextLength;
                return true;
            }

            var closingTagIndex = FindRawTextClosingTag(_bufferPos, rawTextTagName);
            if (closingTagIndex >= 0)
            {
                if (closingTagIndex > _bufferPos)
                {
                    var text = _buffer.ToString(_bufferPos, closingTagIndex - _bufferPos);
                    state.ProcessText(
                        text,
                        decodeCharacterReferences: state.ShouldDecodeRawTextCharacterReferences(rawTextTagName));
                    OnTextParsed?.Invoke(text);
                }

                // Consume the actual closing tag here. If we merely leave the cursor
                // on '<', the state is still raw-text and the next loop iteration finds
                // the exact same closing tag forever.
                _bufferPos = closingTagIndex;
                var tagEnd = FindTagEnd(_bufferPos);
                if (tagEnd < 0)
                    return false;

                var closingToken = _buffer.ToString(_bufferPos, tagEnd - _bufferPos + 1);
                state.ProcessToken(closingToken);
                _bufferPos = tagEnd + 1;
                return true;
            }

            var holdBack = state.GetRawTextHoldBackLength(rawTextTagName);
            if (!isFinalChunk && availableTextLength <= holdBack)
                return false;

            var textLength = isFinalChunk
                ? availableTextLength
                : Math.Max(0, availableTextLength - holdBack);
            if (textLength <= 0)
                return false;

            var textContent = _buffer.ToString(_bufferPos, textLength);
            state.ProcessText(
                textContent,
                decodeCharacterReferences: state.ShouldDecodeRawTextCharacterReferences(rawTextTagName));
            OnTextParsed?.Invoke(textContent);
            _bufferPos += textLength;
            return true;
        }

        private int FindRawTextClosingTag(int start, string tagName)
        {
            var nameLength = tagName.Length;
            for (var i = start; i + 2 + nameLength <= _buffer.Length; i++)
            {
                if (_buffer[i] != '<' || i + 1 >= _buffer.Length || _buffer[i + 1] != '/')
                    continue;

                var matches = true;
                for (var j = 0; j < nameLength; j++)
                {
                    if (char.ToUpperInvariant(_buffer[i + 2 + j]) != char.ToUpperInvariant(tagName[j]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                    continue;

                var afterName = i + 2 + nameLength;
                if (afterName >= _buffer.Length)
                    return -1;

                var boundary = _buffer[afterName];
                if (boundary == '>' || boundary == '/' || IsHtmlSpace(boundary))
                    return i;
            }

            return -1;
        }

        private int FindTagEnd(int start)
        {
            var i = start + 1;
            var inQuote = false;
            var quoteChar = '\0';

            if (i + 3 < _buffer.Length &&
                _buffer[i] == '!' && _buffer[i + 1] == '-' && _buffer[i + 2] == '-')
            {
                for (var j = i + 3; j < _buffer.Length - 2; j++)
                {
                    if (_buffer[j] == '-' && _buffer[j + 1] == '-' && _buffer[j + 2] == '>')
                        return j + 2;
                }
                return -1;
            }

            while (i < _buffer.Length)
            {
                var c = _buffer[i];
                if (inQuote)
                {
                    if (c == quoteChar)
                        inQuote = false;
                }
                else if (c == '"' || c == '\'')
                {
                    inQuote = true;
                    quoteChar = c;
                }
                else if (c == '>')
                {
                    return i;
                }
                i++;
            }

            return -1;
        }

        private void TrimBuffer()
        {
            if (_bufferPos <= 0)
                return;

            _buffer.Remove(0, _bufferPos);
            _bufferPos = 0;
        }

        private static bool IsHtmlSpace(char c) =>
            c is ' ' or '\t' or '\n' or '\r' or '\f';

        private static int CountElements(Node root)
        {
            var count = 1;
            if (root is ContainerNode container)
            {
                foreach (var child in container.ChildNodes)
                    count += CountElements(child);
            }
            return count;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_ownsReader)
                _reader.Dispose();
            _disposed = true;
        }

        private sealed class IncrementalParseState
        {
            private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
            {
                "script",
                "style",
                "title",
                "textarea",
                "xmp",
                "iframe",
                "noembed",
                "noframes",
                "noscript",
                "plaintext"
            };

            private readonly Document _document;
            private readonly Stack<Node> _stack;

            public IncrementalParseState(Document document)
            {
                _document = document;
                _stack = new Stack<Node>();
                _stack.Push(document);
            }

            public void ProcessToken(string token)
            {
                if (string.IsNullOrEmpty(token) || token.Length < 2)
                    return;

                token = token.Substring(1, token.Length - 2).Trim();
                if (string.IsNullOrEmpty(token))
                    return;

                if (token.StartsWith("!", StringComparison.Ordinal))
                    return;

                if (token.StartsWith("/", StringComparison.Ordinal))
                {
                    var endTagText = token.Substring(1).TrimStart();
                    var end = 0;
                    while (end < endTagText.Length &&
                           !IsHtmlSpace(endTagText[end]) &&
                           endTagText[end] != '/')
                    {
                        end++;
                    }

                    if (end == 0)
                        return;

                    var endTag = endTagText.Substring(0, end).ToLowerInvariant();
                    var hasMatchingOpenElement = false;
                    foreach (var node in _stack)
                    {
                        if (node is Element openElement &&
                            string.Equals(openElement.LocalName, endTag, StringComparison.OrdinalIgnoreCase))
                        {
                            hasMatchingOpenElement = true;
                            break;
                        }
                    }

                    // This incremental stack is intentionally simpler than the full
                    // HTML5 tree builder, but an unmatched end tag must at least be
                    // ignored rather than popping every currently-open element.
                    if (!hasMatchingOpenElement)
                        return;

                    while (_stack.Count > 1 &&
                           !string.Equals(
                               (_stack.Peek() as Element)?.LocalName,
                               endTag,
                               StringComparison.OrdinalIgnoreCase))
                    {
                        _stack.Pop();
                    }

                    if (_stack.Count > 1)
                        _stack.Pop();
                    return;
                }

                var selfClosing = token.EndsWith("/", StringComparison.Ordinal);
                if (selfClosing)
                    token = token.Substring(0, token.Length - 1).Trim();

                var spaceIdx = token.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '\f' });
                var tagName = (spaceIdx > 0 ? token.Substring(0, spaceIdx) : token).ToLowerInvariant();
                if (string.IsNullOrEmpty(tagName))
                    return;

                var element = new Element(tagName, _document);
                if (spaceIdx > 0)
                    ParseAttributes(element, token.Substring(spaceIdx));

                if (_stack.Peek() is ContainerNode parent)
                    parent.AppendChild(element);

                if (!selfClosing && !Parsing.HtmlParser.IsVoid(tagName))
                    _stack.Push(element);
            }

            public void ProcessText(string text, bool decodeCharacterReferences)
            {
                if (string.IsNullOrEmpty(text))
                    return;

                var value = decodeCharacterReferences
                    ? System.Net.WebUtility.HtmlDecode(text)
                    : text;

                if (_stack.Count == 0 || _stack.Peek() is not ContainerNode parent)
                    return;

                var children = parent.ChildNodes;
                var lastChild = children.Length > 0 ? children[children.Length - 1] : null;
                if (lastChild is Text lastText)
                {
                    lastText.Data += value;
                }
                else
                {
                    parent.AppendChild(new Text(value, _document));
                }
            }

            public void Finish()
            {
                while (_stack.Count > 1)
                    _stack.Pop();
            }

            public bool TryGetRawTextTagName(out string tagName)
            {
                tagName = string.Empty;
                if (_stack.Count <= 1 || _stack.Peek() is not Element element)
                    return false;

                if (!RawTextElements.Contains(element.LocalName))
                    return false;

                tagName = element.LocalName.ToLowerInvariant();
                return true;
            }

            public bool ShouldDecodeRawTextCharacterReferences(string tagName)
            {
                return string.Equals(tagName, "title", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(tagName, "textarea", StringComparison.OrdinalIgnoreCase);
            }

            public int GetRawTextHoldBackLength(string rawTextTagName)
            {
                if (string.IsNullOrEmpty(rawTextTagName))
                    return 0;

                return rawTextTagName.Length + 3;
            }

            private static void ParseAttributes(Element element, string attrString)
            {
                var i = 0;
                while (i < attrString.Length)
                {
                    while (i < attrString.Length && IsHtmlSpace(attrString[i]))
                        i++;
                    if (i >= attrString.Length)
                        break;

                    var nameStart = i;
                    while (i < attrString.Length &&
                           attrString[i] != '=' &&
                           !IsHtmlSpace(attrString[i]) &&
                           attrString[i] != '/')
                    {
                        i++;
                    }

                    if (i == nameStart)
                        break;

                    var name = attrString.Substring(nameStart, i - nameStart).ToLowerInvariant();
                    var value = string.Empty;

                    while (i < attrString.Length && IsHtmlSpace(attrString[i]))
                        i++;

                    if (i < attrString.Length && attrString[i] == '=')
                    {
                        i++;
                        while (i < attrString.Length && IsHtmlSpace(attrString[i]))
                            i++;

                        if (i < attrString.Length)
                        {
                            var quote = attrString[i];
                            if (quote == '"' || quote == '\'')
                            {
                                i++;
                                var valueStart = i;
                                while (i < attrString.Length && attrString[i] != quote)
                                    i++;
                                value = System.Net.WebUtility.HtmlDecode(
                                    attrString.Substring(valueStart, i - valueStart));
                                if (i < attrString.Length)
                                    i++;
                            }
                            else
                            {
                                var valueStart = i;
                                while (i < attrString.Length && !IsHtmlSpace(attrString[i]))
                                    i++;
                                value = System.Net.WebUtility.HtmlDecode(
                                    attrString.Substring(valueStart, i - valueStart));
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(name) && !element.HasAttribute(name))
                        element.SetAttribute(name, value);
                }
            }
        }
    }
}
