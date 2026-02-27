using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace FenBrowser.Core.Parsing
{
    /// <summary>
    /// A production-grade HTML5 Tokenizer implementing the state machine defined in:
    /// https://html.spec.whatwg.org/multipage/parsing.html#tokenization
    /// </summary>
    public class HtmlTokenizer
    {
        private readonly string _input;
        private int _position;
        private readonly int _length;
        private int _emittedTokenCount;
        private bool _emissionLimitReached;
        
        // Current state
        private TokenizerState _state = TokenizerState.Data;
        
        // Current buffers
        private StringBuilder _buffer = new StringBuilder();
        private TagToken _currentTag;
        private CommentToken _currentComment;
        private DoctypeToken _currentDoctype;
        
        private string _lastAttrName;
        private StringBuilder _attrValueBuffer = new StringBuilder();
        
        private TokenizerState _returnState;
        private uint _charRefValue;
        private bool _charRefInAttributeValue;
        private char _charRefHexMarker;

        private static readonly Dictionary<string, string> NamedCharacterReferences = new(StringComparer.Ordinal)
        {
            ["amp"] = "&",
            ["lt"] = "<",
            ["gt"] = ">",
            ["quot"] = "\"",
            ["apos"] = "'",
            ["nbsp"] = "\u00A0",
            ["copy"] = "\u00A9",
            ["reg"] = "\u00AE",
            ["trade"] = "\u2122",
            ["euro"] = "\u20AC",
            ["cent"] = "\u00A2",
            ["pound"] = "\u00A3",
            ["yen"] = "\u00A5",
            ["sect"] = "\u00A7",
            ["para"] = "\u00B6",
            ["middot"] = "\u00B7",
            ["deg"] = "\u00B0",
            ["plusmn"] = "\u00B1",
            ["sup2"] = "\u00B2",
            ["sup3"] = "\u00B3",
            ["frac14"] = "\u00BC",
            ["frac12"] = "\u00BD",
            ["frac34"] = "\u00BE",
            ["times"] = "\u00D7",
            ["divide"] = "\u00F7",
            ["micro"] = "\u00B5",
            ["ndash"] = "\u2013",
            ["mdash"] = "\u2014",
            ["hellip"] = "\u2026",
            ["lsquo"] = "\u2018",
            ["rsquo"] = "\u2019",
            ["ldquo"] = "\u201C",
            ["rdquo"] = "\u201D",
            ["laquo"] = "\u00AB",
            ["raquo"] = "\u00BB",
            ["bull"] = "\u2022",
            ["iexcl"] = "\u00A1",
            ["iquest"] = "\u00BF",
            ["Agrave"] = "\u00C0",
            ["Aacute"] = "\u00C1",
            ["Acirc"] = "\u00C2",
            ["Atilde"] = "\u00C3",
            ["Auml"] = "\u00C4",
            ["Aring"] = "\u00C5",
            ["AElig"] = "\u00C6",
            ["Ccedil"] = "\u00C7",
            ["Egrave"] = "\u00C8",
            ["Eacute"] = "\u00C9",
            ["Ecirc"] = "\u00CA",
            ["Euml"] = "\u00CB",
            ["Igrave"] = "\u00CC",
            ["Iacute"] = "\u00CD",
            ["Icirc"] = "\u00CE",
            ["Iuml"] = "\u00CF",
            ["Ntilde"] = "\u00D1",
            ["Ograve"] = "\u00D2",
            ["Oacute"] = "\u00D3",
            ["Ocirc"] = "\u00D4",
            ["Otilde"] = "\u00D5",
            ["Ouml"] = "\u00D6",
            ["Oslash"] = "\u00D8",
            ["Ugrave"] = "\u00D9",
            ["Uacute"] = "\u00DA",
            ["Ucirc"] = "\u00DB",
            ["Uuml"] = "\u00DC",
            ["Yacute"] = "\u00DD",
            ["agrave"] = "\u00E0",
            ["aacute"] = "\u00E1",
            ["acirc"] = "\u00E2",
            ["atilde"] = "\u00E3",
            ["auml"] = "\u00E4",
            ["aring"] = "\u00E5",
            ["aelig"] = "\u00E6",
            ["ccedil"] = "\u00E7",
            ["egrave"] = "\u00E8",
            ["eacute"] = "\u00E9",
            ["ecirc"] = "\u00EA",
            ["euml"] = "\u00EB",
            ["igrave"] = "\u00EC",
            ["iacute"] = "\u00ED",
            ["icirc"] = "\u00EE",
            ["iuml"] = "\u00EF",
            ["ntilde"] = "\u00F1",
            ["ograve"] = "\u00F2",
            ["oacute"] = "\u00F3",
            ["ocirc"] = "\u00F4",
            ["otilde"] = "\u00F5",
            ["ouml"] = "\u00F6",
            ["oslash"] = "\u00F8",
            ["ugrave"] = "\u00F9",
            ["uacute"] = "\u00FA",
            ["ucirc"] = "\u00FB",
            ["uuml"] = "\u00FC",
            ["yacute"] = "\u00FD",
            ["yuml"] = "\u00FF"
        };

        private static readonly Dictionary<int, int> NumericCharacterReferenceReplacements = new()
        {
            [0x80] = 0x20AC,
            [0x82] = 0x201A,
            [0x83] = 0x0192,
            [0x84] = 0x201E,
            [0x85] = 0x2026,
            [0x86] = 0x2020,
            [0x87] = 0x2021,
            [0x88] = 0x02C6,
            [0x89] = 0x2030,
            [0x8A] = 0x0160,
            [0x8B] = 0x2039,
            [0x8C] = 0x0152,
            [0x8E] = 0x017D,
            [0x91] = 0x2018,
            [0x92] = 0x2019,
            [0x93] = 0x201C,
            [0x94] = 0x201D,
            [0x95] = 0x2022,
            [0x96] = 0x2013,
            [0x97] = 0x2014,
            [0x98] = 0x02DC,
            [0x99] = 0x2122,
            [0x9A] = 0x0161,
            [0x9B] = 0x203A,
            [0x9C] = 0x0153,
            [0x9E] = 0x017E,
            [0x9F] = 0x0178
        };

        private static readonly Dictionary<string, string?> NamedCharacterReferenceDecodeCache = new(StringComparer.Ordinal);
        private static readonly object NamedCharacterReferenceDecodeCacheLock = new();

        // Queue for characters that need to be emitted before continuing state machine
        private readonly Queue<char> _pendingChars = new Queue<char>();

        // Temporary buffer for script data escape end-tag matching
        private StringBuilder _scriptEscapeBuffer = new StringBuilder();

        /// <summary>
        /// Hard safety cap for non-EOF token emissions to prevent pathological inputs
        /// from producing unbounded token streams.
        /// </summary>
        public int MaxTokenEmissions { get; set; } = 2_000_000;

        public HtmlTokenizer(string input)
        {
            _input = input ?? "";
            _length = _input.Length;
            _position = 0;
        }

        public void SetState(TokenizerState state)
        {
            _state = state;
        }
        
        // This is simplified. Spec requires "appropriate end tag token" check which technically
        // depends on the "last start tag".
        // The Tree Builder typically handles checking if the end tag matches the open element.
        // But the Tokenizer needs to know the "last start tag name" for the </tag> check in RCDATA/etc.
        // We will store it here.
        public string LastStartTagName { get; set; }

        public enum TokenizerState
        {
            Data,
            CharacterReference,
            RcData,
            RawText,
            ScriptData,
            PlainText,
            TagOpen,
            EndTagOpen,
            TagName,
            RcDataLessThanSign,
            RcDataEndTagOpen,
            RcDataEndTagName,
            RawTextLessThanSign,
            RawTextEndTagOpen,
            RawTextEndTagName,
            ScriptDataLessThanSign,
            ScriptDataEndTagOpen,
            ScriptDataEndTagName,
            ScriptDataEscapeStart,
            ScriptDataEscapeStartDash,
            ScriptDataEscaped,
            ScriptDataEscapedDash,
            ScriptDataEscapedDashDash,
            ScriptDataEscapedLessThanSign,
            ScriptDataEscapedEndTagOpen,
            ScriptDataEscapedEndTagName,
            ScriptDataDoubleEscapeStart,
            ScriptDataDoubleEscaped,
            ScriptDataDoubleEscapedDash,
            ScriptDataDoubleEscapedDashDash,
            ScriptDataDoubleEscapedLessThanSign,
            ScriptDataDoubleEscapeEnd,
            BeforeAttributeName,
            AttributeName,
            AfterAttributeName,
            BeforeAttributeValue,
            AttributeValueDoubleQuoted,
            AttributeValueSingleQuoted,
            AttributeValueUnquoted,
            AfterAttributeValueQuoted,
            SelfClosingStartTag,
            BogusComment,
            MarkupDeclarationOpen,
            CommentStart,
            CommentStartDash,
            Comment,
            CommentLessThanSign,
            CommentLessThanSignBang,
            CommentLessThanSignBangDash,
            CommentLessThanSignBangDashDash,
            CommentEndDash,
            CommentEnd,
            CommentEndBang,
            Doctype,
            BeforeDoctypeName,
            DoctypeName,
            AfterDoctypeName,
            AfterDoctypeSystemIdentifier,
            BogusDoctype,
            // Character Reference States
            NumericCharacterReference,
            HexadecimalCharacterReferenceStart,
            DecimalCharacterReference,
            HexadecimalCharacterReference,
            NumericCharacterReferenceEnd
        }

        public IEnumerable<HtmlToken> Tokenize()
        {
            while (true)
            {
                var token = NextToken();
                if (token == null) continue; // Internal state transition produced no token yet

                if (!_emissionLimitReached &&
                    token.Type != HtmlTokenType.EndOfFile &&
                    _emittedTokenCount >= MaxTokenEmissions)
                {
                    _emissionLimitReached = true;
                    EmitError($"Tokenizer emission limit reached ({MaxTokenEmissions}). Aborting stream.");
                    yield return new EofToken();
                    break;
                }

                if (token.Type != HtmlTokenType.EndOfFile)
                {
                    _emittedTokenCount++;
                }
                
                yield return token;
                
                if (token.Type == HtmlTokenType.EndOfFile)
                    break;
            }
        }

        private HtmlToken NextToken()
        {
            // Drain any pending characters first (emitted by multi-char transitions)
            if (_pendingChars.Count > 0)
                return EmitCharacter(_pendingChars.Dequeue());

            // This loop runs until a token is emitted
            while (_position <= _length)
            {
                char c = Peek();
                
                switch (_state)
                {
                    case TokenizerState.Data:
                        if (c == '&')
                        {
                            Consume();
                            _returnState = TokenizerState.Data;
                            SwitchTo(TokenizerState.CharacterReference);
                            continue;
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.TagOpen);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;

                    case TokenizerState.CharacterReference:
                        if (c == '#')
                        {
                            Consume();
                            SwitchTo(TokenizerState.NumericCharacterReference);
                        }
                        else
                        {
                            if (TryConsumeNamedCharacterReference(out var namedValue))
                            {
                                SwitchTo(_returnState);
                                var namedToken = EmitCharacterReferenceResult(namedValue);
                                if (namedToken != null) return namedToken;
                                continue;
                            }

                            // Not a recognized reference, emit literal '&' and continue from return state.
                            SwitchTo(_returnState);
                            var literalAmp = EmitCharacterReferenceResult("&");
                            if (literalAmp != null) return literalAmp;
                            continue;
                        }
                        break;

                    case TokenizerState.NumericCharacterReference:
                        _charRefValue = 0;
                        _charRefHexMarker = '\0';
                        if (c == 'x' || c == 'X')
                        {
                            _charRefHexMarker = c;
                            Consume();
                            SwitchTo(TokenizerState.HexadecimalCharacterReferenceStart);
                        }
                        else if (char.IsDigit(c))
                        {
                            SwitchTo(TokenizerState.DecimalCharacterReference);
                            // Reconsume done by not calling Consume()
                        }
                        else
                        {
                            EmitError("Invalid Numeric Character Reference");
                            SwitchTo(_returnState);
                            var invalidNumeric = EmitCharacterReferenceResult("&#");
                            if (invalidNumeric != null) return invalidNumeric;
                            continue;
                        }
                        break;

                    case TokenizerState.DecimalCharacterReference:
                        if (char.IsDigit(c))
                        {
                            Consume();
                            _charRefValue = (_charRefValue * 10) + (uint)(c - '0');
                        }
                        else if (c == ';')
                        {
                            Consume();
                            SwitchTo(TokenizerState.NumericCharacterReferenceEnd);
                        }
                        else
                        {
                            // Missing semicolon but valid number
                            SwitchTo(TokenizerState.NumericCharacterReferenceEnd);
                        }
                        break;

                    case TokenizerState.HexadecimalCharacterReferenceStart:
                        if (IsHexDigit(c))
                        {
                            SwitchTo(TokenizerState.HexadecimalCharacterReference);
                        }
                        else
                        {
                            EmitError("Invalid Hex Character Reference");
                            SwitchTo(_returnState);
                            var invalidHexPrefix = _charRefHexMarker == 'X' ? "&#X" : "&#x";
                            var invalidHex = EmitCharacterReferenceResult(invalidHexPrefix);
                            if (invalidHex != null) return invalidHex;
                            continue;
                        }
                        break;

                    case TokenizerState.HexadecimalCharacterReference:
                        if (IsHexDigit(c))
                        {
                            Consume();
                            _charRefValue = (_charRefValue * 16) + (uint)GetHexValue(c);
                        }
                        else if (c == ';')
                        {
                            Consume();
                            SwitchTo(TokenizerState.NumericCharacterReferenceEnd);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.NumericCharacterReferenceEnd);
                        }
                        break;

                    case TokenizerState.NumericCharacterReferenceEnd:
                        var resolved = ResolveNumericCharacterReference(_charRefValue);
                        SwitchTo(_returnState);
                        var numericToken = EmitCharacterReferenceResult(resolved);
                        if (numericToken != null) return numericToken;
                        continue;

                    case TokenizerState.TagOpen:
                        if (c == '!')
                        {
                            Consume();
                            SwitchTo(TokenizerState.MarkupDeclarationOpen);
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.EndTagOpen);
                        }
                        else if (char.IsLetter(c))
                        {
                            _currentTag = new StartTagToken();
                            SwitchTo(TokenizerState.TagName);
                             // Don't consume here, TagName state will handle it (reconsume)
                             continue;
                        }
                        else if (c == '?')
                        {
                            // Bogus comment (<?... >)
                             Consume();
                            _currentComment = new CommentToken();
                            SwitchTo(TokenizerState.BogusComment);
                        }
                        else
                        {
                            // Emit < as char state
                             // Invalid first character of start tag
                             EmitError("Invalid First Character of Tag");
                             SwitchTo(TokenizerState.Data);
                             return EmitCharacter('<');
                        }
                        break;

                    // --- RCDATA States (Title, Textarea) ---
                    case TokenizerState.RcData:
                        if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.RcDataLessThanSign);
                        }
                        else if (c == '&')
                        {
                            Consume();
                            _returnState = TokenizerState.RcData;
                            SwitchTo(TokenizerState.CharacterReference);
                            continue;
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;
                        
                    case TokenizerState.RcDataLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            // Set temp buffer to empty
                            _buffer.Clear(); 
                            SwitchTo(TokenizerState.RcDataEndTagOpen);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RcData);
                            return EmitCharacter('<');
                        }
                        break;
                        
                    case TokenizerState.RcDataEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = ""; // New tag
                             // Reconsume in RcDataEndTagName?
                             // No, spec says: create end tag token, append current char to tag name, switch to RcDataEndTagName
                             _currentTag.TagName += char.ToLowerInvariant(c);
                             Consume();
                             SwitchTo(TokenizerState.RcDataEndTagName);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RcData);
                            return EmitCharacter('<'); // And '/' ? Spec is complex here.
                            // Simplified: Just emit characters
                        }
                        break;
                        
                    case TokenizerState.RcDataEndTagName:
                        // Scan until we match the LastStartTagName or fail
                        bool isAppropriate = _currentTag.TagName == LastStartTagName;
                        
                        if (char.IsWhiteSpace(c))
                        {
                            if (isAppropriate)
                            {
                                Consume();
                                SwitchTo(TokenizerState.BeforeAttributeName);
                            }
                            else
                            {
                                 // Fail -> Treat as raw text
                                 SwitchTo(TokenizerState.RcData);
                                 return EmitCharacter('<'); // Rough approximation, proper rollback needed for full spec
                            }
                        }
                        else if (c == '/')
                        {
                             if (isAppropriate)
                            {
                                Consume();
                                SwitchTo(TokenizerState.SelfClosingStartTag);
                            }
                            else
                            {
                                 SwitchTo(TokenizerState.RcData);
                                 return EmitCharacter('<');
                            }
                        }
                        else if (c == '>')
                        {
                            if (isAppropriate)
                            {
                                Consume();
                                SwitchTo(TokenizerState.Data);
                                return EmitCurrentTag();
                            }
                             else
                            {
                                 SwitchTo(TokenizerState.RcData);
                                 return EmitCharacter('<'); 
                            }
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        else
                        {
                             SwitchTo(TokenizerState.RcData);
                             return EmitCharacter('<');
                        }
                        break;

                    // --- RAWTEXT States (Style, Script - wait Script has own state) ---
                    case TokenizerState.RawText:
                        if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.RawTextLessThanSign);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;
                        
                    case TokenizerState.RawTextLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.RawTextEndTagOpen);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RawText);
                            return EmitCharacter('<');
                        }
                        break;

                    case TokenizerState.RawTextEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = "";
                            _currentTag.TagName += char.ToLowerInvariant(c);
                            Consume();
                            SwitchTo(TokenizerState.RawTextEndTagName);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.RawText);
                             return EmitCharacter('<'); // And /
                        }
                        break;
                        
                    case TokenizerState.RawTextEndTagName:
                         bool isAppropriateRaw = _currentTag.TagName == LastStartTagName;
                         if (c == '>')
                        {
                            if (isAppropriateRaw)
                            {
                                Consume();
                                SwitchTo(TokenizerState.Data);
                                return EmitCurrentTag();
                            }
                             else
                            {
                                 SwitchTo(TokenizerState.RawText);
                                 return EmitCharacter('<'); 
                            }
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        else
                        {
                            // Simplified whitespace/others handling
                             if (isAppropriateRaw && char.IsWhiteSpace(c))
                             {
                                 Consume();
                                 SwitchTo(TokenizerState.BeforeAttributeName);
                             }
                             else
                             {
                                SwitchTo(TokenizerState.RawText);
                                return EmitCharacter('<');
                             }
                        }
                        break;

                    case TokenizerState.ScriptData:
                        // Similar to RawText but handles <!--
                         if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataLessThanSign);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;

                    case TokenizerState.ScriptDataLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEndTagOpen);
                        }
                        else if (c == '!')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapeStart);
                            // Emit '<' and '!' characters — the caller will get '<',
                            // then on next call we continue in ScriptDataEscapeStart.
                            // We need to queue the '!' for emission too.
                            _pendingChars.Enqueue('!');
                            return EmitCharacter('<');
                        }
                        else
                        {
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('<');
                        }
                        break;

                    case TokenizerState.ScriptDataEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = "";
                            _currentTag.TagName += char.ToLowerInvariant(c);
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEndTagName);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('<');
                        }
                        break;

                     case TokenizerState.ScriptDataEndTagName:
                        // Simplified
                         bool isAppropriateScript = _currentTag.TagName == LastStartTagName; // Usually "script"
                         if (c == '>')
                        {
                            if (isAppropriateScript)
                            {
                                Consume();
                                SwitchTo(TokenizerState.Data);
                                return EmitCurrentTag();
                            }
                             else
                            {
                                // Not an appropriate end tag. Emit '<', '/' and the accumulated tag name chars as script data.
                                SwitchTo(TokenizerState.ScriptData);
                                _pendingChars.Enqueue('/');
                                foreach (var ch in _currentTag.TagName) _pendingChars.Enqueue(ch);
                                return EmitCharacter('<'); 
                            }
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                         else
                        {
                            // Not a letter and not '>': emit '<', '/' and accumulated tag name chars, then reconsume.
                            SwitchTo(TokenizerState.ScriptData);
                            _pendingChars.Enqueue('/');
                            foreach (var ch in _currentTag.TagName) _pendingChars.Enqueue(ch);
                            return EmitCharacter('<');
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.17 – Script data escape start state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapeStart:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapeStartDash);
                            return EmitCharacter('-');
                        }
                        else
                        {
                            // Not a `<!--` sequence; reconsume in script data
                            SwitchTo(TokenizerState.ScriptData);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.18 – Script data escape start dash state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapeStartDash:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapedDashDash);
                            return EmitCharacter('-');
                        }
                        else
                        {
                            // Only one dash — not `<!--`; reconsume in script data
                            SwitchTo(TokenizerState.ScriptData);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.19 – Script data escaped state
                    // ================================================================
                    case TokenizerState.ScriptDataEscaped:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapedDash);
                            return EmitCharacter('-');
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapedLessThanSign);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            EmitError("eof-in-script-html-comment-like-text");
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.20 – Script data escaped dash state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapedDash:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapedDashDash);
                            return EmitCharacter('-');
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapedLessThanSign);
                        }
                        else if (c == '\0' && IsEof())
                        {
                            EmitError("eof-in-script-html-comment-like-text");
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscaped);
                            return EmitCharacter(c);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.21 – Script data escaped dash dash state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapedDashDash:
                        if (c == '-')
                        {
                            Consume();
                            return EmitCharacter('-');
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscapedLessThanSign);
                        }
                        else if (c == '>')
                        {
                            // End of the escaped section: `-->` closes it
                            Consume();
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('>');
                        }
                        else if (c == '\0' && IsEof())
                        {
                            EmitError("eof-in-script-html-comment-like-text");
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataEscaped);
                            return EmitCharacter(c);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.22 – Script data escaped less-than sign state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapedLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            _scriptEscapeBuffer.Clear();
                            SwitchTo(TokenizerState.ScriptDataEscapedEndTagOpen);
                        }
                        else if (char.IsLetter(c))
                        {
                            _scriptEscapeBuffer.Clear();
                            // Don't consume — reconsume in double escape start
                            _pendingChars.Enqueue('<');
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapeStart);
                        }
                        else
                        {
                            SwitchTo(TokenizerState.ScriptDataEscaped);
                            return EmitCharacter('<');
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.23 – Script data escaped end tag open state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapedEndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            _currentTag.TagName = "";
                            // Reconsume in end tag name
                            SwitchTo(TokenizerState.ScriptDataEscapedEndTagName);
                        }
                        else
                        {
                            _pendingChars.Enqueue('/');
                            SwitchTo(TokenizerState.ScriptDataEscaped);
                            return EmitCharacter('<');
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.24 – Script data escaped end tag name state
                    // ================================================================
                    case TokenizerState.ScriptDataEscapedEndTagName:
                    {
                        bool isAppropriateEscaped = _currentTag.TagName == LastStartTagName;
                        if ((c == '\t' || c == '\n' || c == '\f' || c == ' ') && isAppropriateEscaped)
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '/' && isAppropriateEscaped)
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>' && isAppropriateEscaped)
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        else
                        {
                            // Not appropriate — emit buffered chars and reconsume
                            _pendingChars.Enqueue('/');
                            foreach (char ch in _currentTag.TagName)
                                _pendingChars.Enqueue(ch);
                            SwitchTo(TokenizerState.ScriptDataEscaped);
                            return EmitCharacter('<');
                        }
                        break;
                    }

                    // ================================================================
                    // HTML5 §13.2.5.25 – Script data double escape start state
                    // ================================================================
                    case TokenizerState.ScriptDataDoubleEscapeStart:
                        if (c == '\t' || c == '\n' || c == '\f' || c == ' ' || c == '/' || c == '>')
                        {
                            Consume();
                            if (_scriptEscapeBuffer.ToString() == "script")
                                SwitchTo(TokenizerState.ScriptDataDoubleEscaped);
                            else
                                SwitchTo(TokenizerState.ScriptDataEscaped);
                            return EmitCharacter(c);
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _scriptEscapeBuffer.Append(char.ToLowerInvariant(c));
                            return EmitCharacter(c);
                        }
                        else
                        {
                            // Reconsume in script data escaped
                            SwitchTo(TokenizerState.ScriptDataEscaped);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.26 – Script data double escaped state
                    // ================================================================
                    case TokenizerState.ScriptDataDoubleEscaped:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapedDash);
                            return EmitCharacter('-');
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapedLessThanSign);
                            return EmitCharacter('<');
                        }
                        else if (c == '\0' && IsEof())
                        {
                            EmitError("eof-in-script-html-comment-like-text");
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            return EmitCharacter(c);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.27 – Script data double escaped dash state
                    // ================================================================
                    case TokenizerState.ScriptDataDoubleEscapedDash:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapedDashDash);
                            return EmitCharacter('-');
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapedLessThanSign);
                            return EmitCharacter('<');
                        }
                        else if (c == '\0' && IsEof())
                        {
                            EmitError("eof-in-script-html-comment-like-text");
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscaped);
                            return EmitCharacter(c);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.28 – Script data double escaped dash dash state
                    // ================================================================
                    case TokenizerState.ScriptDataDoubleEscapedDashDash:
                        if (c == '-')
                        {
                            Consume();
                            return EmitCharacter('-');
                        }
                        else if (c == '<')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapedLessThanSign);
                            return EmitCharacter('<');
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptData);
                            return EmitCharacter('>');
                        }
                        else if (c == '\0' && IsEof())
                        {
                            EmitError("eof-in-script-html-comment-like-text");
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscaped);
                            return EmitCharacter(c);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.29 – Script data double escaped less-than sign state
                    // ================================================================
                    case TokenizerState.ScriptDataDoubleEscapedLessThanSign:
                        if (c == '/')
                        {
                            Consume();
                            _scriptEscapeBuffer.Clear();
                            SwitchTo(TokenizerState.ScriptDataDoubleEscapeEnd);
                            return EmitCharacter('/');
                        }
                        else
                        {
                            // Reconsume in double escaped
                            SwitchTo(TokenizerState.ScriptDataDoubleEscaped);
                        }
                        break;

                    // ================================================================
                    // HTML5 §13.2.5.30 – Script data double escape end state
                    // ================================================================
                    case TokenizerState.ScriptDataDoubleEscapeEnd:
                        if (c == '\t' || c == '\n' || c == '\f' || c == ' ' || c == '/' || c == '>')
                        {
                            Consume();
                            if (_scriptEscapeBuffer.ToString() == "script")
                                SwitchTo(TokenizerState.ScriptDataEscaped);
                            else
                                SwitchTo(TokenizerState.ScriptDataDoubleEscaped);
                            return EmitCharacter(c);
                        }
                        else if (char.IsLetter(c))
                        {
                            Consume();
                            _scriptEscapeBuffer.Append(char.ToLowerInvariant(c));
                            return EmitCharacter(c);
                        }
                        else
                        {
                            // Reconsume in double escaped
                            SwitchTo(TokenizerState.ScriptDataDoubleEscaped);
                        }
                        break;

                    case TokenizerState.EndTagOpen:
                        if (char.IsLetter(c))
                        {
                            _currentTag = new EndTagToken();
                            SwitchTo(TokenizerState.TagName);
                        }
                        else if (c == '>')
                        {
                             Consume();
                             EmitError("Missing End Tag Name");
                             SwitchTo(TokenizerState.Data);
                        }
                        else if (IsEof())
                        {
                            EmitError("Eof Before Tag Name");
                            SwitchTo(TokenizerState.Data);
                            return EmitCharacter('<'); // and / ?
                        }
                        else
                        {
                            // Bogus comment
                            Consume();
                            _currentComment = new CommentToken();
                            _currentComment.Data += c;
                            SwitchTo(TokenizerState.BogusComment);
                        }
                        break;

                    case TokenizerState.TagName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Tag");
                             return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _currentTag.TagName += char.ToLowerInvariant(c);
                        }
                        break;

                    case TokenizerState.BeforeAttributeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume(); // Ignore
                        }
                        else if (c == '/')
                        {
                             Consume();
                             SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>')
                        {
                             Consume();
                             SwitchTo(TokenizerState.Data);
                             return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                            return EmitCurrentTag(); // Or error
                        }
                        else
                        {
                            SwitchTo(TokenizerState.AttributeName);
                            _buffer.Clear();
                        }
                        break;

                    case TokenizerState.AttributeName:
                        if (char.IsWhiteSpace(c) || c == '/' || c == '>' || IsEof())
                        {
                            // End of attribute name
                            _lastAttrName = _buffer.ToString();
                            if (_currentTag.Attributes.Find(a => a.Name == _lastAttrName) == null)
                            {
                                 _currentTag.AddAttribute(_lastAttrName, "");
                            }
                            
                            _buffer.Clear();
                            if (c == '=') // Should prevent this case here if strict? No spec says check whitespace first.
                            {
                                // Wait, simple check:
                                // If whitespace, go to AfterAttributeName
                            }
                             
                             SwitchTo(TokenizerState.AfterAttributeName);
                        }
                        else if (c == '=')
                        {
                            Consume();
                            _lastAttrName = _buffer.ToString();
                             // Create attribute if not exists
                             if (_currentTag.Attributes.Find(a => a.Name == _lastAttrName) == null)
                            {
                                 _currentTag.AddAttribute(_lastAttrName, "");
                            }
                             _buffer.Clear();
                            SwitchTo(TokenizerState.BeforeAttributeValue);
                        }
                        else
                        {
                             Consume();
                             _buffer.Append(char.ToLowerInvariant(c));
                        }
                        break;

                    case TokenizerState.AfterAttributeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '=')
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeValue);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                           return EmitCurrentTag();
                        }
                        else
                        {
                            // New attribute
                            SwitchTo(TokenizerState.AttributeName);
                            _buffer.Clear();
                            // Reconsume c
                            continue;
                        }
                        break;

                    case TokenizerState.BeforeAttributeValue:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                        }
                        else if (c == '"')
                        {
                            Consume();
                            SwitchTo(TokenizerState.AttributeValueDoubleQuoted);
                            _attrValueBuffer.Clear();
                        }
                        else if (c == '\'')
                        {
                            Consume();
                            SwitchTo(TokenizerState.AttributeValueSingleQuoted);
                            _attrValueBuffer.Clear();
                        }
                        else if (c == '>')
                        {
                            Consume();
                            EmitError("Missing Attribute Value");
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else
                        {
                            SwitchTo(TokenizerState.AttributeValueUnquoted);
                            _attrValueBuffer.Clear();
                            continue; // Reconsume
                        }
                        break;

                    case TokenizerState.AttributeValueDoubleQuoted:
                        if (c == '&')
                        {
                            Consume();
                            _returnState = TokenizerState.AttributeValueDoubleQuoted;
                            _charRefInAttributeValue = true;
                            SwitchTo(TokenizerState.CharacterReference);
                            continue;
                        }
                        else if (c == '"')
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.AfterAttributeValueQuoted);
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof in Attribute Value");
                             return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _attrValueBuffer.Append(c);
                        }
                        break;

                    case TokenizerState.AttributeValueSingleQuoted:
                        if (c == '&')
                        {
                            Consume();
                            _returnState = TokenizerState.AttributeValueSingleQuoted;
                            _charRefInAttributeValue = true;
                            SwitchTo(TokenizerState.CharacterReference);
                            continue;
                        }
                        else if (c == '\'')
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.AfterAttributeValueQuoted);
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof in Attribute Value");
                             return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _attrValueBuffer.Append(c);
                        }
                        break;

                    case TokenizerState.AttributeValueUnquoted:
                        if (c == '&')
                        {
                            Consume();
                            _returnState = TokenizerState.AttributeValueUnquoted;
                            _charRefInAttributeValue = true;
                            SwitchTo(TokenizerState.CharacterReference);
                            continue;
                        }
                        else if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SetAttributeValue();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                            SetAttributeValue();
                            return new EofToken();
                        }
                        else
                        {
                            Consume();
                            _attrValueBuffer.Append(c);
                        }
                        break;

                    case TokenizerState.AfterAttributeValueQuoted:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeAttributeName);
                        }
                        else if (c == '/')
                        {
                            Consume();
                            SwitchTo(TokenizerState.SelfClosingStartTag);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                             return new EofToken();
                        }
                        else
                        {
                             EmitError("Missing space after attribute value");
                             SwitchTo(TokenizerState.BeforeAttributeName);
                             continue;
                        }
                        break;

                    case TokenizerState.SelfClosingStartTag:
                        if (c == '>')
                        {
                            Consume();
                            _currentTag.SelfClosing = true;
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentTag();
                        }
                        else if (IsEof())
                        {
                             return new EofToken();
                        }
                        else
                        {
                            EmitError("Unexpected char in self closing tag");
                            SwitchTo(TokenizerState.BeforeAttributeName);
                            continue;
                        }
                        break;
                        
                    case TokenizerState.MarkupDeclarationOpen:
                        if (Matches("--"))
                        {
                            Consume(2);
                            _currentComment = new CommentToken();
                            SwitchTo(TokenizerState.CommentStart);
                        }
                        else if (Matches("DOCTYPE", ignoreCase: true))
                        {
                            Consume(7);
                            SwitchTo(TokenizerState.Doctype);
                        }
                        else
                        {
                            EmitError("Invalid Markup Declaration");
                            SwitchTo(TokenizerState.BogusComment);
                            _currentComment = new CommentToken();
                        }
                        break;
                        
                    case TokenizerState.CommentStart:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentStartDash);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            EmitError("Abrupt Closing of Empty Comment");
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else
                        {
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;
                    
                    case TokenizerState.CommentStartDash:
                         if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEnd);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            EmitError("Abrupt Closing of Empty Comment");
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else
                        {
                            _currentComment.Data += '-';
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;

                    case TokenizerState.Comment:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEndDash);
                        }
                        else if (c == '<')
                        {
                            Consume();
                            _currentComment.Data += c;
                            SwitchTo(TokenizerState.CommentLessThanSign);
                        }
                        else if (IsEof())
                        {
                            EmitError("Eof In Comment");
                            return EmitCurrentComment();
                        }
                        else
                        {
                            Consume();
                            _currentComment.Data += c;
                        }
                        break;
                        
                    case TokenizerState.CommentEndDash:
                        if (c == '-')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEnd);
                        }
                        else if (IsEof())
                        {
                            EmitError("Eof In Comment");
                            return EmitCurrentComment();
                        }
                        else
                        {
                            _currentComment.Data += '-';
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;

                    case TokenizerState.CommentEnd:
                        if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else if (c == '!')
                        {
                            Consume();
                            SwitchTo(TokenizerState.CommentEndBang);
                        }
                        else if (c == '-')
                        {
                            Consume();
                            _currentComment.Data += '-';
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Comment");
                             return EmitCurrentComment();
                        }
                        else
                        {
                            _currentComment.Data += "--";
                            SwitchTo(TokenizerState.Comment);
                            continue;
                        }
                        break;
                        
                    case TokenizerState.BogusComment:
                         if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentComment();
                        }
                        else if (IsEof())
                        {
                            return EmitCurrentComment();
                        }
                        else
                        {
                            Consume();
                            _currentComment.Data += c;
                        }
                        break;

                    case TokenizerState.Doctype:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.BeforeDoctypeName);
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype = new DoctypeToken() { ForceQuirks = true };
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                             EmitError("Missing Whitespace Before Doctype Name");
                             SwitchTo(TokenizerState.BeforeDoctypeName);
                        }
                        break;

                    case TokenizerState.BeforeDoctypeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                        }
                        else if (c == '>')
                        {
                             Consume();
                             EmitError("Missing Doctype Name");
                             _currentDoctype = new DoctypeToken() { ForceQuirks = true };
                             SwitchTo(TokenizerState.Data);
                             return EmitCurrentDoctype();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype = new DoctypeToken() { ForceQuirks = true };
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                            _currentDoctype = new DoctypeToken();
                            _currentDoctype.Name = "";
                            _currentDoctype.Name += char.ToLowerInvariant(c);
                            Consume();
                            SwitchTo(TokenizerState.DoctypeName);
                        }
                        break;

                    case TokenizerState.DoctypeName:
                        if (char.IsWhiteSpace(c))
                        {
                            Consume();
                            SwitchTo(TokenizerState.AfterDoctypeName);
                        }
                        else if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentDoctype();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype.ForceQuirks = true;
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                            Consume();
                            _currentDoctype.Name += char.ToLowerInvariant(c);
                        }
                        break;
                        
                    // Simplified Doctype (skipping PUBLIC/SYSTEM specifics for brevity, just consuming until >)
                    case TokenizerState.AfterDoctypeName:
                         if (c == '>')
                        {
                            Consume();
                            SwitchTo(TokenizerState.Data);
                            return EmitCurrentDoctype();
                        }
                        else if (IsEof())
                        {
                             EmitError("Eof In Doctype");
                             _currentDoctype.ForceQuirks = true;
                             return EmitCurrentDoctype();
                        }
                        else
                        {
                            Consume();
                            // Effectively BogusDoctype behavior for now
                        }
                        break;
                        
                    default:
                         // Fallback
                         Consume();
                         break;
                }
            }
            return new EofToken();
        }
        
        // Helpers
        
        private void SwitchTo(TokenizerState newState)
        {
            _state = newState;
        }

        private char Peek()
        {
            if (_position >= _length) return '\0';
            return _input[_position];
        }

        private void Consume(int count = 1)
        {
            _position += count;
        }

        private bool IsEof() => _position >= _length;

        private void EmitError(string message)
        {
            // FenLogger.Debug($"[HtmlTokenizer] Error: {message}");
        }

        private HtmlToken EmitCharacter(char c)
        {
            return new CharacterToken(c);
        }

        private TagToken EmitCurrentTag()
        {
            var tag = _currentTag;
            _currentTag = null;
            return tag;
        }
        
        private CommentToken EmitCurrentComment()
        {
            var c = _currentComment;
            _currentComment = null;
            return c;
        }
        
        private DoctypeToken EmitCurrentDoctype()
        {
            var d = _currentDoctype;
            _currentDoctype = null;
            return d;
        }

        private bool Matches(string s, bool ignoreCase = false)
        {
            if (_position + s.Length > _length) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c1 = _input[_position + i];
                char c2 = s[i];
                if (ignoreCase)
                {
                    c1 = char.ToLowerInvariant(c1);
                    c2 = char.ToLowerInvariant(c2);
                }
                if (c1 != c2) return false;
            }
            return true;
        }
        
        private void SetAttributeValue()
        {
            if (_currentTag != null && !string.IsNullOrEmpty(_lastAttrName))
            {
                var attrIndex = _currentTag.Attributes.FindIndex(a => a.Name == _lastAttrName);
                if (attrIndex >= 0)
                {
                    _currentTag.Attributes[attrIndex].Value = _attrValueBuffer.ToString();
                }
            }
        }

        private bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private bool TryConsumeNamedCharacterReference(out string value)
        {
            value = string.Empty;
            if (IsEof())
            {
                return false;
            }

            int start = _position;
            int i = _position;
            while (i < _length && IsAsciiAlphaNumeric(_input[i]))
            {
                i++;
            }

            int nameLength = i - start;
            if (nameLength <= 0)
            {
                return false;
            }

            for (int candidateLength = nameLength; candidateLength >= 1; candidateLength--)
            {
                var name = _input.Substring(start, candidateLength);
                if (!TryResolveNamedCharacterReference(name, out value))
                {
                    continue;
                }

                int end = start + candidateLength;
                bool hasSemicolon = end < _length && _input[end] == ';';

                if (!hasSemicolon && _charRefInAttributeValue)
                {
                    char next = end < _length ? _input[end] : '\0';
                    if (IsAsciiAlphaNumeric(next) || next == '=')
                    {
                        continue;
                    }
                }

                _position = end + (hasSemicolon ? 1 : 0);
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryResolveNamedCharacterReference(string name, out string value)
        {
            if (NamedCharacterReferences.TryGetValue(name, out value))
            {
                return true;
            }

            lock (NamedCharacterReferenceDecodeCacheLock)
            {
                if (NamedCharacterReferenceDecodeCache.TryGetValue(name, out var cached))
                {
                    if (cached is not null)
                    {
                        value = cached;
                        return true;
                    }

                    value = string.Empty;
                    return false;
                }
            }

            var entity = "&" + name + ";";
            var decoded = WebUtility.HtmlDecode(entity);
            bool success = !string.Equals(decoded, entity, StringComparison.Ordinal);

            lock (NamedCharacterReferenceDecodeCacheLock)
            {
                NamedCharacterReferenceDecodeCache[name] = success ? decoded : null;
            }

            if (success)
            {
                value = decoded;
                return true;
            }

            value = string.Empty;
            return false;
        }

        private string ResolveNumericCharacterReference(uint codePoint)
        {
            if (NumericCharacterReferenceReplacements.TryGetValue((int)codePoint, out var replacement))
            {
                codePoint = (uint)replacement;
            }

            if (codePoint == 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                return "\uFFFD";
            }

            try
            {
                return char.ConvertFromUtf32((int)codePoint);
            }
            catch
            {
                return "\uFFFD";
            }
        }

        private HtmlToken EmitCharacterReferenceResult(string resolved)
        {
            if (_charRefInAttributeValue)
            {
                _attrValueBuffer.Append(resolved);
                _charRefInAttributeValue = false;
                return null;
            }

            if (string.IsNullOrEmpty(resolved))
            {
                return null;
            }

            if (resolved.Length > 1)
            {
                for (int i = 1; i < resolved.Length; i++)
                {
                    _pendingChars.Enqueue(resolved[i]);
                }
            }

            return EmitCharacter(resolved[0]);
        }

        private static bool IsAsciiAlphaNumeric(char c)
        {
            return (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9');
        }

        private int GetHexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return 0;
        }
    }
}
