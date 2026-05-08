// SpecRef: HTML5 Â§13.2.5 â€” Tokenization (performance optimization)
// CapabilityId: PARSER-TOKEN-POOL-01
// Determinism: strict
// FallbackPolicy: allocate-new (graceful degradation to heap allocation)
using System;
using System.Runtime.CompilerServices;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// Object pool for HTML tokenizer tokens. Reduces GC pressure during large document
    /// parsing by reusing token instances instead of allocating new ones per emission.
    /// 
    /// Design:
    ///   - Ring-buffer pool for each token type (tag, character, comment, doctype).
    ///   - Tokens are rented via Rent*() and implicitly returned when the pool wraps.
    ///   - Tree builder must consume each token before the next is emitted (guaranteed
    ///     by the tokenizer's yield-based API).
    ///   - Thread-local: one pool per tokenizer instance, no synchronisation needed.
    ///   - Fallback: if pool is exhausted, allocates fresh (correctness over perf).
    /// </summary>
    public sealed class HtmlTokenPool
    {
        // Pool sizes chosen to cover typical token distances between tree builder
        // Pool sizes must be larger than HtmlTreeBuilder.InterleavedTokenBatchSize
        // to prevent ring-buffer overwrite before tokens are processed by the builder.
        private const int TagPoolSize = 4096;
        private const int CharPoolSize = 16384;
        private const int CommentPoolSize = 1024;
        private const int DoctypePoolSize = 64;

        private readonly PooledStartTagToken[] _startTagPool;
        private readonly PooledEndTagToken[] _endTagPool;
        private readonly PooledCharacterToken[] _charPool;
        private readonly PooledCommentToken[] _commentPool;
        private readonly PooledDoctypeToken[] _doctypePool;

        private int _startTagIndex;
        private int _endTagIndex;
        private int _charIndex;
        private int _commentIndex;
        private int _doctypeIndex;

        // Telemetry
        private long _rented;
        private long _allocated;

        public long TotalRented => _rented;
        public long TotalAllocated => _allocated;
        public double ReuseRatio => _rented > 0 ? 1.0 - ((double)_allocated / _rented) : 0.0;

        public HtmlTokenPool()
        {
            _startTagPool = new PooledStartTagToken[TagPoolSize];
            _endTagPool = new PooledEndTagToken[TagPoolSize];
            _charPool = new PooledCharacterToken[CharPoolSize];
            _commentPool = new PooledCommentToken[CommentPoolSize];
            _doctypePool = new PooledDoctypeToken[DoctypePoolSize];

            for (int i = 0; i < TagPoolSize; i++)
            {
                _startTagPool[i] = new PooledStartTagToken();
                _endTagPool[i] = new PooledEndTagToken();
            }
            for (int i = 0; i < CharPoolSize; i++)
                _charPool[i] = new PooledCharacterToken();
            for (int i = 0; i < CommentPoolSize; i++)
                _commentPool[i] = new PooledCommentToken();
            for (int i = 0; i < DoctypePoolSize; i++)
                _doctypePool[i] = new PooledDoctypeToken();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StartTagToken RentStartTag()
        {
            _rented++;
            var index = _startTagIndex;
            _startTagIndex = (index + 1) % TagPoolSize;
            var token = _startTagPool[index];
            token.Reset();
            return token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EndTagToken RentEndTag()
        {
            _rented++;
            var index = _endTagIndex;
            _endTagIndex = (index + 1) % TagPoolSize;
            var token = _endTagPool[index];
            token.Reset();
            return token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CharacterToken RentCharacter(char c)
        {
            _rented++;
            var index = _charIndex;
            _charIndex = (index + 1) % CharPoolSize;
            var token = _charPool[index];
            token.ResetWith(c);
            return token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CharacterToken RentCharacter(string s)
        {
            _rented++;
            var index = _charIndex;
            _charIndex = (index + 1) % CharPoolSize;
            var token = _charPool[index];
            token.ResetWith(s);
            return token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CommentToken RentComment()
        {
            _rented++;
            var index = _commentIndex;
            _commentIndex = (index + 1) % CommentPoolSize;
            var token = _commentPool[index];
            token.Reset();
            return token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DoctypeToken RentDoctype()
        {
            _rented++;
            var index = _doctypeIndex;
            _doctypeIndex = (index + 1) % DoctypePoolSize;
            var token = _doctypePool[index];
            token.Reset();
            return token;
        }

        /// <summary>
        /// Resets all pool indices. Called at the end of a parse session.
        /// </summary>
        public void ResetAll()
        {
            _startTagIndex = 0;
            _endTagIndex = 0;
            _charIndex = 0;
            _commentIndex = 0;
            _doctypeIndex = 0;
        }
    }

    // â”€â”€ Pooled token subclasses with Reset() methods â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Pooled StartTagToken with reset capability.</summary>
    public sealed class PooledStartTagToken : StartTagToken
    {
        public void Reset()
        {
            TagName = null;
            SelfClosing = false;
            Attributes.Clear();
        }
    }

    /// <summary>Pooled EndTagToken with reset capability.</summary>
    public sealed class PooledEndTagToken : EndTagToken
    {
        public void Reset()
        {
            TagName = null;
            SelfClosing = false;
            Attributes.Clear();
        }
    }

    /// <summary>Pooled CharacterToken with reset capability.</summary>
    public sealed class PooledCharacterToken : CharacterToken
    {
        public PooledCharacterToken() : base('\0') { }

        public void ResetWith(char c)
        {
            Data = c.ToString();
        }

        public void ResetWith(string s)
        {
            Data = s;
        }
    }

    /// <summary>Pooled CommentToken with reset capability.</summary>
    public sealed class PooledCommentToken : CommentToken
    {
        public void Reset()
        {
            Data = "";
        }
    }

    /// <summary>Pooled DoctypeToken with reset capability.</summary>
    public sealed class PooledDoctypeToken : DoctypeToken
    {
        public void Reset()
        {
            Name = null;
            PublicIdentifier = null;
            SystemIdentifier = null;
            ForceQuirks = false;
        }
    }
}


