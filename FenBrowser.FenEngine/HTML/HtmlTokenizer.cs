using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.HTML
{
    public class HtmlTokenizer
    {
        private enum State
        {
            // Original states
            Data,
            TagOpen,
            EndTagOpen,
            TagName,
            BeforeAttributeName,
            AttributeName,
            AfterAttributeName,
            BeforeAttributeValue,
            AttributeValueDoubleQuoted,
            AttributeValueSingleQuoted,
            AttributeValueUnquoted,
            AfterAttributeValue,
            SelfClosingStartTag,
            BogusComment,
            MarkupDeclarationOpen,
            CommentStart,
            CommentStartDash,
            Comment,
            CommentEndDash,
            CommentEnd,
            BeforeDoctypeName,
            DoctypeName,
            AfterDoctypeName,
            Doctype,
            
            // RCDATA states (for <title>, <textarea>)
            RcData,
            RcDataLessThanSign,
            RcDataEndTagOpen,
            RcDataEndTagName,
            
            // RAWTEXT states (for <style>, <xmp>, etc.)
            RawText,
            RawTextLessThanSign,
            RawTextEndTagOpen,
            RawTextEndTagName,
            
            // Script data states
            ScriptData,
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
            
            // Character reference states
            CharacterReference,
            NamedCharacterReference,
            AmbiguousAmpersand,
            NumericCharacterReference,
            HexadecimalCharacterReferenceStart,
            DecimalCharacterReferenceStart,
            HexadecimalCharacterReference,
            DecimalCharacterReference,
            NumericCharacterReferenceEnd,
            
            // CDATA states
            CdataSection,
            CdataSectionBracket,
            CdataSectionEnd,
            
            // Extended DOCTYPE states
            AfterDoctypePublicKeyword,
            BeforeDoctypePublicIdentifier,
            DoctypePublicIdentifierDoubleQuoted,
            DoctypePublicIdentifierSingleQuoted,
            AfterDoctypePublicIdentifier,
            BetweenDoctypePublicAndSystemIdentifiers,
            AfterDoctypeSystemKeyword,
            BeforeDoctypeSystemIdentifier,
            DoctypeSystemIdentifierDoubleQuoted,
            DoctypeSystemIdentifierSingleQuoted,
            AfterDoctypeSystemIdentifier,
            BogusDoctype
        }

        private readonly string _input;
        private int _position;
        private State _state;
        private HtmlToken _currentToken;
        private TagToken _currentTagToken; // Typed ref to _currentToken if it's a tag
        private readonly List<HtmlToken> _emitQueue = new List<HtmlToken>();
        
        // Character reference handling (WHATWG 13.2.5.72-13.2.5.80)
        private State _returnState;
        private StringBuilder _temporaryBuffer = new StringBuilder();
        private StringBuilder _charRefBuffer = new StringBuilder();
        private int _charRefCode;
        
        // Last start tag name for end tag matching (RCDATA, RAWTEXT, script)
        private string _lastStartTagName;
        
        // Debug/Context info
        public int Line { get; private set; } = 1;
        public int Column { get; private set; } = 0;
        
        /// <summary>
        /// Set the tokenizer state externally (called by tree builder).
        /// </summary>
        public void SetState(string stateName)
        {
            switch (stateName.ToUpperInvariant())
            {
                case "RCDATA": _state = State.RcData; break;
                case "RAWTEXT": _state = State.RawText; break;
                case "SCRIPTDATA": _state = State.ScriptData; break;
                case "PLAINTEXT": _state = State.RawText; break; // Treat PLAINTEXT as RAWTEXT
                default: _state = State.Data; break;
            }
        }
        
        /// <summary>
        /// Set the last start tag name (for end tag matching).
        /// </summary>
        public void SetLastStartTagName(string tagName)
        {
            _lastStartTagName = tagName?.ToUpperInvariant();
        }

        public HtmlTokenizer(string input)
        {
            _input = input ?? string.Empty;
            _state = State.Data;
            _position = 0;
        }

        public IEnumerable<HtmlToken> Tokenize()
        {
            while (true)
            {
                if (_emitQueue.Count > 0)
                {
                    var partial = _emitQueue[0];
                    _emitQueue.RemoveAt(0);
                    yield return partial;
                    continue;
                }

                if (_position >= _input.Length)
                {
                    if (_input.Length == 0 && _position == 0) 
                    {
                         // Empty input
                         yield return new EofToken();
                         yield break;
                    }
                    
                    // Handle pending character reference at EOF
                    if (_state == State.NumericCharacterReferenceEnd)
                    {
                        HandleNumericCharacterReferenceEnd();
                    }
                    else if (_state == State.DecimalCharacterReference || 
                             _state == State.HexadecimalCharacterReference)
                    {
                        // Missing semicolon - emit what we have
                        HandleNumericCharacterReferenceEnd();
                    }
                    else if (_state == State.CharacterReference ||
                             _state == State.NamedCharacterReference ||
                             _state == State.NumericCharacterReference ||
                             _state == State.DecimalCharacterReferenceStart ||
                             _state == State.HexadecimalCharacterReferenceStart)
                    {
                        // Incomplete character reference - flush buffer
                        FlushCharacterReference();
                    }
                    
                    // Emit any queued tokens before EOF
                    while (_emitQueue.Count > 0)
                    {
                        var queued = _emitQueue[0];
                        _emitQueue.RemoveAt(0);
                        yield return queued;
                    }
                    
                    // Flush EOF
                    if (_currentToken != null) 
                    {
                         yield return _currentToken;
                         _currentToken = null;
                    }
                    yield return new EofToken();
                    yield break;
                }

                char c = _input[_position];
                
                // State machine step
                switch (_state)
                {
                    case State.Data:
                        HandleData(c);
                        break;
                    case State.TagOpen:
                        HandleTagOpen(c);
                        break;
                    case State.EndTagOpen:
                        HandleEndTagOpen(c);
                        break;
                    case State.TagName:
                        HandleTagName(c);
                        break;
                    case State.BeforeAttributeName:
                        HandleBeforeAttributeName(c);
                        break;
                    case State.AttributeName:
                        HandleAttributeName(c);
                        break;
                    case State.AfterAttributeName:
                        HandleAfterAttributeName(c);
                        break;
                    case State.BeforeAttributeValue:
                        HandleBeforeAttributeValue(c);
                        break;
                    case State.AttributeValueDoubleQuoted:
                        HandleAttributeValueDoubleQuoted(c);
                        break;
                    case State.AttributeValueSingleQuoted:
                        HandleAttributeValueSingleQuoted(c);
                        break;
                    case State.AttributeValueUnquoted:
                        HandleAttributeValueUnquoted(c);
                        break;
                    case State.AfterAttributeValue:
                        HandleAfterAttributeValue(c);
                        break;
                    case State.SelfClosingStartTag:
                        HandleSelfClosingStartTag(c);
                        break;
                    case State.MarkupDeclarationOpen:
                        HandleMarkupDeclarationOpen(c);
                        break;
                    case State.CommentStart:
                        HandleCommentStart(c);
                        break;
                    case State.CommentStartDash:
                        HandleCommentStartDash(c);
                        break;
                    case State.Comment:
                        HandleComment(c);
                        break;
                    case State.CommentEndDash:
                        HandleCommentEndDash(c);
                        break;
                    case State.CommentEnd:
                        HandleCommentEnd(c);
                        break;
                     case State.BeforeDoctypeName:
                        HandleBeforeDoctypeName(c);
                        break;
                    case State.DoctypeName:
                        HandleDoctypeName(c);
                        break;
                     case State.AfterDoctypeName:
                        HandleAfterDoctypeName(c);
                        break;
                    case State.BogusComment:
                        HandleBogusComment(c);
                        break;
                    
                    // RCDATA states
                    case State.RcData:
                        HandleRcData(c);
                        break;
                    case State.RcDataLessThanSign:
                        HandleRcDataLessThanSign(c);
                        break;
                    case State.RcDataEndTagOpen:
                        HandleRcDataEndTagOpen(c);
                        break;
                    case State.RcDataEndTagName:
                        HandleRcDataEndTagName(c);
                        break;
                        
                    // RAWTEXT states
                    case State.RawText:
                        HandleRawText(c);
                        break;
                    case State.RawTextLessThanSign:
                        HandleRawTextLessThanSign(c);
                        break;
                    case State.RawTextEndTagOpen:
                        HandleRawTextEndTagOpen(c);
                        break;
                    case State.RawTextEndTagName:
                        HandleRawTextEndTagName(c);
                        break;
                        
                    // ScriptData states
                    case State.ScriptData:
                        HandleScriptData(c);
                        break;
                    case State.ScriptDataLessThanSign:
                        HandleScriptDataLessThanSign(c);
                        break;
                    case State.ScriptDataEndTagOpen:
                        HandleScriptDataEndTagOpen(c);
                        break;
                    case State.ScriptDataEndTagName:
                        HandleScriptDataEndTagName(c);
                        break;
                        
                    // Character reference states
                    case State.CharacterReference:
                        HandleCharacterReference(c);
                        break;
                    case State.NamedCharacterReference:
                        HandleNamedCharacterReference(c);
                        break;
                    case State.NumericCharacterReference:
                        HandleNumericCharacterReference(c);
                        break;
                    case State.HexadecimalCharacterReferenceStart:
                        HandleHexadecimalCharacterReferenceStart(c);
                        break;
                    case State.DecimalCharacterReferenceStart:
                        HandleDecimalCharacterReferenceStart(c);
                        break;
                    case State.HexadecimalCharacterReference:
                        HandleHexadecimalCharacterReference(c);
                        break;
                    case State.DecimalCharacterReference:
                        HandleDecimalCharacterReference(c);
                        break;
                    case State.NumericCharacterReferenceEnd:
                        HandleNumericCharacterReferenceEnd();
                        break;
                        
                    // CDATA states
                    case State.CdataSection:
                        HandleCdataSection(c);
                        break;
                    case State.CdataSectionBracket:
                        HandleCdataSectionBracket(c);
                        break;
                    case State.CdataSectionEnd:
                        HandleCdataSectionEnd(c);
                        break;
                        
                     default:
                         // Simple fallback for unimplemented states
                        Advance();
                        break;
                }
            }
        }

        private void Emit(HtmlToken token)
        {
            _emitQueue.Add(token);
        }

        private void EmitCurrentToken()
        {
             if (_currentToken != null)
             {
                 if (_currentTagToken != null) _currentTagToken.FlushAttribute();
                 Emit(_currentToken);
                 _currentToken = null;
                 _currentTagToken = null;
             }
        }

        private void Advance()
        {
            if (_position < _input.Length)
            {
                if (_input[_position] == '\n') { Line++; Column = 0; }
                else { Column++; }
                _position++;
            }
        }
        
        private void SwitchTo(State state)
        {
            _state = state;
        }
        
        // --- State Handlers ---

        private void HandleData(char c)
        {
            if (c == '&')
            {
                _returnState = State.Data;
                SwitchTo(State.CharacterReference);
                Advance();
            }
            else if (c == '<')
            {
                SwitchTo(State.TagOpen);
                Advance();
            }
            else
            {
                // Emit character
                Emit(new CharacterToken(c));
                Advance();
            }
        }

        private void HandleTagOpen(char c)
        {
            if (c == '!')
            {
                SwitchTo(State.MarkupDeclarationOpen);
                Advance();
            }
            else if (c == '/')
            {
                SwitchTo(State.EndTagOpen);
                Advance();
            }
            else if (IsAlpha(c))
            {
                _currentToken = new StartTagToken();
                _currentTagToken = (TagToken)_currentToken;
                SwitchTo(State.TagName);
                // Do NOT advance, re-consume in TagName
            }
            else if (c == '?')
            {
                // Bogus comment (e.g. <?xml ... ?>)
                _currentToken = new CommentToken();
                SwitchTo(State.BogusComment);
                Advance();
            }
            else
            {
                // Invalid tag open, treat as data '<' then 'c'
                Emit(new CharacterToken('<'));
                SwitchTo(State.Data);
                // Re-consume c
            }
        }

        private void HandleEndTagOpen(char c)
        {
            if (IsAlpha(c))
            {
                _currentToken = new EndTagToken();
                _currentTagToken = (TagToken)_currentToken;
                SwitchTo(State.TagName);
            }
            else if (c == '>')
            {
                // Missing tag name </>, ignore?
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                SwitchTo(State.BogusComment);
                _currentToken = new CommentToken();
                Advance();
            }
        }

        private void HandleTagName(char c)
        {
            if (IsSpace(c))
            {
                SwitchTo(State.BeforeAttributeName);
                Advance();
            }
            else if (c == '/')
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>')
            {
                SwitchTo(State.Data);
                EmitCurrentToken();
                Advance();
            }
            else
            {
                _currentTagToken.TagName += char.ToUpperInvariant(c); // Canonical upper
                Advance();
            }
        }

        private void HandleBeforeAttributeName(char c)
        {
            if (IsSpace(c))
            {
                Advance();
            }
            else if (c == '/')
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>')
            {
                SwitchTo(State.Data);
                EmitCurrentToken();
                Advance();
            }
            else
            {
                SwitchTo(State.AttributeName);
                // Start new attribute
                _currentTagToken.StartAttribute(char.ToLowerInvariant(c).ToString());
                Advance();
            }
        }

        private void HandleAttributeName(char c)
        {
            if (IsSpace(c) || c == '/' || c == '>' || c == '=')
            {
                if (c == '=')
                {
                    SwitchTo(State.BeforeAttributeValue);
                }
                else if (c == '/')
                {
                    _currentTagToken.FlushAttribute();
                    SwitchTo(State.SelfClosingStartTag);
                }
                else if (c == '>')
                {
                    _currentTagToken.FlushAttribute();
                    EmitCurrentToken();
                    SwitchTo(State.Data);
                }
                else
                {
                    // Space
                    _currentTagToken.FlushAttribute();
                    SwitchTo(State.AfterAttributeName);
                }
                Advance();
            }
            else
            {
                 // Append to name
                 _currentTagToken.CurrentAttributeName += char.ToLowerInvariant(c);
                 Advance();
            }
        }

        private void HandleAfterAttributeName(char c)
        {
            if (IsSpace(c))
            {
                Advance();
            }
            else if (c == '=')
            {
                SwitchTo(State.BeforeAttributeValue);
                Advance();
            }
            else if (c == '/')
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                _currentTagToken.StartAttribute("");
                SwitchTo(State.AttributeName);
                // Re-consume
            }
        }

        private void HandleBeforeAttributeValue(char c)
        {
            if (IsSpace(c))
            {
                Advance();
            }
            else if (c == '"')
            {
                SwitchTo(State.AttributeValueDoubleQuoted);
                Advance();
            }
            else if (c == '\'')
            {
                SwitchTo(State.AttributeValueSingleQuoted);
                Advance();
            }
            else if (c == '>')
            {
                // Missing value
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                SwitchTo(State.AttributeValueUnquoted);
                _currentTagToken.AppendAttributeValue(c);
                Advance();
            }
        }

        private void HandleAttributeValueDoubleQuoted(char c)
        {
            if (c == '"')
            {
                SwitchTo(State.AfterAttributeValue);
                Advance();
            }
            else if (c == '&')
            {
                _returnState = State.AttributeValueDoubleQuoted;
                SwitchTo(State.CharacterReference);
                Advance();
            }
            else
            {
                _currentTagToken.AppendAttributeValue(c);
                Advance();
            }
        }

        private void HandleAttributeValueSingleQuoted(char c)
        {
            if (c == '\'')
            {
                SwitchTo(State.AfterAttributeValue);
                Advance();
            }
            else if (c == '&')
            {
                _returnState = State.AttributeValueSingleQuoted;
                SwitchTo(State.CharacterReference);
                Advance();
            }
            else
            {
                _currentTagToken.AppendAttributeValue(c);
                Advance();
            }
        }

        private void HandleAttributeValueUnquoted(char c)
        {
            if (IsSpace(c))
            {
                SwitchTo(State.BeforeAttributeName);
                Advance();
            }
            else if (c == '&')
            {
                _returnState = State.AttributeValueUnquoted;
                SwitchTo(State.CharacterReference);
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                _currentTagToken.AppendAttributeValue(c);
                Advance();
            }
        }

        private void HandleAfterAttributeValue(char c)
        {
            if (IsSpace(c))
            {
                SwitchTo(State.BeforeAttributeName);
                Advance();
            }
            else if (c == '/')
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                // Missing space between attributes
                SwitchTo(State.BeforeAttributeName);
                // Re-consume
            }
        }

        private void HandleSelfClosingStartTag(char c)
        {
            if (c == '>')
            {
                _currentTagToken.SelfClosing = true;
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                // Unexpected char after /, treat as before attribute name? or just reset?
                SwitchTo(State.BeforeAttributeName);
                // Re-consume
            }
        }
        
        private void HandleMarkupDeclarationOpen(char c)
        {
            // Already consumed '<!', c is next
            if (c == '-' && IsNext('-'))
            {
                Advance(); // Consume first -
                Advance(); // Consume second -
                _currentToken = new CommentToken();
                SwitchTo(State.CommentStart);
            }
            else if (IsMatch("DOCTYPE"))
            {
                _position += 7; // Consume DOCTYPE
                _currentToken = new DoctypeToken();
                SwitchTo(State.BeforeDoctypeName);
            }
            else
            {
                SwitchTo(State.BogusComment);
                _currentToken = new CommentToken(); // Treat bogus declaration as comment
                _currentToken.As<CommentToken>().Data.Append(c);
                Advance();
            }
        }

        private void HandleCommentStart(char c)
        {
            if (c == '-')
            {
                SwitchTo(State.CommentStartDash);
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                SwitchTo(State.Comment);
                _currentToken.As<CommentToken>().Data.Append(c);
                Advance();
            }
        }
        
        private void HandleCommentStartDash(char c)
        {
             if (c == '-')
            {
                SwitchTo(State.CommentEnd);
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                // It was just a single dash in comment
                _currentToken.As<CommentToken>().Data.Append('-');
                _currentToken.As<CommentToken>().Data.Append(c);
                SwitchTo(State.Comment);
                Advance();
            }
        }

        private void HandleComment(char c)
        {
            if (c == '<')
            {
                // Spec says check matching, simplified: just append
                 _currentToken.As<CommentToken>().Data.Append(c);
                 Advance();
            }
            else if (c == '-')
            {
                SwitchTo(State.CommentEndDash);
                Advance();
            }
            else if (c == '\0') // EOF in comment
            {
                EmitCurrentToken();
                // EOF handled by loop
            }
            else
            {
                _currentToken.As<CommentToken>().Data.Append(c);
                Advance();
            }
        }

        private void HandleCommentEndDash(char c)
        {
            if (c == '-')
            {
                SwitchTo(State.CommentEnd);
                Advance();
            }
            else
            {
                // Dash was data
                _currentToken.As<CommentToken>().Data.Append('-');
                _currentToken.As<CommentToken>().Data.Append(c);
                SwitchTo(State.Comment);
                Advance();
            }
        }

        private void HandleCommentEnd(char c)
        {
            if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else if (c == '!')
            {
                 // <!-- ... --!>
                 EmitCurrentToken(); // Spec says parse error but emit
                 SwitchTo(State.Data);
                 Advance();
            }
            else if (c == '-')
            {
                 // ---
                 _currentToken.As<CommentToken>().Data.Append('-');
                 // Stay in CommentEnd (waiting for > or char)
                 // This is tricky: <!-- - - --> 
                 // If we had --, we went to CommentEnd. 
                 // If we get -, we append it? No, -- triggers end state.
                 // Correct logic:
                 // Comment -> '-' -> CommentEndDash
                 // CommentEndDash -> '-' -> CommentEnd
                 // CommentEnd -> '>' -> Emit
                 // CommentEnd -> '-' -> Append '-' ? No
                 // CommentEnd means we saw '--'.
                 
                 // If we match anything else, we append '--' plus char and go back to Comment?
                 _currentToken.As<CommentToken>().Data.Append('-');
                 _currentToken.As<CommentToken>().Data.Append('-');
                 _currentToken.As<CommentToken>().Data.Append(c);
                 SwitchTo(State.Comment);
                 Advance();
            }
            else
            {
                // We saw '--' but not '>'
                _currentToken.As<CommentToken>().Data.Append('-');
                _currentToken.As<CommentToken>().Data.Append('-');
                _currentToken.As<CommentToken>().Data.Append(c);
                SwitchTo(State.Comment);
                Advance();
            }
        }
        
        private void HandleBeforeDoctypeName(char c)
        {
            if (IsSpace(c))
            {
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                var dt = _currentToken as DoctypeToken;
                dt.Name = char.ToLowerInvariant(c).ToString();
                SwitchTo(State.DoctypeName);
                Advance();
            }
        }

        private void HandleDoctypeName(char c)
        {
            if (IsSpace(c))
            {
                SwitchTo(State.AfterDoctypeName);
                Advance();
            }
            else if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                var dt = _currentToken as DoctypeToken;
                dt.Name += char.ToLowerInvariant(c);
                Advance();
            }
        }
        
        private void HandleAfterDoctypeName(char c)
        {
             // Simplified: Skip everything else in DOCTYPE
             if (c == '>')
             {
                 EmitCurrentToken();
                 SwitchTo(State.Data);
                 Advance();
             }
             else
             {
                 Advance();
             }
        }

        private void HandleBogusComment(char c)
        {
            if (c == '>')
            {
                EmitCurrentToken();
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                if (_currentToken is CommentToken ct) ct.Data.Append(c);
                Advance();
            }
        }

        // Helpers
        private bool IsNext(char c)
        {
            return _position + 1 < _input.Length && _input[_position + 1] == c;
        }

        private bool IsMatch(string s)
        {
            if (_position + s.Length > _input.Length) return false;
            return string.Compare(_input, _position, s, 0, s.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\r';
        }

        private bool IsAlpha(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }
        
        private bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
        private bool IsHexDigit(char c) => IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        private bool IsAlphanumeric(char c) => IsAlpha(c) || IsAsciiDigit(c);
        
        // --- RCDATA State Handlers (WHATWG 13.2.5.2-13.2.5.5) ---
        
        private void HandleRcData(char c)
        {
            if (c == '&')
            {
                _returnState = State.RcData;
                SwitchTo(State.CharacterReference);
                Advance();
            }
            else if (c == '<')
            {
                SwitchTo(State.RcDataLessThanSign);
                Advance();
            }
            else
            {
                Emit(new CharacterToken(c));
                Advance();
            }
        }
        
        private void HandleRcDataLessThanSign(char c)
        {
            if (c == '/')
            {
                _temporaryBuffer.Clear();
                SwitchTo(State.RcDataEndTagOpen);
                Advance();
            }
            else
            {
                Emit(new CharacterToken('<'));
                SwitchTo(State.RcData);
                // Re-consume
            }
        }
        
        private void HandleRcDataEndTagOpen(char c)
        {
            if (IsAlpha(c))
            {
                _currentToken = new EndTagToken();
                _currentTagToken = (TagToken)_currentToken;
                SwitchTo(State.RcDataEndTagName);
                // Re-consume
            }
            else
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('/'));
                SwitchTo(State.RcData);
                // Re-consume
            }
        }
        
        private void HandleRcDataEndTagName(char c)
        {
            if (IsSpace(c) && IsAppropriateEndTagToken())
            {
                SwitchTo(State.BeforeAttributeName);
                Advance();
            }
            else if (c == '/' && IsAppropriateEndTagToken())
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>' && IsAppropriateEndTagToken())
            {
                SwitchTo(State.Data);
                EmitCurrentToken();
                Advance();
            }
            else if (IsAlpha(c))
            {
                _currentTagToken.TagName += char.ToUpperInvariant(c);
                _temporaryBuffer.Append(c);
                Advance();
            }
            else
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('/'));
                foreach (char ch in _temporaryBuffer.ToString())
                    Emit(new CharacterToken(ch));
                SwitchTo(State.RcData);
                // Re-consume
            }
        }
        
        // --- RAWTEXT State Handlers (WHATWG 13.2.5.6-13.2.5.9) ---
        
        private void HandleRawText(char c)
        {
            if (c == '<')
            {
                SwitchTo(State.RawTextLessThanSign);
                Advance();
            }
            else
            {
                Emit(new CharacterToken(c));
                Advance();
            }
        }
        
        private void HandleRawTextLessThanSign(char c)
        {
            if (c == '/')
            {
                _temporaryBuffer.Clear();
                SwitchTo(State.RawTextEndTagOpen);
                Advance();
            }
            else
            {
                Emit(new CharacterToken('<'));
                SwitchTo(State.RawText);
            }
        }
        
        private void HandleRawTextEndTagOpen(char c)
        {
            if (IsAlpha(c))
            {
                _currentToken = new EndTagToken();
                _currentTagToken = (TagToken)_currentToken;
                SwitchTo(State.RawTextEndTagName);
            }
            else
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('/'));
                SwitchTo(State.RawText);
            }
        }
        
        private void HandleRawTextEndTagName(char c)
        {
            if (IsSpace(c) && IsAppropriateEndTagToken())
            {
                SwitchTo(State.BeforeAttributeName);
                Advance();
            }
            else if (c == '/' && IsAppropriateEndTagToken())
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>' && IsAppropriateEndTagToken())
            {
                SwitchTo(State.Data);
                EmitCurrentToken();
                Advance();
            }
            else if (IsAlpha(c))
            {
                _currentTagToken.TagName += char.ToUpperInvariant(c);
                _temporaryBuffer.Append(c);
                Advance();
            }
            else
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('/'));
                foreach (char ch in _temporaryBuffer.ToString())
                    Emit(new CharacterToken(ch));
                SwitchTo(State.RawText);
            }
        }
        
        // --- Script Data State Handlers (WHATWG 13.2.5.10+) ---
        
        private void HandleScriptData(char c)
        {
            if (c == '<')
            {
                SwitchTo(State.ScriptDataLessThanSign);
                Advance();
            }
            else
            {
                Emit(new CharacterToken(c));
                Advance();
            }
        }
        
        private void HandleScriptDataLessThanSign(char c)
        {
            if (c == '/')
            {
                _temporaryBuffer.Clear();
                SwitchTo(State.ScriptDataEndTagOpen);
                Advance();
            }
            else if (c == '!')
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('!'));
                SwitchTo(State.ScriptDataEscapeStart);
                Advance();
            }
            else
            {
                Emit(new CharacterToken('<'));
                SwitchTo(State.ScriptData);
            }
        }
        
        private void HandleScriptDataEndTagOpen(char c)
        {
            if (IsAlpha(c))
            {
                _currentToken = new EndTagToken();
                _currentTagToken = (TagToken)_currentToken;
                SwitchTo(State.ScriptDataEndTagName);
            }
            else
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('/'));
                SwitchTo(State.ScriptData);
            }
        }
        
        private void HandleScriptDataEndTagName(char c)
        {
            if (IsSpace(c) && IsAppropriateEndTagToken())
            {
                SwitchTo(State.BeforeAttributeName);
                Advance();
            }
            else if (c == '/' && IsAppropriateEndTagToken())
            {
                SwitchTo(State.SelfClosingStartTag);
                Advance();
            }
            else if (c == '>' && IsAppropriateEndTagToken())
            {
                SwitchTo(State.Data);
                EmitCurrentToken();
                Advance();
            }
            else if (IsAlpha(c))
            {
                _currentTagToken.TagName += char.ToUpperInvariant(c);
                _temporaryBuffer.Append(c);
                Advance();
            }
            else
            {
                Emit(new CharacterToken('<'));
                Emit(new CharacterToken('/'));
                foreach (char ch in _temporaryBuffer.ToString())
                    Emit(new CharacterToken(ch));
                SwitchTo(State.ScriptData);
            }
        }
        
        // --- Character Reference State Handlers (WHATWG 13.2.5.72+) ---
        
        private void HandleCharacterReference(char c)
        {
            _charRefBuffer.Clear();
            _charRefBuffer.Append('&');
            
            if (IsAlphanumeric(c))
            {
                SwitchTo(State.NamedCharacterReference);
                // Re-consume
            }
            else if (c == '#')
            {
                _charRefBuffer.Append(c);
                SwitchTo(State.NumericCharacterReference);
                Advance();
            }
            else
            {
                // Flush as ampersand
                FlushCharacterReference();
                SwitchTo(_returnState);
                // Re-consume
            }
        }
        
        private void HandleNamedCharacterReference(char c)
        {
            // Simplified: collect until ; or non-alphanumeric
            if (IsAlphanumeric(c))
            {
                _charRefBuffer.Append(c);
                Advance();
            }
            else if (c == ';')
            {
                _charRefBuffer.Append(c);
                var entityName = _charRefBuffer.ToString().Substring(1); // Remove &
                var decoded = HtmlEntities.Decode(entityName);
                if (decoded != null)
                {
                    EmitCharacterReferenceResult(decoded);
                }
                else
                {
                    FlushCharacterReference();
                }
                SwitchTo(_returnState);
                Advance();
            }
            else
            {
                // Try to match without semicolon
                var entityName = _charRefBuffer.ToString().Substring(1);
                var decoded = HtmlEntities.Decode(entityName);
                if (decoded != null)
                {
                    EmitCharacterReferenceResult(decoded);
                }
                else
                {
                    FlushCharacterReference();
                }
                SwitchTo(_returnState);
                // Re-consume
            }
        }
        
        private void HandleNumericCharacterReference(char c)
        {
            _charRefCode = 0;
            if (c == 'x' || c == 'X')
            {
                _charRefBuffer.Append(c);
                SwitchTo(State.HexadecimalCharacterReferenceStart);
                Advance();
            }
            else
            {
                SwitchTo(State.DecimalCharacterReferenceStart);
                // Re-consume
            }
        }
        
        private void HandleHexadecimalCharacterReferenceStart(char c)
        {
            if (IsHexDigit(c))
            {
                SwitchTo(State.HexadecimalCharacterReference);
                // Re-consume
            }
            else
            {
                FlushCharacterReference();
                SwitchTo(_returnState);
                // Re-consume
            }
        }
        
        private void HandleDecimalCharacterReferenceStart(char c)
        {
            if (IsAsciiDigit(c))
            {
                SwitchTo(State.DecimalCharacterReference);
                // Re-consume
            }
            else
            {
                FlushCharacterReference();
                SwitchTo(_returnState);
                // Re-consume
            }
        }
        
        private void HandleHexadecimalCharacterReference(char c)
        {
            if (IsAsciiDigit(c))
            {
                _charRefCode = _charRefCode * 16 + (c - '0');
                Advance();
            }
            else if (c >= 'A' && c <= 'F')
            {
                _charRefCode = _charRefCode * 16 + (c - 'A' + 10);
                Advance();
            }
            else if (c >= 'a' && c <= 'f')
            {
                _charRefCode = _charRefCode * 16 + (c - 'a' + 10);
                Advance();
            }
            else if (c == ';')
            {
                SwitchTo(State.NumericCharacterReferenceEnd);
                Advance();
            }
            else
            {
                SwitchTo(State.NumericCharacterReferenceEnd);
                // Re-consume
            }
        }
        
        private void HandleDecimalCharacterReference(char c)
        {
            if (IsAsciiDigit(c))
            {
                _charRefCode = _charRefCode * 10 + (c - '0');
                Advance();
            }
            else if (c == ';')
            {
                SwitchTo(State.NumericCharacterReferenceEnd);
                Advance();
            }
            else
            {
                SwitchTo(State.NumericCharacterReferenceEnd);
                // Re-consume
            }
        }
        
        private void HandleNumericCharacterReferenceEnd()
        {
            // Convert code point to character per WHATWG 13.2.5.80
            string decoded;
            if (_charRefCode == 0)
            {
                decoded = "\uFFFD"; // NULL → REPLACEMENT CHARACTER
            }
            else if (_charRefCode > 0x10FFFF)
            {
                decoded = "\uFFFD"; // Out of range
            }
            else if (_charRefCode >= 0xD800 && _charRefCode <= 0xDFFF)
            {
                decoded = "\uFFFD"; // Surrogate
            }
            else if (_charRefCode <= 0xFFFF)
            {
                decoded = ((char)_charRefCode).ToString();
            }
            else
            {
                // Supplementary character (surrogate pair)
                _charRefCode -= 0x10000;
                decoded = new string(new char[] {
                    (char)(0xD800 + (_charRefCode >> 10)),
                    (char)(0xDC00 + (_charRefCode & 0x3FF))
                });
            }
            EmitCharacterReferenceResult(decoded);
            SwitchTo(_returnState);
        }
        
        // --- CDATA State Handlers (WHATWG 13.2.5.68-70) ---
        
        private void HandleCdataSection(char c)
        {
            if (c == ']')
            {
                SwitchTo(State.CdataSectionBracket);
                Advance();
            }
            else
            {
                Emit(new CharacterToken(c));
                Advance();
            }
        }
        
        private void HandleCdataSectionBracket(char c)
        {
            if (c == ']')
            {
                SwitchTo(State.CdataSectionEnd);
                Advance();
            }
            else
            {
                Emit(new CharacterToken(']'));
                SwitchTo(State.CdataSection);
                // Re-consume
            }
        }
        
        private void HandleCdataSectionEnd(char c)
        {
            if (c == ']')
            {
                Emit(new CharacterToken(']'));
                Advance();
            }
            else if (c == '>')
            {
                SwitchTo(State.Data);
                Advance();
            }
            else
            {
                Emit(new CharacterToken(']'));
                Emit(new CharacterToken(']'));
                SwitchTo(State.CdataSection);
                // Re-consume
            }
        }
        
        // --- Helper methods for character references ---
        
        private bool IsAppropriateEndTagToken()
        {
            return _currentTagToken != null && 
                   _lastStartTagName != null &&
                   _currentTagToken.TagName == _lastStartTagName;
        }
        
        private void FlushCharacterReference()
        {
            foreach (char ch in _charRefBuffer.ToString())
            {
                if (_returnState == State.AttributeValueDoubleQuoted ||
                    _returnState == State.AttributeValueSingleQuoted ||
                    _returnState == State.AttributeValueUnquoted)
                {
                    _currentTagToken?.AppendAttributeValue(ch);
                }
                else
                {
                    Emit(new CharacterToken(ch));
                }
            }
        }
        
        private void EmitCharacterReferenceResult(string chars)
        {
            if (_returnState == State.AttributeValueDoubleQuoted ||
                _returnState == State.AttributeValueSingleQuoted ||
                _returnState == State.AttributeValueUnquoted)
            {
                _currentTagToken?.AppendAttributeValue(chars);
            }
            else
            {
                Emit(new CharacterToken(chars));
            }
        }
    }
}

