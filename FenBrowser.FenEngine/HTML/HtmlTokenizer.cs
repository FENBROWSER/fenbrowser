using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Dom;

namespace FenBrowser.FenEngine.HTML
{
    public class HtmlTokenizer
    {
        private enum State
        {
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
            Doctype
        }

        private readonly string _input;
        private int _position;
        private State _state;
        private HtmlToken _currentToken;
        private TagToken _currentTagToken; // Typed ref to _currentToken if it's a tag
        private readonly List<HtmlToken> _emitQueue = new List<HtmlToken>();
        
        // Debug/Context info
        public int Line { get; private set; } = 1;
        public int Column { get; private set; } = 0;

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
            if (c == '<')
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
    }
}
