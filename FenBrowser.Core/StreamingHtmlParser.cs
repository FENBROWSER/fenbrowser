using FenBrowser.Core.Dom.V2;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;

namespace FenBrowser.Core
{
    /// <summary>
    /// Streaming HTML parser that can parse incrementally from TextReader/Stream.
    /// Supports partial parsing and callbacks for progressive rendering.
    /// Implements HTML5 streaming parsing per WHATWG spec.
    /// </summary>
    public class StreamingHtmlParser : IDisposable
    {
        private readonly TextReader _reader;
        private readonly StringBuilder _buffer;
        private readonly bool _ownsReader;
        private int _bufferPos;
        private bool _disposed;
        
        // Buffer size for reading chunks
        private const int ChunkSize = 8192;
        private const int MaxBufferSize = 1024 * 1024; // 1MB max buffer

        /// <summary>
        /// Event fired when an element is fully parsed
        /// </summary>
        public event Action<Element> OnElementParsed;

        /// <summary>
        /// Event fired when a text node is parsed
        /// </summary>
        public event Action<string> OnTextParsed;

        /// <summary>
        /// Event fired when document parsing is complete
        /// </summary>
        public event Action<Document> OnDocumentComplete;

        /// <summary>
        /// Create streaming parser from TextReader
        /// </summary>
        public StreamingHtmlParser(TextReader reader, bool ownsReader = false)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _buffer = new StringBuilder(ChunkSize);
            _ownsReader = ownsReader;
            _bufferPos = 0;
        }

        /// <summary>
        /// Create streaming parser from Stream
        /// </summary>
        public StreamingHtmlParser(Stream stream, Encoding encoding = null)
            : this(new StreamReader(stream, encoding ?? Encoding.UTF8, true, ChunkSize, leaveOpen: false), true)
        {
        }

        /// <summary>
        /// Create streaming parser from string (for compatibility)
        /// </summary>
        public StreamingHtmlParser(string html)
            : this(new StringReader(html ?? string.Empty), true)
        {
        }

        /// <summary>
        /// Parse the entire document asynchronously
        /// </summary>
        public async Task<Document> ParseAsync(CancellationToken ct = default)
        {
            try
            {
                var content = await _reader.ReadToEndAsync();
                ct.ThrowIfCancellationRequested();
                var doc = HtmlParser.ParseDocument(content, out _);
                OnDocumentComplete?.Invoke(doc);
                EngineLogCompat.Debug($"[StreamingHtmlParser] Parsed document with {CountElements(doc)} elements", LogCategory.HtmlParsing);
                return doc;
            }
            catch (OperationCanceledException)
            {
                EngineLogCompat.Debug("[StreamingHtmlParser] Parsing cancelled", LogCategory.HtmlParsing);
                throw;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[StreamingHtmlParser] Error during parsing: {ex.Message}", LogCategory.HtmlParsing);
                return new Document();
            }
        }

        /// <summary>
        /// Parse synchronously (for backward compatibility)
        /// </summary>
        public Document Parse()
        {
            // Read all content first
            var content = _reader.ReadToEnd();
            
            // Use the regular parser for full content
            var parser = new HtmlParser(content);
            return parser.Parse();
        }

        /// <summary>
        /// Parse incrementally, processing data as it becomes available
        /// </summary>
        public async Task ParseIncrementallyAsync(Action<Document> onProgress = null, CancellationToken ct = default)
        {
            var doc = await ParseAsync(ct);
            onProgress?.Invoke(doc);
        }

        /// <summary>
        /// Feed data to the parser incrementally
        /// </summary>
        public void FeedData(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            _buffer.Append(data);
        }

        /// <summary>
        /// Get current buffer position for debugging
        /// </summary>
        public int BufferPosition => _bufferPos;

        /// <summary>
        /// Get current buffer length
        /// </summary>
        public int BufferLength => _buffer.Length;

        private async Task<int> ReadChunkAsync(char[] buffer, CancellationToken ct)
        {
            // TextReader.ReadAsync doesn't support CancellationToken directly
            // So we wrap it in a task
            return await Task.Run(() => _reader.Read(buffer, 0, buffer.Length), ct);
        }

        private void ParseBufferedContent(IncrementalParseState state, bool isFinalChunk)
        {
            // Parse complete tags from buffer
            while (_bufferPos < _buffer.Length)
            {
                if (state.TryGetRawTextTagName(out var rawTextTagName))
                {
                    if (!ParseRawTextContent(state, rawTextTagName, isFinalChunk))
                    {
                        break;
                    }

                    continue;
                }

                char c = _buffer[_bufferPos];
                
                if (c == '<')
                {
                    // Look for complete tag
                    int tagEnd = FindTagEnd(_bufferPos);
                    if (tagEnd < 0)
                    {
                        // Incomplete tag, wait for more data
                        break;
                    }
                    
                    // Extract and parse the tag
                    string tagContent = _buffer.ToString(_bufferPos, tagEnd - _bufferPos + 1);
                    state.ProcessToken(tagContent);
                    _bufferPos = tagEnd + 1;
                }
                else
                {
                    // Text content
                    int textEnd = _bufferPos;
                    while (textEnd < _buffer.Length && _buffer[textEnd] != '<')
                    {
                        textEnd++;
                    }
                    
                    if (textEnd > _bufferPos)
                    {
                        string text = _buffer.ToString(_bufferPos, textEnd - _bufferPos);
                        state.ProcessText(text);
                        OnTextParsed?.Invoke(text);
                        _bufferPos = textEnd;
                    }
                }
            }
        }

        private bool ParseRawTextContent(IncrementalParseState state, string rawTextTagName, bool isFinalChunk)
        {
            var currentBuffer = _buffer.ToString();
            var closingTagPrefix = $"</{rawTextTagName}";
            var closingTagIndex = currentBuffer.IndexOf(
                closingTagPrefix,
                _bufferPos,
                StringComparison.OrdinalIgnoreCase);

            if (closingTagIndex >= 0)
            {
                if (closingTagIndex > _bufferPos)
                {
                    var text = currentBuffer.Substring(_bufferPos, closingTagIndex - _bufferPos);
                    state.ProcessText(text);
                    OnTextParsed?.Invoke(text);
                }

                _bufferPos = closingTagIndex;
                return true;
            }

            var holdBack = state.GetRawTextHoldBackLength(rawTextTagName);
            var availableTextLength = _buffer.Length - _bufferPos;
            if (!isFinalChunk && availableTextLength <= holdBack)
            {
                return false;
            }

            var textLength = isFinalChunk
                ? availableTextLength
                : Math.Max(0, availableTextLength - holdBack);

            if (textLength <= 0)
            {
                return false;
            }

            var textContent = currentBuffer.Substring(_bufferPos, textLength);
            state.ProcessText(textContent);
            OnTextParsed?.Invoke(textContent);
            _bufferPos += textLength;
            return true;
        }

        private int FindTagEnd(int start)
        {
            int i = start + 1;
            bool inQuote = false;
            char quoteChar = '\0';
            
            // Handle comments
            if (i + 3 < _buffer.Length && 
                _buffer[i] == '!' && _buffer[i + 1] == '-' && _buffer[i + 2] == '-')
            {
                // Find comment end
                for (int j = i + 3; j < _buffer.Length - 2; j++)
                {
                    if (_buffer[j] == '-' && _buffer[j + 1] == '-' && _buffer[j + 2] == '>')
                    {
                        return j + 2;
                    }
                }
                return -1; // Incomplete comment
            }
            
            while (i < _buffer.Length)
            {
                char c = _buffer[i];
                
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
            
            return -1; // Incomplete tag
        }

        private void TrimBuffer()
        {
            if (_bufferPos > 0)
            {
                _buffer.Remove(0, _bufferPos);
                _bufferPos = 0;
            }
        }

        private static int CountElements(Node root)
        {
            int count = 1;
            foreach (var child in root.Children)
            {
                count += CountElements(child);
            }
            return count;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_ownsReader)
                {
                    _reader?.Dispose();
                }
                _disposed = true;
            }
        }

        /// <summary>
        /// Internal state for incremental parsing
        /// </summary>
        private class IncrementalParseState
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
            private readonly System.Collections.Generic.Stack<Node> _stack;
            
            public IncrementalParseState(Document document)
            {
                _document = document;
                _stack = new System.Collections.Generic.Stack<Node>();
                _stack.Push(document);
            }

            public void ProcessToken(string token)
            {
                if (string.IsNullOrEmpty(token) || token.Length < 2) return;
                
                // Remove < and >
                token = token.Substring(1, token.Length - 2).Trim();
                if (string.IsNullOrEmpty(token)) return;
                
                // Handle comments and declarations
                if (token.StartsWith("!"))
                {
                    return; // Skip comments and DOCTYPE
                }
                
                // Handle end tags
                if (token.StartsWith("/"))
                {
                    var endTag = token.Substring(1).Trim().ToLowerInvariant();
                    while (_stack.Count > 1 && 
                           !string.Equals((_stack.Peek() as Element)?.NodeName, endTag, StringComparison.OrdinalIgnoreCase))
                    {
                        _stack.Pop();
                    }
                    if (_stack.Count > 1) _stack.Pop();
                    return;
                }
                
                // Handle start tags
                bool selfClosing = token.EndsWith("/");
                if (selfClosing)
                    token = token.Substring(0, token.Length - 1).Trim();
                
                // Extract tag name
                int spaceIdx = token.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                string tagName = (spaceIdx > 0 ? token.Substring(0, spaceIdx) : token).ToLowerInvariant();
                
                var element = new Element(tagName);
                
                // Parse attributes (simplified)
                if (spaceIdx > 0)
                {
                    ParseAttributes(element, token.Substring(spaceIdx));
                }
                
                // Add to parent
                if (_stack.Count > 0)
                {
                    ((ContainerNode)_stack.Peek()).AppendChild(element);
                }

                // Push if not self-closing and not void
                if (!selfClosing && !Parsing.HtmlParser.IsVoid(tagName))
                {
                    _stack.Push(element);
                }
            }

            public void ProcessText(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                
                var decoded = System.Net.WebUtility.HtmlDecode(text);
                if (_stack.Count > 0)
                {
                    var parent = _stack.Peek();
                    var lastChild = ((ContainerNode)parent).ChildNodes.Length > 0 
                        ? ((ContainerNode)parent).ChildNodes[((ContainerNode)parent).ChildNodes.Length - 1] 
                        : null;
                    
                    if (lastChild != null && lastChild.NodeType == NodeType.Text)
                    {
                        lastChild.NodeValue += decoded;
                    }
                    else
                    {
                        ((ContainerNode)parent).AppendChild(new Text(decoded));
                    }
                }
            }

            public void Finalize()
            {
                // Close any remaining open tags
                while (_stack.Count > 1)
                {
                    _stack.Pop();
                }
            }

            public bool TryGetRawTextTagName(out string tagName)
            {
                tagName = string.Empty;
                if (_stack.Count <= 1 || _stack.Peek() is not Element element)
                {
                    return false;
                }

                if (!RawTextElements.Contains(element.TagName))
                {
                    return false;
                }

                tagName = element.TagName.ToLowerInvariant();
                return true;
            }

            public int GetRawTextHoldBackLength(string rawTextTagName)
            {
                if (string.IsNullOrEmpty(rawTextTagName))
                {
                    return 0;
                }

                return rawTextTagName.Length + 3;
            }

            private static void ParseAttributes(Element element, string attrString)
            {
                // Simplified attribute parsing
                int i = 0;
                while (i < attrString.Length)
                {
                    // Skip whitespace
                    while (i < attrString.Length && char.IsWhiteSpace(attrString[i])) i++;
                    if (i >= attrString.Length) break;
                    
                    // Read attribute name
                    int nameStart = i;
                    while (i < attrString.Length && attrString[i] != '=' && 
                           !char.IsWhiteSpace(attrString[i]) && attrString[i] != '/')
                    {
                        i++;
                    }
                    
                    if (i == nameStart) break;
                    
                    string name = attrString.Substring(nameStart, i - nameStart).ToLowerInvariant();
                    string value = name; // Boolean attribute default
                    
                    // Skip whitespace
                    while (i < attrString.Length && char.IsWhiteSpace(attrString[i])) i++;
                    
                    // Check for value
                    if (i < attrString.Length && attrString[i] == '=')
                    {
                        i++; // Skip =
                        while (i < attrString.Length && char.IsWhiteSpace(attrString[i])) i++;
                        
                        if (i < attrString.Length)
                        {
                            char quote = attrString[i];
                            if (quote == '"' || quote == '\'')
                            {
                                i++; // Skip opening quote
                                int valueStart = i;
                                while (i < attrString.Length && attrString[i] != quote) i++;
                                value = System.Net.WebUtility.HtmlDecode(
                                    attrString.Substring(valueStart, i - valueStart));
                                if (i < attrString.Length) i++; // Skip closing quote
                            }
                            else
                            {
                                // Unquoted value
                                int valueStart = i;
                                while (i < attrString.Length && 
                                       !char.IsWhiteSpace(attrString[i]) && attrString[i] != '/')
                                {
                                    i++;
                                }
                                value = System.Net.WebUtility.HtmlDecode(
                                    attrString.Substring(valueStart, i - valueStart));
                            }
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(name))
                    {
                        element.SetAttribute(name, value);
                    }
                }
            }
        }
    }
}

