using System;
using System.IO;
using System.Text;

namespace FenBrowser.Core.Parsing
{
    internal sealed class HtmlTokenizerInput
    {
        private const int ReadChunkSize = 16 * 1024;
        private readonly string _text;
        private readonly TextReader _reader;
        private char[] _buffer;
        private char[] _rawBuffer;
        private int _bufferStart;
        private int _bufferCount;
        private int _totalRead;
        private int _sourceRead;
        private int _sourceLength;
        private bool _pendingCarriageReturn;
        private bool _eof;
        private int _maxLength = int.MaxValue;

        public HtmlTokenizerInput(string text)
        {
            var source = text ?? string.Empty;
            _sourceLength = source.Length;
            _text = NormalizeLineEndings(source);
            _eof = true;
            _totalRead = _text.Length;
        }

        public HtmlTokenizerInput(TextReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _buffer = new char[ReadChunkSize];
            _rawBuffer = new char[ReadChunkSize];
        }

        public bool IsStreaming => _reader != null;
        public bool LimitExceeded { get; private set; }
        public int KnownLength => _eof ? _totalRead : int.MaxValue;
        public int SourceLength => _text != null ? _sourceLength : _sourceRead;

        public int MaxLength
        {
            set
            {
                _maxLength = value > 0 ? value : int.MaxValue;
                if (_text != null)
                {
                    LimitExceeded = _sourceLength > _maxLength;
                }
            }
        }

        public char this[int position]
        {
            get
            {
                if (position < 0) return '\0';
                if (_text != null) return position < _text.Length ? _text[position] : '\0';
                EnsureAvailable(position);
                if (position < _bufferStart || position >= _totalRead) return '\0';
                return _buffer[position - _bufferStart];
            }
        }

        public bool IsEofAt(int position)
        {
            if (_text != null) return position >= _text.Length;
            EnsureAvailable(position);
            return _eof && position >= _totalRead;
        }

        public string Substring(int start, int length)
        {
            if (length <= 0) return string.Empty;
            if (_text != null) return _text.Substring(start, length);
            EnsureAvailable(checked(start + length - 1));
            if (start < _bufferStart || start + length > _totalRead)
            {
                throw new InvalidOperationException("Tokenizer requested input outside its active window.");
            }
            return new string(_buffer, start - _bufferStart, length);
        }

        public void AppendTo(StringBuilder target, int start, int length)
        {
            if (length <= 0) return;
            if (_text != null)
            {
                target.Append(_text, start, length);
                return;
            }

            EnsureAvailable(checked(start + length - 1));
            if (start < _bufferStart || start + length > _totalRead)
            {
                throw new InvalidOperationException("Tokenizer requested input outside its active window.");
            }
            target.Append(_buffer, start - _bufferStart, length);
        }

        public void DiscardBefore(int position)
        {
            if (_text != null || position <= _bufferStart) return;
            var discard = Math.Min(position - _bufferStart, _bufferCount);
            if (discard <= 0) return;
            var remaining = _bufferCount - discard;
            if (remaining > 0) Array.Copy(_buffer, discard, _buffer, 0, remaining);
            _bufferStart += discard;
            _bufferCount = remaining;
        }

        private void EnsureAvailable(int position)
        {
            while (!_eof && position >= _totalRead)
            {
                if (_sourceRead >= _maxLength)
                {
                    CompleteInput(limitReached: true);
                    break;
                }

                var readSize = Math.Min(ReadChunkSize, _maxLength - _sourceRead);
                var read = _reader.Read(_rawBuffer, 0, readSize);
                if (read <= 0)
                {
                    CompleteInput(limitReached: false);
                    break;
                }

                _sourceRead += read;
                AppendNormalized(_rawBuffer, read);
            }
        }

        private void AppendNormalized(char[] source, int length)
        {
            EnsureCapacity(_bufferCount + length + 1);
            var sourceIndex = 0;

            if (_pendingCarriageReturn)
            {
                _buffer[_bufferCount++] = '\n';
                _totalRead++;
                _pendingCarriageReturn = false;
                if (length > 0 && source[0] == '\n')
                {
                    sourceIndex = 1;
                }
            }

            for (; sourceIndex < length; sourceIndex++)
            {
                var ch = source[sourceIndex];
                if (ch == '\r')
                {
                    if (sourceIndex + 1 >= length)
                    {
                        _pendingCarriageReturn = true;
                        continue;
                    }

                    _buffer[_bufferCount++] = '\n';
                    _totalRead++;
                    if (source[sourceIndex + 1] == '\n')
                    {
                        sourceIndex++;
                    }
                    continue;
                }

                _buffer[_bufferCount++] = ch;
                _totalRead++;
            }
        }

        private void CompleteInput(bool limitReached)
        {
            if (limitReached)
            {
                LimitExceeded = _reader.Read() >= 0;
                if (LimitExceeded)
                {
                    _sourceRead++;
                }
            }

            if (_pendingCarriageReturn)
            {
                EnsureCapacity(_bufferCount + 1);
                _buffer[_bufferCount++] = '\n';
                _totalRead++;
                _pendingCarriageReturn = false;
            }

            _eof = true;
        }

        private static string NormalizeLineEndings(string source)
        {
            var firstCarriageReturn = source.IndexOf('\r');
            if (firstCarriageReturn < 0)
            {
                return source;
            }

            var normalized = new StringBuilder(source.Length);
            normalized.Append(source, 0, firstCarriageReturn);
            for (var i = firstCarriageReturn; i < source.Length; i++)
            {
                var ch = source[i];
                if (ch != '\r')
                {
                    normalized.Append(ch);
                    continue;
                }

                normalized.Append('\n');
                if (i + 1 < source.Length && source[i + 1] == '\n')
                {
                    i++;
                }
            }

            return normalized.ToString();
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length) return;
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}
