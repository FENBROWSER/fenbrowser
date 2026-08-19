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
        private int _bufferStart;
        private int _bufferCount;
        private int _totalRead;
        private bool _eof;
        private int _maxLength = int.MaxValue;

        public HtmlTokenizerInput(string text)
        {
            _text = text ?? string.Empty;
            _eof = true;
            _totalRead = _text.Length;
        }

        public HtmlTokenizerInput(TextReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _buffer = new char[ReadChunkSize];
        }

        public bool IsStreaming => _reader != null;
        public bool LimitExceeded { get; private set; }
        public int KnownLength => _eof ? _totalRead : int.MaxValue;

        public int MaxLength
        {
            set => _maxLength = value > 0 ? value : int.MaxValue;
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
                if (_totalRead >= _maxLength)
                {
                    LimitExceeded = _reader.Read() >= 0;
                    _eof = true;
                    break;
                }

                var readSize = Math.Min(ReadChunkSize, _maxLength - _totalRead);
                EnsureCapacity(_bufferCount + readSize);
                var read = _reader.Read(_buffer, _bufferCount, readSize);
                if (read <= 0)
                {
                    _eof = true;
                    break;
                }
                _bufferCount += read;
                _totalRead += read;
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length) return;
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}
